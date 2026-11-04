using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Advisor;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>
/// Fix My Game 2.0, on the Dashboard: game → problem → Quick or Deep Scan → findings ranked by severity (confirmed facts
/// apart from likely causes, with evidence, other explanations and technical details) → the best 1–3 fixes, selectable
/// and applied together after a review (tweaks through TweakService: Conflict Radar, admin, backup, read-back; the game's
/// Gaming Profile through ProfileService; existing tools; plain steps) → Verification Lab (the same measurement again,
/// optionally repeated, with a noise-aware verdict) → report. Every run is kept in local per-game history. All
/// measurements come from the existing capture, diagnosis, network, scanner and crash code; nothing is invented.
/// </summary>
public sealed class FixMyGamePanel : UserControl
{
    private readonly StackPanel _body = new();
    private readonly ComboBox _gamePick = new() { MinWidth = 230, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _gameInfo = new() { FontSize = 11, Foreground = Ui.Brush("Subtle"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, Margin = new Thickness(0, 4, 0, 0) };
    private List<GameContext> _games = [];
    private GameContext? _game;
    private GameProblem _problem;
    private bool _deep, _repeat;
    private FixContext? _ctx;
    private FixMeasurement? _before, _after;
    private FixPlan? _plan;
    private FixComparison? _cmp;
    private FixSession? _session;
    private readonly HashSet<string> _selected = [];
    private readonly List<(FixAction action, TweakItem? item)> _applied = [];
    private readonly List<string> _undone = [];
    private CancellationTokenSource? _cts;
    private const string FrontItem = "The window in front (5-second countdown)";
    private const string NoGameItem = "No specific game";

    public FixMyGamePanel()
    {
        var root = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "FIX MY GAME", Style = Ui.Res<Style>("Eyebrow") });
        title.Children.Add(new TextBlock { Text = "What's wrong in your game?", Style = Ui.Res<Style>("H2") });
        title.Children.Add(new TextBlock { Text = "Pick the problem. Money Tweaks checks this PC, measures the game while you play if you want, shows what's really causing it, suggests the best fixes and checks afterwards whether they helped. Nothing changes without your OK.", Style = Ui.Res<Style>("MutedText"), Margin = new Thickness(0, 4, 16, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left });
        head.Children.Add(title);
        var gameBox = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        gameBox.Children.Add(new TextBlock { Text = "GAME", Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Muted") });
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        pickRow.Children.Add(_gamePick);
        var refresh = Ui.Btn("↻", async (_, _) => await LoadGamesAsync(), "GhostButton", new Thickness(0));
        refresh.ToolTip = "Look for running games again";
        refresh.Padding = new Thickness(10, 4, 10, 4);
        pickRow.Children.Add(refresh);
        gameBox.Children.Add(pickRow);
        gameBox.Children.Add(_gameInfo);
        Grid.SetColumn(gameBox, 1);
        head.Children.Add(gameBox);
        root.Children.Add(head);
        root.Children.Add(new Grid { Height = 2, Margin = new Thickness(0, 14, 0, 16), Children = { new Border { Height = 1, Background = Ui.Brush("Hairline"), VerticalAlignment = VerticalAlignment.Center }, new Border { Width = 64, Height = 2, Background = Ui.Brush("AccentGradientH"), HorizontalAlignment = HorizontalAlignment.Left } } });
        root.Children.Add(_body);
        _gamePick.SelectionChanged += (_, _) => OnGamePicked();
        Content = Ui.Hud(new Border { Style = Ui.Res<Style>("CardBorder"), Padding = new Thickness(24, 20, 24, 22), Child = root, Margin = new Thickness(0, 0, 14, 18) });
        ShowPick();
        Loaded += async (_, _) => { if (_games.Count == 0) await LoadGamesAsync(); };
    }

    // ---- game ---------------------------------------------------------------------------------------------------------------
    private async Task LoadGamesAsync()
    {
        if (_scanning) return;
        _gameInfo.Text = "Looking for games…";
        var keep = _game?.Key;
        _games = await Task.Run(FixMyGameScan.Games);
        // A game picked with the countdown isn't in the library: keep it selectable while its process runs.
        if (_game is { } picked && !_games.Any(g => g.Key == picked.Key) && picked.Pid is { } pid && GameCapture.StillRunning(pid)) _games.Insert(0, picked);
        _gamePick.Items.Clear();
        foreach (var g in _games) _gamePick.Items.Add(g.Name + (g.Running ? "  ●  running" : ""));
        _gamePick.Items.Add(FrontItem);
        _gamePick.Items.Add(NoGameItem);
        int i = keep is null ? _games.FindIndex(g => g.Running) : _games.FindIndex(g => g.Key == keep);
        _gamePick.SelectedIndex = i >= 0 ? i : _games.Count + 1;
        OnGamePicked();
    }

    private void OnGamePicked()
    {
        if (_scanning) return;
        int i = _gamePick.SelectedIndex;
        if (i >= 0 && i < _games.Count) _game = _games[i];
        else if (_gamePick.SelectedItem as string == NoGameItem) _game = null;
        _gameInfo.Text = _game is null ? (_gamePick.SelectedItem as string == FrontItem ? "Picked when a Deep Scan starts." : "Checks the PC without a specific game.")
            : string.Join(" · ", new[] { _game.Running ? "Running" : "Not running", _game.Version is { } v ? "version " + v : null, _game.Exe, _game.ProfileName is { } p ? $"profile “{p}”{(_game.ProfileActive ? " (on)" : "")}" : null }.Where(x => x is not null));
        _gameInfo.ToolTip = _game?.ExePath;
        if (_plan is null && _body.Children.Count > 0) ShowPick();
    }

    // ---- shared visuals -----------------------------------------------------------------------------------------------------
    private void Swap(params UIElement[] parts)
    {
        _body.Children.Clear();
        foreach (var p in parts) _body.Children.Add(p);
        if (App.ReduceMotion) return;
        _body.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        _body.RenderTransform = tt;
        Ui.Animate(_body, OpacityProperty, 1, 220);
        Ui.Animate(tt, TranslateTransform.YProperty, 0, 280);
    }

    private static UIElement StepTitle(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
        sp.Children.Add(Ui.Muted(sub, 11.5));
        return sp;
    }

    private static TextBlock Eyebrow(string text, double top = 16) => new() { Text = text, Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, top, 0, 6) };

    private static string SeverityBrush(FixSeverity s) => s switch { FixSeverity.Critical => "Danger", FixSeverity.High => "Gold", FixSeverity.Medium => "Violet", _ => "Muted" };
    private static string StatusText(FixStatus s) => s switch { FixStatus.Confirmed => "Confirmed", FixStatus.Likely => "Likely cause", FixStatus.Possible => "Possible cause", _ => "Not enough evidence" };

    private static UIElement Diamond(string brush, double size = 8) => new Rectangle
    {
        Width = size, Height = size, Fill = Ui.Brush(brush), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        RenderTransform = new RotateTransform(45, size / 2, size / 2),
    };

    // ---- step 1: problem ----------------------------------------------------------------------------------------------------
    private static readonly (GameProblem p, string icon)[] Problems =
    [
        (GameProblem.LowFps, "\uEC4A"), (GameProblem.Stutter, "\uE9D9"), (GameProblem.InputDelay, "\uE962"), (GameProblem.HighPing, "\uE701"),
        (GameProblem.Overheating, "\uE9CA"), (GameProblem.Crashes, "\uE7BA"), (GameProblem.Everything, "\uE721"),
    ];

    private void ShowPick()
    {
        _plan = null; _before = _after = null; _cmp = null; _session = null;
        _selected.Clear(); _applied.Clear(); _undone.Clear();
        var grid = (ResponsiveGrid)Ui.Grid(4);
        grid.MinItemWidth = 190;
        foreach (var (p, icon) in Problems)
        {
            var sp = new StackPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), FontSize = 20, Foreground = Ui.Brush("AccentGradient") });
            if (_game is not null && FixHistoryCount(p) is > 0 and var n)
                top.Children.Add(new TextBlock { Text = $"{n}× before", FontSize = 10.5, Foreground = Ui.Brush("Subtle"), HorizontalAlignment = HorizontalAlignment.Right });
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = FixMyGame.Label(p), FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
            sp.Children.Add(new TextBlock { Text = FixMyGame.Blurb(p), Style = Ui.Res<Style>("MutedText"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) });
            var card = Ui.ClickCard(sp, () => { _problem = p; ShowMode(); }, new Thickness(0, 0, 10, 10));
            card.Padding = new Thickness(14);
            grid.Children.Add(card);
        }
        var parts = new List<UIElement> { grid };
        if (_game is not null && HistoryPanel() is { } hist) parts.Add(hist);
        Swap([.. parts]);
    }

    private int FixHistoryCount(GameProblem p) => _game is null ? 0 : (_ctx?.History ?? (_cachedHistory ??= FixHistory.Load())).For(_game.Key).Count(s => s.Problem == p);
    private FixHistory? _cachedHistory;

    /// <summary>The last runs for this game: what was found, what was changed and what the re-test said.</summary>
    private UIElement? HistoryPanel()
    {
        var sessions = (_cachedHistory = FixHistory.Load()).For(_game!.Key).Take(5).ToList();
        if (sessions.Count == 0) return null;
        var sp = new StackPanel();
        sp.Children.Add(Eyebrow($"YOUR HISTORY WITH {_game.Name.ToUpperInvariant()}", 6));
        foreach (var s in sessions)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(Diamond(s.Verdict switch { FixVerdict.Improved => "Success", FixVerdict.Worse => "Danger", null => "Subtle", _ => "Muted" }, 7));
            var verdict = s.Verdict switch { FixVerdict.Improved => "improved", FixVerdict.Worse => "got worse", FixVerdict.Same => "no measurable improvement", FixVerdict.Inconclusive => "inconclusive", _ => "not re-tested" };
            var fixes = s.Applied.Count == 0 ? "nothing changed" : $"{s.Applied.Count} fix(es) applied{(s.Undone.Count > 0 ? $", {s.Undone.Count} undone" : "")}";
            row.Children.Add(new TextBlock { Text = $"{s.At:g}  ·  {FixMyGame.Label(s.Problem)}  ·  {(s.Deep ? "Deep" : "Quick")} scan  ·  {fixes}  ·  {verdict}", FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = s.Findings.Count == 0 ? null : "Found: " + string.Join("; ", s.Findings) });
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.Muted("Kept on this PC only (fixmygame.json in Money Tweaks' data folder). Earlier results shape the suggestions.", 10.5));
        return new Border { Margin = new Thickness(0, 6, 0, 0), Child = sp };
    }

    // ---- step 2: scan mode --------------------------------------------------------------------------------------------------
    private void ShowMode()
    {
        var box = new StackPanel();
        box.Children.Add(StepTitle(FixMyGame.Label(_problem), _game is null ? "No specific game" : $"{_game.Name}{(_game.Running ? " · running" : " · not running")}"));
        bool game = FixMyGame.NeedsGame(_problem), net = FixMyGame.NeedsNetwork(_problem);
        var quickLines = new List<string> { "Windows power, gaming and input settings", "Display refresh rate and modes", "Graphics driver" };
        if (FixMyGame.NeedsCrashes(_problem)) quickLines.Add("Crash logs of the last 7 days (game, driver, Windows)");
        if (_game is not null) quickLines.Add($"Your history with {_game.Name}");
        var deepLines = new List<string> { "Everything in Quick Scan" };
        if (game) deepLines.Add("20 s live measurement while you play: FPS, 1% lows, frame times, CPU/GPU load, clocks, temperatures, throttling, RAM, VRAM, disk");
        if (net) deepLines.Add("20 s network test: router and internet ping, jitter, packet loss");
        var tiles = (ResponsiveGrid)Ui.Grid(2, ModeTile("Quick Scan", "About 5 seconds · no game needed", quickLines, false),
            _problem == GameProblem.Crashes ? ModeTile("Deep Scan", "Crashes can't be measured live", ["Use Quick Scan: crashes are read from Windows' logs."], null) : ModeTile("Deep Scan", game ? "About 30 seconds · while you play" : "About 25 seconds", deepLines, true));
        tiles.MinItemWidth = 280;
        box.Children.Add(tiles);
        if (game)
        {
            var repeat = new CheckBox { Content = "Measure twice for a more reliable result (Deep Scan, +25 s)", IsChecked = _repeat, Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.Brush("Muted") };
            repeat.Checked += (_, _) => _repeat = true;
            repeat.Unchecked += (_, _) => _repeat = false;
            box.Children.Add(repeat);
            if (!Admin.IsElevated)
            {
                var warn = Ui.Muted("FPS, 1% lows, stutter and frame times need administrator rights (Windows only shares frame timing with administrators). Without them a Deep Scan uses CPU, GPU, memory and temperature readings only.", 11.5);
                warn.Foreground = Ui.Brush("Gold");
                warn.Margin = new Thickness(0, 8, 0, 0);
                box.Children.Add(warn);
                box.Children.Add(Ui.Row(Ui.Btn("Restart as admin", (_, _) => { if (Admin.RestartElevated()) Application.Current.Shutdown(); }, "GhostButton")));
            }
        }
        box.Children.Add(Ui.Row(Ui.Btn("‹ Back", (_, _) => ShowPick(), "GhostButton")));
        Swap(box);
    }

    private UIElement ModeTile(string title, string sub, IEnumerable<string> lines, bool? deep)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(deep is null ? "Subtle" : "Text") });
        sp.Children.Add(Ui.Muted(sub, 11.5));
        foreach (var l in lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(Diamond(deep is null ? "Subtle" : "Accent", 6));
            row.Children.Add(new TextBlock { Text = l, FontSize = 12, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(row);
        }
        if (deep is null) { var c = Ui.Card(sp, new Thickness(0, 0, 10, 0)); c.Opacity = 0.6; c.Padding = new Thickness(16); return c; }
        var card = Ui.ClickCard(sp, async () => { _deep = deep.Value; await ScanAsync(retest: false); }, new Thickness(0, 0, 10, 0));
        card.Padding = new Thickness(16);
        return card;
    }

    // ---- step 3: scanning ---------------------------------------------------------------------------------------------------
    private sealed class Check(string label)
    {
        public readonly TextBlock Glyph = new() { Text = "◇", Width = 22, FontSize = 13, Foreground = Ui.Brush("Subtle") };
        public readonly TextBlock Detail = new() { FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        public readonly DockPanel Row = new() { Margin = new Thickness(0, 3, 0, 3) };
        public string Label { get; } = label;
        public void Build() { Row.Children.Add(Glyph); Row.Children.Add(new TextBlock { Text = Label, FontSize = 12.5, Foreground = Ui.Brush("Text"), Width = 190 }); Row.Children.Add(Detail); }
        public void Running(string? detail = null)
        {
            Glyph.Text = "◆"; Glyph.Foreground = Ui.Brush("Accent"); if (detail is not null) Detail.Text = detail;
            if (!App.ReduceMotion) Glyph.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        public void Done(string? detail = null, string brush = "Success", string glyph = "✓")
        {
            Glyph.BeginAnimation(OpacityProperty, null); Glyph.Opacity = 1; Glyph.Text = glyph; Glyph.Foreground = Ui.Brush(brush); if (detail is not null) Detail.Text = detail;
        }
    }

    /// <summary>The scanner visual: a hairline grid, a sweeping band and the live reading drawn as it arrives.</summary>
    private sealed class ScanVisual : Grid
    {
        private readonly Polyline _line = new() { Stroke = Ui.Brush("Violet"), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Miter };
        private readonly List<double> _values = [];
        private readonly TextBlock _value = new() { FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 22, Foreground = Ui.Brush("Text"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 12, 0) };
        private readonly TextBlock _caption = new() { FontSize = 10.5, Foreground = Ui.Brush("Subtle"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 38, 12, 0) };
        private readonly Canvas _canvas = new() { ClipToBounds = true };
        private double _max = 1;

        public ScanVisual()
        {
            Height = 110;
            Margin = new Thickness(0, 0, 0, 12);
            Background = Ui.Brush("Surface");
            var grid = new DrawingBrush { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 24, 24), ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1), new RectangleGeometry(new Rect(0, 0, 24, 24))) };
            grid.Freeze();
            Children.Add(new Rectangle { Fill = grid });
            var band = new Rectangle { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0x8A, 0x6B, 0xFF), Color.FromArgb(0x40, 0x2F, 0xD8, 0xEE), 0) { GradientStops = { new(Color.FromArgb(0, 0x8A, 0x6B, 0xFF), 0), new(Color.FromArgb(0x38, 0x2F, 0xD8, 0xEE), 0.85), new(Color.FromArgb(0x90, 0x2F, 0xD8, 0xEE), 1) } } };
            var tt = new TranslateTransform(-140, 0);
            band.RenderTransform = tt;
            Children.Add(band);
            _canvas.Children.Add(_line);
            Children.Add(_canvas);
            Children.Add(_value);
            Children.Add(_caption);
            Children.Add(new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), IsHitTestVisible = false });
            Loaded += (_, _) =>
            {
                if (App.ReduceMotion) return;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-140, Math.Max(400, ActualWidth), TimeSpan.FromMilliseconds(1700)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            };
        }

        public void Add(double v, string value, string caption)
        {
            _values.Add(v);
            _max = Math.Max(_max, v * 1.15);
            _value.Text = value;
            _caption.Text = caption;
            double w = Math.Max(1, ActualWidth), h = Height - 16;
            int n = Math.Max(20, _values.Count);
            _line.Points = new PointCollection(_values.Select((x, i) => new Point(i * w / (n - 1), 8 + h - x / _max * h)));
        }
    }

    private bool _scanning;

    /// <summary>Runs a scan with the game picker locked, so the game can't change underneath a running measurement.</summary>
    private async Task ScanAsync(bool retest)
    {
        _scanning = true;
        _gamePick.IsEnabled = false;
        try { await ScanCoreAsync(retest); }
        finally { _scanning = false; _gamePick.IsEnabled = true; }
    }

    private async Task ScanCoreAsync(bool retest)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        bool live = _deep;
        bool game = live && FixMyGame.NeedsGame(_problem), net = live && FixMyGame.NeedsNetwork(_problem);
        if (game)
        {
            // Look again: the game may have started since the list was read.
            if (_game is not null && !_game.Running) { _games = await Task.Run(FixMyGameScan.Games); _game = _games.FirstOrDefault(g => g.Key == _game.Key) ?? _game; }
            if (_game is null || !_game.Running)
            {
                var picked = await PickRunningAsync();
                if (picked is null) return;
                _game = picked;
            }
        }
        var visual = new ScanVisual();
        var checks = new List<Check>();
        Check C(string label) { var c = new Check(label); c.Build(); checks.Add(c); return c; }
        var cGame = C("Game");
        var cSettings = retest ? null : C("Windows settings");
        var cDisplay = retest ? null : C("Display");
        var cDriver = retest ? null : C("Graphics driver");
        var cCrash = !retest && FixMyGame.NeedsCrashes(_problem) || _problem == GameProblem.Crashes ? C("Crash logs") : null;
        var cHistory = retest ? null : C("History");
        var runs = game ? (_repeat ? 2 : 1) : 0;
        var cCapture = Enumerable.Range(1, runs).Select(i => C(runs > 1 ? $"Live measurement {i}/{runs}" : "Live measurement")).ToList();
        var cNet = net ? C("Network test") : null;
        var box = new StackPanel();
        box.Children.Add(StepTitle(retest ? "Verification Lab: measuring again" : (live ? "Deep Scan" : "Quick Scan"), FixMyGame.Label(_problem) + (_game is null ? "" : " · " + _game.Name)));
        box.Children.Add(visual);
        foreach (var c in checks) box.Children.Add(c.Row);
        box.Children.Add(Ui.Row(Ui.Btn("Cancel", (_, _) => _cts?.Cancel(), "GhostButton")));
        Swap(box);
        cGame.Done(_game is null ? "no specific game" : $"{_game.Name}{(_game.Version is { } v ? " " + v : "")}{(_game.Running ? $" · process {_game.Pid}" : " · not running")}", _game is null ? "Subtle" : "Success", _game is null ? "–" : "✓");
        try
        {
            if (!retest)
            {
                var map = new Dictionary<string, Check?> { ["settings"] = cSettings, ["display"] = cDisplay, ["driver"] = cDriver, ["crashes"] = cCrash, ["history"] = cHistory };
                Check? current = null;
                var progress = new Progress<string>(step => { current?.Done(); current = map.GetValueOrDefault(step); current?.Running(); visual.Add(checks.IndexOf(current!) + 1, $"{checks.Count(c => c.Glyph.Text == "✓")}/{checks.Count}", "checks"); });
                var ctx = await Task.Run(() => FixMyGameScan.Quick(_game, cCrash is not null, progress, ct), ct);
                current?.Done();
                _ctx = ctx;
                cSettings?.Done($"{ctx.Findings.Count} finding(s)");
                cDisplay?.Done(ctx.Displays.FirstOrDefault(d => d.Primary) is { } d ? $"{d.MonitorName}: {d.Current}" : "not read", ctx.Displays.Count > 0 ? "Success" : "Gold", ctx.Displays.Count > 0 ? "✓" : "!");
                cDriver?.Done(ctx.GpuDriver is { } drv ? $"{ctx.GpuName} · {drv}" : "not read", ctx.GpuDriver is null ? "Gold" : "Success", ctx.GpuDriver is null ? "!" : "✓");
                cCrash?.Done(ctx.Crashes is { } cr ? $"{cr.Incidents.Count} incident(s) in 7 days" : "couldn't be read", ctx.Crashes is null ? "Gold" : "Success", ctx.Crashes is null ? "!" : "✓");
                cHistory?.Done(_game is null ? "no game picked" : $"{ctx.History.For(_game.Key).Count()} earlier run(s)");
            }
            else if (_problem == GameProblem.Crashes && cCrash is not null)
            {
                cCrash.Running();
                var report = await Task.Run(() => Core.Health.CrashDetective.Analyze(Core.Health.CrashDetective.Collect(7), DateTime.Now), ct);
                cCrash.Done($"{report.Incidents.Count} incident(s) in 7 days");
                _ctx = new FixContext { Game = _ctx?.Game ?? _game, Findings = _ctx?.Findings ?? [], Displays = _ctx?.Displays ?? [], History = _ctx?.History ?? FixHistory.Load(), Crashes = report, GpuDriver = _ctx?.GpuDriver };
            }

            var measurements = new List<FixMeasurement>();
            for (int i = 0; i < runs; i++)
            {
                var (pid, name) = (_game!.Pid!.Value, _game.Name);
                var cc = cCapture[i];
                cc.Running($"keep playing {name}…");
                double hz = StressScene.RefreshRate() is > 1 and var h ? h : 60;
                var cap = await GameCapture.RunAsync(pid, name, 20, hz, new Progress<(int second, TelemetrySample s)>(x =>
                {
                    cc.Detail.Text = $"{x.second}/20 s · " + (x.s.Frames > 0 ? $"{x.s.Frames} FPS · " : "") + $"GPU {x.s.GpuUtil ?? 0:0}% · busiest CPU core {x.s.CpuBusiestCore:0}%" +
                        (x.s.GpuTempC is { } gt ? $" · GPU {gt:0} °C" : "") + (x.s.CpuTempC is { } c ? $" · CPU {c:0} °C" : "");
                    if (x.s.Frames > 0) visual.Add(x.s.Frames, $"{x.s.Frames}", "FPS (live)");
                    else visual.Add(x.s.GpuUtil ?? 0, $"{x.s.GpuUtil ?? 0:0}%", "GPU busy (live)");
                }), ct);
                var hw = await Task.Run(() => TuningDetector.Cached() is { } inv ? HardwareFactsDetector.Detect(inv, _game.InstallDir)
                    : new HardwareFacts { Threads = Environment.ProcessorCount, RamGb = Math.Round(MemoryInfo.Status().total / 1073741824.0) }, ct);
                var dx = BottleneckEngine.Diagnose(cap, hw, new AdvisorGoal { Game = name, TargetFps = hz });
                measurements.Add(FixMyGame.FromCapture(_problem, cap, dx, hz));
                cc.Done(dx.Frames is { } fr ? $"{fr.AvgFps:0} FPS average · 1% low {fr.Low1Fps:0}" : "done (no frame timing)");
                if (i + 1 < runs) await Task.Delay(3000, ct);
            }
            FixMeasurement m = measurements.Count > 0 ? FixMyGame.Combine(measurements) : FixMyGame.QuickOnly(_problem, live ? "Network" : "Quick Scan");
            if (net)
            {
                cNet!.Running("pinging your router and two internet servers…");
                var r = await NetworkDiagnostics.RunAsync(20, new Progress<int>(pct => cNet.Detail.Text = $"{pct * 20 / 100}/20 s"), ct);
                m = measurements.Count > 0 ? FixMyGame.Merge(m, r) : FixMyGame.FromNetwork(r, _problem);
                cNet.Done(r.Connection);
            }
            if (!retest) ShowResults(m);
            else { _after = m; ShowVerification(); }
        }
        catch (OperationCanceledException) { if (retest) ShowResults(); else ShowMode(); }
        catch (Exception ex)
        {
            Log.Error("Fix My Game scan failed", ex);
            Log.Debug("Fix My Game scan failed: " + ex);
            Swap(StepTitle("The scan stopped", FixMyGame.Label(_problem)), Ui.Muted(ex.Message, 12.5), Ui.Row(Ui.Btn("Back", (_, _) => ShowMode(), "GhostButton")));
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>Asks for a running game: one from the library, or the window in front after a countdown.</summary>
    private async Task<GameContext?> PickRunningAsync()
    {
        var running = _games.Where(g => g.Running).ToList();
        if (running.Count == 1 && _gamePick.SelectedItem as string != FrontItem) return running[0];
        var box = new StackPanel();
        box.Children.Add(StepTitle("Start your game", "A Deep Scan measures the game while it runs"));
        box.Children.Add(Ui.Muted(running.Count == 0
            ? "No game from your Game Library is running. Start the game and get into a normal match or scene, then press ↻ above, or use the countdown and switch to the game."
            : "Several games are running. Pick one above, or use the countdown and switch to the game you want measured.", 12.5));
        var status = new TextBlock { FontSize = 13, Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 8, 0, 0) };
        box.Children.Add(status);
        var tcs = new TaskCompletionSource<GameContext?>();
        box.Children.Add(Ui.Row(
            Ui.Btn("Countdown, then measure the window in front", async (_, _) =>
            {
                for (int i = 5; i > 0; i--) { status.Text = $"Switch to your game now… {i}"; await Task.Delay(1000); }
                GetWindowThreadProcessId(GetForegroundWindow(), out var fpid);
                var g = (int)fpid == Environment.ProcessId ? null : await Task.Run(() => FixMyGameScan.FromProcess((int)fpid));
                if (g is null || g.Exe is "explorer.exe") { status.Text = "No game was in front. Switch to the game during the countdown."; status.Foreground = Ui.Brush("Gold"); return; }
                tcs.TrySetResult(g);
            }, "PrimaryButton"),
            Ui.Btn("‹ Back", (_, _) => { tcs.TrySetResult(null); ShowMode(); }, "GhostButton")));
        Swap(box);
        return await tcs.Task;
    }

    // ---- step 4: results ----------------------------------------------------------------------------------------------------
    private void ShowResults(FixMeasurement? m = null)
    {
        if (m is not null)
        {
            _before = m;
            _ctx ??= new FixContext { History = FixHistory.Load() };
            _plan = FixMyGame.Plan(m, _ctx);
            _selected.Clear();
            foreach (var a in _plan.Actions.Where(a => a.Kind is FixKind.Tweak)) _selected.Add(a.Id);
            _session = new FixSession
            {
                GameKey = _game?.Key ?? "", GameName = _game?.Name ?? "", GameVersion = _game?.Version, Problem = _problem, Deep = m.Live,
                Findings = [.. _plan.Causes.Select(c => $"{c.Severity}: {c.Title}")], Before = m.Metrics, GpuDriver = _ctx.GpuDriver, WindowsBuild = _ctx.WindowsBuild,
            };
            if (_game is not null) _ctx.History.Save(_session);
            Log.Info($"Fix My Game: {FixMyGame.Label(_problem)} on {_game?.Name ?? "no game"} ({(m.Live ? "deep" : "quick")}): {_plan.Causes.Count} finding(s), top: {_plan.Causes.FirstOrDefault()?.Title}");
        }
        if (_plan is null || _before is null) { ShowPick(); return; }
        var box = new StackPanel();
        box.Children.Add(StepTitle("What we found", $"{FixMyGame.Label(_problem)} · {(_game?.Name ?? "no specific game")} · {(_before.Live ? $"Deep Scan ({_before.Runs} run{(_before.Runs > 1 ? "s" : "")})" : "Quick Scan")} · {_before.At:t}"));
        box.Children.Add(SeverityStrip(_plan.Causes));
        if (_before.Live && _before.Metrics.Count > 0) box.Children.Add(KeyNumbers(_before));

        var important = _plan.Causes.Where(c => c.Severity > FixSeverity.Info || c.Status == FixStatus.NotEnoughEvidence).ToList();
        var info = _plan.Causes.Except(important).ToList();
        int k = 0;
        foreach (var c in important) box.Children.Add(FindingCard(c, k++));
        if (info.Count > 0)
        {
            var ex = new Expander { Header = new TextBlock { Text = $"Informational ({info.Count}): readings that are fine or matter little", FontSize = 12, Foreground = Ui.Brush("Muted") }, Margin = new Thickness(0, 4, 0, 0), IsExpanded = important.Count == 0 };
            var sp = new StackPanel();
            foreach (var c in info) sp.Children.Add(FindingCard(c, k++));
            ex.Content = sp;
            box.Children.Add(ex);
        }

        box.Children.Add(Eyebrow(_plan.Actions.Count == 0 ? "NO RECOMMENDED FIXES" : $"BEST FIXES ({_plan.Actions.Count})", 18));
        if (_plan.Actions.Count == 0)
            box.Children.Add(Ui.Muted("Nothing found supports a specific fix, so Money Tweaks isn't suggesting one. " +
                (_problem == GameProblem.HighPing ? "Run the test again while the lag is happening." : _before.Live ? "Measuring a heavier or more typical scene, while the problem happens, can show more." : "A Deep Scan while you play can show more."), 12.5));
        var actions = (ResponsiveGrid)Ui.Grid(3);
        actions.MinItemWidth = 280;
        int n = 1;
        foreach (var a in _plan.Actions) actions.Children.Add(ActionCard(a, n++, compact: false));
        box.Children.Add(actions);
        if (_plan.More.Count > 0)
        {
            var ex = new Expander { Header = new TextBlock { Text = $"More things that may help ({_plan.More.Count})", FontSize = 12, Foreground = Ui.Brush("Muted") }, Margin = new Thickness(0, 4, 0, 0) };
            var more = (ResponsiveGrid)Ui.Grid(3);
            more.MinItemWidth = 280;
            foreach (var a in _plan.More) more.Children.Add(ActionCard(a, n++, compact: true));
            ex.Content = more;
            box.Children.Add(ex);
        }
        foreach (var note in _plan.Notes) box.Children.Add(new TextBlock { Text = "ⓘ " + note, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (_ctx?.Unavailable.Count > 0) box.Children.Add(new TextBlock { Text = "ⓘ Not available on this PC: " + string.Join("; ", _ctx.Unavailable), FontSize = 11, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var bar = Ui.Row();
        bar.Margin = new Thickness(0, 18, 0, 0);
        var selectable = _plan.Actions.Concat(_plan.More).Where(a => a.Kind is FixKind.Tweak or FixKind.Profile && !_applied.Any(x => x.action.Id == a.Id)).ToList();
        if (selectable.Count > 0)
        {
            int count = selectable.Count(a => _selected.Contains(a.Id));
            var apply = Ui.Btn($"Review and apply selected ({count})", async (_, _) => await ApplySelectedAsync(), "PrimaryButton");
            apply.IsEnabled = count > 0;
            bar.Children.Add(apply);
        }
        if (_problem == GameProblem.Crashes) bar.Children.Add(Ui.Btn("Check the logs again", async (_, _) => await ScanAsync(retest: true), _applied.Count > 0 ? "PrimaryButton" : "GhostButton"));
        else if (_before.Live) bar.Children.Add(Ui.Btn(_applied.Count > 0 ? "Verify: measure again" : "Re-test", async (_, _) => await ScanAsync(retest: true), _applied.Count > 0 ? "PrimaryButton" : "GhostButton"));
        else bar.Children.Add(Ui.Btn("Run a Deep Scan", async (_, _) => { _deep = true; _ctx = null; await ScanAsync(retest: false); }, "GhostButton"));
        if (_applied.Count > 0) bar.Children.Add(Ui.Btn("Undo everything applied here", async (_, _) => await UndoAllAsync(), "GhostButton"));
        bar.Children.Add(Ui.Btn("Report", (_, _) => ShowReport(), "GhostButton"));
        bar.Children.Add(Ui.Btn("Start over", (_, _) => ShowPick(), "GhostButton"));
        box.Children.Add(bar);
        if (!_before.Live && _problem != GameProblem.Crashes)
            box.Children.Add(Ui.Muted("Verification compares two Deep Scans: run one before applying fixes so there's a baseline to compare against.", 11.5));
        Swap(box);
    }

    private static UIElement SeverityStrip(IReadOnlyList<FixCause> causes)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var s in new[] { FixSeverity.Critical, FixSeverity.High, FixSeverity.Medium, FixSeverity.Info })
        {
            int n = causes.Count(c => c.Severity == s && c.Status != FixStatus.NotEnoughEvidence);
            var b = new Border { BorderBrush = Ui.Brush(n > 0 ? SeverityBrush(s) : "BorderBrush"), BorderThickness = new Thickness(1, 1, 1, 3), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Background = Ui.Brush("Surface") };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = n.ToString(), FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 20, Foreground = Ui.Brush(n > 0 ? (s == FixSeverity.Info ? "Text" : SeverityBrush(s)) : "Subtle") });
            sp.Children.Add(new TextBlock { Text = s == FixSeverity.Info ? "INFO" : s.ToString().ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Muted") });
            b.Child = sp;
            w.Children.Add(b);
        }
        return w;
    }

    /// <summary>The headline numbers of a Deep Scan, as a compact row (only what was measured).</summary>
    private static UIElement KeyNumbers(FixMeasurement m)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        void N(string key, string label, string unit, string fmt = "0")
        {
            if (!m.Metrics.TryGetValue(key, out var v)) return;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
            sp.Children.Add(new TextBlock { Text = v.ToString(fmt) + unit, FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 17, Foreground = Ui.Brush("Text") });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = Ui.Brush("Subtle") });
            w.Children.Add(sp);
        }
        N("Average FPS", "average FPS", ""); N("1% low FPS", "1% low", ""); N("Stutter frames (%)", "long frames", "%", "0.#");
        N("GPU busy (median %)", "GPU busy", "%"); N("Busiest CPU core (median %)", "busiest CPU core", "%");
        N("GPU temp (max °C)", "GPU peak", " °C"); N("CPU temp (max °C)", "CPU peak", " °C");
        N("Internet ping (ms)", "ping", " ms"); N("Internet jitter (ms)", "jitter", " ms", "0.#"); N("Internet packet loss (%)", "packet loss", "%", "0.#");
        return w;
    }

    private static UIElement FindingCard(FixCause c, int index)
    {
        var tone = SeverityBrush(c.Severity);
        var card = new Border { Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Rectangle { Fill = Ui.Brush(tone) });
        var sp = new StackPanel { Margin = new Thickness(14, 10, 12, 10) };
        Grid.SetColumn(sp, 1);
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = (c.Severity == FixSeverity.Info ? "INFO" : c.Severity.ToString().ToUpperInvariant()), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Ui.Brush(tone == "Muted" ? "Subtle" : tone), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = c.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(Ui.Tag(StatusText(c.Status), c.Status == FixStatus.Confirmed ? "Success" : c.Status == FixStatus.NotEnoughEvidence ? "Muted" : "Violet"));
        if (c.Status != FixStatus.NotEnoughEvidence) head.Children.Add(Ui.Tag($"{c.Confidence} confidence", "Muted"));
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = c.Plain, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        if (c.Evidence.Count > 0 || c.Alternatives.Count > 0 || c.Technical is not null)
        {
            var ex = new Expander { Header = new TextBlock { Text = "Evidence and details", FontSize = 11.5, Foreground = Ui.Brush("Subtle") }, Margin = new Thickness(0, 6, 0, 0) };
            var d = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
            void Section(string title, IEnumerable<string> lines, bool mono = false)
            {
                var l = lines.ToList();
                if (l.Count == 0) return;
                d.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 6, 0, 2) });
                foreach (var x in l) d.Children.Add(new TextBlock { Text = (mono ? "" : "• ") + x, FontSize = mono ? 10.5 : 11.5, FontFamily = mono ? Ui.Res<FontFamily>("MonoFont") : Ui.Res<FontFamily>("UiFont"), Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
            }
            Section("WHAT WAS MEASURED OR READ", c.Evidence);
            Section("OTHER POSSIBLE EXPLANATIONS", c.Alternatives);
            if (c.Technical is { } t) Section("TECHNICAL", [t], mono: true);
            ex.Content = d;
            sp.Children.Add(ex);
        }
        grid.Children.Add(sp);
        card.Child = grid;
        if (!App.ReduceMotion)
        {
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(60 * Math.Min(index, 8)) });
        }
        return card;
    }

    private UIElement ActionCard(FixAction a, int n, bool compact)
    {
        var sp = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        bool done = _applied.Any(x => x.action.Id == a.Id);
        if (a.Kind is FixKind.Tweak or FixKind.Profile && !done)
        {
            var pick = new CheckBox { IsChecked = _selected.Contains(a.Id), ToolTip = "Include in “Review and apply selected”", VerticalAlignment = VerticalAlignment.Center };
            pick.Checked += (_, _) => { _selected.Add(a.Id); ShowResults(); };
            pick.Unchecked += (_, _) => { _selected.Remove(a.Id); ShowResults(); };
            DockPanel.SetDock(pick, Dock.Right);
            top.Children.Add(pick);
        }
        top.Children.Add(new TextBlock { Text = n.ToString(), FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 18, Foreground = Ui.Brush("AccentGradient"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(a.Kind switch
        {
            FixKind.Tweak => Ui.Tag("Changes a Windows setting · undoable", "Gold"),
            FixKind.Profile => Ui.Tag("Turns on the game's profile · undoable", "Gold"),
            FixKind.Tool => Ui.Tag("Opens a Money Tweaks tool", "Violet"),
            _ => Ui.Tag("You do this", "Muted"),
        });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock { Text = a.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = a.Plain, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        void Line(string label, string text, string brush = "Subtle")
        {
            var t = new TextBlock { FontSize = 11.5, Foreground = Ui.Brush(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            t.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = Ui.Brush("Muted"), FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(text));
            sp.Children.Add(t);
        }
        Line("Expect", a.Expect);
        if (!compact)
        {
            Line("Risk", a.Risk);
            if (a.Restart) Line("Restart", "Needed to take full effect.", "Gold");
            Line("Undo", a.Reversible);
        }
        if (a.History is { } h) Line("Before", h, "Gold");
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var preview = new TextBlock { FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 10.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        if (a.Kind == FixKind.Tool && a.PageTag is { } tag)
            buttons.Children.Add(Ui.Btn("Open " + (AppNavigation.Find(tag)?.Label ?? tag), (_, _) => MainWindow.Current?.Navigate(tag), "GhostButton"));
        if (a.Kind == FixKind.Tweak && a.TweakId is { } id && TweakCatalog.Find(id) is { } t)
        {
            Button? pb = null;
            pb = Ui.Btn("Preview change", async (_, _) =>
            {
                if (preview.Visibility == Visibility.Visible) { preview.Visibility = Visibility.Collapsed; pb!.Content = "Preview change"; return; }
                preview.Text = "Reading current values…";
                preview.Visibility = Visibility.Visible;
                pb!.Content = "Hide preview";
                preview.Text = await PreviewAsync(t);
            }, "GhostButton");
            buttons.Children.Add(pb);
        }
        if (done && _applied.First(x => x.action.Id == a.Id) is var applied)
        {
            var ok = new TextBlock { Text = "✓ Applied here", FontSize = 11.5, Foreground = Ui.Brush("Success"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            buttons.Children.Insert(0, ok);
            buttons.Children.Add(Ui.Btn("Undo", async (_, _) => { await UndoAsync(applied); ShowResults(); }, "GhostButton"));
        }
        sp.Children.Add(buttons);
        sp.Children.Add(preview);
        var card = Ui.Card(sp, new Thickness(0, 0, 10, 10));
        card.Padding = new Thickness(16, 14, 16, 14);
        if (done) card.BorderBrush = Ui.Brush("Success");
        return card;
    }

    private static async Task<string> PreviewAsync(Tweak t)
    {
        try { return await Task.Run(() => TweakXRay.Inspect(t).Compact()); }
        catch (Exception ex) { return "Couldn't read the current values: " + ex.Message; }
    }

    // ---- apply / undo -------------------------------------------------------------------------------------------------------
    /// <summary>
    /// One review of everything selected (current → proposed values read right now, risk, restart, undo), then the tweaks
    /// through TweakService's batch (Conflict Radar, admin check, restore point, backup, read-back) and the profile through
    /// ProfileService, with a progress row per fix.
    /// </summary>
    private async Task ApplySelectedAsync()
    {
        if (_plan is null) return;
        var picked = _plan.Actions.Concat(_plan.More).Where(a => _selected.Contains(a.Id) && a.Kind is FixKind.Tweak or FixKind.Profile && !_applied.Any(x => x.action.Id == a.Id)).ToList();
        if (picked.Count == 0) return;
        var box = new StackPanel();
        box.Children.Add(StepTitle("Review", $"{picked.Count} fix(es) · reading current values…"));
        Swap(box);
        var review = new System.Text.StringBuilder();
        var items = new List<(FixAction a, TweakItem? item, GamingProfile? profile)>();
        foreach (var a in picked)
        {
            if (a.Kind == FixKind.Tweak && TweakCatalog.Find(a.TweakId!) is { } t)
            {
                var item = new TweakItem(t);
                await item.RefreshAsync();
                if (item.State == TweakState.Applied) { review.AppendLine($"• {t.Name}: already active on this PC, skipped."); continue; }
                review.AppendLine($"• {t.Name}{(t.RequiresReboot ? " (restart needed)" : "")}");
                review.AppendLine("  " + (await PreviewAsync(t)).Replace("\n", "\n  "));
                review.AppendLine($"  Risk: {a.Risk}  Undo: {a.Reversible}");
                items.Add((a, item, null));
            }
            else if (a.Kind == FixKind.Profile && ProfileStore.All(ProfileService.File).FirstOrDefault(p => p.Id == a.ProfileId) is { } prof)
            {
                review.AppendLine($"• Gaming profile “{prof.Name}”: {prof.TweakIds.Count} tweak(s){(prof.Power != PowerChoice.Keep ? ", power plan" : "")}{(prof.TimerResolutionMs is { } ms ? $", {ms} ms timer" : "")}. Turning it off restores and verifies everything.");
                items.Add((a, null, prof));
            }
        }
        if (items.Count == 0) { MainWindow.Current?.ShowToast("Everything selected is already active.", "Accent"); ShowResults(); return; }
        if (!Dialog.Confirm($"Apply {items.Count} fix(es)?", review + "\nEvery change is backed up first and can be undone from Fix My Game or where it lives. Nothing else is changed.", "Apply", items.Any(x => x.a.Risk.StartsWith("Caution", StringComparison.Ordinal)) ? DialogKind.Warning : DialogKind.Confirm))
        { ShowResults(); return; }

        // Progress rows.
        box = new StackPanel();
        box.Children.Add(StepTitle("Applying", $"{items.Count} fix(es)"));
        var bar = new ProgressBar { Maximum = items.Count, Height = 3, Margin = new Thickness(0, 0, 0, 10), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 520 };
        box.Children.Add(bar);
        var rows = items.ToDictionary(x => x.a.Id, x => { var c = new Check(x.a.Title); c.Build(); box.Children.Add(c.Row); return c; });
        var status = Ui.Muted("", 11.5);
        box.Children.Add(status);
        Swap(box);

        var tweakItems = items.Where(x => x.item is not null).ToList();
        if (tweakItems.Count > 0)
        {
            foreach (var x in tweakItems) rows[x.a.Id].Running();
            var (ok, failed, reboot) = await TweakService.BatchAsync([.. tweakItems.Select(x => x.item!)], apply: true, new Progress<string>(s => status.Text = s));
            foreach (var x in tweakItems)
            {
                await x.item!.RefreshAsync();
                if (x.item.State == TweakState.Applied) { rows[x.a.Id].Done(x.item.Message ?? "applied and verified"); _applied.Add((x.a, x.item)); _session?.Applied.Add(x.a.Id); }
                else rows[x.a.Id].Done(x.item.Message ?? "not applied", "Danger", "✗");
                bar.Value++;
            }
            if (reboot) status.Text = "Some changes need a restart to take full effect.";
        }
        foreach (var x in items.Where(x => x.profile is not null))
        {
            rows[x.a.Id].Running();
            bool on = await ProfileService.ActivateAsync(x.profile!, null, confirm: false, _game?.Key);
            if (on && ProfileService.Active?.ProfileId == x.profile!.Id) { rows[x.a.Id].Done("on"); _applied.Add((x.a, null)); _session?.Applied.Add(x.a.Id); }
            else rows[x.a.Id].Done("not turned on (see the message above)", "Danger", "✗");
            bar.Value++;
        }
        SaveSession();
        Log.Info($"Fix My Game: applied {_applied.Count} fix(es) for {FixMyGame.Label(_problem)} on {_game?.Name ?? "no game"}");
        box.Children.Add(Ui.Row(Ui.Btn("Continue", (_, _) => ShowResults(), "PrimaryButton")));
    }

    private async Task UndoAsync((FixAction action, TweakItem? item) x)
    {
        bool undone;
        if (x.item is { } item)
        {
            await item.RefreshAsync();
            if (item.State is TweakState.Applied or TweakState.Partial) await TweakService.ToggleAsync(item);
            undone = item.State is not (TweakState.Applied or TweakState.Partial);
            if (!undone) MainWindow.Current?.ShowToast($"“{item.Name}” wasn't undone: {item.Message}", "Gold");
        }
        else
        {
            undone = ProfileService.Active?.ProfileId != x.action.ProfileId || await ProfileService.DeactivateAsync();
        }
        if (!undone) return;
        _applied.Remove(x);
        _undone.Add(x.action.Title);
        _session?.Undone.Add(x.action.Id);
        SaveSession();
    }

    private async Task UndoAllAsync()
    {
        var names = string.Join("\n", _applied.Select(x => "• " + x.action.Title));
        if (!Dialog.Confirm("Undo everything applied here?", names + "\n\nEach is restored from the backup taken before it was applied and read back.", "Undo")) return;
        foreach (var x in _applied.ToList()) await UndoAsync(x);
        if (_after is not null) ShowVerification(); else ShowResults();
    }

    private void SaveSession() { if (_session is not null && _game is not null) (_ctx?.History ?? FixHistory.Load()).Save(_session); }

    // ---- step 5: Verification Lab -------------------------------------------------------------------------------------------
    private void ShowVerification()
    {
        if (_before is null || _after is null) { ShowResults(); return; }
        var box = new StackPanel();
        box.Children.Add(StepTitle("Verification Lab", $"{FixMyGame.Label(_problem)} · before {_before.At:t} ({_before.Runs} run{(_before.Runs > 1 ? "s" : "")}), after {_after.At:t} ({_after.Runs} run{(_after.Runs > 1 ? "s" : "")})"));
        if (_problem == GameProblem.Crashes)
        {
            var since = _session?.At ?? _before.At;
            var exe = _game?.Exe;
            var fresh = _ctx?.Crashes?.Incidents.Where(i => i.Time > since && (exe is null || i.App?.Equals(exe, StringComparison.OrdinalIgnoreCase) == true || i.Kind is not (Core.Health.CrashKind.AppCrash or Core.Health.CrashKind.AppHang))).ToList() ?? [];
            box.Children.Add(VerdictHeader(fresh.Count == 0 ? "No new crashes yet" : $"{fresh.Count} new crash(es)", fresh.Count == 0 ? "Text" : "Danger", "Inconclusive until you've played a while", "Muted"));
            box.Children.Add(Ui.Muted(fresh.Count == 0 ? $"Nothing new was logged since {since:g}. Crashes can't be confirmed fixed in minutes: play normally and check again later." : string.Join("\n", fresh.Select(i => $"{i.Time:g}: {i.Title}")), 12.5));
            _session!.Verdict = FixVerdict.Inconclusive;
        }
        else
        {
            _cmp = FixMyGame.Compare(_before, _after);
            Log.Info($"Fix My Game verification: {FixMyGame.Label(_problem)}: {_cmp.Verdict}, still a problem: {_cmp.StillAProblem}. {_cmp.Summary}");
            var (brush, sub, subBrush) = (_cmp.Verdict switch { FixVerdict.Improved => "Success", FixVerdict.Worse => "Danger", _ => "Text" },
                _cmp.StillAProblem ? "Still a problem" : _cmp.HadProblem ? "Looks resolved" : "No problem measured", _cmp.StillAProblem ? "Gold" : "Success");
            box.Children.Add(VerdictHeader(_cmp.VerdictLabel, brush, sub, subBrush));
            box.Children.Add(Ui.Muted(_cmp.Summary, 12.5));
            box.Children.Add(Bars(_cmp));
            foreach (var n in _cmp.Notes) box.Children.Add(new TextBlock { Text = "ⓘ " + n, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            if (_session is not null) { _session.Verdict = _cmp.Verdict; _session.StillAProblem = _cmp.StillAProblem; _session.After = _after.Metrics; }
        }
        SaveSession();
        if (_applied.Count > 0) box.Children.Add(new TextBlock { Text = "Changed here: " + string.Join(", ", _applied.Select(x => x.action.Title)), FontSize = 12, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        var bar = Ui.Row();
        bar.Margin = new Thickness(0, 16, 0, 0);
        if (_applied.Count > 0)
            bar.Children.Add(Ui.Btn(_cmp?.Verdict == FixVerdict.Improved ? "Undo the changes anyway" : "Undo the changes made here", async (_, _) => await UndoAllAsync(), _cmp?.Verdict is FixVerdict.Worse or FixVerdict.Same ? "GoldButton" : "GhostButton"));
        bar.Children.Add(Ui.Btn("Measure again", async (_, _) => await ScanAsync(retest: true), "GhostButton"));
        bar.Children.Add(Ui.Btn("Back to the findings", (_, _) => ShowResults(), "GhostButton"));
        bar.Children.Add(Ui.Btn("Report", (_, _) => ShowReport(), "GhostButton"));
        bar.Children.Add(Ui.Btn("Done", (_, _) => ShowPick(), "GhostButton"));
        box.Children.Add(bar);
        Swap(box);
    }

    private static UIElement VerdictHeader(string verdict, string brush, string sub, string subBrush)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        w.Children.Add(new TextBlock { Text = verdict, FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(brush), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        w.Children.Add(Ui.Tag(sub, subBrush));
        return w;
    }

    /// <summary>Before/after as paired angular bars that grow in; coloured only beyond normal run-to-run variation.</summary>
    private static UIElement Bars(FixComparison cmp)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });   // fixed: a star column collapses in a left-aligned grid
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        int r = 0;
        foreach (var (metric, b, a, unit, higher) in cmp.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            double max = Math.Max(Math.Abs(b ?? 0), Math.Abs(a ?? 0));
            if (max <= 0) max = 1;
            // Usage isn't better or worse by itself (high GPU use can be ideal), so those rows stay neutral.
            bool neutral = metric.Contains("busy", StringComparison.Ordinal) || metric.Contains("core", StringComparison.Ordinal);
            string tone = !neutral && b is { } x && a is { } y && Math.Abs(y - x) > Math.Max(0.05 * Math.Abs(x), unit is " ms" ? 1 : unit is " °C" ? 3 : unit is "%" ? 0.5 : 2) ? ((y > x) == higher ? "Success" : "Danger") : "Violet";
            var label = new TextBlock { Text = metric.Replace(" (median %)", "").Replace(" (max °C)", "").Replace(" (%)", "").Replace(" (ms)", "").Replace(" (max GB)", "").Replace(" (GB)", ""), FontSize = 11.5, Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r); grid.Children.Add(label);
            var pair = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            pair.Children.Add(Bar(b, max, "Subtle"));
            pair.Children.Add(Bar(a, max, tone));
            Grid.SetRow(pair, r); Grid.SetColumn(pair, 1); grid.Children.Add(pair);
            var vals = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            vals.Inlines.Add(new System.Windows.Documents.Run(b is { } bv ? bv.ToString("0.#") + unit : "n/a") { Foreground = Ui.Brush("Muted") });
            vals.Inlines.Add(new System.Windows.Documents.Run("  →  ") { Foreground = Ui.Brush("Subtle") });
            vals.Inlines.Add(new System.Windows.Documents.Run(a is { } av ? av.ToString("0.#") + unit : "n/a") { Foreground = Ui.Brush(tone == "Violet" ? "Text" : tone) });
            Grid.SetRow(vals, r); Grid.SetColumn(vals, 2); grid.Children.Add(vals);
            r++;
        }
        return grid;
    }

    private static UIElement Bar(double? v, double max, string brush)
    {
        var track = new Grid { Height = 6, Margin = new Thickness(0, 1, 0, 1), Background = Ui.Brush("Surface") };
        if (v is not { } x) return track;
        // Star columns size the bar from the value itself, so it never depends on when the layout pass happens.
        double share = Math.Clamp(Math.Abs(x) / max, 0.01, 1);
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share + 1e-6, GridUnitType.Star) });
        var fill = new Rectangle { Fill = Ui.Brush(brush), RenderTransformOrigin = new Point(0, 0.5) };
        var scale = new ScaleTransform(App.ReduceMotion ? 1 : 0, 1);
        fill.RenderTransform = scale;
        track.Children.Add(fill);
        if (!App.ReduceMotion) scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, BeginTime = TimeSpan.FromMilliseconds(120) });
        return track;
    }

    // ---- report -------------------------------------------------------------------------------------------------------------
    private void ShowReport()
    {
        if (_plan is null || _before is null) return;
        var text = FixMyGame.Report(_game, _plan, _before, _cmp, _applied.Select(x => x.action.Title), _undone);
        var box = new StackPanel();
        box.Children.Add(StepTitle("Repair report", "Everything this run found, changed and measured. Nothing is uploaded."));
        box.Children.Add(new TextBox { Text = text, IsReadOnly = true, FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) });
        box.Children.Add(Ui.Row(
            Ui.Btn("Copy", (_, _) => { try { Clipboard.SetText(text); MainWindow.Current?.ShowToast("Report copied.", "Success"); } catch (Exception ex) { MainWindow.Current?.ShowToast("Couldn't copy: " + ex.Message, "Gold"); } }, "PrimaryButton"),
            Ui.Btn("Save as text file…", (_, _) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"Fix My Game {DateTime.Now:yyyy-MM-dd HHmm}.txt", Filter = "Text file|*.txt" };
                if (dlg.ShowDialog() == true) try { File.WriteAllText(dlg.FileName, text); } catch (Exception ex) { Dialog.Show("Not saved", ex.Message, DialogKind.Error); }
            }, "GhostButton"),
            Ui.Btn("‹ Back", (_, _) => { if (_after is not null) ShowVerification(); else ShowResults(); }, "GhostButton")));
        Swap(box);
    }
}
