using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Display;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>Resolution changes with automatic rollback: temporary until confirmed, reverted on timeout, exit or crash recovery.</summary>
public static class DisplayService
{
    public static DisplayState State { get; } = DisplayState.Load();

    public static void Save()
    {
        try { State.Save(); } catch (Exception ex) { Log.Error("Saving display-state.json failed", ex); }
    }

    /// <summary>
    /// Applies a mode temporarily and shows a countdown. Keep = saved (unless <paramref name="previewOnly"/>);
    /// timeout, Esc, closing the dialog or "Revert" = back to the previous mode. Returns the final outcome text.
    /// </summary>
    public static async Task<(bool kept, string message)> TryModeAsync(DisplayInfo d, int w, int h, int hz, ScalingMode scaling, bool previewOnly)
    {
        var test = await Task.Run(() => DisplayManager.Test(d.DeviceName, w, h, hz, scaling));
        if (test != ChangeResult.Ok) return (false, DisplayManager.Explain(test, w, h, hz));
        State.RememberOriginal(d);
        State.PendingTemporaryDevice = d.DeviceName; // written before the change: crash recovery
        Save();
        var r = DisplayManager.ApplyTemporary(d.DeviceName, w, h, hz, scaling);
        if (r != ChangeResult.Ok) { ClearPending(); return (false, DisplayManager.Explain(r, w, h, hz)); }
        bool keep = Countdown.Ask(previewOnly ? $"Previewing {w}×{h}" : $"Keep {w}×{h}?",
            previewOnly ? "This is a test. The previous resolution comes back automatically." : "If the screen looks wrong or you can't see this, just wait: the previous resolution comes back automatically.",
            previewOnly ? 10 : 15, previewOnly ? null : "Keep this resolution", "Revert now");
        if (keep && !previewOnly)
        {
            var c = DisplayManager.Commit(d.DeviceName);
            ClearPending();
            return c == ChangeResult.Ok ? (true, $"{w}×{h} applied and saved for {d.MonitorName}.") : (false, "Applied for this session, but Windows couldn't save it: " + DisplayManager.Explain(c, w, h, hz));
        }
        var back = DisplayManager.RevertToSaved(d.DeviceName);
        ClearPending();
        return (false, back == ChangeResult.Ok ? (previewOnly ? "Test finished. Previous resolution restored." : "Reverted to the previous resolution.") : "Couldn't revert automatically: " + DisplayManager.Explain(back, w, h, hz) + " Use 'Restore original'.");
    }

    private static void ClearPending() { State.PendingTemporaryDevice = null; Save(); }

    /// <summary>Puts a display back to the mode it had before Money Tweaks first changed it.</summary>
    public static string RestoreOriginal(DisplayInfo d)
    {
        if (!State.Originals.TryGetValue(d.DeviceName, out var o)) return "Money Tweaks hasn't changed this display, so there's nothing to restore.";
        var r = DisplayManager.ApplyAndSave(d.DeviceName, o.Mode, o.Scaling);
        if (r != ChangeResult.Ok) return "Couldn't restore: " + DisplayManager.Explain(r, o.Width, o.Height, o.Hz);
        State.Originals.Remove(d.DeviceName);
        Save();
        return $"Restored {o.Width}×{o.Height} @ {o.Hz} Hz for {d.MonitorName}.";
    }

    /// <summary>At startup: a test was interrupted (crash/kill). Never changes anything without asking.</summary>
    public static void CheckPending()
    {
        if (State.PendingTemporaryDevice is not { } dev) return;
        var d = DisplayManager.Find(dev);
        if (d is null || (d.Current.Width == d.Registry.Width && d.Current.Height == d.Registry.Height && d.Current.Hz == d.Registry.Hz)) { ClearPending(); return; }
        if (Dialog.Confirm("Resolution test was interrupted",
                $"{d.MonitorName} is still at the test resolution {d.Current} (saved resolution: {d.Registry}). Go back to the saved resolution?", "Go back", DialogKind.Warning))
            DisplayManager.RevertToSaved(dev);
        ClearPending();
    }

    /// <summary>On exit: drop any unconfirmed temporary mode.</summary>
    public static void Shutdown()
    {
        if (State.PendingTemporaryDevice is { } dev) { DisplayManager.RevertToSaved(dev); ClearPending(); }
    }
}

/// <summary>Themed countdown confirmation. Returns true only if the user clicks the keep button in time.</summary>
public static class Countdown
{
    public static bool Ask(string title, string message, int seconds, string? keepLabel, string revertLabel)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Foreground = Ui.Brush("Text"),
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = 440, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Title = title,
        };
        bool keep = false;
        int left = seconds;
        var count = new TextBlock { FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 44, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Center };
        var bar = new ProgressBar { Maximum = seconds, Value = seconds, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("H2"), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = message, Style = Ui.Res<Style>("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        sp.Children.Add(count);
        sp.Children.Add(bar);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var revert = new Button { Content = revertLabel, MinWidth = 110, IsCancel = true };
        revert.Click += (_, _) => win.Close();
        buttons.Children.Add(revert);
        if (keepLabel is not null)
        {
            var k = new Button { Content = keepLabel, MinWidth = 140, Style = Ui.Res<Style>("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
            k.Click += (_, _) => { keep = true; win.Close(); };
            buttons.Children.Add(k);
        }
        sp.Children.Add(buttons);
        win.Content = new Border { Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(12), Child = sp };
        void Show() { count.Text = left.ToString(System.Globalization.CultureInfo.CurrentCulture); bar.Value = left; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { left--; Show(); if (left <= 0) win.Close(); };
        win.Loaded += (_, _) => { Show(); timer.Start(); win.Activate(); };
        win.Closed += (_, _) => timer.Stop();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
        return keep;
    }
}
