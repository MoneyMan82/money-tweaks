using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

public enum RescueBackupStatus { Ok, Damaged, OlderVersion, UnknownTweak }

/// <summary>One setting a change touched: where it is, what Windows reads now, and what the backup will put back.</summary>
public sealed record RescueValue(string Location, string Now, string Original);

/// <summary>A Money Tweaks change found in the backups folder, as Emergency Rescue shows it.</summary>
public sealed record RescueEntry(string Id, string Name, Tweak? Tweak, TweakBackup? Backup, RescueBackupStatus Status,
    TweakState State, bool NeedsAdmin, IReadOnlyList<RescueValue> Values)
{
    public DateTime? AppliedAt => Backup?.AppliedAt;
    /// <summary>Restorable from its backup by the engine's own Undo. A damaged backup is never guessed at.</summary>
    public bool CanRestore => Tweak is not null && Status is RescueBackupStatus.Ok or RescueBackupStatus.OlderVersion;
    public string StatusText => Status switch
    {
        RescueBackupStatus.Damaged => "backup file is damaged: not restorable automatically",
        RescueBackupStatus.UnknownTweak => "this version doesn't know this change: backup kept",
        RescueBackupStatus.OlderVersion => "backup from an older version covers only some steps",
        _ when Tweak?.Kind == TweakKind.Guide => "marked as done (a BIOS step: restoring only removes the mark)",
        _ => State switch
        {
            TweakState.Applied => "applied",
            TweakState.Partial => "partly applied",
            TweakState.NotApplied => "already reads as original (Windows or another tool reverted it)",
            _ => "not present on this PC any more",
        },
    };
}

public sealed record RescueLogLine(DateTime Time, string Level, string Text);

public sealed record RescueOutcome(string Id, string Name, bool Ok, string Message, bool Skipped = false, bool NeedsAdmin = false);

/// <summary>One recovery run. Saved as session.json in its folder before anything changes and again when it ends.</summary>
public sealed class RescueSession
{
    public string Id { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public bool Elevated { get; set; }
    public List<string> Selected { get; set; } = [];
    public string? Profile { get; set; }
    public List<RescueOutcome> Outcomes { get; set; } = [];
    public bool RestartRecommended { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Folder { get; set; } = "";
}

/// <summary>
/// Emergency Rescue: inspects and undoes Money Tweaks changes without the main app, straight from the existing backup
/// files (backups\*.json, one TweakBackup per applied tweak) and the daily logs. Every restore goes through
/// <see cref="TweakEngine.Undo"/>, the same code as the app's Undo buttons, which verifies each value by read-back.
/// Before anything changes, each selected tweak's backup is copied and the values Windows has right now are captured in
/// the same TweakBackup format, under rescue\&lt;time&gt;\, so a rescue can itself be undone. Nothing is reset to
/// Windows defaults blindly: a tweak without a readable backup is listed, never guessed at, and no user data is deleted.
/// </summary>
public static class EmergencyRescue
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string SessionsRoot => Path.Combine(AppPaths.Root, "rescue");

    /// <summary>Every change in the backups folder, newest first. Reads Windows but changes nothing.</summary>
    public static List<RescueEntry> Scan(Func<string, Tweak?>? find = null)
    {
        find ??= TweakCatalog.Find;
        var list = new List<RescueEntry>();
        if (!Directory.Exists(Path.Combine(AppPaths.Root, "backups"))) return list;
        foreach (var id in BackupStore.AllIds())
        {
            var t = find(id);
            bool readable = BackupStore.TryLoad(id, out var b);
            if (t is null) { list.Add(new(id, id, null, b, RescueBackupStatus.UnknownTweak, TweakState.NotApplicable, false, [])); continue; }
            if (!readable) { list.Add(new(id, t.Name, t, null, RescueBackupStatus.Damaged, Safe(() => TweakEngine.GetState(t), TweakState.Partial), t.RequiresAdmin, [])); continue; }
            var status = b is not null && t.Kind != TweakKind.Guide && b.Ops.Count != t.Ops.Count ? RescueBackupStatus.OlderVersion : RescueBackupStatus.Ok;
            list.Add(new(id, t.Name, t, b, status, Safe(() => TweakEngine.GetState(t), TweakState.Partial), t.RequiresAdmin, Values(t, b)));
        }
        return [.. list.OrderByDescending(e => e.AppliedAt ?? DateTime.MinValue).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch (Exception ex) { Log.Debug("rescue read: " + ex.Message); return fallback; } }

    private static List<RescueValue> Values(Tweak t, TweakBackup? b)
    {
        var rows = new List<RescueValue>();
        for (int i = 0; i < t.Ops.Count; i++)
        {
            var op = t.Ops[i];
            if (op is DynamicRegOp d)
            {
                // One row per value found on this PC (each network adapter, GPU…), in the per-target form DynamicRegOp backs up.
                var data = b is not null && i < b.Ops.Count ? b.Ops[i].Data : null;
                foreach (var target in Safe(() => d.Resolve(), []))
                {
                    var key = $@"{target.Hive}\{target.Path}\{target.Name}";
                    string orig2 = "(no backup of this value: Windows default)";
                    if (data is not null && data.ContainsKey(key + "|keyExisted"))
                    {
                        var ob = new OpBackup { Kind = "reg", Data = { ["keyExisted"] = data[key + "|keyExisted"] } };
                        if (data.GetValueOrDefault(key + "|kind") is { } k) { ob.Data["kind"] = k; ob.Data["value"] = data.GetValueOrDefault(key + "|value"); }
                        orig2 = TweakXRay.BackupLine(target, ob);
                    }
                    rows.Add(new($"{key} → {RegValueOp.FormatValue(target.Value, target.ValueKind)} ({target.ValueKind})",
                        Safe(() => TweakXRay.BackupLine(target, target.Capture()), "could not be read"), orig2));
                }
                continue;
            }
            var orig = b is not null && i < b.Ops.Count ? TweakXRay.BackupLine(op, b.Ops[i]) : "(no backup of this step: Windows default)";
            var now = op is CommandOp c
                ? c.HasCheck ? (c.Check() == OpState.Applied ? "changed (check reads applied)" : "original (check reads not applied)") : "no read-back for this command"
                : Safe(() => TweakXRay.BackupLine(op, op.Capture()), "could not be read");
            rows.Add(new(op.Describe(), now, orig));
        }
        return rows;
    }

    /// <summary>The active gaming profile, if one is on. Turning it off restores what it changed from its own saved state.</summary>
    public static ProfileSession? ActiveProfile() => Safe(() => File.Exists(ProfileStore.FilePath) ? ProfileStore.Load().Active : null, null);

    // ---- change log ------------------------------------------------------------------------------------------------
    // Log times use the culture's separator ("16:46:59", or "16.46.59" on Finnish Windows), so any separator is accepted.
    private static readonly Regex LogLine = new(@"^(\d{1,2})\D(\d{2})\D(\d{2}) \[(\w+)\] (.*)$", RegexOptions.Compiled);
    private static readonly string[] ChangeMarkers =
        ["Applied: ", "Reverted: ", "Apply '", "Undo '", "Profile '", "Emergency Rescue", "Uninstall restore: Tweak", "Restore point created"];

    /// <summary>Change entries from the daily logs (moneytweaks-yyyy-MM-dd.log) of the last <paramref name="days"/> days, newest first.</summary>
    public static List<RescueLogLine> RecentChanges(int days = 14, int max = 200)
    {
        var lines = new List<RescueLogLine>();
        var dir = Path.Combine(AppPaths.Root, "logs");
        if (!Directory.Exists(dir)) return lines;
        var since = DateTime.Today.AddDays(-days);
        foreach (var f in Directory.EnumerateFiles(dir, "moneytweaks-*.log"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)["moneytweaks-".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day < since) continue;
            string[] text;
            // The app may be writing to today's log: open it shared.
            try { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var r = new StreamReader(fs); text = r.ReadToEnd().Split('\n'); }
            catch (Exception ex) { Log.Debug($"rescue log {f}: {ex.Message}"); continue; }
            foreach (var raw in text)
            {
                var m = LogLine.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;
                var msg = m.Groups[5].Value;
                // "<id> before step N: <setting> | was <original>" is the original value captured when a tweak was applied.
                if (!ChangeMarkers.Any(k => msg.StartsWith(k, StringComparison.Ordinal)) && !msg.Contains(" before step ", StringComparison.Ordinal)) continue;
                int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), se = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (h > 23 || mi > 59 || se > 59) continue;
                lines.Add(new(day + new TimeSpan(h, mi, se), m.Groups[4].Value, msg));
            }
        }
        // Newest first; lines from the same second keep the order they were written in.
        return [.. lines.Select((l, i) => (l, i)).OrderByDescending(x => x.l.Time).ThenByDescending(x => x.i).Select(x => x.l).Take(max)];
    }

    // ---- restore ---------------------------------------------------------------------------------------------------
    /// <summary>
    /// Restores <paramref name="picked"/> (and turns off the active gaming profile first when <paramref name="profile"/>
    /// is set). Per tweak: copy its backup and capture the current values into the session folder, then Undo with
    /// read-back verification. A tweak whose current values can't be captured is not touched. Tweaks that need
    /// administrator rights are skipped (and reported) when this process isn't elevated.
    /// </summary>
    public static RescueSession Restore(IReadOnlyList<RescueEntry> picked, ProfileSession? profile = null, IProgress<string>? progress = null)
    {
        bool elevated = Admin.IsElevated;
        var s = NewSession(elevated);
        s.Selected = [.. picked.Select(p => p.Id)];
        s.Profile = profile?.ProfileName;
        Save(s);
        Log.Info($"Emergency Rescue started: {picked.Count} change(s){(profile is null ? "" : $" and profile '{profile.ProfileName}'")}, admin: {elevated}, saved to {s.Folder}");

        if (profile is not null)
        {
            progress?.Report($"Turning off the gaming profile '{profile.ProfileName}'");
            try
            {
                File.Copy(ProfileStore.FilePath, Path.Combine(s.Folder, "profiles.json"), overwrite: true);
                var problems = ProfileEngine.Deactivate(profile);
                if (problems.Count == 0)
                {
                    var file = ProfileStore.Load(); file.Active = null; ProfileStore.Save(file);
                    s.Outcomes.Add(new("profile", $"Gaming profile: {profile.ProfileName}", true, "Turned off; its settings were restored and read back."));
                }
                else s.Outcomes.Add(new("profile", $"Gaming profile: {profile.ProfileName}", false, string.Join(" ", problems), NeedsAdmin: !elevated));
            }
            catch (Exception ex) { s.Outcomes.Add(new("profile", $"Gaming profile: {profile.ProfileName}", false, "Could not turn it off: " + ex.Message)); }
            Save(s);
        }

        int n = 0;
        foreach (var e in picked)
        {
            n++;
            progress?.Report($"Restoring {n} of {picked.Count}: {e.Name}");
            s.Outcomes.Add(RestoreOne(e, s.Folder, elevated, out bool restart));
            s.RestartRecommended |= restart;
            Save(s);
        }
        s.FinishedAt = DateTime.Now;
        Save(s);
        Log.Info($"Emergency Rescue finished: {s.Outcomes.Count(o => o.Ok)} restored, {s.Outcomes.Count(o => !o.Ok)} not restored");
        return s;
    }

    private static RescueOutcome RestoreOne(RescueEntry e, string folder, bool elevated, out bool restart)
    {
        restart = false;
        if (e.Tweak is not { } t || !e.CanRestore)
            return new(e.Id, e.Name, false, "Not restored: " + e.StatusText + ".", Skipped: true);
        if (t.RequiresAdmin && !elevated)
            return new(e.Id, e.Name, false, "Needs administrator rights; nothing was changed.", Skipped: true, NeedsAdmin: true);
        try
        {
            // 1. Keep the backup the engine consumes on success, and the values Windows has right now.
            Directory.CreateDirectory(Path.Combine(folder, "original"));
            Directory.CreateDirectory(Path.Combine(folder, "before-rescue"));
            if (BackupStore.Exists(t.Id)) File.Copy(BackupStore.PathOf(t.Id), Path.Combine(folder, "original", t.Id + ".json"), overwrite: true);
            var current = new TweakBackup { TweakId = t.Id, AppliedAt = DateTime.Now };
            foreach (var op in t.Ops) current.Ops.Add(op.Capture());
            AtomicFile.WriteAllText(Path.Combine(folder, "before-rescue", t.Id + ".json"), JsonSerializer.Serialize(current, Json));
        }
        catch (Exception ex)
        {
            Log.Error($"Emergency Rescue: could not back up the current values of {t.Id}; not restored", ex);
            return new(e.Id, e.Name, false, $"Could not back up the current values first ({ex.Message}), so nothing was changed.", Skipped: true);
        }
        try
        {
            var r = TweakEngine.Undo(t);
            Log.Info($"Emergency Rescue: {t.Id}: {(r.Success ? "restored" : "not restored")} - {r.Message}");
            restart = r.Success && t.RequiresReboot;
            return new(e.Id, e.Name, r.Success, r.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Emergency Rescue: undo of {t.Id} failed", ex);
            return new(e.Id, e.Name, false, "Undo failed: " + ex.Message + " The backup was kept.");
        }
    }

    /// <summary>
    /// Reads every restored value back and compares it with the original backup kept in the session folder. Returns one
    /// line per change; empty mismatch list means it is exactly back.
    /// </summary>
    public static List<(RescueOutcome outcome, List<string> mismatches)> Verify(RescueSession s, Func<string, Tweak?>? find = null)
    {
        find ??= TweakCatalog.Find;
        var result = new List<(RescueOutcome, List<string>)>();
        foreach (var o in s.Outcomes.Where(o => o.Ok && o.Id != "profile"))
        {
            if (find(o.Id) is not { } t) { result.Add((o, ["this version doesn't know the change any more"])); continue; }
            var orig = LoadBackup(Path.Combine(s.Folder, "original", o.Id + ".json"));
            var diffs = TweakVerifier.RestoreMismatches(t, orig);
            if (orig is null && t.Kind != TweakKind.Guide) diffs.Insert(0, "no copy of the original backup in the rescue folder; compared with Windows defaults");
            result.Add((o, diffs));
        }
        return result;
    }

    // ---- earlier rescues -------------------------------------------------------------------------------------------
    public static List<RescueSession> Sessions()
    {
        var list = new List<RescueSession>();
        if (!Directory.Exists(SessionsRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(SessionsRoot))
        {
            try
            {
                var s = JsonSerializer.Deserialize<RescueSession>(File.ReadAllText(Path.Combine(dir, "session.json")));
                if (s is null) continue;
                s.Folder = dir;
                list.Add(s);
            }
            catch (Exception ex) { Log.Debug($"rescue session {dir}: {ex.Message}"); }
        }
        return [.. list.OrderByDescending(s => s.StartedAt)];
    }

    /// <summary>
    /// Undoes a rescue for one tweak: puts its original backup back into the backups folder and applies the tweak again
    /// through the engine, which keeps that backup (so a later Undo still restores the true originals) and verifies the
    /// result. Refused when the tweak has been applied again since (a newer backup exists).
    /// </summary>
    public static RescueOutcome PutBack(RescueSession s, string id, Func<string, Tweak?>? find = null)
    {
        find ??= TweakCatalog.Find;
        if (find(id) is not { } t) return new(id, id, false, "This version doesn't know this change.");
        var orig = Path.Combine(s.Folder, "original", id + ".json");
        if (!File.Exists(orig) || LoadBackup(orig) is null) return new(id, t.Name, false, "The rescue folder has no readable copy of its original backup.");
        if (BackupStore.Exists(id)) return new(id, t.Name, false, "It has been applied again since the rescue; nothing was changed.");
        if (t.RequiresAdmin && !Admin.IsElevated) return new(id, t.Name, false, "Needs administrator rights; nothing was changed.", NeedsAdmin: true);
        File.Copy(orig, BackupStore.PathOf(id));
        var r = TweakEngine.Apply(t);
        if (!r.Success && TweakEngine.GetState(t) != TweakState.Applied) BackupStore.Delete(id); // back to how the rescue left it
        Log.Info($"Emergency Rescue: put back {id}: {(r.Success ? "applied again" : "not applied")} - {r.Message}");
        return new(id, t.Name, r.Success, r.Message);
    }

    /// <summary>
    /// Parses a list selection like "1,3-5", "all" or "2 4" into zero-based indexes below <paramref name="count"/>.
    /// Null when anything in it isn't a valid number or range, so a typo never selects something unintended.
    /// </summary>
    public static List<int>? ParseSelection(string? text, int count)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) return [.. Enumerable.Range(0, count)];
        var picked = new SortedSet<int>();
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-');
            if (ends.Length > 2 || !ends.All(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
            int a = int.Parse(ends[0], CultureInfo.InvariantCulture), b = int.Parse(ends[^1], CultureInfo.InvariantCulture);
            if (a < 1 || b > count || a > b) return null;
            for (int i = a; i <= b; i++) picked.Add(i - 1);
        }
        return picked.Count == 0 ? null : [.. picked];
    }

    private static TweakBackup? LoadBackup(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TweakBackup>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static RescueSession NewSession(bool elevated)
    {
        var now = DateTime.Now;
        var id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(SessionsRoot, id);
        for (int i = 2; Directory.Exists(folder); i++) folder = Path.Combine(SessionsRoot, $"{id}-{i}");
        Directory.CreateDirectory(folder);
        return new RescueSession { Id = Path.GetFileName(folder), StartedAt = now, Elevated = elevated, Folder = folder };
    }

    private static void Save(RescueSession s) => AtomicFile.WriteAllText(Path.Combine(s.Folder, "session.json"), JsonSerializer.Serialize(s, Json));
}
