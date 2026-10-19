using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Cleaner;

public enum ProcessClass { SafeToClose, ReviewFirst, Protected }

/// <summary>One app (all processes with the same name in this session), measured and classified.</summary>
public sealed record AppProcess(string Name, IReadOnlyList<int> Pids, string? Path, string? Title, string? Company, double CpuPercent, double RamMb,
    ProcessClass Class, string Reason, string Benefit, bool HasWindow);

public enum CloseOutcome { ClosedGracefully, ForceClosed, StillRunning, Refused, Failed, NotRunning }

public sealed record CloseResult(string Name, CloseOutcome Outcome, int Processes, double RamMbBefore, string Detail);

public sealed record SystemLoad(double CpuPercent, double RamUsedGb, double RamTotalGb);

public sealed record BackgroundCleanReport(DateTime At, string Mode, string? Game, SystemLoad Before, SystemLoad After, IReadOnlyList<CloseResult> Results)
{
    public double RamFreedByClosedAppsMb => Results.Where(r => r.Outcome is CloseOutcome.ClosedGracefully or CloseOutcome.ForceClosed).Sum(r => r.RamMbBefore);
}

/// <summary>Per-game cleanup: the apps the user approved closing before that game, and whether to do it on launch.</summary>
public sealed class GameCleanupRule
{
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public List<string> Close { get; set; } = [];
    public bool CleanOnLaunch { get; set; }
    public bool ForceIfIgnored { get; set; } = true;
}

/// <summary>Cleaner settings and history (cleaner.json): exclusions, per-game rules, storage exclusions, past runs.</summary>
public sealed class CleanerSettings
{
    public List<string> ProcessExclusions { get; set; } = [];
    public List<GameCleanupRule> GameRules { get; set; } = [];
    public List<string> StorageExclusions { get; set; } = [];
    public bool Quarantine { get; set; } = true;
    public bool ForceIfIgnored { get; set; } = true;
    public List<string> History { get; set; } = [];

    public static string PathFor => System.IO.Path.Combine(AppPaths.Root, "cleaner.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static CleanerSettings Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<CleanerSettings>(File.ReadAllText(PathFor), Json) ?? new(); }
        catch (Exception ex) { Log.Warn("cleaner.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save() => AtomicFile.WriteAllText(PathFor, JsonSerializer.Serialize(this, Json));

    public void AddHistory(string line)
    {
        History.Insert(0, $"{DateTime.Now:yyyy-MM-dd HH:mm} · {line}");
        if (History.Count > 60) History.RemoveRange(60, History.Count - 60);
    }

    public GameCleanupRule? RuleFor(string gameKey) => GameRules.FirstOrDefault(r => r.GameKey == gameKey);
}

/// <summary>
/// Background Cleaner. Lists this user's apps with real CPU and RAM use, classifies each one, and closes only what the
/// user approved: politely first (close request to its windows), force only if allowed and the app ignored it, never a
/// process tree, never another user's or a service's process. Protected: Windows, security software, anti-cheat, drivers
/// and their helpers, installers/updaters at work, networking/VPN, audio, controllers, streaming/recording, Money Tweaks
/// and the game. Safe to Close is a curated list of background helpers whose job can wait; everything else is Review First.
/// </summary>
public static class ProcessCleaner
{
    private sealed record Rule(ProcessClass Class, string Reason, string Benefit = "");

    // Exact process names (without .exe), case-insensitive.
    private static readonly Dictionary<string, Rule> Known = new(StringComparer.OrdinalIgnoreCase);
    // Name prefixes (vendors with many helper processes).
    private static readonly List<(string prefix, Rule rule)> Prefixes = [];

    private static void P(string reason, params string[] names) { foreach (var n in names) Known[n] = new(ProcessClass.Protected, reason); }
    private static void PP(string reason, params string[] prefixes) { foreach (var n in prefixes) Prefixes.Add((n, new(ProcessClass.Protected, reason))); }
    private static void S(string reason, string benefit, params string[] names) { foreach (var n in names) Known[n] = new(ProcessClass.SafeToClose, reason, benefit); }
    private static void R(string reason, string benefit, params string[] names) { foreach (var n in names) Known[n] = new(ProcessClass.ReviewFirst, reason, benefit); }
    private static void RP(string reason, string benefit, params string[] prefixes) { foreach (var n in prefixes) Prefixes.Add((n, new(ProcessClass.ReviewFirst, reason, benefit))); }

    static ProcessCleaner()
    {
        P("Core Windows process.", "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "explorer", "fontdrvhost", "Registry",
          "Memory Compression", "sihost", "ctfmon", "taskhostw", "conhost", "dllhost", "RuntimeBroker", "SearchHost", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost",
          "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "LockApp", "WmiPrvSE", "spoolsv", "SgrmBroker", "WUDFHost", "smartscreen", "ApplicationFrameHost",
          "SystemSettings", "UserOOBEBroker", "backgroundTaskHost", "CompPkgSrv", "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp", "AggregatorHost", "wlanext", "dasHost",
          "ShellHost", "unsecapp", "consent", "LogonUI", "userinit", "WerFault", "WerFaultSecure", "SecurityHealthHost");
        P("Security software: closing it would weaken protection.", "MsMpEng", "MpDefenderCoreService", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "MsSense", "SenseIR",
          "avp", "avpui", "AvastSvc", "AvastUI", "AVGSvc", "AVGUI", "bdagent", "bdservicehost", "vsserv", "mbam", "mbamtray", "MBAMService", "ekrn", "egui", "NortonSecurity", "ns", "nsWscSvc",
          "mcshield", "mfemms", "ModuleCoreService", "SavService", "SophosHealth", "CSFalconService", "CSFalconContainer", "SentinelAgent", "SentinelUI", "PSUAService", "PSUAMain", "HitmanPro",
          "Malwarebytes", "MBAMWsc", "wrsa", "fsaua", "fshoster32", "KeePass", "KeePassXC", "1Password", "Bitwarden");
        PP("Security software: closing it would weaken protection.", "McAfee", "Norton", "Kaspersky", "Avira", "ESET", "Sophos", "Trend", "TMBMSRV", "Webroot", "Panda", "Comodo", "cmdagent", "Defender");
        P("Anti-cheat: games refuse to run or kick you without it.", "vgc", "vgtray", "vgk", "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup", "BEService", "BEService_x64", "BattlEye",
          "FACEIT", "FACEITService", "faceit_ac", "EAAntiCheat.GameServiceLauncher", "EAAntiCheat.GameService", "xigncode", "GameGuard", "GameMon", "nProtect", "mhyprot", "ACE-Guard", "SGuard64", "SGuardSvc64",
          "EOSOverlayRenderer-Win64-Shipping", "PnkBstrA", "PnkBstrB", "Ricochet");
        PP("Anti-cheat: games refuse to run or kick you without it.", "EasyAntiCheat", "BEService", "FACEIT", "EAAntiCheat", "ACE-");
        P("Graphics/chipset driver helper: closing it can break display, overlay or driver settings.", "nvcontainer", "NVDisplay.Container", "nvsphelper64", "NVIDIA Web Helper", "NVIDIA Share", "NVIDIA Overlay",
          "atiesrxx", "atieclxx", "amdfendrsr", "AMDRSServ", "RadeonSoftware", "igfxEM", "igfxHK", "igfxTray", "IntelGraphicsSoftware", "IGCCTray", "IGCC", "jhi_service", "LMS", "esif_uf", "DptfPolicyLpmService");
        PP("Graphics/chipset driver helper: closing it can break display, overlay or driver settings.", "NVIDIA", "AMDRS", "amdow", "igfx", "Intel(R)");
        P("Audio: closing it can mute sound or your microphone.", "audiodg", "RtkAudUService64", "RtkAudioService64", "RAVBg64", "RAVCpl64", "NahimicSvc64", "NahimicSvc32", "Nahimic3", "WavesSvc64", "WavesSysSvc64",
          "MaxxAudioPro", "DTSAudioService64", "SonicStudio3", "DolbyDAX2API", "voicemeeter", "voicemeeterpro", "voicemeeter8", "audiorelay", "SteelSeriesSonar", "SteelSeriesGG", "SteelSeriesEngine", "NVBroadcast", "NVIDIA Broadcast");
        P("Controller/input driver: closing it can disconnect your controller or remap.", "DS4Windows", "DSX", "x360ce", "XboxPcAppFT", "GameInputSvc", "joytokey", "antimicrox", "SCPToolkit", "ViGEmBus", "HidHide", "reWASD", "reWASDService", "reWASDEngine");
        PP("Controller/input driver: closing it can disconnect your controller or remap.", "ViGEm", "reWASD");
        P("Networking/VPN: closing it can drop your connection.", "openvpn", "openvpn-gui", "wireguard", "NordVPN", "nordvpn-service", "ExpressVPN", "expressvpnd", "ProtonVPN", "ProtonVPNService", "tailscaled", "tailscale-ipn",
          "zerotier_desktop_ui", "zerotier-one_x64", "Mullvad VPN", "mullvad-daemon", "WARP", "Cloudflare WARP", "warp-svc", "PIA", "pia-service", "Killer Network Service", "KillerNetworkService", "xtuservice");
        P("Streaming/recording: it may be recording or live right now.", "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core", "XSplit.Broadcaster", "Medal", "MedalEncoder", "ShadowPlay", "GameBar", "GameBarFTServer", "Outplayed", "Overwolf");
        PP("Streaming/recording: it may be recording or live right now.", "obs-", "Streamlabs", "XSplit");
        P("Installer or updater at work: interrupting it can leave software half-installed.", "msiexec", "TrustedInstaller", "TiWorker", "MoUsoCoreWorker", "usocoreworker", "wuauclt", "UpdateAssistant", "Windows10UpgraderApp",
          "SetupHost", "setup", "Setup", "install", "Install", "unins000", "Uninstall", "DismHost", "dism", "EpicInstaller", "SteamSetup", "vc_redist.x64", "vc_redist.x86");
        P("Money Tweaks itself.", "MoneyTweaks", "MoneyTweaks-Setup");
        P("Embedded browser engine used by another app: closing it breaks the app that hosts it.", "msedgewebview2", "CefSharp.BrowserSubprocess", "QtWebEngineProcess", "CefSharp", "cefsharp.browsersubprocess");
        PP("Embedded browser engine used by another app: closing it breaks the app that hosts it.", "msedgewebview", "CefSharp", "QtWebEngine");
        P("Windows input component (touch keyboard, pen, game controllers).", "TabTip", "TabTip32", "InputPersonalization", "GameInputRedistService", "GameInputSvc", "wisptis");
        PP("Security software: closing it would weaken protection.", "Mbam", "MBAM");

        // Safe to close: background helpers whose job (sync, updates, launch-at-login extras) simply resumes later.
        S("Background sync client: it resumes syncing when you start it again.", "Syncing uses disk and network in the background.", "OneDrive", "Dropbox", "iCloudDrive", "iCloudServices", "MEGAsync", "pCloud", "BoxSync");
        S("Updater: it checks for updates again next time.", "Update checks can download and use disk while you play.", "AdobeARM", "AdobeCollabSync", "jusched", "jucheck", "GoogleUpdate", "SoftwareUpdate",
          "CCXProcess", "AdobeUpdateService", "Creative Cloud", "Creative Cloud Helper", "CoreSync", "AdobeIPCBroker", "acrotray", "SpotifyWebHelper", "Lenovo.Modern.ImController", "HPPrintScanDoctor");
        S("Optional extra that starts with Windows.", "Frees memory and an occasional CPU wake-up.", "PhoneExperienceHost", "YourPhone", "Widgets", "WidgetService", "WidgetBoard", "MicrosoftStartFeedProvider", "Copilot", "Cortana", "Grammarly.Desktop", "GrammarlyHelper",
          "CCleaner64", "CCleanerMonitor", "Ccleaner", "SkypeApp", "SkypeBackgroundHost", "Skype", "Wallpaper32", "Wallpaper64", "wallpaper32", "wallpaper64", "Lively", "Rainmeter", "WinStore.App", "HxOutlook", "HxTsr", "HxCalendarAppImm", "Microsoft.Photos", "PhotosApp", "Video.UI", "Music.UI");
        // Review first: useful apps you may be using.
        R("Web browser: tabs may play music, hold unsaved forms or a guide you're reading.", "Browsers can use a lot of memory and some CPU.", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc");
        R("Voice/chat app: closing it ends calls and voice chat.", "Frees memory; video calls also use CPU/GPU.", "Discord", "TeamSpeak", "ts3client_win64", "Mumble", "ms-teams", "Teams", "Zoom", "Slack", "WhatsApp", "Telegram", "Signal");
        R("Game launcher: some games need it running (Steam for Steam Input and Steam games).", "Launchers use memory and update in the background.", "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net",
          "EADesktop", "EABackgroundService", "UbisoftConnect", "upc", "GalaxyClient", "RiotClientServices", "RiotClientUx", "RobloxPlayerLauncher", "XboxPcApp", "GOG Galaxy");
        R("Music/media: closing it stops playback.", "Frees memory.", "Spotify", "iTunes", "vlc", "foobar2000", "MusicBee", "AppleMusic", "TIDAL", "Deezer");
        R("Peripheral software: closing it can reset RGB, fan curves, DPI or macros.", "Some RGB suites poll hardware often.", "iCUE", "Corsair.Service", "SignalRgb", "OpenRGB", "LightingService", "ArmouryCrate", "ArmouryCrate.UserSessionHelper",
          "RazerCentral", "Razer Synapse 3", "RazerAppEngine", "lghub", "lghub_agent", "lghub_updater", "Lghub_agent", "MSI.CentralServer", "MSI Center", "NZXT CAM", "CAM", "FanControl", "HWiNFO64", "MSIAfterburner", "RTSS", "RTSSHooksLoader64");
        RP("Peripheral software: closing it can reset RGB, fan curves, DPI, analog-key or macro settings.", "", "Corsair", "Razer", "lghub", "ArmouryCrate", "MSI", "wooting", "Wooting", "SteelSeries", "HyperX", "NGENUITY", "Glorious", "Logitech");
        R("Office/productivity app: may hold unsaved work.", "Frees memory.", "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "Notepad", "notepad++", "Code", "devenv", "Photoshop", "Acrobat", "AcroRd32", "Obsidian", "Notion");
    }

    /// <summary>The game being played is always protected, and the user's exclusions are never offered.</summary>
    public static (ProcessClass cls, string reason, string benefit) Classify(string name, string? path, bool hasWindow, IReadOnlyCollection<string>? exclusions = null, string? game = null)
    {
        if (game is not null && string.Equals(name, System.IO.Path.GetFileNameWithoutExtension(game), StringComparison.OrdinalIgnoreCase)) return (ProcessClass.Protected, "The game you're playing.", "");
        if (exclusions?.Contains(name, StringComparer.OrdinalIgnoreCase) == true) return (ProcessClass.Protected, "In your exclusions: never closed by Money Tweaks.", "");
        if (Known.TryGetValue(name, out var k) && k.Class == ProcessClass.Protected) return (k.Class, k.Reason, k.Benefit);
        foreach (var (prefix, rule) in Prefixes.Where(p => p.rule.Class == ProcessClass.Protected))
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (rule.Class, rule.Reason, rule.Benefit);
        // Installers by name pattern.
        var lower = name.ToLowerInvariant();
        if (lower.Contains("setup") || lower.Contains("install") || lower.StartsWith("unins", StringComparison.Ordinal) || lower.Contains("update") && lower.Contains("install"))
            return (ProcessClass.Protected, "Looks like an installer: interrupting it can leave software half-installed.", "");
        // Anything that runs from the Windows folder is part of Windows (or a driver component).
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path is not null && path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return (ProcessClass.Protected, "Runs from the Windows folder: part of Windows or a driver.", "");
        if (path is null) return (ProcessClass.Protected, "Its file can't be checked (it runs with higher rights), so it's left alone.", "");
        if (path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Drivers\", StringComparison.OrdinalIgnoreCase))
            return (ProcessClass.Protected, "Driver component.", "");
        if (Known.TryGetValue(name, out var r)) return (r.Class, r.Reason, r.Benefit);
        foreach (var (prefix, rule) in Prefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (rule.Class, rule.Reason, rule.Benefit);
        return hasWindow
            ? (ProcessClass.ReviewFirst, "An open app: it may have unsaved work.", "Frees the memory and CPU it uses.")
            : (ProcessClass.ReviewFirst, "Unknown background process: close it only if you know what it does.", "Frees the memory and CPU it uses.");
    }

    public static bool IsProtected(string name, string? path = null) =>
        Classify(name, path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name + ".exe"), true).cls == ProcessClass.Protected;

    // ---- listing ----------------------------------------------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint w, nint l);
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    private const uint WM_CLOSE = 0x0010;

    private static Dictionary<int, List<nint>> TopWindows(bool visibleOnly)
    {
        var map = new Dictionary<int, List<nint>>();
        EnumWindows((h, _) =>
        {
            if (visibleOnly && !IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            (map.TryGetValue((int)pid, out var l) ? l : map[(int)pid] = []).Add(h);
            return true;
        }, 0);
        return map;
    }

    /// <summary>
    /// This user's apps (same session as Money Tweaks; services and other users are never listed), measured over
    /// <paramref name="sample"/>: CPU as a share of all cores, RAM as working set.
    /// </summary>
    public static async Task<List<AppProcess>> ListAsync(IReadOnlyCollection<string> exclusions, string? game, TimeSpan? sample = null, CancellationToken ct = default)
    {
        int session = Process.GetCurrentProcess().SessionId, self = Environment.ProcessId;
        var first = new Dictionary<int, TimeSpan>();
        var procs = new List<(int pid, string name, long ws)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.SessionId != session || p.Id == 0) continue;
                    procs.Add((p.Id, p.ProcessName, p.WorkingSet64));
                    try { first[p.Id] = p.TotalProcessorTime; } catch { }
                }
                catch { }
            }
        }
        var sw = Stopwatch.StartNew();
        await Task.Delay(sample ?? TimeSpan.FromSeconds(1.5), ct);
        double elapsed = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
        var cpu = new Dictionary<int, double>();
        foreach (var (pid, _, _) in procs)
        {
            if (!first.TryGetValue(pid, out var t0)) continue;
            try { using var p = Process.GetProcessById(pid); cpu[pid] = Math.Max(0, (p.TotalProcessorTime - t0).TotalMilliseconds / elapsed * 100); } catch { }
        }
        var windows = TopWindows(visibleOnly: true);
        var titles = new Dictionary<int, string>();
        foreach (var (pid, _, _) in procs)
            try { using var p = Process.GetProcessById(pid); if (!string.IsNullOrEmpty(p.MainWindowTitle)) titles[pid] = p.MainWindowTitle; } catch { }

        var list = new List<AppProcess>();
        foreach (var g in procs.GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase))
        {
            var pids = g.Select(x => x.pid).ToList();
            string? path = pids.Select(GameDetector.ImagePath).FirstOrDefault(x => x is not null);
            bool hasWindow = pids.Any(windows.ContainsKey);
            var (cls, reason, benefit) = Classify(g.Key, path, hasWindow, exclusions, game);
            string? company = null;
            if (path is not null) try { company = FileVersionInfo.GetVersionInfo(path).CompanyName; } catch { }
            double ram = g.Sum(x => x.ws) / 1048576.0, c = pids.Sum(p => cpu.GetValueOrDefault(p));
            var why = benefit.Length == 0 ? "" : $"Using {c:0.#}% CPU and {ram:0} MB RAM. {benefit}";
            list.Add(new(g.Key, pids, path, pids.Select(p => titles.GetValueOrDefault(p)).FirstOrDefault(t => t is not null), company, c, ram, cls, reason, why, hasWindow));
        }
        return list.OrderBy(a => a.Class).ThenByDescending(a => a.RamMb + a.CpuPercent * 50).ToList();
    }

    public static SystemLoad Load()
    {
        using var hm = new HardwareMonitor();
        Thread.Sleep(1000);
        var s = hm.Sample();
        return new(s.CpuPercent, s.RamUsedGb, s.RamTotalGb);
    }

    // ---- closing ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Closes the approved apps. Each is re-classified right before closing (anything Protected now is refused). Polite
    /// first: WM_CLOSE to every top-level window of the app (visible or not), then wait; force only when
    /// <paramref name="force"/> and the app ignored the request. One process at a time, never its children. Only this
    /// session's processes. Measures system CPU/RAM before and after.
    /// </summary>
    public static async Task<BackgroundCleanReport> CleanAsync(IEnumerable<string> approved, bool force, string mode, string? game, IReadOnlyCollection<string> exclusions,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Measuring CPU and RAM before…");
        var before = await Task.Run(Load, ct);
        var current = await ListAsync(exclusions, game, TimeSpan.FromSeconds(0.5), ct);
        var results = new List<CloseResult>();
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var name in approved.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var app = current.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (app is null) { results.Add(new(name, CloseOutcome.NotRunning, 0, 0, "Wasn't running.")); continue; }
            if (app.Class == ProcessClass.Protected) { results.Add(new(name, CloseOutcome.Refused, app.Pids.Count, app.RamMb, "Protected: " + app.Reason)); continue; }
            progress?.Report($"Closing {name}…");
            var outcome = await Task.Run(() => CloseApp(app, force, session), ct);
            results.Add(outcome with { RamMbBefore = app.RamMb });
        }
        progress?.Report("Measuring CPU and RAM after…");
        await Task.Delay(2000, ct);
        var after = await Task.Run(Load, ct);
        var report = new BackgroundCleanReport(DateTime.Now, mode, game, before, after, results);
        foreach (var r in results) Log.Info($"Background Cleaner: {r.Name} → {r.Outcome} ({r.Detail})");
        return report;
    }

    private static CloseResult CloseApp(AppProcess app, bool force, int session)
    {
        var windows = TopWindows(visibleOnly: false);
        int asked = 0;
        foreach (var pid in app.Pids)
            foreach (var h in windows.GetValueOrDefault(pid) ?? [])
                if (PostMessage(h, WM_CLOSE, 0, 0)) asked++;
        var deadline = DateTime.UtcNow.AddSeconds(asked > 0 ? 5 : 0);
        while (DateTime.UtcNow < deadline && Alive(app.Pids).Count > 0) Thread.Sleep(250);
        var left = Alive(app.Pids);
        if (left.Count == 0) return new(app.Name, CloseOutcome.ClosedGracefully, app.Pids.Count, 0, asked > 0 ? "Closed after a close request (like clicking X)." : "Already exited.");
        if (!force)
            return new(app.Name, CloseOutcome.StillRunning, left.Count, 0, asked > 0 ? "Didn't close after a close request; force-close is off." : "Has no window to ask; force-close is off.");
        int killed = 0; string? error = null;
        foreach (var pid in left)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.SessionId != session) continue;
                p.Kill(entireProcessTree: false); // never the tree: a launcher's children can be the game
                if (p.WaitForExit(3000)) killed++;
            }
            catch (Exception ex) { error = ex.Message; }
        }
        left = Alive(app.Pids);
        return left.Count == 0
            ? new(app.Name, CloseOutcome.ForceClosed, app.Pids.Count, 0, asked > 0 ? "Ignored the close request, so it was force-closed." : "Background process without a window: force-closed.")
            : new(app.Name, CloseOutcome.Failed, left.Count, 0, $"{left.Count} process(es) still running" + (error is not null ? $": {error}" : " (Windows refused)."));
    }

    private static List<int> Alive(IEnumerable<int> pids)
    {
        var alive = new List<int>();
        foreach (var pid in pids)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) alive.Add(pid); } catch { }
        return alive;
    }

    public static string Summarize(BackgroundCleanReport r)
    {
        int closed = r.Results.Count(x => x.Outcome is CloseOutcome.ClosedGracefully or CloseOutcome.ForceClosed);
        return $"{r.Mode}: closed {closed} of {r.Results.Count(x => x.Outcome != CloseOutcome.NotRunning)} app(s); RAM in use {r.Before.RamUsedGb:0.0} → {r.After.RamUsedGb:0.0} GB, CPU {r.Before.CpuPercent:0}% → {r.After.CpuPercent:0}%";
    }
}
