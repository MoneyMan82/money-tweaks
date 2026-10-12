using System.Management;
using System.Text.Json;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

public sealed class TweakBackup
{
    public string TweakId { get; set; } = "";
    public DateTime AppliedAt { get; set; }
    public List<OpBackup> Ops { get; set; } = [];
}

public sealed record TweakResult(Tweak Tweak, bool Success, string Message, TweakState NewState);

/// <summary>Stores one JSON backup per applied tweak so every change can be undone, even after an app update.</summary>
public static class BackupStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string PathFor(string id) => Path.Combine(AppPaths.Backups, id + ".json");

    public static TweakBackup? Load(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<TweakBackup>(File.ReadAllText(p)); }
        catch (Exception ex) { Log.Error($"Backup for {id} is unreadable", ex); return null; }
    }

    public static void Save(TweakBackup b) => File.WriteAllText(PathFor(b.TweakId), JsonSerializer.Serialize(b, Json));
    public static void Delete(string id) { try { File.Delete(PathFor(id)); } catch { /* ignore */ } }
    public static bool Exists(string id) => File.Exists(PathFor(id));
    public static IEnumerable<string> AllIds() =>
        Directory.EnumerateFiles(AppPaths.Backups, "*.json").Select(f => Path.GetFileNameWithoutExtension(f));
}

public static class TweakEngine
{
    /// <summary>Runs the slow PowerShell-based state checks of many tweaks in a single process before GetState is called.</summary>
    public static void PrewarmChecks(IEnumerable<Tweak> tweaks)
    {
        using var perf = Perf.Measure("prewarm PowerShell checks", 50);
        CommandOp.PrewarmChecks(tweaks.SelectMany(t => t.Ops).OfType<CommandOp>());
    }

    public static TweakState GetState(Tweak t)
    {
        if (t.Kind == TweakKind.Guide) return BackupStore.Exists(t.Id) ? TweakState.Applied : TweakState.NotApplied;
        if (t.MinBuild > 0 && OsInfo.Build < t.MinBuild) return TweakState.NotApplicable;
        var states = new List<OpState>();
        foreach (var op in t.Ops)
        {
            try { states.Add(op.Check()); }
            catch (Exception ex) { Log.Debug($"check {t.Id}: {ex.Message}"); states.Add(OpState.Unknown); }
        }
        bool hasBackup = BackupStore.Exists(t.Id);
        var relevant = states.Where(s => s != OpState.NotApplicable).ToList();
        // e.g. a Store app we removed is "not installed" now, but must remain undoable.
        if (relevant.Count == 0) return hasBackup ? TweakState.Applied : TweakState.NotApplicable;
        var resolved = relevant.Select(s => s == OpState.Unknown ? (hasBackup ? OpState.Applied : OpState.NotApplied) : s).ToList();
        if (resolved.All(s => s == OpState.Applied)) return TweakState.Applied;
        if (resolved.All(s => s == OpState.NotApplied)) return TweakState.NotApplied;
        return TweakState.Partial;
    }

    public static TweakResult Apply(Tweak t)
    {
        if (t.Kind == TweakKind.Guide)
        {
            BackupStore.Save(new TweakBackup { TweakId = t.Id, AppliedAt = DateTime.Now });
            return new(t, true, "Marked as done.", TweakState.Applied);
        }
        if (t.MinBuild > 0 && OsInfo.Build < t.MinBuild)
            return new(t, false, $"Requires Windows build {t.MinBuild} or newer.", TweakState.NotApplicable);
        if (t.RequiresAdmin && !Admin.IsElevated)
            return new(t, false, "Administrator rights are required. Use 'Restart as admin'.", GetState(t));

        // Keep the very first backup: re-applying must never overwrite the user's true original values.
        var backup = BackupStore.Load(t.Id);
        bool freshBackup = backup is null || backup.Ops.Count != t.Ops.Count;
        if (freshBackup)
        {
            backup = new TweakBackup { TweakId = t.Id, AppliedAt = DateTime.Now };
            foreach (var op in t.Ops)
            {
                try { backup.Ops.Add(op.Capture()); }
                catch (Exception ex) { return new(t, false, $"Could not back up current settings: {ex.Message}", GetState(t)); }
            }
            BackupStore.Save(backup);
        }

        var done = new List<int>();
        for (int i = 0; i < t.Ops.Count; i++)
        {
            try
            {
                t.Ops[i].Apply();
                done.Add(i);
                Log.Debug($"{t.Id}: {t.Ops[i].Describe()}");
            }
            catch (Exception ex)
            {
                Log.Error($"Apply '{t.Name}' failed at step {i + 1}/{t.Ops.Count} ({t.Ops[i].Describe()})", ex);
                // Roll back the steps that already succeeded so the PC is never left half-tweaked.
                foreach (var j in Enumerable.Reverse(done))
                {
                    try { t.Ops[j].Undo(backup!.Ops[j]); }
                    catch (Exception rex) { Log.Error($"Rollback of {t.Id} step {j + 1} failed", rex); }
                }
                if (freshBackup) BackupStore.Delete(t.Id);
                return new(t, false, ex.Message, GetState(t));
            }
        }
        Log.Info($"Applied: {t.Name}{(t.RequiresReboot ? " (restart required)" : "")}");
        var msg = t.RequiresReboot ? "Applied. Restart Windows to finish." : "Applied.";
        return new(t, true, msg, GetState(t));
    }

    public static TweakResult Undo(Tweak t)
    {
        if (t.Kind == TweakKind.Guide)
        {
            BackupStore.Delete(t.Id);
            return new(t, true, "Unmarked.", TweakState.NotApplied);
        }
        if (t.RequiresAdmin && !Admin.IsElevated)
            return new(t, false, "Administrator rights are required. Use 'Restart as admin'.", GetState(t));

        var backup = BackupStore.Load(t.Id);
        var errors = new List<string>();
        for (int i = t.Ops.Count - 1; i >= 0; i--)
        {
            var ob = backup is not null && i < backup.Ops.Count ? backup.Ops[i] : null;
            try { t.Ops[i].Undo(ob); }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                Log.Error($"Undo '{t.Name}' step {i + 1} ({t.Ops[i].Describe()})", ex);
            }
        }
        if (errors.Count == 0)
        {
            BackupStore.Delete(t.Id);
            Log.Info($"Reverted: {t.Name}{(backup is null ? " (no backup found, restored Windows defaults)" : "")}");
            return new(t, true, t.RequiresReboot ? "Reverted. Restart Windows to finish." : "Reverted.", GetState(t));
        }
        return new(t, false, string.Join(" | ", errors), GetState(t));
    }
}

public static class RestorePoints
{
    /// <summary>Creates a System Restore checkpoint. Windows may skip it if one was created in the last 24 hours.</summary>
    public static (bool ok, string message) Create(string description)
    {
        if (!Admin.IsElevated) return (false, "Restore points require administrator rights.");
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
            inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var code = Convert.ToUInt32(outParams["ReturnValue"]);
            if (code == 0)
            {
                Log.Info($"Restore point created: {description}");
                return (true, "Restore point created.");
            }
            var msg = code == 1058
                ? "System Protection is turned off for the system drive. Enable it in System Properties > System Protection."
                : $"Windows returned code {code}. System Protection may be disabled, or a restore point was made recently.";
            Log.Warn("Restore point not created: " + msg);
            return (false, msg);
        }
        catch (Exception ex)
        {
            Log.Error("Restore point failed", ex);
            return (false, ex.Message);
        }
    }

    public static List<(DateTime when, string description)> List()
    {
        var list = new List<(DateTime, string)>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            foreach (ManagementObject o in s.Get())
                list.Add((ManagementDateTimeConverter.ToDateTime(o["CreationTime"].ToString()!), o["Description"]?.ToString() ?? ""));
        }
        catch (Exception ex) { Log.Debug("list restore points: " + ex.Message); }
        return list.OrderByDescending(x => x.Item1).ToList();
    }
}
