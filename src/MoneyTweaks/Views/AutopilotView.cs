using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Game Autopilot: the master switch and live session, the games with their rules, and the automation editor in three
/// phases (before the game, while playing, after) where every action is its own switch with what it does and how it's
/// undone. Runs on AutopilotService; settings go through the Gaming Profiles engine, so nothing here is a second system.
/// </summary>
public sealed class AutopilotView : UserControl, IPageLifecycle
{
    private readonly StackPanel _status = new(), _games = new(), _editor = new(), _history = new();
    private readonly ToggleButton _master = new() { Style = Ui.Res<Style>("Switch"), VerticalAlignment = VerticalAlignment.Center };
    private List<LibraryGame> _library = [];
    private string? _selected;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public AutopilotView()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = "Autopilot", FontSize = 13, Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(_master);
        _master.IsChecked = AutopilotService.File.Enabled;
        _master.Click += (_, _) => { AutopilotService.File.Enabled = _master.IsChecked == true; AutopilotService.Save(); AutopilotService.Refresh(); };
        var (root, body) = Ui.Page("Game Autopilot",
            "Prepares the PC when a game starts, runs only the actions you switch on while you play, and puts everything back when the game closes or crashes. Every temporary change is recorded before it's made, restored afterwards and read back; after an unexpected shutdown the session is recovered. One game is managed at a time.",
            head, eyebrow: "Gaming", icon: "\uE768");
        body.Children.Add(Ui.Hud(Ui.Card(_status, new Thickness(0, 0, 0, 14))));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "GAMES", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 8) });
        left.Children.Add(_games);
        left.Children.Add(Ui.Row(Ui.Btn("+ Add a game by its .exe…", (_, _) => AddExe(), "GhostButton")));
        grid.Children.Add(Ui.Card(left, new Thickness(0, 0, 14, 14)));
        var editorCard = Ui.Card(_editor, new Thickness(0, 0, 0, 14));
        Grid.SetColumn(editorCard, 1);
        grid.Children.Add(editorCard);
        body.Children.Add(grid);
        var hist = new StackPanel();
        hist.Children.Add(Ui.Section("Session history", "What Autopilot did in each session, how it ended and what it restored. Kept on this PC (autopilot.json)."));
        hist.Children.Add(_history);
        body.Children.Add(Ui.Card(hist));
        Content = root;
        _clock.Tick += (_, _) => { if (AutopilotService.File.Active is not null) RenderStatus(); };
        AutopilotService.Changed += OnChanged;
        Unloaded += (_, _) => _clock.Stop();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() => { RenderStatus(); RenderGames(); RenderHistory(); });

    public async void OnShown()
    {
        _clock.Start();
        _library = await Task.Run(() => GameLibrary.Items(GameLibrary.Current).OrderBy(g => g.Def.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        var running = await Task.Run(() => GameRuntime.Statuses(_library).Where(s => s.status == GameStatus.Running).Select(s => s.game.Key).ToHashSet());
        _runningKeys = running;
        _selected ??= _library.FirstOrDefault(g => running.Contains(g.Key))?.Key ?? AutopilotService.File.Rules.Keys.FirstOrDefault(k => _library.Any(g => g.Key == k)) ?? _library.FirstOrDefault()?.Key;
        RenderStatus(); RenderGames(); RenderEditor(); RenderHistory();
    }

    public void OnHidden() => _clock.Stop();
    private HashSet<string> _runningKeys = [];

    // ---- status -------------------------------------------------------------------------------------------------------------
    private void RenderStatus()
    {
        _status.Children.Clear();
        var f = AutopilotService.File;
        _master.IsChecked = f.Enabled;
        if (f.Active is { } s)
        {
            var top = new DockPanel();
            var end = Ui.Btn("End session now", async (_, _) =>
            {
                if (Dialog.Confirm("End the session now?", $"Everything this session changed for {s.GameName} is restored right away. Autopilot won't start again until the game is restarted.", "End session")) await AutopilotService.EndNowAsync();
            }, "GhostButton", new Thickness(0));
            DockPanel.SetDock(end, Dock.Right);
            top.Children.Add(end);
            var title = new StackPanel();
            var live = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Rectangle { Width = 8, Height = 8, Fill = Ui.Brush("Success"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(45, 4, 4) };
            if (!App.ReduceMotion) dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            live.Children.Add(dot);
            live.Children.Add(new TextBlock { Text = "SESSION ACTIVE", Style = Ui.Res<Style>("Eyebrow") });
            title.Children.Add(live);
            var dur = DateTime.Now - s.StartedAt;
            title.Children.Add(new TextBlock { Text = $"{s.GameName}  ·  {(int)dur.TotalHours:0}:{dur.Minutes:00}:{dur.Seconds:00}", Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 2, 0, 0) });
            title.Children.Add(Ui.Muted($"Process {s.Pid}{(s.Exe is null ? "" : " · " + s.Exe)} · started {s.StartedAt:t}" +
                (s.PeakCpuTempC is { } c ? $" · CPU peak {c:0} °C" : "") + (s.PeakGpuTempC is { } g ? $" · GPU peak {g:0} °C" : ""), 12));
            top.Children.Add(title);
            _status.Children.Add(top);
            var cols = (ResponsiveGrid)Ui.Grid(3, List("DOING NOW", s.Actions, "Nothing changed (this game's rule only watches and records)."),
                List("CHECKED AT START", s.Readiness, "Readiness check off or still running."),
                List("WARNINGS", s.Warnings, "None so far."));
            cols.Margin = new Thickness(0, 12, 0, 0);
            _status.Children.Add(cols);
            if (s.Skipped.Count > 0) _status.Children.Add(new TextBlock { Text = "Left as is: " + string.Join(" ", s.Skipped), FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        }
        else
        {
            int rules = f.Rules.Values.Count(r => r.Enabled);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = Ui.Brush(f.Enabled && rules > 0 ? "Accent" : "Subtle"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(45, 4, 4) });
            row.Children.Add(new TextBlock { Text = !f.Enabled ? "AUTOPILOT OFF" : rules == 0 ? "NO GAMES SET UP" : "WATCHING", Style = Ui.Res<Style>("Eyebrow") });
            _status.Children.Add(row);
            _status.Children.Add(new TextBlock
            {
                Text = !f.Enabled ? "Turn Autopilot on (top right), then pick what it may do for each game." : rules == 0 ? "Pick a game on the left and choose a preset or your own actions." : $"Waiting for {string.Join(", ", f.Rules.Values.Where(r => r.Enabled).Select(r => r.GameName))} to start.",
                Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            _status.Children.Add(Ui.Muted(f.Enabled && rules > 0 ? "Checks every 3 seconds by verified process identity. Nothing runs between sessions." : "Autopilot uses no resources while it's off.", 12));
        }
        if (AutopilotService.Waiting.Count > 0)
            _status.Children.Add(new TextBlock { Text = $"Also running: {string.Join(", ", AutopilotService.Waiting)}. Its session starts when this one ends, so two sessions never change the same settings.", FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    }

    private static UIElement List(string title, IReadOnlyList<string> lines, string empty)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        sp.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 0, 0, 4) });
        if (lines.Count == 0) sp.Children.Add(Ui.Muted(empty, 11.5));
        foreach (var l in lines.TakeLast(6)) sp.Children.Add(new TextBlock { Text = "• " + l, FontSize = 11.5, Foreground = Ui.Brush(l.StartsWith("Check", StringComparison.Ordinal) ? "Gold" : "Muted"), TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    // ---- games --------------------------------------------------------------------------------------------------------------
    private void RenderGames()
    {
        _games.Children.Clear();
        var rules = AutopilotService.File.Rules;
        foreach (var g in _library.OrderByDescending(g => rules.TryGetValue(g.Key, out var r) && r.Enabled).ThenBy(g => g.Def.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var r = rules.GetValueOrDefault(g.Key);
            bool sel = g.Key == _selected, active = AutopilotService.File.Active?.GameKey == g.Key;
            var row = new DockPanel();
            var tag = r is { Enabled: true } ? Ui.Tag(r.Preset.ToString(), r.Preset == AutopilotPreset.Competitive ? "Gold" : "Accent") : Ui.Tag("Off", "Muted");
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
            row.Children.Add(new Rectangle { Width = 6, Height = 6, Fill = Ui.Brush(active ? "Success" : _runningKeys.Contains(g.Key) ? "Accent" : "Subtle"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(45, 3, 3) });
            row.Children.Add(new TextBlock { Text = g.Def.Name, FontSize = 12.5, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border { Child = row, Padding = new Thickness(10, 7, 8, 7), Cursor = System.Windows.Input.Cursors.Hand, Background = sel ? Ui.Brush("CardHover") : Brushes.Transparent, BorderBrush = sel ? Ui.Brush("AccentGradient") : Brushes.Transparent, BorderThickness = new Thickness(2, 0, 0, 0) };
            var key = g.Key;
            b.MouseLeftButtonUp += (_, _) => { _selected = key; RenderGames(); RenderEditor(); };
            _games.Children.Add(b);
        }
        if (_library.Count == 0) _games.Children.Add(Ui.Muted("Your Game Library is empty. Add a game by its .exe below.", 12));
    }

    private void AddExe()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Game executable|*.exe", Title = "Pick the game's .exe" };
        if (dlg.ShowDialog() != true) return;
        var lib = GameLibrary.Current;
        var (game, message) = GameLibrary.AddExe(lib, dlg.FileName);
        GameLibrary.Save(lib);
        MainWindow.Current?.ShowToast(message, "Accent");
        _library = GameLibrary.Items(lib).OrderBy(g => g.Def.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _selected = game.Key;
        RenderGames(); RenderEditor();
    }

    // ---- editor -------------------------------------------------------------------------------------------------------------
    private void RenderEditor()
    {
        _editor.Children.Clear();
        if (_library.FirstOrDefault(g => g.Key == _selected) is not { } game) { _editor.Children.Add(Ui.Muted("Pick a game.", 12.5)); return; }
        var rule = AutopilotService.RuleFor(game.Key, game.Def.Name);
        var gameProfile = game.Entry?.ProfileId is { } pid ? ProfileStore.All(ProfileService.File).FirstOrDefault(p => p.Id == pid) : null;
        void Changed(bool custom = true)
        {
            if (custom && rule.Preset != AutopilotPreset.Off) rule.Preset = AutopilotPreset.Custom;
            AutopilotService.Save();
            AutopilotService.Refresh();
            RenderEditor(); RenderGames();
        }

        var head = new DockPanel();
        var on = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Run this game's actions automatically" };
        on.Click += (_, _) => { rule.Enabled = on.IsChecked == true; if (rule.Enabled && rule.Preset == AutopilotPreset.Off) rule.Preset = AutopilotPreset.Custom; Changed(false); };
        DockPanel.SetDock(on, Dock.Right);
        head.Children.Add(on);
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = game.Def.Name, Style = Ui.Res<Style>("H2") });
        t.Children.Add(Ui.Muted(string.Join(" · ", new[] { game.Entry?.ExePath ?? string.Join(", ", game.Def.Processes.Select(p => p.Exe)), gameProfile is null ? "no gaming profile linked" : $"profile “{gameProfile.Name}”" }.Where(x => !string.IsNullOrEmpty(x))), 11.5));
        head.Children.Add(t);
        _editor.Children.Add(head);

        var presets = new SegmentedPicker([
            ("Off", nameof(AutopilotPreset.Off), "Autopilot ignores this game"),
            ("Light", nameof(AutopilotPreset.Light), "Readiness check, overlay, warnings and a summary. Changes no settings."),
            ("Competitive", nameof(AutopilotPreset.Competitive), gameProfile is null ? "Readiness, High performance power, 0.5 ms timer, Game Mode, no background recording, High priority" : $"Readiness, the “{gameProfile.Name}” profile, High priority"),
            ("Custom", nameof(AutopilotPreset.Custom), "Your own selection below"),
        ], rule.Preset.ToString());
        presets.Margin = new Thickness(0, 12, 0, 4);
        presets.Changed += v => { var p = Enum.Parse<AutopilotPreset>(v); if (p != AutopilotPreset.Custom) GameAutopilot.Apply(rule, p, gameProfile is not null); else rule.Enabled = true; rule.Preset = p; Changed(false); };
        _editor.Children.Add(presets);

        // Three phases.
        var phases = (ResponsiveGrid)Ui.Grid(3);
        phases.MinItemWidth = 250;
        phases.Margin = new Thickness(0, 12, 0, 0);
        var pre = Phase("01", "BEFORE THE GAME", "When the game's process appears");
        pre.Children.Add(Toggle("Readiness check", "Reads temperatures, free RAM and graphics memory, space on the game's drive, background CPU use and the driver's age. Read only; a quiet note if something needs a look.", rule.Readiness, v => { rule.Readiness = v; Changed(); }));
        pre.Children.Add(Ui.Row(Ui.Btn("Run it now", async (_, _) => await ReadinessNowAsync(game), "GhostButton")));
        (_readinessOut.Parent as Panel)?.Children.Remove(_readinessOut);   // kept across redraws, so detach from the old editor first
        pre.Children.Add(_readinessOut);

        var during = Phase("02", "WHILE PLAYING", "Only what's switched on");
        var src = new ComboBox { Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        src.Items.Add("Settings: none");
        src.Items.Add(gameProfile is null ? "Settings: the game's profile (none linked)" : $"Settings: the “{gameProfile.Name}” profile");
        src.Items.Add("Settings: my own selection");
        src.SelectedIndex = (int)rule.Optimizations;
        src.SelectionChanged += (_, _) => { rule.Optimizations = (AutopilotOptimizations)src.SelectedIndex; Changed(); };
        during.Children.Add(src);
        during.Children.Add(Ui.Muted("Applied through the Gaming Profiles engine: each value is recorded before it changes, rolled back if a step fails, and restored and read back when the game ends. An already-active profile is never replaced.", 11));
        if (rule.Optimizations == AutopilotOptimizations.Custom)
        {
            var power = new ComboBox { Margin = new Thickness(0, 8, 0, 0) };
            power.Items.Add("Power plan: keep mine"); power.Items.Add("Power plan: High performance"); power.Items.Add("Power plan: Balanced");
            power.SelectedIndex = (int)rule.Power;
            power.SelectionChanged += (_, _) => { rule.Power = (PowerChoice)power.SelectedIndex; Changed(); };
            during.Children.Add(power);
            var timer = new ComboBox { Margin = new Thickness(0, 6, 0, 0) };
            timer.Items.Add("Timer resolution: Windows default"); timer.Items.Add("Timer resolution: 0.5 ms"); timer.Items.Add("Timer resolution: 1.0 ms");
            timer.SelectedIndex = rule.TimerMs switch { 0.5 => 1, 1.0 => 2, _ => 0 };
            timer.SelectionChanged += (_, _) => { rule.TimerMs = timer.SelectedIndex switch { 1 => 0.5, 2 => 1.0, _ => null }; Changed(); };
            during.Children.Add(timer);
            var tw = new Expander { Header = new TextBlock { Text = $"Tweaks for the session ({rule.Tweaks.Count})", FontSize = 12, Foreground = Ui.Brush("Muted") }, Margin = new Thickness(0, 6, 0, 0), IsExpanded = _tweaksOpen };
            tw.Expanded += (_, _) => _tweaksOpen = true; tw.Collapsed += (_, _) => _tweaksOpen = false;
            var list = new StackPanel();
            foreach (var id in GameAutopilot.SessionTweaks)
            {
                if (TweakCatalog.Find(id) is not { } tk) continue;
                var cb = new CheckBox { Content = tk.Name + (tk.RequiresAdmin ? "  (admin)" : ""), IsChecked = rule.Tweaks.Contains(id), Margin = new Thickness(0, 3, 0, 0), ToolTip = tk.Description, Foreground = Ui.Brush("Muted") };
                cb.Click += (_, _) => { if (cb.IsChecked == true) { if (!rule.Tweaks.Contains(id)) rule.Tweaks.Add(id); } else rule.Tweaks.Remove(id); Changed(); };
                list.Children.Add(cb);
            }
            tw.Content = list;
            during.Children.Add(tw);
        }
        during.Children.Add(Toggle("Raise the game's priority", "Sets only this game's process to High priority if it's at Normal. Some anti-cheat games don't allow it; then it's skipped. Never touches other processes.", rule.RaisePriority, v => { rule.RaisePriority = v; Changed(); }));
        during.Children.Add(Toggle("Show the overlay", "Shows the performance overlay with the layout you saved for this game, and hides it afterwards if Autopilot showed it.", rule.ShowOverlay, v => { rule.ShowOverlay = v; Changed(); }));
        during.Children.Add(Toggle("Live protection", $"Records Hardware Monitor's warnings while you play (CPU from {App.Settings.Monitor.CpuTempWarnC:0} °C, GPU from {App.Settings.Monitor.GpuTempWarnC:0} °C, throttling, memory over {App.Settings.Monitor.RamWarnPercent:0}%) with a quiet note. Nothing is closed or stopped.", rule.LiveWarnings, v => { rule.LiveWarnings = v; Changed(); }));

        var after = Phase("03", "AFTER THE GAME", "When it closes, crashes or you end it");
        after.Children.Add(Locked("Restore everything", "Puts back each value the session changed from its record, reads it back, hides the overlay it showed and returns the priority. Always on. If Money Tweaks itself closes unexpectedly, this runs at the next start (or the session continues if the game still runs)."));
        after.Children.Add(Toggle("Session summary", "A short note with how long you played, warnings, peak temperatures and what was restored. Kept in the history below.", rule.Summary, v => { rule.Summary = v; Changed(); }));
        phases.Children.Add(Wrap(pre, 0)); phases.Children.Add(Wrap(during, 1)); phases.Children.Add(Wrap(after, 2));
        _editor.Children.Add(phases);

        // The plan in words.
        var (a, b, c) = GameAutopilot.Describe(rule, gameProfile);
        var plan = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        plan.Children.Add(new TextBlock { Text = rule.Enabled ? "WHAT HAPPENS FOR " + game.Def.Name.ToUpperInvariant() : "THIS GAME IS OFF: NOTHING HAPPENS", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 4) });
        if (rule.Enabled)
            foreach (var (label, lines) in new[] { ("Before", a), ("While playing", b), ("After", c) })
                foreach (var l in lines) plan.Children.Add(new TextBlock { Text = $"{label}:  {l}", FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
        if (rule.Enabled && !AutopilotService.File.Enabled) plan.Children.Add(new TextBlock { Text = "Autopilot itself is off (top right), so this won't run yet.", FontSize = 11.5, Foreground = Ui.Brush("Gold"), Margin = new Thickness(0, 4, 0, 0) });
        _editor.Children.Add(plan);
    }

    private bool _tweaksOpen;
    private readonly StackPanel _readinessOut = new() { Margin = new Thickness(0, 6, 0, 0) };

    private async Task ReadinessNowAsync(LibraryGame game)
    {
        _readinessOut.Children.Clear();
        _readinessOut.Children.Add(Ui.Muted("Reading…", 11.5));
        Core.Monitoring.SensorHub.Shared.Acquire(_readinessOut, record: false);
        try
        {
            var exe = game.Entry?.ExePath ?? GameRuntime.InstallFor(game).Install?.ExePath;
            var items = await Task.Run(() => { Thread.Sleep(1500); return GameAutopilot.Readiness(GameAutopilot.ReadInputs(exe, Core.Monitoring.SensorHub.Shared.Last)); });
            _readinessOut.Children.Clear();
            foreach (var i in items)
                _readinessOut.Children.Add(new TextBlock { Text = $"{(i.Ok ? "✓" : "!")}  {i.Area}: {i.Text}", FontSize = 11.5, Foreground = Ui.Brush(i.Ok ? "Muted" : "Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
        }
        catch (Exception ex) { _readinessOut.Children.Clear(); _readinessOut.Children.Add(Ui.Muted("Couldn't read: " + ex.Message, 11.5)); }
        finally { Core.Monitoring.SensorHub.Shared.Release(_readinessOut); }
    }

    private static StackPanel Phase(string n, string title, string sub)
    {
        var sp = new StackPanel();
        var h = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        h.Children.Add(new TextBlock { Text = n, FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 18, Foreground = Ui.Brush("AccentGradient"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        h.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("Eyebrow"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(h);
        sp.Children.Add(Ui.Muted(sub, 11));
        return sp;
    }

    private static UIElement Wrap(StackPanel phase, int index)
    {
        var b = new Border { Child = phase, Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1, 2, 1, 1), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 0, 10, 10) };
        if (!App.ReduceMotion)
        {
            b.Opacity = 0;
            var tt = new TranslateTransform(0, 8);
            b.RenderTransform = tt;
            var d = TimeSpan.FromMilliseconds(70 * index);
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = d });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = d, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        return b;
    }

    private static UIElement Toggle(string title, string text, bool value, Action<bool> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var sw = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = value, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 2, 0, 0) };
        sw.Click += (_, _) => set(sw.IsChecked == true);
        DockPanel.SetDock(sw, Dock.Right);
        row.Children.Add(sw);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(sp);
        return row;
    }

    private static UIElement Locked(string title, string text)
    {
        var row = (DockPanel)Toggle(title, text, true, _ => { });
        var sw = row.Children.OfType<ToggleButton>().First();
        sw.IsEnabled = false;
        sw.ToolTip = "Always on: Autopilot never leaves a temporary change behind.";
        return row;
    }

    // ---- history ------------------------------------------------------------------------------------------------------------
    private void RenderHistory()
    {
        _history.Children.Clear();
        var h = AutopilotService.File.History;
        if (h.Count == 0) { _history.Children.Add(Ui.Muted("No sessions yet.", 12)); return; }
        foreach (var s in Enumerable.Reverse(h).Take(15))
        {
            var ex = new Expander { Margin = new Thickness(0, 2, 0, 2) };
            var head = new DockPanel();
            var tone = s.RestoreProblems.Count > 0 ? "Danger" : s.EndedBy?.Contains("crash", StringComparison.OrdinalIgnoreCase) == true ? "Gold" : "Success";
            head.Children.Add(new Rectangle { Width = 7, Height = 7, Fill = Ui.Brush(tone), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(45, 3.5, 3.5) });
            head.Children.Add(new TextBlock { Text = $"{s.StartedAt:g}  ·  {s.GameName}  ·  {(s.Duration is { } d ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : "–")}  ·  {s.EndedBy}  ·  {s.Actions.Count} action(s), {s.Warnings.Count} warning(s){(s.RestoreProblems.Count > 0 ? ", restore needs attention" : "")}", FontSize = 12, Foreground = Ui.Brush("Text") });
            ex.Header = head;
            var body = new StackPanel { Margin = new Thickness(18, 4, 0, 6) };
            void L(string title, IEnumerable<string> lines) { var l = lines.ToList(); if (l.Count == 0) return; body.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 4, 0, 1) }); foreach (var x in l) body.Children.Add(new TextBlock { Text = "• " + x, FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap }); }
            L("DONE", s.Actions); L("LEFT AS IS", s.Skipped); L("READINESS", s.Readiness); L("WARNINGS", s.Warnings); L("RESTORED", s.Restored); L("NEEDS ATTENTION", s.RestoreProblems);
            if (s.PeakCpuTempC is not null || s.PeakGpuTempC is not null) L("PEAK TEMPERATURES", [$"CPU {(s.PeakCpuTempC is { } c ? $"{c:0} °C" : "n/a")}, GPU {(s.PeakGpuTempC is { } g ? $"{g:0} °C" : "n/a")}"]);
            ex.Content = body;
            _history.Children.Add(ex);
        }
    }
}
