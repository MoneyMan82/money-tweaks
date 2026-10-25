using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Health;

/// <summary>One event as Windows logged it: positional data plus the named fields from its XML.</summary>
public sealed record RawEvent(DateTime Time, string Log, string Provider, int Id, int Level, IReadOnlyList<string> Data,
    IReadOnlyDictionary<string, string> Named, string? Message = null)
{
    public string D(int i) => i < Data.Count ? Data[i] : "";
    public string N(string name) => Named.TryGetValue(name, out var v) ? v : "";
}

/// <summary>A crash dump file on disk (user-mode dumps in CrashDumps, kernel minidumps).</summary>
public sealed record DumpFile(string Path, DateTime Time, long Bytes);

/// <summary>Something that changed on the PC: a driver, a Windows update, an installed program or a Money Tweaks tweak.</summary>
public sealed record PcChange(DateTime Time, string Kind, string Text, string? TweakId = null);

/// <summary>Everything Crash Detective read. Collected by <see cref="CrashDetective.Collect"/>, analysed by <see cref="CrashDetective.Analyze"/>.</summary>
public sealed record CrashInputs(IReadOnlyList<RawEvent> Events, IReadOnlyList<DumpFile> Dumps, IReadOnlyList<PcChange> Changes,
    double? StabilityNow, double? StabilityLowest, int? CrashDumpSetting, IReadOnlyList<string> SourcesRead, IReadOnlyList<string> Unavailable, int Days);

public enum CrashKind { BlueScreen, UnexpectedRestart, GpuDriverReset, HardwareError, AppCrash, AppHang, LiveKernelEvent }

/// <summary>How well the evidence supports a suspected cause. Every level is still a hypothesis.</summary>
public enum Confidence { Weak, Moderate, Strong }

/// <summary>A confirmed fact: read from Windows (which log/source and what it said).</summary>
public sealed record CrashFact(DateTime Time, string Source, string Text);

/// <summary>
/// A fix. <see cref="Action"/> is null for step-by-step guidance; otherwise "undo-tweak:id", "repair:id" (Health Center
/// safe repair) or "link:target" (a Windows tool or web page). Every action asks for confirmation first.
/// </summary>
public sealed record CrashFix(string Title, IReadOnlyList<string> Steps, string? Action = null, string? Reversibility = null);

public sealed record CrashHypothesis(string Cause, Confidence Confidence, IReadOnlyList<string> Evidence, string Caveat, IReadOnlyList<CrashFix> Fixes)
{
    public string ConfidenceLabel => Confidence switch { Confidence.Strong => "Strong lead", Confidence.Moderate => "Moderate lead", _ => "Weak lead" };
}

public sealed record CrashIncident(DateTime Time, CrashKind Kind, string Title, string? App, IReadOnlyList<CrashFact> Facts, IReadOnlyList<CrashHypothesis> Hypotheses)
{
    public string KindLabel => Kind switch
    {
        CrashKind.BlueScreen => "Blue screen", CrashKind.UnexpectedRestart => "Unexpected restart", CrashKind.GpuDriverReset => "GPU driver reset",
        CrashKind.HardwareError => "Hardware error", CrashKind.AppCrash => "App crash", CrashKind.AppHang => "App hang", _ => "Live kernel event",
    };
}

public sealed record CrashReport(DateTime At, int Days, IReadOnlyList<CrashIncident> Incidents, IReadOnlyList<PcChange> Changes, IReadOnlyList<string> Patterns,
    double? StabilityNow, double? StabilityLowest, IReadOnlyList<string> SourcesRead, IReadOnlyList<string> Unavailable, IReadOnlyList<string> Notes);

/// <summary>
/// Crash Detective: reads the System and Application event logs, Reliability Monitor and crash dump folders (all local,
/// read-only), groups events that happened together into incidents, and lists what is confirmed (the events themselves)
/// separately from suspected causes, each with its evidence, a confidence level and fixes. Causes come from fixed rules over
/// what Windows logged; when nothing points anywhere it says so. Builds on the Health Center's stability checks.
/// </summary>
public static class CrashDetective
{
    public const int DefaultDays = 30;

    // ---- collection (real system, read-only) -------------------------------------------------------------------------

    private static readonly string[] SystemProviders =
    [
        "Microsoft-Windows-WER-SystemErrorReporting", "Microsoft-Windows-Kernel-Power", "EventLog", "Display", "nvlddmkm", "amdkmdag", "amdkmdap",
        "Microsoft-Windows-WHEA-Logger", "disk", "Ntfs", "stornvme", "Microsoft-Windows-Resource-Exhaustion-Detector", "Microsoft-Windows-UserPnp",
        "Microsoft-Windows-WindowsUpdateClient",
    ];
    private static readonly string[] AppProviders = ["Application Error", "Application Hang", "Windows Error Reporting", ".NET Runtime"];

    /// <summary>Reads every source this PC offers. Sources that can't be read are listed, never guessed. Call off the UI thread.</summary>
    public static CrashInputs Collect(int days = DefaultDays)
    {
        var events = new List<RawEvent>();
        var read = new List<string>();
        var unavailable = new List<string>();
        foreach (var (log, providers) in new[] { ("System", SystemProviders), ("Application", AppProviders) })
        {
            try { events.AddRange(ReadLog(log, providers, days)); read.Add($"{log} event log ({string.Join(", ", providers.Select(Short))})"); }
            catch (Exception ex) { unavailable.Add($"{log} event log: {ex.Message}"); }
        }

        var changes = new List<PcChange>();
        double? stabNow = null, stabLow = null;
        try
        {
            var since = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now.AddDays(-days));
            using (var s = new ManagementObjectSearcher($"SELECT TimeGenerated, SourceName, EventIdentifier, ProductName FROM Win32_ReliabilityRecords WHERE TimeGenerated > '{since}'"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        string src = o["SourceName"]?.ToString() ?? "";
                        int id = Convert.ToInt32(o["EventIdentifier"] ?? 0, CultureInfo.InvariantCulture);
                        var when = ManagementDateTimeConverter.ToDateTime(o["TimeGenerated"]?.ToString() ?? "");
                        string product = o["ProductName"]?.ToString() ?? "";
                        if (src == "MsiInstaller" && id is 1033 or 11707) changes.Add(new(when, "Program installed", product));
                        else if (src == "MsiInstaller" && id is 1034 or 11724) changes.Add(new(when, "Program removed", product));
                    }
            var metrics = new List<(DateTime t, double v)>();
            using (var s = new ManagementObjectSearcher($"SELECT TimeGenerated, SystemStabilityIndex FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated > '{since}'"))
                foreach (ManagementObject o in s.Get())
                    using (o) metrics.Add((ManagementDateTimeConverter.ToDateTime(o["TimeGenerated"]?.ToString() ?? ""), Convert.ToDouble(o["SystemStabilityIndex"] ?? 0, CultureInfo.InvariantCulture)));
            if (metrics.Count > 0) { stabNow = metrics.MaxBy(m => m.t).v; stabLow = metrics.Min(m => m.v); }
            read.Add("Reliability Monitor (installs and the stability index)");
        }
        catch (Exception ex) { unavailable.Add("Reliability Monitor: " + ex.Message + " (its data collection task may be off)"); }

        var dumps = new List<DumpFile>();
        void Dumps(string dir, string label)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.dmp")) dumps.Add(new(f.FullName, f.LastWriteTime, f.Length));
                read.Add(label);
            }
            catch (UnauthorizedAccessException) { unavailable.Add(label + ": needs administrator rights to list"); }
            catch (Exception ex) { unavailable.Add(label + ": " + ex.Message); }
        }
        Dumps(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), "App crash dumps (%LOCALAPPDATA%\\CrashDumps)");
        Dumps(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"), "Blue screen minidumps (C:\\Windows\\Minidump)");

        int? dumpSetting = null;
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl"); dumpSetting = k?.GetValue("CrashDumpEnabled") as int?; }
        catch { /* not readable: no note about dump settings */ }

        foreach (var id in BackupStore.AllIds())
            if (BackupStore.Load(id) is { } b && TweakCatalog.Find(id) is { } t)
                changes.Add(new(b.AppliedAt, "Money Tweaks tweak applied", t.Name, id));
        return new(events, dumps, changes, stabNow, stabLow, dumpSetting, read, unavailable, days);
    }

    private static string Short(string provider) => provider.Replace("Microsoft-Windows-", "");

    private static IEnumerable<RawEvent> ReadLog(string log, string[] providers, int days)
    {
        string prov = string.Join(" or ", providers.Select(p => $"Provider[@Name='{p}']"));
        var q = new EventLogQuery(log, PathType.LogName, $"*[System[({prov}) and TimeCreated[timediff(@SystemTime) <= {(long)days * 86_400_000}]]]") { ReverseDirection = true };
        using var reader = new EventLogReader(q);
        int n = 0;
        for (EventRecord? e = reader.ReadEvent(); e is not null && n < 3000; e = reader.ReadEvent(), n++)
            using (e)
            {
                List<string> data;
                try { data = e.Properties.Select(p => p.Value?.ToString() ?? "").ToList(); } catch { data = []; }
                var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var x = XDocument.Parse(e.ToXml());
                    foreach (var d in x.Descendants().Where(d => d.Name.LocalName == "Data" && d.Attribute("Name") is not null))
                        named[d.Attribute("Name")!.Value] = d.Value;
                }
                catch { /* positional data still there */ }
                string? message = null;
                // The localised text says which hardware unit reported a WHEA error and which device a driver was installed for.
                if (e.ProviderName is "Microsoft-Windows-WHEA-Logger" or "Microsoft-Windows-UserPnp")
                    try { message = e.FormatDescription()?.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4).Aggregate((a, b) => a + " " + b); } catch { }
                yield return new(e.TimeCreated ?? DateTime.MinValue, log, e.ProviderName, e.Id, e.Level ?? 0, data, named, message);
            }
    }

    // ---- analysis (pure: tested with recorded events) ------------------------------------------------------------------

    /// <summary>Known module / driver names: what they belong to. Used only to name a component, never as proof.</summary>
    private static readonly (string pattern, string what, string area)[] KnownModules =
    [
        (@"^nvlddmkm", "the NVIDIA graphics driver", "gpu"), (@"^amdkmd", "the AMD graphics driver", "gpu"), (@"^igdkmd", "the Intel graphics driver", "gpu"),
        (@"^dxgkrnl|^dxgmms", "Windows' graphics kernel (used by every GPU driver)", "gpu"),
        (@"^nvwgf2umx|^nvoglv|^nvd3dum|^nvldumd|^nvgpucomp", "the NVIDIA graphics driver (user-mode part)", "gpu"),
        (@"^amdxx64|^atidxx64|^amdxc64|^aticfx64|^amdvlk", "the AMD graphics driver (user-mode part)", "gpu"),
        (@"^igd10iumd|^igd12umd|^igxelpicd|^igvk", "the Intel graphics driver (user-mode part)", "gpu"),
        (@"^ndis\.sys|^tcpip\.sys|^netio\.sys|^afd\.sys|^wfplwfs\.sys", "Windows' networking stack (it calls network adapter and filter drivers)", "net"),
        (@"^rt\d+.*\.sys|^rtwlan|^e1[a-z]\d+.*\.sys|^netwtw|^netadapter|^mtkwl|^athw|^qcamain|^rtux64", "a network adapter driver", "net"),
        (@"^storport\.sys|^stornvme\.sys|^storahci\.sys|^iastor|^nvme", "the storage driver", "disk"),
        (@"^ntoskrnl\.exe|^hal\.dll", "the Windows kernel (blamed by default when the real culprit isn't clear; often memory or another driver)", "kernel"),
        (@"^wdf01000\.sys", "Windows' driver framework (used by many drivers)", "kernel"),
        (@"^ntdll\.dll|^kernelbase\.dll|^ucrtbase\.dll|^msvcp|^vcruntime", "a Windows system library (where the error surfaced, rarely the cause)", "system"),
        (@"^gameoverlayrenderer", "the Steam overlay", "overlay"), (@"^discordhook", "the Discord overlay", "overlay"), (@"^rtsshooks", "RivaTuner Statistics Server (MSI Afterburner's overlay)", "overlay"),
        (@"^nvspcap|^nvcamera", "the NVIDIA overlay / ShadowPlay", "overlay"), (@"^igo(32|64)", "the EA/Origin overlay", "overlay"), (@"^owclient|^overwolf", "the Overwolf overlay", "overlay"),
        (@"^gameinput", "Microsoft GameInput", "system"),
    ];

    public static (string what, string area)? Identify(string module)
    {
        var m = module.Trim();
        foreach (var (p, what, area) in KnownModules)
            if (Regex.IsMatch(m, p, RegexOptions.IgnoreCase)) return (what, area);
        return null;
    }

    /// <summary>What an exception code means (Windows' documented NTSTATUS names).</summary>
    public static string ExceptionText(string code) => code.Trim().TrimStart('0', 'x').ToLowerInvariant() switch
    {
        "c0000005" => "0xc0000005 access violation (the program used memory it shouldn't)",
        "c0000409" => "0xc0000409 stack buffer overrun / fail-fast (the program stopped itself after detecting corruption)",
        "c0000374" => "0xc0000374 heap corruption",
        "c00000fd" => "0xc00000fd stack overflow",
        "c000001d" => "0xc000001d illegal instruction",
        "c0000096" => "0xc0000096 privileged instruction",
        "c06d007e" => "0xc06d007e a required module couldn't be loaded",
        "e0434352" => "0xe0434352 unhandled .NET exception",
        "80000003" => "0x80000003 breakpoint (the program stopped itself)",
        "c0000142" => "0xc0000142 the program couldn't initialise",
        var c when c.Length > 0 => "0x" + c,
        _ => "unknown exception",
    };

    private static readonly int[] GpuWatchdogCodes = [0x141, 0x117, 0x1a1, 0x1a8];

    /// <summary>Driver installs (UserPnp 20001/20003) and Windows updates (WindowsUpdateClient 19) from the System log.</summary>
    public static IEnumerable<PcChange> ChangesFromEvents(IEnumerable<RawEvent> events)
    {
        foreach (var e in events)
            if (e.Provider == "Microsoft-Windows-UserPnp" && e.Id == 20003)
                yield return new(e.Time, "Driver installed", $"{e.D(0)} ({Path.GetFileName(e.D(1))})");
            else if (e.Provider == "Microsoft-Windows-UserPnp" && e.Id == 20001)
                yield return new(e.Time, "Driver installed", e.Message is { Length: > 0 } m ? (m.Length > 160 ? m[..160] + "…" : m) : e.D(0));
            else if (e.Provider == "Microsoft-Windows-WindowsUpdateClient" && e.Id == 19)
                yield return new(e.Time, "Windows update installed", e.N("updateTitle") is { Length: > 0 } t ? t : e.D(0));
    }

    public static CrashReport Analyze(CrashInputs input, DateTime now)
    {
        var ev = input.Events.OrderBy(e => e.Time).ToList();
        input = input with { Changes = input.Changes.Concat(ChangesFromEvents(ev)).OrderBy(c => c.Time).ToList() };
        var incidents = new List<CrashIncident>();
        var used = new HashSet<RawEvent>();
        IEnumerable<RawEvent> Near(DateTime t, TimeSpan before, TimeSpan after, Func<RawEvent, bool> f) => ev.Where(e => e.Time >= t - before && e.Time <= t + after && f(e));
        string F(RawEvent e) => $"{(e.Log == "System" ? "System" : "Application")} log · {e.Provider} {e.Id}";

        // Blue screens: WER 1001 in the System log (stop code, parameters, dump), with 1019 (Windows' related driver),
        // Kernel-Power 41 and EventLog 6008 logged for the same restart.
        foreach (var b in ev.Where(e => e.Provider == "Microsoft-Windows-WER-SystemErrorReporting" && e.Id == 1001))
        {
            used.Add(b);
            var facts = new List<CrashFact>();
            var code = HealthChecks.ParseBugcheck(b.D(0));
            facts.Add(new(b.Time, F(b), $"Windows restarted after a stop error: {b.D(0)}" + (code is { } c ? $" = {HealthChecks.BugcheckName(c)}" : "")));
            if (b.D(1).Length > 0) facts.Add(new(b.Time, F(b), "Crash dump saved: " + b.D(1)));
            string? driver = null;
            foreach (var r in Near(b.Time, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), e => e.Provider == "Microsoft-Windows-WER-SystemErrorReporting" && e.Id == 1019))
            {
                used.Add(r); driver = r.D(0);
                facts.Add(new(r.Time, F(r), $"Windows names a possibly related driver: {driver}"));
            }
            foreach (var k in Near(b.Time, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), IsKernelPower41)) { used.Add(k); facts.Add(new(k.Time, F(k), "Kernel-Power 41: the restart wasn't clean (same crash)")); }
            foreach (var s in Near(b.Time, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), e => e.Provider == "EventLog" && e.Id == 6008)) { used.Add(s); facts.Add(new(s.Time, F(s), "EventLog 6008: the previous shutdown was unexpected (same crash)")); }
            incidents.Add(new(b.Time, CrashKind.BlueScreen, "Blue screen" + (code is { } cc ? $": {HealthChecks.BugcheckName(cc).Split(" (")[0]}" : ""), driver, facts, []));
        }

        // Kernel-Power 41 not explained by a blue screen record: a stop code here is still a blue screen; without one it's a
        // restart Windows can't explain (power button, power loss, freeze, reset).
        foreach (var k in ev.Where(e => IsKernelPower41(e) && !used.Contains(e)))
        {
            used.Add(k);
            var facts = new List<CrashFact>();
            uint code = uint.TryParse(k.N("BugcheckCode").Length > 0 ? k.N("BugcheckCode") : k.D(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bc) ? bc : 0;
            foreach (var s in Near(k.Time, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), e => e.Provider == "EventLog" && e.Id == 6008)) { used.Add(s); facts.Add(new(s.Time, F(s), $"EventLog 6008: the previous shutdown at {s.D(0)} was unexpected")); }
            if (code > 0)
            {
                facts.Insert(0, new(k.Time, F(k), $"Windows restarted after stop code 0x{code:X} = {HealthChecks.BugcheckName(code)} (no blue screen report was logged)"));
                incidents.Add(new(k.Time, CrashKind.BlueScreen, $"Blue screen: {HealthChecks.BugcheckName(code).Split(" (")[0]}", null, facts, []));
                continue;
            }
            facts.Insert(0, new(k.Time, F(k), "Kernel-Power 41: Windows started again without having shut down cleanly; no stop code was recorded"));
            if (k.N("LongPowerButtonPressDetected").Equals("true", StringComparison.OrdinalIgnoreCase)) facts.Add(new(k.Time, F(k), "A long press of the power button was detected"));
            else if (k.N("PowerButtonTimestamp") is { Length: > 0 } pb && pb != "0") facts.Add(new(k.Time, F(k), "The power button was pressed before the restart"));
            if (k.N("SleepInProgress") is { Length: > 0 } sl && sl != "0") facts.Add(new(k.Time, F(k), $"The PC was going to sleep or waking up (sleep state {sl})"));
            incidents.Add(new(k.Time, CrashKind.UnexpectedRestart, "Unexpected restart (no stop code)", null, facts, []));
        }

        // GPU driver resets (Display 4101), grouped when they come in bursts.
        foreach (var t in ev.Where(e => e.Provider == "Display" && e.Id == 4101))
        {
            if (used.Contains(t)) continue;
            var burst = Near(t.Time, TimeSpan.Zero, TimeSpan.FromMinutes(2), e => (e.Provider == "Display" && e.Id == 4101) || (e.Provider is "nvlddmkm" or "amdkmdag" or "amdkmdap" && e.Level is 1 or 2)).ToList();
            burst.ForEach(e => used.Add(e));
            var facts = burst.Select(e => new CrashFact(e.Time, F(e), e.Provider == "Display" ? $"The display driver{(e.D(0).Length > 0 ? " " + e.D(0) : "")} stopped responding and Windows recovered it" : $"{e.Provider} logged error {e.Id}")).ToList();
            incidents.Add(new(t.Time, CrashKind.GpuDriverReset, "GPU driver reset", null, facts, []));
        }

        // Live kernel events (Application log WER 1001, e.g. GPU watchdog 141/117) reported without a restart.
        foreach (var w in ev.Where(e => e.Provider == "Windows Error Reporting" && e.Id == 1001 && e.D(2).Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)))
        {
            used.Add(w);
            int.TryParse(w.D(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lk);
            bool gpu = GpuWatchdogCodes.Contains(lk);
            incidents.Add(new(w.Time, CrashKind.LiveKernelEvent, gpu ? "GPU stopped responding (live kernel event)" : "Live kernel event",
                null, [new(w.Time, F(w), $"Windows Error Reporting recorded a live kernel event, code {w.D(5)}{(gpu ? " (graphics watchdog: the GPU stopped responding and was reset)" : "")}")], []));
        }

        // Uncorrected hardware errors are incidents of their own; corrected ones are context for nearby incidents.
        foreach (var h in ev.Where(e => e.Provider == "Microsoft-Windows-WHEA-Logger" && e.Level is 1 or 2))
        {
            used.Add(h);
            incidents.Add(new(h.Time, CrashKind.HardwareError, "Uncorrected hardware error (WHEA)", null, [new(h.Time, F(h), h.Message ?? $"WHEA-Logger event {h.Id} (error)")], []));
        }

        // App crashes and hangs.
        foreach (var a in ev.Where(e => e.Provider == "Application Error" && e.Id == 1000))
        {
            used.Add(a);
            string app = a.D(0), module = a.D(3), exc = a.D(6);
            var facts = new List<CrashFact> { new(a.Time, F(a), $"{app} {a.D(1)} crashed in {module} {a.D(4)}: {ExceptionText(exc)}, offset {a.D(7)}") };
            foreach (var w in Near(a.Time, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), e => e.Provider == "Windows Error Reporting" && e.Id == 1001 && e.Data.Any(d => d.Equals(app, StringComparison.OrdinalIgnoreCase))))
            { used.Add(w); facts.Add(new(w.Time, F(w), $"Windows Error Reporting filed the crash ({w.D(2)})")); }
            foreach (var d in input.Dumps.Where(d => Path.GetFileName(d.Path).StartsWith(app + ".", StringComparison.OrdinalIgnoreCase) && Math.Abs((d.Time - a.Time).TotalMinutes) < 3))
                facts.Add(new(d.Time, "Crash dump", $"A crash dump was saved: {d.Path} ({d.Bytes / 1048576.0:0.#} MB)"));
            incidents.Add(new(a.Time, CrashKind.AppCrash, $"{app} crashed", app, facts, []));
        }
        foreach (var h in ev.Where(e => e.Provider == "Application Hang" && e.Id == 1002))
        {
            used.Add(h);
            incidents.Add(new(h.Time, CrashKind.AppHang, $"{h.D(0)} stopped responding", h.D(0), [new(h.Time, F(h), $"{h.D(0)} {h.D(1)} stopped responding and was closed")], []));
        }

        // Context: what else Windows logged around each incident (facts too, with their own times).
        var withContext = incidents.Select(i => i with { Facts = i.Facts.Concat(Context(i, ev, used, F)).OrderBy(f => f.Time).ToList() }).ToList();

        // Patterns across incidents and suspected causes per incident.
        var patterns = new List<string>();
        var result = new List<CrashIncident>();
        foreach (var i in withContext)
            result.Add(i with { Hypotheses = Hypotheses(i, withContext, input, patterns) });
        foreach (var g in result.Where(i => i.Kind == CrashKind.BlueScreen).GroupBy(i => (i.Title, i.App)).Where(g => g.Count() >= 2))
            patterns.Add($"{g.Count()} blue screens with the same stop code{(g.Key.App is { } d ? $" and the same driver named ({d})" : "")}: {string.Join(", ", g.Select(x => x.Time.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)))}");
        foreach (var g in result.Where(i => i.Kind == CrashKind.AppCrash).GroupBy(i => i.App ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3))
            patterns.Add($"{g.Key} crashed {g.Count()} times");
        if (result.Count(i => i.Kind == CrashKind.GpuDriverReset) is var gr and >= 2) patterns.Add($"The GPU driver was reset {gr} times");

        var notes = new List<string>();
        if (result.Count == 0) notes.Add($"No crashes, blue screens, GPU driver resets, hardware errors or app failures were logged in the last {input.Days} days.");
        if (input.CrashDumpSetting == 0 && result.Any(i => i.Kind is CrashKind.BlueScreen or CrashKind.UnexpectedRestart))
            notes.Add("Windows is set not to write crash dumps (CrashControl\\CrashDumpEnabled = 0), so blue screens leave less evidence. See the fix \"Turn crash dumps back on\".");
        notes.Add("Crash Detective reads what Windows recorded. Some crashes leave no trace (instant power loss, a game closing itself without reporting), and a suspected cause is only a lead until a fix confirms it.");
        return new(now, input.Days, result.OrderByDescending(i => i.Time).ToList(), input.Changes.Where(c => c.Time >= now.AddDays(-input.Days)).OrderByDescending(c => c.Time).ToList(),
            patterns, input.StabilityNow, input.StabilityLowest, input.SourcesRead, input.Unavailable, notes);
    }

    /// <summary>
    /// Changes that can plausibly affect this kind of failure: drivers and programs always; Windows updates except the daily
    /// Defender definition/platform updates; Money Tweaks tweaks that change the kernel, drivers, the network stack or power
    /// (Advanced, Networking, power-plan tweaks; for app crashes also game process tweaks). Privacy, debloat, service and
    /// interface tweaks are left out: they don't touch the paths these failures go through.
    /// </summary>
    private static bool Relevant(PcChange c, CrashIncident i)
    {
        if (c.Kind == "Windows update installed")
            return !Regex.IsMatch(c.Text, @"Defender Antivirus|Security Intelligence Update|KB2267602|KB4052623", RegexOptions.IgnoreCase)
                // Microsoft Store app updates ("9NRZT3Q9R3DL-…") run in user mode: no lead for kernel-level failures.
                && !(i.Kind is not (CrashKind.AppCrash or CrashKind.AppHang) && Regex.IsMatch(c.Text, @"^[0-9A-Z]{12}-"));
        if (c.TweakId is { } id)
        {
            var t = TweakCatalog.Find(id);
            if (t is null) return false;
            return i.Kind is CrashKind.AppCrash or CrashKind.AppHang
                ? t.Category is Model.TweakCategory.Advanced or Model.TweakCategory.GameProfiles or Model.TweakCategory.AppOptimizer
                : t.Category is Model.TweakCategory.Advanced or Model.TweakCategory.Networking || t.Group == "Power";
        }
        return true;
    }

    private static string Plural(string s) => s.EndsWith("sh", StringComparison.Ordinal) ? s + "es" : s + "s";

    private static bool IsKernelPower41(RawEvent e) => e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 41;

    private static IEnumerable<CrashFact> Context(CrashIncident i, List<RawEvent> ev, HashSet<RawEvent> used, Func<RawEvent, string> F)
    {
        var window = i.Kind is CrashKind.AppCrash or CrashKind.AppHang ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10);
        foreach (var e in ev.Where(e => e.Time >= i.Time - window && e.Time <= i.Time + TimeSpan.FromMinutes(1) && !i.Facts.Any(f => f.Time == e.Time && f.Source == F(e))))
        {
            if (e.Provider == "Microsoft-Windows-WHEA-Logger") yield return new(e.Time, F(e), (e.Level is 1 or 2 ? "Uncorrected" : "Corrected") + " hardware error nearby: " + (e.Message ?? $"event {e.Id}"));
            else if (e.Provider == "Display" && e.Id == 4101 && i.Kind != CrashKind.GpuDriverReset) yield return new(e.Time, F(e), "The GPU driver was reset nearby");
            else if (e.Provider is "disk" or "Ntfs" or "stornvme" && e.Level is 1 or 2) yield return new(e.Time, F(e), $"Disk error nearby ({e.Provider} {e.Id})");
            else if (e.Provider == "Microsoft-Windows-Resource-Exhaustion-Detector" && e.Id == 2004) yield return new(e.Time, F(e), "Windows warned that memory was running low");
        }
    }

    private static readonly CrashFix NetDriverFix = new("Update or replace the network driver",
    [
        "Open Device Manager › Network adapters and note your adapters (e.g. Realtek, Intel, Killer, Wi-Fi).",
        "Install the newest driver from your PC or motherboard maker's support page, or from the chip maker (Realtek, Intel, MediaTek).",
        "If the crashes started after installing network software (VPN, virtual machines, packet capture, firewalls), uninstall it for a few days to test.",
    ], "link:devmgmt.msc", "Reversible: Device Manager › adapter › Driver › Roll Back Driver restores the previous one.");
    private static readonly CrashFix GpuDriverFix = new("Reinstall the graphics driver cleanly",
    [
        "Download the current driver for your card from the official site (NVIDIA, AMD or Intel).",
        "Run its installer and choose the clean install / factory reset option.",
        "If the problem started with a new driver, install the previous version instead.",
    ], "link:devmgmt.msc", "Reversible: Device Manager › display adapter › Driver › Roll Back Driver, or install any earlier version.");
    private static readonly CrashFix StockClocksFix = new("Test at stock clocks",
    [
        "Reset any GPU overclock or undervolt (Money Tweaks › CPU & GPU Tuning, MSI Afterburner, or the GPU maker's tool).",
        "In the BIOS, test with CPU overclocks, PBO/undervolt curves and XMP/EXPO turned off for a few days.",
        "If the crashes stop, bring the settings back one at a time.",
    ], null, "Reversible: re-enable each setting afterwards.");
    private static readonly CrashFix MemoryTestFix = new("Test the memory",
    [
        "Save your work, then start Windows Memory Diagnostic; it restarts the PC and tests the RAM.",
        "If you use XMP/EXPO, also test with it turned off in the BIOS.",
    ], "link:mdsched.exe", "Nothing changes until you choose to restart in the tool.");
    private static readonly CrashFix SystemFilesFix = new("Check Windows system files", ["Run the safe repair \"Repair system files (sfc /scannow)\" from the Health Center."], "repair:" + SafeRepairs.SfcScanNow,
        "Not reversible by design (only damaged Windows files are replaced with Microsoft's originals); a restore point is made first when possible.");
    private static readonly CrashFix DumpsFix = new("Turn crash dumps back on",
    [
        "Open System Properties › Advanced › Startup and Recovery › Settings.",
        "Under \"Write debugging information\", choose \"Small memory dump\" or \"Automatic memory dump\".",
    ], "link:SystemPropertiesAdvanced.exe", "Reversible: set it back the same way.");
    private static readonly CrashFix PowerFix = new("Check power and heat",
    [
        "Check the power cable, wall socket or power strip, and that the PSU's cables are fully seated (GPU power connectors especially).",
        "Watch CPU and GPU temperatures in Money Tweaks' Hardware Monitor while gaming.",
        "If the PC switches off under load, the power supply may be too weak or failing.",
    ]);
    private static readonly CrashFix GameFilesFix = new("Update or repair the program",
    [
        "Install the program's latest version or update.",
        "For a game, verify its files: Steam › right-click the game › Properties › Installed Files › Verify integrity of game files; Epic Games › ⋯ on the game › Manage › Verify.",
        "Remove mods or add-ons for it while testing, or reinstall it.",
    ], null, "Verifying and updating only replace the program's own files.");

    private static CrashFix OverlayFix(string what) => new($"Turn off {what} for this game",
    [
        $"Disable {what} (in its own settings, or just for this game) and play again.",
        "If the crashes stop, update that program or keep its overlay off for this game.",
    ], null, "Reversible: turn it back on in the same place.");

    private static List<CrashHypothesis> Hypotheses(CrashIncident i, List<CrashIncident> all, CrashInputs input, List<string> patterns)
    {
        var list = new List<CrashHypothesis>();
        var facts = string.Join(" ", i.Facts.Select(f => f.Text));
        bool wheaNear = i.Facts.Any(f => f.Text.Contains("hardware error", StringComparison.OrdinalIgnoreCase));
        bool tdrNear = i.Facts.Any(f => f.Text.Contains("GPU driver was reset", StringComparison.OrdinalIgnoreCase));

        switch (i.Kind)
        {
            case CrashKind.BlueScreen:
            {
                var same = all.Where(o => o.Kind == CrashKind.BlueScreen && o.Title == i.Title && o.App == i.App).ToList();
                string repeat = same.Count >= 2 ? $"The same stop code{(i.App is null ? "" : " and driver")} appeared {same.Count} times ({string.Join(", ", same.Select(s => s.Time.ToString("d MMM", CultureInfo.InvariantCulture)))})" : "";
                if (i.App is { Length: > 0 } driver)
                {
                    var id = Identify(driver);
                    var ev = new List<string> { $"Windows' own crash report names {driver} as a possibly related driver" };
                    if (repeat.Length > 0) ev.Add(repeat);
                    string cause = id is { } k ? $"A fault involving {k.what} ({driver})" : $"The driver {driver}";
                    var fixes = id?.area switch
                    {
                        "net" => new List<CrashFix> { NetDriverFix },
                        "gpu" => [GpuDriverFix, StockClocksFix],
                        "disk" => [new("Check the drive and its driver", ["Check the drive's health in the Health Center (Disks).", "Update the storage/chipset driver from your motherboard maker."], "link:devmgmt.msc")],
                        "kernel" => [MemoryTestFix, StockClocksFix, SystemFilesFix],
                        _ => [new($"Update or remove what installed {driver}", [$"Find which program or device installed {driver} (its file is in C:\\Windows\\System32\\drivers).", "Update it from the maker, or uninstall it to test."], "link:devmgmt.msc", "Reversible: reinstall it afterwards.")],
                    };
                    list.Add(new(cause, same.Count >= 2 ? Confidence.Moderate : Confidence.Weak, ev,
                        id?.area == "net" ? "ndis.sys, tcpip.sys and similar files are part of Windows; when they're named, the fault usually comes from a network adapter or network filter driver (VPN, virtualisation, capture or security software) that calls into them."
                        : "Windows names the driver that was running when the crash happened. That's often the culprit, but a driver can also be the victim of bad memory or another driver.", fixes));
                }
                var code = HealthChecks.ParseBugcheck(Regex.Match(facts, @"0x[0-9a-fA-F]+").Value) ?? 0;
                if (code == 0x124 || (wheaNear && code > 0))
                    list.Add(new("Hardware instability (CPU, RAM, PCIe device, power or heat)", code == 0x124 && wheaNear ? Confidence.Strong : Confidence.Moderate,
                        [.. code == 0x124 ? new[] { "Stop code 0x124 WHEA_UNCORRECTABLE_ERROR is raised by the hardware itself" } : [], .. wheaNear ? new[] { "A hardware error (WHEA) was logged right before the crash" } : []],
                        "WHEA errors come from the hardware, but an unstable overclock or undervolt makes healthy hardware report them too.", [StockClocksFix, MemoryTestFix, PowerFix]));
                else if (code is 0x1A or 0x50 or 0xA or 0x19 or 0x139 or 0x154 && i.App is null)
                    list.Add(new("Memory or a driver corrupting memory", repeat.Length > 0 ? Confidence.Moderate : Confidence.Weak, [$"Stop code {HealthChecks.BugcheckName(code)}", .. repeat.Length > 0 ? new[] { repeat } : []],
                        "These stop codes are shared by faulty RAM, unstable XMP/EXPO and buggy drivers; the event logs can't tell them apart.", [MemoryTestFix, StockClocksFix]));
                else if (code is 0x116 or 0x117)
                    list.Add(new("The graphics driver or GPU stopped responding", Confidence.Moderate, [$"Stop code {HealthChecks.BugcheckName(code)}"], "Also caused by GPU overclocks/undervolts, power delivery or overheating.", [GpuDriverFix, StockClocksFix, PowerFix]));
                else if (code is 0x9F)
                    list.Add(new("A driver failed during sleep, wake or a power change", repeat.Length > 0 ? Confidence.Moderate : Confidence.Weak, [$"Stop code {HealthChecks.BugcheckName(code)}"],
                        "Without the dump analysed, the driver isn't known; USB, network and GPU drivers are common.", [new("Update drivers and test without sleep", ["Update chipset, USB, network and graphics drivers from the makers.", "If it happens on wake, test for a few days with sleep turned off (Settings › System › Power)."], "link:devmgmt.msc")]));
                else if (code is 0xEF or 0x3B)
                    list.Add(new("Damaged system files or a faulty driver", Confidence.Weak, [$"Stop code {HealthChecks.BugcheckName(code)}"], "The log alone can't separate the two.", [SystemFilesFix, GpuDriverFix]));
                else if (code is 0x101)
                    list.Add(new("CPU instability (overclock, undervolt or power)", Confidence.Moderate, ["Stop code 0x101 CLOCK_WATCHDOG_TIMEOUT: a CPU core stopped responding"], "Rarely a BIOS bug; update the BIOS if stock settings don't help.", [StockClocksFix, PowerFix]));
                break;
            }
            case CrashKind.UnexpectedRestart:
                if (facts.Contains("long press of the power button", StringComparison.OrdinalIgnoreCase) || facts.Contains("power button was pressed", StringComparison.OrdinalIgnoreCase))
                    list.Add(new("The PC was switched off with the power button (possibly because it froze)", Confidence.Moderate, ["Kernel-Power recorded a power button press before the restart"],
                        "If you pressed it because the PC froze, the freeze is the real problem; Windows recorded no stop code, so a hard hang (driver, GPU, power) is possible.", [StockClocksFix, PowerFix]));
                else
                    list.Add(new("Power loss, a power supply problem or a hard freeze followed by a reset", Confidence.Weak, ["No stop code, no power button press and no crash report were recorded"],
                        "Windows can't tell these apart: a power cut, a tripped PSU, the reset button and a total freeze look the same in the log.", [PowerFix, StockClocksFix, DumpsFix]));
                if (wheaNear) list.Add(new("Hardware instability", Confidence.Moderate, ["A hardware error (WHEA) was logged shortly before"], "See the hardware error for which part reported it.", [StockClocksFix, MemoryTestFix]));
                break;
            case CrashKind.GpuDriverReset or CrashKind.LiveKernelEvent:
            {
                int n = all.Count(o => o.Kind is CrashKind.GpuDriverReset or CrashKind.LiveKernelEvent);
                list.Add(new("The graphics driver or GPU stopped responding (driver, overclock/undervolt, power or heat)", n >= 3 ? Confidence.Moderate : Confidence.Weak,
                    [$"{n} GPU driver reset(s) or graphics watchdog event(s) in {input.Days} days"],
                    "Windows only records that the GPU stopped responding, not why. Overclocks, undervolts, power connectors and heat cause the same event as driver bugs.", [StockClocksFix, GpuDriverFix, PowerFix]));
                break;
            }
            case CrashKind.HardwareError:
                list.Add(new("Hardware instability or a failing component", Confidence.Moderate, ["The hardware itself reported an uncorrected error (WHEA)"],
                    "The event text names the unit (processor core, memory, PCIe device). An overclock or undervolt can trigger it on healthy hardware.", [StockClocksFix, MemoryTestFix, PowerFix]));
                break;
            case CrashKind.AppCrash:
            {
                // The Application Error record itself (facts are in time order, so context can come first).
                var crash = i.Facts.FirstOrDefault(f => f.Source.EndsWith("Application Error 1000", StringComparison.Ordinal))?.Text ?? "";
                var m = Regex.Match(crash, @" crashed in (\S+)");
                string module = m.Success ? m.Groups[1].Value : "";
                var id = Identify(module);
                int sameApp = all.Count(o => o.Kind == CrashKind.AppCrash && string.Equals(o.App, i.App, StringComparison.OrdinalIgnoreCase));
                string again = sameApp >= 3 ? $"{i.App} crashed {sameApp} times in {input.Days} days" : "";
                if (id?.area == "gpu")
                    list.Add(new($"The graphics driver ({module})", tdrNear ? Confidence.Moderate : Confidence.Weak,
                        [$"The crash happened inside {id.Value.what}", .. tdrNear ? new[] { "The GPU driver was reset at the same time" } : [], .. again.Length > 0 ? new[] { again } : []],
                        "The fault surfaced in the driver, but the game, an overlay or an unstable GPU overclock can cause that too.", [GpuDriverFix, StockClocksFix]));
                else if (id?.area == "overlay")
                    list.Add(new($"{char.ToUpperInvariant(id.Value.what[0])}{id.Value.what[1..]}", sameApp >= 2 ? Confidence.Moderate : Confidence.Weak,
                        [$"The crash happened inside {module}, which belongs to {id.Value.what}", .. again.Length > 0 ? new[] { again } : []],
                        "Overlays inject into the game; a crash in their module is a good lead but not proof.", [OverlayFix(id.Value.what)]));
                else if (id?.area == "system")
                    list.Add(new($"A problem inside {i.App} itself (the error surfaced in a Windows library)", Confidence.Weak,
                        [$"The crash surfaced in {module}, a Windows library programs call into", ExceptionText(Regex.Match(crash, @": (\S+)").Groups[1].Value)],
                        "Errors in ntdll/KernelBase usually start in the program that called them; the log can't show which part.", [GameFilesFix]));
                else if (module.Length > 0)
                    list.Add(new($"A fault in {i.App}'s own code or one of its files ({module})", again.Length > 0 ? Confidence.Moderate : Confidence.Weak,
                        [$"The crash happened in {module}", .. again.Length > 0 ? new[] { again } : []],
                        "A game bug, a damaged file, a mod or an outdated version can all crash there; updating or verifying the game is the first step.", [GameFilesFix]));
                if (Regex.IsMatch(crash, @"c000001d|c0000096", RegexOptions.IgnoreCase))
                    list.Add(new("CPU instability", Confidence.Weak, ["Illegal/privileged instruction exceptions can come from an unstable CPU"], "Also caused by programs built for newer CPUs.", [StockClocksFix]));
                if (i.Facts.Any(f => f.Text.Contains("memory was running low", StringComparison.Ordinal)))
                    list.Add(new("Running out of memory", Confidence.Moderate, ["Windows warned that memory was running low just before the crash"], "Close background apps and let Windows manage the page file.",
                        [new("Free up memory", ["Close browsers and other heavy apps while gaming.", "System Properties › Advanced › Performance › Settings › Advanced › Virtual memory: let Windows manage the page file size."], "link:SystemPropertiesAdvanced.exe", "Reversible: set the page file back the same way.")]));
                break;
            }
            case CrashKind.AppHang:
                list.Add(new($"{i.App} stopped responding", Confidence.Weak, ["Windows closed the program after it stopped responding"],
                    "Hangs leave little evidence: the game may have been loading, waiting on the disk or network, or stuck in a bug.", [GameFilesFix]));
                break;
        }

        // Changes shortly before the first incident of a repeating problem: a lead, never a conclusion. One-off crashes get
        // no timing leads (coincidences), and only changes that can touch this kind of failure are considered.
        var group = all.Where(o => o.Kind == i.Kind && (o.App == i.App || o.Kind is CrashKind.UnexpectedRestart or CrashKind.GpuDriverReset)).ToList();
        if (group.Count >= 2 && group.MinBy(o => o.Time) == i)
        {
            var leads = input.Changes.Where(c => c.Time <= i.Time && c.Time >= i.Time.AddHours(-72) && Relevant(c, i)).OrderByDescending(c => c.Time)
                .GroupBy(c => (c.Kind, Minute: new DateTime(c.Time.Year, c.Time.Month, c.Time.Day, c.Time.Hour, c.Time.Minute, 0))).Take(3);
            foreach (var g in leads)
            {
                var items = g.DistinctBy(c => c.Text).ToList();
                var c = items[0];
                string what = items.Count == 1 ? $"\"{c.Text}\"" : $"{items.Count} at once: {string.Join(", ", items.Take(4).Select(x => $"\"{x.Text}\""))}";
                var fixes = items.Where(x => x.TweakId is not null).Select(x => new CrashFix($"Undo \"{x.Text}\"",
                    ["Undo restores the values Money Tweaks backed up when it was applied and checks them.", "Use the PC normally for a few days to see whether the problem returns."], "undo-tweak:" + x.TweakId, "Reversible: apply the tweak again any time.")).ToList();
                if (c.Kind == "Driver installed") fixes.Add(new("Roll back that driver", ["Device Manager › the device › Properties › Driver › Roll Back Driver."], "link:devmgmt.msc", "Reversible: update the driver again later."));
                else if (c.TweakId is null) fixes.Add(new("Test without it", ["If you don't need it, uninstall it (Settings › Apps) for a few days and see whether the problem returns."], null, "Reversible: install it again."));
                list.Add(new($"Changed shortly before it started: {c.Kind.ToLowerInvariant()} {what}", Confidence.Weak,
                    [$"{c.Kind} on {c.Time:d MMM HH:mm}, {(i.Time - c.Time).TotalHours:0} h before the first of {group.Count} {Plural(i.KindLabel.ToLowerInvariant())} of this kind"],
                    "Timing alone doesn't prove a connection; it's listed because it's easy to test by undoing it.", fixes));
            }
        }

        if (list.Count == 0)
            list.Add(new("No clear lead in the logs", Confidence.Weak, ["Nothing Windows recorded points to a specific cause"],
                "Not every crash can be diagnosed from logs. If it keeps happening, the crash dump (if any) can be analysed with WinDbg, or the game's own crash logs may say more.", []));
        return list;
    }

    // ---- export ------------------------------------------------------------------------------------------------------

    public static string ToText(CrashReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Money Tweaks Crash Detective report · {r.At:yyyy-MM-dd HH:mm} · last {r.Days} days");
        if (r.StabilityNow is { } s) sb.AppendLine($"Reliability Monitor stability index: {s:0.0} / 10 now, lowest {r.StabilityLowest:0.0}");
        sb.AppendLine("Sources read: " + string.Join("; ", r.SourcesRead));
        if (r.Unavailable.Count > 0) sb.AppendLine("Not available: " + string.Join("; ", r.Unavailable));
        foreach (var p in r.Patterns) sb.AppendLine("Pattern: " + p);
        foreach (var n in r.Notes) sb.AppendLine("Note: " + n);
        foreach (var i in r.Incidents)
        {
            sb.AppendLine().AppendLine($"== {i.Time:yyyy-MM-dd HH:mm:ss} · {i.KindLabel} · {i.Title}");
            sb.AppendLine("Confirmed (from Windows):");
            foreach (var f in i.Facts) sb.AppendLine($"  {f.Time:HH:mm:ss} {f.Text}  [{f.Source}]");
            sb.AppendLine("Suspected causes (hypotheses):");
            foreach (var h in i.Hypotheses)
            {
                sb.AppendLine($"  {h.ConfidenceLabel}: {h.Cause}");
                foreach (var e in h.Evidence) sb.AppendLine($"    evidence: {e}");
                sb.AppendLine($"    caveat: {h.Caveat}");
                foreach (var fx in h.Fixes) sb.AppendLine($"    fix: {fx.Title}");
            }
        }
        if (r.Changes.Count > 0)
        {
            sb.AppendLine().AppendLine("Changes in this period:");
            foreach (var c in r.Changes) sb.AppendLine($"  {c.Time:yyyy-MM-dd HH:mm} {c.Kind}: {c.Text}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Removes personal details before a report leaves the PC: user and computer names, profile paths, account SIDs,
    /// e-mail, IP and MAC addresses, report GUIDs and device serial numbers.
    /// </summary>
    public static string Redact(string text, string? userName = null, string? machineName = null, string? profile = null)
    {
        userName ??= Environment.UserName; machineName ??= Environment.MachineName;
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"S-1-5-21(-\d+){3,4}", "S-1-5-21-[account]");
        text = Regex.Replace(text, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "[e-mail]");
        // Dotted quads after a file name or "Version" are version numbers (10.0.26100.1), not addresses.
        text = Regex.Replace(text, @"(?<!\.(?:exe|dll|sys) )(?<![Vv]ersion )(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", "[ip]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "[mac]");
        text = Regex.Replace(text, @"\b(?:[0-9a-fA-F]{1,4}:){4,7}[0-9a-fA-F]{1,4}\b", "[ip]");
        text = Regex.Replace(text, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "[id]");
        text = Regex.Replace(text, @"((?:USB|PCI|HID|SWD|BTHENUM|USBSTOR)\\[^\\\s]+\\)[^\s;,)]+", "$1[serial]", RegexOptions.IgnoreCase);
        foreach (var name in new[] { userName, machineName }.Where(n => n is { Length: >= 3 }))
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(name!)}(?![A-Za-z0-9])", "[redacted]", RegexOptions.IgnoreCase);
        return text;
    }
}
