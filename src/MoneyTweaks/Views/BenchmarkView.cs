using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>
/// Tweak verification (read-back of every tweak) and before/after benchmarks with real measurements only:
/// Windows counters, ICMP latency, frame-time CSVs, or numbers the user reads off the game.
/// </summary>
public sealed partial class BenchmarkView : UserControl, IPageLifecycle
{
    private static (string kind, string id)? _pending;
    private static BenchmarkView? _instance;

    private BenchmarkFile _file = BenchmarkStore.Load();
    private BenchmarkStudy? _open;
    private CancellationTokenSource? _cts;
    private bool _busy;

    private readonly StackPanel _auditPanel = new();
    private readonly TextBlock _auditStatus = Ui.Muted("Not run yet. Verifying reads values only and changes nothing.", 12.5);
    private readonly WrapPanel _auditTiles = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _auditList = new();
    private List<VerifyReport> _reports = [];
    private string _auditFilter = "problems";
    private Button? _auditBtn;

    private readonly StackPanel _newPanel = new();
    private readonly StackPanel _studyPanel = new();
    private readonly StackPanel _history = new();

    public BenchmarkView()
    {
        _instance = this;
        var (root, body) = Ui.Page("Test & Benchmark",
            "Verify that each tweak really changed Windows, then measure before and after. Every number here comes from Windows counters, a ping, a frame-time file or what you type in from the game. Nothing is estimated.",
            eyebrow: "Verify · Measure · Compare", icon: "\uE9D2");
        Content = root;

        body.Children.Add(Ui.Card(Honesty()));

        body.Children.Add(Ui.Section("Tweak verification", "Reads every tweak's settings back from Windows, checks that Undo has a backup, and flags known limitations."));
        _auditBtn = Ui.Btn("Verify all tweaks (read-only)", async (_, _) => await RunAuditAsync(), "PrimaryButton");
        _auditPanel.Children.Add(Ui.Row(_auditBtn, Ui.Btn("Export report…", (_, _) => ExportAudit())));
        _auditPanel.Children.Add(_auditStatus);
        _auditPanel.Children.Add(_auditTiles);
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        foreach (var (tag, label) in new[] { ("problems", "Fail + warning"), ("FAIL", "Fail"), ("WARNING", "Warning"), ("UNVERIFIABLE", "Unverifiable"), ("PASS", "Pass"), ("all", "All") })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "auditf", IsChecked = tag == _auditFilter, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _auditFilter = tag; RenderAudit(); };
            chips.Children.Add(rb);
        }
        _auditPanel.Children.Add(chips);
        _auditPanel.Children.Add(_auditList);
        body.Children.Add(Ui.Card(_auditPanel));

        body.Children.Add(Ui.Section("Synthetic 3D benchmark", "A repeatable 3D stress scene, run without and/or with a preset applied. Your settings are restored afterwards."));
        body.Children.Add(BuildSynthetic());
        body.Children.Add(Ui.Section("Before / after benchmark", "A study measures the same conditions several times before and after a change, then says whether the difference is bigger than the normal run-to-run variation."));
        body.Children.Add(Ui.Card(_newPanel));
        body.Children.Add(_studyPanel);
        body.Children.Add(Ui.Section("History", "Saved on this PC only."));
        body.Children.Add(_history);
        body.Children.Add(Ui.Section("Testing Fortnite fairly"));
        body.Children.Add(Ui.Card(Guide()));

        BuildNewStudy(null, null);
        RenderHistory();
    }

    /// <summary>Opens the Benchmark page with a new-study form pre-filled for a tweak or preset.</summary>
    public static void Open(string kind, string id)
    {
        _pending = (kind, id);
        MainWindow.Current?.Navigate("benchmark");
        _instance?.ConsumePending();
    }

    public void OnShown() => ConsumePending();
    public void OnHidden() { }

    private void ConsumePending()
    {
        if (_pending is not { } p) return;
        _pending = null;
        BuildNewStudy(p.kind, p.id);
        _newPanel.BringIntoView();
    }

    // ---- honesty & guide ------------------------------------------------------------------------------------------
    private static UIElement Honesty()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text("What this can and can't measure", 14, FontWeights.SemiBold));
        foreach (var line in new[]
        {
            "• CPU, GPU and RAM use come from Windows performance counters. CPU temperature only appears if your firmware exposes it; GPU temperature only on NVIDIA (nvidia-smi).",
            "• Latency, jitter and packet loss are measured by pinging a public endpoint. That is NOT Fortnite's server ping. Fortnite's real ping is in its Net Debug Stats; type it in per run.",
            "• FPS, 1% lows and frame times can't be read from Fortnite directly. Import a PresentMon or CapFrameX CSV, or type in what Fortnite's FPS counter shows.",
            "• Different matches, scenes, time of day and background apps change results. A difference only counts as real when it's bigger than the run-to-run variation.",
        })
            sp.Children.Add(new TextBlock { Text = line, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    private static UIElement Guide()
    {
        var sp = new StackPanel();
        string[] steps =
        [
            "1. Restart Windows, wait 2 minutes, close every app you don't need (browser, Discord overlay, downloads, updates).",
            "2. Pick one repeatable scene: the same Creative map and spot, or the same replay moment. Avoid live matches for FPS; they're never the same twice.",
            "3. FPS: start PresentMon or CapFrameX, record the same 30–60 seconds, and import the CSV on each run. Or turn on Fortnite's FPS counter (Settings → Video → Show FPS) and type in the average you saw.",
            "4. Fortnite ping: Settings → Game UI → Net Debug Stats. Note the ping in the same lobby/region each run, then type it into that run.",
            "5. Do at least 3 runs before and 3 after with the same duration and endpoint. If a tweak needs a restart, restart before the 'after' runs.",
            "6. Read the verdict: 'No clear change' means the tweak didn't measurably help on this PC. That's a valid result.",
        ];
        foreach (var s in steps) sp.Children.Add(new TextBlock { Text = s, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Ui.Muted("Money Tweaks never reads or modifies game memory or files and never interacts with anti-cheat.", 11.5));
        return sp;
    }

    // ---- verification ---------------------------------------------------------------------------------------------
    internal async Task AuditForScreenshotAsync()
    {
        await RunAuditAsync();
        if (Content is ScrollViewer sv) sv.ScrollToVerticalOffset(_auditPanel.TranslatePoint(new Point(0, 0), sv).Y + sv.VerticalOffset - 20);
    }

    private async Task RunAuditAsync()
    {
        if (_busy) return;
        _busy = true;
        _auditBtn!.IsEnabled = false;
        try
        {
            var all = TweakCatalog.All;
            int done = 0;
            var progress = new Progress<int>(n => _auditStatus.Text = $"Verifying {n}/{all.Count}…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _reports = await Task.Run(() =>
            {
                TweakEngine.PrewarmChecks(all);
                var bag = new System.Collections.Concurrent.ConcurrentBag<VerifyReport>();
                Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = 4 }, t =>
                {
                    VerifyReport r;
                    try { r = TweakVerifier.Verify(t); }
                    catch (Exception ex) { r = new(t, VerifyStatus.Unverifiable, TweakState.Unknown, "Verification crashed: " + ex.Message, [], [], DateTime.Now); }
                    bag.Add(r);
                    ((IProgress<int>)progress).Report(Interlocked.Increment(ref done));
                });
                return bag.OrderBy(r => r.Tweak.Category).ThenBy(r => r.Tweak.Name).ToList();
            });
            var c = Counts();
            _auditStatus.Text = $"Verified {_reports.Count} tweaks in {sw.Elapsed.TotalSeconds:0.0} s ({DateTime.Now:T}){(Admin.IsElevated ? "" : ". Not running as admin: boot-configuration checks are unverifiable")}.";
            Log.Info($"Tweak audit: {_reports.Count} audited, {c.pass} pass, {c.warn} warning, {c.fail} fail, {c.unv} unverifiable");
            RenderAudit();
        }
        finally { _busy = false; _auditBtn.IsEnabled = true; }
    }

    private (int pass, int warn, int fail, int unv) Counts() =>
        (_reports.Count(r => r.Status == VerifyStatus.Pass), _reports.Count(r => r.Status == VerifyStatus.Warning),
         _reports.Count(r => r.Status == VerifyStatus.Fail), _reports.Count(r => r.Status == VerifyStatus.Unverifiable));

    private static string Tone(VerifyStatus s) => s switch { VerifyStatus.Pass => "Success", VerifyStatus.Warning => "Gold", VerifyStatus.Fail => "Danger", _ => "Muted" };

    private void RenderAudit()
    {
        _auditTiles.Children.Clear();
        _auditList.Children.Clear();
        if (_reports.Count == 0) return;
        var (pass, warn, fail, unv) = Counts();
        foreach (var (label, n, tone) in new[] { ("Audited", _reports.Count, "Accent"), ("Pass", pass, "Success"), ("Warning", warn, "Gold"), ("Fail", fail, "Danger"), ("Unverifiable", unv, "Muted") })
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
            sp.Children.Add(new TextBlock { Text = n.ToString(CultureInfo.CurrentCulture), FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Ui.Brush(tone == "Muted" ? "Muted" : tone) });
            sp.Children.Add(Ui.Muted(label, 11.5));
            _auditTiles.Children.Add(sp);
        }
        var shown = _reports.Where(r => _auditFilter switch
        {
            "all" => true,
            "problems" => r.Status is VerifyStatus.Fail or VerifyStatus.Warning,
            var s => r.StatusLabel == s,
        }).OrderByDescending(r => r.Status == VerifyStatus.Fail).ToList();
        if (shown.Count == 0) { _auditList.Children.Add(Ui.Muted("Nothing in this filter.")); return; }
        foreach (var r in shown.Take(150)) _auditList.Children.Add(AuditRow(r));
        if (shown.Count > 150) _auditList.Children.Add(Ui.Muted($"…and {shown.Count - 150} more. Export the report to see all.", 11.5));
    }

    private static UIElement AuditRow(VerifyReport r)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var head = new WrapPanel();
        head.Children.Add(Ui.Tag(r.StatusLabel, Tone(r.Status)));
        head.Children.Add(Ui.Text(r.Tweak.Name, 13, FontWeights.SemiBold));
        head.Children.Add(new TextBlock { Text = $"   {CategoryInfo.DisplayName(r.Tweak.Category)} · {r.State}", Foreground = Ui.Brush("Subtle"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = r.Summary, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock
        {
            Text = string.Join("\n", r.Evidence.Select(e => "• " + e).Concat(r.Issues.Select(i => $"! {i.Label}: {i.Text}"))),
            FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed
        };
        sp.Children.Add(detail);
        var b = new Border { Child = sp, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(0), Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, ToolTip = "Click for evidence" };
        b.MouseLeftButtonUp += (_, _) => detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        return b;
    }

    private void ExportAudit()
    {
        if (_reports.Count == 0) { MainWindow.Current?.ShowToast("Run 'Verify all tweaks' first.", "Gold"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text report|*.txt", FileName = $"money-tweaks-verification-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dlg.ShowDialog() != true) return;
        var (pass, warn, fail, unv) = Counts();
        var sb = new StringBuilder($"Money Tweaks {App.Version} tweak verification, {DateTime.Now:g}\n{OsInfo.ProductName} (build {OsInfo.Build}), admin: {Admin.IsElevated}\n");
        sb.Append($"Audited {_reports.Count}: {pass} pass, {warn} warning, {fail} fail, {unv} unverifiable.\n\n");
        foreach (var r in _reports.OrderBy(r => r.Status == VerifyStatus.Pass).ThenBy(r => r.Tweak.Id)) sb.Append(r.ToText()).Append("\n\n");
        try { File.WriteAllText(dlg.FileName, sb.ToString()); MainWindow.Current?.ShowToast("Report saved.", "Success"); }
        catch (Exception ex) { Dialog.Show("Couldn't save the report", ex.Message, DialogKind.Error); }
    }

    // ---- new study ------------------------------------------------------------------------------------------------
    private sealed record Target(string Kind, string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static List<Target> Targets(string kind) => kind == "preset"
        ? PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).Select(p => new Target("preset", p.Id, p.Name)).ToList()
        : TweakCatalog.All.Where(t => t.Kind == TweakKind.Toggle && TweakAudit.DisabledReason(t) is null && t.Category != TweakCategory.AppRemoval)
            .Select(t => new Target("tweak", t.Id, $"{t.Name}  ({CategoryInfo.DisplayName(t.Category)})")).ToList();

    private void BuildNewStudy(string? kind, string? id)
    {
        _newPanel.Children.Clear();
        _newPanel.Children.Add(Ui.Text("New study", 15, FontWeights.SemiBold));
        var name = new TextBox { Text = $"Test {DateTime.Now:g}", MaxLength = 80, Margin = new Thickness(0, 6, 0, 0) };
        _newPanel.Children.Add(Label("Name"));
        _newPanel.Children.Add(name);

        _newPanel.Children.Add(Label("What to test"));
        var kindRow = new WrapPanel();
        var search = new TextBox { Margin = new Thickness(0, 4, 0, 4), ToolTip = "Search" };
        var list = new ListBox { Height = 150, Background = Brushes.Transparent, Foreground = Ui.Brush("Text"), BorderBrush = Ui.Brush("BorderBrush") };
        var pickerBox = new StackPanel();
        pickerBox.Children.Add(search);
        pickerBox.Children.Add(list);
        string curKind = kind ?? "none";
        void Fill()
        {
            var q = search.Text.Trim();
            var items = curKind == "none" ? [] : Targets(curKind).Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = items;
            if (id is not null) { list.SelectedItem = items.FirstOrDefault(t => t.Id == id); list.ScrollIntoView(list.SelectedItem); }
            pickerBox.Visibility = curKind == "none" ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var (k, label) in new[] { ("none", "Nothing (measure only)"), ("tweak", "A tweak"), ("preset", "A preset") })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "bk", IsChecked = k == curKind, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { curKind = k; id = null; search.Text = ""; Fill(); };
            kindRow.Children.Add(rb);
        }
        search.TextChanged += (_, _) => Fill();
        _newPanel.Children.Add(kindRow);
        _newPanel.Children.Add(pickerBox);
        Fill();

        int duration = 30, runs = 3;
        string endpoint = "1.1.1.1";
        _newPanel.Children.Add(Label("Run length (same for every run)"));
        _newPanel.Children.Add(ChipRow("bd", [("15 s", "15"), ("30 s", "30"), ("60 s", "60"), ("120 s", "120")], "30", v => duration = int.Parse(v, CultureInfo.InvariantCulture)));
        _newPanel.Children.Add(Label("Runs before and after (3+ recommended)"));
        _newPanel.Children.Add(ChipRow("br", [("1", "1"), ("2", "2"), ("3", "3"), ("5", "5")], "3", v => runs = int.Parse(v, CultureInfo.InvariantCulture)));
        _newPanel.Children.Add(Label("Latency endpoint (generic, not Fortnite's server)"));
        _newPanel.Children.Add(ChipRow("be", PingMonitor.Targets.Select(t => (t.label, t.host)).Append(("No ping", "")).ToArray(), "1.1.1.1", v => endpoint = v));
        _newPanel.Children.Add(Label("Scene / conditions (written on every run)"));
        var scene = new TextBox { Text = "", MaxLength = 200, ToolTip = "e.g. Creative map 1234-5678-9012, same spot, lobby EU" };
        _newPanel.Children.Add(scene);

        var create = Ui.Btn("Create study", (_, _) =>
        {
            var target = list.SelectedItem as Target;
            if (curKind != "none" && target is null) { Dialog.Show("Pick a target", "Choose the tweak or preset to test, or pick 'Nothing' to only measure."); return; }
            var s = new BenchmarkStudy
            {
                Name = string.IsNullOrWhiteSpace(name.Text) ? $"Test {DateTime.Now:g}" : name.Text.Trim(),
                TargetKind = target?.Kind ?? "none", TargetId = target?.Id, TargetName = target?.Name ?? "Nothing (measure only)",
                DurationSec = duration, RunsPerPhase = runs, Endpoint = endpoint, Scene = scene.Text.Trim(),
            };
            _file.Studies.Add(s);
            Save();
            OpenStudy(s);
            RenderHistory();
        }, "PrimaryButton", new Thickness(0, 14, 8, 0));
        _newPanel.Children.Add(create);
    }

    private static TextBlock Label(string t) => new() { Text = t, Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 12, 0, 4) };

    private static WrapPanel ChipRow(string group, (string label, string value)[] opts, string initial, Action<string> pick)
    {
        var w = new WrapPanel();
        foreach (var (label, value) in opts)
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = value == initial, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => pick(value);
            w.Children.Add(rb);
        }
        return w;
    }

    // ---- history ----------------------------------------------------------------------------------------------
    private void RenderHistory()
    {
        _history.Children.Clear();
        if (_file.Studies.Count == 0) { _history.Children.Add(Ui.Card(Ui.Empty("\uE9D2", "No benchmarks yet", "Create a study above. Results are saved on this PC."))); return; }
        foreach (var s in _file.Studies.OrderByDescending(s => s.Created))
        {
            var runs = _file.Runs.Where(r => r.StudyId == s.Id).ToList();
            var sp = new StackPanel();
            var head = new WrapPanel();
            head.Children.Add(Ui.Text(s.Name, 14, FontWeights.SemiBold));
            sp.Children.Add(head);
            var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            tags.Children.Add(Ui.Tag(s.TargetName, "Accent"));
            tags.Children.Add(Ui.Tag($"{runs.Count(r => r.Phase == "Baseline")} before · {runs.Count(r => r.Phase == "After")} after", "Muted"));
            if (s.AppliedIds.Count > 0) tags.Children.Add(Ui.Tag("Applied", "Gold"));
            if (s.RolledBackAt is not null) tags.Children.Add(Ui.Tag("Rolled back", "Muted"));
            tags.Children.Add(new TextBlock { Text = s.Created.ToString("g", CultureInfo.CurrentCulture), Foreground = Ui.Brush("Subtle"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(tags);
            var row = Ui.Row(Ui.Btn("Open", (_, _) => OpenStudy(s), "PrimaryButton"),
                Ui.Btn("Export CSV…", (_, _) => Export([s], csv: true), "GhostButton"),
                Ui.Btn("Export JSON…", (_, _) => Export([s], csv: false), "GhostButton"),
                Ui.Btn("Delete", (_, _) => DeleteStudy(s), "GhostButton"));
            row.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(row);
            _history.Children.Add(Ui.Card(sp));
        }
        if (_file.Studies.Count > 1)
            _history.Children.Add(Ui.Row(Ui.Btn("Export all (CSV)…", (_, _) => Export(_file.Studies, true)), Ui.Btn("Export all (JSON)…", (_, _) => Export(_file.Studies, false))));
    }

    private void Export(IEnumerable<BenchmarkStudy> studies, bool csv)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = csv ? "CSV|*.csv" : "JSON|*.json", FileName = $"money-tweaks-benchmarks-{DateTime.Now:yyyyMMdd-HHmm}.{(csv ? "csv" : "json")}" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (csv) BenchmarkStore.ExportCsv(_file, studies, dlg.FileName); else BenchmarkStore.ExportJson(_file, studies, dlg.FileName);
            MainWindow.Current?.ShowToast("Exported.", "Success");
        }
        catch (Exception ex) { Dialog.Show("Export failed", ex.Message, DialogKind.Error); }
    }

    private void DeleteStudy(BenchmarkStudy s)
    {
        var extra = s.AppliedIds.Count > 0 ? "\n\nThe tweaks this study applied stay applied; roll them back first if you want them off." : "";
        if (!Dialog.Confirm($"Delete “{s.Name}”?", $"Deletes the study and its {_file.Runs.Count(r => r.StudyId == s.Id)} runs from this PC.{extra}", "Delete", DialogKind.Warning)) return;
        _file.Studies.Remove(s);
        _file.Runs.RemoveAll(r => r.StudyId == s.Id);
        Save();
        if (_open == s) { _open = null; _studyPanel.Children.Clear(); }
        RenderHistory();
    }

    private void Save()
    {
        try { BenchmarkStore.Save(_file); }
        catch (Exception ex) { Log.Error("Saving benchmarks failed", ex); Dialog.Show("Couldn't save benchmark history", ex.Message, DialogKind.Error); }
    }

    // ---- study ------------------------------------------------------------------------------------------------
    private string _chartMetric = "cpu";
    private ProgressBar? _runBar;
    private TextBlock? _runText;
    private Canvas? _live;

    private void OpenStudy(BenchmarkStudy s)
    {
        _open = s;
        RenderStudy();
        _studyPanel.BringIntoView();
    }

    private List<BenchmarkRun> RunsOf(string phase) => _file.Runs.Where(r => r.StudyId == _open!.Id && r.Phase == phase).OrderBy(r => r.At).ToList();

    private void RenderStudy()
    {
        _studyPanel.Children.Clear();
        if (_open is not { } s) return;
        var sp = new StackPanel();
        var head = new DockPanel();
        var close = Ui.Btn("Close", (_, _) => { _open = null; _studyPanel.Children.Clear(); }, "GhostButton");
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(Ui.Text(s.Name, 16, FontWeights.SemiBold));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted($"Target: {s.TargetName} · {s.DurationSec} s per run · {s.RunsPerPhase} runs per phase · endpoint {(s.Endpoint.Length == 0 ? "none" : s.Endpoint)}" + (s.Scene.Length > 0 ? $" · {s.Scene}" : ""), 12));
        if (s.AppliedAt is { } at) sp.Children.Add(Ui.Muted($"Applied {at:g}: {s.AppliedIds.Count} tweak(s) changed." + (s.RolledBackAt is { } rb ? $" Rolled back {rb:g}." : ""), 12));

        var before = RunsOf("Baseline");
        var after = RunsOf("After");
        bool hasTarget = s.TargetKind != "none";
        bool synthetic = s.TargetKind == "synthetic";
        if (synthetic) sp.Children.Add(Ui.Muted("Synthetic 3D benchmark: Before = without tweaks, After = with tweaks. Start new runs from the Synthetic 3D section above.", 12));
        var steps = Ui.Row(
            Ui.Btn($"1  Run baseline ×{s.RunsPerPhase}", async (_, _) => await RunPhaseAsync("Baseline"), before.Count == 0 ? "PrimaryButton" : "BaseButton"));
        if (hasTarget) steps.Children.Add(Ui.Btn($"2  Apply {(s.TargetKind == "preset" ? "preset" : "tweak")}", async (_, _) => await ApplyTargetAsync(), before.Count > 0 && s.AppliedAt is null ? "PrimaryButton" : "BaseButton"));
        steps.Children.Add(Ui.Btn($"{(hasTarget ? "3" : "2")}  Run {(hasTarget ? "after" : "comparison")} ×{s.RunsPerPhase}", async (_, _) => await RunPhaseAsync("After"), (s.AppliedAt is not null || !hasTarget) && after.Count == 0 && before.Count > 0 ? "PrimaryButton" : "BaseButton"));
        if (hasTarget && s.AppliedIds.Count > 0) steps.Children.Add(Ui.Btn("Roll back", async (_, _) => await RollbackAsync(), "GoldButton"));
        steps.Children.Add(Ui.Btn("Stop run", (_, _) => _cts?.Cancel(), "GhostButton"));
        steps.Margin = new Thickness(0, 12, 0, 0);
        if (!synthetic) sp.Children.Add(steps);

        _runBar = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 8, 0, 4), Visibility = Visibility.Collapsed };
        _runText = Ui.Muted("", 12);
        _live = new Canvas { Height = 90, Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed, ClipToBounds = true };
        sp.Children.Add(_runBar);
        sp.Children.Add(_runText);
        sp.Children.Add(_live);

        sp.Children.Add(new TextBlock { Text = "RUNS", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 16, 0, 6) });
        sp.Children.Add(RunsTable(before.Concat(after).ToList()));

        sp.Children.Add(new TextBlock { Text = "COMPARISON", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 18, 0, 6) });
        sp.Children.Add(Comparison(before, after));

        sp.Children.Add(new TextBlock { Text = "CHART", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 18, 0, 6) });
        var withData = BenchmarkStats.Metrics.Where(m => before.Concat(after).Any(r => m.Get(r).HasValue)).ToList();
        if (withData.Count > 0)
        {
            if (withData.All(m => m.Key != _chartMetric)) _chartMetric = withData[0].Key;
            var chartHost = new ContentControl();
            sp.Children.Add(ChipRow("bcm" + s.Id, withData.Select(m => (m.Label, m.Key)).ToArray(), _chartMetric, k => { _chartMetric = k; chartHost.Content = Chart(before, after); }));
            chartHost.Content = Chart(before, after);
            sp.Children.Add(chartHost);
        }
        else sp.Children.Add(Ui.Muted("No data yet."));
        _studyPanel.Children.Add(Ui.Card(sp));
    }

    private UIElement RunsTable(List<BenchmarkRun> runs)
    {
        if (runs.Count == 0) return Ui.Muted("No runs yet. Start with the baseline.");
        var g = new Grid();
        string[] cols = ["Phase", "Time", "CPU %", "GPU %", "RAM GB", "Temp CPU/GPU", "Endpoint ms (jitter, loss)", "FPS (1% low)", "Fortnite ping", ""];
        foreach (var _ in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int r, int c, UIElement e) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 16, 3); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < cols.Length; c++) Cell(0, c, new TextBlock { Text = cols[c], FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Subtle") });
        string N(double? v, string f = "0.0") => v?.ToString(f, CultureInfo.CurrentCulture) ?? "–";
        int row = 1;
        foreach (var r in runs)
        {
            g.RowDefinitions.Add(new RowDefinition());
            Cell(row, 0, Ui.Tag(r.Phase == "Baseline" ? "Before" : "After", r.Phase == "Baseline" ? "Accent" : "Violet"));
            Cell(row, 1, new TextBlock { Text = r.At.ToString("g", CultureInfo.CurrentCulture), FontSize = 12, ToolTip = $"Power plan: {r.Conditions.PowerPlan}\nOn AC: {r.Conditions.OnAcPower?.ToString() ?? "unknown"}\nProcesses: {r.Conditions.ProcessCount}\nCPU before start: {r.Conditions.IdleCpuBefore:0.#}%\nForeground: {r.Conditions.Foreground}\nFortnite running: {r.Conditions.FortniteRunning}\nScene: {r.Conditions.Scene}" });
            Cell(row, 2, new TextBlock { Text = $"{N(r.CpuAvg)} (p95 {N(r.CpuP95, "0")})", FontSize = 12 });
            Cell(row, 3, new TextBlock { Text = N(r.GpuAvg), FontSize = 12 });
            Cell(row, 4, new TextBlock { Text = N(r.RamUsedGb, "0.00"), FontSize = 12 });
            Cell(row, 5, new TextBlock { Text = $"{N(r.CpuTempC, "0")} / {N(r.GpuTempC, "0")}", FontSize = 12 });
            Cell(row, 6, new TextBlock { Text = r.PingSent == 0 ? "–" : $"{N(r.PingAvgMs)} ({N(r.JitterMs)}, {N(r.LossPct, "0.#")}%)", FontSize = 12, ToolTip = r.Conditions.Endpoint is null ? null : $"ICMP to {r.Conditions.Endpoint}, {r.PingSent} pings. Not Fortnite's server." });
            Cell(row, 7, new TextBlock { Text = r.Frames is null ? "–" : $"{N(r.Frames.AvgFps)} ({N(r.Frames.Low1Fps)})", FontSize = 12, ToolTip = r.Frames?.Source });
            Cell(row, 8, new TextBlock { Text = r.GamePingMs is null ? "–" : $"{N(r.GamePingMs, "0")} ms", FontSize = 12 });
            var acts = new WrapPanel();
            var run = r;
            acts.Children.Add(SmallBtn("FPS CSV…", () => ImportCsv(run)));
            acts.Children.Add(SmallBtn("Enter…", () => EnterManual(run)));
            acts.Children.Add(SmallBtn("Delete", () =>
            {
                if (!Dialog.Confirm("Delete this run?", $"{run.Phase} run from {run.At:g}.", "Delete", DialogKind.Warning)) return;
                _file.Runs.Remove(run); Save(); RenderStudy(); RenderHistory();
            }));
            Cell(row, 9, acts);
            row++;
        }
        return new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static Button SmallBtn(string text, Action click)
    {
        var b = new Button { Content = text, Style = Ui.Res<Style>("GhostButton"), Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private UIElement Comparison(List<BenchmarkRun> before, List<BenchmarkRun> after)
    {
        var sp = new StackPanel();
        if (before.Count == 0 || after.Count == 0) { sp.Children.Add(Ui.Muted("Needs runs before and after.")); return sp; }
        foreach (var w in BenchmarkStats.ConditionWarnings(before, after))
            sp.Children.Add(new TextBlock { Text = "⚠ " + w, Foreground = Ui.Brush("Gold"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        string[] cols = ["Metric", "Before (mean ± sd, n)", "After (mean ± sd, n)", "Change", "Verdict"];
        foreach (var _ in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int r, int c, UIElement e) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 18, 3); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < cols.Length; c++) Cell(0, c, new TextBlock { Text = cols[c], FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Subtle") });
        string S(Summary? x, string unit) => x is null ? "–" : $"{BenchmarkStats.Fmt(x.Mean)} ± {BenchmarkStats.Fmt(x.Sd)} {unit} (n={x.N})";
        int row = 1, better = 0, worse = 0, tested = 0;
        foreach (var m in BenchmarkStats.Metrics)
        {
            var c = BenchmarkStats.Compare(m, before, after);
            if (c.Before is null && c.After is null) continue;
            g.RowDefinitions.Add(new RowDefinition());
            Cell(row, 0, new TextBlock { Text = m.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, ToolTip = "Source: " + m.Source });
            Cell(row, 1, new TextBlock { Text = S(c.Before, m.Unit), FontSize = 12 });
            Cell(row, 2, new TextBlock { Text = S(c.After, m.Unit), FontSize = 12 });
            Cell(row, 3, new TextBlock { Text = c.Diff is { } d ? $"{(d >= 0 ? "+" : "")}{BenchmarkStats.Fmt(d)} {m.Unit}" + (c.Pct is { } p ? $" ({(p >= 0 ? "+" : "")}{p:0.#}%)" : "") : "–", FontSize = 12 });
            var (txt, tone) = c.Verdict switch
            {
                Verdict.Better => ("Better", "Success"), Verdict.Worse => ("Worse", "Danger"), Verdict.NoClearChange => ("No clear change", "Muted"), _ => ("Not enough data", "Muted")
            };
            var tag = Ui.Tag(txt, tone);
            tag.ToolTip = c.Explanation;
            Cell(row, 4, tag);
            if (c.Verdict != Verdict.NotEnoughData) tested++;
            if (c.Verdict == Verdict.Better) better++;
            if (c.Verdict == Verdict.Worse) worse++;
            row++;
        }
        sp.Children.Add(new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        string verdict = tested == 0 ? "Not conclusive yet: run at least 2 before and 2 after."
            : better == 0 && worse == 0 ? "No metric changed by more than the normal variation between runs. On this PC, under these conditions, the change had no measurable effect."
            : $"{better} metric(s) measurably better, {worse} worse. Only these are conclusive; everything else is within run-to-run variation.";
        sp.Children.Add(new TextBlock { Text = verdict, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        if (before.Concat(after).All(r => r.GamePingMs is null) && before.Concat(after).Any(r => r.PingSent > 0))
            sp.Children.Add(Ui.Muted("Endpoint latency is a generic network test. It does not show whether Fortnite's ping changed; type in the ping from Fortnite's Net Debug Stats for that.", 11.5));
        return sp;
    }

    private UIElement Chart(List<BenchmarkRun> before, List<BenchmarkRun> after)
    {
        var m = BenchmarkStats.Metrics.First(x => x.Key == _chartMetric);
        var pts = before.Select(r => (r, phase: 0)).Concat(after.Select(r => (r, phase: 1))).Select(x => (x.r, x.phase, v: m.Get(x.r))).Where(x => x.v.HasValue).ToList();
        const double H = 150, BarW = 34, Gap = 12, Left = 8;
        var canvas = new Canvas { Height = H + 40, Width = Math.Max(300, Left + pts.Count * (BarW + Gap) + 40), HorizontalAlignment = HorizontalAlignment.Left };
        if (pts.Count == 0) return canvas;
        var max = pts.Max(p => p.v!.Value);
        var min = Math.Min(0, pts.Min(p => p.v!.Value));
        var range = Math.Max(1e-6, max - min);
        for (int i = 0; i < pts.Count; i++)
        {
            var (r, phase, v) = pts[i];
            var h = Math.Max(2, (v!.Value - min) / range * H);
            var x = Left + i * (BarW + Gap);
            var bar = new Rectangle { Width = BarW, Height = h, RadiusX = 0, RadiusY = 0, Fill = Ui.Brush(phase == 0 ? "Accent" : "Violet"), ToolTip = $"{(phase == 0 ? "Before" : "After")} {r.At:g}: {BenchmarkStats.Fmt(v.Value)} {m.Unit}" };
            Canvas.SetLeft(bar, x); Canvas.SetTop(bar, H - h + 16);
            canvas.Children.Add(bar);
            Motion.GrowUp(bar, i * 55);
            var lbl = new TextBlock { Text = BenchmarkStats.Fmt(v.Value), FontSize = 10, Foreground = Ui.Brush("Muted"), Width = BarW + Gap, TextAlignment = TextAlignment.Left };
            Canvas.SetLeft(lbl, x); Canvas.SetTop(lbl, H - h);
            canvas.Children.Add(lbl);
        }
        foreach (var (phase, brush) in new[] { (0, "Accent"), (1, "Violet") })
        {
            var vals = pts.Where(p => p.phase == phase).Select(p => p.v!.Value).ToList();
            if (vals.Count == 0) continue;
            var y = H - (vals.Average() - min) / range * H + 16;
            var line = new Line { X1 = 0, X2 = canvas.Width, Y1 = y, Y2 = y, Stroke = Ui.Brush(brush), StrokeDashArray = [4, 4], StrokeThickness = 1, Opacity = 0.7 };
            canvas.Children.Add(line);
        }
        var legend = new TextBlock { Text = $"{m.Label} ({m.Unit}) per run · blue = before, violet = after · dashed = average · source: {m.Source}", FontSize = 11, Foreground = Ui.Brush("Subtle") };
        Canvas.SetLeft(legend, 0); Canvas.SetTop(legend, H + 20);
        canvas.Children.Add(legend);
        return new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    // ---- running ----------------------------------------------------------------------------------------------
    private async Task RunPhaseAsync(string phase)
    {
        if (_open is not { } s || _busy) return;
        if (phase == "After" && s.TargetKind != "none" && s.AppliedAt is null &&
            !Dialog.Confirm("Target not applied yet", "This study's tweak/preset hasn't been applied from here, so 'after' runs would measure the same thing as the baseline. Run anyway?", "Run anyway", DialogKind.Warning)) return;
        if (phase == "After" && s.AppliedAt is { } at && at > Core.Engine.TweakVerifier.BootTime && AppliedNeedsReboot(s) &&
            !Dialog.Confirm("Restart first", "At least one applied tweak only takes effect after a restart, and Windows hasn't restarted since. 'After' runs now would not include its effect. Run anyway?", "Run anyway", DialogKind.Warning)) return;
        if (phase == "Baseline" && s.TargetKind == "tweak" && s.TargetId is { } tid && TweakCatalog.Find(tid) is { } tw &&
            await Task.Run(() => TweakEngine.GetState(tw)) == TweakState.Applied &&
            !Dialog.Confirm("Already applied", $"“{tw.Name}” is already active, so the baseline would include it and the comparison would show nothing. Undo it first from its category page for a fair test. Run anyway?", "Run anyway", DialogKind.Warning)) return;

        _busy = true;
        _cts = new CancellationTokenSource();
        _runBar!.Visibility = Visibility.Visible;
        _live!.Visibility = Visibility.Visible;
        int done = 0;
        try
        {
            for (int i = 1; i <= s.RunsPerPhase; i++)
            {
                if (i > 1)
                {
                    _runText!.Text = $"Pause between runs (5 s)…";
                    await Task.Delay(5000, _cts.Token);
                }
                var cpuPts = new List<double>(); var pingPts = new List<double?>();
                var progress = new Progress<BenchmarkTick>(t =>
                {
                    _runBar.Value = (double)t.Second / t.Total;
                    cpuPts.Add(t.Cpu); pingPts.Add(t.PingMs);
                    _runText!.Text = $"{phase} run {i}/{s.RunsPerPhase}: {t.Second}/{t.Total} s · CPU {t.Cpu:0}%" + (t.Gpu is { } g ? $" · GPU {g:0}%" : "") + (t.PingMs is { } p ? $" · endpoint {p:0} ms" : "");
                    DrawLive(cpuPts, pingPts, t.Total);
                });
                _runText!.Text = $"{phase} run {i}/{s.RunsPerPhase}: settling…";
                var run = await BenchmarkSampler.RunAsync(s.Id, phase, s.DurationSec, s.Endpoint.Length == 0 ? null : s.Endpoint, s.Scene, progress, _cts.Token, App.Version);
                _file.Runs.Add(run);
                Save();
                done++;
            }
            MainWindow.Current?.ShowToast($"{done} {phase.ToLowerInvariant()} run(s) saved. Add FPS data per run if you measured it.", "Success");
        }
        catch (OperationCanceledException) { MainWindow.Current?.ShowToast($"Stopped. {done} completed run(s) were saved; the interrupted one was discarded.", "Gold"); }
        catch (Exception ex) { Log.Error("Benchmark run failed", ex); Dialog.Show("Benchmark run failed", ex.Message, DialogKind.Error); }
        finally
        {
            _busy = false;
            _cts.Dispose(); _cts = null;
            RenderStudy();
            RenderHistory();
        }
    }

    private static bool AppliedNeedsReboot(BenchmarkStudy s) => s.AppliedIds.Select(TweakCatalog.Find).Any(t => t?.RequiresReboot == true);

    private void DrawLive(List<double> cpu, List<double?> ping, int total)
    {
        if (_live is null) return;
        _live.Children.Clear();
        double w = Math.Max(100, _live.ActualWidth), h = _live.Height;
        var cpuLine = new Polyline { Stroke = Ui.Brush("Accent"), StrokeThickness = 1.6 };
        for (int i = 0; i < cpu.Count; i++) cpuLine.Points.Add(new Point(i * w / Math.Max(1, total - 1), h - cpu[i] / 100 * (h - 6) - 3));
        _live.Children.Add(cpuLine);
        var okPing = ping.Where(p => p.HasValue).Select(p => p!.Value).ToList();
        if (okPing.Count > 1)
        {
            var max = Math.Max(10, okPing.Max() * 1.2);
            var pl = new Polyline { Stroke = Ui.Brush("Violet"), StrokeThickness = 1.6 };
            for (int i = 0; i < ping.Count; i++) if (ping[i] is { } p) pl.Points.Add(new Point(i * w / Math.Max(1, total - 1), h - p / max * (h - 6) - 3));
            _live.Children.Add(pl);
        }
        var legend = new TextBlock { Text = "CPU % (blue) · endpoint ms (violet)", FontSize = 10, Foreground = Ui.Brush("Subtle") };
        Canvas.SetLeft(legend, 6); Canvas.SetTop(legend, 2);
        _live.Children.Add(legend);
    }

    private List<string> TargetIds(BenchmarkStudy s) => s.TargetKind switch
    {
        "tweak" when s.TargetId is not null => [s.TargetId],
        "preset" => PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).FirstOrDefault(p => p.Id == s.TargetId)?.TweakIds ?? [],
        _ => []
    };

    private async Task ApplyTargetAsync()
    {
        if (_open is not { } s || _busy) return;
        var ids = TargetIds(s);
        var items = ids.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
        if (items.Count == 0) { Dialog.Show("Nothing to apply", "The target no longer exists (deleted preset?)."); return; }
        _busy = true;
        try
        {
            await TweakService.RefreshStatesAsync(items);
            var todo = items.Where(i => i.CanToggle && !i.IsGuide && i.State is TweakState.NotApplied or TweakState.Partial).ToList();
            if (todo.Count == 0) { Dialog.Show("Already active", "Everything in this target is already applied (or not available on this PC), so there's nothing to change."); return; }
            var warn = string.Join("", todo.Where(i => i.IsCaution && i.Tweak.Warning is not null).Select(i => $"\n⚠ {i.Name}: {i.Tweak.Warning}"));
            if (!Dialog.Confirm($"Apply {todo.Count} tweak(s)?", string.Join("\n", todo.Select(i => "• " + i.Name)) + warn +
                    "\n\nEach one is backed up, read back after applying (and rolled back automatically if Windows doesn't keep it). 'Roll back' on this study undoes exactly these.",
                    "Apply", todo.Any(i => i.IsCaution) ? DialogKind.Warning : DialogKind.Confirm)) return;
            var (ok, failed, reboot) = await TweakService.BatchAsync(todo, true);
            var applied = todo.Where(i => i.State == TweakState.Applied).Select(i => i.Tweak.Id).ToList();
            if (applied.Count > 0)
            {
                s.AppliedIds = s.AppliedIds.Union(applied).ToList();
                s.SwitchedIds = s.SwitchedIds.Union(applied).ToList(); // kept after a rollback (Tweak Truth Index)
                s.AppliedAt = DateTime.Now;
                s.RolledBackAt = null;
                Save();
            }
            var fails = string.Join("\n", todo.Where(i => i.State != TweakState.Applied).Select(i => $"• {i.Name}: {i.Message}"));
            Dialog.Show(failed == 0 ? "Applied and verified" : "Applied with problems",
                $"{ok} applied (each read back from Windows), {failed} failed{(fails.Length > 0 ? ":\n" + fails : ".")}" +
                (reboot ? "\n\nAt least one change needs a restart. Restart Windows, reopen Money Tweaks, open this study from History, then run the 'after' runs." : "\n\nNow run the 'after' runs under the same conditions."),
                failed == 0 ? DialogKind.Info : DialogKind.Warning);
        }
        finally { _busy = false; RenderStudy(); RenderHistory(); }
    }

    private async Task RollbackAsync()
    {
        if (_open is not { } s || _busy || s.AppliedIds.Count == 0) return;
        if (!Dialog.Confirm("Roll back?", $"Undoes the {s.AppliedIds.Count} tweak(s) this study applied, restores their backed-up values and checks each value afterwards. Benchmark results are kept.", "Roll back", DialogKind.Warning)) return;
        _busy = true;
        try
        {
            var items = s.AppliedIds.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
            await TweakService.RefreshStatesAsync(items);
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, false);
            await TweakService.RefreshStatesAsync(items);
            var still = items.Where(i => i.State is TweakState.Applied or TweakState.Partial).ToList();
            s.AppliedIds = still.Select(i => i.Tweak.Id).ToList();
            if (still.Count == 0) s.RolledBackAt = DateTime.Now;
            Save();
            var detail = string.Join("\n", items.Select(i => $"• {i.Name}: {i.Message}"));
            Dialog.Show(still.Count == 0 ? "Rolled back and verified" : "Rollback incomplete",
                $"{ok} reverted, {failed} failed.\n{detail}{(reboot ? "\n\nRestart Windows to finish." : "")}", still.Count == 0 ? DialogKind.Info : DialogKind.Warning);
        }
        finally { _busy = false; RenderStudy(); RenderHistory(); }
    }

    // ---- game data ----------------------------------------------------------------------------------------------
    private void ImportCsv(BenchmarkRun run)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Frame-time CSV (PresentMon, CapFrameX)|*.csv|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (stats, process) = FrameTimeCsv.Parse(dlg.FileName);
            run.Frames = stats;
            Save();
            MainWindow.Current?.ShowToast($"Imported {stats.Frames} frames from {(process.Length == 0 ? "the file" : process)}: {stats.AvgFps:0.0} FPS avg, {stats.Low1Fps:0.0} 1% low.", "Success");
            RenderStudy();
        }
        catch (Exception ex) { Dialog.Show("Couldn't read that file", ex.Message, DialogKind.Error); }
    }

    private void EnterManual(BenchmarkRun run)
    {
        var fields = new (string label, double? value, double min, double max)[]
        {
            ("Average FPS (from Fortnite's FPS counter or another tool)", run.Frames?.Source.StartsWith("Manual") == true || run.Frames is null ? run.Frames?.AvgFps : null, 1, 2000),
            ("1% low FPS (optional)", run.Frames?.Source.StartsWith("Manual") == true ? run.Frames.Low1Fps : null, 1, 2000),
            ("Fortnite ping in ms (Net Debug Stats, optional)", run.GamePingMs, 0, 2000),
        };
        if (run.Frames is not null && !run.Frames.Source.StartsWith("Manual"))
            fields[0].value = null;
        var res = NumberForm.Ask("Enter game numbers", "Only enter what you actually saw for this run. Leave a field empty if you didn't measure it.", fields);
        if (res is null) return;
        if (res[0] is { } fps)
        {
            if (res[1] is { } low && low > fps) { Dialog.Show("Check the numbers", "The 1% low can't be higher than the average FPS."); return; }
            run.Frames = new FrameStats { Source = "Manual entry", AvgFps = fps, Low1Fps = res[1] };
        }
        else if (run.Frames?.Source.StartsWith("Manual") == true) run.Frames = null;
        run.GamePingMs = res[2];
        Save();
        RenderStudy();
    }
}

/// <summary>Small themed form with numeric fields and range validation.</summary>
internal static class NumberForm
{
    public static double?[]? Ask(string title, string note, (string label, double? value, double min, double max)[] fields)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Foreground = Ui.Brush("Text"),
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = 440, ShowInTaskbar = false,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = title,
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("H2") });
        sp.Children.Add(new TextBlock { Text = note, Style = Ui.Res<Style>("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        var boxes = new List<TextBox>();
        foreach (var f in fields)
        {
            sp.Children.Add(new TextBlock { Text = f.label, Foreground = Ui.Brush("Muted"), FontSize = 12, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap });
            var tb = new TextBox { Text = f.value?.ToString(CultureInfo.CurrentCulture) ?? "", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            boxes.Add(tb);
            sp.Children.Add(tb);
        }
        var err = new TextBlock { Foreground = Ui.Brush("Danger"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(err);
        double?[]? result = null;
        var ok = new Button { Content = "Save", Style = Ui.Res<Style>("PrimaryButton"), MinWidth = 90, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var vals = new double?[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var t = boxes[i].Text.Trim();
                if (t.Length == 0) continue;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                { err.Text = $"'{t}' is not a number."; return; }
                if (v < fields[i].min || v > fields[i].max) { err.Text = $"{fields[i].label}: must be between {fields[i].min} and {fields[i].max}."; return; }
                vals[i] = v;
            }
            result = vals;
            win.Close();
        };
        cancel.Click += (_, _) => win.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        sp.Children.Add(buttons);
        win.Content = new Border
        {
            Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(22), Margin = new Thickness(12), Child = sp,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 }
        };
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) win.DragMove(); };
        win.ShowDialog();
        return result;
    }
}
