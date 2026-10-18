using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Genome;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

public sealed partial class GenomeView
{
    private string _cost = "gputemp";
    private readonly List<string> _compare = [];

    // ---- results: frontier, profiles, predictions, comparison ------------------------------------------------------------

    private void RenderResults()
    {
        _results.Children.Clear();
        if (_a is null) return;
        var recs = _a.Recommend(_store.Goal);
        var labels = new Dictionary<string, string>();
        foreach (var r in recs) labels[_a.Key(r.Config)] = r.Name;
        var rng = new Random(1);
        var predicted = _a.WorthTesting(_store.Goal, GenomePlanner.Candidates(_a.Factors, _a.Reference, rng), 4);

        // Frontier.
        var front = new StackPanel();
        front.Children.Add(Ui.Section("Performance frontier", "Every measured configuration: FPS change against a cost. The dashed line joins the ones nothing measured beats on both. Click a point to compare it."));
        var costs = new[] { ("GPU temperature", "gputemp"), ("GPU power", "gpupower"), ("CPU temperature", "cputemp"), ("CPU power", "cpupower") }
            .Where(c => _a.Evidence.Any(e => e.Measured.ContainsKey(c.Item2))).ToList();
        if (costs.Count > 0 && costs.All(c => c.Item2 != _cost)) _cost = costs[0].Item2;
        if (costs.Count > 1)
        {
            var axis = new SegmentedPicker(costs.Select(c => (c.Item1, c.Item2, (string?)null)), _cost);
            axis.Changed += v => { _cost = v; RenderResults(); };
            front.Children.Add(axis);
        }
        var metric = GenomeMetrics.Get(_cost);
        var chart = new FrontierChart { Margin = new Thickness(0, 10, 0, 0) };
        var pts = costs.Count == 0 ? [] : _a.Frontier(_cost, predicted.Select(p => (p.config, p.score)), labels);
        chart.Show(pts, "FPS vs. your settings (%)", $"{metric.Label} ({(metric.Kind == MetricKind.Ratio ? "%" : metric.Unit)})", _compare.FirstOrDefault());
        chart.PointClicked += p =>
        {
            if (!p.Measured) { Dialog.Show("Prediction, not a measurement", "This point is the model's prediction for an untested combination. Run more experiments; the planner tests promising predictions."); return; }
            if (_compare.Contains(p.Key)) _compare.Remove(p.Key); else { _compare.Insert(0, p.Key); if (_compare.Count > 3) _compare.RemoveAt(3); }
            RenderResults();
        };
        front.Children.Add(chart);
        front.Children.Add(Ui.Muted("● measured (size = runs; whiskers = 95% interval)   ○ predicted, untested (violet)   gold = your settings   blue = on the frontier", 11.5));
        if (costs.Count == 0) front.Children.Add(Ui.Muted("Costs appear once runs have temperature or power readings (GPU: nvidia-smi; CPU: MSI Afterburner or HWiNFO running).", 11.5));
        _results.Children.Add(Ui.Card(front));

        // Recommended profiles (measured only).
        var prof = new StackPanel();
        prof.Children.Add(Ui.Section("Recommended profiles", "Only configurations measured at least twice on this setup, never failed, and within the goal's limits. Each goal's best; identical winners are merged."));
        if (recs.Count == 0)
            prof.Children.Add(Ui.Muted(_a.Evidence.Any(e => e.N > 0) ? "Not enough repeated measurements yet. Keep experimenting; the planner repeats promising candidates." : "No measurements yet. Start an experiment above.", 12.5));
        var grid = Ui.Grid(2);
        foreach (var r in recs) grid.Children.Add(Ui.Card(ProfileCard(r), new Thickness(0, 0, 10, 10)));
        prof.Children.Add(grid);
        if (predicted.Count > 0)
        {
            prof.Children.Add(Ui.Text("Worth testing next (predictions, NOT measured)", 13, FontWeights.SemiBold));
            foreach (var (config, score) in predicted)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                row.Children.Add(Ui.Tag("PREDICTED", "Violet"));
                row.Children.Add(Ui.Text($"{GenomeAnalysis.Describe(_store, config, _a.Reference)}: {score.Mean:+0.0;−0.0} points (95% {score.Low:+0.0;−0.0} to {score.Mean + score.Ci95:+0.0;−0.0})", 12.5));
                row.ToolTip = "From the model fitted to this PC's measurements. Untested combinations can behave differently; the planner tests these next.";
                prof.Children.Add(row);
            }
        }
        _results.Children.Add(Ui.Card(prof));

        // Comparison.
        var cmp = new StackPanel();
        cmp.Children.Add(Ui.Section("Compare measured results", "Change against your settings, per metric, with 95% intervals."));
        if (_compare.Count == 0) _compare.AddRange(recs.Where(r => r.Evidence is { IsReference: false }).Take(2).Select(r => _a.Key(r.Config)));
        var items = _compare.Select(k => _a.Evidence.FirstOrDefault(e => e.Key == k)).Where(e => e is { N: > 0 })
            .Select(e => (labels.TryGetValue(e!.Key, out var n) ? n : GenomeAnalysis.Describe(_store, e.Config, _a.Reference), e)).ToList();
        var chartCmp = new CompareChart();
        chartCmp.Show(items);
        cmp.Children.Add(chartCmp);
        _results.Children.Add(Ui.Card(cmp));
    }

    private UIElement ProfileCard(Recommendation r)
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Text(r.Name, 14.5, FontWeights.SemiBold));
        head.Children.Add(new Border { Width = 8 });
        head.Children.Add(Ui.Tag(r.Confidence + " confidence", r.Confidence == "High" ? "Success" : r.Confidence == "Medium" ? "Accent" : "Gold"));
        head.Children.Add(Ui.Tag("MEASURED", "Success"));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Text(GenomeAnalysis.Describe(_store, r.Config, _a!.Reference), 12.5, FontWeights.SemiBold));
        sp.Children.Add(Ui.Muted(r.Verdict, 12));
        if (r.Evidence is { } e)
        {
            sp.Children.Add(Ui.Muted($"{e.N} measured run(s) · stable in {e.Ok.Count}/{e.Ok.Count + e.Failed.Count} · goal score {r.Score.Mean:+0.0;−0.0} ± {r.Score.Ci95:0.0}" +
                (r.Score.MissingMetrics.Count > 0 ? $" · not measured: {string.Join(", ", r.Score.MissingMetrics)}" : ""), 11.5));
            foreach (var m in GenomeMetrics.All)
            {
                if (!e.Measured.TryGetValue(m.Key, out var s)) continue;
                bool clear = s.Low > 0 || s.High < 0;
                var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0), Foreground = Ui.Brush(!clear ? "Muted" : m.Points(s.Mean) >= 0 ? "Success" : "Danger") };
                string lo = m.Change(s.Low), hi = m.Change(s.High);
                line.Text = $"{m.Label}: {m.Change(s.Mean)}  (95% {lo} … {hi}){(clear ? "" : " · within noise")}";
                if (_a.BaselineAbs.TryGetValue(m.Key, out var b)) line.ToolTip = $"Your settings: {b:0.#} {m.Unit}. Source: {m.Source}.";
                sp.Children.Add(line);
            }
        }
        sp.Children.Add(new TextBlock { Text = "Why", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 8, 0, 0) });
        foreach (var l in r.Explanation) sp.Children.Add(Ui.Muted("• " + l, 11.5));
        var buttons = new List<UIElement>();
        if (r.Evidence is { IsReference: false })
            buttons.Add(Ui.Btn("Apply…", async (_, _) => await ApplyProfileAsync(r), "PrimaryButton", new Thickness(0, 10, 8, 0)));
        buttons.Add(Ui.Btn("Compare", (_, _) => { var k = _a.Key(r.Config); _compare.Remove(k); _compare.Insert(0, k); if (_compare.Count > 3) _compare.RemoveAt(3); RenderResults(); }, "GhostButton", new Thickness(0, 10, 8, 0)));
        sp.Children.Add(Ui.Row(buttons.ToArray()));
        return sp;
    }

    // ---- apply / rollback --------------------------------------------------------------------------------------------------

    private async Task ApplyProfileAsync(Recommendation r)
    {
        var tuning = GenomeSystem.ToTuning(_store, r.Config, _now);
        var tweaks = GenomeSystem.TweakChanges(_store, r.Config, _now);
        var manual = _store.Factors.Where(f => f.IsManual && r.Config.TryGetValue(f.Id, out var v) && v != f.Levels[0].Id).ToList();
        if (tuning.IsEmpty && tweaks.Count == 0 && manual.Count == 0) { MainWindow.Current?.ShowToast("Already your current settings."); return; }
        bool gpu = tuning.GpuPowerLimitW is not null || tuning.GpuClockLock is not null;
        if (gpu && !Admin.IsElevated)
        {
            if (Dialog.Confirm("Administrator rights needed", "GPU settings can only be changed by an administrator. Restart Money Tweaks as administrator? Nothing has been changed.", "Restart as admin", DialogKind.Warning) && Admin.RestartElevated())
                Application.Current.Shutdown();
            return;
        }
        var lines = _store.Factors.Where(f => r.Config.TryGetValue(f.Id, out var v) && v != Reference().GetValueOrDefault(f.Id))
            .Select(f => $"• {f.Name}: {f.LabelOf(Reference().GetValueOrDefault(f.Id, ""))} → {f.LabelOf(r.Config[f.Id])}{(f.IsManual ? " (you set this in the game)" : "")}");
        if (!Dialog.Confirm($"Apply \"{r.Name}\"?", string.Join("\n", lines) +
                $"\n\nMeasured on this PC: {r.Evidence?.N} runs, {r.Confidence.ToLowerInvariant()} confidence. {r.Verdict}\n\n" +
                "CPU/GPU settings go through the CPU & GPU Tuning path (originals backed up, every value read back, \"Undo all tuning\" reverts them; GPU limits reset when Windows restarts). " +
                "Tweaks go through the tweak engine with backups. Measured results describe this workload; your games may differ.", "Apply", DialogKind.Warning)) return;
        var log = new List<string>();
        bool ok = true;
        var previous = new Dictionary<string, bool>();
        await Task.Run(() =>
        {
            if (!tuning.IsEmpty)
            {
                var (tok, results) = TuningEngine.Apply(tuning, TuningState.Load(), "Genome: " + r.Name);
                ok &= tok;
                log.AddRange(results.Select(x => $"{x.Status}: {x.Item} {x.Value}"));
            }
            if (tweaks.Count > 0)
            {
                var list = tweaks.Keys.Select(Core.Catalog.TweakCatalog.Find).Where(t => t is not null).Select(t => t!).ToList();
                foreach (var kv in TweakStateGuard.Capture(list)) previous[kv.Key] = kv.Value;
                var problems = TweakStateGuard.SetAll(tweaks.Where(t => t.Value).Select(t => t.Key), true).Concat(TweakStateGuard.SetAll(tweaks.Where(t => !t.Value).Select(t => t.Key), false)).ToList();
                ok &= problems.Count == 0;
                log.AddRange(problems.Count == 0 ? tweaks.Select(t => $"Verified: {Core.Catalog.TweakCatalog.Find(t.Key)?.Name} {(t.Value ? "on" : "off")}") : problems);
            }
        });
        foreach (var f in manual) log.Add($"Set in the game yourself: {f.Name} → {f.LabelOf(r.Config[f.Id])}");
        if (ok)
        {
            _store.Applied = new AppliedGenomeProfile { Name = r.Name, Config = new(r.Config), Fingerprint = _fp ?? new(), PreviousTweaks = previous, WorkloadId = _a!.Workload.Id };
            _store.Note($"Applied profile \"{r.Name}\": {GenomeAnalysis.Describe(_store, r.Config, Reference())}.");
            _store.Save();
        }
        Dialog.Show(ok ? "Profile applied" : "Not fully applied", string.Join("\n", log) + (ok ? "\n\nRollback is in History below (and Undo all tuning on CPU & GPU Tuning)." : ""), ok ? DialogKind.Info : DialogKind.Warning);
        await LoadAsync();
    }

    private async Task RollbackAsync()
    {
        if (_store.Applied is not { } ap) return;
        var ts = TuningState.Load();
        bool gpu = ts.GpuClocksLocked || ts.LastApply.Any(x => x.Item.StartsWith("GPU", StringComparison.Ordinal));
        if (gpu && !Admin.IsElevated) { Dialog.Show("Administrator rights needed", "Undoing GPU settings needs administrator rights (restarting Windows also resets them)."); return; }
        if (!Dialog.Confirm("Roll back the profile?", $"Restores the CPU/GPU values from before Money Tweaks' first tuning change (the same as Undo all tuning) and puts {ap.PreviousTweaks.Count} tweak(s) back as they were before \"{ap.Name}\".", "Roll back", DialogKind.Warning)) return;
        var (ok, log) = await Task.Run(() => TuningEngine.RevertAll(TuningState.Load()));
        var problems = await Task.Run(() => TweakStateGuard.Restore(ap.PreviousTweaks));
        bool done = ok && problems.Count == 0;
        if (done) { _store.Note($"Rolled back \"{ap.Name}\"."); _store.Applied = null; _store.Save(); }
        Dialog.Show(done ? "Rolled back" : "Rollback incomplete", string.Join("\n", log.Concat(problems)) is { Length: > 0 } t ? t : "Everything was already at its original value.", done ? DialogKind.Info : DialogKind.Warning);
        await LoadAsync();
    }

    // ---- history ----------------------------------------------------------------------------------------------------------

    private void RenderHistory()
    {
        _history.Children.Clear();
        _history.Children.Add(Ui.Section("History", "Every tested configuration is kept, including failures and results from older drivers."));
        if (_store.Applied is { } ap)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Ui.Tag("APPLIED", "Accent"));
            row.Children.Add(Ui.Text($"{ap.Name} ({ap.At:g}): {GenomeAnalysis.Describe(_store, ap.Config)}", 12.5));
            row.Children.Add(Ui.Btn("Roll back", async (_, _) => await RollbackAsync(), "GhostButton", new Thickness(8, 0, 0, 0)));
            _history.Children.Add(row);
        }
        var fails = _store.Failures.Where(f => _a is null || f.WorkloadId == _a.Workload.Id).ToList();
        if (fails.Count > 0)
        {
            _history.Children.Add(Ui.Text("Learned from failures (never tried again)", 12.5, FontWeights.SemiBold));
            foreach (var f in fails.TakeLast(10))
            {
                var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(Ui.Tag("BLOCKED", "Danger"));
                row.Children.Add(Ui.Text($"{GenomeAnalysis.Describe(_store, f.Levels)}: {f.Reason} ({f.At:g})", 12));
                row.Children.Add(Ui.Btn("Allow again", (_, _) => { _store.Failures.Remove(f); _store.Note("Unblocked " + GenomeAnalysis.Describe(_store, f.Levels)); _store.Save(); RenderHistory(); }, "GhostButton", new Thickness(8, 0, 0, 0)));
                _history.Children.Add(row);
            }
        }
        var trials = _store.Trials.Where(t => _a is null || t.WorkloadId == _a.Workload.Id).ToList();
        if (trials.Count == 0) { _history.Children.Add(Ui.Muted("No runs yet.", 12)); return; }
        _history.Children.Add(Ui.Text($"Runs ({trials.Count}, newest first)", 12.5, FontWeights.SemiBold));
        var table = new StackPanel();
        foreach (var t in Enumerable.Reverse(trials).Take(40))
        {
            bool stale = _fp is not null && !t.Fingerprint.SameAs(_fp);
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(Ui.Tag(t.Status == TrialStatus.Ok ? (t.IsBaseline ? "BASELINE" : "OK") : "FAILED", t.Status == TrialStatus.Ok ? (t.IsBaseline ? "Muted" : "Success") : "Danger"));
            if (stale) row.Children.Add(Ui.Tag("STALE", "Gold"));
            row.Children.Add(new TextBlock
            {
                Text = $"{t.At:g} · {GenomeAnalysis.Describe(_store, t.Config, _a?.Reference)} · {(t.Status == TrialStatus.Ok ? GenomeRunner.Summary(t) : t.Problem)}",
                FontSize = 12, Foreground = Ui.Brush(stale ? "Subtle" : "Text"), TextWrapping = TextWrapping.Wrap, MaxWidth = 900,
                ToolTip = $"Why: {t.Reason}\nCPU before: {t.IdleCpuBefore:0}% · {t.ProcessCount} processes · {(t.OnAc == false ? "on battery" : "mains")}\nDriver {t.Fingerprint.GpuDriver} · Windows {t.Fingerprint.OsBuild}" +
                          (t.Measured is { } m ? $"\n{m.Frames} frames{(m.FpsCapped ? " · at the refresh-rate cap (frame metrics left out)" : "")}" : ""),
            });
            table.Children.Add(row);
        }
        _history.Children.Add(table);
        var sessions = _store.Sessions.Where(s => _a is null || s.WorkloadId == _a.Workload.Id).TakeLast(5).Reverse().ToList();
        if (sessions.Count > 0)
        {
            _history.Children.Add(Ui.Text("Sessions", 12.5, FontWeights.SemiBold));
            foreach (var s in sessions) _history.Children.Add(Ui.Muted($"{s.Started:g}: {s.Outcome}", 11.5));
        }
        if (_store.Journal.Count > 0)
        {
            _history.Children.Add(Ui.Text("Journal", 12.5, FontWeights.SemiBold));
            foreach (var j in Enumerable.Reverse(_store.Journal).Take(8)) _history.Children.Add(Ui.Muted(j, 11.5));
        }
    }

    // ---- startup: crash recovery and update notice ------------------------------------------------------------------------

    /// <summary>Called at startup: if a session was interrupted (crash, power loss), offers to put the settings back.</summary>
    public static async Task CheckPendingAsync()
    {
        var pending = GenomeSystem.LoadPending();
        if (pending is null) return;
        var store = GenomeStore.Load();
        if (!Dialog.Confirm("Restore settings from an interrupted experiment?",
                "A Performance Genome session didn't finish (Money Tweaks or Windows closed during it). Put the CPU/GPU/Windows settings back the way they were before it started?" +
                (store.Factors.Any(f => f.IsManual) ? " Game settings you changed by hand need to be put back by you." : ""), "Restore", DialogKind.Warning)) return;
        var problems = await Task.Run(() => GenomeRunner.Recover(store, new GenomeSystem(), pending));
        store.Save();
        if (problems.Count == 0) MainWindow.Current?.ShowToast("Settings from the interrupted experiment were restored and read back.", "Success");
        else Dialog.Show("Some settings were not restored", string.Join("\n", problems) + (problems.Any(p => p.Contains("administrator")) ? "\n\nRestart Money Tweaks as administrator to restore GPU settings (restarting Windows also resets them)." : ""), DialogKind.Warning);
    }

    /// <summary>Called at startup (background): a toast when a driver/Windows/BIOS update makes earlier results stale.</summary>
    public static async Task CheckForUpdatesAsync()
    {
        var store = GenomeStore.Load();
        if (store.Trials.Count == 0 || store.LastSeen is null || store.ActiveWorkload is not { } w) return;
        var fp = await Task.Run(() => new GenomeSystem().Fingerprint(w));
        var changes = fp.ChangesSince(store.LastSeen);
        if (changes.Count == 0) return;
        store.Note("Detected " + string.Join(", ", changes) + ": earlier results are now marked stale; profiles need re-checking.");
        store.LastSeen = fp;
        store.Save();
        MainWindow.Current?.ShowToast($"Performance Genome: {string.Join(", ", changes)}. Your profiles need re-checking.", "Gold");
    }
}
