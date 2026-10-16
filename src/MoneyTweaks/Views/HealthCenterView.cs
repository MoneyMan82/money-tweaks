using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Driver &amp; Windows Health Center, added below the live hardware monitor. Scans are read-only and asynchronous; every
/// change (SFC/DISM repair, update services, hardware rescan) needs an explicit confirmation that lists risk, admin and
/// restart needs and the rollback options. Results are kept in health.json so the last scan shows with its timestamp.
/// </summary>
public sealed partial class MonitorView
{
    private readonly HealthState _health = HealthState.Load();
    private readonly StackPanel _hSummary = new(), _hDrivers = new(), _hWindows = new(), _hStability = new(), _fileChecks = new(), _repairs = new();
    private readonly TextBlock _hStatus = Ui.Muted("", 12);
    private readonly ProgressBar _hProgress = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 4), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _filters = new() { Margin = new Thickness(0, 4, 0, 10) };
    private Button? _scanBtn;
    private List<HealthResult> _results = [];
    private readonly HashSet<string> _pending = [];
    private readonly HashSet<string> _openDetails = [];
    private bool _scanning, _fromDisk, _fileBusy;
    private string _filter = "all";
    private CancellationTokenSource? _fileCts;
    private FileCheckKind? _fileRunning;
    private readonly TextBlock _fileStatus = Ui.Muted("", 12);
    private readonly ProgressBar _fileProgress = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new Thickness(0, 6, 0, 4), Visibility = Visibility.Collapsed };

    private Button HealthScanButton() => _scanBtn = Ui.Btn("Scan now", async (_, _) => await ScanHealthAsync(), "PrimaryButton");

    private void BuildHealth(StackPanel body)
    {
        var head = Ui.Section("Driver & Windows Health Center",
            "Checks drivers, Windows and Windows Update, and the logs that show crashes, restarts, throttling and hardware errors. Scanning only reads; nothing is changed unless you start a repair below and confirm it.");
        head.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(head);
        body.Children.Add(Ui.Card(_hSummary));
        body.Children.Add(_filters);
        body.Children.Add(AreaHeader("Driver health", "\uE7F8", "GPU, chipset, network, audio and storage drivers, Device Manager errors and newer drivers Windows Update offers."));
        body.Children.Add(_hDrivers);
        body.Children.Add(AreaHeader("Windows health", "\uE770", "Edition, version and support status, updates, update services and system file checks."));
        body.Children.Add(_hWindows);
        body.Children.Add(AreaHeader("Gaming stability", "\uE7FC", $"Blue screens, unexpected restarts, GPU driver resets, app crashes, hardware errors, throttling, memory and disks (last {HealthChecks.EventDays} days)."));
        body.Children.Add(_hStability);
        body.Children.Add(Ui.Card(_fileChecks));
        body.Children.Add(Ui.Card(_repairs));
        body.Children.Add(Ui.Muted("Money Tweaks never installs or removes drivers, never changes BIOS, security settings or services on its own, and never downloads anything except through Windows' own tools when you start a repair. Driver links open the makers' official pages.", 11.5));

        _results = _health.Results.ToList();
        _fromDisk = _results.Count > 0;
        RenderHealth();
        RenderFileChecks();
        RenderRepairs();
    }

    private static UIElement AreaHeader(string title, string icon, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), FontSize = 16, Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("H2") });
        sp.Children.Add(row);
        sp.Children.Add(new TextBlock { Text = sub, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    private static string Tone(HealthStatus s) => s switch { HealthStatus.Passed => "Success", HealthStatus.Warning => "Gold", HealthStatus.Problem => "Danger", _ => "Muted" };
    private static string SevTone(Severity s) => s switch { Severity.High => "Danger", Severity.Medium => "Gold", Severity.Low => "Accent", _ => "Muted" };

    /// <summary>Scan results plus the last SFC/DISM outcomes (shown under Windows health).</summary>
    private List<HealthResult> AllResults() => _results.Concat(_health.FileChecks.Values.Select(SystemFileCheck.AsResult)).ToList();

    // ---- scan --------------------------------------------------------------------------------------------------------

    private async Task ScanHealthAsync()
    {
        if (_scanning) return;
        _scanning = true;
        if (_scanBtn is not null) _scanBtn.IsEnabled = false;
        _results = [];
        _fromDisk = false;
        _pending.Clear();
        foreach (var c in HealthChecks.All) _pending.Add(c.Id);
        _hProgress.Value = 0;
        _hProgress.Visibility = Visibility.Visible;
        _hStatus.Text = "Scanning… (the Windows Update search can take a minute)";
        RenderHealth();
        var sw = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<HealthProgress>(p =>
            {
                _pending.Remove(p.Check.Id);
                _results.AddRange(p.Results);
                _hProgress.Value = (double)p.Done / p.Total;
                _hStatus.Text = $"Checked {p.Check.Name.ToLowerInvariant()} ({p.Done}/{p.Total})" + (_pending.Count > 0 ? $"; still checking {string.Join(", ", HealthChecks.All.Where(c => _pending.Contains(c.Id)).Select(c => c.Name.ToLowerInvariant()))}…" : "");
                RenderHealth();
            });
            _results = await HealthChecks.RunAllAsync(progress);
            _health.Results = _results;
            _health.LastScan = DateTime.Now;
            SaveHealth();
            _hStatus.Text = $"Scan finished {DateTime.Now:T} in {sw.Elapsed.TotalSeconds:0} s. Nothing was changed.";
            Log.Info($"Health scan: {_results.Count} results, overall {HealthState.Overall(_results)} ({string.Join(", ", _results.Where(r => r.Status != HealthStatus.Passed).Select(r => $"{r.Id}={r.Status}"))})");
        }
        catch (Exception ex) { Log.Error("Health scan failed", ex); _hStatus.Text = "Scan failed: " + ex.Message; }
        finally
        {
            _scanning = false;
            _pending.Clear();
            _hProgress.Visibility = Visibility.Collapsed;
            if (_scanBtn is not null) _scanBtn.IsEnabled = true;
            RenderHealth();
        }
    }

    /// <summary>Re-runs one check after a repair and replaces its results.</summary>
    private async Task RecheckAsync(string checkId)
    {
        var check = HealthChecks.All.First(c => c.Id == checkId);
        _pending.Add(checkId);
        RenderHealth();
        IReadOnlyList<HealthResult> res;
        try { res = await Task.Run(() => check.Run(CancellationToken.None)); }
        catch (Exception ex) { res = [HealthChecks.R(check.Id, check.Area, check.Name, HealthStatus.Unknown, Severity.Info, "This check couldn't run: " + ex.Message, [])]; }
        var ids = res.Select(r => r.Id).ToHashSet();
        _results.RemoveAll(r => ids.Contains(r.Id));
        _results.AddRange(res);
        _pending.Remove(checkId);
        _health.Results = _results;
        SaveHealth();
        RenderHealth();
    }

    private void SaveHealth()
    {
        try { _health.Save(); } catch (Exception ex) { Log.Error("Saving health.json failed", ex); }
    }

    // ---- rendering ---------------------------------------------------------------------------------------------------

    private void RenderHealth()
    {
        var all = AllResults();
        RenderSummary(all);
        RenderFilters(all);
        RenderArea(_hDrivers, HealthArea.Drivers, all);
        RenderArea(_hWindows, HealthArea.Windows, all);
        RenderArea(_hStability, HealthArea.Stability, all);
    }

    private void RenderSummary(List<HealthResult> all)
    {
        _hSummary.Children.Clear();
        var overall = HealthState.Overall(all);
        bool none = all.Count == 0;
        var (icon, headline, tone) = none ? ("\uE9D9", _scanning ? "Scanning…" : "Not scanned yet", "Accent")
            : overall switch
            {
                HealthStatus.Problem => ("\uE783", "Problems detected", "Danger"),
                HealthStatus.Warning => ("\uE7BA", "Some things need attention", "Gold"),
                HealthStatus.Passed => ("\uE73E", "Everything checked looks healthy", "Success"),
                _ => ("\uE9CE", "Couldn't check most items", "Muted"),
            };
        var top = new DockPanel();
        var badge = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 14, 0),
            Background = tone == "Muted" ? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) : Ui.Brush(tone + "Soft"),
            Child = new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), FontSize = 20, Foreground = Ui.Brush(tone == "Muted" ? "Muted" : tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        top.Children.Add(badge);
        var right = Ui.Row(Ui.Btn(_scanning ? "Scanning…" : none ? "Scan now" : "Refresh", async (_, _) => await ScanHealthAsync(), none ? "PrimaryButton" : "BaseButton"));
        ((Button)right.Children[0]).IsEnabled = !_scanning;
        DockPanel.SetDock(right, Dock.Right);
        right.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(right);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "OVERALL HEALTH", Style = Ui.Res<Style>("Eyebrow") });
        titles.Children.Add(new TextBlock { Text = headline, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 2, 0, 0) });
        var when = _health.LastScan is { } ls ? $"Last scan: {ls.ToString("g", CultureInfo.CurrentCulture)}{(_fromDisk ? " (saved results; press Refresh to check again)" : "")}" : "Takes about 10–60 s. Read-only.";
        titles.Children.Add(new TextBlock { Text = when, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        top.Children.Add(titles);
        _hSummary.Children.Add(top);

        if (!none)
        {
            var counts = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var s in new[] { HealthStatus.Problem, HealthStatus.Warning, HealthStatus.Passed, HealthStatus.Unknown })
            {
                var n = all.Count(r => r.Status == s);
                if (n > 0) counts.Children.Add(Ui.Tag($"{n} {HealthResult.Label(s)}", Tone(s)));
            }
            _hSummary.Children.Add(counts);
            var areas = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (area, label) in new[] { (HealthArea.Drivers, "Drivers"), (HealthArea.Windows, "Windows"), (HealthArea.Stability, "Stability") })
            {
                var a = all.Where(r => r.Area == area).ToList();
                if (a.Count == 0) continue;
                var st = HealthState.Overall(a);
                var p = a.Count(r => r.Status == HealthStatus.Problem); var w = a.Count(r => r.Status == HealthStatus.Warning);
                var text = p + w == 0 ? (st == HealthStatus.Unknown ? "unable to check" : "all passed") : string.Join(", ", new[] { p > 0 ? $"{p} problem{(p > 1 ? "s" : "")}" : null, w > 0 ? $"{w} warning{(w > 1 ? "s" : "")}" : null }.Where(x => x is not null));
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 4) };
                sp.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Brush(Tone(st) == "Muted" ? "Muted" : Tone(st)), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = $"{label}: {text}", FontSize = 12.5, Foreground = Ui.Brush("Text") });
                areas.Children.Add(sp);
            }
            _hSummary.Children.Add(areas);
        }
        _hSummary.Children.Add(_hProgress);
        if (_hStatus.Parent is Panel old) old.Children.Remove(_hStatus);
        _hSummary.Children.Add(_hStatus);
        var admin = Admin.IsElevated
            ? Ui.Muted("Running as administrator: system file checks and repairs are available.", 11.5)
            : Ui.Muted("Not running as administrator. The scan works fully; system file checks and repairs need administrator rights.", 11.5);
        admin.Margin = new Thickness(0, 6, 0, 0);
        _hSummary.Children.Add(admin);
    }

    private void RenderFilters(List<HealthResult> all)
    {
        _filters.Children.Clear();
        if (all.Count == 0) return;
        foreach (var (id, label) in new[] { ("all", $"All ({all.Count})"), ("issues", $"Problems & warnings ({all.Count(r => r.Status is HealthStatus.Problem or HealthStatus.Warning)})"), ("unknown", $"Unable to check ({all.Count(r => r.Status == HealthStatus.Unknown)})") })
        {
            var key = id;
            _filters.Children.Add(Ui.Btn(label, (_, _) => { _filter = key; RenderHealth(); }, _filter == key ? "PrimaryButton" : "GhostButton"));
        }
    }

    private bool Visible(HealthResult r) => _filter switch
    {
        "issues" => r.Status is HealthStatus.Problem or HealthStatus.Warning,
        "unknown" => r.Status == HealthStatus.Unknown,
        _ => true,
    };

    private static int Rank(HealthStatus s) => s switch { HealthStatus.Problem => 0, HealthStatus.Warning => 1, HealthStatus.Unknown => 2, _ => 3 };

    private void RenderArea(StackPanel host, HealthArea area, List<HealthResult> all)
    {
        host.Children.Clear();
        foreach (var c in HealthChecks.All.Where(c => _pending.Contains(c.Id) && (c.Area == area || (c.Id == "wu-search" && area == HealthArea.Drivers))))
            host.Children.Add(Ui.Card(Ui.Loading($"Checking {c.Name.ToLowerInvariant()}…"), new Thickness(0, 0, 0, 10)));
        var items = all.Where(r => r.Area == area && Visible(r)).OrderBy(r => Rank(r.Status)).ThenByDescending(r => r.Severity).ToList();
        if (items.Count == 0 && _pending.Count == 0)
        {
            var msg = all.Any(r => r.Area == area) ? "Nothing matches this filter." : "Press Scan now to check this area.";
            host.Children.Add(Ui.Card(Ui.Muted(msg, 12.5), new Thickness(0, 0, 0, 10)));
            return;
        }
        foreach (var r in items) host.Children.Add(ResultCard(r));
    }

    private UIElement ResultCard(HealthResult r)
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Tag(HealthResult.Label(r.Status).ToUpperInvariant(), Tone(r.Status)));
        if (r.Status is HealthStatus.Problem or HealthStatus.Warning) head.Children.Add(Ui.Tag("Severity: " + r.Severity, SevTone(r.Severity)));
        head.Children.Add(new TextBlock { Text = r.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = r.Summary, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (r.Uncertainty is { } u)
            sp.Children.Add(new TextBlock { Text = "ⓘ " + u, FontSize = 11.5, FontStyle = FontStyles.Italic, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });

        var details = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0),
            Visibility = _openDetails.Contains(r.Id) ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBox
            {
                Text = string.Join("\n", r.Details.Select(d => "• " + d).Prepend($"Check: {r.Id} · checked {r.CheckedAt.ToString("G", CultureInfo.CurrentCulture)}")),
                FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            },
        };

        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Checked " + r.CheckedAt.ToString(r.CheckedAt.Date == DateTime.Today ? "T" : "g", CultureInfo.CurrentCulture), FontSize = 11.5, Foreground = Ui.Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 8) });
        if (r.RepairId is { } rid && SafeRepairs.All.Any(x => x.Id == rid))
            row.Children.Add(Ui.Btn(SafeRepairs.Get(rid).Title + "…", async (_, _) => await RunRepairAsync(rid), "GoldButton"));
        foreach (var l in r.Links) row.Children.Add(Ui.Btn(l.Label + " ↗", (_, _) => OpenLink(l), "GhostButton"));
        if (r.Details.Count > 0)
        {
            var b = Ui.Btn(details.Visibility == Visibility.Visible ? "Hide technical details" : $"Technical details ({r.Details.Count})", (_, _) => { }, "GhostButton");
            b.Click += (_, _) =>
            {
                bool open = details.Visibility != Visibility.Visible;
                details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                if (open) _openDetails.Add(r.Id); else _openDetails.Remove(r.Id);
                b.Content = open ? "Hide technical details" : $"Technical details ({r.Details.Count})";
            };
            row.Children.Add(b);
        }
        sp.Children.Add(row);
        sp.Children.Add(details);
        return Ui.Card(sp, new Thickness(0, 0, 0, 10));
    }

    private static void OpenLink(HealthLink l)
    {
        try
        {
            var t = l.Target;
            if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) || !t.Contains(' '))
                Process.Start(new ProcessStartInfo(t) { UseShellExecute = true });
            else
            {
                var i = t.IndexOf(' ');
                Process.Start(new ProcessStartInfo(t[..i], t[(i + 1)..]) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Dialog.Show("Couldn't open it", ex.Message, DialogKind.Error); }
    }

    // ---- SFC / DISM diagnostics ---------------------------------------------------------------------------------------

    private void RenderFileChecks()
    {
        _fileChecks.Children.Clear();
        var sec = Ui.Section("System file diagnostics (SFC & DISM)",
            "Windows' own integrity checks. The read-only checks below change nothing; the repairing versions are under Safe repair actions. All of them need administrator rights.");
        foreach (var info in SystemFileCheck.All.Where(i => !i.Repairs))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var btn = Ui.Btn(_fileRunning == info.Kind ? "Running…" : "Run", async (_, _) => await RunFileCheckAsync(info.Kind, false), "BaseButton");
            btn.IsEnabled = !_fileBusy;
            DockPanel.SetDock(btn, Dock.Right);
            btn.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(btn);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{info.Title}  ·  {info.Duration}", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
            text.Children.Add(new TextBlock { Text = info.What, Style = Ui.Res<Style>("MutedText"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (_health.FileChecks.TryGetValue(info.Title, out var last))
            {
                var lr = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                lr.Children.Add(Ui.Tag(HealthResult.Label(last.Status), Tone(last.Status)));
                lr.Children.Add(new TextBlock { Text = $"{last.Finished:g} · {last.Summary}", FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, VerticalAlignment = VerticalAlignment.Center });
                text.Children.Add(lr);
            }
            row.Children.Add(text);
            sec.Children.Add(row);
        }
        sec.Children.Add(_fileProgress);
        if (_fileStatus.Parent is Panel old) old.Children.Remove(_fileStatus);
        sec.Children.Add(_fileStatus);
        if (_fileBusy && _fileRunning is { } k && SystemFileCheck.Info(k).Cancellable)
            sec.Children.Add(Ui.Row(Ui.Btn("Cancel", (_, _) => _fileCts?.Cancel(), "GhostButton", new Thickness(0, 6, 8, 0))));
        if (!Admin.IsElevated)
            sec.Children.Add(Ui.Row(Ui.Btn("Restart as administrator", (_, _) => RestartAsAdmin(), "GoldButton", new Thickness(0, 6, 8, 0))));
        _fileChecks.Children.Add(sec);
    }

    private static bool RestartAsAdmin()
    {
        if (Admin.IsElevated) return true;
        if (Dialog.Confirm("Administrator rights needed",
                "System file checks and repairs need administrator rights, so Money Tweaks has to restart as administrator. Nothing has been changed.",
                "Restart as admin", DialogKind.Warning) && Admin.RestartElevated())
        {
            App.ExitRequested = true;
            Application.Current.Shutdown();
        }
        return false;
    }

    private async Task<FileCheckOutcome?> RunFileCheckAsync(FileCheckKind kind, bool confirmed)
    {
        if (_fileBusy) return null;
        if (!Admin.IsElevated) { RestartAsAdmin(); return null; }
        var info = SystemFileCheck.Info(kind);
        if (!confirmed && !info.Repairs && info.Kind != FileCheckKind.DismCheckHealth &&
            !Dialog.Confirm($"Run {info.Title}?", $"{info.What}\n\nTakes {info.Duration}. Read-only: nothing is changed. You can keep using the PC and cancel at any time.", "Run"))
            return null;
        _fileBusy = true;
        _fileRunning = kind;
        _fileCts = new CancellationTokenSource();
        _fileProgress.Value = 0;
        _fileProgress.IsIndeterminate = true;
        _fileProgress.Visibility = Visibility.Visible;
        _fileStatus.Text = $"{info.Title}: starting…";
        RenderFileChecks();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<(double? percent, string text)>(p =>
        {
            if (p.percent is { } v) { _fileProgress.IsIndeterminate = false; _fileProgress.Value = v; }
            _fileStatus.Text = $"{info.Title}: {(p.percent is { } x ? x.ToString("0.#", CultureInfo.CurrentCulture) + "% · " : "")}{sw.Elapsed:mm\\:ss} elapsed{(info.Cancellable ? "" : " · don't turn off the PC")}";
        });
        FileCheckOutcome outcome;
        try { outcome = await SystemFileCheck.RunAsync(kind, progress, _fileCts.Token); }
        catch (Exception ex)
        {
            Log.Error(info.Title + " failed", ex);
            outcome = new(info.Title, HealthStatus.Unknown, "Couldn't run: " + ex.Message, [ex.ToString()], DateTime.Now, sw.Elapsed.TotalMinutes, -1);
        }
        finally
        {
            _fileBusy = false;
            _fileRunning = null;
            _fileProgress.Visibility = Visibility.Collapsed;
        }
        _health.FileChecks[info.Title] = outcome;
        SaveHealth();
        _fileStatus.Text = $"{info.Title} finished {outcome.Finished:T} after {outcome.Minutes:0.0} min: {HealthResult.Label(outcome.Status)}. {outcome.Summary}";
        RenderFileChecks();
        RenderHealth();
        return outcome;
    }

    // ---- safe repairs -------------------------------------------------------------------------------------------------

    private void RenderRepairs()
    {
        _repairs.Children.Clear();
        var sec = Ui.Section("Safe repair actions",
            "Each repair asks first and shows what it changes, the risk, whether it needs administrator rights or a restart, and how to undo it. A System Restore point is created first where it makes sense.");
        foreach (var r in SafeRepairs.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var btns = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var run = Ui.Btn("Run…", async (_, _) => await RunRepairAsync(r.Id), "GoldButton");
            run.IsEnabled = !_fileBusy;
            btns.Children.Add(run);
            if (r.Id == SafeRepairs.UpdateServices && _health.ServiceBackup.Count > 0)
                btns.Children.Add(Ui.Btn("Roll back", async (_, _) => await RollbackServicesAsync(), "GhostButton"));
            DockPanel.SetDock(btns, Dock.Right);
            row.Children.Add(btns);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = r.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = r.What, Style = Ui.Res<Style>("MutedText"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var tags = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (r.NeedsAdmin) tags.Children.Add(Ui.Tag("Admin", "Violet"));
            if (r.MayNeedRestart) tags.Children.Add(Ui.Tag("May need restart", "Gold"));
            tags.Children.Add(Ui.Tag(r.Duration, "Muted"));
            tags.Children.Add(Ui.Tag(r.Rollback.StartsWith("Reversible") ? "Can roll back" : r.Rollback.StartsWith("Nothing") ? "Nothing to undo" : "Not reversible", r.Rollback.StartsWith("Reversible") ? "Success" : "Muted"));
            text.Children.Add(tags);
            if (r.Id == SafeRepairs.UpdateServices && _health.ServiceBackup.Count > 0)
                text.Children.Add(new TextBlock { Text = $"Backup from {_health.ServiceBackupAt:g}: " + string.Join(", ", _health.ServiceBackup.Select(kv => $"{kv.Key} was {kv.Value}")), FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            row.Children.Add(text);
            sec.Children.Add(row);
        }
        _repairs.Children.Add(sec);
    }

    private static (bool ok, string msg) TryRestorePoint(string what) => RestorePoints.Create("Money Tweaks: before " + what);

    private async Task RunRepairAsync(string id)
    {
        if (_fileBusy) { Dialog.Show("Busy", "A system file check is still running. Wait for it to finish first."); return; }
        var r = SafeRepairs.Get(id);
        if (r.NeedsAdmin && !Admin.IsElevated) { RestartAsAdmin(); return; }
        bool rp = id is SafeRepairs.SfcScanNow or SafeRepairs.DismRestore or SafeRepairs.UpdateServices;
        var msg = new StringBuilder();
        msg.Append(r.What).Append("\n\nRisk: ").Append(r.Risk)
           .Append("\nAdministrator rights: ").Append(r.NeedsAdmin ? "yes (Money Tweaks is running as administrator)" : "no")
           .Append("\nRestart: ").Append(r.MayNeedRestart ? "may be needed afterwards; Money Tweaks will tell you, it never restarts the PC by itself" : "not needed")
           .Append("\nUndo: ").Append(r.Rollback)
           .Append("\nTakes: ").Append(r.Duration);
        if (rp) msg.Append("\n\nA System Restore point is created first (if System Protection is on for the Windows drive).");
        if (!Dialog.Confirm(r.Title + "?", msg.ToString(), "Run repair", DialogKind.Warning)) return;

        if (rp)
        {
            _fileStatus.Text = "Creating a restore point…";
            var (ok, rpMsg) = await Task.Run(() => TryRestorePoint(r.Title));
            if (!ok && !Dialog.Confirm("Restore point not created", rpMsg + "\n\nContinue with the repair without a restore point?", "Continue", DialogKind.Warning))
            {
                _fileStatus.Text = "Repair cancelled; nothing was changed.";
                return;
            }
            _fileStatus.Text = ok ? "Restore point created." : "Continuing without a restore point.";
        }

        switch (id)
        {
            case SafeRepairs.SfcScanNow or SafeRepairs.DismRestore:
            {
                var o = await RunFileCheckAsync(id == SafeRepairs.SfcScanNow ? FileCheckKind.SfcScanNow : FileCheckKind.DismRestoreHealth, true);
                if (o is not null)
                    Dialog.Show(o.Status == HealthStatus.Passed ? "Repair finished" : "Repair result", $"{o.Summary}\n\nFinished {o.Finished:T} after {o.Minutes:0.0} min (exit code {o.ExitCode}).",
                        o.Status == HealthStatus.Passed ? DialogKind.Info : DialogKind.Warning);
                break;
            }
            case SafeRepairs.UpdateServices:
            {
                var (ok, m, log) = await Task.Run(() => SafeRepairs.EnableUpdateServices(_health));
                Dialog.Show(ok ? "Update services" : "Not changed", m + (log.Count > 0 ? "\n\n" + string.Join("\n", log) : ""), ok ? DialogKind.Info : DialogKind.Warning);
                RenderRepairs();
                await RecheckAsync("wu-services");
                break;
            }
            case SafeRepairs.PnpRescan:
            {
                var (ok, m) = await Task.Run(SafeRepairs.RescanDevices);
                MainWindow.Current?.ShowToast(m, ok ? "Success" : "Gold");
                if (ok) await RecheckAsync("drivers");
                break;
            }
        }
    }

    private async Task RollbackServicesAsync()
    {
        if (!Admin.IsElevated) { RestartAsAdmin(); return; }
        if (!Dialog.Confirm("Roll back update services?",
                "Puts these services back to the start types they had before Money Tweaks changed them:\n\n" + string.Join("\n", _health.ServiceBackup.Select(kv => $"• {kv.Key}: {kv.Value}")) +
                "\n\nIf a service goes back to Disabled, Windows Update can't install updates again.", "Roll back", DialogKind.Warning)) return;
        var (ok, m, log) = await Task.Run(() => SafeRepairs.RollbackUpdateServices(_health));
        Dialog.Show(ok ? "Rolled back" : "Roll back incomplete", m + (log.Count > 0 ? "\n\n" + string.Join("\n", log) : ""), ok ? DialogKind.Info : DialogKind.Warning);
        RenderRepairs();
        await RecheckAsync("wu-services");
    }
}
