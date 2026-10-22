using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Lab;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>Reality Lab experiments: preview, estimate, measured A/B with guaranteed restore, results, keep and roll back.</summary>
public sealed partial class RealityLabView
{
    private const int RunSeconds = 20, Pairs = 2, SceneIntensity = 3;
    private static readonly (int w, int h) SceneRes = (1280, 720);

    private readonly StackPanel _preview = new();
    private readonly TextBlock _status = Ui.Muted("", 12.5);
    private readonly ProgressBar _bar = new() { Height = 6, Maximum = 1, Margin = new Thickness(0, 8, 0, 4), Visibility = Visibility.Collapsed };
    private readonly Border _resultCard = new();
    private readonly Border _keptCard = new();
    private Button? _runBtn, _stopBtn;
    private CancellationTokenSource? _cts;
    private bool _measuring, _busy;
    private bool _withScene;
    private string _selId = RealityLab.Experiments[0].Id;
    private int _previewVersion;

    // The last experiment's measured runs (drive the Before/After views of the model).
    private List<BenchmarkRun> _lastRuns = [];
    private string _lastName = "";
    private DateTime _lastAt;

    private sealed record Choice(string Id, string Name, string Goal, IReadOnlyList<string> TweakIds, bool Curated);

    private static List<Choice> Choices() =>
        RealityLab.Experiments.Select(e => new Choice(e.Id, e.Name, e.Goal, e.TweakIds, true))
            .Concat(PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).Select(p => new Choice(p.Id, p.Name, p.Description, p.TweakIds, false)))
            .ToList();

    private Choice Selected => Choices().FirstOrDefault(c => c.Id == _selId) ?? Choices()[0];

    private UIElement BuildExperimentCard()
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Text("Experiment", 15, FontWeights.SemiBold));
        head.Children.Add(new Border { Width = 8 });
        head.Children.Add(Ui.Tag("SAFE TWEAKS ONLY · ALWAYS RESTORED", "Success"));
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock
        {
            Text = "Pick a set of tweaks. You'll see every setting it changes and what is expected, before anything happens. Running it switches the tweaks off and on in a counterbalanced order (off, on, on, off), measures each state, and then puts every setting back exactly as it was. Nothing is kept unless you choose to keep it afterwards.",
            Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });

        sp.Children.Add(Label("Lab experiments"));
        sp.Children.Add(ChoiceRow(Choices().Where(c => c.Curated)));
        sp.Children.Add(Label("Or one of your presets (only its safe, switchable tweaks are tested)"));
        FillPresetRow();
        sp.Children.Add(_presetRow);
        var own = Ui.Btn("+  Make your own set", (_, _) => { PresetsView.OpenNewOnShow = true; MainWindow.Current?.Navigate("presets"); }, "GhostButton", new Thickness(0, 0, 0, 4));
        own.ToolTip = "Pick any tweaks in the preset editor; your new preset appears here, and only its safe, switchable tweaks are tested";
        sp.Children.Add(Ui.Row(own));
        sp.Children.Add(_preview);

        sp.Children.Add(Label("Measure"));
        var mode = new WrapPanel();
        foreach (var (scene, text, tip) in new[]
        {
            (false, $"Resource use (about {Minutes(false)} min)", "CPU, GPU, RAM and temperatures while the PC is otherwise idle, from Windows counters."),
            (true, $"Resource use + 3D FPS scene (about {Minutes(true)} min)", "Adds the synthetic 3D scene from Test & Benchmark: real frame times, average FPS and 1% lows. Synthetic: games can react differently."),
        })
        {
            var rb = new RadioButton { Content = text, ToolTip = tip, Style = Ui.Res<Style>("Chip"), GroupName = "labmode", IsChecked = _withScene == scene, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _withScene = scene; RefreshPreview(); };
            mode.Children.Add(rb);
        }
        sp.Children.Add(mode);

        _runBtn = Ui.Btn("Run experiment", async (_, _) => await RunAsync(), "PrimaryButton", new Thickness(0, 12, 8, 0));
        _stopBtn = Ui.Btn("■  Emergency stop", (_, _) => _cts?.Cancel(), "DangerButton", new Thickness(0, 12, 8, 0));
        _stopBtn.IsEnabled = false;
        _stopBtn.ToolTip = $"Also: Esc in the 3D scene window, or {MacroService.EmergencyHotkeyText} anywhere. Settings are restored either way.";
        sp.Children.Add(Ui.Row(_runBtn, _stopBtn));
        _status.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(_bar);
        sp.Children.Add(_status);
        RefreshPreview();
        return Ui.Card(sp);
    }

    /// <summary>Rough duration: each run settles (5–20 s) and measures; the scene adds warm-up and a 10 s priming run.</summary>
    private static int Minutes(bool scene) =>
        (int)Math.Max(1, Math.Round(Pairs * 2 * (RunSeconds + 12 + (scene ? StressScene.WarmupSec + RunSeconds + 4 : 0)) / 60.0 + (scene ? 0.3 : 0)));

    private static TextBlock Label(string t) => new() { Text = t, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 14, 0, 6) };

    private readonly WrapPanel _presetRow = new();

    /// <summary>Presets can change on the Presets page (including a set made for the lab), so this row is refreshed on show.</summary>
    private void FillPresetRow()
    {
        _presetRow.Children.Clear();
        AddChoices(_presetRow, Choices().Where(c => !c.Curated));
        if (Choices().All(c => c.Id != _selId)) { _selId = RealityLab.Experiments[0].Id; RefreshPreview(); }
    }

    private WrapPanel ChoiceRow(IEnumerable<Choice> items)
    {
        var row = new WrapPanel();
        AddChoices(row, items);
        return row;
    }

    private void AddChoices(Panel row, IEnumerable<Choice> items)
    {
        foreach (var c in items)
        {
            var rb = new RadioButton { Content = c.Name, ToolTip = c.Goal, Style = Ui.Res<Style>("Chip"), GroupName = "labexp", IsChecked = c.Id == _selId, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _selId = c.Id; RefreshPreview(); };
            row.Children.Add(rb);
        }
    }

    // ---- preview + estimate -----------------------------------------------------------------------------------------

    private async void RefreshPreview()
    {
        var version = ++_previewVersion;
        var choice = Selected;
        var (usable, left) = RealityLab.Usable(choice.TweakIds);
        _preview.Children.Clear();
        _preview.Children.Add(new TextBlock { Text = choice.Goal, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontStyle = FontStyles.Italic });
        if (_runBtn is not null) _runBtn.IsEnabled = usable.Count > 0 && !_measuring;
        if (usable.Count == 0)
        {
            _preview.Children.Add(Ui.Muted("Nothing in this set can be tested here: " + string.Join("; ", left), 12.5));
            return;
        }

        // What changes: each tweak, its state now, and every setting it writes.
        _preview.Children.Add(new TextBlock { Text = "WHAT WOULD CHANGE (PREVIEW · NOTHING CHANGED YET)", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 14, 0, 6) });
        var items = usable.Select(t => new TweakItem(t)).ToList();
        var list = new StackPanel();
        _preview.Children.Add(list);
        void Render()
        {
            list.Children.Clear();
            foreach (var i in items)
            {
                var t = i.Tweak;
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                var top = new WrapPanel();
                top.Children.Add(Ui.Text(t.Name, 13, FontWeights.SemiBold));
                top.Children.Add(new Border { Width = 8 });
                var (st, tone) = i.State switch
                {
                    TweakState.Applied when !BackupStore.Exists(t.Id) => ("Already set outside Money Tweaks: left as is", "Muted"),
                    TweakState.Applied => ("On now: switched off for the “without” runs", "Accent"),
                    TweakState.NotApplied => ("Off now: switched on for the “with” runs", "Accent"),
                    TweakState.Unknown => ("Checking…", "Muted"),
                    _ => (i.StatusText, "Gold"),
                };
                top.Children.Add(Ui.Tag(st, tone));
                if (t.RequiresAdmin) { top.Children.Add(new Border { Width = 6 }); top.Children.Add(Ui.Tag("Admin", "Muted")); }
                row.Children.Add(top);
                foreach (var op in t.Ops)
                    row.Children.Add(new TextBlock { Text = "→ " + op.Describe(), FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 0, 0) });
                var facts = TweakInfo.For(t);
                row.Children.Add(new TextBlock { Text = $"Undo: {facts.Undo}", FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 3, 0, 0) });
                list.Children.Add(row);
            }
        }
        Render();
        if (left.Count > 0) _preview.Children.Add(Ui.Muted("Left out: " + string.Join("; ", left), 12));

        // Estimate: clearly labelled, per metric, with the reasons.
        _preview.Children.Add(EstimatePanel(usable));
        if (LastMeasured(choice.Name) is { } last) _preview.Children.Add(last);

        await TweakService.RefreshStatesAsync(items);
        if (version == _previewVersion) Render();
    }

    private UIElement EstimatePanel(List<Tweak> usable)
    {
        var sp = new StackPanel();
        var head = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        head.Children.Add(new TextBlock { Text = "EXPECTED EFFECT", Style = Ui.Res<Style>("Eyebrow"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new Border { Width = 8 });
        head.Children.Add(Ui.Tag("ESTIMATE · NOT A MEASUREMENT", "Gold"));
        sp.Children.Add(head);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var key in RealityLab.MetricKeys)
        {
            if (!_withScene && RealityLab.SceneOnly.Contains(key)) continue;
            var m = BenchmarkStats.Metrics.First(x => x.Key == key);
            var (e, why) = RealityLab.Combine(usable, key);
            g.RowDefinitions.Add(new RowDefinition());
            void Cell(int c, UIElement el) { Grid.SetRow(el, row); Grid.SetColumn(el, c); if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 18, 3); g.Children.Add(el); }
            Cell(0, new TextBlock { Text = m.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            Cell(1, new TextBlock { Text = RealityLab.Describe(e, m), FontSize = 12.5, Foreground = Ui.Brush(e == Expect.Unknown ? "Muted" : "Text") });
            Cell(2, new TextBlock { Text = why.Count == 0 ? "None of these settings works on this." : string.Join(" ", why), FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
            row++;
        }
        sp.Children.Add(g);
        var conf = usable.Select(t => $"{t.Name}: {TweakInfo.For(t).Confidence}").ToList();
        sp.Children.Add(Ui.Muted("Estimated from how each setting works, not from tests on your PC, and never a promise: hardware, drivers and what else is running all change the outcome. " +
            "Only the measurement counts. Confidence in each tweak's usual benefit: " + string.Join(" · ", conf) + ".", 11.5));
        ((TextBlock)sp.Children[^1]).TextWrapping = TextWrapping.Wrap;
        return new Border { Child = sp, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(10), Background = Ui.Brush("GoldSoft") };
    }

    /// <summary>The most recent earlier lab measurement of the same experiment on this PC, if any.</summary>
    private static UIElement? LastMeasured(string name)
    {
        var f = BenchmarkStore.Load();
        var s = f.Studies.Where(x => x.TargetKind == "lab" && x.TargetName == name).MaxBy(x => x.Created);
        if (s is null) return null;
        var runs = f.Runs.Where(r => r.StudyId == s.Id).ToList();
        var off = runs.Where(r => r.Phase == "Baseline").ToList(); var on = runs.Where(r => r.Phase == "After").ToList();
        var changes = BenchmarkStats.Metrics.Where(m => RealityLab.MetricKeys.Contains(m.Key)).Select(m => BenchmarkStats.Compare(m, off, on))
            .Where(c => c.Verdict is Verdict.Better or Verdict.Worse).Select(c => $"{c.Metric.Label} {(c.Verdict == Verdict.Better ? "better" : "worse")} ({c.Pct:+0.0;-0.0}%)").ToList();
        var t = Ui.Muted($"Measured on this PC before ({s.Created:g}): " + (changes.Count == 0 ? "no change larger than run-to-run variation." : string.Join(", ", changes) + ".") + " Details in Test & Benchmark › History.", 12);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 8, 0, 0);
        return t;
    }

    // ---- run ----------------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        if (_busy || _measuring) return;
        var choice = Selected;
        var (usable, left) = RealityLab.Usable(choice.TweakIds);
        if (usable.Count == 0) return;
        var skipped = new List<string>(left);
        _busy = true;
        _status.Text = "Reading the current state of each tweak…";
        Dictionary<string, bool> original;
        try { original = await Task.Run(() => TweakStateGuard.Capture(usable, skipped)); }
        finally { _busy = false; }
        if (original.Count == 0)
        {
            _status.Text = "";
            Dialog.Show("Nothing to switch", "None of these tweaks can be switched for a test right now:\n" + string.Join("\n", skipped.Select(s => "• " + s)));
            return;
        }
        var tweaks = original.Keys.Select(TweakCatalog.Find).OfType<Tweak>().ToList();
        var games = await Task.Run(BenchmarkView.RunningGames);
        if (games.Count > 0 && !Dialog.Confirm("A game is running", $"{string.Join(", ", games)} is running. It competes for CPU and GPU, so the results wouldn't be valid. Close it first for a fair test.", "Run anyway", DialogKind.Warning)) { _status.Text = ""; return; }
        var plan = SyntheticPlan.Abba(Pairs);
        var msg = $"Experiment: {choice.Name}\n{plan.Length} measured runs of {RunSeconds} s ({string.Join(", ", plan.Select(on => on ? "with" : "without"))}), about {Minutes(_withScene)} min" +
                  (_withScene ? $", each followed by the 3D scene (intensity {SceneIntensity}, {SceneRes.w}×{SceneRes.h}) in its own window." : ".") +
                  $"\n\nTemporarily switched ({tweaks.Count}):\n" + string.Join("\n", tweaks.Select(t => "• " + t.Name)) +
                  "\n\nEach tweak keeps its own backup. When the experiment ends, is stopped, or fails, every tweak is put back exactly as it is now and read back. If Money Tweaks closes mid-test, it offers the restore on the next start." +
                  (skipped.Count > 0 ? $"\n\nNot switched ({skipped.Count}): {string.Join("; ", skipped)}" : "") +
                  "\n\nClose other apps for consistent results. Nothing is kept afterwards unless you choose Keep.";
        if (!Dialog.Confirm($"Run “{choice.Name}”?", msg, "Run experiment", DialogKind.Warning)) { _status.Text = ""; return; }
        if (!TweakService.EnsureAdmin(tweaks)) { _status.Text = ""; return; }

        _measuring = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        void OnHotkey() => Dispatcher.BeginInvoke(() => _cts?.Cancel());
        MacroService.EmergencyStopPressed += OnHotkey;
        _runBtn!.IsEnabled = false; _stopBtn!.IsEnabled = true;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        SyncRunning();
        TweakStateGuard.SavePending(original, RealityLab.PendingPath);
        var study = new BenchmarkStudy
        {
            Name = $"Reality Lab: {choice.Name} ({DateTime.Now:g})", TargetKind = "lab", TargetName = choice.Name, DurationSec = RunSeconds, RunsPerPhase = Pairs, Endpoint = "",
            Scene = $"Reality Lab · {RunSeconds} s per run, idle resource use" + (_withScene ? $" + synthetic 3D scene (intensity {SceneIntensity}, {SceneRes.w}×{SceneRes.h})" : ""),
            Notes = "Counterbalanced A/B; every tweak restored afterwards.",
        };
        var runs = new List<BenchmarkRun>();
        string? error = null;
        List<string> restoreProblems = [];
        int steps = plan.Length * (_withScene ? 2 : 1) + (_withScene ? 1 : 0), done = 0;
        void Step() => _bar.Value = (double)++done / steps;
        try
        {
            if (_withScene)
            {
                _status.Text = "Priming run (10 s, not measured): warms up the GPU so no state gets the cold first run…";
                var prime = await StressScene.RunAsync(new SceneOptions(SceneIntensity, 10, SceneRes.w, SceneRes.h, "Reality Lab · priming run · not measured"), ct);
                if (!prime.Completed) throw new OperationCanceledException(prime.StopReason);
                Step();
            }
            for (int i = 0; i < plan.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                bool on = plan[i];
                var what = on ? "with tweaks" : "without tweaks";
                _status.Text = $"Run {i + 1}/{plan.Length}: switching ({what})…";
                var problems = await Task.Run(() => TweakStateGuard.SetAll(original.Keys, on), ct);
                if (problems.Count > 0) throw new InvalidOperationException("Couldn't set the tweak state, so the comparison would be invalid:\n" + string.Join("\n", problems));
                var n = i;
                await BenchmarkSampler.SettleAsync(t => _status.Text = $"Run {n + 1}/{plan.Length} ({what}): {t}", ct);
                var progress = new Progress<BenchmarkTick>(tk => _status.Text = $"Run {n + 1}/{plan.Length} ({what}): measuring resource use {tk.Second}/{tk.Total} s · CPU {tk.Cpu:0}%" + (tk.Gpu is { } g ? $" · GPU {g:0}%" : ""));
                var idle = await BenchmarkSampler.RunAsync(study.Id, on ? "After" : "Baseline", RunSeconds, null, study.Scene, progress, ct, App.Version);
                Step();
                if (_withScene)
                {
                    _status.Text = $"Run {i + 1}/{plan.Length} ({what}): 3D scene running. Esc or Emergency stop to cancel.";
                    var at = DateTime.Now;
                    var r = await StressScene.RunAsync(new SceneOptions(SceneIntensity, RunSeconds, SceneRes.w, SceneRes.h, $"Reality Lab · run {i + 1}/{plan.Length} · {what.ToUpperInvariant()}"), ct);
                    if (!r.Completed) throw new OperationCanceledException(r.StopReason);
                    // FPS and frame times from the scene; resource use under that load is kept on the same run.
                    var scene = BenchmarkView.RunFromScene(r, study.Id, on, at, RunSeconds, study.Scene, $"Synthetic 3D scene (intensity {SceneIntensity}, {SceneRes.w}×{SceneRes.h})");
                    idle.Frames = scene.Frames;
                    idle.FpsSeries = scene.FpsSeries;
                    Step();
                }
                runs.Add(idle);
            }
        }
        catch (OperationCanceledException) { error = "Stopped. Completed runs were kept."; }
        catch (Exception ex) { error = ex.Message; Log.Error("Reality Lab experiment failed", ex); }
        finally
        {
            MacroService.EmergencyStopPressed -= OnHotkey;
            _status.Text = "Putting every setting back as it was…";
            restoreProblems = await Task.Run(() => TweakStateGuard.Restore(original));
            if (restoreProblems.Count == 0) TweakStateGuard.ClearPending(RealityLab.PendingPath);
            _measuring = false;
            _cts.Dispose(); _cts = null;
            _runBtn.IsEnabled = true; _stopBtn.IsEnabled = false;
            _bar.Visibility = Visibility.Collapsed;
            SyncRunning();
        }

        var restoreText = restoreProblems.Count == 0 ? $"All {original.Count} tweak(s) are back exactly as before and were read back."
            : "Some settings were not restored (see Backup & Restore and the tweak pages):\n" + string.Join("\n", restoreProblems);
        _status.Text = (error is null ? "Finished. " : error + " ") + restoreText;
        Log.Info($"Reality Lab '{choice.Name}': {runs.Count} run(s){(error is null ? "" : " – " + error)}; restore {(restoreProblems.Count == 0 ? "verified" : "problems")}");
        if (restoreProblems.Count > 0) Dialog.Show("Some settings were not restored", restoreText, DialogKind.Error);
        else if (error is not null && runs.Count == 0) Dialog.Show("Experiment stopped", error + "\n\n" + restoreText, error.StartsWith("Stopped", StringComparison.Ordinal) ? DialogKind.Info : DialogKind.Error);
        if (runs.Count == 0) return;
        try { BenchmarkView.AddStudy(study, runs); }
        catch (Exception ex) { Log.Error("Saving the lab result to benchmark history failed", ex); }
        _lastRuns = runs; _lastName = choice.Name; _lastAt = DateTime.Now;
        SyncViewChips();
        RenderResult(choice, usable, original, runs, error);
    }

    // ---- results ------------------------------------------------------------------------------------------------

    private void RenderResult(Choice choice, List<Tweak> usable, Dictionary<string, bool> original, List<BenchmarkRun> runs, string? error)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = $"RESULT · {choice.Name.ToUpperInvariant()}", Style = Ui.Res<Style>("Eyebrow") });
        var off = runs.Where(r => r.Phase == "Baseline").ToList();
        var on = runs.Where(r => r.Phase == "After").ToList();
        var rows = RealityLab.MetricKeys.Select(k => BenchmarkStats.Metrics.First(m => m.Key == k))
            .Select(m => (m, c: BenchmarkStats.Compare(m, off, on), e: RealityLab.Combine(usable, m.Key).expect))
            .Where(x => x.c.Before is not null || x.c.After is not null).ToList();
        var meaningful = rows.Where(x => x.c.Verdict is Verdict.Better or Verdict.Worse).ToList();
        var headline = off.Count < 2 || on.Count < 2 ? "Not conclusive: the experiment stopped before two runs of each state were measured."
            : meaningful.Count == 0 ? "No measurable difference: every change stayed within run-to-run variation."
            : string.Join(" · ", meaningful.Select(x => $"{x.m.Label} {(x.c.Verdict == Verdict.Better ? "better" : "worse")} ({x.c.Pct:+0.0;-0.0}%)"));
        sp.Children.Add(new TextBlock { Text = headline, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (error is not null) sp.Children.Add(Ui.Muted(error, 12));

        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        string[] cols = ["Metric", "Estimate", $"Without (n={off.Count})", $"With (n={on.Count})", "Change", "Measured", "Estimate vs measured"];
        foreach (var _ in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int r, int c, UIElement e) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 18, 3); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < cols.Length; c++) Cell(0, c, new TextBlock { Text = cols[c], FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Subtle") });
        string S(Summary? x, string unit) => x is null ? "–" : x.N > 1 ? $"{BenchmarkStats.Fmt(x.Mean)} ± {BenchmarkStats.Fmt(x.Sd)} {unit}" : $"{BenchmarkStats.Fmt(x.Mean)} {unit}";
        int row = 1;
        foreach (var (m, c, e) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition());
            Cell(row, 0, new TextBlock { Text = m.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            Cell(row, 1, new TextBlock { Text = RealityLab.Describe(e, m), FontSize = 12, Foreground = Ui.Brush("Muted") });
            Cell(row, 2, new TextBlock { Text = S(c.Before, m.Unit), FontSize = 12.5 });
            Cell(row, 3, new TextBlock { Text = S(c.After, m.Unit), FontSize = 12.5 });
            Cell(row, 4, new TextBlock { Text = c.Pct is { } p ? $"{p:+0.0;-0.0}%" : "–", FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            var (vt, vtone) = c.Verdict switch { Verdict.Better => ("Better", "Success"), Verdict.Worse => ("Worse", "Danger"), Verdict.NoClearChange => ("Within noise", "Muted"), _ => ("Needs 2+ runs", "Muted") };
            var vtag = Ui.Tag(vt, vtone); vtag.ToolTip = c.Explanation;
            Cell(row, 5, vtag);
            var check = RealityLab.Check(e, c);
            Cell(row, 6, Ui.Tag(RealityLab.Describe(check), check switch { PredictionCheck.Matched => "Success", PredictionCheck.Opposite or PredictionCheck.Unexpected => "Gold", _ => "Muted" }));
            row++;
        }
        sp.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        foreach (var w in BenchmarkStats.ConditionWarnings(off, on))
            sp.Children.Add(new TextBlock { Text = "⚠ " + w, Foreground = Ui.Brush("Gold"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var hz = StressScene.RefreshRate();
        if (_withScene && hz > 0 && runs.All(r => r.Frames is { } f && f.AvgFps >= hz * 0.97))
            sp.Children.Add(new TextBlock { Text = $"⚠ Every run reached your display's {hz} Hz cap, so FPS can't show a difference here.", Foreground = Ui.Brush("Gold"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        sp.Children.Add(Ui.Muted("“Better/Worse” only when the difference is larger than the variation between repeated runs. Resource use was measured with the PC otherwise idle" +
            (_withScene ? "; FPS comes from a synthetic 3D scene, and games can react differently." : ".") + " Saved in Test & Benchmark › History.", 11.5));
        ((TextBlock)sp.Children[^1]).TextWrapping = TextWrapping.Wrap;

        // Inspect + keep.
        var actions = Ui.Row(
            Ui.Btn("Show without in 3D", (_, _) => { _view = "before"; ShowView(); Ui.ScrollTo(_sceneHost); }, margin: new Thickness(0, 14, 8, 0)),
            Ui.Btn("Show with in 3D", (_, _) => { _view = "after"; ShowView(); Ui.ScrollTo(_sceneHost); }, margin: new Thickness(0, 14, 8, 0)));
        var toKeep = original.Where(kv => !kv.Value).Select(kv => TweakCatalog.Find(kv.Key)).OfType<Tweak>().ToList();
        if (toKeep.Count > 0)
            actions.Children.Insert(0, Ui.Btn($"Keep these changes ({toKeep.Count})…", async (_, _) => await KeepAsync(choice, toKeep, runs[0].StudyId), "PrimaryButton", new Thickness(0, 14, 8, 0)));
        actions.Children.Add(Ui.Btn("Open benchmark history", (_, _) => MainWindow.Current?.Navigate("benchmark"), "GhostButton", new Thickness(0, 14, 8, 0)));
        sp.Children.Add(actions);
        sp.Children.Add(Ui.Muted(toKeep.Count > 0 ? "Your settings are back exactly as before the experiment. Keep switches the tested tweaks on for good, with a backup of each, and can be rolled back below."
            : "Every tested tweak was already on before the experiment, so there is nothing new to keep.", 11.5));
        ((TextBlock)sp.Children[^1]).TextWrapping = TextWrapping.Wrap;

        _resultCard.Child = Ui.Card(sp);
        _resultCard.Visibility = Visibility.Visible;
        Ui.ScrollTo(_resultCard);
    }

    // ---- keep / roll back -----------------------------------------------------------------------------------------

    private async Task KeepAsync(Choice choice, List<Tweak> tweaks, string studyId)
    {
        if (_busy || _measuring) return;
        if (!Dialog.Confirm($"Keep “{choice.Name}”?",
                "Switches these on and leaves them on:\n" + string.Join("\n", tweaks.Select(t => "• " + t.Name)) +
                "\n\nA restore point is created first (admin, System Protection on), each tweak saves a backup of your current values, and Roll back in Reality Lab reverts exactly these.",
                "Keep changes", DialogKind.Confirm)) return;
        _busy = true;
        try
        {
            var items = tweaks.Select(t => new TweakItem(t)).ToList();
            await TweakService.RefreshStatesAsync(items);
            var before = items.Where(i => i.State != TweakState.Applied).Select(i => i.Tweak.Id).ToHashSet();
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, true);
            var applied = items.Where(i => i.State == TweakState.Applied && before.Contains(i.Tweak.Id)).Select(i => i.Tweak.Id).ToList();
            if (applied.Count > 0)
            {
                var f = RealityLab.Load();
                f.Kept.Add(new LabRecord { ExperimentName = choice.Name, StudyId = studyId, AppliedIds = applied });
                RealityLab.Save(f);
            }
            Log.Info($"Reality Lab kept '{choice.Name}': {ok} applied, {failed} failed");
            MainWindow.Current?.ShowToast($"{choice.Name}: {ok} kept{(failed > 0 ? $", {failed} failed (see the Activity Log)" : "")}.{(reboot ? " Restart Windows to finish." : "")}", failed > 0 ? "Gold" : "Success");
            RenderKept();
            RefreshPreview();
        }
        catch (Exception ex) { Log.Error("Reality Lab keep failed", ex); Dialog.Show("Couldn't keep the changes", ex.Message, DialogKind.Error); }
        finally { _busy = false; }
    }

    private void RenderKept()
    {
        var f = RealityLab.Load();
        if (f.Kept.Count == 0) { _keptCard.Visibility = Visibility.Collapsed; return; }
        var sp = Ui.Section("Kept experiments", "Tweaks you kept after an experiment. Roll back reverts exactly those, restoring the values backed up when you kept them.");
        foreach (var k in f.Kept.OrderByDescending(k => k.KeptAt))
        {
            var names = k.AppliedIds.Select(id => TweakCatalog.Find(id)?.Name ?? id);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var btn = Ui.Btn("Roll back", async (_, _) => await RollBackAsync(k), margin: new Thickness(12, 0, 0, 0));
            btn.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            var text = new StackPanel();
            text.Children.Add(Ui.Text($"{k.ExperimentName} · kept {k.KeptAt:g}", 13, FontWeights.SemiBold));
            var list = Ui.Muted(string.Join(", ", names), 12); list.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(list);
            row.Children.Add(text);
            sp.Children.Add(row);
        }
        _keptCard.Child = Ui.Card(sp);
        _keptCard.Visibility = Visibility.Visible;
    }

    private async Task RollBackAsync(LabRecord k)
    {
        if (_busy || _measuring) return;
        if (!Dialog.Confirm($"Roll back “{k.ExperimentName}”?", $"Reverts the {k.AppliedIds.Count} tweak(s) kept on {k.KeptAt:g}, restoring their backed-up original values. Nothing else is touched.", "Roll back", DialogKind.Warning)) return;
        _busy = true;
        try
        {
            var items = k.AppliedIds.Select(TweakCatalog.Find).OfType<Tweak>().Select(t => new TweakItem(t)).ToList();
            await TweakService.RefreshStatesAsync(items);
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, false);
            if (failed == 0)
            {
                var f = RealityLab.Load();
                f.Kept.RemoveAll(x => x.Id == k.Id);
                RealityLab.Save(f);
            }
            Log.Info($"Reality Lab rolled back '{k.ExperimentName}': {ok} reverted, {failed} failed");
            MainWindow.Current?.ShowToast($"{k.ExperimentName}: {ok} rolled back{(failed > 0 ? $", {failed} failed (kept in the list so you can retry)" : "")}.{(reboot ? " Restart Windows to finish." : "")}", failed > 0 ? "Gold" : "Success");
            RenderKept();
            RefreshPreview();
        }
        catch (Exception ex) { Log.Error("Reality Lab roll back failed", ex); Dialog.Show("Couldn't roll back", ex.Message, DialogKind.Error); }
        finally { _busy = false; }
    }

    /// <summary>Called at startup: if an experiment was interrupted, offer to put its tweaks back.</summary>
    public static async Task CheckPendingAsync()
    {
        var pending = TweakStateGuard.LoadPending(RealityLab.PendingPath);
        if (pending is null || pending.Count == 0) { TweakStateGuard.ClearPending(RealityLab.PendingPath); return; }
        if (!Dialog.Confirm("Restore settings from an interrupted Reality Lab experiment?",
                $"An experiment was interrupted before it could put {pending.Count} tweak(s) back. Restore them to the state they were in before the experiment?", "Restore", DialogKind.Warning)) return;
        var tweaks = pending.Keys.Select(TweakCatalog.Find).OfType<Tweak>().ToList();
        if (!TweakService.EnsureAdmin(tweaks)) return;
        var problems = await Task.Run(() => TweakStateGuard.Restore(pending));
        if (problems.Count == 0) { TweakStateGuard.ClearPending(RealityLab.PendingPath); MainWindow.Current?.ShowToast("Settings from the interrupted experiment were restored and verified.", "Success"); }
        else Dialog.Show("Some settings were not restored", string.Join("\n", problems), DialogKind.Error);
    }
}
