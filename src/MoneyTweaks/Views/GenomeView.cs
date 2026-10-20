using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Genome;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

/// <summary>
/// Performance Genome: learns how this PC responds to combinations of supported settings from its own measurements.
/// Goals and hard limits → adaptive experiments (baselines, repeats, interaction checks, failures learned) → measured
/// recommendations, a performance frontier and predictions that are always labelled as such.
/// </summary>
public sealed partial class GenomeView : UserControl, IPageLifecycle
{
    private readonly GenomeStore _store = GenomeStore.Load();
    private readonly GenomeSystem _backend = new();
    private readonly StackPanel _hero = new(), _goal = new(), _experiment = new(), _results = new(), _history = new(), _banner = new(), _quick = new(), _answer = new(), _details = new();
    private readonly Expander _advanced = new() { Margin = new Thickness(0, 0, 0, 14) };
    private Fingerprint? _fp;
    private Dictionary<string, string> _now = [];
    private GenomeAnalysis? _a;
    private List<string> _discoveryNotes = [];
    private CancellationTokenSource? _cts;
    private bool _loaded;
    private readonly Button _refresh;

    public GenomeView()
    {
        _refresh = Ui.Btn("Re-read settings", async (_, _) => await LoadAsync(), "BaseButton", new Thickness(0));
        var (root, body) = Ui.Page("Performance Genome",
            "Finds the best settings for your PC by actually testing them, instead of guessing. Pick what you want, press Start, and come back in about 10 minutes.", _refresh, icon: "\uE9D2");
        body.Children.Add(_banner);
        body.Children.Add(Ui.Card(_quick));
        body.Children.Add(_answer);
        // Everything else is optional: the advanced controls are collapsed, the details appear once there are results.
        _advanced.Header = Ui.Text("More options: goal weights, limits and what gets tested", 13, FontWeights.SemiBold);
        _advanced.Foreground = Ui.Brush("Text");
        _advanced.Content = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { Ui.Card(_goal), Ui.Card(_experiment) } };
        body.Children.Add(_advanced);
        _details.Children.Add(new TextBlock { Text = "DETAILS", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 4, 0, 6) });
        _details.Children.Add(Ui.Card(_hero));
        _details.Children.Add(_results);
        _details.Children.Add(Ui.Card(_history));
        body.Children.Add(_details);
        Content = root;
        _quick.Children.Add(Ui.Loading("Reading this PC's settings…"));
    }

    public async void OnShown() { if (!_loaded) await LoadAsync(); }
    public void OnHidden() { }

    private async Task LoadAsync()
    {
        if (_cts is not null) return;
        _refresh.IsEnabled = false;
        try
        {
            if (_store.Workloads.Count == 0) _store.Workloads.Add(new GenomeWorkload { Id = "synthetic", Kind = "synthetic", Name = "Built-in 3D scene" });
            if (_store.Goal.Weights.Count == 0) _store.Goal = GenomeGoals.FromPreset(_store.Goal.Preset);
            var w = _store.ActiveWorkload!;
            if (TuningDetector.Cached() is null) await TuningDetector.ScanAsync(); // CPU, GPU, BIOS for the fingerprint
            (_discoveryNotes, _now, _fp) = await Task.Run(() =>
            {
                var notes = GenomeSystem.Discover(_store);
                var now = _backend.ReadCurrent(_store.Factors.Where(f => !f.IsManual).ToList());
                return (notes, now, _backend.Fingerprint(w));
            });
            // Updates since the last visit: results from before are kept but no longer count as measured for this setup.
            if (_store.LastSeen is { } last && _store.Trials.Count > 0 && _fp.ChangesSince(last) is { Count: > 0 } changes)
                _store.Note("Detected " + string.Join(", ", changes) + ": earlier results are now marked stale; profiles need re-checking.");
            _store.LastSeen = _fp;
            _store.Save();
            _loaded = true;
            Analyze();
            RenderAll();
        }
        catch (Exception ex) { Log.Error("genome load", ex); _quick.Children.Clear(); _quick.Children.Add(Ui.Muted("Couldn't read this PC's settings: " + ex.Message)); }
        finally { _refresh.IsEnabled = true; }
    }

    private Dictionary<string, string> Reference()
    {
        var r = new Dictionary<string, string>(_now);
        foreach (var f in _store.Factors.Where(f => f.IsManual)) r[f.Id] = f.Levels[0].Id;
        return GenomeAnalysis.Complete(r, _store.Factors);
    }

    private void Analyze()
    {
        if (_store.ActiveWorkload is { } w) _a = GenomeAnalysis.Build(_store, w, _fp, Reference());
    }

    private void RenderAll()
    {
        RenderBanner(); RenderQuick(); RenderAnswer(); RenderHero(); RenderGoal(); RenderExperiment(); RenderResults(); RenderHistory();
        bool hasData = _a is not null && _store.Trials.Any(t => t.WorkloadId == _a.Workload.Id);
        _details.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- quick start: the whole feature in three steps ------------------------------------------------------------------

    private void RenderQuick()
    {
        _quick.Children.Clear();
        if (_store.ActiveWorkload is not { } w) return;
        _quick.Children.Add(Ui.Text("Find the best settings for this PC", 18, FontWeights.Bold));
        _quick.Children.Add(Ui.Muted("Three steps. You only choose a goal and press Start; Money Tweaks does the testing.", 12.5));

        var steps = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < 3; i++) steps.ColumnDefinitions.Add(new ColumnDefinition());
        int minutes = (int)Math.Ceiling((GenomeRunner.TotalRuns(_budget) * (w.DurationSec + (w.IsGame ? GameGenomeWorkload.CountdownSec + 20 : 13)) + 30) / 60.0);
        UIElement Step(int n, string title, string text, UIElement? extra = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeights.Bold, Foreground = Ui.Brush("OnAccent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            head.Children.Add(Ui.Text(title, 14, FontWeights.SemiBold));
            sp.Children.Add(head);
            sp.Children.Add(Ui.Muted(text, 12));
            if (extra is not null) sp.Children.Add(extra);
            return sp;
        }
        var goalBox = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var p in GenomeGoals.Presets)
        {
            var rb = new RadioButton { Content = p.Name, GroupName = "genomegoal", IsChecked = _store.Goal.Preset == p.Id, Margin = new Thickness(0, 3, 0, 0), ToolTip = p.Blurb, Foreground = Ui.Brush("Text") };
            var id = p.Id;
            rb.Checked += (_, _) => { if (_store.Goal.Preset != id) SetPreset(id); };
            goalBox.Children.Add(rb);
        }
        if (_store.Goal.Preset == "custom") goalBox.Children.Add(Ui.Muted("(Custom weights set under More options.)", 11.5));
        var s1 = Step(1, "Choose what you want", "What should \"better\" mean on this PC?", goalBox);
        var s2 = Step(2, $"Press Start, wait ~{minutes} min",
            $"Money Tweaks tries different CPU and GPU settings one at a time and runs a short 3D test ({w.DurationSec} s) for each, measuring FPS, temperatures and power. " +
            "A test window keeps popping up: don't use the PC meanwhile. Your own settings are put back at the end, even if you stop early.");
        var s3 = Step(3, "Apply the winner", "You get the best measured settings for your goal right here, with one Apply button and an Undo. Nothing changes permanently unless you press Apply.");
        Grid.SetColumn(s2, 1); Grid.SetColumn(s3, 2);
        steps.Children.Add(s1); steps.Children.Add(s2); steps.Children.Add(s3);
        _quick.Children.Add(steps);

        if (!Admin.IsElevated && _store.Factors.Any(f => f.Enabled && f.NeedsAdmin))
        {
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            row.Children.Add(new TextBlock { Text = "Not running as administrator: GPU settings can't be tested, so only CPU settings will be. ", FontSize = 12, Foreground = Ui.Brush("Gold"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(Ui.Btn("Restart as admin", (_, _) => { if (Admin.RestartElevated()) Application.Current.Shutdown(); }, "GhostButton"));
            _quick.Children.Add(row);
        }
        _start = Ui.Btn(_cts is null ? "▶  Start" : "Testing…", async (_, _) => await StartAsync(), "PrimaryButton", new Thickness(0, 14, 8, 0));
        _start.IsEnabled = _cts is null;
        _start.FontSize = 14; _start.Padding = new Thickness(22, 9, 22, 9);
        _stop = Ui.Btn("■  Stop (puts your settings back)", (_, _) => _cts?.Cancel(), "DangerButton", new Thickness(0, 14, 8, 0));
        _stop.IsEnabled = _cts is not null;
        _stop.ToolTip = $"Also {MacroService.EmergencyHotkeyText} anywhere, or Esc in the 3D window";
        var more = Ui.Btn("More options", (_, _) => { _advanced.IsExpanded = !_advanced.IsExpanded; if (_advanced.IsExpanded) Ui.ScrollTo(_advanced); }, "GhostButton", new Thickness(0, 14, 8, 0));
        _quick.Children.Add(Ui.Row(_start, _stop, more));
        foreach (var e in new UIElement[] { _bar, _status, _log }) if (e is FrameworkElement fe && fe.Parent is Panel old) old.Children.Remove(e);
        _quick.Children.Add(_bar);
        _quick.Children.Add(_status);
        _quick.Children.Add(_log);
    }

    private void SetPreset(string id)
    {
        var keep = _store.Goal;
        _store.Goal = GenomeGoals.FromPreset(id);
        _store.Goal.MaxCpuTempC ??= keep.MaxCpuTempC; _store.Goal.MaxGpuPowerW ??= keep.MaxGpuPowerW;
        if (keep.MaxGpuTempC is not null && GenomeGoals.Find(id)?.MaxGpuTempC is null) _store.Goal.MaxGpuTempC = keep.MaxGpuTempC;
        _store.Save();
        RenderGoal(); RenderAnswer(); RenderResults();
    }

    /// <summary>The answer, in one card: the best measured settings for the chosen goal, with Apply.</summary>
    private void RenderAnswer()
    {
        _answer.Children.Clear();
        if (_a is null || !_store.Trials.Any(t => t.WorkloadId == _a.Workload.Id)) return;
        var goalName = GenomeGoals.Find(_store.Goal.Preset)?.Name ?? "your custom goal";
        var rec = _a.Recommend(_store.Goal).FirstOrDefault(r => r.Goals.Contains("Your goal"));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "YOUR RESULT", Style = Ui.Res<Style>("Eyebrow") });
        if (rec is null)
        {
            sp.Children.Add(Ui.Text("Not enough results yet", 16, FontWeights.SemiBold));
            sp.Children.Add(Ui.Muted("A setting only counts once it has been measured at least twice without problems. Press Start again to keep going; each run builds on the earlier ones.", 12.5));
        }
        else if (rec.Evidence is { IsReference: true })
        {
            sp.Children.Add(Ui.Text($"Keep your current settings for {goalName.ToLowerInvariant()}", 16, FontWeights.SemiBold));
            sp.Children.Add(Ui.Muted(rec.Verdict, 12.5));
        }
        else
        {
            sp.Children.Add(Ui.Text($"Best for {goalName.ToLowerInvariant()}: {GenomeAnalysis.Describe(_store, rec.Config, _a.Reference)}", 16, FontWeights.SemiBold));
            var lines = new List<string>();
            foreach (var key in new[] { "fps", "low1", "gputemp", "cputemp", "gpupower" })
                if (rec.Evidence?.Measured.GetValueOrDefault(key) is { } m)
                {
                    var metric = GenomeMetrics.Get(key);
                    bool clear = m.Low > 0 || m.High < 0;
                    lines.Add($"{metric.Label}: {metric.Change(m.Mean)}{(clear ? "" : " (within the noise)")}");
                }
            sp.Children.Add(new TextBlock { Text = string.Join("   ·   ", lines), FontSize = 13, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            sp.Children.Add(Ui.Muted($"Measured {rec.Evidence?.N} times on this PC · {rec.Confidence.ToLowerInvariant()} confidence. {rec.Verdict}", 12));
            sp.Children.Add(Ui.Row(Ui.Btn("Apply these settings…", async (_, _) => await ApplyProfileAsync(rec), "PrimaryButton", new Thickness(0, 10, 8, 0)),
                Ui.Btn("See all results", (_, _) => Ui.ScrollTo(_details), "GhostButton", new Thickness(0, 10, 8, 0))));
        }
        _answer.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 0, 14), Child = sp });
    }

    // ---- banner: stale results / applied profile ------------------------------------------------------------------------

    private void RenderBanner()
    {
        _banner.Children.Clear();
        if (_a is null) return;
        var lines = new List<string>();
        if (_a.StaleTrials > 0)
        {
            var old = _store.Trials.Where(t => t.WorkloadId == _a.Workload.Id && _fp is not null && !t.Fingerprint.SameAs(_fp)).Select(t => t.Fingerprint).LastOrDefault();
            lines.Add($"{_a.StaleTrials} earlier result(s) were measured on a different setup ({(old is not null && _fp is not null ? string.Join(", ", _fp.ChangesSince(old)) : "driver, Windows, BIOS or game changed")}). " +
                "They stay in the history but don't count as measured now. Run an experiment to re-check: screening starts again automatically.");
        }
        if (_store.Applied is { } ap && _fp is not null && ap.Fingerprint.ChangesSince(_fp) is { Count: > 0 } ch)
            lines.Add($"Your applied profile \"{ap.Name}\" was measured before: {string.Join(", ", ch)}. Re-check it.");
        if (lines.Count == 0) return;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text("Results need re-checking", 14, FontWeights.SemiBold));
        foreach (var l in lines) sp.Children.Add(Ui.Muted(l, 12.5));
        _banner.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Gold"), Margin = new Thickness(0, 0, 0, 14), Child = sp });
    }

    // ---- hero: this PC's genome -------------------------------------------------------------------------------------------

    private void RenderHero()
    {
        _hero.Children.Clear();
        if (_a is null || _fp is null) return;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(new TextBlock { Text = "THIS PC'S GENOME", Style = Ui.Res<Style>("Eyebrow") });
        void Row(string k, string v) { var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; var l = Ui.Muted(k, 12); l.Width = 92; DockPanel.SetDock(l, Dock.Left); d.Children.Add(l); d.Children.Add(new TextBlock { Text = v, FontSize = 12, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap }); left.Children.Add(d); }
        Row("CPU", _fp.Cpu);
        Row("GPU", _fp.Gpu.Length > 0 ? $"{_fp.Gpu} · driver {_fp.GpuDriver}" : "—");
        Row("Windows", "build " + _fp.OsBuild);
        Row("BIOS", _fp.Bios);
        var mine = _store.Trials.Where(t => t.WorkloadId == _a.Workload.Id).ToList();
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 6) };
        void Stat(string big, string small)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
            sp.Children.Add(new TextBlock { Text = big, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Text") });
            sp.Children.Add(Ui.Muted(small, 11));
            stats.Children.Add(sp);
        }
        Stat(mine.Count(t => t.Status == TrialStatus.Ok).ToString(CultureInfo.CurrentCulture), "runs");
        Stat(_a.Evidence.Count(e => e.N > 0).ToString(CultureInfo.CurrentCulture), "configs");
        Stat(_store.Failures.Count(f => f.WorkloadId == _a.Workload.Id).ToString(CultureInfo.CurrentCulture), "failures");
        Stat(_a.Interactions().Count.ToString(CultureInfo.CurrentCulture), "interactions");
        left.Children.Add(stats);
        double? noise = _a.Fits.GetValueOrDefault("fps")?.Sigma;
        left.Children.Add(Ui.Muted(noise is { } n ? $"Run-to-run noise on this workload: ±{n * 100:0.0}% FPS per run (measured from your baselines)." : "Run-to-run noise: not measured yet.", 11.5));
        grid.Children.Add(left);
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = $"WHAT EACH SETTING DOES ON THIS PC · {_a.Workload.Name.ToUpperInvariant()}", Style = Ui.Res<Style>("Eyebrow") });
        var map = new EffectMap();
        map.Show(_a.Facts());
        right.Children.Add(new ScrollViewer { Content = map, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        foreach (var i in _a.Interactions().Take(4)) right.Children.Add(new TextBlock { Text = "⧉ " + i.text, FontSize = 11.5, Foreground = Ui.Brush("Violet"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        right.Children.Add(Ui.Muted("Compared with your current setting of each. Coloured = clearly better/worse (95% interval excludes zero); grey = not distinguishable from noise yet. Hover for intervals and trial counts.", 11));
        grid.Children.Add(right);
        _hero.Children.Add(grid);
    }

    // ---- goal ------------------------------------------------------------------------------------------------------------

    private void RenderGoal()
    {
        _goal.Children.Clear();
        _goal.Children.Add(Ui.Section("Your goal", "What \"better\" means for you. Weights mix the measured metrics into one score; hard limits are never broken by a recommendation."));
        var options = GenomeGoals.Presets.Select(p => (p.Name, p.Id, (string?)p.Blurb)).Append(("Custom", "custom", "Your own weights below.")).ToList();
        var picker = new SegmentedPicker(options, _store.Goal.Preset);
        picker.Changed += id =>
        {
            if (id != "custom")
            {
                var keep = _store.Goal;
                _store.Goal = GenomeGoals.FromPreset(id);
                _store.Goal.MaxCpuTempC ??= keep.MaxCpuTempC; _store.Goal.MaxGpuPowerW ??= keep.MaxGpuPowerW;
                if (keep.MaxGpuTempC is not null && GenomeGoals.Find(id)?.MaxGpuTempC is null) _store.Goal.MaxGpuTempC = keep.MaxGpuTempC;
            }
            else _store.Goal.Preset = "custom";
            _store.Save();
            RenderGoal(); RenderResults();
        };
        _goal.Children.Add(picker);
        if (GenomeGoals.Find(_store.Goal.Preset) is { } preset) _goal.Children.Add(Ui.Muted(preset.Blurb, 12));

        var weights = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var m in GenomeMetrics.All)
        {
            var sp = new StackPanel { Width = 210, Margin = new Thickness(0, 0, 18, 8), ToolTip = $"{m.Label}: {m.Source}." };
            double cur = _store.Goal.Weights.GetValueOrDefault(m.Key);
            var head = new DockPanel();
            var val = Ui.Text($"{cur * 100:0}%", 12, FontWeights.SemiBold); DockPanel.SetDock(val, Dock.Right); head.Children.Add(val);
            head.Children.Add(Ui.Muted(m.Label, 12));
            sp.Children.Add(head);
            var s = new Slider { Minimum = 0, Maximum = 1, Value = cur, SmallChange = 0.05, LargeChange = 0.1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            s.ValueChanged += (_, e) => { val.Text = $"{e.NewValue * 100:0}%"; };
            s.PreviewMouseUp += (_, _) => SetWeight(m.Key, s.Value);
            s.LostKeyboardFocus += (_, _) => SetWeight(m.Key, s.Value);
            sp.Children.Add(s);
            weights.Children.Add(sp);
        }
        _goal.Children.Add(weights);

        var limits = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Limit(string label, double? value, string unit, Action<double?> set)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 6) };
            sp.Children.Add(Ui.Muted(label + " ", 12));
            var tb = new TextBox { Text = value?.ToString("0.#", CultureInfo.CurrentCulture) ?? "", Width = 64, Tag = "none" };
            tb.LostKeyboardFocus += (_, _) =>
            {
                double? v = double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var d) && d > 0 ? d : null;
                if (v == value) return;
                set(v); value = v; _store.Goal.Preset = GenomeGoals.Find(_store.Goal.Preset) is null ? "custom" : _store.Goal.Preset;
                _store.Save(); RenderResults();
            };
            sp.Children.Add(tb);
            sp.Children.Add(Ui.Muted(" " + unit, 12));
            limits.Children.Add(sp);
        }
        Limit("Max GPU temperature", _store.Goal.MaxGpuTempC, "°C", v => _store.Goal.MaxGpuTempC = v);
        Limit("Max CPU temperature", _store.Goal.MaxCpuTempC, "°C", v => _store.Goal.MaxCpuTempC = v);
        Limit("Max FPS loss", _store.Goal.MaxFpsLossPct, "%", v => _store.Goal.MaxFpsLossPct = v);
        Limit("Max 1% low loss", _store.Goal.MaxLow1LossPct, "%", v => _store.Goal.MaxLow1LossPct = v);
        Limit("Max GPU power", _store.Goal.MaxGpuPowerW, "W", v => _store.Goal.MaxGpuPowerW = v);
        _goal.Children.Add(Ui.Text("Hard limits (empty = no limit)", 12.5, FontWeights.SemiBold));
        _goal.Children.Add(limits);
        _goal.Children.Add(Ui.Muted("Score units: 1 point = 1% FPS, 1% lows or frame time; 4 points = 1 percentage point less frame-time variability; 2 points = 1 °C cooler; 0.5 points = 1% less power. Changing the goal re-ranks existing results instantly; no new tests needed.", 11.5));
    }

    private void SetWeight(string key, double v)
    {
        if (Math.Abs(_store.Goal.Weights.GetValueOrDefault(key) - v) < 0.001) return;
        _store.Goal.Weights[key] = v;
        _store.Goal.Preset = "custom";
        _store.Save();
        RenderResults();
    }

    // ---- experiment ------------------------------------------------------------------------------------------------------

    private int _budget = 12;
    private readonly ProgressBar _bar = new() { Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 6) };
    private readonly TextBlock _status = Ui.Muted("", 12.5);
    private readonly StackPanel _log = new();
    private Button? _start, _stop;

    private void RenderExperiment()
    {
        _experiment.Children.Clear();
        _experiment.Children.Add(Ui.Section("Experiments", "Money Tweaks chooses what to test next, controls the conditions, repeats runs to beat noise and puts your settings back at the end."));
        var w = _store.ActiveWorkload!;

        // Workload.
        _experiment.Children.Add(Ui.Text("What to measure with", 12.5, FontWeights.SemiBold));
        var games = _store.Workloads.Where(x => x.IsGame).ToList();
        var opts = new List<(string, string, string?)> { ("Built-in 3D scene", "synthetic", "Repeatable, automatic, calibrated so the display's refresh rate doesn't cap FPS.") };
        opts.AddRange(games.Select(g => (g.Name + " (manual)", g.Id, (string?)"Controlled manual comparison in the game.")));
        opts.Add(("+ Measure in a game…", "+game", "Pick a running game from your library."));
        var wp = new SegmentedPicker(opts, w.Id);
        wp.Changed += async id =>
        {
            if (id == "+game") { await AddGameWorkloadAsync(); return; }
            _store.ActiveWorkloadId = id; _store.Save(); _fp = await Task.Run(() => _backend.Fingerprint(_store.ActiveWorkload!)); Analyze(); RenderAll();
        };
        _experiment.Children.Add(wp);
        _experiment.Children.Add(Ui.Muted(w.IsGame
            ? $"Manual comparison: before each capture you go to the same spot in {w.Name} and do the same thing ({(w.Scene.Length > 0 ? w.Scene : "describe your scene below")}). Frame timing comes from the game's own Present events (read-only; nothing is injected, anti-cheat safe). Less repeatable than the built-in scene: expect wider intervals and use more runs."
            : $"{(w.Scene.Length > 0 ? w.Scene + ". " : "Calibrated on the first run. ")}Synthetic: it shows how this PC responds in this scene; games can respond differently, so confirm important results in your game.", 12));
        if (w.IsGame)
        {
            var scene = new TextBox { Text = w.Scene, Tag = "Your test scene, e.g. \"Creative island X, spawn, look north, 30 s\"", Width = 460, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            scene.LostKeyboardFocus += (_, _) => { w.Scene = scene.Text.Trim(); _store.Save(); };
            _experiment.Children.Add(scene);
        }

        // Duration and budget.
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var dur = new SegmentedPicker([("15 s", "15", null), ("20 s", "20", null), ("30 s", "30", null), ("60 s", "60", null)], w.DurationSec.ToString(CultureInfo.InvariantCulture));
        dur.Changed += v => { w.DurationSec = int.Parse(v, CultureInfo.InvariantCulture); _store.Save(); RenderExperiment(); RenderQuick(); };
        var bud = new SegmentedPicker([("6 trials", "6", null), ("12 trials", "12", null), ("24 trials", "24", null), ("40 trials", "40", null)], _budget.ToString(CultureInfo.InvariantCulture));
        bud.Changed += v => { _budget = int.Parse(v, CultureInfo.InvariantCulture); RenderExperiment(); RenderQuick(); };
        row.Children.Add(Labeled("Per run", dur));
        row.Children.Add(Labeled("This session", bud));
        _experiment.Children.Add(row);
        int runs = GenomeRunner.TotalRuns(_budget);
        double perRun = w.DurationSec + (w.IsGame ? GameGenomeWorkload.CountdownSec + 20 : 3 + 10);
        _experiment.Children.Add(Ui.Muted($"{runs} measured runs ({_budget} experiments + {runs - _budget} baseline runs of your own settings to control drift), about {Math.Ceiling((runs * perRun + 30) / 60):0} minutes{(w.IsGame ? " plus the time you need to get into position" : "")}. Results are saved after every run, so you can stop any time and continue later.", 12));

        // Factors.
        _experiment.Children.Add(Ui.Text("Settings it may vary", 12.5, FontWeights.SemiBold));
        var fac = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var f in _store.Factors.Where(f => !f.IsManual || w.IsGame))
        {
            var cb = new CheckBox
            {
                IsChecked = f.Enabled, Margin = new Thickness(0, 2, 0, 2), IsEnabled = _cts is null,
                Content = $"{f.Name}: {string.Join(" · ", f.Levels.Select(l => l.Label))}" + (f.NeedsAdmin && !Admin.IsElevated ? "  (needs administrator)" : ""),
                ToolTip = $"Changed through {(f.IsManual ? "you, in the game, when asked" : f.Interface)}. Your current: {f.LabelOf(Reference().GetValueOrDefault(f.Id, ""))}.",
            };
            cb.Click += (_, _) => { f.Enabled = cb.IsChecked == true; _store.Save(); Analyze(); RenderResults(); RenderHero(); };
            fac.Children.Add(cb);
        }
        _experiment.Children.Add(fac);
        foreach (var n in _discoveryNotes) _experiment.Children.Add(Ui.Muted("• " + n, 11.5));

        // Add a tweak / game setting.
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var tweaks = GenomeSystem.TweakCandidates(w).Where(t => _store.Factor("tweak." + t.Id) is null).ToList();
        var tcb = new ComboBox { Width = 320, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var t in tweaks) tcb.Items.Add(t.Name);
        if (tweaks.Count > 0) tcb.SelectedIndex = 0;
        add.Children.Add(tcb);
        add.Children.Add(Ui.Btn("Add Windows tweak", (_, _) =>
        {
            if (tcb.SelectedIndex < 0) return;
            _store.Factors.Add(GenomeSystem.TweakFactor(tweaks[tcb.SelectedIndex]));
            _store.Save(); Analyze(); RenderExperiment();
        }, "GhostButton", new Thickness(0, 0, 8, 0)));
        _experiment.Children.Add(add);
        if (w.IsGame)
        {
            var gs = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var name = new TextBox { Width = 200, Tag = "Game setting, e.g. View distance", Margin = new Thickness(0, 0, 8, 0) };
            var levels = new TextBox { Width = 260, Tag = "Values, yours first: Epic, High, Medium", Margin = new Thickness(0, 0, 8, 0) };
            gs.Children.Add(name); gs.Children.Add(levels);
            gs.Children.Add(Ui.Btn("Add game setting", (_, _) =>
            {
                var ls = levels.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
                if (name.Text.Trim().Length == 0 || ls.Count < 2) { Dialog.Show("Game setting", "Give it a name and at least two values, separated by commas, with the one you use now first."); return; }
                _store.Factors.Add(new GenomeFactor
                {
                    Id = "game." + Guid.NewGuid().ToString("N")[..8], Name = name.Text.Trim(), Kind = FactorKind.Manual, Interface = "you, in the game's own settings",
                    Levels = ls.Select((l, i) => new FactorLevel { Id = "v" + i, Label = l, Value = i }).ToList(),
                });
                _store.Save(); Analyze(); RenderExperiment();
            }, "GhostButton"));
            _experiment.Children.Add(gs);
            _experiment.Children.Add(Ui.Muted("Game settings are never changed by Money Tweaks: it tells you what to set before a capture and reminds you to put it back.", 11.5));
        }

        // Controls.
        _experiment.Children.Add(Ui.Muted("Start, Stop and progress are in the card at the top of the page.", 11.5));
    }

    private static StackPanel Labeled(string label, UIElement e)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 20, 4) };
        sp.Children.Add(Ui.Muted(label, 11.5));
        sp.Children.Add(e);
        return sp;
    }

    private async Task AddGameWorkloadAsync()
    {
        var running = await Task.Run(() =>
        {
            var games = Core.Games.GameLibrary.Items(Core.Games.GameLibrary.Current);
            return Core.Games.GameRuntime.Statuses(games).Where(s => s.status == Core.Games.GameStatus.Running && s.match.Process is not null)
                .Select(s => (name: s.game.Def.Name, path: s.match.Process!.Path ?? s.install.Install?.ExePath, exe: s.match.Process!.Exe)).ToList();
        });
        if (running.Count == 0) { Dialog.Show("Start the game first", "Start the game you want to measure (it must be in your Game Library), then pick it here. Money Tweaks only reads its frame timing; nothing is injected."); RenderExperiment(); return; }
        var g = running[0];
        if (running.Count > 1 && !Dialog.Confirm("Which game?", $"Several games are running. Use {g.name}? (Close the others to pick another.)", "Use " + g.name)) { RenderExperiment(); return; }
        var id = "game:" + g.name.ToLowerInvariant().Replace(' ', '-');
        var w = _store.Workloads.FirstOrDefault(x => x.Id == id);
        if (w is null) { w = new GenomeWorkload { Id = id, Kind = "game", Name = g.name, DurationSec = 30, GameExe = g.path ?? g.exe }; _store.Workloads.Add(w); }
        else w.GameExe = g.path ?? w.GameExe;
        _store.ActiveWorkloadId = id;
        _store.Save();
        _fp = await Task.Run(() => _backend.Fingerprint(w));
        Analyze(); RenderAll();
    }

    private async Task StartAsync()
    {
        if (_cts is not null || _store.ActiveWorkload is not { } w) return;
        var factors = _store.Factors.Where(f => f.Enabled && (!f.IsManual || w.IsGame)).ToList();
        if (factors.Count == 0) { Dialog.Show("Nothing to vary", "Tick at least one setting under \"Settings it may vary\"."); return; }
        if (factors.Any(f => f.NeedsAdmin) && !Admin.IsElevated)
        {
            var adminOnly = factors.Where(f => f.NeedsAdmin).ToList();
            if (Dialog.Confirm("Include GPU settings?", $"{string.Join(", ", adminOnly.Select(f => f.Name))} can only be changed by an administrator. Restart Money Tweaks as administrator to include them? Nothing has been changed.", "Restart as admin", DialogKind.Info))
            {
                if (Admin.RestartElevated()) Application.Current.Shutdown();
                return;
            }
            if (!Dialog.Confirm("Test CPU settings only?", "Without administrator rights Money Tweaks can still test the CPU settings. You can include the GPU settings later by running as administrator.", "Test CPU settings", DialogKind.Info)) return;
            foreach (var f in adminOnly) f.Enabled = false;
            _store.Save();
            factors = factors.Except(adminOnly).ToList();
            if (factors.Count == 0) { Dialog.Show("Nothing to test", "Every selected setting needs administrator rights."); return; }
        }
        if (!w.IsGame)
        {
            var games = await Task.Run(TuningViewAutoScan.RunningGames);
            if (games.Count > 0) { Dialog.Show("Close your games first", $"{string.Join(", ", games)} is running and would distort the measurements."); return; }
        }
        else if (GameGenomeWorkload.FindPid(w) is null) { Dialog.Show("Start the game first", $"{w.Name} isn't running."); return; }

        int runs = GenomeRunner.TotalRuns(_budget);
        var changes = string.Join("\n", factors.Select(f => $"• {f.Name}: {string.Join(", ", f.Levels.Where(l => l.Id != Reference().GetValueOrDefault(f.Id)).Select(l => l.Label))}"));
        if (!Dialog.Confirm("Start testing?",
                $"Money Tweaks will run {runs} short tests (about {Math.Ceiling(runs * (w.DurationSec + (w.IsGame ? GameGenomeWorkload.CountdownSec + 20 : 13)) / 60.0):0} minutes), changing these settings between them:\n{changes}\n\n" +
                (w.IsGame ? "You'll be asked to get into position in the game before each test." : "A 3D test window opens for each test. Don't use the PC until it's done.") +
                "\n\nYour settings are put back at the end, when you press Stop, or if something fails. Tests stop on their own if the PC gets too hot.", "Start", DialogKind.Info)) return;

        _cts = new CancellationTokenSource();
        void OnHotkey() => Dispatcher.BeginInvoke(() => _cts?.Cancel());
        MacroService.EmergencyStopPressed += OnHotkey;
        RenderExperiment();
        _bar.Visibility = Visibility.Visible; _bar.Maximum = runs; _bar.Value = 0;
        _log.Children.Clear();
        try
        {
            if (!w.IsGame && w.Scene.Length == 0)
            {
                var msg = await SyntheticGenomeWorkload.CalibrateAsync(w, t => _status.Text = t, _cts.Token);
                _store.Save();
                Log.Info("genome calibration: " + msg);
                AddLog(msg);
            }
            Task<bool> Ask(string title, string text) => Task.FromResult(Dialog.Confirm(title, text, "Continue", DialogKind.Info));
            IGenomeWorkload load = w.IsGame ? new GameGenomeWorkload(Ask) : new SyntheticGenomeWorkload();
            var progress = new Progress<GenomeProgress>(p =>
            {
                _bar.Value = Math.Min(_bar.Maximum, p.Run);
                _status.Text = p.Text;
                if (p.Last is { } t) { AddLog($"{(t.IsBaseline ? "Baseline" : t.Reason)} → {(t.Status == TrialStatus.Ok ? GenomeRunner.Summary(t) : "FAILED: " + t.Problem)}"); Analyze(); RenderResults(); RenderHero(); }
            });
            var session = await GenomeRunner.RunAsync(_store, w, _budget, _backend, load, Ask, progress, _cts.Token);
            _status.Text = session.Outcome;
            if (!session.Outcome.Contains("restored and read back", StringComparison.Ordinal)) Dialog.Show("Session ended", session.Outcome, DialogKind.Warning);
        }
        catch (OperationCanceledException) { _status.Text = "Stopped before the session started. Nothing was changed."; }
        catch (Exception ex) { _status.Text = "The experiment failed: " + ex.Message; Log.Error("genome start", ex); }
        finally
        {
            MacroService.EmergencyStopPressed -= OnHotkey;
            _cts.Dispose(); _cts = null;
            _bar.Visibility = Visibility.Collapsed;
            _now = await Task.Run(() => _backend.ReadCurrent(_store.Factors.Where(f => !f.IsManual).ToList()));
            Analyze();
            var status = _status.Text; var log = _log.Children.Cast<UIElement>().ToList();
            RenderAll();
            _status.Text = status;
            _log.Children.Clear(); foreach (var l in log) _log.Children.Add(l);
        }
    }

    private void AddLog(string text)
    {
        _log.Children.Insert(0, new TextBlock { Text = $"{DateTime.Now:HH:mm:ss}  {text}", FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
        while (_log.Children.Count > 12) _log.Children.RemoveAt(_log.Children.Count - 1);
    }
}
