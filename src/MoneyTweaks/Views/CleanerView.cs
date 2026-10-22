using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Cleaner;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Ultimate PC Cleaner: Performance Cleanup (background apps, with Quick / Custom / Clean &amp; Play) and Storage Cleanup
/// (scan → review → clean selected, with quarantine) on one page, plus history. Real Windows data and real operations only.
/// </summary>
public sealed partial class CleanerView : UserControl, IPageLifecycle
{
    private readonly CleanerSettings _s = CleanerSettings.Load();
    private readonly StackPanel _dash = new(), _perf = new(), _storage = new(), _history = new();
    private readonly TextBlock _cpuNow = Ui.Text("–", 22, FontWeights.Bold), _ramNow = Ui.Text("–", 22, FontWeights.Bold), _reclaim = Ui.Text("Not scanned", 22, FontWeights.Bold), _quarantine = Ui.Text("–", 22, FontWeights.Bold);
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(2) };
    private HardwareMonitor? _hm;
    private bool _shown;

    public CleanerView()
    {
        var (root, body) = Ui.Page("PC Cleaner",
            "Close background apps you don't need while playing, and remove files that are verifiably junk. Everything is shown before it happens, protected apps and files are never touched, and results are measured, not estimated.",
            icon: "\uEA99");
        body.Children.Add(_dash);
        body.Children.Add(Ui.Card(_perf));
        body.Children.Add(Ui.Card(_storage));
        body.Children.Add(Ui.Card(_history));
        Content = root;
        BuildDash();
        _live.Tick += (_, _) => UpdateLive();
    }

    public async void OnShown()
    {
        _hm ??= new HardwareMonitor();
        _live.Start();
        if (_shown) return;
        _shown = true;
        var expired = await Task.Run(StorageCleaner.PurgeExpired);
        if (expired > 0) { _s.AddHistory($"Quarantine older than {StorageCleaner.QuarantineDays} days emptied: {StorageCleaner.Size(expired)} freed"); _s.Save(); }
        RenderPerf();
        RenderStorage();
        RenderHistory();
        await ScanAppsAsync();
    }

    public void OnHidden() => _live.Stop();

    // ---- dashboard ---------------------------------------------------------------------------------------------------------

    private void BuildDash()
    {
        var grid = Ui.Grid(4);
        UIElement Tile(string label, TextBlock value, string sub)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow") });
            sp.Children.Add(value);
            sp.Children.Add(Ui.Muted(sub, 11.5));
            return Ui.Card(sp, new Thickness(0, 0, 10, 10));
        }
        grid.Children.Add(Tile("CPU now", _cpuNow, "all apps, live"));
        grid.Children.Add(Tile("RAM in use", _ramNow, "live, from Windows"));
        grid.Children.Add(Tile("Reclaimable", _reclaim, "selected storage categories"));
        grid.Children.Add(Tile("In quarantine", _quarantine, $"restorable for {StorageCleaner.QuarantineDays} days"));
        _dash.Children.Add(grid);
        _dash.Children.Add(Ui.Muted("Freeing storage doesn't make games faster by itself. Closing background apps frees CPU and RAM, which only helps when your PC was short of them; Money Tweaks reports what actually changed and never claims an FPS gain (measure that in Test & Benchmark).", 12));
        _dash.Children.Add(new Border { Height = 14 });
        UpdateQuarantineTile();
    }

    private void UpdateLive()
    {
        try
        {
            var s = _hm!.Sample();
            _cpuNow.Text = $"{s.CpuPercent:0}%";
            _ramNow.Text = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB";
        }
        catch { }
    }

    private void UpdateQuarantineTile() => _quarantine.Text = StorageCleaner.Size(StorageCleaner.QuarantineSessions().Sum(s => s.Bytes));

    // ---- performance cleanup -----------------------------------------------------------------------------------------------

    private string _mode = "quick";
    private List<AppProcess> _apps = [];
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private bool _showProtected, _busy;
    private LibraryGame? _game;
    private GameInstall? _gameInstall;
    private List<(LibraryGame g, GameInstall? i)> _games = [];
    private readonly StackPanel _appList = new(), _perfResult = new();
    private readonly TextBlock _perfStatus = Ui.Muted("", 12);

    private void RenderPerf()
    {
        _perf.Children.Clear();
        _perf.Children.Add(Ui.Section("Performance Cleanup", "Background apps, measured right now and sorted by what's safe to close. Protected apps (Windows, security, anti-cheat, drivers, audio, controllers, networking, streaming, installers) can't be selected."));
        var modes = new SegmentedPicker([("Quick Clean", "quick", "Closes only apps marked Safe to Close."), ("Custom Clean", "custom", "You pick from Safe and Review First."),
            ("Clean & Play", "play", "Per-game cleanup, then launch the game.")], _mode);
        modes.Changed += m => { _mode = m; ApplyModeDefaults(); RenderPerf(); };
        _perf.Children.Add(modes);
        _perf.Children.Add(Ui.Muted(_mode switch
        {
            "quick" => "Quick Clean closes the Safe to Close apps below: background sync, updaters and optional extras whose work simply resumes later.",
            "custom" => "Custom Clean: tick exactly what to close. Review First apps can hold unsaved work or be in use (voice chat, music, launchers): check before closing.",
            _ => "Clean & Play: choose a game, tick what to close for it and save it as that game's cleanup. Launch from here (or the Game Library) cleans first; turn on \"every launch\" to make it automatic.",
        }, 12));

        if (_mode == "play") RenderGamePicker();

        var opts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var force = new CheckBox { Content = "Force-close apps that ignore a close request (after 5 seconds)", IsChecked = _s.ForceIfIgnored, Margin = new Thickness(0, 0, 16, 0),
            ToolTip = "Apps are always asked to close first, like clicking X. Without this, an app that ignores the request is left running." };
        force.Click += (_, _) => { _s.ForceIfIgnored = force.IsChecked == true; _s.Save(); };
        opts.Children.Add(force);
        var showProt = new CheckBox { Content = "Show protected apps", IsChecked = _showProtected };
        showProt.Click += (_, _) => { _showProtected = showProt.IsChecked == true; RenderAppList(); };
        opts.Children.Add(showProt);
        _perf.Children.Add(opts);

        var buttons = new List<UIElement>
        {
            Ui.Btn("Rescan apps", async (_, _) => await ScanAppsAsync(), "BaseButton", new Thickness(0, 10, 8, 0)),
        };
        if (_mode == "play")
        {
            buttons.Add(Ui.Btn("Save as this game's cleanup", (_, _) => SaveGameRule(), "BaseButton", new Thickness(0, 10, 8, 0)));
            buttons.Add(Ui.Btn("Clean & launch", async (_, _) => await CleanAsync(launchAfter: true), "PrimaryButton", new Thickness(0, 10, 8, 0)));
        }
        else buttons.Add(Ui.Btn(_mode == "quick" ? "Quick Clean" : "Clean selected", async (_, _) => await CleanAsync(launchAfter: false), "PrimaryButton", new Thickness(0, 10, 8, 0)));
        _perf.Children.Add(Ui.Row(buttons.ToArray()));
        _perf.Children.Add(_perfStatus);
        _perf.Children.Add(StandbyPanel());
        _perf.Children.Add(_appList);
        _perf.Children.Add(_perfResult);
        RenderExclusions(_perf, "Never close (your exclusions)", _s.ProcessExclusions, "process name, e.g. Spotify", async () => await ScanAppsAsync());
        RenderAppList();
    }

    private void RenderGamePicker()
    {
        if (_games.Count == 0)
        {
            _perf.Children.Add(Ui.Muted("Loading your games…", 12));
            _ = Task.Run(() =>
            {
                var items = GameLibrary.Items(GameLibrary.Current);
                return GameRuntime.Statuses(items).Where(x => x.install.Install is not null).Select(x => (x.game, x.install.Install)).ToList();
            }).ContinueWith(t => Dispatcher.Invoke(() => { _games = t.Result!; if (_games.Count > 0) { _game ??= _games[0].g; _gameInstall = _games.First(x => x.g.Key == _game.Key).i; } ApplyModeDefaults(); RenderPerf(); }));
            return;
        }
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var cb = new ComboBox { Width = 260 };
        foreach (var (g, _) in _games) cb.Items.Add(g.Def.Name);
        cb.SelectedIndex = Math.Max(0, _games.FindIndex(x => x.g.Key == _game?.Key));
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex < 0) return; (_game, _gameInstall) = _games[cb.SelectedIndex]; ApplyModeDefaults(); RenderPerf(); };
        row.Children.Add(cb);
        var rule = _game is null ? null : _s.RuleFor(_game.Key);
        var auto = new CheckBox { Content = "Clean like this every time I launch it from Money Tweaks", IsChecked = rule?.CleanOnLaunch == true, Margin = new Thickness(12, 4, 0, 0), IsEnabled = rule is not null,
            ToolTip = rule is null ? "Save this game's cleanup first." : "Uses only the apps you saved for this game. Protected apps are re-checked and never closed." };
        auto.Click += (_, _) => { if (rule is null) return; rule.CleanOnLaunch = auto.IsChecked == true; _s.Save(); };
        row.Children.Add(auto);
        _perf.Children.Add(row);
        _perf.Children.Add(Ui.Muted(_games.Count == 0 ? "No installed games found in your Game Library."
            : rule is null ? "No cleanup saved for this game yet: Safe to Close apps are pre-selected." : $"Saved cleanup: {string.Join(", ", rule.Close)}.", 11.5));
    }

    private void ApplyModeDefaults()
    {
        _checked.Clear();
        if (_mode == "play" && _game is not null && _s.RuleFor(_game.Key) is { } rule) { foreach (var n in rule.Close) _checked.Add(n); return; }
        foreach (var a in _apps.Where(a => a.Class == ProcessClass.SafeToClose)) _checked.Add(a.Name);
    }

    private string? GameExe => _mode == "play" ? _game?.Def.ExeNames.FirstOrDefault() : null;

    private async Task ScanAppsAsync()
    {
        if (_busy) return;
        _perfStatus.Text = "Measuring running apps (CPU over 1.5 s, memory)…";
        try
        {
            _apps = await ProcessCleaner.ListAsync(_s.ProcessExclusions, GameExe);
            ApplyModeDefaults();
            _perfStatus.Text = $"{_apps.Count(a => a.Class == ProcessClass.SafeToClose)} safe to close · {_apps.Count(a => a.Class == ProcessClass.ReviewFirst)} to review · {_apps.Count(a => a.Class == ProcessClass.Protected)} protected · measured {DateTime.Now:T}";
        }
        catch (Exception ex) { _perfStatus.Text = "Couldn't list apps: " + ex.Message; Log.Error("cleaner list", ex); }
        RenderAppList();
    }

    private void RenderAppList()
    {
        _appList.Children.Clear();
        foreach (var cls in new[] { ProcessClass.SafeToClose, ProcessClass.ReviewFirst, ProcessClass.Protected })
        {
            var group = _apps.Where(a => a.Class == cls).ToList();
            if (group.Count == 0) continue;
            var (label, tone) = cls switch { ProcessClass.SafeToClose => ("Safe to close", "Success"), ProcessClass.ReviewFirst => ("Review first", "Gold"), _ => ("Protected", "Muted") };
            var head = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
            head.Children.Add(Ui.Tag(label.ToUpperInvariant(), tone));
            head.Children.Add(Ui.Muted($"{group.Count} app(s) · {group.Sum(a => a.RamMb):0} MB · {group.Sum(a => a.CpuPercent):0.#}% CPU", 11.5));
            _appList.Children.Add(head);
            if (cls == ProcessClass.Protected && !_showProtected) { _appList.Children.Add(Ui.Muted("Hidden. Tick \"Show protected apps\" to see them and why they're protected.", 11.5)); continue; }
            foreach (var a in group) _appList.Children.Add(AppRow(a));
        }
        if (_apps.Count == 0) _appList.Children.Add(Ui.Muted("No apps measured yet.", 12));
    }

    private UIElement AppRow(AppProcess a)
    {
        bool selectable = a.Class != ProcessClass.Protected && (_mode != "quick" || a.Class == ProcessClass.SafeToClose);
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cb = new CheckBox { IsChecked = _checked.Contains(a.Name), IsEnabled = selectable, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0),
            ToolTip = selectable ? null : a.Class == ProcessClass.Protected ? "Protected: never closed" : "Quick Clean only closes Safe to Close apps; use Custom Clean for this one." };
        cb.Click += (_, _) => { if (cb.IsChecked == true) _checked.Add(a.Name); else _checked.Remove(a.Name); };
        grid.Children.Add(cb);
        var info = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = a.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(a.Class == ProcessClass.Protected ? "Subtle" : "Text") });
        var sub = string.Join(" · ", new[] { a.Title, a.Company, a.Pids.Count > 1 ? $"{a.Pids.Count} processes" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (sub.Length > 0) title.Children.Add(new TextBlock { Text = "  " + sub, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 });
        info.Children.Add(title);
        info.Children.Add(Ui.Muted(a.Reason + (a.Benefit.Length > 0 ? " " + a.Benefit : ""), 11.5));
        Grid.SetColumn(info, 1); grid.Children.Add(info);
        var usage = new TextBlock { Text = $"{a.CpuPercent:0.#}% CPU · {a.RamMb:0} MB", FontSize = 12, Foreground = Ui.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 8, 0) };
        Grid.SetColumn(usage, 2); grid.Children.Add(usage);
        if (a.Class != ProcessClass.Protected)
        {
            var ex = Ui.Btn("Never close", async (_, _) => { if (!_s.ProcessExclusions.Contains(a.Name, StringComparer.OrdinalIgnoreCase)) _s.ProcessExclusions.Add(a.Name); _s.Save(); await ScanAppsAsync(); RenderPerf(); }, "GhostButton", new Thickness(0));
            ex.Padding = new Thickness(8, 2, 8, 2); ex.FontSize = 11;
            Grid.SetColumn(ex, 3); grid.Children.Add(ex);
        }
        return new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(0), Background = Ui.Brush(_checked.Contains(a.Name) && selectable ? "AccentSoft" : "Surface"), Margin = new Thickness(0, 0, 0, 3), Child = grid };
    }

    private void SaveGameRule()
    {
        if (_game is null) return;
        var names = _checked.Where(n => _apps.Any(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && a.Class != ProcessClass.Protected)).ToList();
        var rule = _s.RuleFor(_game.Key) ?? new GameCleanupRule { GameKey = _game.Key, GameName = _game.Def.Name };
        if (!_s.GameRules.Contains(rule)) _s.GameRules.Add(rule);
        rule.Close = names; rule.ForceIfIgnored = _s.ForceIfIgnored;
        _s.Save();
        MainWindow.Current?.ShowToast($"Saved: before {_game.Def.Name}, close {(names.Count == 0 ? "nothing" : string.Join(", ", names))}.", "Success");
        RenderPerf();
    }

    private async Task CleanAsync(bool launchAfter)
    {
        if (_busy) return;
        var targets = _apps.Where(a => _checked.Contains(a.Name) && a.Class != ProcessClass.Protected && (_mode != "quick" || a.Class == ProcessClass.SafeToClose)).ToList();
        if (targets.Count == 0 && !launchAfter) { Dialog.Show("Nothing selected", "Tick the apps to close first."); return; }
        if (targets.Count > 0)
        {
            var review = targets.Where(t => t.Class == ProcessClass.ReviewFirst).ToList();
            if (!Dialog.Confirm($"Close {targets.Count} app(s)?",
                    string.Join("\n", targets.Select(t => $"• {t.Name} ({t.RamMb:0} MB){(t.Class == ProcessClass.ReviewFirst ? " – review: " + t.Reason : "")}")) +
                    "\n\nEach app is asked to close first, like clicking X, so it can save." + (_s.ForceIfIgnored ? " Apps that ignore it for 5 seconds are force-closed (unsaved work in them is lost)." : " Apps that ignore it are left running.") +
                    (review.Count > 0 ? "\n\nSave your work in Review First apps before continuing." : "") + "\n\nOnly this one process per app is closed, never what it started.", "Close apps", review.Count > 0 ? DialogKind.Warning : DialogKind.Confirm)) return;
        }
        _busy = true;
        _perfResult.Children.Clear();
        try
        {
            BackgroundCleanReport? report = null;
            if (targets.Count > 0)
            {
                var progress = new Progress<string>(t => _perfStatus.Text = t);
                report = await ProcessCleaner.CleanAsync(targets.Select(t => t.Name), _s.ForceIfIgnored, _mode == "quick" ? "Quick Clean" : _mode == "custom" ? "Custom Clean" : "Clean & Play", GameExe, _s.ProcessExclusions, progress);
                _s.AddHistory(ProcessCleaner.Summarize(report));
                _s.Save();
                ShowPerfReport(report);
            }
            if (_s.ClearStandby && Admin.IsElevated)
            {
                var sb = await Task.Run(StandbyMemory.Clear);
                _s.AddHistory(StandbyMemory.Describe(sb)); _s.Save();
                _perfResult.Children.Add(Ui.Muted(StandbyMemory.Describe(sb), 12));
                RefreshStandby();
            }
            if (launchAfter && _game is not null) GameLibraryView.Launch(_game, _gameInstall);
        }
        catch (OperationCanceledException) { _perfStatus.Text = "Stopped."; }
        catch (Exception ex) { _perfStatus.Text = "Cleanup failed: " + ex.Message; Log.Error("cleaner clean", ex); }
        finally { _busy = false; }
        await ScanAppsAsync();
        RenderHistory();
    }

    private void ShowPerfReport(BackgroundCleanReport r)
    {
        _perfResult.Children.Clear();
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text("Result", 14, FontWeights.SemiBold));
        var grid = Ui.Grid(3);
        UIElement Cell(string label, string before, string after, string delta)
        {
            var c = new StackPanel();
            c.Children.Add(Ui.Muted(label, 11.5));
            c.Children.Add(Ui.Text($"{before} → {after}", 15, FontWeights.SemiBold));
            c.Children.Add(Ui.Muted(delta, 11.5));
            return Ui.Card(c, new Thickness(0, 6, 10, 6));
        }
        grid.Children.Add(Cell("CPU (whole PC, 1 s sample)", $"{r.Before.CpuPercent:0}%", $"{r.After.CpuPercent:0}%", "CPU readings swing a lot from second to second; treat small changes as noise."));
        grid.Children.Add(Cell("RAM in use (Windows)", $"{r.Before.RamUsedGb:0.00} GB", $"{r.After.RamUsedGb:0.00} GB", $"{(r.Before.RamUsedGb - r.After.RamUsedGb) * 1024:+0;-0} MB measured change"));
        grid.Children.Add(Cell("Memory the closed apps used", $"{r.RamFreedByClosedAppsMb:0} MB", "released", "Their working set right before closing."));
        sp.Children.Add(grid);
        foreach (var x in r.Results)
        {
            var tone = x.Outcome switch { CloseOutcome.ClosedGracefully or CloseOutcome.ForceClosed => "Success", CloseOutcome.NotRunning => "Muted", CloseOutcome.Refused => "Gold", _ => "Danger" };
            var label = x.Outcome switch { CloseOutcome.ClosedGracefully => "CLOSED", CloseOutcome.ForceClosed => "FORCE-CLOSED", CloseOutcome.StillRunning => "STILL RUNNING", CloseOutcome.Refused => "REFUSED", CloseOutcome.NotRunning => "NOT RUNNING", _ => "FAILED" };
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(Ui.Tag(label, tone));
            row.Children.Add(Ui.Text($"{x.Name}: {x.Detail}", 12));
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.Muted("No FPS change is claimed. If you want to know whether it helped your games, compare in Test & Benchmark.", 11.5));
        _perfResult.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), Margin = new Thickness(0, 10, 0, 0), Child = sp });
        _perfStatus.Text = ProcessCleaner.Summarize(r);
    }

    // ---- standby memory ----------------------------------------------------------------------------------------------------

    private readonly TextBlock _standbyText = Ui.Muted("", 12);

    /// <summary>Standby (cached) memory, measured from Windows, with a Clear button and an "every cleanup" option.</summary>
    private UIElement StandbyPanel()
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Text("Standby memory (cached RAM)", 13, FontWeights.SemiBold));
        if (!Admin.IsElevated) { head.Children.Add(new Border { Width = 8 }); head.Children.Add(Ui.Tag("NEEDS ADMIN", "Gold")); }
        sp.Children.Add(head);
        sp.Children.Add(_standbyText);
        sp.Children.Add(Ui.Muted("Standby memory is Windows' cache of recently used files. It already counts as available: any game that needs RAM gets it instantly. " +
            "Clearing it turns cache into free memory, which can help if a game hitches while Windows repurposes cache; the cost is that recently used files load from disk again. It won't raise FPS on its own.", 11.5));
        var clear = Ui.Btn("Clear standby memory", async (s, _) =>
        {
            if (!Admin.IsElevated)
            {
                if (Dialog.Confirm("Administrator rights needed", "Windows only lets administrators clear the standby list. Restart Money Tweaks as administrator? Nothing has been changed.", "Restart as admin", DialogKind.Warning) && Admin.RestartElevated())
                    Application.Current.Shutdown();
                return;
            }
            ((Button)s).IsEnabled = false;
            var r = await Task.Run(StandbyMemory.Clear);
            ((Button)s).IsEnabled = true;
            _s.AddHistory(StandbyMemory.Describe(r)); _s.Save();
            _standbyText.Text = StandbyMemory.Describe(r);
            RenderHistory();
        }, "BaseButton", new Thickness(0, 8, 12, 0));
        var every = new CheckBox { Content = "Also clear it after every cleanup", IsChecked = _s.ClearStandby, IsEnabled = Admin.IsElevated, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        every.Click += (_, _) => { _s.ClearStandby = every.IsChecked == true; _s.Save(); };
        sp.Children.Add(Ui.Row(clear, every));
        RefreshStandby();
        return new Border { Style = Ui.Res<Style>("CardBorder"), Margin = new Thickness(0, 10, 0, 4), Child = sp };
    }

    private async void RefreshStandby()
    {
        var s = await Task.Run(StandbyMemory.Snapshot);
        _standbyText.Text = s is null ? "Windows didn't report the memory lists on this PC."
            : $"Now: {StorageCleaner.Size(s.StandbyBytes)} standby (cache) · {StorageCleaner.Size(s.FreeBytes)} free · {StorageCleaner.Size(s.AvailableBytes)} available of {StorageCleaner.Size(s.TotalBytes)} (read from Windows).";
    }

    // ---- shared: exclusions editor, history ---------------------------------------------------------------------------------

    private void RenderExclusions(StackPanel host, string title, List<string> list, string hint, Func<Task> changed)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(Ui.Text(title, 12.5, FontWeights.SemiBold));
        var chips = new WrapPanel();
        foreach (var e in list.ToList())
        {
            var chip = new Border { CornerRadius = new CornerRadius(0), Padding = new Thickness(8, 2, 4, 2), Margin = new Thickness(0, 4, 6, 0), Background = Ui.Brush("Surface") };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = e, FontSize = 11.5, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center });
            var x = Ui.Btn("×", async (_, _) => { list.Remove(e); _s.Save(); await changed(); }, "GhostButton", new Thickness(4, 0, 0, 0));
            x.Padding = new Thickness(5, 0, 5, 0);
            row.Children.Add(x);
            chip.Child = row;
            chips.Children.Add(chip);
        }
        if (list.Count == 0) chips.Children.Add(Ui.Muted("None.", 11.5));
        sp.Children.Add(chips);
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var tb = new TextBox { Width = 320, Tag = hint };
        add.Children.Add(tb);
        add.Children.Add(Ui.Btn("Add", async (_, _) =>
        {
            var v = tb.Text.Trim().Trim('"');
            if (v.Length == 0 || list.Contains(v, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v[..^4] : v);
            _s.Save();
            await changed();
        }, "GhostButton", new Thickness(8, 0, 0, 0)));
        sp.Children.Add(add);
        host.Children.Add(sp);
    }

    private void RenderHistory()
    {
        _history.Children.Clear();
        _history.Children.Add(Ui.Section("Cleanup history", "Every cleanup with its measured result."));
        if (_s.History.Count == 0) { _history.Children.Add(Ui.Muted("Nothing cleaned yet.", 12)); return; }
        foreach (var h in _s.History.Take(25)) _history.Children.Add(Ui.Muted(h, 12));
    }
}
