using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

public enum VerifyStatus { Pass, Warning, Fail, Unverifiable }

public sealed record VerifyReport(Tweak Tweak, VerifyStatus Status, TweakState State, string Summary,
    IReadOnlyList<string> Evidence, IReadOnlyList<AuditIssue> Issues, DateTime At)
{
    public string StatusLabel => Status switch
    {
        VerifyStatus.Pass => "PASS", VerifyStatus.Warning => "WARNING", VerifyStatus.Fail => "FAIL", _ => "UNVERIFIABLE"
    };

    public string ToText() =>
        $"[{StatusLabel}] {Tweak.Name} ({Tweak.Id}), state {State}: {Summary}" +
        string.Concat(Evidence.Select(e => "\n    • " + e)) +
        string.Concat(Issues.Select(i => $"\n    ! {i.Label}: {i.Text}"));
}

/// <summary>
/// Read-only verification: reads every value a tweak controls back from Windows, checks that an applied tweak has a
/// usable backup, and where possible checks the live effect (e.g. the mouse setting Windows is actually using, the
/// priority of a running game). A command having run is never counted as proof.
/// </summary>
public static class TweakVerifier
{
    private sealed record ReadBack(OpState State, string Text, bool Verifiable);

    public static bool HasReadBack(TweakOp op) => op is not CommandOp c || c.HasCheck;

    public static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static VerifyReport Verify(Tweak t)
    {
        var issues = TweakAudit.IssuesFor(t).ToList();
        var ev = new List<string>();
        if (t.Kind == TweakKind.Guide) return VerifyGuide(t, issues);
        if (t.MinBuild > 0 && OsInfo.Build < t.MinBuild)
            return new(t, VerifyStatus.Unverifiable, TweakState.NotApplicable, $"Needs Windows build {t.MinBuild}+, this PC has {OsInfo.Build}.", ev, issues, DateTime.Now);

        var reads = t.Ops.Select(Read).ToList();
        ev.AddRange(reads.Select(r => r.Text));
        var state = TweakEngine.GetState(t);
        bool backupFile = BackupStore.Exists(t.Id);
        var backup = backupFile ? BackupStore.Load(t.Id) : null;
        bool allVerifiable = reads.All(r => r.Verifiable);
        var status = VerifyStatus.Pass;
        string summary;
        void Worse(VerifyStatus s) { if (Rank(s) > Rank(status)) status = s; }

        switch (state)
        {
            case TweakState.NotApplicable when backup is not null:
                summary = "Applied by Money Tweaks; the item it removed is no longer present.";
                break;
            case TweakState.NotApplicable:
                return new(t, VerifyStatus.Unverifiable, state, "Not present on this PC (nothing to change or test).", ev, issues, DateTime.Now);
            case TweakState.Applied:
                summary = allVerifiable ? "Active: every setting was read back from Windows and matches." : "Marked active, but at least one step has no read-back, so the effect is unverified.";
                if (!allVerifiable) Worse(VerifyStatus.Unverifiable);
                break;
            case TweakState.Partial:
                summary = backup is not null
                    ? $"Only {reads.Count(r => r.State == OpState.Applied)} of {reads.Count(r => r.State != OpState.NotApplicable)} settings are active. Windows or another program changed some back."
                    : "Partly set (by Windows, policy or another tool), not by Money Tweaks.";
                if (backup is not null) Worse(VerifyStatus.Fail);
                break;
            default:
                if (backup is not null)
                {
                    summary = "Money Tweaks applied this, but Windows now reads back the original values. Something reverted it (Windows Update, policy or another tool).";
                    Worse(VerifyStatus.Fail);
                }
                else
                {
                    summary = allVerifiable ? "Not applied. The state check works: current values were read back from Windows." : "Not applied. No read-back exists for at least one step.";
                    if (!allVerifiable) Worse(VerifyStatus.Unverifiable);
                }
                break;
        }

        // Undo readiness.
        if (state is TweakState.Applied or TweakState.Partial)
        {
            if (backupFile && backup is null) { ev.Add("Backup file exists but is unreadable, so Undo can only restore Windows defaults."); Worse(VerifyStatus.Fail); }
            else if (backup is null) { ev.Add("No Money Tweaks backup: this was set outside Money Tweaks. Undo restores Windows defaults."); }
            else if (backup.Ops.Count != t.Ops.Count) { ev.Add($"Backup covers {backup.Ops.Count} of {t.Ops.Count} steps (made by an older version)."); Worse(VerifyStatus.Warning); }
            else ev.Add($"Backup for Undo: OK (saved {backup.AppliedAt:g}).");
            if (t.RequiresReboot && backup is not null && backup.AppliedAt > BootTime)
            {
                ev.Add($"Restart pending: applied {backup.AppliedAt:g}, Windows last started {BootTime:g}. The setting is written but not in effect yet.");
                Worse(VerifyStatus.Warning);
            }
            foreach (var (ok, text) in EffectChecks(t))
            {
                ev.Add(text);
                if (ok == false) Worse(VerifyStatus.Warning);
            }
        }

        if (!Admin.IsElevated && t.Ops.OfType<CommandOp>().Any(c => c.CheckCommand?.StartsWith("bcdedit", StringComparison.OrdinalIgnoreCase) == true))
        {
            ev.Add("Reading the boot configuration needs administrator rights; run Money Tweaks as admin for a reliable result.");
            Worse(VerifyStatus.Unverifiable);
        }

        // Known limitations lower a pass to a warning so they are visible; plain duplicates are informational.
        foreach (var i in issues)
        {
            if (i.Kind == AuditKind.Duplicate) continue;
            if (i.Kind == AuditKind.Conflict && i.Other is not null)
            {
                var other = Catalog.TweakCatalog.Find(i.Other);
                if (other is null || state != TweakState.Applied || TweakEngine.GetState(other) != TweakState.Applied) continue;
                ev.Add($"Conflict is active: '{other.Name}' is also applied.");
            }
            else if (i.Kind == AuditKind.Conflict) continue;
            if (status == VerifyStatus.Pass) { status = VerifyStatus.Warning; summary += $" {i.Label}: {i.Text}"; }
        }
        if (TweakAudit.DisabledReason(t) is not null && state == TweakState.NotApplied && status == VerifyStatus.Warning) status = VerifyStatus.Pass;
        return new(t, status, state, summary, ev, issues, DateTime.Now);
    }

    private static int Rank(VerifyStatus s) => s switch { VerifyStatus.Pass => 0, VerifyStatus.Unverifiable => 1, VerifyStatus.Warning => 2, _ => 3 };

    private static ReadBack Read(TweakOp op)
    {
        try
        {
            switch (op)
            {
                case RegValueOp r:
                {
                    var b = r.Capture();
                    var cur = b.Data.GetValueOrDefault("kind") is null ? "(not set)" : $"{b.Data.GetValueOrDefault("value")} ({b.Data["kind"]})";
                    var st = r.Check();
                    return new(st, $"{r.Hive}\\{r.Path}\\{(r.Name.Length == 0 ? "(Default)" : r.Name)}: reads {cur}, target {RegValueOp.FormatValue(r.Value, r.ValueKind)} → {Word(st)}", true);
                }
                case DynamicRegOp d:
                {
                    var targets = d.Resolve();
                    if (targets.Count == 0) return new(OpState.NotApplicable, $"{d.Describe()}: nothing found on this PC", true);
                    int ok = targets.Count(x => x.Check() == OpState.Applied);
                    return new(ok == targets.Count ? OpState.Applied : OpState.NotApplied, $"{d.Describe()}: {ok} of {targets.Count} values match", true);
                }
                case ServiceStartOp s:
                {
                    var cur = s.CurrentStart();
                    if (cur is null) return new(OpState.NotApplicable, $"Service {s.Service}: not installed", true);
                    string run = "";
                    try { using var sc = new System.ServiceProcess.ServiceController(s.Service); run = $", currently {sc.Status}"; } catch { }
                    var st = s.Check();
                    return new(st, $"Service {s.Service}: start type {ServiceStartOp.StartName(cur.Value)}{run}, target {ServiceStartOp.StartName(s.Start)} → {Word(st)}", true);
                }
                case PowerCfgOp p:
                {
                    var b = p.Capture();
                    var st = p.Check();
                    return new(st, $"{p.Describe()}: active plan reads AC {b.Data.GetValueOrDefault("ac") ?? "?"}, DC {b.Data.GetValueOrDefault("dc") ?? "?"} → {Word(st)}", true);
                }
                case ScheduledTaskOp k:
                {
                    var st = k.Check();
                    var en = k.Capture().Data.GetValueOrDefault("enabled");
                    return new(st, $"Task {k.TaskPath}: {(en is null ? "not present" : en == "True" ? "enabled" : "disabled")} → {Word(st)}", true);
                }
                case AppxRemoveOp a:
                {
                    var installed = AppxRemoveOp.InstalledNames().Contains(a.PackageName);
                    return new(a.Check(), $"Store package {a.PackageName}: {(installed ? "installed" : "not installed")}", true);
                }
                case CommandOp c when c.HasCheck:
                {
                    var outp = c.ReadCheckOutput();
                    var st = c.Check();
                    var first = (outp ?? "(no output)").Trim().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(empty)";
                    if (first.Length > 120) first = first[..120] + "…";
                    return new(st, $"{c.Describe()}: check reads \"{first}\" → {Word(st)}", outp is not null);
                }
                case CommandOp c:
                    return new(OpState.Unknown, $"{c.Describe()}: Windows offers no read-back for this command", false);
                default:
                    return new(op.Check(), op.Describe(), true);
            }
        }
        catch (Exception ex) { return new(OpState.Unknown, $"{op.Describe()}: could not read ({ex.Message})", false); }
    }

    private static string Word(OpState s) => s switch { OpState.Applied => "matches", OpState.NotApplied => "differs", OpState.NotApplicable => "n/a", _ => "unknown" };

    // ---- live-effect checks ---------------------------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, int[] pv, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, out int pv, uint winIni);

    /// <summary>Checks what Windows is using right now, not just what is stored. ok=false means stored but not in effect.</summary>
    private static IEnumerable<(bool? ok, string text)> EffectChecks(Tweak t)
    {
        switch (t.Id)
        {
            case "gen.mouse-accel":
            {
                var m = new int[3];
                if (SystemParametersInfo(0x0003, 0, m, 0)) // SPI_GETMOUSE
                    yield return m[2] == 0 ? (true, "Live: Windows reports pointer acceleration off.") : (false, "Live: Windows still uses pointer acceleration. Sign out and back in for it to take effect.");
                break;
            }
            case "gen.menu-delay":
                if (SystemParametersInfo(0x006A, 0, out int d, 0)) // SPI_GETMENUSHOWDELAY
                    yield return d == 0 ? (true, "Live: menu delay is 0 ms.") : (false, $"Live: menu delay is still {d} ms. Sign out and back in.");
                break;
            case "gen.keyboard-repeat":
                if (SystemParametersInfo(0x0016, 0, out int kd, 0) && SystemParametersInfo(0x000A, 0, out int ks, 0)) // SPI_GETKEYBOARDDELAY / SPEED
                    yield return kd == 0 && ks == 31 ? (true, "Live: repeat delay 0, repeat rate 31.") : (false, $"Live: repeat delay {kd}, rate {ks}. Sign out and back in.");
                break;
            case "dbl.telemetry":
            {
                var edition = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "")?.ToString() ?? "";
                bool honoured = edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.Contains("Education", StringComparison.OrdinalIgnoreCase) || edition.Contains("Server", StringComparison.OrdinalIgnoreCase);
                yield return honoured ? (true, $"Edition {edition}: level 0 is honoured.") : (false, $"Edition {edition}: Windows uses level 1 (Required), the lowest it allows here.");
                break;
            }
        }
        // CPU priority profiles: if the program is running, read the priority Windows actually gave it.
        foreach (var r in t.Ops.OfType<RegValueOp>().Where(o => o.Name == "CpuPriorityClass"))
        {
            var exe = r.Path.Split('\\')[^2];
            var want = PriorityName(Convert.ToInt32(r.Value, CultureInfo.InvariantCulture));
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            try
            {
                if (procs.Length == 0) { yield return (null, $"{exe} is not running, so its live priority can't be checked (takes effect on next launch)."); continue; }
                string? got = null;
                try { got = procs[0].PriorityClass.ToString(); } catch { }
                if (got is null) yield return (null, $"{exe} is running but its priority can't be read (protected process).");
                else yield return got == want ? (true, $"Live: {exe} runs at {got}.") : (false, $"Live: {exe} runs at {got}, expected {want}. It was probably started before the tweak, or the app/anti-cheat sets its own priority.");
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        foreach (var s in t.Ops.OfType<ServiceStartOp>().Where(s => s.Start == 4))
        {
            string? status = null;
            try { using var sc = new System.ServiceProcess.ServiceController(s.Service); status = sc.Status.ToString(); } catch { }
            if (status == "Running") yield return (false, $"Service {s.Service} is disabled but still running; it stops after a restart.");
        }
    }

    private static string PriorityName(int ifeo) => ifeo switch
    {
        1 => "Idle", 2 => "Normal", 3 => "High", 5 => "BelowNormal", 6 => "AboveNormal", _ => ifeo.ToString(CultureInfo.InvariantCulture)
    };

    private static VerifyReport VerifyGuide(Tweak t, List<AuditIssue> issues)
    {
        var ev = new List<string>();
        var state = TweakEngine.GetState(t);
        switch (t.Id)
        {
            case "bios.xmp":
                try
                {
                    int? rated = null, conf = null;
                    using var s = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
                    foreach (var o in s.Get())
                    {
                        if (o["Speed"] is uint sp && sp > 0) rated = (int)Math.Max(rated ?? 0, sp);
                        if (o["ConfiguredClockSpeed"] is uint cc && cc > 0) conf = (int)Math.Max(conf ?? 0, cc);
                    }
                    if (rated is null || conf is null) break;
                    ev.Add($"Memory: configured {conf} MT/s, module SPD speed {rated} MT/s (Win32_PhysicalMemory).");
                    // Many modules report their JEDEC speed as 'Speed', so equal values prove nothing either way.
                    if (conf < rated) return new(t, VerifyStatus.Warning, state, "Memory runs below its reported speed: XMP/EXPO is off.", ev, issues, DateTime.Now);
                    ev.Add("Windows can't see whether a faster XMP/EXPO profile exists above this speed. Compare with the speed printed on the RAM kit.");
                    return new(t, VerifyStatus.Unverifiable, state, "Memory runs at its reported speed; whether that is the XMP speed can't be read.", ev, issues, DateTime.Now);
                }
                catch (Exception ex) { ev.Add("WMI memory query failed: " + ex.Message); }
                break;
            case "bios.rebar":
            {
                var smi = Path.Combine(ProcessRunner.System32, "nvidia-smi.exe");
                if (!File.Exists(smi)) { ev.Add("No NVIDIA tool found. Check in your GPU control panel (NVIDIA: System Information; AMD Software: System)."); break; }
                var r = ProcessRunner.Run(smi, "-q -d MEMORY");
                var fb = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"FB Memory Usage[\s\S]*?Total\s*:\s*(\d+)\s*MiB");
                var bar = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"BAR1 Memory Usage[\s\S]*?Total\s*:\s*(\d+)\s*MiB");
                if (!fb.Success || !bar.Success) { ev.Add("nvidia-smi output didn't include BAR1 size."); break; }
                int fbMb = int.Parse(fb.Groups[1].Value, CultureInfo.InvariantCulture), barMb = int.Parse(bar.Groups[1].Value, CultureInfo.InvariantCulture);
                ev.Add($"nvidia-smi: BAR1 {barMb} MiB, VRAM {fbMb} MiB.");
                return barMb >= fbMb
                    ? new(t, VerifyStatus.Pass, state, "Resizable BAR is active (BAR1 covers all VRAM).", ev, issues, DateTime.Now)
                    : new(t, VerifyStatus.Warning, state, "Resizable BAR is off (BAR1 is smaller than VRAM).", ev, issues, DateTime.Now);
            }
        }
        if (ev.Count == 0) ev.Add("This is a firmware setting Windows can't read. Check it in the BIOS/UEFI.");
        return new(t, VerifyStatus.Unverifiable, state, "Firmware setting: Windows exposes no reliable reading.", ev, issues, DateTime.Now);
    }

    // ---- restore verification -------------------------------------------------------------------------------------
    /// <summary>After Undo: which values differ from the backup taken before Apply. Empty = restored exactly.</summary>
    public static List<string> RestoreMismatches(Tweak t, TweakBackup? backup)
    {
        var diffs = new List<string>();
        for (int i = 0; i < t.Ops.Count; i++)
        {
            var op = t.Ops[i];
            var before = backup is not null && i < backup.Ops.Count ? backup.Ops[i] : null;
            try
            {
                switch (op)
                {
                    case CommandOp c when c.HasCheck:
                        if (c.Check() == OpState.Applied) diffs.Add($"{c.Describe()}: still reads as applied");
                        break;
                    case CommandOp:
                        break; // nothing to read back
                    case AppxRemoveOp a:
                        if (before?.Data.GetValueOrDefault("installed") == "True" && !AppxRemoveOp.InstalledNames().Contains(a.PackageName))
                            diffs.Add($"{a.PackageName}: not reinstalled");
                        break;
                    default:
                        if (before is null) { if (op.Check() == OpState.Applied) diffs.Add($"{op.Describe()}: still applied (no backup, Windows default expected)"); break; }
                        var now = op.Capture();
                        foreach (var (k, v) in before.Data)
                        {
                            if (k.EndsWith("keyExisted", StringComparison.Ordinal) && !(op is RegValueOp { RemoveKeyIfCreated: true } && v == "False")) continue;
                            if (k == "delayed" && v is null) continue;
                            if ((op is PowerCfgOp or ScheduledTaskOp) && v is null) continue;
                            var cur = now.Data.GetValueOrDefault(k);
                            if (!string.Equals(cur, v, StringComparison.OrdinalIgnoreCase))
                                diffs.Add($"{op.Describe()}: {k} is {cur ?? "(not set)"}, backup had {v ?? "(not set)"}");
                        }
                        break;
                }
            }
            catch (Exception ex) { diffs.Add($"{op.Describe()}: could not read back ({ex.Message})"); }
        }
        return diffs;
    }

    /// <summary>
    /// Live test of a tweak that is currently off: apply, read back, undo, and confirm every value is exactly what it
    /// was before. Changes the system briefly, so callers must confirm with the user and ensure admin rights first.
    /// </summary>
    public static VerifyReport RoundTrip(Tweak t)
    {
        var issues = TweakAudit.IssuesFor(t).ToList();
        var ev = new List<string>();
        VerifyReport R(VerifyStatus s, string sum) => new(t, s, TweakEngine.GetState(t), sum, ev, issues, DateTime.Now);
        if (!CanRoundTrip(t, out var why)) return R(VerifyStatus.Unverifiable, why);
        if (TweakEngine.GetState(t) != TweakState.NotApplied) return R(VerifyStatus.Unverifiable, "Round-trip only runs on tweaks that are off, so your current settings are never touched.");

        var snapshot = new TweakBackup { TweakId = t.Id, Ops = t.Ops.Select(o => o.Capture()).ToList() };
        var apply = TweakEngine.Apply(t);
        ev.Add($"Apply: {apply.Message}");
        if (!apply.Success)
        {
            var left = RestoreMismatches(t, snapshot);
            ev.AddRange(left.Select(l => "After automatic rollback: " + l));
            return R(VerifyStatus.Fail, left.Count == 0 ? "Apply failed and was rolled back; all values are back to their originals." : "Apply failed and rollback left differences.");
        }
        ev.AddRange(t.Ops.Select(Read).Select(r => "After apply: " + r.Text));
        var undo = TweakEngine.Undo(t);
        ev.Add($"Undo: {undo.Message}");
        var diffs = RestoreMismatches(t, snapshot);
        ev.AddRange(diffs.Select(d => "After undo: " + d));
        if (!undo.Success || diffs.Count > 0) return R(VerifyStatus.Fail, "Undo did not restore every original value.");
        var unverifiable = t.Ops.Any(o => !HasReadBack(o));
        return R(unverifiable ? VerifyStatus.Unverifiable : t.RequiresReboot ? VerifyStatus.Warning : VerifyStatus.Pass,
            unverifiable ? "Apply/undo ran, but a step has no read-back."
            : t.RequiresReboot ? "Written, read back and restored exactly. The effect itself needs a restart, so it wasn't observed live."
            : "Applied, read back, undone and every original value restored exactly.");
    }

    public static bool CanRoundTrip(Tweak t, out string reason)
    {
        reason = t.Kind == TweakKind.Guide ? "Guides are firmware steps; nothing to apply."
            : t.Category == TweakCategory.AppRemoval ? "Not run for app removal: reinstalling can need the Microsoft Store."
            : TweakAudit.DisabledReason(t) is { } d ? d
            : t.Ops.Any(o => o is CommandOp { HasCheck: false }) ? "Has a step with no read-back, so a round-trip can't prove anything."
            : "";
        return reason.Length == 0;
    }
}
