using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MoneyTweaks.Core.Games;

public enum GameStatus { Running, Installed, NotInstalled, NotDetected }

/// <summary>A running process: exe name, full image path (null if Windows won't tell) and main-window title when asked for.</summary>
public sealed record ProcInfo(int Pid, string Exe, string? Path, string? Title);

public sealed record GameMatch(ProcInfo? Process, List<string> Notes);

public sealed record DetectionReport(GameStatus Status, GameInstall? Install, ProcInfo? Process, List<string> Lines);

/// <summary>
/// Running-game detection by process identity: exe name plus the real image path (and window title where a game shares
/// a generic exe like javaw.exe). Only PROCESS_QUERY_LIMITED_INFORMATION is used, the same right Task Manager uses; no
/// game memory is read and nothing is injected.
/// </summary>
public static class GameDetector
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(nint h, int flags, StringBuilder name, ref int size);

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Processes whose exe name is in <paramref name="names"/> (all if null). Titles only for <paramref name="titleFor"/>.</summary>
    public static List<ProcInfo> Snapshot(ISet<string>? names = null, ISet<string>? titleFor = null)
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string exe;
                try { exe = p.ProcessName + ".exe"; } catch { continue; }
                if (names is not null && !names.Contains(exe)) continue;
                string? title = null;
                if (titleFor?.Contains(exe) == true) { try { title = p.MainWindowTitle; } catch { } }
                list.Add(new ProcInfo(p.Id, exe, ImagePath(p.Id), title));
            }
        }
        return list;
    }

    /// <summary>The exe names (and title-checked exes) to snapshot for these games.</summary>
    public static (HashSet<string> names, HashSet<string> titles) Interesting(IEnumerable<GameDef> games)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
        {
            foreach (var r in g.Processes) { names.Add(r.Exe); if (r.TitlePrefix is not null) titles.Add(r.Exe); }
            foreach (var n in g.NotThisGame) names.Add(n);
        }
        return (names, titles);
    }

    private static string Dir(string d) => d.TrimEnd('\\', '/') + "\\";

    /// <summary>
    /// Matches a game against running processes. Rules with a path/title identity must satisfy it; other rules must run
    /// from the known install folder (or the chosen exe) when one is known. Every accept or reject is explained.
    /// </summary>
    public static GameMatch Match(GameDef def, GameInstall? install, IReadOnlyList<ProcInfo> procs)
    {
        var notes = new List<string>();
        ProcInfo? hit = null;
        foreach (var rule in def.Processes)
        {
            foreach (var p in procs.Where(p => p.Exe.Equals(rule.Exe, StringComparison.OrdinalIgnoreCase)))
            {
                string where = p.Path ?? "path not readable";
                if (rule.HasIdentityRule)
                {
                    bool pathOk = p.Path is not null && rule.PathAny?.Any(f => p.Path.Contains(f, StringComparison.OrdinalIgnoreCase)) == true;
                    bool titleOk = rule.TitlePrefix is not null && p.Title?.StartsWith(rule.TitlePrefix, StringComparison.OrdinalIgnoreCase) == true;
                    if (pathOk || titleOk) { notes.Add($"✔ {p.Exe} (pid {p.Pid}) {where}{(titleOk ? $" · window \"{p.Title}\"" : "")}"); hit ??= p; }
                    else notes.Add($"✘ {p.Exe} (pid {p.Pid}) {where}: not {def.Name} (no matching folder{(rule.TitlePrefix is null ? "" : $" or \"{rule.TitlePrefix}…\" window")})");
                    continue;
                }
                if (install is not null && p.Path is not null)
                {
                    bool inFolder = install.InstallDir.Length > 0 && Directory.Exists(install.InstallDir) && p.Path.StartsWith(Dir(install.InstallDir), StringComparison.OrdinalIgnoreCase);
                    bool sameExe = install.ExePath is not null && p.Path.Equals(install.ExePath, StringComparison.OrdinalIgnoreCase);
                    if (inFolder || sameExe) { notes.Add($"✔ {p.Exe} (pid {p.Pid}) {p.Path}"); hit ??= p; }
                    else notes.Add($"✘ {p.Exe} (pid {p.Pid}) runs from {p.Path}, not from {def.Name}'s folder {install.InstallDir}");
                    continue;
                }
                notes.Add($"✔ {p.Exe} (pid {p.Pid}) {where}{(install is null ? " · matched by name (install folder unknown)" : " · matched by name (Windows didn't give the path)")}");
                hit ??= p;
            }
        }
        foreach (var p in procs.Where(p => def.NotThisGame.Contains(p.Exe, StringComparer.OrdinalIgnoreCase)))
            notes.Add($"✘ {p.Exe} (pid {p.Pid}) is running but isn't {def.Name}{(p.Exe.Contains("Studio", StringComparison.OrdinalIgnoreCase) ? " (Roblox Studio)" : "")}");
        return new GameMatch(hit, notes);
    }

    /// <summary>
    /// Running if a verified process exists; Installed if an install with the exe was found; Not installed only if every
    /// definitive source could be checked and none had it; otherwise Not detected (couldn't check).
    /// </summary>
    public static GameStatus StatusOf(InstallResult r, GameMatch m)
    {
        if (m.Process is not null) return GameStatus.Running;
        if (r.Install is not null) return GameStatus.Installed;
        var definitive = r.Checks.Where(c => c.Definitive).ToList();
        if (definitive.Count > 0 && definitive.All(c => c.Available)) return GameStatus.NotInstalled;
        return GameStatus.NotDetected;
    }

    public static string Label(GameStatus s) => s switch
    {
        GameStatus.Running => "Running",
        GameStatus.Installed => "Installed",
        GameStatus.NotInstalled => "Not installed",
        _ => "Not detected",
    };

    public static DetectionReport Report(GameDef def, InstallResult r, IReadOnlyList<ProcInfo> procs)
    {
        var m = Match(def, r.Install, procs);
        var status = StatusOf(r, m);
        var lines = new List<string> { $"{def.Name}: {Label(status)}" };
        if (r.Install is { } i)
        {
            lines.Add($"Install ({i.Source}): {i.InstallDir}");
            lines.Add(i.ExePath is not null ? $"Executable: {i.ExePath}" : "Executable: not a single file (launched through its launcher)");
        }
        lines.Add("Looked for: " + string.Join(", ", def.Processes.Select(p => p.Exe + (p.HasIdentityRule ? " (with folder/window check)" : ""))));
        foreach (var c in r.Checks) lines.Add($"{(c.Found ? "✔" : c.Available ? "–" : "?")} {c.Source}: {c.Detail}");
        if (m.Notes.Count == 0) lines.Add("No matching process is running.");
        lines.AddRange(m.Notes);
        if (status == GameStatus.NotDetected) lines.Add("Not detected: a launcher it can come from isn't installed or readable here, so Money Tweaks can't tell. Point it at the game's exe or folder if it's installed.");
        return new DetectionReport(status, r.Install, m.Process, lines);
    }

    /// <summary>The first running exe from a list, using catalogue identity rules where the exe needs them (javaw.exe ≠ Minecraft).</summary>
    public static (string exe, int pid)? FirstRunning(IEnumerable<string> exes)
    {
        var wanted = exes.Select(e => Path.GetFileName(e)).ToList();
        var defs = wanted.SelectMany(GameCatalog.ByExe).Distinct().ToList();
        var titles = new HashSet<string>(defs.SelectMany(d => d.Processes).Where(r => r.TitlePrefix is not null).Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
        var procs = Snapshot(new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase), titles);
        foreach (var exe in wanted)
            foreach (var p in procs.Where(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            {
                var rules = defs.SelectMany(d => d.Processes).Where(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) && r.HasIdentityRule).ToList();
                if (rules.Count == 0 || rules.Any(r => Match(new GameDef { Id = "x", Name = exe, Processes = [r] }, null, [p]).Process is not null))
                    return (exe, p.Pid);
            }
        return null;
    }
}
