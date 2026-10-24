using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>
/// Run by the uninstaller (MoneyTweaks.exe --uninstall-restore) before any file is removed: closes a running copy, then
/// undoes everything Money Tweaks changed (<see cref="UninstallRestore"/>), with a progress window and a summary.
/// Administrator rights are asked for once, and only if something system-wide is still applied; the elevated copy
/// (--uninstall-restore-admin) runs without a window on the same data folder and reports back through a file.
/// With --quiet (silent uninstall) there is no window, but the elevation prompt still appears when it's needed.
/// </summary>
public static class UninstallMode
{
    public const string Arg = "--uninstall-restore", AdminArg = "--uninstall-restore-admin";

    public static bool Handles(string[] args) => args.Any(a => a.Equals(Arg, StringComparison.OrdinalIgnoreCase) || a.Equals(AdminArg, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Run(App app, string[] args)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (args.Contains(AdminArg, StringComparer.OrdinalIgnoreCase)) { RunAdminPass(args); app.Shutdown(); return; }
        bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        // Tests only (the installer passes neither): a scratch data folder, and leave other running copies alone.
        if (Value(args, "--data") is { Length: > 0 } data && Directory.Exists(data)) AppPaths.OverrideRoot(data);
        Log.Info($"Uninstall restore started (admin: {Admin.IsElevated}, quiet: {quiet}, data: {AppPaths.Root})");
        if (!args.Contains("--keep-running", StringComparer.OrdinalIgnoreCase)) CloseRunningCopy();
        if (quiet)
        {
            var report = RunAll(null);
            Log.Info($"Uninstall restore finished: {report.Restored} restored, {report.Remaining.Count} left");
            app.Shutdown();
            return;
        }
        var win = new UninstallWindow();
        win.Closed += (_, _) => app.Shutdown();
        win.Show();
        win.Start();
    }

    /// <summary>Both passes; the elevated one only when something system-wide is left.</summary>
    internal static RestoreReport RunAll(IProgress<string>? progress)
    {
        var report = UninstallRestore.Run(adminPass: false, progress);
        if (!UninstallRestore.NeedsAdminPass(report) || Admin.IsElevated) return report;
        progress?.Report("Some changes are system-wide. Windows will ask for administrator permission to undo them…");
        var file = Path.Combine(Path.GetTempPath(), $"moneytweaks-uninstall-{Guid.NewGuid():N}.json");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{AdminArg} --data \"{AppPaths.Root}\" --report \"{file}\"") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit();
            if (File.Exists(file) && RestoreReport.FromJson(File.ReadAllText(file)) is { } admin) report.Merge(admin);
            else Log.Warn("Uninstall restore: the administrator pass didn't report back");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Elevation declined: those changes stay, with their backups kept for a later reinstall.
            Log.Info("Uninstall restore: administrator permission declined");
            foreach (var i in report.Items.Where(i => i.NeedsAdmin).ToList())
            {
                report.Items.Remove(i);
                report.Items.Add(i with { Message = "Not undone: administrator permission was declined. Its backup is kept, so reinstalling Money Tweaks can still undo it." });
            }
        }
        finally { try { File.Delete(file); } catch { } }
        return report;
    }

    private static void RunAdminPass(string[] args)
    {
        if (Value(args, "--data") is { Length: > 0 } data && Directory.Exists(data)) AppPaths.OverrideRoot(data);
        var report = UninstallRestore.Run(adminPass: true);
        if (Value(args, "--report") is { Length: > 0 } file)
            try { File.WriteAllText(file, report.ToJson()); } catch (Exception ex) { Log.Warn("Uninstall restore: couldn't write the report: " + ex.Message); }
    }

    /// <summary>
    /// Asks a running Money Tweaks to exit normally (its shutdown turns off an active gaming profile and stops its
    /// watchers, so nothing re-applies while undoing). A copy that doesn't answer is closed through its window, then ended.
    /// </summary>
    private static void CloseRunningCopy()
    {
        bool signalled = App.SignalExit();
        var others = Process.GetProcessesByName("MoneyTweaks").Where(p => p.Id != Environment.ProcessId).ToList();
        try
        {
            if (others.Count == 0) return;
            Log.Info($"Uninstall restore: closing {others.Count} running Money Tweaks ({(signalled ? "asked to exit" : "no exit signal")})");
            foreach (var p in others) if (!p.WaitForExit(signalled ? 15_000 : 0)) { try { p.CloseMainWindow(); } catch { } }
            foreach (var p in others)
                if (!p.WaitForExit(5_000))
                {
                    try { p.Kill(); p.WaitForExit(5_000); Log.Warn($"Uninstall restore: ended Money Tweaks process {p.Id} that didn't close"); }
                    catch (Exception ex) { Log.Warn($"Uninstall restore: couldn't close process {p.Id}: {ex.Message}"); }
                }
        }
        finally { foreach (var p in others) p.Dispose(); }
    }
}

/// <summary>Progress and the result of the uninstall restore.</summary>
public sealed class UninstallWindow : Window
{
    private readonly TextBlock _status = Ui.Muted("Starting…", 13);
    private readonly StackPanel _result = new();
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button _close;

    public UninstallWindow()
    {
        Title = "Removing Money Tweaks";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("Surface"); Foreground = Ui.Brush("Text"); FontFamily = Ui.Res<FontFamily>("UiFont");
        SourceInitialized += (_, _) => Motion.SquareCorners(this);
        var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        sp.Children.Add(new TextBlock { Text = "UNINSTALL", Style = Ui.Res<Style>("Eyebrow") });
        sp.Children.Add(new TextBlock { Text = "Undoing Money Tweaks' changes", FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("HeadingFont"), Margin = new Thickness(0, 2, 0, 6) });
        sp.Children.Add(new TextBlock { Text = "Every tweak, profile, tuning and game setting Money Tweaks applied goes back to the value backed up before it was changed. This takes a few seconds.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Muted"), FontSize = 12.5 });
        sp.Children.Add(_bar);
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(_status);
        sp.Children.Add(_result);
        _close = Ui.Btn("Close", (_, _) => Close(), "PrimaryButton", new Thickness(0, 16, 0, 0));
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.Visibility = Visibility.Collapsed;
        sp.Children.Add(_close);
        Content = new Border { Background = Ui.Brush("Surface"), Child = sp };
    }

    public async void Start()
    {
        var progress = new Progress<string>(s => _status.Text = s);
        RestoreReport report;
        try { report = await Task.Run(() => UninstallMode.RunAll(progress)); }
        catch (Exception ex)
        {
            Log.Error("Uninstall restore failed", ex);
            _bar.Visibility = Visibility.Collapsed;
            _status.Text = "Undoing stopped because of an error: " + ex.Message + "\nYour backups are kept in " + AppPaths.Backups + ".";
            _close.Visibility = Visibility.Visible;
            return;
        }
        _bar.Visibility = Visibility.Collapsed;
        var left = report.Remaining;
        _status.Text = report.Items.Count == 0 ? "Nothing to undo: Money Tweaks had no changes applied."
            : left.Count == 0 ? $"All {report.Restored} changes were undone and checked."
            : $"{report.Restored} changes undone; {left.Count} couldn't be:";
        foreach (var i in left.Take(12))
            _result.Children.Add(new TextBlock { Text = $"• {i.Area}: {i.Name}. {i.Message}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Brush("Gold"), Margin = new Thickness(0, 4, 0, 0) });
        if (left.Count > 12) _result.Children.Add(Ui.Muted($"…and {left.Count - 12} more (see the log in {AppPaths.Logs}).", 12));
        if (left.Count > 0) _result.Children.Add(Ui.Muted($"Their backups are kept in {AppPaths.Root}, so reinstalling Money Tweaks can still undo them.", 12));
        if (report.RestartRecommended) _result.Children.Add(new TextBlock { Text = "Restart Windows to finish.", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        _close.Visibility = Visibility.Visible;
        _close.Focus();
    }
}
