using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Evidence;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Lab;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Tweak Truth Index panel in a tweak's details: what was measured on this PC (from the benchmark history), the estimate,
/// and, only when asked, what other players shared. The three are labelled separately and never mixed; the measured part
/// works offline and never states a conclusion before there are enough valid tests.
/// </summary>
public static class EvidencePanel
{
    private static (DateTime stamp, Dictionary<string, TweakEvidenceReport> reports)? _cache;

    private static Tweak? FindByName(string name) => TweakCatalog.All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>All local evidence, rebuilt only when the benchmark history file changed.</summary>
    public static TweakEvidenceReport Local(string tweakId)
    {
        var stamp = File.Exists(BenchmarkStore.FilePath) ? File.GetLastWriteTimeUtc(BenchmarkStore.FilePath) : DateTime.MinValue;
        if (_cache is not { } c || c.stamp != stamp)
            _cache = c = (stamp, TweakEvidence.Build(BenchmarkStore.Load(), TweakCatalog.Find, FindByName));
        return c.reports.GetValueOrDefault(tweakId) ?? new(tweakId, [], [], []);
    }

    public static UIElement Build(Tweak t)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "TWEAK TRUTH INDEX", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 6) });
        var report = Local(t.Id);
        root.Children.Add(Section("Measured on this PC", "MEASURED", "Success", Measured(t, report)));
        root.Children.Add(Section("Expected benefit", "ESTIMATE · NOT MEASURED", "Gold", Estimate(t)));
        root.Children.Add(Section("Other players", "COMMUNITY-SUBMITTED · NOT VERIFIED", "Violet", Community(t, report)));
        return root;
    }

    private static Border Section(string title, string tag, string tone, UIElement body)
    {
        var sp = new StackPanel();
        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(Ui.Tag(tag, tone));
        sp.Children.Add(head);
        sp.Children.Add(body);
        return new Border { Child = sp, BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, 0, 8) };
    }

    private static TextBlock Note(string text, double size = 11.5, string brush = "Muted") =>
        new() { Text = text, FontSize = size, Foreground = Ui.Brush(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    // ---- measured ---------------------------------------------------------------------------------------------------

    private static string Pct(double? p) => p is { } v ? v.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "%" : "–";
    private static string Share(double v) => (v * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    private static UIElement Measured(Tweak t, TweakEvidenceReport r)
    {
        var sp = new StackPanel();
        if (r.ValidTrials == 0)
            sp.Children.Add(Note($"No valid measurement of this tweak on its own yet. A conclusion needs at least {TweakEvidence.MinTrials} tests, each with 2+ runs with and without it."));
        foreach (var g in r.Groups)
        {
            sp.Children.Add(new TextBlock
            {
                Text = $"{TweakEvidence.KindLabel(g.Kind)}{(g.Game is { } game ? " · " + game : "")} · {g.Trials.Count} test{(g.Trials.Count == 1 ? "" : "s")}, last {g.Trials[^1].At:d}",
                FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 4, 0, 2),
            });
            var grid = new Grid();
            string[] cols = ["Metric", "Tests", "Median change", "Spread", "Repeatable", "Regressions", "Confidence", "Conclusion"];
            for (int c = 0; c < cols.Length; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == cols.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            void Cell(int row, int col, UIElement e) { Grid.SetRow(e, row); Grid.SetColumn(e, col); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 14, 2); grid.Children.Add(e); }
            grid.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < cols.Length; c++) Cell(0, c, new TextBlock { Text = cols[c], FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Subtle") });
            int row = 1;
            foreach (var m in g.Metrics)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                bool enough = m.Level != EvidenceLevel.Insufficient;
                Cell(row, 0, new TextBlock { Text = m.Metric.Label, FontSize = 11.5, FontWeight = FontWeights.SemiBold });
                Cell(row, 1, new TextBlock { Text = $"{m.Trials}", FontSize = 11.5 });
                // Before there's enough evidence, show no effect sizes at all, only how many tests are still needed.
                Cell(row, 2, new TextBlock { Text = enough ? Pct(m.MedianPct) : "–", FontSize = 11.5 });
                Cell(row, 3, new TextBlock { Text = enough ? "±" + Pct(m.SpreadPct).TrimStart('+') : "–", FontSize = 11.5, ToolTip = "Median absolute deviation between tests" });
                Cell(row, 4, new TextBlock { Text = enough ? Share(m.Repeatability) : "–", FontSize = 11.5, ToolTip = "Share of tests that reached the same verdict" });
                Cell(row, 5, new TextBlock { Text = enough ? Share(m.RegressionRate) : "–", FontSize = 11.5, ToolTip = "Share of tests where it measurably got worse" });
                var (level, tone) = m.Level switch
                {
                    EvidenceLevel.High => ("High", "Success"), EvidenceLevel.Medium => ("Medium", "Accent"), EvidenceLevel.Low => ("Low", "Gold"),
                    EvidenceLevel.Inconsistent => ("Inconsistent", "Gold"), _ => ("Not enough data", "Muted"),
                };
                Cell(row, 6, Ui.Tag(level, tone));
                Cell(row, 7, new TextBlock
                {
                    Text = m.Conclusion ?? $"Needs {TweakEvidence.MinTrials - m.Trials} more test{(TweakEvidence.MinTrials - m.Trials == 1 ? "" : "s")}.",
                    FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = Ui.Brush(m.Consensus == Verdict.Better && enough ? "Success" : m.Consensus == Verdict.Worse && enough ? "Danger" : "Muted"),
                });
                row++;
            }
            sp.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        if (r.Excluded.Count > 0)
            sp.Children.Add(Note($"Not counted ({r.Excluded.Count}): " + string.Join("; ", r.Excluded.Take(3).Select(x => $"{x.At:d} {string.Join(", ", x.Issues)}")) + (r.Excluded.Count > 3 ? "; …" : "") + "."));
        if (r.CombinedTests.Count > 0)
            sp.Children.Add(Note($"Also part of {r.CombinedTests.Count} test{(r.CombinedTests.Count == 1 ? "" : "s")} that changed several tweaks at once; those can't be attributed to this one, so they aren't counted."));
        sp.Children.Add(Note("Results of different test types are never pooled. Medians are used, so one unusual test can't swing the result; “Spread” and “Repeatable” show how much tests agree.", 11));

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var (usable, _) = RealityLab.Usable([t.Id]);
        if (usable.Count == 1)
            actions.Children.Add(Ui.Btn("Measure in Reality Lab", (_, _) => Views.RealityLabView.Measure(t.Id), "BaseButton", new Thickness(0, 0, 8, 0)));
        var own = Ui.Btn("Own before/after test", (_, _) => BenchmarkView.Open("tweak", t.Id), "GhostButton", new Thickness(0, 0, 8, 0));
        own.ToolTip = "Test & Benchmark: measure (e.g. in your game with a frame-time file), apply, measure again";
        actions.Children.Add(own);
        sp.Children.Add(actions);
        return sp;
    }

    // ---- estimate ---------------------------------------------------------------------------------------------------

    private static UIElement Estimate(Tweak t)
    {
        // The benefit text itself is already shown above in the details; this only labels what kind of statement it is.
        var f = TweakInfo.For(t);
        return Note($"“Expected benefit” above is an estimate (confidence: {f.Confidence}), written from how the setting works. It is not a measurement and is never mixed into the measured results.");
    }

    // ---- community --------------------------------------------------------------------------------------------------

    private static UIElement Community(Tweak t, TweakEvidenceReport local)
    {
        var sp = new StackPanel();
        var results = new StackPanel();
        var load = Ui.Btn("Load community results", async (s, _) =>
        {
            ((Button)s).IsEnabled = false;
            results.Children.Clear();
            results.Children.Add(Note("Loading…"));
            await LoadCommunityAsync(t, results);
            ((Button)s).IsEnabled = true;
        }, "BaseButton", new Thickness(0, 0, 8, 0));
        load.ToolTip = "Asks the Community Hub for other players' shared results for this tweak (nothing is sent about you)";
        var row = new WrapPanel();
        row.Children.Add(load);
        row.Children.Add(ShareButton(t, local));
        sp.Children.Add(Note("Results other players chose to share. They're self-reported from different PCs and can't be checked here, so treat them as claims, not proof. Nothing is loaded until you ask."));
        sp.Children.Add(row);
        sp.Children.Add(results);
        return sp;
    }

    private static async Task LoadCommunityAsync(Tweak t, StackPanel into)
    {
        CommunityEvidence ev;
        try { ev = await CommunityClient.GetEvidenceAsync(t.Id); }
        catch (CommunityApiException ex)
        {
            into.Children.Clear();
            into.Children.Add(Note(ex.Code == "not_found" ? "This Community Hub server doesn't collect shared results."
                : ex.Status == 0 ? $"Couldn't reach the Community Hub ({ex.Message}). Your own measurements above work without it."
                : "Couldn't load community results: " + ex.Message, 11.5, "Gold"));
            return;
        }
        into.Children.Clear();
        if (ev.Groups.Count == 0) { into.Children.Add(Note("Nobody has shared a result for this tweak yet.")); return; }
        // This PC's own groups first, so the most relevant comparison is on top.
        var specs = await Task.Run(HardwareMonitor.ReadSpecs);
        string myGpu = EvidenceShare.PickGpu(specs.Gpus), myCpu = EvidenceShare.CleanModel(specs.Cpu, cpu: true), myOs = OsInfo.Build >= 22000 ? "Windows 11" : "Windows 10";
        foreach (var kind in ev.Groups.Select(g => g.Kind).Distinct())
        {
            var kindName = EvidenceShare.KindCodes.FirstOrDefault(kv => kv.Value == kind) is { Value: not null } k ? TweakEvidence.KindLabel(k.Key) : kind;
            into.Children.Add(new TextBlock { Text = kindName, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 8, 0, 2) });
            var groups = ev.Groups.Where(g => g.Kind == kind).ToList();
            var picked = groups.Where(g => g.Dimension == "all").Concat(groups.Where(g =>
                    (g.Dimension == "gpu" && g.Value == myGpu) || (g.Dimension == "cpu" && g.Value == myCpu) || (g.Dimension == "os" && g.Value == myOs)))
                .Concat(groups.Where(g => g.Dimension == "game").Take(3)).ToList();
            foreach (var g in picked)
            {
                var label = g.Dimension switch { "all" => "All PCs", "gpu" => $"Same GPU ({g.Value})", "cpu" => $"Same CPU ({g.Value})", "os" => g.Value, _ => $"In {g.Value}" };
                var line = g.Metrics.Count == 0 || g.Metrics.All(m => m.MedianPct is null)
                    ? $"{g.Submissions} shared test(s): not enough yet (numbers appear at {ev.MinSubmissions})."
                    : string.Join(" · ", g.Metrics.Where(m => m.MedianPct is not null).Select(m =>
                        $"{BenchmarkStats.Metrics.FirstOrDefault(x => x.Key == m.K)?.Label ?? m.K}: median {Pct(m.MedianPct)}, {Share(m.BetterShare ?? 0)} better / {Share(m.WorseShare ?? 0)} worse (n={m.N})"));
                var tb = Note($"{label}: {line}", 11.5, "Text");
                into.Children.Add(tb);
            }
        }
        into.Children.Add(Note("“Better/worse” is each sharer's own verdict (difference larger than their run-to-run variation).", 11));
    }

    private static UIElement ShareButton(Tweak t, TweakEvidenceReport local)
    {
        var shared = EvidenceShare.LoadShared();
        var unshared = local.Groups.SelectMany(g => g.Trials).Where(x => !shared.Contains(EvidenceShare.KeyFor(x.Study))).ToList();
        if (!App.Settings.ShareEvidence)
        {
            var b = Ui.Btn("Sharing is off ›", (_, _) => MainWindow.Current?.Navigate("about#evidence"), "GhostButton");
            b.ToolTip = "Your results stay on this PC. Turn sharing on in Settings › Result sharing.";
            return b;
        }
        if (unshared.Count == 0)
        {
            var none = Ui.Btn(local.ValidTrials == 0 ? "Nothing to share yet" : "All your results are shared", (_, _) => { }, "GhostButton");
            none.IsEnabled = false;
            return none;
        }
        return Ui.Btn($"Share my {unshared.Count} result{(unshared.Count == 1 ? "" : "s")}…", async (s, _) => await ShareAsync(t, unshared, (Button)s), "GhostButton");
    }

    private static async Task ShareAsync(Tweak t, List<Trial> trials, Button button)
    {
        if (!App.Settings.ShareEvidence) return;
        if (!CommunityClient.SignedIn)
        {
            if (Dialog.Confirm("Sign in to share", "Sharing needs a Community Hub sign-in, only so the Hub can limit how many results one account sends per day. Your account is never stored with the results.", "Open Community Hub"))
                MainWindow.Current?.Navigate("community");
            return;
        }
        button.IsEnabled = false;
        try
        {
            var specs = await Task.Run(HardwareMonitor.ReadSpecs);
            var subs = trials.Select(x => EvidenceShare.Build(x, specs, OsInfo.Build, App.Version)).OfType<EvidenceSubmission>().ToList();
            if (subs.Count == 0) { Dialog.Show("Nothing to share", "None of these tests has a shareable result."); return; }
            var preview = JsonSerializer.Serialize(subs, new JsonSerializerOptions(MoneyTweaks.Community.Api.Json) { WriteIndented = true });
            if (!Dialog.Confirm($"Share {subs.Count} result(s) for “{t.Name}”?",
                    "Exactly this is sent, nothing else (no account, name, serial number, file path or time):\n\n" + preview, "Share")) return;
            int ok = 0, dup = 0;
            var done = new List<string>();
            foreach (var s in subs)
            {
                var r = await CommunityClient.ShareEvidenceAsync(s);
                if (r.Accepted) ok++; else if (r.Duplicate) dup++;
                done.Add(s.Key);
            }
            EvidenceShare.MarkShared(done);
            Log.Info($"Tweak Truth Index: shared {ok} result(s) for {t.Id} ({dup} already shared)");
            MainWindow.Current?.ShowToast($"Shared {ok} result(s){(dup > 0 ? $", {dup} were already shared" : "")}. Thank you!", "Success");
            button.Content = "All your results are shared";
        }
        catch (CommunityApiException ex) { Dialog.Show("Couldn't share", ex.Message + (ex.Problems is { Count: > 0 } p ? "\n\n" + string.Join("\n", p) : ""), DialogKind.Warning); button.IsEnabled = true; }
    }
}
