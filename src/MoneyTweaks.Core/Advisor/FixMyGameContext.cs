using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Display;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Advisor;

/// <summary>The game being diagnosed: from the Game Library (running or not) or the window that was in front.</summary>
public sealed record GameContext(string Key, string Name, int? Pid, string? ExePath, string? Version, string? InstallDir,
    string? ProfileId, string? ProfileName, bool ProfileActive, string? PotatoId)
{
    public bool Running => Pid is not null;
    public string? Exe => ExePath is null ? null : Path.GetFileName(ExePath);
}

/// <summary>Everything Fix My Game read about this PC besides the live measurement. Collected read-only.</summary>
public sealed class FixContext
{
    public GameContext? Game { get; init; }
    public List<Finding> Findings { get; init; } = [];
    public CrashReport? Crashes { get; init; }
    public List<DisplayInfo> Displays { get; init; } = [];
    public string? GpuName { get; init; }
    public string? GpuDriver { get; init; }
    public int WindowsBuild { get; init; } = OsInfo.Build;
    public bool Elevated { get; init; }
    public FixHistory History { get; init; } = new();
    public List<string> Unavailable { get; init; } = [];
}

/// <summary>Finds games, reads versions and profiles, and runs the read-only Quick Scan.</summary>
public static class FixMyGameScan
{
    /// <summary>Library games with whether they run now (and their verified process), running ones first.</summary>
    public static List<GameContext> Games()
    {
        var list = new List<GameContext>();
        try
        {
            var lib = GameLibrary.Current;
            var profiles = ProfileStore.All(ProfileStore.Load()).ToList();
            var active = ProfileStore.Load().Active;
            foreach (var (g, install, match, status) in GameRuntime.Statuses(GameLibrary.Items(lib)))
            {
                if (status is GameStatus.NotInstalled && match.Process is null) continue;
                var proc = match.Process;
                var exe = proc?.Path ?? install.Install?.ExePath ?? g.Entry?.ExePath;
                var profile = profiles.FirstOrDefault(p => p.Id == g.Entry?.ProfileId)
                    ?? profiles.FirstOrDefault(p => exe is not null && p.GameExes.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase));
                list.Add(new(g.Key, g.Def.Name, proc?.Pid, exe, Version(exe), install.Install?.InstallDir, profile?.Id, profile?.Name,
                    profile is not null && active?.ProfileId == profile.Id, PotatoFor(exe, g.Def.Name)));
            }
        }
        catch (Exception ex) { Log.Warn("Fix My Game: game list failed: " + ex.Message); }
        return [.. list.OrderByDescending(x => x.Running).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>A game that isn't in the library: the process the player switched to.</summary>
    public static GameContext? FromProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* protected process: name only */ }
            var name = path is not null && FileVersionInfo.GetVersionInfo(path).ProductName is { Length: > 0 } pn ? pn : p.ProcessName;
            return new("proc:" + p.ProcessName.ToLowerInvariant(), name, pid, path, Version(path), path is null ? null : Path.GetDirectoryName(path), null, null, false, PotatoFor(path, name));
        }
        catch { return null; }
    }

    /// <summary>File version of the game's exe ("product version" when the file has one), or null.</summary>
    public static string? Version(string? exe)
    {
        try
        {
            if (exe is null || !File.Exists(exe)) return null;
            var v = FileVersionInfo.GetVersionInfo(exe);
            var s = !string.IsNullOrWhiteSpace(v.ProductVersion) ? v.ProductVersion : v.FileVersion;
            return string.IsNullOrWhiteSpace(s) || s.Trim() is "0.0.0.0" or "1.0.0.0" ? null : s.Trim();
        }
        catch { return null; }
    }

    private static string? PotatoFor(string? exe, string name)
    {
        var proc = exe is null ? null : Path.GetFileNameWithoutExtension(exe);
        return PotatoGraphics.Games.FirstOrDefault(g => (proc is not null && g.ProcessNames.Contains(proc, StringComparer.OrdinalIgnoreCase)) || g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>
    /// Quick Scan: Smart Scanner's checks, Crash Detective (7 days), displays and the GPU driver, plus this game's
    /// history. Read-only; every source that can't be read is listed in <see cref="FixContext.Unavailable"/>.
    /// </summary>
    public static FixContext Quick(GameContext? game, bool crashes, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var unavailable = new List<string>();
        progress?.Report("settings");
        var findings = Try(() => OptimizationScanner.Analyze(OptimizationScanner.Collect()), [], "Windows settings check", unavailable);
        ct.ThrowIfCancellationRequested();
        progress?.Report("display");
        var displays = Try(DisplayManager.Displays, [], "display modes", unavailable);
        ct.ThrowIfCancellationRequested();
        progress?.Report("driver");
        var (gpu, driver) = Try(() =>
        {
            var inv = HardwareInventory.Read();
            var g = inv.Gpus.OrderByDescending(x => x.VramGb ?? 0).FirstOrDefault();
            return (g?.Name, g?.Driver);
        }, ((string?)null, (string?)null), "GPU driver", unavailable);
        ct.ThrowIfCancellationRequested();
        CrashReport? report = null;
        if (crashes)
        {
            progress?.Report("crashes");
            report = Try(() => CrashDetective.Analyze(CrashDetective.Collect(7), DateTime.Now), null, "crash logs", unavailable);
            if (report is not null) unavailable.AddRange(report.Unavailable.Select(u => "crash source: " + u));
        }
        progress?.Report("history");
        return new FixContext
        {
            Game = game, Findings = findings, Displays = displays, GpuName = gpu, GpuDriver = driver, Crashes = report,
            Elevated = Admin.IsElevated, History = FixHistory.Load(), Unavailable = unavailable,
        };
    }

    private static T Try<T>(Func<T> f, T fallback, string what, List<string> unavailable)
    {
        try { return f(); }
        catch (Exception ex) { Log.Warn($"Fix My Game: {what} unavailable: {ex.Message}"); unavailable.Add($"{what}: {ex.Message}"); return fallback; }
    }
}

// ---- personal PC knowledge ------------------------------------------------------------------------------------------------

/// <summary>One Fix My Game run for one game, kept locally (fixmygame.json) and never uploaded.</summary>
public sealed class FixSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime At { get; set; } = DateTime.Now;
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public string? GameVersion { get; set; }
    public GameProblem Problem { get; set; }
    public bool Deep { get; set; }
    public List<string> Findings { get; set; } = [];
    public Dictionary<string, double> Before { get; set; } = [];
    public Dictionary<string, double> After { get; set; } = [];
    public List<string> Applied { get; set; } = [];
    public List<string> Undone { get; set; } = [];
    public FixVerdict? Verdict { get; set; }
    public bool? StillAProblem { get; set; }
    public string? GpuDriver { get; set; }
    public int WindowsBuild { get; set; }
}

/// <summary>What happened the last time an action was tried for a game and problem.</summary>
public sealed record PastOutcome(DateTime At, FixVerdict? Verdict, bool Undone, string? Driver, string? GameVersion);

/// <summary>Per-game history: diagnoses, repairs and their measured outcome. Local file, at most 300 sessions.</summary>
public sealed class FixHistory
{
    public int Version { get; set; } = 1;
    public List<FixSession> Sessions { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath => Path.Combine(AppPaths.Root, "fixmygame.json");

    public static FixHistory Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<FixHistory>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex) { Log.Warn("fixmygame.json unreadable (kept as .bad): " + ex.Message); try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new();
    }

    /// <summary>Adds or replaces <paramref name="s"/> and saves atomically.</summary>
    public void Save(FixSession s)
    {
        Sessions.RemoveAll(x => x.Id == s.Id);
        Sessions.Add(s);
        if (Sessions.Count > 300) Sessions.RemoveRange(0, Sessions.Count - 300);
        try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Warn("fixmygame.json not saved: " + ex.Message); }
    }

    public IEnumerable<FixSession> For(string gameKey) => Sessions.Where(s => s.GameKey == gameKey).OrderByDescending(s => s.At);

    /// <summary>How often this problem was checked for this game in the last <paramref name="days"/> days (not counting now).</summary>
    public int Recurring(string gameKey, GameProblem p, int days = 30) => For(gameKey).Count(s => s.Problem == p && s.At > DateTime.Now.AddDays(-days));

    /// <summary>The most recent measured outcome of an action for this game and problem, if it was tried.</summary>
    public PastOutcome? Outcome(string gameKey, GameProblem p, string actionId) =>
        For(gameKey).Where(s => s.Problem == p && s.Applied.Contains(actionId))
            .Select(s => new PastOutcome(s.At, s.Verdict, s.Undone.Contains(actionId), s.GpuDriver, s.GameVersion)).FirstOrDefault();
}
