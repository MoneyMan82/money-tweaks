using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>Settings and status for the live performance overlay (runtime in <see cref="OverlayService"/>).</summary>
public sealed class OverlayView : UserControl, IPageLifecycle
{
    private static OverlaySettings S => App.Settings.Overlay;
    private readonly Button _toggle;
    private readonly StackPanel _status = new(), _editor = new(), _games = new();
    private readonly TextBlock _testResult = Ui.Muted("", 12);
    private readonly OverlayPanel _preview = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _editing; // null = default layout, else a game's exe
    private bool _testing;

    public OverlayView()
    {
        _toggle = Ui.Btn("Show overlay", (_, _) => OverlayService.Toggle(), "PrimaryButton");
        var (root, body) = Ui.Page("Performance Overlay",
            "A small always-on-top readout of FPS, frame time, CPU/GPU load, RAM and temperatures while you play. Frame timing uses the same Windows ETW Present events as PresentMon; nothing is injected into games. The overlay is click-through and never takes focus.",
            _toggle, "Tools", "\uE9D2");
        body.Children.Add(Ui.Card(_status));

        var look = Ui.Section("Layout", "What the overlay shows and where. Changes apply immediately.");
        look.Children.Add(_editor);
        var previewHost = new Border { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x3B, 0x52)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 12, 0, 0), Child = _preview };
        look.Children.Add(new TextBlock { Text = "PREVIEW", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 14, 0, 0) });
        look.Children.Add(previewHost);
        body.Children.Add(Ui.Card(look));

        body.Children.Add(Ui.Card(HotkeyCard()));
        var games = Ui.Section("Games", "Per-game profiles. With automatic monitoring on, the overlay appears when one of these starts and disappears (and frame timing stops) when it closes. A game can have its own layout.");
        games.Children.Add(_games);
        body.Children.Add(Ui.Card(games));
        body.Children.Add(Ui.Card(Compatibility()));
        Content = root;

        _previewTimer.Tick += (_, _) => UpdatePreview();
        RenderEditor();
        RenderGames();
        RenderStatus();
    }

    public void OnShown()
    {
        OverlayService.Changed += OnServiceChanged;
        _previewTimer.Start();
        RenderStatus();
        UpdatePreview();
    }

    public void OnHidden()
    {
        OverlayService.Changed -= OnServiceChanged;
        _previewTimer.Stop();
    }

    private void OnServiceChanged() => Dispatcher.BeginInvoke(RenderStatus);

    private static void Save()
    {
        App.SaveSettingsSoon();
        OverlayService.LayoutChanged();
    }

    // ---- status ----------------------------------------------------------------------------------------------------

    private void RenderStatus()
    {
        _toggle.Content = OverlayService.Visible ? "Hide overlay" : "Show overlay";
        _status.Children.Clear();
        var sec = Ui.Section("Status");
        var row = new WrapPanel();
        row.Children.Add(Ui.Tag(OverlayService.Visible ? "Overlay on" : "Overlay off", OverlayService.Visible ? "Success" : "Muted"));
        var (tone, label) = PresentMonitor.State switch
        {
            CaptureState.Running => ("Success", "Frame timing running"),
            CaptureState.NeedsAdmin => ("Gold", "Frame timing needs admin"),
            CaptureState.Failed => ("Danger", "Frame timing unavailable"),
            _ => ("Muted", "Frame timing starts with the overlay"),
        };
        row.Children.Add(Ui.Tag(label, tone));
        row.Children.Add(Ui.Tag(Admin.IsElevated ? "Administrator" : "Standard user", "Muted"));
        sec.Children.Add(row);
        if (PresentMonitor.State != CaptureState.Stopped) sec.Children.Add(Ui.Muted(PresentMonitor.Message, 12));
        sec.Children.Add(Ui.Muted(OverlayService.HotkeyStatus, 12));
        if (OverlayService.LastFrame is { Target: { } t } f && OverlayService.Visible)
            sec.Children.Add(Ui.Muted($"Measuring: {t}. {GameWindow.Explain(f.Mode)}", 12));
        var actions = Ui.Row(Ui.Btn(_testing ? "Testing…" : "Test frame capture (3 s)", async (_, _) => await TestCaptureAsync()));
        if (PresentMonitor.State == CaptureState.NeedsAdmin && !Admin.IsElevated)
            actions.Children.Add(Ui.Btn("Restart as administrator", (_, _) => { if (Admin.RestartElevated()) { App.ExitRequested = true; Application.Current.Shutdown(); } }, "GoldButton"));
        ((Button)actions.Children[0]).IsEnabled = !_testing;
        actions.Margin = new Thickness(0, 10, 0, 0);
        sec.Children.Add(actions);
        if (_testResult.Parent is Panel old) old.Children.Remove(_testResult); // the status section is rebuilt; keep the result
        sec.Children.Add(_testResult);
        _status.Children.Add(sec);
    }

    /// <summary>Real check: which apps are presenting frames right now, measured through the same ETW session.</summary>
    private async Task TestCaptureAsync()
    {
        _testing = true;
        RenderStatus();
        bool started = PresentMonitor.State != CaptureState.Running;
        try
        {
            var state = PresentMonitor.Start();
            if (state != CaptureState.Running) { _testResult.Text = PresentMonitor.Message; return; }
            _testResult.Text = "Listening for 3 seconds…";
            await Task.Delay(3000);
            var now = PresentMonitor.Now;
            var active = PresentMonitor.Frames.Active(TimeSpan.FromSeconds(3)).Take(6).ToList();
            if (active.Count == 0) { _testResult.Text = "The session is running but no app presented a frame in 3 s. Move a window or start a game and test again."; return; }
            _testResult.Text = $"{PresentMonitor.EventsSeen} Present events. Apps drawing frames:\n" + string.Join("\n", active.Select(a =>
            {
                string name; try { using var p = Process.GetProcessById(a.pid); name = p.ProcessName; } catch { name = "process " + a.pid; }
                var s = PresentMonitor.Frames.Stats(a.pid, TimeSpan.FromSeconds(3), 120, now);
                return $"• {name}: {s?.Fps:0} FPS, {s?.AvgMs:0.0} ms ({(s?.Api == PresentApi.D3D9 ? "Direct3D 9" : "DXGI: DirectX 10/11/12")})";
            }));
        }
        finally
        {
            if (started && !OverlayService.Visible) PresentMonitor.Stop();
            _testing = false;
            RenderStatus();
        }
    }

    // ---- layout editor ---------------------------------------------------------------------------------------------

    private OverlayLayout Editing => _editing is null ? S.Layout : S.ProfileFor(_editing)?.Layout ?? S.Layout;

    private void RenderEditor()
    {
        _editor.Children.Clear();
        var targets = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Target(string label, string? exe)
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "ovl-edit", IsChecked = _editing == exe, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { if (_editing == exe) return; _editing = exe; RenderEditor(); UpdatePreview(); };
            targets.Children.Add(rb);
        }
        Target("Default layout", null);
        foreach (var g in S.Games.Where(g => g.CustomLayout)) Target(g.Name.Length > 0 ? g.Name : g.Exe, g.Exe);
        _editor.Children.Add(targets);

        var l = Editing;
        var metrics = new WrapPanel();
        void Check(string label, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Content = label, IsChecked = value, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 0, 16, 6) };
            cb.Click += (_, _) => { set(cb.IsChecked == true); Save(); UpdatePreview(); };
            metrics.Children.Add(cb);
        }
        Check("FPS", l.Fps, v => l.Fps = v);
        Check("Frame time", l.FrameTime, v => l.FrameTime = v);
        Check("1% low", l.OnePercentLow, v => l.OnePercentLow = v);
        Check("Frame-time graph", l.Graph, v => l.Graph = v);
        Check("CPU", l.Cpu, v => l.Cpu = v);
        Check("GPU", l.Gpu, v => l.Gpu = v);
        Check("RAM", l.Ram, v => l.Ram = v);
        Check("Temperatures", l.Temps, v => l.Temps = v);
        Check("App name", l.Target, v => l.Target = v);
        _editor.Children.Add(metrics);

        _editor.Children.Add(Slider("Text size", l.FontSize, 9, 28, 1, v => l.FontSize = v, v => $"{v:0} pt"));
        _editor.Children.Add(Slider("Background opacity", l.Opacity, 0, 1, 0.05, v => l.Opacity = v, v => $"{v * 100:0}%"));
        _editor.Children.Add(Slider("Distance from edge", l.Margin, 0, 120, 2, v => l.Margin = v, v => $"{v:0} px"));

        var pos = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var c in Enum.GetValues<OverlayCorner>())
        {
            var corner = c;
            var rb = new RadioButton { Content = c switch { OverlayCorner.TopLeft => "Top left", OverlayCorner.TopRight => "Top right", OverlayCorner.BottomLeft => "Bottom left", _ => "Bottom right" }, Style = Ui.Res<Style>("Chip"), GroupName = "ovl-pos" + _editing, IsChecked = l.Position == c, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { l.Position = corner; Save(); };
            pos.Children.Add(rb);
        }
        _editor.Children.Add(Ui.Text("Position", 13, FontWeights.SemiBold));
        _editor.Children.Add(pos);
        _editor.Children.Add(ColorRow("Text colour", l.TextColor, v => l.TextColor = v, ["#FFFFFFFF", "#FFE6EDF5", "#FFFFE16B", "#FF7FD7FF"]));
        _editor.Children.Add(ColorRow("Accent colour (FPS and graph)", l.AccentColor, v => l.AccentColor = v, ["#FF2BE38B", "#FF4FA3FF", "#FFFF5C7A", "#FFFFB020", "#FFB57CFF"]));
    }

    private UIElement Slider(string label, double value, double min, double max, double step, Action<double> set, Func<double, string> fmt)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new DockPanel();
        var val = Ui.Text(fmt(value), 12.5, FontWeights.SemiBold);
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(Ui.Text(label, 13, FontWeights.SemiBold));
        sp.Children.Add(head);
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, Width = 520 };
        s.ValueChanged += (_, e) => { set(e.NewValue); val.Text = fmt(e.NewValue); Save(); UpdatePreview(); };
        sp.Children.Add(s);
        return sp;
    }

    private UIElement ColorRow(string label, string value, Action<string> set, string[] presets)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(Ui.Text(label, 13, FontWeights.SemiBold));
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var box = new TextBox { Text = value, Width = 110, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in presets)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Button { Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(c), BorderBrush = Ui.Brush("CardRim"), BorderThickness = new Thickness(1), ToolTip = hex, Cursor = System.Windows.Input.Cursors.Hand };
            swatch.Template = SwatchTemplate();
            var h = hex;
            swatch.Click += (_, _) => { set(h); box.Text = h; Save(); UpdatePreview(); };
            row.Children.Add(swatch);
        }
        box.LostFocus += (_, _) =>
        {
            try { ColorConverter.ConvertFromString(box.Text.Trim()); set(box.Text.Trim()); Save(); UpdatePreview(); }
            catch { box.Text = value; }
        };
        row.Children.Add(box);
        sp.Children.Add(row);
        return sp;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        t.VisualTree = b;
        return t;
    }

    /// <summary>Live data while the overlay runs; otherwise clearly-labelled sample numbers so the look can be judged.</summary>
    private void UpdatePreview()
    {
        _preview.ApplyLayout(Editing);
        if (OverlayService.Visible && OverlayService.LastFrame is { } live) { _preview.Update(live); return; }
        var times = new List<long>();
        long t = 0;
        var rnd = new Random(7);
        for (int i = 0; i < 160; i++) { t += (long)((i % 47 == 0 ? 9.5 : 4.0 + rnd.NextDouble() * 0.6) * 10_000); times.Add(t); }
        var hw = LiveStats.Last;
        _preview.Update(new OverlayFrame(FrameTracker.Compute(0, PresentApi.Dxgi, times), CaptureState.Running, null,
            hw?.CpuPercent ?? 34, hw?.GpuPercent ?? 92, hw?.RamUsedGb ?? 17.0, hw?.RamTotalGb ?? 31.2, hw?.CpuTempC, hw?.GpuTempC ?? 64,
            "Preview (sample frame data)", GameWindowMode.Borderless));
    }

    // ---- hotkey ----------------------------------------------------------------------------------------------------

    private UIElement HotkeyCard()
    {
        var sec = Ui.Section("Show/hide hotkey", "A global hotkey that works while a game has focus. Use Ctrl, Shift, Alt or Win with a key, e.g. Ctrl+Shift+F10 (a function key alone also works).");
        var box = new TextBox { Text = S.Hotkey, Width = 200, VerticalAlignment = VerticalAlignment.Center };
        var msg = Ui.Muted(OverlayService.HotkeyStatus, 12);
        var save = Ui.Btn("Save hotkey", (_, _) =>
        {
            var text = box.Text.Trim();
            if (!OverlayHotkey.TryParse(text, out _, out _)) { msg.Text = $"\"{text}\" isn't a valid hotkey. Example: Ctrl+Shift+F10."; return; }
            var old = S.Hotkey;
            S.Hotkey = text;
            if (!OverlayService.RegisterHotkey()) { S.Hotkey = old; OverlayService.RegisterHotkey(); }
            App.SaveSettingsSoon();
            msg.Text = OverlayService.HotkeyStatus;
        }, margin: new Thickness(8, 0, 0, 0));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(save);
        sec.Children.Add(row);
        sec.Children.Add(msg);
        return sec;
    }

    // ---- per-game profiles ---------------------------------------------------------------------------------------

    private void RenderGames()
    {
        _games.Children.Clear();
        var auto = new CheckBox { Content = "Show the overlay automatically when one of these games starts", IsChecked = S.AutoMonitor, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 0, 0, 10) };
        auto.Click += (_, _) =>
        {
            S.AutoMonitor = auto.IsChecked == true;
            App.SaveSettingsSoon();
            if (S.AutoMonitor) OverlayService.StartWatcher(); else OverlayService.StopWatcher();
        };
        _games.Children.Add(auto);

        foreach (var g in S.Games.ToList())
        {
            var game = g;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = Ui.Btn("Remove", (_, _) => { S.Games.Remove(game); if (_editing == game.Exe) _editing = null; App.SaveSettingsSoon(); RenderGames(); RenderEditor(); }, "GhostButton");
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var custom = new CheckBox { Content = "Own layout", IsChecked = game.CustomLayout, Foreground = Ui.Brush("Text"), Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            custom.Click += (_, _) =>
            {
                game.CustomLayout = custom.IsChecked == true;
                if (game.CustomLayout) { game.Layout = S.Layout.Clone(); _editing = game.Exe; } else if (_editing == game.Exe) _editing = null;
                Save(); RenderEditor(); UpdatePreview();
            };
            DockPanel.SetDock(custom, Dock.Right);
            row.Children.Add(custom);
            var show = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = game.AutoShow, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show automatically" };
            show.Click += (_, _) => { game.AutoShow = show.IsChecked == true; App.SaveSettingsSoon(); };
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);
            var text = new StackPanel();
            text.Children.Add(Ui.Text(game.Name.Length > 0 ? game.Name : game.Exe, 13, FontWeights.SemiBold));
            text.Children.Add(Ui.Muted(OverlaySettings.Normalize(game.Exe), 11.5));
            row.Children.Add(text);
            _games.Children.Add(row);
        }
        if (S.Games.Count == 0) _games.Children.Add(Ui.Muted("No games yet.", 12));

        // Add: type an exe, or pick from apps that have a window open right now.
        var add = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var exeBox = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Game executable, e.g. GeometryDash.exe", Tag = "Game .exe, e.g. GeometryDash.exe" };
        add.Children.Add(exeBox);
        add.Children.Add(Ui.Btn("Add game", (_, _) => AddGame(exeBox.Text, null)));
        var running = new ComboBox { Width = 300, Margin = new Thickness(8, 0, 8, 6), ToolTip = "Apps with an open window" };
        running.Items.Add(new ComboBoxItem { Content = "…or pick an app that's open now", IsEnabled = false, Tag = null });
        running.SelectedIndex = 0;
        try
        {
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    try { if (p.MainWindowHandle != 0 && p.Id != Environment.ProcessId && p.MainWindowTitle.Length > 0) running.Items.Add(new ComboBoxItem { Content = $"{p.ProcessName} — {p.MainWindowTitle}", Tag = p.ProcessName }); }
                    catch { }
                }
        }
        catch { }
        add.Children.Add(running);
        add.Children.Add(Ui.Btn("Add selected app", (_, _) => { if (running.SelectedItem is ComboBoxItem { Tag: string n }) AddGame(n, (running.SelectedItem as ComboBoxItem)!.Content as string); }));
        _games.Children.Add(add);
    }

    private void AddGame(string exe, string? label)
    {
        exe = exe.Trim();
        if (exe.Length == 0 || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { MainWindow.Current?.ShowToast("Type the game's .exe name, e.g. GeometryDash.exe.", "Gold"); return; }
        if (S.ProfileFor(exe) is not null) { MainWindow.Current?.ShowToast("That game is already in the list.", "Gold"); return; }
        S.Games.Add(new OverlayGameProfile { Exe = OverlaySettings.Normalize(exe), Name = label?.Split(" — ")[0] ?? Path.GetFileNameWithoutExtension(exe), AutoShow = true });
        App.SaveSettingsSoon();
        RenderGames();
    }

    // ---- honesty ---------------------------------------------------------------------------------------------------

    private static UIElement Compatibility()
    {
        var sec = Ui.Section("Compatibility, permissions and anti-cheat");
        sec.Children.Add(Ui.Muted(
            "• FPS and frame time: measured from Windows' ETW Present events, the same source PresentMon uses (time between the game's Present calls). Works for DirectX 10, 11 and 12 (DXGI) and DirectX 9, in windowed, borderless and exclusive fullscreen alike.\n" +
            "• Not measured: Vulkan and OpenGL games (they don't raise these events) and games that present through other APIs. The overlay says \"no frames from this app\" instead of guessing.\n" +
            "• Visibility: the overlay is a normal transparent window, so it's drawn over windowed and borderless fullscreen games. True exclusive fullscreen doesn't let other windows draw on top, so it may be hidden there (frame timing keeps working); use borderless fullscreen.\n" +
            "• Permissions: Windows only lets administrators and members of the Performance Log Users group read these events. Without that, FPS shows \"needs admin\" and the other readings still work.\n" +
            "• Sensors: CPU temperature only if your firmware exposes it; GPU temperature on NVIDIA through the driver (NVAPI). Missing sensors are shown as n/a.\n" +
            "• Anti-cheat: Money Tweaks never injects DLLs, hooks games or reads game memory. Some games or anti-cheat systems still hide or block third-party overlays, or ask you to close them; respect the game's rules.\n" +
            "• Overhead: frame timing and sensors only run while the overlay is visible; the overlay refreshes 4 times a second.", 12.5));
        return sec;
    }
}
