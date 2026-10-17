using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>Synthetic 3D stress test: without tweaks / with tweaks / compare both, with guaranteed restore.</summary>
public sealed partial class BenchmarkView
{
    private readonly StackPanel _synth = new();
    private readonly StackPanel _synthResult = new();
    private readonly TextBlock _synthStatus = Ui.Muted("", 12.5);
    private Button? _synthStart, _synthStop;
    private CancellationTokenSource? _synthCts;
    private string? _synthMode; // "without", "with", "compare", "each"
    private sealed record Step(string? TweakId, bool On);
    private string _synthPreset = "builtin.competitive-fortnite";
    private int _synthIntensity = 3, _synthDuration = 30, _synthRuns = 2;
    private (int w, int h) _synthRes = (1280, 720);

    private UIElement BuildSynthetic()
    {
        _synth.Children.Clear();
        var head = new WrapPanel();
        head.Children.Add(Ui.Text("Synthetic 3D stress test", 15, FontWeights.SemiBold));
        head.Children.Add(new Border { Width = 8 });
        head.Children.Add(Ui.Tag("SYNTHETIC · NOT A FORTNITE BENCHMARK", "Gold"));
        _synth.Children.Add(head);
        _synth.Children.Add(new TextBlock
        {
            Text = "Renders the same seeded 3D scene every run (textured meshes, coloured lights, projected shadows, moving objects) and measures real frame times plus CPU/GPU/RAM/temperatures where Windows exposes them. " +
                   "It is a synthetic test: it shows how Windows settings affect this scene, and games like Fortnite can behave differently. WPF draws in sync with your display, so FPS can't go above its refresh rate; raise the intensity until it drops below.",
            Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });

        _synth.Children.Add(Label("What do you want to test?"));
        var modes = new WrapPanel();
        foreach (var (k, label) in new[] { ("without", "Without tweaks"), ("with", "With tweaks"), ("compare", "Compare both"), ("each", "Each tweak separately") })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "smode", IsChecked = _synthMode == k, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _synthMode = k; if (_synthStart is not null) _synthStart.IsEnabled = true; };
            modes.Children.Add(rb);
        }
        _synth.Children.Add(modes);
        _synth.Children.Add(Label("Which tweaks (a preset)"));
        _synth.Children.Add(ChipRow("spreset", PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).Select(p => (p.Name, p.Id)).ToArray(), _synthPreset, v => _synthPreset = v));
        _synth.Children.Add(Label("Intensity"));
        _synth.Children.Add(ChipRow("sint", [("1 Light", "1"), ("2 Medium", "2"), ("3 Heavy", "3"), ("4 Extreme", "4"), ("5 Insane", "5")], _synthIntensity.ToString(CultureInfo.InvariantCulture), v => _synthIntensity = int.Parse(v, CultureInfo.InvariantCulture)));
        _synth.Children.Add(Label("Duration per run (plus 3 s unmeasured warm-up)"));
        _synth.Children.Add(ChipRow("sdur", [("15 s", "15"), ("30 s", "30"), ("60 s", "60"), ("120 s", "120")], _synthDuration.ToString(CultureInfo.InvariantCulture), v => _synthDuration = int.Parse(v, CultureInfo.InvariantCulture)));
        _synth.Children.Add(Label("Resolution"));
        _synth.Children.Add(ChipRow("sres", [("1280×720", "1280x720"), ("1600×900", "1600x900"), ("1920×1080", "1920x1080")], $"{_synthRes.w}x{_synthRes.h}", v => { var p = v.Split('x'); _synthRes = (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)); }));
        _synth.Children.Add(Label("Runs per state (2+ to judge whether a difference is meaningful)"));
        _synth.Children.Add(ChipRow("sruns", [("1", "1"), ("2", "2"), ("3", "3")], _synthRuns.ToString(CultureInfo.InvariantCulture), v => _synthRuns = int.Parse(v, CultureInfo.InvariantCulture)));

        _synthStart = Ui.Btn("Start benchmark", async (_, _) => await RunSyntheticAsync(), "PrimaryButton", new Thickness(0, 14, 8, 0));
        _synthStart.IsEnabled = _synthMode is not null;
        _synthStop = Ui.Btn("■  Emergency stop", (_, _) => _synthCts?.Cancel(), "DangerButton", new Thickness(0, 14, 8, 0));
        _synthStop.IsEnabled = false;
        _synthStop.ToolTip = $"Also: Esc in the benchmark window, or {MacroService.EmergencyHotkeyText} anywhere";
        _synth.Children.Add(Ui.Row(_synthStart, _synthStop));
        _synth.Children.Add(_synthStatus);
        _synth.Children.Add(_synthResult);
        return Ui.Card(_synth);
    }

    private async Task RunSyntheticAsync()
    {
        if (_busy || _synthMode is null) { if (_synthMode is null) Dialog.Show("Choose what to test", "Pick “Without tweaks”, “With tweaks” or “Compare both” first."); return; }
        var preset = PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).FirstOrDefault(p => p.Id == _synthPreset);
        if (preset is null) { Dialog.Show("Preset not found", "Pick a preset."); return; }
        var all = preset.TweakIds.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => t!).ToList();
        var notEligible = all.Where(t => !TweakStateGuard.Eligible(t, out _)).Select(t => { TweakStateGuard.Eligible(t, out var why); return $"{t.Name} ({why})"; }).ToList();
        var eligible = all.Where(t => TweakStateGuard.Eligible(t, out _)).ToList();
        var skipped = new List<string>();
        _busy = true;
        _synthStatus.Text = "Reading current tweak states…";
        Dictionary<string, bool> original;
        try { original = await Task.Run(() => TweakStateGuard.Capture(eligible, skipped)); }
        finally { _busy = false; }
        skipped.AddRange(notEligible);

        // Counterbalanced A/B order (off,on,on,off…) so warm-up and thermal drift don't favour one state.
        var unmeasurable = original.Keys.Select(TweakCatalog.Find).Where(t => t is not null && SyntheticPlan.NotMeasurable(t) is not null)
            .Select(t => $"{t!.Name} ({SyntheticPlan.NotMeasurable(t)})").ToList();
        var plan = _synthMode switch
        {
            "without" => Enumerable.Repeat(new Step(null, false), _synthRuns).ToList(),
            "with" => Enumerable.Repeat(new Step(null, true), _synthRuns).ToList(),
            "each" => original.Keys.Where(id => SyntheticPlan.NotMeasurable(TweakCatalog.Find(id)!) is null)
                .SelectMany(id => SyntheticPlan.Abba(_synthRuns).Select(on => new Step(id, on))).ToList(),
            _ => SyntheticPlan.Abba(_synthRuns).Select(on => new Step(null, on)).ToList(),
        };
        if (plan.Count == 0)
        {
            Dialog.Show("Nothing this test can measure", "None of this preset's switchable tweaks can affect a windowed 3D scene:\n" + string.Join("\n", unmeasurable.Select(u => "• " + u)) +
                "\n\nThey may still matter in real games. Measure those in the game itself with a PresentMon/CapFrameX CSV (Before / after benchmark below).");
            return;
        }
        var willChange = original.Where(kv => plan.Any(p => (p.TweakId is null || p.TweakId == kv.Key) && p.On != kv.Value)).Select(kv => TweakCatalog.Find(kv.Key)!).ToList();
        var minutes = Math.Ceiling(plan.Count * (_synthDuration + StressScene.WarmupSec + 10) / 60.0 + 0.3);
        var msg = $"Preset: {preset.Name}\n{plan.Count} measured runs ({_synthDuration} s each, about {minutes:0} min) · intensity {_synthIntensity} · {_synthRes.w}×{_synthRes.h}\n" +
                  "Order is counterbalanced (off, on, on, off…) after a 10 s unmeasured priming run, and each run waits for background activity to settle.\n\n" +
                  (willChange.Count == 0 ? "No Windows settings need to change." :
                   $"Temporarily changed ({willChange.Count}):\n" + string.Join("\n", willChange.Select(t => "• " + t.Name)) +
                   "\n\nEvery tweak is put back exactly as it is now when the test ends, is stopped, or fails. If Money Tweaks is closed mid-test, it offers the restore on next start.") +
                  (unmeasurable.Count > 0 ? $"\n\nCan't affect this synthetic scene ({unmeasurable.Count}){(_synthMode == "each" ? ", not tested" : "")}: {string.Join(", ", unmeasurable)}" : "") +
                  (skipped.Count > 0 ? $"\n\nNot switched ({skipped.Count}): {string.Join(", ", skipped)}" : "") +
                  "\n\n“Without tweaks” only turns off the tweaks in this preset, not every Windows setting. Close other apps for consistent results.";
        var games = await Task.Run(RunningGames);
        if (games.Count > 0 && !Dialog.Confirm("A game is running", $"{string.Join(", ", games)} is running. It will compete with the benchmark for CPU and GPU, so the results won't be valid. Close it first for a fair test.", "Run anyway", DialogKind.Warning)) return;
        if (!Dialog.Confirm("Start synthetic benchmark?", msg, "Start", willChange.Count > 0 ? DialogKind.Warning : DialogKind.Confirm)) return;
        if (willChange.Count > 0 && !TweakService.EnsureAdmin(willChange)) return;

        _busy = true;
        _synthCts = new CancellationTokenSource();
        var ct = _synthCts.Token;
        void OnHotkey() => Dispatcher.BeginInvoke(() => _synthCts?.Cancel());
        MacroService.EmergencyStopPressed += OnHotkey;
        _synthStart!.IsEnabled = false; _synthStop!.IsEnabled = true;
        _synthResult.Children.Clear();
        if (willChange.Count > 0) TweakStateGuard.SavePending(original);
        var runs = new List<(Step step, DateTime at, SceneResult r)>();
        string? error = null;
        List<string> restoreProblems = [];
        try
        {
            _synthStatus.Text = "Priming run (10 s, not measured): warms up the GPU so no state gets the cold first run…";
            var prime = await StressScene.RunAsync(new SceneOptions(_synthIntensity, 10, _synthRes.w, _synthRes.h, "Priming run · not measured"), ct);
            if (!prime.Completed) throw new OperationCanceledException(prime.StopReason);
            for (int i = 0; i < plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = plan[i];
                var what = step.TweakId is null ? $"{(step.On ? "with" : "without")} tweaks" : $"{TweakCatalog.Find(step.TweakId)!.Name} {(step.On ? "ON" : "OFF")}";
                _synthStatus.Text = $"Run {i + 1}/{plan.Count}: switching ({what})…";
                List<string> ids = step.TweakId is null ? original.Keys.ToList() : [step.TweakId];
                var problems = await Task.Run(() => TweakStateGuard.SetAll(ids, step.On), ct);
                if (problems.Count > 0) throw new InvalidOperationException("Couldn't set the tweak state, so the comparison would be invalid:\n" + string.Join("\n", problems));
                var n = i;
                await SettleAsync(t => _synthStatus.Text = $"Run {n + 1}/{plan.Count} ({what}): {t}", ct);
                _synthStatus.Text = $"Run {i + 1}/{plan.Count} ({what}): running. Esc or Emergency stop to cancel.";
                var at = DateTime.Now;
                var r = await StressScene.RunAsync(new SceneOptions(_synthIntensity, _synthDuration, _synthRes.w, _synthRes.h, $"Run {i + 1}/{plan.Count} · {what.ToUpperInvariant()} ({preset.Name})"), ct);
                if (!r.Completed) throw new OperationCanceledException(r.StopReason);
                runs.Add((step, at, r));
            }
        }
        catch (OperationCanceledException) { error = "Stopped. Completed runs were kept."; }
        catch (Exception ex) { error = ex.Message; Log.Error("Synthetic benchmark failed", ex); }
        finally
        {
            MacroService.EmergencyStopPressed -= OnHotkey;
            if (willChange.Count > 0)
            {
                _synthStatus.Text = "Restoring your original settings…";
                restoreProblems = await Task.Run(() => TweakStateGuard.Restore(original));
                if (restoreProblems.Count == 0) TweakStateGuard.ClearPending();
            }
            _busy = false;
            _synthCts.Dispose(); _synthCts = null;
            _synthStart.IsEnabled = true; _synthStop.IsEnabled = false;
        }

        var restoreText = willChange.Count == 0 ? "No settings were changed."
            : restoreProblems.Count == 0 ? $"All {original.Count} tweak(s) restored to their original state and read back."
            : "Restore problems (see Backup & Restore / the tweak pages):\n" + string.Join("\n", restoreProblems);
        _synthStatus.Text = (error is null ? "Finished. " : error + " ") + restoreText;
        if (restoreProblems.Count > 0) Dialog.Show("Some settings were not restored", restoreText, DialogKind.Error);
        else if (error is not null && runs.Count == 0) Dialog.Show("Benchmark stopped", error + "\n\n" + restoreText, error.StartsWith("Stopped") ? DialogKind.Info : DialogKind.Error);
        if (runs.Count == 0) return;
        if (_synthMode != "each") { RenderSyntheticResult(SaveSynthetic(preset.Name, runs.Select(x => (x.step.On, x.at, x.r)).ToList())); return; }
        var perTweak = runs.GroupBy(x => x.step.TweakId!).Select(g => (TweakCatalog.Find(g.Key)!, SaveSynthetic($"{preset.Name} → {TweakCatalog.Find(g.Key)!.Name}", g.Select(x => (x.step.On, x.at, x.r)).ToList()))).ToList();
        RenderPerTweak(perTweak, unmeasurable);
    }

    private (BenchmarkStudy s, List<BenchmarkRun> runs) SaveSynthetic(string presetName, List<(bool on, DateTime at, SceneResult r)> runs)
    {
        var scene = $"Synthetic 3D · intensity {_synthIntensity} · {_synthRes.w}×{_synthRes.h} · {_synthDuration} s · {runs[0].r.Objects} objects, {runs[0].r.Triangles:N0} triangles" + (runs[0].r.RefreshHz > 0 ? $" · display {runs[0].r.RefreshHz} Hz" : "");
        var s = new BenchmarkStudy
        {
            Name = $"Synthetic 3D: {presetName} ({DateTime.Now:g})", TargetKind = "synthetic", TargetName = presetName,
            DurationSec = _synthDuration, RunsPerPhase = _synthRuns, Endpoint = "", Scene = scene,
        };
        _file.Studies.Add(s);
        var saved = new List<BenchmarkRun>();
        foreach (var (on, at, r) in runs)
        {
            var run = new BenchmarkRun
            {
                StudyId = s.Id, Phase = on ? "After" : "Baseline", At = at,
                Conditions = new TestConditions { DurationSec = _synthDuration, PowerPlan = BenchmarkSampler.ActivePowerPlan(), OnAcPower = BenchmarkSampler.OnAcPower(), OsBuild = OsInfo.Build, AppVersion = App.Version, Scene = scene },
                CpuAvg = r.CpuAvg, GpuAvg = r.GpuAvg, RamUsedGb = r.RamGb, CpuTempC = r.CpuTemp, GpuTempC = r.GpuTemp, FpsSeries = r.FpsPerSecond,
            };
            try { run.Frames = FrameTimeCsv.FromFrameTimes(r.FrameMs, $"Synthetic 3D scene (intensity {_synthIntensity}, {_synthRes.w}×{_synthRes.h})"); }
            catch (Exception ex) { Log.Warn("Synthetic run had too few frames: " + ex.Message); }
            _file.Runs.Add(run);
            saved.Add(run);
        }
        Save();
        RenderHistory();
        return (s, saved);
    }

    private static async Task SettleAsync(Action<string> status, CancellationToken ct)
    {
        // Switching tweaks starts processes (powercfg, PowerShell) and Windows reacts to setting changes. Wait at least 5 s,
        // then until total CPU stays under 15% for 2 s (max 15 s more), so that work doesn't land inside a measured run.
        status("settling 5 s…");
        await Task.Delay(5000, ct);
        using var hm = new HardwareMonitor();
        for (int i = 0, calm = 0; i < 15 && calm < 2; i++)
        {
            await Task.Delay(1000, ct);
            var cpu = await Task.Run(() => hm.Sample().CpuPercent, ct);
            calm = cpu < 15 ? calm + 1 : 0;
            status($"waiting for background activity to settle (CPU {cpu:0}%)…");
        }
    }

    private void RenderPerTweak(List<(Core.Model.Tweak t, (BenchmarkStudy s, List<BenchmarkRun> runs) res)> results, List<string> unmeasurable)
    {
        _synthResult.Children.Clear();
        _synthResult.Children.Add(new TextBlock { Text = "PER-TWEAK RESULTS · SYNTHETIC", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 16, 0, 6) });
        var g = new Grid();
        string[] cols = ["Tweak", "Avg FPS off → on", "1% low off → on", "Frame time", "Verdict in this scene"];
        foreach (var _ in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int r, int c, UIElement e) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 20, 3); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < cols.Length; c++) Cell(0, c, new TextBlock { Text = cols[c], FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Subtle") });
        int row = 1;
        foreach (var (t, (_, runs)) in results)
        {
            var off = runs.Where(r => r.Phase == "Baseline").ToList(); var on = runs.Where(r => r.Phase == "After").ToList();
            MetricComparison M(string k) => BenchmarkStats.Compare(BenchmarkStats.Metrics.First(m => m.Key == k), off, on);
            var fps = M("fps"); var low = M("low1"); var ft = M("ft");
            string P(MetricComparison c) => c.Before is null || c.After is null ? "–" : $"{BenchmarkStats.Fmt(c.Before.Mean)} → {BenchmarkStats.Fmt(c.After.Mean)}" + (c.Pct is { } p ? $" ({p:+0.0;-0.0}%)" : "");
            var verdicts = new[] { fps.Verdict, low.Verdict, ft.Verdict };
            var (txt, tone) = verdicts.Contains(Verdict.NotEnoughData) ? ("Needs 2+ runs each", "Muted")
                : verdicts.Contains(Verdict.Worse) && !verdicts.Contains(Verdict.Better) ? ("Hurts", "Danger")
                : verdicts.Contains(Verdict.Better) && !verdicts.Contains(Verdict.Worse) ? ("Helps", "Success")
                : verdicts.All(v => v == Verdict.NoClearChange) ? ("No measurable effect", "Muted") : ("Mixed", "Gold");
            g.RowDefinitions.Add(new RowDefinition());
            Cell(row, 0, new TextBlock { Text = t.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            Cell(row, 1, new TextBlock { Text = P(fps), FontSize = 12.5 });
            Cell(row, 2, new TextBlock { Text = P(low), FontSize = 12.5 });
            Cell(row, 3, new TextBlock { Text = P(ft), FontSize = 12.5 });
            var tag = Ui.Tag(txt, tone); tag.ToolTip = $"FPS: {fps.Explanation}\n1% low: {low.Explanation}\nFrame time: {ft.Explanation}";
            Cell(row, 4, tag);
            row++;
            Log.Info($"Synthetic per-tweak {t.Id}: {txt} (fps {P(fps)}, 1% {P(low)})");
        }
        _synthResult.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        if (unmeasurable.Count > 0)
            _synthResult.Children.Add(new TextBlock { Text = "Not testable with this scene (may still matter in games; test those in-game with a frame-time CSV):\n" + string.Join("\n", unmeasurable.Select(u => "• " + u)), Style = Ui.Res<Style>("MutedText"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        _synthResult.Children.Add(Ui.Muted("“Helps”/“Hurts” only when the change is larger than the variation between repeated runs. Synthetic scene only: games can react differently. Each tweak's runs are saved in History.", 11.5));
    }

    private void RenderSyntheticResult((BenchmarkStudy s, List<BenchmarkRun> runs) res)
    {
        var (s, runs) = res;
        _synthResult.Children.Clear();
        var without = runs.Where(r => r.Phase == "Baseline").ToList();
        var with = runs.Where(r => r.Phase == "After").ToList();
        _synthResult.Children.Add(new TextBlock { Text = "RESULTS · SYNTHETIC", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 16, 0, 6) });
        _synthResult.Children.Add(Ui.Muted(s.Scene, 11.5));
        if (without.Count > 0 && with.Count > 0)
        {
            var fpsC = BenchmarkStats.Compare(BenchmarkStats.Metrics.First(m => m.Key == "fps"), without, with);
            var head = fpsC.Verdict switch
            {
                Verdict.Better => $"Measurably faster with tweaks: {BenchmarkStats.Fmt(fpsC.Before!.Mean)} → {BenchmarkStats.Fmt(fpsC.After!.Mean)} FPS ({fpsC.Pct:+0.0;-0.0}%).",
                Verdict.Worse => $"Measurably slower with tweaks: {BenchmarkStats.Fmt(fpsC.Before!.Mean)} → {BenchmarkStats.Fmt(fpsC.After!.Mean)} FPS ({fpsC.Pct:+0.0;-0.0}%).",
                Verdict.NoClearChange => $"No meaningful difference: {BenchmarkStats.Fmt(fpsC.Before!.Mean)} vs {BenchmarkStats.Fmt(fpsC.After!.Mean)} FPS ({fpsC.Pct:+0.0;-0.0}%). {fpsC.Explanation}",
                _ => "Not conclusive: run at least 2 runs per state.",
            };
            _synthResult.Children.Add(new TextBlock { Text = head, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                Foreground = Ui.Brush(fpsC.Verdict == Verdict.Better ? "Success" : fpsC.Verdict == Verdict.Worse ? "Danger" : "Text") });
        }

        string[] keys = ["fps", "low1", "low01", "ft", "ft99", "cpu", "gpu", "ram", "cputemp", "gputemp"];
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        string[] cols = ["Metric", $"Without tweaks (n={without.Count})", $"With tweaks (n={with.Count})", "Difference", "Meaningful?"];
        foreach (var _ in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int r, int c, UIElement e) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 22, 3); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < cols.Length; c++) Cell(0, c, new TextBlock { Text = cols[c], FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Subtle") });
        string S(Summary? x, string unit) => x is null ? "–" : x.N > 1 ? $"{BenchmarkStats.Fmt(x.Mean)} ± {BenchmarkStats.Fmt(x.Sd)} {unit}" : $"{BenchmarkStats.Fmt(x.Mean)} {unit}";
        int row = 1;
        foreach (var m in BenchmarkStats.Metrics.Where(m => keys.Contains(m.Key)))
        {
            var c = BenchmarkStats.Compare(m, without, with);
            if (c.Before is null && c.After is null) continue;
            g.RowDefinitions.Add(new RowDefinition());
            Cell(row, 0, new TextBlock { Text = m.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            Cell(row, 1, new TextBlock { Text = S(c.Before, m.Unit), FontSize = 12.5 });
            Cell(row, 2, new TextBlock { Text = S(c.After, m.Unit), FontSize = 12.5 });
            Cell(row, 3, new TextBlock { Text = c.Pct is { } p ? $"{(p >= 0 ? "+" : "")}{p:0.0}%" : "–", FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            var (txt, tone) = c.Verdict switch
            {
                Verdict.Better => ("Yes, better", "Success"), Verdict.Worse => ("Yes, worse", "Danger"), Verdict.NoClearChange => ("No (within noise)", "Muted"), _ => ("Needs 2+ runs each", "Muted")
            };
            var tag = Ui.Tag(txt, tone); tag.ToolTip = c.Explanation;
            Cell(row, 4, tag);
            row++;
        }
        _synthResult.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var hz = StressScene.RefreshRate();
        if (hz > 0 && runs.All(r => r.Frames is { } f && f.AvgFps >= hz * 0.97))
            _synthResult.Children.Add(new TextBlock { Text = $"⚠ Every run reached your display's {hz} Hz cap, so FPS can't show a difference. Use a higher intensity.", Foreground = Ui.Brush("Gold"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        _synthResult.Children.Add(Ui.Muted("“Meaningful” means the difference is larger than the variation between repeated runs (2+ runs per state needed). This is a synthetic scene; Fortnite and other games may react differently to the same tweaks.", 11.5));
        _synthResult.Children.Add(FpsChart(runs));
    }

    private static UIElement FpsChart(List<BenchmarkRun> runs)
    {
        const double W = 640, H = 150;
        var canvas = new Canvas { Width = W, Height = H + 34, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var series = runs.Where(r => r.FpsSeries.Count > 1).ToList();
        if (series.Count == 0) return canvas;
        var max = series.Max(r => r.FpsSeries.Max()) * 1.1;
        var len = series.Max(r => r.FpsSeries.Count);
        canvas.Children.Add(new Rectangle { Width = W, Height = H, Fill = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), RadiusX = 8, RadiusY = 8 });
        foreach (var r in series)
        {
            var pl = new Polyline { Stroke = Ui.Brush(r.Phase == "Baseline" ? "Accent" : "Violet"), StrokeThickness = 1.8, Opacity = 0.9 };
            for (int i = 0; i < r.FpsSeries.Count; i++) pl.Points.Add(new Point(i * W / Math.Max(1, len - 1), H - r.FpsSeries[i] / max * (H - 8) - 4));
            canvas.Children.Add(pl);
        }
        var legend = new TextBlock { Text = $"FPS per second · blue = without tweaks · violet = with tweaks · top ≈ {max:0} FPS", FontSize = 11, Foreground = Ui.Brush("Subtle") };
        Canvas.SetTop(legend, H + 8);
        canvas.Children.Add(legend);
        return new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>Known games currently running (they would skew a synthetic benchmark).</summary>
    private static List<string> RunningGames()
    {
        // Same identity-checked detection as the Game Library (a generic exe like javaw.exe only counts when it is the game).
        var games = Core.Games.GameLibrary.Items(Core.Games.GameLibrary.Current);
        return Core.Games.GameRuntime.Statuses(games).Where(s => s.status == Core.Games.GameStatus.Running).Select(s => s.game.Def.Name).ToList();
    }

    /// <summary>Called at startup: if a synthetic benchmark was interrupted, offer to put the tweaks back.</summary>
    public static async Task CheckPendingSyntheticRestoreAsync()
    {
        var pending = TweakStateGuard.LoadPending();
        if (pending is null || pending.Count == 0) { TweakStateGuard.ClearPending(); return; }
        if (!Dialog.Confirm("Restore settings from an interrupted benchmark?",
                $"A synthetic benchmark was interrupted before it could put {pending.Count} tweak(s) back. Restore them to the state they were in before the test?", "Restore", DialogKind.Warning)) return;
        var tweaks = pending.Keys.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => t!).ToList();
        if (!TweakService.EnsureAdmin(tweaks)) return;
        var problems = await Task.Run(() => TweakStateGuard.Restore(pending));
        if (problems.Count == 0) { TweakStateGuard.ClearPending(); MainWindow.Current?.ShowToast("Settings from the interrupted benchmark were restored and verified.", "Success"); }
        else Dialog.Show("Some settings were not restored", string.Join("\n", problems), DialogKind.Error);
    }
}
