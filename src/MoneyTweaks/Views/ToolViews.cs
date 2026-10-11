using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>A compact list of catalog tweaks with switches, used by tool pages to bundle related tweaks.</summary>
public sealed class TweakBundle : StackPanel
{
    public List<TweakItem> Items { get; }

    public TweakBundle(IEnumerable<string> ids)
    {
        Items = ids.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
        foreach (var it in Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var sw = new System.Windows.Controls.Primitives.ToggleButton { Style = Ui.Res<Style>("Switch"), IsThreeState = true, VerticalAlignment = VerticalAlignment.Center };
            sw.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(TweakItem.IsOn)) { Mode = System.Windows.Data.BindingMode.OneWay });
            sw.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(TweakItem.CanToggle)));
            sw.DataContext = it;
            sw.Click += async (_, _) =>
            {
                sw.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
                await TweakService.ToggleAsync(it);
                sw.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            };
            DockPanel.SetDock(sw, Dock.Right);
            row.Children.Add(sw);
            var txt = new StackPanel();
            var name = new WrapPanel();
            name.Children.Add(Ui.Text(it.Name, 13, FontWeights.SemiBold));
            name.Children.Add(new Border { Width = 6 });
            name.Children.Add(Ui.Pill(it.RiskLabel, it.IsCaution));
            var status = Ui.Muted("", 11.5);
            status.Foreground = Ui.Brush("Accent");
            status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TweakItem.StatusText)));
            status.DataContext = it;
            name.Children.Add(status);
            txt.Children.Add(name);
            txt.Children.Add(Ui.Muted(it.Description, 12));
            row.Children.Add(txt);
            Children.Add(row);
        }
    }

    public Task RefreshAsync() => Task.WhenAll(Items.Select(i => i.RefreshAsync()));

    public async Task ApplyAllAsync()
    {
        await RefreshAsync();
        var (ok, failed, reboot) = await TweakService.BatchAsync(Items, true);
        MainWindow.Current?.ShowToast($"{ok} applied{(failed > 0 ? $", {failed} failed" : "")}{(reboot ? ". Restart to finish." : ".")}");
    }
}

public sealed class PotatoView : UserControl, IPageLifecycle
{
    private readonly StackPanel _games = new();
    private readonly TweakBundle _windows = new(["gen.visualfx-performance", "gen.transparency", "gen.background-recording", "adv.fso", "adv.system-responsiveness", "adv.games-task", "gen.ultimate-plan"]);

    public PotatoView()
    {
        var (root, body) = Ui.Page("Potato Graphics", "Turn games into a low-poly, ultra-smooth potato: flat textures, simple meshes, no grass, shadows or effects, and as many FPS as your PC can push. Money Tweaks edits each game's own settings file after backing it up, and Restore puts everything back. Close the game before applying.");
        body.Children.Add(_games);
        var ws = Ui.Section("Windows potato preset", "System-side changes that help every game. Each one is a regular, reversible tweak.");
        ws.Children.Add(_windows);
        ws.Children.Add(Ui.Row(Ui.Btn("Apply Windows preset", async (_, _) => await _windows.ApplyAllAsync(), "PrimaryButton", new Thickness(0, 10, 0, 0))));
        body.Children.Add(Ui.Card(ws));
        Content = root;
        BuildGames();
    }

    private static PotatoOptions OptionsFor(PotatoGame g)
    {
        if (!App.Settings.Potato.TryGetValue(g.Id, out var o))
        {
            o = new PotatoOptions { PerformanceMode = g.SupportsPerformanceMode };
            App.Settings.Potato[g.Id] = o;
        }
        return o;
    }

    private void BuildGames()
    {
        _games.Children.Clear();
        foreach (var g in PotatoGraphics.Games) _games.Children.Add(GameCard(g));
    }

    private Border GameCard(PotatoGame g)
    {
        var o = OptionsFor(g);
        var sp = new StackPanel();
        var head = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var apply = Ui.Btn("Apply potato", (_, _) => Run(g, true), "PrimaryButton");
        apply.IsEnabled = g.Installed;
        var restore = Ui.Btn("Restore original", (_, _) => Run(g, false), margin: new Thickness(0));
        restore.IsEnabled = g.HasBackup;
        btns.Children.Add(apply);
        btns.Children.Add(restore);
        DockPanel.SetDock(btns, Dock.Right);
        head.Children.Add(btns);
        var title = new WrapPanel();
        title.Children.Add(Ui.Text(g.Name, 16, FontWeights.SemiBold));
        title.Children.Add(new Border { Width = 8 });
        title.Children.Add(Ui.Pill(g.Installed ? (g.HasBackup ? "Potato applied" : "Detected") : "Not found. Launch the game once", !g.Installed));
        head.Children.Add(title);
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted(g.Notes, 12));

        var summary = Ui.Muted("", 12);
        summary.Foreground = Ui.Brush("Accent");
        summary.Margin = new Thickness(0, 8, 0, 0);

        Slider? scale = null;
        var levels = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        foreach (var lvl in Enum.GetValues<PotatoLevel>())
        {
            var label = lvl switch { PotatoLevel.Low => "Low", PotatoLevel.Potato => "Potato", _ => "Ultra potato" };
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "lvl-" + g.Id, IsChecked = o.Level == lvl, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) =>
            {
                o.Level = lvl;
                o.RenderScale = PotatoOptions.DefaultScale(lvl);
                if (scale is not null) scale.Value = o.RenderScale;
                Update();
            };
            levels.Children.Add(rb);
        }
        sp.Children.Add(levels);

        if (g.SupportsRenderScale)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var name = Ui.Muted("3D resolution", 12.5);
            name.Width = 110;
            var val = Ui.Text($"{o.RenderScale}%", 12.5);
            val.Width = 50;
            scale = new Slider { Minimum = 25, Maximum = 100, Value = o.RenderScale, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Width = 360 };
            scale.ValueChanged += (_, e) => { o.RenderScale = (int)e.NewValue; val.Text = $"{o.RenderScale}%"; Update(); };
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(val, Dock.Right);
            row.Children.Add(name);
            row.Children.Add(val);
            row.Children.Add(scale);
            sp.Children.Add(row);
        }

        var opts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Opt(string text, bool value, Action<bool> set, string tip)
        {
            var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 18, 4), ToolTip = tip };
            cb.Click += (_, _) => { set(cb.IsChecked == true); Update(); };
            opts.Children.Add(cb);
        }
        if (g.SupportsPerformanceMode) Opt("Performance mode (low-poly builds)", o.PerformanceMode, v => o.PerformanceMode = v, "DirectX 11 Performance rendering: simplified meshes and flat shading. Gives the biggest FPS jump.");
        if (g.SupportsFpsCap) Opt("Uncap FPS (smoothest)", o.UncapFps, v => o.UncapFps = v, "Removes the in-game frame-rate limit.");
        if (g.SupportsStretched) Opt($"Stretched {o.StretchWidth}x{o.StretchHeight}", o.Stretched, v => o.Stretched = v, "Classic 4:3 stretched look. The settings file is set read-only so Fortnite keeps it. Set GPU scaling to Full-screen in your driver.");
        sp.Children.Add(opts);
        sp.Children.Add(summary);
        if (g.Installed) sp.Children.Add(Ui.Muted(g.ConfigPath, 11));
        Update();
        return Ui.Card(sp);

        void Update()
        {
            var parts = new List<string>
            {
                o.Level switch { PotatoLevel.Low => "Everything Low", PotatoLevel.Potato => "Potato: everything Low, short view distance", _ => "Ultra potato: absolute minimum" }
            };
            if (g.SupportsRenderScale) parts.Add($"{o.RenderScale}% 3D resolution{(o.RenderScale <= 50 ? " (blurry, smooth)" : "")}");
            if (g.SupportsPerformanceMode && o.PerformanceMode) parts.Add("Performance mode");
            if (g.SupportsFpsCap && o.UncapFps) parts.Add("uncapped FPS");
            if (g.SupportsStretched && o.Stretched) parts.Add($"stretched {o.StretchWidth}x{o.StretchHeight}");
            summary.Text = "Will apply: " + string.Join(" · ", parts);
        }
    }

    private void Run(PotatoGame g, bool apply)
    {
        if (apply && PotatoGraphics.IsRunning(g))
        {
            Dialog.Show("Close the game first", $"{g.Name} is running and would overwrite its settings file when it exits.", DialogKind.Warning);
            return;
        }
        try
        {
            var msg = apply ? PotatoGraphics.Apply(g, OptionsFor(g)) : PotatoGraphics.Restore(g);
            App.Settings.Save();
            if (apply && g.Id == "fortnite")
                msg += "\n\nIn Fortnite, open Settings > Video and check that Rendering Mode is Performance and Mesh Quality is Low. That's what makes builds low-poly.";
            Dialog.Show(apply ? $"{g.Name} is now a potato" : $"{g.Name} restored", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"Potato Graphics {g.Name}", ex);
            Dialog.Show("Couldn't change settings", ex.Message, DialogKind.Error);
        }
        BuildGames();
    }

    public void OnShown() { _ = _windows.RefreshAsync(); BuildGames(); }
    public void OnHidden() => App.Settings.Save();
}

public sealed class BoosterView : UserControl, IPageLifecycle
{
    private readonly GameBooster _booster = new();
    private readonly WrapPanel _procs = new();
    private readonly TextBox _game = new() { Width = 300 };
    private readonly CheckBox _purge = new() { Content = "Clear standby memory (admin)", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _report = Ui.Muted("");
    private readonly Button _start, _end;

    public BoosterView()
    {
        _start = Ui.Btn("⚡ Start boost", Start, "PrimaryButton");
        _end = Ui.Btn("End boost", End);
        _end.IsEnabled = false;
        var (root, body) = Ui.Page("Game Booster", "One click before you play: switches to the High/Ultimate Performance power plan, closes the background apps you choose, raises your game to High priority and clears cached standby memory. End boost restores your power plan.",
            Ui.Row(_start, _end));

        var gs = Ui.Section("Game to prioritize", "Process name of the running game, e.g. FortniteClient-Win64-Shipping or cs2. Leave it empty to skip.");
        _game.Text = App.Settings.BoosterGameProcess;
        gs.Children.Add(_game);
        _purge.IsChecked = App.Settings.BoosterPurgeStandby;
        gs.Children.Add(_purge);
        body.Children.Add(Ui.Card(gs));

        var ps = Ui.Section("Apps to close while boosting", "Your choices are remembered. System processes, anti-cheat and Money Tweaks itself are never listed. Apps with a window get a normal close request first so they can save.");
        ps.Children.Add(Ui.Row(Ui.Btn("Refresh list", (_, _) => LoadProcs(), margin: new Thickness(0, 0, 0, 8))));
        ps.Children.Add(_procs);
        body.Children.Add(Ui.Card(ps));
        var rs = Ui.Section("Last boost");
        rs.Children.Add(_report);
        body.Children.Add(Ui.Card(rs));
        Content = root;
    }

    private void LoadProcs()
    {
        _procs.Children.Clear();
        foreach (var p in GameBooster.ClosableProcesses().Take(60))
        {
            var cb = new CheckBox
            {
                Content = $"{p.Name}  ·  {p.WorkingSetMb:0} MB", Tag = p.Name, Width = 300, Margin = new Thickness(0, 4, 8, 4),
                IsChecked = App.Settings.BoosterCloseList.Contains(p.Name, StringComparer.OrdinalIgnoreCase), ToolTip = p.Title
            };
            cb.Checked += (_, _) => { if (!App.Settings.BoosterCloseList.Contains(p.Name)) App.Settings.BoosterCloseList.Add(p.Name); };
            cb.Unchecked += (_, _) => App.Settings.BoosterCloseList.RemoveAll(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            _procs.Children.Add(cb);
        }
    }

    private async void Start(object s, RoutedEventArgs e)
    {
        App.Settings.BoosterGameProcess = _game.Text.Trim();
        App.Settings.BoosterPurgeStandby = _purge.IsChecked == true;
        App.Settings.Save();
        var close = App.Settings.BoosterCloseList.ToList();
        if (close.Count > 0 && !Dialog.Confirm("Close these apps?", string.Join("\n", close.Select(c => "• " + c)) + "\n\nUnsaved work in them may be lost.", "Boost")) return;
        _start.IsEnabled = false;
        var lines = await Task.Run(() => _booster.Start(close, App.Settings.BoosterGameProcess, App.Settings.BoosterPurgeStandby));
        _report.Text = string.Join("\n", lines);
        _end.IsEnabled = true;
        MainWindow.Current?.ShowToast("Boost active. Good luck!");
        LoadProcs();
    }

    private async void End(object s, RoutedEventArgs e)
    {
        var msg = await Task.Run(_booster.End);
        _report.Text = msg;
        _end.IsEnabled = false;
        _start.IsEnabled = true;
    }

    public void OnShown() => LoadProcs();
    public void OnHidden() { }
}

public sealed class InputDelayView : UserControl, IPageLifecycle
{
    private readonly TextBlock _current = Ui.Text("", 28, FontWeights.SemiBold);
    private readonly TextBlock _range = Ui.Muted("");
    private readonly Slider _slider = new() { Minimum = 0.5, Maximum = 1.0, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _sliderVal = Ui.Text("", 13);
    private readonly Button _hold, _release;
    private double? _held;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TweakBundle _bundle = new(["gen.mouse-accel", "gen.keyboard-repeat", "gen.sticky-keys", "gen.usb-suspend", "adv.fso", "adv.global-timer-res", "adv.timer-dynamic-tick", "adv.priority-separation", "adv.gpu-msi"]);

    public InputDelayView()
    {
        _hold = Ui.Btn("Hold high-precision timer", Hold, "PrimaryButton");
        _release = Ui.Btn("Release", Release);
        _release.IsEnabled = false;
        var (root, body) = Ui.Page("Input Delay Reducer", "Lowers the Windows timer interval so games and input polling wake up more often, and bundles the input-related tweaks in one place. The timer request is held only while Money Tweaks is open; close the app and Windows goes back to normal.");

        var ts = Ui.Section("Kernel timer resolution");
        ts.Children.Add(_current);
        ts.Children.Add(_range);
        _slider.Value = Math.Clamp(App.Settings.TimerResolutionMs, 0.5, 1.0);
        _slider.ValueChanged += (_, _) => _sliderVal.Text = $"Request: {_slider.Value:0.00} ms";
        _sliderVal.Text = $"Request: {_slider.Value:0.00} ms";
        var sliderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) };
        sliderRow.Children.Add(_slider);
        sliderRow.Children.Add(new Border { Width = 12 });
        sliderRow.Children.Add(_sliderVal);
        ts.Children.Add(sliderRow);
        ts.Children.Add(Ui.Row(_hold, _release));
        ts.Children.Add(Ui.Muted(OsInfo.IsWindows11
            ? "Windows 11: a timer request only affects the requesting process unless 'Global timer resolution requests' (below) is applied and the PC restarted."
            : "Windows 10: the request applies system-wide while it is held.", 12));
        body.Children.Add(Ui.Card(ts));

        var bs = Ui.Section("Input tweak bundle", "Each switch is a normal Money Tweaks tweak with its own backup.");
        bs.Children.Add(_bundle);
        body.Children.Add(Ui.Card(bs));
        Content = root;
        _timer.Tick += (_, _) => UpdateReadout();
    }

    private void UpdateReadout()
    {
        var (min, max, cur) = TimerResolution.Query();
        _current.Text = $"{cur:0.###} ms";
        _range.Text = $"Supported range {max:0.###} – {min:0.###} ms{(_held is null ? "" : $" · holding {_held:0.##} ms")}";
    }

    private void Hold(object s, RoutedEventArgs e)
    {
        if (_held is { } old) TimerResolution.Release(old);
        _held = _slider.Value;
        var got = TimerResolution.Set(_slider.Value);
        App.Settings.TimerResolutionMs = _slider.Value;
        Log.Info($"Timer resolution requested {_slider.Value:0.00} ms, system now {got:0.###} ms");
        _release.IsEnabled = true;
        UpdateReadout();
    }

    private void Release(object s, RoutedEventArgs e)
    {
        if (_held is { } h) TimerResolution.Release(h);
        _held = null;
        _release.IsEnabled = false;
        UpdateReadout();
    }

    public void OnShown() { _timer.Start(); UpdateReadout(); _ = _bundle.RefreshAsync(); }
    public void OnHidden() => _timer.Stop();
}

public sealed class PingView : UserControl, IPageLifecycle
{
    private readonly PingMonitor _mon = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly TextBox _host = new() { Width = 220 };
    private readonly TextBlock _grade = Ui.Text("Idle", 26, FontWeights.SemiBold);
    private readonly TextBlock _stats = Ui.Muted("");
    private readonly Canvas _chart = new() { Height = 160, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Polyline _line = new() { Stroke = Ui.Brush("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Button _toggle;
    private readonly TweakBundle _bundle = new(["net.nagle", "net.throttling-index", "net.qos-reserve", "net.adapter-power", "net.autotuning", "net.dns-cloudflare"]);
    private bool _running, _busy;

    public PingView()
    {
        _toggle = Ui.Btn("▶ Start monitoring", Toggle, "PrimaryButton");
        var (root, body) = Ui.Page("Ping Stabilizer", "Measures latency, jitter and packet loss continuously, then applies the network tweaks that cut lag spikes. It can't fix a bad route to a game server; it removes the local causes of spikes.");
        _host.Text = App.Settings.PingHost;
        var hostRow = new StackPanel { Orientation = Orientation.Horizontal };
        hostRow.Children.Add(_host);
        hostRow.Children.Add(new Border { Width = 8 });
        foreach (var (label, host) in PingMonitor.Targets)
            hostRow.Children.Add(Ui.Btn(label.Split(' ')[0] == "Your" ? "Router" : label.Split(' ')[0], (_, _) => _host.Text = host == "gateway" ? PingMonitor.DefaultGateway() ?? "" : host));
        var ms = Ui.Section("Live connection");
        ms.Children.Add(hostRow);
        ms.Children.Add(Ui.Row(_toggle, Ui.Btn("Flush DNS cache", (_, _) => { var r = ProcessRunner.Run(System.IO.Path.Combine(ProcessRunner.System32, "ipconfig.exe"), "/flushdns"); MainWindow.Current?.ShowToast(r.Ok ? "DNS cache flushed." : "Flush failed: " + r.Combined); })));
        ms.Children.Add(new Border { Height = 10 });
        ms.Children.Add(_grade);
        ms.Children.Add(_stats);
        _chart.Children.Add(_line);
        ms.Children.Add(new Border { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6), Child = _chart });
        body.Children.Add(Ui.Card(ms));
        var bs = Ui.Section("Stabilize", "Apply the network tweaks most associated with lag spikes. Each is reversible.");
        bs.Children.Add(_bundle);
        bs.Children.Add(Ui.Row(Ui.Btn("Apply all", async (_, _) => await _bundle.ApplyAllAsync(), "PrimaryButton", new Thickness(0, 10, 0, 0))));
        body.Children.Add(Ui.Card(bs));
        Content = root;
        _timer.Tick += Tick;
        _chart.SizeChanged += (_, _) => Draw();
    }

    private void Toggle(object s, RoutedEventArgs e)
    {
        _running = !_running;
        _toggle.Content = _running ? "■ Stop" : "▶ Start monitoring";
        if (_running) { _mon.Reset(); App.Settings.PingHost = _host.Text.Trim(); _timer.Start(); } else _timer.Stop();
    }

    private async void Tick(object? s, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var st = await _mon.PingOnceAsync(_host.Text.Trim());
            _grade.Text = st.Grade;
            _grade.Foreground = Ui.Brush(st.Grade == "Stable" ? "Accent" : st.Grade == "Fair" ? "Gold" : st.Grade == "Unstable" ? "Danger" : "Text");
            _stats.Text = $"Now {(st.LastMs is { } l ? $"{l:0} ms" : "timeout")} · avg {st.AvgMs:0.0} ms · min {st.MinMs:0} · max {st.MaxMs:0} · jitter {st.JitterMs:0.0} ms · loss {st.LossPercent:0.#}% ({st.Sent} samples)";
            Draw();
        }
        finally { _busy = false; }
    }

    private void Draw()
    {
        var samples = _mon.Samples;
        double w = _chart.ActualWidth, h = _chart.ActualHeight;
        if (w <= 0 || samples.Count < 2) return;
        var max = Math.Max(50, samples.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(50).Max() * 1.2);
        var pts = new PointCollection();
        for (int i = 0; i < samples.Count; i++)
            pts.Add(new Point(w * i / 119.0, samples[i] is { } v ? h - h * v / max : h));
        _line.Points = pts;
    }

    public void OnShown() => _ = _bundle.RefreshAsync();
    public void OnHidden() { }
}
