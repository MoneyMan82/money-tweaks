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

    // ---- automatic switching while Fortnite runs -------------------------------------------------------------------
    public const string FortniteExe = "FortniteClient-Win64-Shipping.exe";
    private static CancellationTokenSource? _auto;
    private static string? _autoDevice;      // display currently switched by the watcher
    private static int _misses;
    private static bool _refusedThisLaunch;
    public static bool AutoActive => _autoDevice is not null;
    public static event Action? AutoChanged;

    public static DisplayInfo? AutoTarget(List<DisplayInfo>? displays = null)
    {
        displays ??= DisplayManager.Displays();
        var dev = State.AutoFortnite.Device;
        return displays.FirstOrDefault(d => d.DeviceName == dev) ?? displays.FirstOrDefault(d => d.Primary);
    }

    /// <summary>Refresh rate to use: the configured one, else the highest the driver lists for the size, else driver default.</summary>
    public static int AutoHz(DisplayInfo d)
    {
        var a = State.AutoFortnite;
        if (a.Hz > 0) return a.Hz;
        return d.RatesFor(a.Width, a.Height).FirstOrDefault();
    }

    public static void StartAutoSwitch()
    {
        if (_auto is not null || !State.AutoFortnite.Enabled) return;
        _auto = new CancellationTokenSource();
        var ct = _auto.Token;
        var ui = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct); } catch { break; }
                try { PollAuto(ui); } catch (Exception ex) { Log.Debug("auto resolution: " + ex.Message); }
            }
        });
        Log.Info($"Fortnite auto-resolution watcher started ({State.AutoFortnite.Width}×{State.AutoFortnite.Height})");
    }

    /// <summary>Stops watching Fortnite and puts a switched display back. False only if that revert failed.</summary>
    public static bool StopAutoSwitch()
    {
        _auto?.Cancel();
        _auto = null;
        if (_autoDevice is not { } dev) return true; // nothing was switched
        var r = DisplayManager.RevertToSaved(dev);
        if (r != ChangeResult.Ok) return false; // keep the crash-recovery record so it's offered again
        _autoDevice = null; ClearPending(); AutoChanged?.Invoke();
        return true;
    }

    private static void Toast(System.Windows.Threading.Dispatcher ui, string text, string tone) =>
        ui.BeginInvoke(() => MainWindow.Current?.ShowToast(text, tone));

    private static void PollAuto(System.Windows.Threading.Dispatcher ui)
    {
        var a = State.AutoFortnite;
        bool running = Core.Profiles.ProfileEngine.RunningGame([FortniteExe]) is not null;
        if (running && _autoDevice is null)
        {
            if (_refusedThisLaunch || !a.Enabled) return;
            var d = AutoTarget();
            if (d is null) return;
            if (d.Current.Width == a.Width && d.Current.Height == a.Height) return; // already there (e.g. set by hand)
            var hz = AutoHz(d);
            var test = DisplayManager.Test(d.DeviceName, a.Width, a.Height, hz, a.Scaling);
            if (test != ChangeResult.Ok)
            {
                _refusedThisLaunch = true;
                Log.Warn($"Fortnite auto-resolution: driver refused {a.Width}×{a.Height}@{hz}: {test}");
                Toast(ui, $"Fortnite started, but your driver refused {a.Width}×{a.Height}. Create it as a custom resolution in NVIDIA/AMD first (Fortnite Stretched tab).", "Gold");
                return;
            }
            State.RememberOriginal(d);
            State.PendingTemporaryDevice = d.DeviceName; // crash recovery, written before the change
            Save();
            var r = DisplayManager.ApplyTemporary(d.DeviceName, a.Width, a.Height, hz, a.Scaling);
            if (r != ChangeResult.Ok) { ClearPending(); _refusedThisLaunch = true; Toast(ui, "Couldn't switch resolution for Fortnite: " + DisplayManager.Explain(r, a.Width, a.Height, hz), "Gold"); return; }
            _autoDevice = d.DeviceName;
            _misses = 0;
            Log.Info($"Fortnite started: {d.DeviceName} switched to {a.Width}×{a.Height}@{hz} (temporary)");
            Toast(ui, $"Fortnite detected: {d.MonitorName} switched to {a.Width}×{a.Height}. It goes back to {d.Registry.Width}×{d.Registry.Height} when Fortnite closes.", "Success");
            ui.BeginInvoke(() => AutoChanged?.Invoke());
        }
        else if (!running)
        {
            _refusedThisLaunch = false;
            if (_autoDevice is not { } dev) return;
            if (++_misses < 2) return; // ~4 s without Fortnite (ignores the launcher's brief restarts)
            var back = DisplayManager.RevertToSaved(dev);
            _autoDevice = null;
            ClearPending();
            var d = DisplayManager.Find(dev);
            Log.Info($"Fortnite closed: {dev} reverted to saved mode -> {back}");
            Toast(ui, back == ChangeResult.Ok ? $"Fortnite closed: back to {d?.Current.Width}×{d?.Current.Height} @ {d?.Current.Hz} Hz." : "Fortnite closed, but the resolution couldn't be restored automatically. Use 'Restore original' in Stretched Resolution.", back == ChangeResult.Ok ? "Success" : "Danger");
            ui.BeginInvoke(() => AutoChanged?.Invoke());
        }
        else _misses = 0;
    }

    /// <summary>On exit: drop any unconfirmed temporary mode.</summary>
    public static void Shutdown()
    {
        _auto?.Cancel();
        _autoDevice = null;
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
