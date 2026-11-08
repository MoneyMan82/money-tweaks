using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>Every stored session: summary, replay of its timeline, comparing two runs, search, and ideas that shipped.</summary>
public sealed class HistoryView : Border
{
    private readonly Hub _hub;
    private readonly ListBox _sessions = new();
    private readonly ContentControl _right = new();
    private readonly Slider _slider = new() { Minimum = 0, Margin = new Thickness(0, 6, 0, 6), IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _replayClock = Ui.T("", 12, Ui.Accent, FontWeights.SemiBold, Ui.Mono);
    private readonly StackPanel _replayAgents = new(), _replayFeed = new();
    private readonly DispatcherTimer _play = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly TextBox _search = new() { ToolTip = "Search every stored agent update" };
    private readonly StackPanel _searchResults = new();
    private readonly ComboBox _cmpA = new(), _cmpB = new();
    private readonly Grid _cmpGrid = new();
    private readonly StackPanel _shipped = new();
    private List<AgentEvent> _events = [];
    private List<SessionInfo> _list = [];
    private DateTimeOffset _loadedAt;
    private string? _current;

    public HistoryView(Hub hub)
    {
        _hub = hub;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var lh = new StackPanel();
        lh.Children.Add(Ui.Head("History"));
        lh.Children.Add(Ui.T("Every event is stored locally in SQLite.", 11, Ui.Faint));
        lh.Children.Add(new Border { Height = 6 });
        lh.Children.Add(_search);
        DockPanel.SetDock(lh, Dock.Top);
        left.Children.Add(lh);
        left.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Background = Ui.Panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _sessions });
        g.Children.Add(left);
        Grid.SetColumn(_right, 2); g.Children.Add(_right);
        Child = g;

        _sessions.SelectionChanged += (_, _) => { if (_sessions.SelectedItem is FrameworkElement { Tag: SessionInfo si }) Open(si); };
        _slider.ValueChanged += (_, _) => RenderReplay();
        _play.Tick += (_, _) => { if (_slider.Value >= _slider.Maximum) _play.Stop(); else _slider.Value = Math.Min(_slider.Maximum, _slider.Value + Math.Max(1, _slider.Maximum / 300)); };
        _search.KeyUp += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Search(); };
        _cmpA.SelectionChanged += (_, _) => Compare(); _cmpB.SelectionChanged += (_, _) => Compare();
        ShowOverview();
    }

    public void Update(List<IdeaRow> ideas, List<TaskRow> tasks, DateTimeOffset now, bool fresh)
    {
        if (_list.Count == 0 || now - _loadedAt > TimeSpan.FromSeconds(30) || (fresh && now - _loadedAt > TimeSpan.FromSeconds(5))) Reload(now);
        _shipped.Children.Clear();
        var done = ideas.Where(TeamFiles.IsDone).ToList();
        var merged = tasks.Where(t => t.Status.Contains("merged", StringComparison.OrdinalIgnoreCase)).ToList();
        if (done.Count == 0 && merged.Count == 0) _shipped.Children.Add(Ui.T("No idea or task is marked done/merged in team/IDEAS.md or team/TASKS.md yet.", 12, Ui.Faint, wrap: true));
        foreach (var i in done) _shipped.Children.Add(Ui.T($"{i.Id} · {i.Title}", 12, Ui.Green, wrap: true));
        foreach (var t in merged) _shipped.Children.Add(Ui.T($"{t.Id} · {t.Title} ({t.Branch})", 12, Ui.Green, wrap: true));
        var waiting = tasks.Where(TeamFiles.AwaitingReview).ToList();
        foreach (var t in waiting) _shipped.Children.Add(Ui.T($"Waiting for your approval: {t.Id} · {t.Title}", 12, Ui.Amber, wrap: true));
    }

    private void Reload(DateTimeOffset now)
    {
        _loadedAt = now;
        _list = _hub.Store.Sessions().Where(s => s.Id != "filesystem").ToList();
        var keep = _current;
        _sessions.Items.Clear();
        foreach (var s in _list)
        {
            var sp = new StackPanel { Margin = new Thickness(12, 7, 12, 7) };
            sp.Children.Add(Ui.T($"{s.Start.ToLocalTime():ddd d MMM HH:mm} · {Ui.Elapsed(s.End - s.Start)}", 12.5, Ui.Fg, FontWeights.SemiBold, Ui.Display));
            sp.Children.Add(Ui.T(s.FirstTask ?? "(no prompt recorded)", 11.5, Ui.Dim));
            sp.Children.Add(Ui.T($"{s.Events} events · {s.Agents} agents · {s.Files} files · tests {s.TestsPassed}/{s.TestsFailed}", 11, Ui.Faint, font: Ui.Mono));
            var item = new Border { Child = sp, Tag = s };
            _sessions.Items.Add(item);
            if (s.Id == keep) _sessions.SelectedItem = item;
        }
        foreach (var cb in new[] { _cmpA, _cmpB })
        {
            var sel = (cb.SelectedItem as ComboBoxItem)?.Tag as string;
            cb.Items.Clear();
            foreach (var s in _list) { var it = new ComboBoxItem { Content = $"{s.Start.ToLocalTime():d MMM HH:mm} · {s.Events} events", Tag = s.Id }; cb.Items.Add(it); if (s.Id == sel) cb.SelectedItem = it; }
        }
    }

    private void ShowOverview()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("Select a session on the left to replay it, or search all updates. Compare two runs:", 12.5, Ui.Dim, wrap: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        _cmpA.Width = 260; _cmpB.Width = 260; _cmpB.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(_cmpA); row.Children.Add(_cmpB);
        sp.Children.Add(row);
        sp.Children.Add(_cmpGrid);
        var sec = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        sec.Children.Add(Ui.Label("Ideas that became real features"));
        sec.Children.Add(_shipped);
        sp.Children.Add(Ui.Card(sec, Ui.Green));
        var res = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        res.Children.Add(Ui.Label("Search results"));
        res.Children.Add(_searchResults);
        sp.Children.Add(res);
        _right.Content = Ui.Scroll(sp);
    }

    private void Search()
    {
        _searchResults.Children.Clear();
        var q = _search.Text.Trim();
        if (q.Length < 2) return;
        var results = _hub.Store.Search(q, 300);
        if (_right.Content is not ScrollViewer { Content: StackPanel p } || !p.Children.Contains(_cmpGrid)) { _current = null; _sessions.SelectedItem = null; ShowOverview(); }
        _searchResults.Children.Add(Ui.T($"{results.Count} result(s) for \"{q}\"", 12, Ui.Dim));
        foreach (var e in results)
            _searchResults.Children.Add(Ui.T($"{e.Time.ToLocalTime():d MMM HH:mm:ss}  {e.AgentType ?? e.Agent}  {e.Type}: {e.Summary ?? e.Task ?? e.Tool?.Target}", 12, Ui.Fg, wrap: true));
    }

    private void Compare()
    {
        _cmpGrid.Children.Clear(); _cmpGrid.RowDefinitions.Clear(); _cmpGrid.ColumnDefinitions.Clear();
        if ((_cmpA.SelectedItem as ComboBoxItem)?.Tag is not string a || (_cmpB.SelectedItem as ComboBoxItem)?.Tag is not string b) return;
        var sa = Hub.Replay(_hub.Store.Session(a), _hub.Workspace); var sb = Hub.Replay(_hub.Store.Session(b), _hub.Workspace);
        var ia = _list.First(x => x.Id == a); var ib = _list.First(x => x.Id == b);
        var rows = new (string, string, string)[]
        {
            ("Duration", Ui.Elapsed(ia.End - ia.Start), Ui.Elapsed(ib.End - ib.Start)),
            ("Events", ia.Events.ToString(), ib.Events.ToString()),
            ("Agents", (sa.Agents.Count).ToString(), (sb.Agents.Count).ToString()),
            ("Delegations", sa.Stats.Delegations.ToString(), sb.Stats.Delegations.ToString()),
            ("Completed tasks", sa.Stats.CompletedTasks.ToString(), sb.Stats.CompletedTasks.ToString()),
            ("Files changed", sa.FileMap.Count.ToString(), sb.FileMap.Count.ToString()),
            ("Tests passed / failed", $"{sa.Stats.TestsPassed} / {sa.Stats.TestsFailed}", $"{sb.Stats.TestsPassed} / {sb.Stats.TestsFailed}"),
            ("Research discoveries", sa.Research.Count.ToString(), sb.Research.Count.ToString()),
            ("Errors / blockers", sa.Stats.Errors.ToString(), sb.Stats.Errors.ToString()),
        };
        for (int c = 0; c < 3; c++) _cmpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == 0 ? 180 : 200) });
        for (int r = 0; r < rows.Length; r++)
        {
            _cmpGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = new[] { rows[r].Item1, rows[r].Item2, rows[r].Item3 };
            for (int c = 0; c < 3; c++)
            {
                var t = Ui.T(cells[c], 12.5, c == 0 ? Ui.Dim : Ui.Fg, c == 0 ? FontWeights.Normal : FontWeights.SemiBold, c == 0 ? Ui.Body : Ui.Mono);
                t.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(t, r); Grid.SetColumn(t, c); _cmpGrid.Children.Add(t);
            }
        }
    }

    private void Open(SessionInfo si)
    {
        _current = si.Id;
        _play.Stop();
        _events = _hub.Store.Session(si.Id);
        var sp = new StackPanel();
        var head = new DockPanel();
        var back = Ui.Btn("Back", () => { _current = null; _sessions.SelectedItem = null; ShowOverview(); });
        DockPanel.SetDock(back, Dock.Right);
        head.Children.Add(back);
        head.Children.Add(Ui.Head($"Session {si.Start.ToLocalTime():ddd d MMM HH:mm} · {Ui.Elapsed(si.End - si.Start)}"));
        sp.Children.Add(head);
        sp.Children.Add(Ui.T(si.FirstTask ?? "", 12, Ui.Dim, wrap: true));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        controls.Children.Add(Ui.Btn("Play", () => { if (_slider.Value >= _slider.Maximum) _slider.Value = 0; _play.Start(); }));
        controls.Children.Add(Ui.Btn("Pause", () => _play.Stop()));
        controls.Children.Add(Ui.Btn("End", () => { _play.Stop(); _slider.Value = _slider.Maximum; }));
        _replayClock.Margin = new Thickness(14, 0, 0, 0); _replayClock.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(_replayClock);
        sp.Children.Add(controls);
        _slider.Maximum = Math.Max(0, _events.Count);
        _slider.Value = _events.Count;
        sp.Children.Add(_slider);
        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        var ag = new StackPanel(); ag.Children.Add(Ui.Label("Agents at this moment")); ag.Children.Add(_replayAgents);
        var fe = new StackPanel(); fe.Children.Add(Ui.Label("Timeline up to this moment")); fe.Children.Add(_replayFeed);
        cols.Children.Add(Ui.Card(ag));
        var fc = Ui.Card(fe); Grid.SetColumn(fc, 2); cols.Children.Add(fc);
        sp.Children.Add(cols);
        _right.Content = Ui.Scroll(sp);
        RenderReplay();
    }

    private void RenderReplay()
    {
        if (_current is null) return;
        var n = (int)_slider.Value;
        var state = Hub.Replay(_events.Take(n), _hub.Workspace);
        var at = n == 0 ? (DateTimeOffset?)null : _events[n - 1].Time;
        _replayClock.Text = at is { } t ? $"{t.ToLocalTime():HH:mm:ss}  ·  event {n}/{_events.Count}" : $"start · event 0/{_events.Count}";
        _replayAgents.Children.Clear();
        foreach (var a in state.Agents.Values.OrderBy(a => a.Started))
        {
            var row = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(Ui.T(Ui.Name(a), 12.5, Ui.Fg, FontWeights.SemiBold, Ui.Display));
            top.Children.Add(Ui.T("  " + a.Status, 10.5, Ui.StatusBrush(a.Status), FontWeights.SemiBold, Ui.Display));
            row.Children.Add(top);
            row.Children.Add(Ui.T(a.Phase ?? a.Summary ?? a.Task ?? "", 11.5, Ui.Dim, wrap: true));
            _replayAgents.Children.Add(row);
        }
        _replayFeed.Children.Clear();
        foreach (var f in state.Feed.AsEnumerable().Reverse().Take(60))
            _replayFeed.Children.Add(Ui.T($"{Ui.Clock(f.Time)}  {f.AgentType}  {f.Text}", 11.5, Ui.KindBrush(f.Kind), wrap: true));
    }
}

/// <summary>Export the live view or a past session as JSON Lines (machine-readable) or CSV (spreadsheet).</summary>
public sealed class ExportDialog : Window
{
    public ExportDialog(Hub hub)
    {
        Title = "Export activity log"; Width = 520; Height = 300; Background = Ui.Bg; Foreground = Ui.Fg; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(Ui.Head("Export activity log"));
        sp.Children.Add(Ui.T("Exports are sanitized (no secrets) and stay on this PC unless you share them.", 11.5, Ui.Dim, wrap: true));
        var pick = new ComboBox { Margin = new Thickness(0, 12, 0, 8) };
        pick.Items.Add(new ComboBoxItem { Content = "Live view (last 12 hours, all sessions)", Tag = "" });
        foreach (var s in hub.Store.Sessions().Where(s => s.Id != "filesystem")) pick.Items.Add(new ComboBoxItem { Content = $"Session {s.Start.ToLocalTime():d MMM HH:mm} · {s.Events} events", Tag = s.Id });
        pick.SelectedIndex = 0;
        sp.Children.Add(pick);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        void Save(bool csv)
        {
            var id = (pick.SelectedItem as ComboBoxItem)?.Tag as string;
            var events = string.IsNullOrEmpty(id) ? hub.Store.Since(DateTimeOffset.Now.AddHours(-12).ToUnixTimeMilliseconds()) : hub.Store.Session(id);
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"agent-activity-{DateTime.Now:yyyyMMdd-HHmm}" + (csv ? ".csv" : ".jsonl"), Filter = csv ? "CSV|*.csv" : "JSON Lines|*.jsonl" };
            if (dlg.ShowDialog(this) != true) return;
            if (csv) Exporter.Csv(events, dlg.FileName); else Exporter.Jsonl(events, dlg.FileName);
            MessageBox.Show(this, $"Saved {events.Count} events to\n{dlg.FileName}", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        row.Children.Add(Ui.Btn("Save JSON Lines", () => Save(false)));
        row.Children.Add(Ui.Btn("Save CSV", () => Save(true)));
        row.Children.Add(Ui.Btn("Cancel", Close));
        sp.Children.Add(row);
        Content = sp;
    }
}
