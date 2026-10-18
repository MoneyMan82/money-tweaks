using System.Diagnostics;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

public enum LaunchOutcome { AlreadyRunning, Started, NotInstalled, NeedsAdmin, Declined, StartFailed, TimedOut }

/// <summary>
/// Starts MSI Afterburner when its controls are used and it isn't running. Never called at Money Tweaks startup. Asks
/// first unless the user chose "Don't ask again". Afterburner runs as administrator, so Money Tweaks must be elevated
/// to start it without a prompt and to talk to it at all.
/// </summary>
public static class AfterburnerLauncher
{
    internal static Func<bool> IsRunning { get; set; } = () => Afterburner.IsRunning;
    internal static Func<bool> IsElevated { get; set; } = () => Admin.IsElevated;
    internal static Func<string?> FindExe { get; set; } = DefaultFindExe;
    internal static Func<string, bool> Start { get; set; } = DefaultStart;
    internal static Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

    /// <summary><paramref name="ask"/> shows the confirmation and returns (start?, don't ask again?); it isn't called when
    /// <paramref name="autoStart"/> is already on. <paramref name="rememberChoice"/> saves "don't ask again".</summary>
    public static async Task<(LaunchOutcome outcome, string message)> EnsureRunningAsync(bool autoStart, Func<(bool start, bool dontAskAgain)> ask,
        Action<bool> rememberChoice, TimeSpan? timeout = null)
    {
        if (IsRunning()) return (LaunchOutcome.AlreadyRunning, "MSI Afterburner is running.");
        if (!IsElevated()) return (LaunchOutcome.NeedsAdmin, "MSI Afterburner runs as administrator, so Money Tweaks must run as administrator to start and control it.");
        if (FindExe() is not { } exe) return (LaunchOutcome.NotInstalled, "MSI Afterburner isn't installed.");
        if (!autoStart)
        {
            var (start, dontAsk) = ask();
            if (!start) return (LaunchOutcome.Declined, "MSI Afterburner wasn't started.");
            if (dontAsk) rememberChoice(true);
        }
        if (!Start(exe)) return (LaunchOutcome.StartFailed, $"Windows couldn't start {exe}.");
        // Afterburner creates its control memory after it has initialized its hardware layer (a few seconds).
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < end)
        {
            await Delay(TimeSpan.FromMilliseconds(500));
            if (IsRunning()) { Log.Info("MSI Afterburner started by Money Tweaks"); return (LaunchOutcome.Started, "MSI Afterburner started."); }
        }
        return (LaunchOutcome.TimedOut, "MSI Afterburner started, but its control interface didn't appear within 20 seconds. Check it in the tray and press Check again.");
    }

    private static string? DefaultFindExe()
    {
        var known = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"MSI Afterburner\MSIAfterburner.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"MSI Afterburner\MSIAfterburner.exe"),
        };
        if (known.FirstOrDefault(File.Exists) is { } k) return k;
        // Installed elsewhere: the inventory has the uninstall entry's folder.
        var dir = TuningDetector.Cached()?.Tool("MSI Afterburner")?.Path;
        if (dir is null) return null;
        if (dir.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return File.Exists(dir) ? dir : null;
        var exe = Path.Combine(dir, "MSIAfterburner.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static bool DefaultStart(string exe)
    {
        try
        {
            // Minimized and without stealing focus; Afterburner may still show itself if its own settings say so.
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return p is not null || true; // ShellExecute may hand off and return null while the app still starts
        }
        catch (Exception ex) { Log.Warn("start Afterburner: " + ex.Message); return false; }
    }
}
