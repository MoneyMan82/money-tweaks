using System.Globalization;
using Microsoft.Win32;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

/// <summary>How far a change can be inspected before it is made.</summary>
public enum XRayReach
{
    /// <summary>The exact location and the current value were read from Windows.</summary>
    Exact,
    /// <summary>The location is known, but part of the effect can't be read in advance (e.g. reinstalling an app).</summary>
    Partial,
    /// <summary>A command whose effect can't be read in advance; only the command itself is shown.</summary>
    Unknown,
}

/// <summary>One system change a tweak makes: where, the value now, the value after applying, and what Undo restores.</summary>
public sealed record XRayChange(string Kind, string Location, string Current, string Proposed, string Undo, XRayReach Reach,
    bool AlreadySet, string? Note = null);

public sealed record XRayReport(Tweak Tweak, IReadOnlyList<XRayChange> Changes, string Purpose, string Risk, string Reversibility,
    string Backup, bool Applied, DateTime At)
{
    public bool HasUnknown => Changes.Any(c => c.Reach != XRayReach.Exact);
    public int Pending => Changes.Count(c => !c.AlreadySet);

    /// <summary>Short preview for a confirmation: each change as "location: now → after", unknown effects marked.</summary>
    public string Compact(int max = 10)
    {
        var lines = Changes.Where(c => !c.AlreadySet).Take(max).Select(c => c.Reach == XRayReach.Unknown
            ? $"• {c.Location}: {c.Proposed} [effect not inspectable]"
            : $"• {c.Location}: {c.Current} → {c.Proposed}{(c.Reach == XRayReach.Partial ? " [partly inspectable]" : "")}").ToList();
        if (Pending > max) lines.Add($"• …and {Pending - max} more (Inspect changes shows all)");
        if (lines.Count == 0) lines.Add("• nothing: every value is already set");
        return "WHAT CHANGES ON THIS PC\n" + string.Join("\n", lines) + $"\nUndo: {Reversibility}";
    }

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Purpose: {Purpose}\nRisk: {Risk}\nReversibility: {Reversibility}\nBackup: {Backup}\n");
        foreach (var c in Changes)
        {
            sb.Append($"\n{c.Kind}: {c.Location}{(c.Reach == XRayReach.Exact ? "" : c.Reach == XRayReach.Partial ? "  [partly inspectable]" : "  [NOT INSPECTABLE]")}\n");
            sb.Append($"  now: {c.Current}\n  {(Applied ? "set by tweak" : "after applying")}: {c.Proposed}{(c.AlreadySet && !Applied ? " (already set, unchanged)" : "")}\n  undo: {c.Undo}\n");
            if (c.Note is { } n) sb.Append($"  note: {n}\n");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Tweak X-Ray: what a tweak actually changes on this PC, derived only from its real operations (the same objects Apply and
/// Undo run). Current values are read through each operation's own Capture, the code that also writes the backup, so the
/// preview shows exactly what would be backed up. Read-only: nothing is applied, written or started. Commands are shown as
/// the command they run and marked not inspectable, since their effect can't be read in advance.
/// </summary>
public static class TweakXRay
{
    /// <summary>The tweak has system changes to inspect (guides only describe manual BIOS steps).</summary>
    public static bool Applies(Tweak t) => t.Kind == TweakKind.Toggle && t.Ops.Count > 0;

    public static XRayReport Inspect(Tweak t)
    {
        bool readable = BackupStore.TryLoad(t.Id, out var backup);
        // The engine only restores from a backup that lines up with the ops (otherwise it captures a fresh one).
        var aligned = backup is not null && backup.Ops.Count == t.Ops.Count ? backup : null;
        bool applied = aligned is not null;
        var rows = new List<XRayChange>();
        for (int i = 0; i < t.Ops.Count; i++)
        {
            try { rows.AddRange(Rows(t.Ops[i], aligned?.Ops[i], applied)); }
            catch (Exception ex)
            {
                Log.Debug($"x-ray {t.Id} step {i + 1}: {ex.Message}");
                rows.Add(new("Step", t.Ops[i].Describe(), "couldn't be read: " + ex.Message, t.Ops[i].Describe(), "see the step above", XRayReach.Unknown, false));
            }
        }
        var facts = TweakInfo.For(t);
        string risk = facts.Risk + " · " + Compatibility.TierLabel(Compatibility.TierOf(t))
            + (t.RequiresAdmin ? " · needs administrator rights" : "") + (t.RequiresReboot ? " · takes effect after a restart" : "");
        string bk = !readable ? $"the backup file ({t.Id}.json) is damaged; Apply and Undo refuse to run until it is fixed"
            : aligned is not null ? $"original values backed up {aligned.AppliedAt:g} in {t.Id}.json; Undo restores them and reads them back"
            : rows.Count > 0 && rows.All(r => r.AlreadySet) ? "none needed: nothing here would change (already set or not on this PC), so applying saves no backup"
            : backup is not null ? "an older backup doesn't match this tweak's steps; applying captures a fresh one"
            : "none yet; applying first captures every \"now\" value below into a backup, and rolls back if Windows doesn't keep the change";
        return new(t, rows, t.Description, risk, Reversibility(t, rows), bk, applied, DateTime.Now);
    }

    private static string Reversibility(Tweak t, List<XRayChange> rows)
    {
        if (t.Ops.Any(o => o is CommandOp))
            return "by command: Undo runs the reverse command shown below. Values a command changes aren't captured beforehand, so Undo restores what that command sets, which can differ from this PC's earlier setting.";
        if (t.Ops.Any(o => o is AppxRemoveOp))
            return "limited: Undo re-registers the app only while Windows still has its files; otherwise it must be reinstalled from the Microsoft Store.";
        return "full: every original value is captured before applying; Undo writes it back and checks the result.";
    }

    private static IEnumerable<XRayChange> Rows(TweakOp op, OpBackup? b, bool applied) => op switch
    {
        RegValueOp r => [Reg(r, b)],
        DynamicRegOp d => Dynamic(d, b),
        RegStringFlagOp f => [Flag(f, b)],
        ServiceStartOp s => [Service(s, b)],
        PowerCfgOp p => [Power(p, b)],
        ScheduledTaskOp st => [Task(st, b)],
        AppxRemoveOp a => [Appx(a, b, applied)],
        CommandOp c => [Command(c)],
        _ => [new("Step", op.Describe(), "unknown", op.Describe(), "unknown", XRayReach.Unknown, false, "This kind of step can't be inspected.")],
    };

    private static string RegLocation(Hive hive, string path, string name) =>
        $@"{(hive == Hive.HKLM ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER")}\{path}\{(name.Length == 0 ? "(Default)" : name)}";

    /// <summary>A registry value as Capture recorded it ("kind"/"value"/"keyExisted").</summary>
    private static string RegText(OpBackup b) =>
        b.Data.GetValueOrDefault("kind") is { } k ? $"{Show(b.Data.GetValueOrDefault("value"))} ({k})"
        : b.Data.GetValueOrDefault("keyExisted") == "False" ? "not set (the key doesn't exist)" : "not set";

    private static XRayChange Reg(RegValueOp r, OpBackup? b)
    {
        var now = r.Capture();
        string undo = b is not null ? "back to " + RegText(b) + (b.Data.GetValueOrDefault("keyExisted") == "False" && r.RemoveKeyIfCreated ? ", removing the key" : "")
            : "back to the \"now\" value, captured when you apply";
        return new("Registry", RegLocation(r.Hive, r.Path, r.Name), RegText(now),
            $"{Show(RegValueOp.FormatValue(r.Value, r.ValueKind))} ({r.ValueKind})", undo, XRayReach.Exact, r.Check() == OpState.Applied);
    }

    private static IEnumerable<XRayChange> Dynamic(DynamicRegOp d, OpBackup? b)
    {
        var targets = d.Resolve();
        if (targets.Count == 0)
        {
            yield return new("Registry (found at runtime)", d.Describe(), "nothing found on this PC", "nothing", "nothing", XRayReach.Partial, false,
                "The values are looked up on this PC when applying; none exist right now, so Apply would stop without changing anything.");
            yield break;
        }
        foreach (var o in targets)
        {
            string key = $@"{o.Hive}\{o.Path}\{o.Name}";
            OpBackup? ob = null;
            if (b is not null && b.Data.ContainsKey(key + "|keyExisted"))
            {
                ob = new OpBackup { Kind = "reg", Data = { ["keyExisted"] = b.Data[key + "|keyExisted"] } };
                if (b.Data.GetValueOrDefault(key + "|kind") is { } k) { ob.Data["kind"] = k; ob.Data["value"] = b.Data.GetValueOrDefault(key + "|value"); }
            }
            var row = Reg(o, ob);
            yield return row with { Kind = "Registry (found at runtime)", Note = d.Describe() };
        }
    }

    private static XRayChange Flag(RegStringFlagOp f, OpBackup? b)
    {
        var now = f.Capture().Data.GetValueOrDefault("value");
        var entries = RegStringFlagOp.Parse(now);
        string cur = entries.TryGetValue(f.Flag, out var v) ? $"{f.Flag}={v}" : $"{f.Flag} not set";
        string undo = b is null ? "back to the exact string captured when you apply"
            : b.Data.GetValueOrDefault("value") is { } orig ? $"back to the exact original string \"{orig}\"" : "removes the entry again";
        return new("Registry entry", RegLocation(f.Hive, f.Path, f.Name), cur + (now is null ? " (value doesn't exist)" : $" in \"{now}\""),
            $"{f.Flag}={f.Value}; other entries kept", undo, XRayReach.Exact, f.Check() == OpState.Applied);
    }

    private static XRayChange Service(ServiceStartOp s, OpBackup? b)
    {
        var cur = s.CurrentStart();
        string loc = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{s.Service}\Start";
        if (cur is null) return new("Service", loc, "not installed on this PC", "nothing (skipped)", "nothing", XRayReach.Exact, true);
        string undo = b?.Data.GetValueOrDefault("start") is { } o ? "back to " + ServiceStartOp.StartName(int.Parse(o, CultureInfo.InvariantCulture)) : $"back to {ServiceStartOp.StartName(cur.Value)}, captured when you apply";
        bool stricter = cur > s.Start;
        return new("Service start type", loc, ServiceStartOp.StartName(cur.Value), ServiceStartOp.StartName(s.Start), undo, XRayReach.Exact,
            cur == s.Start || stricter,
            stricter ? $"Already {ServiceStartOp.StartName(cur.Value)}, stricter than this tweak's setting, so it's left as is."
            : s.Start == 4 ? "Disabling also stops the service now if it's running." : null);
    }

    private static XRayChange Power(PowerCfgOp p, OpBackup? b)
    {
        var now = p.Capture();
        string F(OpBackup x) => x.Data.GetValueOrDefault("ac") is { } ac ? $"plugged in {ac}, on battery {x.Data.GetValueOrDefault("dc") ?? ac}" : "unknown (powercfg couldn't read it)";
        bool known = now.Data.GetValueOrDefault("ac") is not null;
        return new("Power plan setting", $"Active power plan › {p.Label} (powercfg {p.Subgroup} {p.Setting})", F(now),
            $"plugged in {p.Value}, on battery {p.Value}", b is not null ? "back to " + F(b) : "back to the \"now\" values, captured when you apply",
            known ? XRayReach.Exact : XRayReach.Partial, known && p.Check() == OpState.Applied, "Applies to the plan that is active when you apply.");
    }

    private static XRayChange Task(ScheduledTaskOp t, OpBackup? b)
    {
        var e = t.Capture().Data.GetValueOrDefault("enabled");
        string cur = e is null ? "not present on this PC" : e == "True" ? "enabled" : "disabled";
        string undo = b?.Data.GetValueOrDefault("enabled") is { } was ? (was == "False" ? "left disabled (it was disabled before)" : "enabled again")
            : e is null ? "nothing" : e == "False" ? "left disabled (it's disabled now)" : "enabled again";
        return new("Scheduled task", "Task Scheduler › " + t.TaskPath, cur, e is null ? "nothing (skipped)" : "disabled", undo, XRayReach.Exact, e != "True");
    }

    private static XRayChange Appx(AppxRemoveOp a, OpBackup? b, bool applied)
    {
        bool installed = AppxRemoveOp.InstalledNames().Contains(a.PackageName);
        return new("Store app", $"Microsoft Store package {a.PackageName} (current user)", installed ? "installed" : "not installed",
            installed || applied ? "removed for the current user" : "nothing (not installed)",
            b?.Data.GetValueOrDefault("installed") == "False" ? "nothing (it wasn't installed before)" : "re-registers the package if Windows still has its files; otherwise reinstall it from the Microsoft Store",
            XRayReach.Partial, !installed, "Windows deletes the app's data for this account along with it; Money Tweaks doesn't back that data up.");
    }

    private static XRayChange Command(CommandOp c)
    {
        string cur;
        if (!c.HasCheck) cur = "unknown: this step has no read-only check";
        else
        {
            var output = c.ReadCheckOutput();
            cur = output is null ? "unknown: the check couldn't run"
                : $"check reports {(c.Check() == OpState.Applied ? "already applied" : "not applied")}: {Short(output)}";
        }
        string shell = c.Shell == Shell.PowerShell ? "PowerShell" : "Command Prompt";
        return new("Command", c.Label ?? "command", cur, $"runs ({shell}): {c.ApplyCommand}", $"runs: {c.UndoCommand}", XRayReach.Unknown,
            c.HasCheck && c.Check() == OpState.Applied,
            "What this command changes can't be read before it runs, so the previous values aren't backed up; Money Tweaks checks the result afterwards" + (c.HasCheck ? " with the check shown above." : ", but this step has no check."));
    }

    private static string Show(string? v) => v is null ? "(empty)" : v.Length == 0 ? "\"\"" : v.Replace('\0', '|');
    private static string Short(string s) { s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); return s.Length > 160 ? s[..160] + "…" : s; }

    /// <summary>One line per backed-up step for the log, written right after Apply captures the original values.</summary>
    public static string BackupLine(TweakOp op, OpBackup b) => op switch
    {
        RegValueOp => RegText(b),
        RegStringFlagOp => b.Data.GetValueOrDefault("value") is { } v ? $"\"{v}\"" : "not set",
        ServiceStartOp => b.Data.GetValueOrDefault("start") is { } s ? ServiceStartOp.StartName(int.Parse(s, CultureInfo.InvariantCulture)) : "not installed",
        _ => b.Data.Count == 0 ? "(no value captured)" : string.Join(", ", b.Data.Select(kv => $"{kv.Key}={Show(kv.Value)}")),
    };
}
