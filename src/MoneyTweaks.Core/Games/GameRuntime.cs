using System.Diagnostics;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Games;

/// <summary>
/// Shared runtime for the library: launcher data and installs are cached (launcher scans touch the disk), process checks
/// are always live. Used by the Game Library page, profile auto-activation and the overlay/benchmark game checks.
/// </summary>
public static class GameRuntime
{
    private static readonly object Gate = new();
    private static LauncherData? _data;
    private static readonly Dictionary<string, InstallResult> Installs = [];
    private static readonly HashSet<int> Raised = [];

    public static LauncherData Data(bool refresh = false)
    {
        lock (Gate)
        {
            if (refresh || _data is null || DateTime.Now - _data.ScannedAt > TimeSpan.FromMinutes(10))
            {
                _data = LauncherData.Scan();
                Installs.Clear();
            }
            return _data;
        }
    }

    /// <summary>Forgets cached installs (after adding a game, changing its exe, or Refresh).</summary>
    public static void Invalidate()
    {
        lock (Gate) { _data = null; Installs.Clear(); }
    }

    public static InstallResult InstallFor(LibraryGame g)
    {
        var d = Data();
        lock (Gate)
        {
            if (Installs.TryGetValue(g.Key + "|" + g.Entry?.ExePath, out var r)) return r;
        }
        var res = GameLibrary.Resolve(g, d);
        lock (Gate) Installs[g.Key + "|" + g.Entry?.ExePath] = res;
        return res;
    }

    /// <summary>Install, process match and status for each game, with one process snapshot for all of them.</summary>
    public static List<(LibraryGame game, InstallResult install, GameMatch match, GameStatus status)> Statuses(IReadOnlyList<LibraryGame> games)
    {
        var (names, titles) = GameDetector.Interesting(games.Select(g => g.Def));
        var procs = GameDetector.Snapshot(names, titles);
        return games.Select(g =>
        {
            var i = InstallFor(g);
            var m = GameDetector.Match(g.Def, i.Install, procs);
            return (g, i, m, GameDetector.StatusOf(i, m));
        }).ToList();
    }

    public static DetectionReport Test(LibraryGame g)
    {
        Invalidate();
        var i = InstallFor(g);
        var (names, titles) = GameDetector.Interesting([g.Def]);
        return GameDetector.Report(g.Def, i, GameDetector.Snapshot(names, titles));
    }

    public static ProcInfo? Running(string key)
    {
        if (GameLibrary.Find(GameLibrary.Current, key) is not { } g) return null;
        var (names, titles) = GameDetector.Interesting([g.Def]);
        return GameDetector.Match(g.Def, InstallFor(g).Install, GameDetector.Snapshot(names, titles)).Process;
    }

    /// <summary>Library games set to auto-activate a profile or raise priority, so the profile watcher must run.</summary>
    public static bool NeedsWatcher(LibraryFile f) => f.Entries.Any(e => (e.AutoActivate && e.ProfileId is not null) || e.RaisePriority);

    /// <summary>Running library games that have automation configured (auto profile or priority).</summary>
    public static List<(LibraryGame game, ProcInfo proc)> RunningAutomated(LibraryFile f)
    {
        var games = GameLibrary.Items(f).Where(g => g.Entry is { } e && ((e.AutoActivate && e.ProfileId is not null) || e.RaisePriority)).ToList();
        if (games.Count == 0) return [];
        return Statuses(games).Where(s => s.match.Process is not null).Select(s => (s.game, s.match.Process!)).ToList();
    }

    /// <summary>Raises verified game processes to High priority (runtime only; it ends with the process). Never lowers anything.</summary>
    public static void RaisePriorities(IEnumerable<(LibraryGame game, ProcInfo proc)> running)
    {
        foreach (var (g, p) in running.Where(x => x.game.Entry?.RaisePriority == true))
        {
            lock (Gate) if (!Raised.Add(p.Pid)) continue;
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                if (proc.PriorityClass == ProcessPriorityClass.Normal)
                {
                    proc.PriorityClass = ProcessPriorityClass.High;
                    Log.Info($"{g.Def.Name} (pid {p.Pid}) raised to High priority");
                }
            }
            catch (Exception ex) { Log.Debug($"priority {g.Def.Name}: {ex.Message}"); }
        }
    }
}
