using System.Diagnostics;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed record ProcessInfo(int Pid, string Name, double WorkingSetMb, string? Title);

/// <summary>
/// One-click session boost: switches to a high-performance power plan, closes the background apps you picked,
/// raises the game's priority and clears the standby memory list. "End boost" restores the power plan.
/// </summary>
public sealed class GameBooster
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private string? _previousScheme;
    public bool Active { get; private set; }

    /// <summary>Processes that must never be offered for closing.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "fontdrvhost",
        "Registry", "Memory Compression", "spoolsv", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "RuntimeBroker",
        "sihost", "ctfmon", "taskhostw", "audiodg", "conhost", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        "MoneyTweaks", "TextInputHost", "LockApp", "WmiPrvSE", "dllhost", "smartscreen", "SgrmBroker", "WUDFHost", "atiesrxx", "atieclxx",
        "nvcontainer", "NVDisplay.Container", "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "Lghub_agent"
    };

    public static IReadOnlyList<ProcessInfo> ClosableProcesses()
    {
        var self = Environment.ProcessId;
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.SessionId == 0 || Protected.Contains(p.ProcessName)) continue;
                list.Add(new ProcessInfo(p.Id, p.ProcessName, p.WorkingSet64 / 1048576.0, string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle));
            }
            catch { }
            finally { p.Dispose(); }
        }
        // One row per process name, summing memory.
        return list.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessInfo(g.First().Pid, g.Key, g.Sum(x => x.WorkingSetMb), g.Select(x => x.Title).FirstOrDefault(t => t is not null)))
            .OrderByDescending(x => x.WorkingSetMb).ToList();
    }

    /// <summary>Process names that may be closed: distinct, never protected, never the game being boosted.</summary>
    public static IReadOnlyList<string> CloseTargets(IEnumerable<string> closeNames, string? gameProcessName) =>
        closeNames.Select(n => Path.GetFileNameWithoutExtension(n.Trim()))
            .Where(n => n.Length > 0 && !Protected.Contains(n) && !string.Equals(n, gameProcessName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? ActiveScheme()
    {
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/getactivescheme");
        var m = Regex.Match(r.StdOut, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    public List<string> Start(IEnumerable<string> closeNames, string? gameProcess, bool purgeStandby)
    {
        var report = new List<string>();
        _previousScheme = ActiveScheme();
        var ultimate = "99999999-9999-9999-9999-999999999999";
        var target = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/list").StdOut.Contains(ultimate) ? ultimate : HighPerformance;
        var set = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/setactive " + target);
        report.Add(set.Ok ? $"Power plan → {(target == ultimate ? "Ultimate Performance" : "High performance")}" : "Power plan: High performance is not available on this PC (Modern Standby devices hide it).");

        var session = Process.GetCurrentProcess().SessionId;
        var game = string.IsNullOrWhiteSpace(gameProcess) ? null : Path.GetFileNameWithoutExtension(gameProcess);
        foreach (var name in CloseTargets(closeNames, game))
        {
            int closed = 0;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    // Only this user's own processes (an elevated copy could otherwise end other users' apps).
                    if (p.SessionId != session) continue;
                    // Ask nicely first so apps can save; force-kill only if they ignore it.
                    if (p.MainWindowHandle != 0 && p.CloseMainWindow() && p.WaitForExit(2500)) { closed++; continue; }
                    // Only this process, never its tree: a launcher's children include the game it started.
                    p.Kill(entireProcessTree: false);
                    closed++;
                }
                catch (Exception ex) { report.Add($"Could not close {name}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
            if (closed > 0) report.Add($"Closed {name} ({closed})");
        }

        if (!string.IsNullOrWhiteSpace(gameProcess))
        {
            var n = Path.GetFileNameWithoutExtension(gameProcess);
            var procs = Process.GetProcessesByName(n);
            foreach (var p in procs)
            {
                try { p.PriorityClass = ProcessPriorityClass.High; report.Add($"{n} → High priority"); }
                catch (Exception ex) { report.Add($"Could not raise {n}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
            if (procs.Length == 0) report.Add($"{n} is not running. Start the game first, then boost.");
        }

        if (purgeStandby)
        {
            var (_, msg) = MemoryInfo.PurgeStandbyList();
            report.Add("Memory: " + msg);
        }
        Active = true;
        foreach (var l in report) Log.Info("Game Booster: " + l);
        return report;
    }

    public string End()
    {
        Active = false;
        if (_previousScheme is null) return "Boost ended.";
        ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/setactive " + _previousScheme);
        Log.Info("Game Booster ended; power plan restored.");
        return "Boost ended. Your previous power plan is restored. Closed apps can be reopened normally.";
    }
}
