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
    private string? _synthMode; // "without", "with", "compare"
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
        foreach (var (k, label) in new[] { ("without", "Without tweaks"), ("with", "With tweaks"), ("compare", "Compare both") })
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

        string[] plan = _synthMode switch
        {
            "without" => Enumerable.Repeat("off", _synthRuns).ToArray(),
            "with" => Enumerable.Repeat("on", _synthRuns).ToArray(),
            _ => Enumerable.Range(0, _synthRuns * 2).Select(i => i % 2 == 0 ? "off" : "on").ToArray(), // alternate A/B so drift hits both equally
        };
        var willChange = original.Where(kv => plan.Contains(kv.Value ? "off" : "on")).Select(kv => TweakCatalog.Find(kv.Key)!).ToList();
        var msg = $"Preset: {preset.Name}\nRuns: {string.Join(", ", plan.Select(p => p == "on" ? "with" : "without"))} · {_synthDuration} s each · intensity {_synthIntensity} · {_synthRes.w}×{_synthRes.h}\n\n" +
                  (willChange.Count == 0 ? "No Windows settings need to change." :
                   $"Temporarily changed ({willChange.Count}):\n" + string.Join("\n", willChange.Select(t => "• " + t.Name)) +
                   "\n\nEvery tweak is put back exactly as it is now when the test ends, is stopped, or fails. If Money Tweaks is closed mid-test, it offers the restore on next start.") +
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
        var runs = new List<(string state, SceneResult r)>();
        string? error = null;
        List<string> restoreProblems = [];
        try
        {
            for (int i = 0; i < plan.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                bool on = plan[i] == "on";
                _synthStatus.Text = $"Run {i + 1}/{plan.Length}: switching tweaks {(on ? "on" : "off")}…";
                var problems = await Task.Run(() => TweakStateGuard.SetAll(original.Keys, on), ct);
                if (problems.Count > 0) throw new InvalidOperationException("Couldn't set the tweak state, so the comparison would be invalid:\n" + string.Join("\n", problems));
                _synthStatus.Text = $"Run {i + 1}/{plan.Length} ({(on ? "with" : "without")} tweaks): settling 3 s…";
                await Task.Delay(3000, ct);
                _synthStatus.Text = $"Run {i + 1}/{plan.Length} ({(on ? "with" : "without")} tweaks): running. Esc or Emergency stop to cancel.";
                var r = await StressScene.RunAsync(new SceneOptions(_synthIntensity, _synthDuration, _synthRes.w, _synthRes.h,
                    $"Run {i + 1}/{plan.Length} · {(on ? "WITH" : "WITHOUT")} tweaks ({preset.Name})"), ct);
                if (!r.Completed) throw new OperationCanceledException(r.StopReason);
                runs.Add((plan[i], r));
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
        if (runs.Count > 0) SaveSynthetic(preset.Name, runs);
    }

    private void SaveSynthetic(string presetName, List<(string state, SceneResult r)> runs)
    {
        var scene = $"Synthetic 3D · intensity {_synthIntensity} · {_synthRes.w}×{_synthRes.h} · {_synthDuration} s · {runs[0].r.Objects} objects, {runs[0].r.Triangles:N0} triangles" + (runs[0].r.RefreshHz > 0 ? $" · display {runs[0].r.RefreshHz} Hz" : "");
        var s = new BenchmarkStudy
        {
            Name = $"Synthetic 3D: {presetName} ({DateTime.Now:g})", TargetKind = "synthetic", TargetName = presetName,
            DurationSec = _synthDuration, RunsPerPhase = _synthRuns, Endpoint = "", Scene = scene,
        };
        _file.Studies.Add(s);
        var saved = new List<BenchmarkRun>();
        foreach (var (state, r) in runs)
        {
            var run = new BenchmarkRun
            {
                StudyId = s.Id, Phase = state == "on" ? "After" : "Baseline", At = DateTime.Now,
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
        RenderSyntheticResult(s, saved);
    }

    private void RenderSyntheticResult(BenchmarkStudy s, List<BenchmarkRun> runs)
    {
        _synthResult.Children.Clear();
        var without = runs.Where(r => r.Phase == "Baseline").ToList();
        var with = runs.Where(r => r.Phase == "After").ToList();
        _synthResult.Children.Add(new TextBlock { Text = "RESULTS · SYNTHETIC", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 16, 0, 6) });
        _synthResult.Children.Add(Ui.Muted(s.Scene, 11.5));

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
        var found = new List<string>();
        foreach (var (_, game, exe) in TweakCatalog.GameTable)
        {
            var ps = System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exe));
            if (ps.Length > 0 && exe != "javaw.exe") found.Add(game);
            foreach (var p in ps) p.Dispose();
        }
        return found;
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
