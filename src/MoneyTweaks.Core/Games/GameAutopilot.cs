using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Games;

public enum AutopilotPreset { Off, Light, Competitive, Custom }

/// <summary>Where a session's optimizations come from. Both go through the Gaming Profiles engine (backup, rollback, restore).</summary>
public enum AutopilotOptimizations { None, GameProfile, Custom }

/// <summary>What Autopilot may do for one game. Nothing runs that isn't switched on here.</summary>
public sealed class AutopilotRule
{
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public bool Enabled { get; set; }
    public AutopilotPreset Preset { get; set; } = AutopilotPreset.Off;

    // Pre-game
    /// <summary>Readiness check when the game starts: temperatures, RAM/VRAM, storage, background activity, driver.</summary>
    public bool Readiness { get; set; } = true;

    // While playing
    public AutopilotOptimizations Optimizations { get; set; } = AutopilotOptimizations.None;
    /// <summary>Custom optimizations: power plan while playing (the previous plan is restored).</summary>
    public PowerChoice Power { get; set; } = PowerChoice.Keep;
    /// <summary>Custom optimizations: Windows timer resolution while playing (0.5–1.0 ms), null = off.</summary>
    public double? TimerMs { get; set; }
    /// <summary>Custom optimizations: catalog tweaks applied for the session and restored after.</summary>
    public List<string> Tweaks { get; set; } = [];
    /// <summary>The game's own process to High priority while it runs (only if it's at Normal; runtime only).</summary>
    public bool RaisePriority { get; set; }
    /// <summary>Show the performance overlay (with this game's saved layout) and hide it again afterwards.</summary>
    public bool ShowOverlay { get; set; }

    // Live protection
    /// <summary>Record Hardware Monitor warnings (temperature, throttling, memory) during the session and notify quietly.</summary>
    public bool LiveWarnings { get; set; } = true;

    // Post-game
    /// <summary>A short summary when the session ends (duration, warnings, what was restored).</summary>
    public bool Summary { get; set; } = true;

    public bool ChangesSomething => Optimizations != AutopilotOptimizations.None || RaisePriority || ShowOverlay;
}

/// <summary>One gaming session. Saved before Autopilot changes anything, so a crash of Money Tweaks can be recovered.</summary>
public sealed class AutopilotSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public int Pid { get; set; }
    public string? Exe { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public string? EndedBy { get; set; }
    /// <summary>The Gaming Profiles session Autopilot started (and therefore restores), null when it didn't start one.</summary>
    public string? OwnedProfileId { get; set; }
    public bool OverlayShownByAutopilot { get; set; }
    public string? OriginalPriority { get; set; }
    public List<string> Actions { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public List<string> Readiness { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Restored { get; set; } = [];
    public List<string> RestoreProblems { get; set; } = [];
    public double? PeakCpuTempC { get; set; }
    public double? PeakGpuTempC { get; set; }
    public TimeSpan? Duration => EndedAt is { } e ? e - StartedAt : null;
}

public sealed class AutopilotFile
{
    public int Version { get; set; } = 1;
    /// <summary>Master switch: off means no game is watched, whatever the rules say.</summary>
    public bool Enabled { get; set; }
    public Dictionary<string, AutopilotRule> Rules { get; set; } = [];
    public AutopilotSession? Active { get; set; }
    public List<AutopilotSession> History { get; set; } = [];
}

/// <summary>autopilot.json in Money Tweaks' data folder, written atomically. Local only.</summary>
public static class AutopilotStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath => Path.Combine(AppPaths.Root, "autopilot.json");
    public const int MaxHistory = 100;

    public static AutopilotFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AutopilotFile>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex) { Log.Error("autopilot.json unreadable (kept as .bad)", ex); try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new();
    }

    public static void Save(AutopilotFile f)
    {
        if (f.History.Count > MaxHistory) f.History.RemoveRange(0, f.History.Count - MaxHistory);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(f, Json));
    }
}

/// <summary>What the watcher should do next.</summary>
public sealed record AutopilotDecision(string Kind, string? GameKey = null, int Pid = 0, string? Reason = null, IReadOnlyList<string>? Waiting = null)
{
    public static readonly AutopilotDecision Nothing = new("none");
}

/// <summary>
/// Game Autopilot's rules: which session to start, end or keep, presets, the profile a session runs through, and the
/// readiness check. Pure where it can be (tests feed it by hand). One session at a time: a second game that starts while a
/// session runs waits until the first one ends, so two sessions can never capture or restore conflicting values.
/// </summary>
public static class GameAutopilot
{
    public const string ProfilePrefix = "autopilot:";

    /// <summary>Safe, per-user-or-reversible tweaks offered in the editor (all restored at the end of the session).</summary>
    public static readonly string[] SessionTweaks =
        ["gen.game-mode", "gen.background-recording", "gen.gamebar-tips", "gen.mouse-accel", "gen.sticky-keys", "gen.usb-suspend", "gen.toasts-off", "adv.system-responsiveness", "adv.games-task", "opt.launcher-priority"];

    /// <summary>The next step for the watcher. <paramref name="running"/>: rule games that run now (key, verified pid).</summary>
    public static AutopilotDecision Decide(AutopilotFile f, IReadOnlyList<(string key, int pid)> running, Func<int, bool> alive, ISet<int>? suppressed = null)
    {
        var enabled = running.Where(r => f.Rules.TryGetValue(r.key, out var rule) && rule.Enabled && !(suppressed?.Contains(r.pid) ?? false)).ToList();
        if (f.Active is { } a)
        {
            if (!alive(a.Pid)) return new("end", a.GameKey, a.Pid, "exited");
            var waiting = enabled.Where(r => r.pid != a.Pid).Select(r => f.Rules[r.key].GameName).Distinct().ToList();
            return waiting.Count > 0 ? new("busy", a.GameKey, a.Pid, Waiting: waiting) : AutopilotDecision.Nothing;
        }
        if (!f.Enabled || enabled.Count == 0) return AutopilotDecision.Nothing;
        var next = enabled.OrderBy(r => f.Rules[r.key].GameName, StringComparer.CurrentCultureIgnoreCase).First();
        return new("start", next.key, next.pid);
    }

    /// <summary>At startup: a session was still recorded. Resume it if its game is still running, otherwise end and restore.</summary>
    public static AutopilotDecision Recover(AutopilotFile f, Func<int, bool> alive) =>
        f.Active is not { } a ? AutopilotDecision.Nothing
        : alive(a.Pid) ? new("resume", a.GameKey, a.Pid)
        : new("end", a.GameKey, a.Pid, "Money Tweaks closed unexpectedly during the session");

    /// <summary>A preset's actions for a game. Competitive uses the game's own Gaming Profile when it has one.</summary>
    public static AutopilotRule Apply(AutopilotRule r, AutopilotPreset preset, bool hasGameProfile)
    {
        r.Preset = preset;
        r.Enabled = preset != AutopilotPreset.Off;
        switch (preset)
        {
            case AutopilotPreset.Off:
                break;
            case AutopilotPreset.Light:
                r.Readiness = true; r.LiveWarnings = true; r.Summary = true; r.ShowOverlay = true;
                r.Optimizations = AutopilotOptimizations.None; r.RaisePriority = false; r.Power = PowerChoice.Keep; r.TimerMs = null; r.Tweaks = [];
                break;
            case AutopilotPreset.Competitive:
                r.Readiness = true; r.LiveWarnings = true; r.Summary = true; r.ShowOverlay = false; r.RaisePriority = true;
                if (hasGameProfile) { r.Optimizations = AutopilotOptimizations.GameProfile; r.Power = PowerChoice.Keep; r.TimerMs = null; r.Tweaks = []; }
                else { r.Optimizations = AutopilotOptimizations.Custom; r.Power = PowerChoice.HighPerformance; r.TimerMs = 0.5; r.Tweaks = ["gen.game-mode", "gen.background-recording", "gen.gamebar-tips"]; }
                break;
        }
        return r;
    }

    /// <summary>
    /// The Gaming Profiles definition a session runs through: the game's own profile, or one built from the rule's custom
    /// optimizations (not saved to the profile list). Null when the session changes no settings.
    /// </summary>
    public static GamingProfile? ProfileFor(AutopilotRule r, GamingProfile? gameProfile) => r.Optimizations switch
    {
        AutopilotOptimizations.GameProfile => gameProfile,
        AutopilotOptimizations.Custom when r.Power != PowerChoice.Keep || r.TimerMs is not null || r.Tweaks.Count > 0 => new GamingProfile
        {
            Id = ProfilePrefix + r.GameKey, Name = "Autopilot · " + r.GameName,
            Description = "Built by Game Autopilot for this session only.",
            TweakIds = [.. r.Tweaks.Where(id => TweakCatalog.Find(id) is not null)], Power = r.Power, TimerResolutionMs = r.TimerMs,
        },
        _ => null,
    };

    /// <summary>Every action a rule takes, in plain words, by phase. Used by the editor and the session record.</summary>
    public static (List<string> pre, List<string> during, List<string> post) Describe(AutopilotRule r, GamingProfile? gameProfile)
    {
        var pre = new List<string>(); var during = new List<string>(); var post = new List<string>();
        if (r.Readiness) pre.Add("Readiness check: temperatures, free RAM and graphics memory, game drive space, background CPU use and driver age (read only).");
        var p = ProfileFor(r, gameProfile);
        if (r.Optimizations == AutopilotOptimizations.GameProfile && gameProfile is null) during.Add("Game profile: none linked to this game, so nothing is applied.");
        else if (p is not null)
        {
            if (r.Optimizations == AutopilotOptimizations.GameProfile) during.Add($"Turn on the “{p.Name}” gaming profile ({p.TweakIds.Count} tweak(s){PowerText(p.Power)}{(p.TimerResolutionMs is { } ms ? $", {ms:0.0} ms timer" : "")}).");
            else
            {
                if (p.Power != PowerChoice.Keep) during.Add($"Power plan → {(p.Power == PowerChoice.HighPerformance ? "High performance" : "Balanced")} (your plan is recorded first).");
                if (p.TimerResolutionMs is { } ms) during.Add($"Windows timer resolution {ms:0.0} ms (released afterwards).");
                foreach (var id in p.TweakIds) if (TweakCatalog.Find(id) is { } t) during.Add($"{t.Name} (current value backed up first).");
            }
            post.Add("Restore every setting the session changed from the values recorded before it started, and read them back.");
        }
        if (r.RaisePriority) { during.Add("The game's process → High CPU priority if it's at Normal (only that process, never others)."); post.Add("Priority ends with the game; if it's still running, the original priority is put back."); }
        if (r.ShowOverlay) { during.Add("Show the performance overlay with this game's saved layout."); post.Add("Hide the overlay again if Autopilot showed it."); }
        if (r.LiveWarnings) during.Add("Live protection: record Hardware Monitor warnings (heat, throttling, memory pressure) and notify quietly. Nothing is closed or stopped.");
        if (r.Summary) post.Add("Session summary: how long you played, warnings, peak temperatures and what was restored.");
        return (pre, during, post);
    }

    private static string PowerText(PowerChoice p) => p switch { PowerChoice.HighPerformance => ", High performance power", PowerChoice.Balanced => ", Balanced power", _ => "" };

    // ---- readiness ----------------------------------------------------------------------------------------------------------
    public sealed record ReadinessInputs(double? CpuTempC, double? GpuTempC, double RamAvailableGb, double RamTotalGb, double? VramUsedGb, double? VramTotalGb,
        double? GameDriveFreeGb, string? GameDrive, string? TopBackground, double BackgroundCpuPercent, DateTime? GpuDriverDate, string? GpuName);

    public sealed record ReadinessItem(string Area, bool Ok, string Text);

    /// <summary>Plain checks on what was read when the game started. Fine readings are listed as OK, never as warnings.</summary>
    public static List<ReadinessItem> Readiness(ReadinessInputs i)
    {
        var list = new List<ReadinessItem>();
        string F(double v, string fmt = "0") => v.ToString(fmt, CultureInfo.CurrentCulture);
        if (i.CpuTempC is { } c) list.Add(new("CPU temperature", c < 80, c < 80 ? $"{F(c)} °C at the start: fine." : $"{F(c)} °C before the game is even loaded: check cooling and background load."));
        if (i.GpuTempC is { } g) list.Add(new("GPU temperature", g < 75, g < 75 ? $"{F(g)} °C at the start: fine." : $"{F(g)} °C before the game is loaded: high for an idle card; check fans and dust."));
        double ramPct = i.RamTotalGb > 0 ? i.RamAvailableGb / i.RamTotalGb * 100 : 100;
        list.Add(new("Memory", i.RamAvailableGb >= 3 && ramPct >= 15, i.RamAvailableGb >= 3 && ramPct >= 15
            ? $"{F(i.RamAvailableGb, "0.0")} GB of {F(i.RamTotalGb)} GB free."
            : $"Only {F(i.RamAvailableGb, "0.0")} GB of {F(i.RamTotalGb)} GB free: close large apps (browsers) for smoother play."));
        if (i.VramUsedGb is { } vu && i.VramTotalGb is { } vt && vt > 0)
            list.Add(new("Graphics memory", vu / vt < 0.5, vu / vt < 0.5 ? $"{F(vu, "0.0")} of {F(vt, "0.0")} GB in use." : $"{F(vu, "0.0")} of {F(vt, "0.0")} GB already in use by other apps: the game has less room for textures."));
        if (i.GameDriveFreeGb is { } free)
            list.Add(new("Storage", free >= 15, free >= 15 ? $"{F(free)} GB free on {i.GameDrive}." : $"Only {F(free, "0.#")} GB free on {i.GameDrive}: updates and shader caches need room."));
        list.Add(new("Background activity", i.BackgroundCpuPercent < 15, i.BackgroundCpuPercent < 15
            ? $"Background apps use {F(i.BackgroundCpuPercent)}% CPU."
            : $"Background apps use {F(i.BackgroundCpuPercent)}% CPU{(i.TopBackground is { } t ? $" (most: {t})" : "")}. Game Booster can close the ones you pick."));
        if (i.GpuDriverDate is { } d)
        {
            int months = (int)((DateTime.Now - d).TotalDays / 30.4);
            list.Add(new("Graphics driver", months < 12, months < 12 ? $"{i.GpuName}: driver from {d:d}." : $"{i.GpuName}: driver from {d:d}, over a year old. The Health Center shows where to update it."));
        }
        return list;
    }

    /// <summary>Reads the readiness inputs from Windows and the sensor hub (read only, about a second).</summary>
    public static ReadinessInputs ReadInputs(string? gameExePath, Monitoring.SensorFrame? frame)
    {
        double? V(string id) => frame?.Values.GetValueOrDefault(id);
        double? gpuTemp = null, vram = null, vramTotal = null;
        for (int g = 0; g < 8; g++)
        {
            gpuTemp ??= V($"gpu{g}.temp");
            if (V($"gpu{g}.vram") is { } u && vram is null)
            {
                vram = u / 1024;
                vramTotal = frame!.Flags.TryGetValue($"gpu{g}.vramtotal", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var tv) ? tv / 1024 : null;
            }
        }
        var (total, avail, _) = MemoryInfo.Status();
        string? drive = null; double? free = null;
        try
        {
            if (gameExePath is { Length: > 2 } p && p[1] == ':')
            {
                var di = new DriveInfo(p[..1]);
                drive = di.Name.TrimEnd('\\'); free = di.AvailableFreeSpace / 1073741824.0;
            }
        }
        catch { }
        var a = Advisor.GameCapture.ProcessTimes();
        Thread.Sleep(1000);
        var b = Advisor.GameCapture.ProcessTimes();
        double bg = 0; string? top = null; double topPct = 0;
        foreach (var (pid, (name, cpu)) in b)
        {
            if (!a.TryGetValue(pid, out var prev) || name is "Idle" or "System" || gameExePath is not null && name.Equals(Path.GetFileNameWithoutExtension(gameExePath), StringComparison.OrdinalIgnoreCase)) continue;
            double pct = (cpu - prev.cpu).TotalMilliseconds / (1000.0 * Environment.ProcessorCount) * 100;
            bg += pct;
            if (pct > topPct) { topPct = pct; top = name; }
        }
        DateTime? driverDate = null; string? gpuName = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverDate, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                var n = o["Name"]?.ToString();
                if (n is null || n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                if (o["DriverDate"]?.ToString() is { Length: >= 8 } dd && DateTime.TryParseExact(dd[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    if (gpuName is null || n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)) { gpuName = n; driverDate = dt; }
            }
        }
        catch (Exception ex) { Log.Debug("readiness: driver date: " + ex.Message); }
        return new(V("cpu.temp"), gpuTemp, avail / 1073741824.0, total / 1073741824.0, vram, vramTotal, free, drive, top is null ? null : $"{top} {topPct:0}%", Math.Max(0, bg), driverDate, gpuName);
    }

    /// <summary>Whether Windows logged the game crashing in the last few minutes (Application Error 1000 or WER for its exe).</summary>
    public static bool CrashedRecently(string? exe, TimeSpan window)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var q = new EventLogQuery("Application", PathType.LogName,
                $"*[System[(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)][TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]");
            using var r = new EventLogReader(q);
            for (var e = r.ReadEvent(); e is not null; e = r.ReadEvent())
                using (e)
                    if (e.Properties.Count > 0 && e.Properties[0].Value?.ToString() is { } app && app.Equals(exe, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) { Log.Debug("autopilot crash check: " + ex.Message); }
        return false;
    }

    /// <summary>Whether a process with this id still runs (anti-cheat-protected games included).</summary>
    public static bool Alive(int pid) => pid > 0 && Advisor.GameCapture.StillRunning(pid);
}
