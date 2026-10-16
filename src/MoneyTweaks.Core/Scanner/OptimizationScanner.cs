using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Scanner;

public enum Severity { Info, Low, Medium, High }
public enum Confidence { Low, Medium, High }

/// <summary>One scan result. Evidence is the real data read from this PC; FixTweakIds are catalog tweaks (backed up, undoable).</summary>
public sealed record Finding(string Id, string Title, Severity Severity, string Area, IReadOnlyList<string> Evidence,
    string Why, string Benefit, Confidence Confidence, IReadOnlyList<string> FixTweakIds, string? Manual = null);

public sealed record NetAdapterInfo(string Name, string Type, long SpeedBps);

/// <summary>Everything the rules look at. Collected read-only from Windows; tests build it by hand.</summary>
public sealed class ScanFacts
{
    public string PowerPlanGuid { get; set; } = "";
    public string PowerPlanName { get; set; } = "";
    public bool? OnAc { get; set; }
    public bool HasBattery { get; set; }
    public string Cpu { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public string Gpus { get; set; } = "";
    public double RamGb { get; set; }
    public int? RamRatedMts { get; set; }
    public int? RamConfiguredMts { get; set; }
    public double RamUsedPercent { get; set; }
    public List<(string Name, double Mb)> TopMemory { get; set; } = [];
    public List<(string Name, double CpuPercent)> BusyProcesses { get; set; } = [];
    public List<string> StartupEnabled { get; set; } = [];
    public List<NetAdapterInfo> Adapters { get; set; } = [];
    public double? SystemFreeGb { get; set; }
    public double? SystemFreePercent { get; set; }
    public int Build { get; set; }
    public bool HagsOn { get; set; }
    public Dictionary<string, TweakState> States { get; set; } = [];
    public HashSet<string> Backups { get; set; } = [];
    public List<string> RebootPending { get; set; } = [];
}

public static class OptimizationScanner
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    /// <summary>Tweaks the scanner may suggest. Services and security settings are deliberately not in this list.</summary>
    public static readonly string[] Candidates =
        ["gen.game-mode", "gen.background-recording", "gen.mouse-accel", "gen.usb-suspend", "gen.ultimate-plan", "opt.launcher-autostart",
         "net.adapter-power", "gen.visualfx-performance", "gen.transparency", "adv.hags"];

    // ---- collection (read-only) -----------------------------------------------------------------------------------
    public static ScanFacts Collect(IProgress<string>? progress = null)
    {
        var f = new ScanFacts { Build = OsInfo.Build };
        progress?.Report("Reading hardware…");
        var specs = HardwareMonitor.ReadSpecs();
        (f.Cpu, f.Cores, f.Threads, f.Gpus, f.RamGb, f.RamRatedMts, f.RamConfiguredMts) = (specs.Cpu, specs.Cores, specs.Threads, specs.Gpus, specs.RamGb, specs.RamRatedMts, specs.RamConfiguredMts);
        var (total, avail, _) = MemoryInfo.Status();
        f.RamUsedPercent = total == 0 ? 0 : Math.Round(100.0 * (total - avail) / total, 1);

        progress?.Report("Reading power plan…");
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/getactivescheme");
        var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s*\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) { f.PowerPlanGuid = m.Groups[1].Value.ToLowerInvariant(); f.PowerPlanName = m.Groups[2].Value.Trim(); }
        f.OnAc = BenchmarkSampler.OnAcPower();
        f.HasBattery = HasBattery();

        progress?.Report("Measuring background processes (1 s)…");
        (f.TopMemory, f.BusyProcesses) = Processes();

        progress?.Report("Reading startup apps…");
        try { f.StartupEnabled = StartupManager.List().Where(e => e.Enabled).Select(e => e.Name).ToList(); } catch (Exception ex) { Log.Debug("scan startup: " + ex.Message); }

        progress?.Report("Reading network adapters…");
        try
        {
            f.Adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
                .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                .Where(n => !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                .Select(n => new NetAdapterInfo(n.Description, n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet", n.Speed)).ToList();
        }
        catch (Exception ex) { Log.Debug("scan network: " + ex.Message); }

        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            f.SystemFreeGb = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1);
            f.SystemFreePercent = Math.Round(100.0 * d.AvailableFreeSpace / d.TotalSize, 1);
        }
        catch { }
        f.HagsOn = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", null) is int h && h == 2;

        progress?.Report("Checking tweak states…");
        var cands = Candidates.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => t!).ToList();
        TweakEngine.PrewarmChecks(cands);
        foreach (var t in cands) f.States[t.Id] = TweakEngine.GetState(t);
        f.Backups = BackupStore.AllIds().ToHashSet();
        foreach (var id in f.Backups)
            if (TweakCatalog.Find(id) is { Kind: TweakKind.Toggle } t)
            {
                if (!f.States.ContainsKey(id)) f.States[id] = TweakEngine.GetState(t);
                if (t.RequiresReboot && BackupStore.Load(id)?.AppliedAt > TweakVerifier.BootTime) f.RebootPending.Add(t.Name);
            }
        return f;
    }

    private static bool HasBattery()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            return s.Get().Count > 0;
        }
        catch { return false; }
    }

    // Internal: the Health Center reuses this 1 s CPU/memory sample.
    internal static (List<(string, double)> mem, List<(string, double)> cpu) Processes()
    {
        var self = Environment.ProcessId;
        var first = new Dictionary<int, (string name, TimeSpan cpu, long ws)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4 || p.Id == self) continue;
                try { first[p.Id] = (p.ProcessName, p.TotalProcessorTime, p.WorkingSet64); } catch { /* protected process */ }
            }
        }
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1000);
        var cpu = new List<(string, double)>();
        foreach (var (id, (name, t0, _)) in first)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                var pct = (p.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                if (pct >= 1) cpu.Add((name, Math.Round(pct, 1)));
            }
            catch { }
        }
        // Group by name: browsers and launchers run many processes.
        var mem = first.Values.GroupBy(v => v.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, Math.Round(g.Sum(v => v.ws) / 1048576.0))).OrderByDescending(x => x.Item2).Take(6).ToList();
        var busy = cpu.GroupBy(c => c.Item1, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, Math.Round(g.Sum(x => x.Item2), 1)))
            .OrderByDescending(x => x.Item2).Take(6).ToList();
        return (mem, busy);
    }

    // ---- rules (pure) ---------------------------------------------------------------------------------------------
    public static List<Finding> Analyze(ScanFacts f)
    {
        var list = new List<Finding>();
        bool Off(string id) => f.States.TryGetValue(id, out var s) && s is TweakState.NotApplied or TweakState.Partial;
        bool weakPc = f.Threads is > 0 and <= 4 || f.RamGb is > 0 and <= 8;
        bool laptop = f.HasBattery;
        string Inv(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        // Tweaks Money Tweaks applied that Windows or another tool reverted.
        var reverted = f.Backups.Where(id => f.States.TryGetValue(id, out var s) && s == TweakState.NotApplied && TweakCatalog.Find(id) is { } t && TweakAudit.DisabledReason(t) is null).ToList();
        if (reverted.Count > 0)
            list.Add(new("reverted", $"{reverted.Count} applied tweak(s) were changed back", Severity.Medium, "Tweaks",
                reverted.Select(id => $"{TweakCatalog.Find(id)!.Name}: backup exists, but Windows reads the original value").ToList(),
                "Money Tweaks applied these, but Windows Update, a policy, a settings page or another tool changed them back.",
                "Restores the settings you chose earlier. No new kind of change.", Confidence.High, reverted));

        // RAM speed (XMP/EXPO). Only flagged when the configured speed is clearly below the module's reported speed.
        if (f.RamRatedMts is { } rated && f.RamConfiguredMts is { } conf && conf < rated)
            list.Add(new("ram-xmp", "Memory runs below its reported speed", Severity.High, "Memory",
                [$"Configured {conf} MT/s, module reports {rated} MT/s (Win32_PhysicalMemory)"],
                "Memory is running slower than the modules report, which usually means the XMP/EXPO profile is off in the BIOS.",
                "Faster memory often helps CPU-bound games and 1% lows. The size of the gain depends on the CPU and game.", Confidence.Medium, [],
                "Enable XMP/EXPO in the BIOS. See BIOS → 'Enable XMP / EXPO memory profile' for steps."));

        // Power plan.
        if (f.PowerPlanGuid == PowerSaver && f.OnAc != false)
            list.Add(new("power-saver", "Power saver plan is active", Severity.Medium, "Power",
                [$"Active plan: {f.PowerPlanName} ({f.PowerPlanGuid})", f.OnAc == true ? "Running on mains power" : "Power source unknown"],
                "Power saver caps CPU performance to save energy.", "Higher sustained CPU clocks while plugged in. More power use and heat.", Confidence.High, [],
                "Switch to Balanced or High performance: Control Panel → Power Options."));
        else if (f.PowerPlanGuid == Balanced && !laptop && Off("gen.ultimate-plan"))
            list.Add(new("power-balanced", "Desktop is on the Balanced power plan", Severity.Low, "Power",
                [$"Active plan: {f.PowerPlanName}", "No battery detected (desktop)"],
                "Balanced lets the CPU drop to low clocks between bursts. On many modern CPUs the difference in games is small.",
                "Can reduce clock ramp-up stutter on some systems. Not guaranteed to change FPS. Uses more power at idle.", Confidence.Low, ["gen.ultimate-plan"]));
        else if (laptop && f.OnAc == false)
            list.Add(new("on-battery", "Laptop is running on battery", Severity.Info, "Power",
                [$"Active plan: {f.PowerPlanName}", "On battery"],
                "Most laptops limit CPU and GPU power on battery regardless of Windows settings.", "Plug in for gaming and benchmarks.", Confidence.High, []));

        // RAM pressure and memory-heavy apps.
        if (f.RamUsedPercent >= 85)
            list.Add(new("ram-pressure", "Memory is nearly full", f.RamUsedPercent >= 92 ? Severity.High : Severity.Medium, "Memory",
                [$"{Inv(f.RamUsedPercent)}% of {Inv(f.RamGb)} GB in use", .. f.TopMemory.Select(x => $"{x.Name}: {x.Mb:0} MB")],
                "When RAM runs out, Windows pages to disk, which causes stutter.",
                "Closing the largest apps before playing frees memory. Money Tweaks won't close apps for you.", Confidence.High, [],
                "Close the largest apps listed (browsers with many tabs are common), or use Game Booster before playing."));

        // Busy background processes.
        var busy = f.BusyProcesses.Where(p => p.CpuPercent >= 5 && !p.Name.Contains("MoneyTweaks", StringComparison.OrdinalIgnoreCase)).ToList();
        if (busy.Count > 0)
            list.Add(new("busy-processes", "Background apps are using CPU right now", busy.Sum(b => b.CpuPercent) >= 25 ? Severity.Medium : Severity.Low, "Processes",
                busy.Select(b => $"{b.Name}: {Inv(b.CpuPercent)}% of total CPU (1 s sample)").ToList(),
                "These processes were active during the scan. A one-second sample can catch short bursts, so scan again to confirm.",
                "Less competition for CPU time while gaming if they stay busy.", Confidence.Low, [],
                "Check them in Task Manager. Close or pause them (updates, downloads, sync) before playing."));

        // Startup apps / launchers.
        if (Off("opt.launcher-autostart"))
        {
            var launchers = f.StartupEnabled.Where(n => TweakCatalog.AutoStartNames.Any(a => n.StartsWith(a, StringComparison.OrdinalIgnoreCase))).ToList();
            if (launchers.Count > 0)
                list.Add(new("startup-launchers", $"{launchers.Count} launcher/chat app(s) start with Windows", launchers.Count >= 3 ? Severity.Low : Severity.Info, "Startup",
                    [$"Enabled startup entries: {f.StartupEnabled.Count}", .. launchers.Select(n => "Auto-starts: " + n)],
                    "They load at sign-in and keep running in the background.", "Faster sign-in and less background memory/CPU. Apps still work when you open them.", Confidence.Medium,
                    ["opt.launcher-autostart"]));
        }

        // Gaming settings.
        if (Off("gen.game-mode"))
            list.Add(new("game-mode", "Game Mode is off", Severity.Low, "Gaming", ["AutoGameModeEnabled / AllowAutoGameMode are not 1"],
                "Game Mode prioritises the game and pauses Windows Update driver installs while you play.", "Fewer background interruptions. Usually small effect on FPS.", Confidence.Medium, ["gen.game-mode"]));
        if (Off("gen.background-recording"))
            list.Add(new("game-dvr", "Game DVR background recording is on", weakPc ? Severity.Medium : Severity.Low, "Gaming", ["GameDVR / AppCapture values allow background capture"],
                "The Xbox Game Bar can keep recording in the background so you can save clips.", "Frees GPU encoder and memory work. More noticeable on weaker GPUs.", Confidence.Medium, ["gen.background-recording"]));
        if (Off("gen.mouse-accel"))
            list.Add(new("mouse-accel", "Mouse acceleration is on", Severity.Low, "Input", ["MouseSpeed / thresholds are not 0"],
                "'Enhance pointer precision' changes cursor distance based on how fast you move the mouse.", "Consistent aim (same hand movement = same distance). No FPS effect.", Confidence.High, ["gen.mouse-accel"]));
        if (Off("gen.usb-suspend"))
            list.Add(new("usb-suspend", "USB selective suspend is on", Severity.Low, "Input", ["Active power plan: USB selective suspend enabled"],
                "Windows may power down idle USB ports.", "Avoids rare mouse, keyboard or headset dropouts. No FPS effect.", Confidence.Medium, ["gen.usb-suspend"]));

        // Weak hardware: visual effects.
        if (weakPc && (Off("gen.visualfx-performance") || Off("gen.transparency")))
            list.Add(new("visual-effects", "Visual effects on a lower-end PC", Severity.Low, "Windows",
                [$"{f.Threads} threads, {Inv(f.RamGb)} GB RAM"],
                "Animations and transparency cost a little CPU/GPU time in Windows itself.", "Snappier desktop. Games are usually not affected.", Confidence.Low,
                new[] { "gen.visualfx-performance", "gen.transparency" }.Where(Off).ToArray()));

        // Network.
        if (f.Adapters.Count > 0 && f.Adapters.All(a => a.Type == "Wi-Fi"))
            list.Add(new("wifi-only", "Connected over Wi-Fi only", Severity.Medium, "Network",
                f.Adapters.Select(a => $"{a.Name}: {a.Type}, link {a.SpeedBps / 1_000_000} Mbps").ToList(),
                "Wi-Fi adds variable latency and occasional packet loss compared with a cable.", "A wired connection usually gives steadier ping and less jitter. No setting can fix this.", Confidence.High, [],
                "Use an Ethernet cable if you can. Measure before/after with the Ping Stabilizer."));
        foreach (var a in f.Adapters.Where(a => a.Type == "Ethernet" && a.SpeedBps is > 0 and < 1_000_000_000))
            list.Add(new("eth-speed-" + a.Name, "Ethernet link below 1 Gbps", Severity.Low, "Network", [$"{a.Name}: link {a.SpeedBps / 1_000_000} Mbps"],
                "A 100 Mbps link often means a damaged cable, a Cat5 cable, or an old switch port.", "Faster downloads. Ping is rarely affected.", Confidence.Medium, [],
                "Try another cable or router port."));
        if (Off("net.adapter-power") && f.Adapters.Count > 0)
            list.Add(new("adapter-power", "Network adapter power saving is on", Severity.Low, "Network", ["Windows may turn the adapter off to save power"],
                "Power saving can cause brief disconnects or lag spikes on some adapters.", "Fewer drop-outs on affected adapters. Typical ping is not changed.", Confidence.Low, ["net.adapter-power"]));

        // Disk space.
        if (f.SystemFreePercent is { } pct && (pct < 10 || f.SystemFreeGb < 20))
            list.Add(new("disk-space", "System drive is almost full", pct < 5 ? Severity.High : Severity.Medium, "Storage",
                [$"{Inv(f.SystemFreeGb ?? 0)} GB free ({Inv(pct)}%)"],
                "Windows needs free space for the page file, updates and shader caches.", "Avoids update failures and stutter from a full page file.", Confidence.High, [],
                "Free space with Settings → System → Storage → Cleanup recommendations."));

        // HAGS (reboot; mixed results, so low confidence).
        if (!f.HagsOn && f.Build >= 19041 && Off("adv.hags") && f.Gpus.Split('+').Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)))
            list.Add(new("hags", "Hardware-accelerated GPU scheduling is off", Severity.Info, "GPU", [$"GPU: {f.Gpus}", "HwSchMode is not 2"],
                "HAGS lets the GPU manage its own scheduling. Required for DLSS Frame Generation.", "Results vary by game and driver: some see small gains, some none. Needs a restart.", Confidence.Low, ["adv.hags"]));

        if (f.RebootPending.Count > 0)
            list.Add(new("reboot-pending", "Restart needed for applied tweaks", Severity.Info, "Tweaks", f.RebootPending.Select(n => n + " (applied since the last restart)").ToList(),
                "These tweaks are written but only take effect after a restart.", "Makes the changes you already applied active.", Confidence.High, []));

        // Safety net: never offer a disabled/security tweak or a service change, even if a rule above asked for it.
        return list.Select(x => x with { FixTweakIds = x.FixTweakIds.Where(Fixable).ToArray() })
            .OrderByDescending(x => x.Severity).ThenBy(x => x.Area).ToList();
    }

    public static bool Fixable(string id) => TweakCatalog.Find(id) is { Kind: TweakKind.Toggle } t && TweakAudit.DisabledReason(t) is null && t.Category != TweakCategory.Services;
}

/// <summary>Ignored findings and what each Fix changed (so it can be undone), in scanner.json.</summary>
public sealed class ScannerState
{
    public HashSet<string> Ignored { get; set; } = [];
    public Dictionary<string, List<string>> Fixed { get; set; } = [];
    public DateTime? LastScan { get; set; }

    private static string PathFor => Path.Combine(AppPaths.Root, "scanner.json");

    public static ScannerState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<ScannerState>(File.ReadAllText(PathFor)) ?? new(); }
        catch (Exception ex) { Log.Warn("scanner.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(PathFor, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
