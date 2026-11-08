using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoneyTweaks.AgentCenter.Core;
using MoneyTweaks.AgentCenter.Views;

namespace MoneyTweaks.AgentCenter;

public sealed class MainWindow : Window
{
    private readonly Hub _hub = App.Hub;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _conn = Ui.T("", 11, Ui.Dim, FontWeights.SemiBold, Ui.Display), _connDetail = Ui.T("", 11, Ui.Dim);
    private readonly Rectangle _connDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Dictionary<string, TextBlock> _stats = new();
    private readonly Button _pause;
    private readonly ContentControl _center = new();
    private readonly RosterPanel _roster;
    private readonly FeedPanel _feed;
    private readonly OverviewView _overview;
    private readonly TeamGraphView _team;
    private readonly CodeView _code;
    private readonly ResearchView _research;
    private readonly RunsView _runs;
    private readonly Button _runNowBtn;
    private readonly HistoryView _history;
    private readonly Dictionary<string, AgentWindow> _agentWindows = new();
    private string _connState = "";
    private int _tick;
    private List<IdeaRow> _ideas = [];
    private List<TaskRow> _tasks = [];

    public MainWindow()
    {
        Title = "MONEY TWEAKS — AGENT COMMAND CENTER";
        Width = 1560; Height = 940; MinWidth = 1100; MinHeight = 680;
        Background = Ui.Bg; Foreground = Ui.Fg; FontFamily = Ui.Body;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _roster = new RosterPanel(OpenAgent);
        _feed = new FeedPanel(OpenAgentById);
        _overview = new OverviewView(OpenAgent);
        _team = new TeamGraphView(OpenAgentById);
        _code = new CodeView(_hub.Workspace);
        _research = new ResearchView();
        _history = new HistoryView(_hub);
        _runs = new RunsView(StartRun);
        _runNowBtn = Ui.Btn("Run cycle now", () => StartRun(RunKind.Cycle), "Start one full development cycle right away (a real Claude Code session; see the RUNS tab).");
        App.Runner.Changed += () => Dispatcher.BeginInvoke(() => Tick(force: true));
        _pause = Ui.Btn("Pause all", TogglePause, "Agents wait at their next tool call until you resume (via the publisher hook). Agents in the middle of a long tool call stop when it ends.");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Header());
        var stats = StatsStrip(); Grid.SetRow(stats, 1); root.Children.Add(stats);
        var nav = Nav(); Grid.SetRow(nav, 2); root.Children.Add(nav);
        var body = Body(); Grid.SetRow(body, 3); root.Children.Add(body);
        Content = root;

        Show("overview");
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Loaded += (_, _) => { RefreshTeamFiles(); Tick(force: true); };
        Closed += (_, _) => { _timer.Stop(); foreach (var w in _agentWindows.Values.ToList()) w.Close(); };
    }

    private UIElement Header()
    {
        var g = new Grid { Margin = new Thickness(20, 14, 20, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mark = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,18 L9,0 18,18 13,18 9,9 5,18 Z"), Fill = Ui.Accent, Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0) };
        title.Children.Add(mark);
        var tt = new StackPanel();
        tt.Children.Add(Ui.T("MONEY TWEAKS", 11, Ui.Accent, FontWeights.SemiBold, Ui.Display));
        tt.Children.Add(Ui.T("AGENT COMMAND CENTER", 20, Ui.Fg, FontWeights.SemiBold, Ui.Display));
        title.Children.Add(tt);
        g.Children.Add(title);

        var conn = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 0) };
        conn.Children.Add(_connDot);
        var ct = new StackPanel();
        ct.Children.Add(_conn);
        ct.Children.Add(_connDetail);
        conn.Children.Add(ct);
        Grid.SetColumn(conn, 1); g.Children.Add(conn);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_pause);
        actions.Children.Add(_runNowBtn);
        actions.Children.Add(Ui.Btn("Brainstorm now", () => StartRun(RunKind.Brainstorm), "Start a brainstorming competition right away: scout, competitor analyst and a domain specialist propose ideas; the idea critic scores and ranks them. Nothing is built."));
        actions.Children.Add(Ui.Btn("Export", Export, "Save the activity log (JSON Lines or CSV) of the live view or a past session."));
        actions.Children.Add(Ui.Btn("Previous runs", () => { _navButtons["history"].IsChecked = true; }, "Sessions, replay, compare and search."));
        Grid.SetColumn(actions, 2); g.Children.Add(actions);
        return g;
    }

    private static readonly (string Key, string Label)[] StatDefs =
    [
        ("active", "Active agents"), ("done", "Completed tasks"), ("progress", "In progress"), ("review", "Waiting review"), ("failed", "Failed"),
        ("research", "Discoveries"), ("ideas", "Ideas"), ("code", "Code changes"), ("tests", "Tests pass / fail"), ("duration", "Session"),
    ];

    private UIElement StatsStrip()
    {
        var u = new UniformGrid { Rows = 1, Margin = new Thickness(16, 0, 16, 6) };
        foreach (var (key, label) in StatDefs)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Label(label));
            var v = Ui.T("–", 22, Ui.Fg, FontWeights.SemiBold, Ui.Display);
            _stats[key] = v;
            sp.Children.Add(v);
            u.Children.Add(new Border { Margin = new Thickness(4, 0, 4, 0), Child = Ui.Card(sp, key switch { "failed" => Ui.Red, "active" => Ui.Accent, "tests" => Ui.Amber, _ => null }, 8, new Thickness(12, 8, 10, 8)) });
        }
        return u;
    }

    private readonly Dictionary<string, RadioButton> _navButtons = new();

    private UIElement Nav()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(342, 4, 0, 0) };
        foreach (var (key, label) in new[] { ("overview", "OVERVIEW"), ("team", "TEAM COMMUNICATION"), ("code", "CODE MONITOR"), ("research", "RESEARCH"), ("history", "HISTORY"), ("runs", "RUNS") })
        {
            var r = new RadioButton { Content = label, GroupName = "nav", Style = (Style)Application.Current.Resources["NavTab"] };
            r.Checked += (_, _) => Show(key);
            _navButtons[key] = r;
            sp.Children.Add(r);
        }
        _navButtons["overview"].IsChecked = true;
        return sp;
    }

    private UIElement Body()
    {
        var g = new Grid { Margin = new Thickness(16, 0, 16, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 220 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400), MinWidth = 260 });
        g.Children.Add(_roster);
        var s1 = new GridSplitter { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = System.Windows.Input.Cursors.SizeWE };
        Grid.SetColumn(s1, 1); g.Children.Add(s1);
        Grid.SetColumn(_center, 2); g.Children.Add(_center);
        var s2 = new GridSplitter { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = System.Windows.Input.Cursors.SizeWE };
        Grid.SetColumn(s2, 3); g.Children.Add(s2);
        Grid.SetColumn(_feed, 4); g.Children.Add(_feed);
        return g;
    }

    private string _view = "overview";

    private void Show(string key)
    {
        _view = key;
        _center.Content = key switch
        {
            "team" => _team, "code" => _code, "research" => _research, "history" => _history, "runs" => _runs, _ => _overview,
        };
        if (_center.Content is UIElement el) Ui.FadeIn(el, 160);
        Tick(force: true);
    }

    private void Tick(bool force = false)
    {
        var fresh = _hub.Drain();
        var now = DateTimeOffset.Now;
        _tick++;
        if (_tick % 40 == 0) { RefreshTeamFiles(); if (_tick % 240 == 0) _hub.ReloadRegistry(); }
        UpdateConnection(now);
        if (_tick % 120 == 0) CheckSchedule(now);
        if (_tick % 240 == 1 && App.Runner.SignedIn != true) _ = App.Runner.RefreshAsync(App.Settings.ClaudePath);
        UpdateRunButton(now);
        if (fresh.Count == 0 && !force && _tick % 4 != 0) return;   // clocks/elapsed still tick once a second

        var state = _hub.Live;
        var lanes = Lanes.Build(_hub.Registry, state);
        UpdateStats(state, now);
        _roster.Update(lanes, state, now);
        if (fresh.Count > 0 || force) _feed.Update(state);
        switch (_view)
        {
            case "overview": _overview.Update(lanes, state, now); break;
            case "team": _team.Update(lanes, state, now); break;
            case "code": _code.Update(state, now); break;
            case "research": _research.Update(state, _ideas, now); break;
            case "history": _history.Update(_ideas, _tasks, now, fresh.Count > 0); break;
            case "runs": _runs.Update(now); break;
        }
        foreach (var w in _agentWindows.Values) w.Update(state, now);
    }

    private void RefreshTeamFiles()
    {
        try { _ideas = TeamFiles.Ideas(_hub.Workspace); _tasks = TeamFiles.Tasks(_hub.Workspace); } catch { }
    }

    private void UpdateConnection(DateTimeOffset now)
    {
        var c = _hub.Connection(now);
        var hooks = HooksInstalled();
        var label = !hooks ? "NOT CONNECTED — HOOKS MISSING" : c switch { "Live" => "LIVE", "Idle" => "LISTENING — NO RECENT ACTIVITY", _ => "OFFLINE" };
        if (label != _connState)
        {
            _connState = label;
            _conn.Text = label;
            var brush = !hooks ? Ui.Red : c switch { "Live" => Ui.Green, "Idle" => Ui.Amber, _ => Ui.Red };
            _conn.Foreground = brush; _connDot.Fill = brush;
            _connDot.BeginAnimation(OpacityProperty, null);
            if (c == "Live" && hooks) Ui.Breathe(_connDot, 0.3, 0.9);
        }
        var last = _hub.LastEventAt is { } t ? "last event " + Ui.Ago(t, now) : "no events received since start";
        _connDetail.Text = !hooks
            ? "Add .claude/hooks/command_center.py to .claude/settings.json (see team/AGENT-CENTER.md)."
            : $"Named pipe \\\\.\\pipe\\{PipeListener.DefaultName} · {last} · {_hub.Received} new this run · {_hub.Store.Count()} stored"
              + (_hub.Pipe.LastError is { } err && c == "Offline" ? " · " + err : "");
        _pause.Content = _hub.Controls.Paused ? "RESUME ALL" : "PAUSE ALL";
    }

    private bool? _hooksCache; private DateTime _hooksCheckedAt;
    private bool HooksInstalled()
    {
        if (_hooksCache is { } h && DateTime.UtcNow - _hooksCheckedAt < TimeSpan.FromSeconds(20)) return h;
        _hooksCheckedAt = DateTime.UtcNow;
        try { _hooksCache = File.ReadAllText(System.IO.Path.Combine(_hub.Workspace.Root, ".claude", "settings.json")).Contains("command_center.py"); }
        catch { _hooksCache = false; }
        return _hooksCache.Value;
    }

    private void UpdateStats(TeamState s, DateTimeOffset now)
    {
        var st = s.Compute(now);
        _stats["active"].Text = st.ActiveAgents.ToString();
        _stats["done"].Text = st.CompletedTasks.ToString();
        _stats["progress"].Text = st.InProgress.ToString();
        _stats["review"].Text = _tasks.Count(TeamFiles.AwaitingReview).ToString();
        _stats["failed"].Text = st.Failed.ToString();
        _stats["research"].Text = (st.ResearchDiscoveries + s.Research.Count(r => r.Kind == "source")).ToString();
        _stats["ideas"].Text = _ideas.Count.ToString();
        _stats["code"].Text = st.CodeChanges.ToString();
        _stats["tests"].Text = $"{st.TestsPassed} / {st.TestsFailed}";
        _stats["tests"].Foreground = st.TestsFailed > 0 ? Ui.Red : Ui.Fg;
        var main = s.Agents.Values.Where(a => a.IsMain).OrderByDescending(a => a.LastSeen).FirstOrDefault();
        _stats["duration"].Text = main is null ? "–" : Ui.Elapsed((main.Ended ?? now) - (s.Agents.Values.Where(a => a.Session == main.Session).Min(a => a.Started)));
    }

    private void TogglePause()
    {
        if (_hub.Controls.Paused) _hub.Controls.ResumeAll(); else _hub.Controls.PauseAll();
        UpdateConnection(DateTimeOffset.Now);
    }

    private void UpdateRunButton(DateTimeOffset now)
    {
        var r = App.Runner.Current;
        _runNowBtn.Content = r is { Ended: null } ? $"RUNNING · {Ui.Elapsed(now - r.Started)}" : "RUN CYCLE NOW";
    }

    /// <summary>Starts a real agent run now (owner pressed a button). Checks sign-in, a running run and games first.</summary>
    private void StartRun(RunKind kind)
    {
        var runner = App.Runner;
        if (runner.Running) { _navButtons["runs"].IsChecked = true; return; }
        if (runner.SignedIn != true)
        {
            MessageBox.Show(this, runner.ClaudePath is null ? "Claude Code wasn't found on this PC." : "Claude Code isn't signed in yet. Open the RUNS tab and use \"Sign in to Claude Code\" once.", "Agent run", MessageBoxButton.OK, MessageBoxImage.Information);
            _navButtons["runs"].IsChecked = true; return;
        }
        if (AgentRunner.GameRunning())
        {
            MessageBox.Show(this, "A game is running. Agent runs build and test the app, which would slow your game down - start it after you finish playing.", "Agent run", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            runner.Start(kind, scheduled: false);
            _navButtons["runs"].IsChecked = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Agent run", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Starts a scheduled cycle when one is due (only while this app is open).</summary>
    private void CheckSchedule(DateTimeOffset now)
    {
        var s = App.Settings.Schedule;
        var runner = App.Runner;
        if (!s.IsDue(now) || runner.Running || runner.SignedIn != true) return;
        if (s.SkipWhileGaming && AgentRunner.GameRunning()) return;   // try again when the game is closed
        try
        {
            runner.Start(RunKind.Cycle, scheduled: true);
            s.LastAutoRun = now;
            App.SaveSettings();
        }
        catch { }
    }

    private void Export()
    {
        var dlg = new ExportDialog(_hub) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenAgent(Lane lane)
    {
        if (lane.Instance is { } inst) OpenAgentById(inst.Id);
        else MessageBox.Show(this, $"{lane.Name} hasn't run yet in the last 12 hours.\n\n{lane.Registered?.Description}", lane.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Test mode: saves the current view every 4 seconds (frame-NNN.png) so a pipeline test can see it update.</summary>
    public void StartFrameCapture(string folder)
    {
        Directory.CreateDirectory(folder);
        var n = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) =>
        {
            try
            {
                Save(this, System.IO.Path.Combine(folder, $"frame-{++n:000}.png"));
                File.WriteAllText(System.IO.Path.Combine(folder, "status.txt"),
                    $"{DateTimeOffset.Now:O}\nconnection={_hub.Connection(DateTimeOffset.Now)}\nreceived={_hub.Received}\nstored={_hub.Store.Count()}\nagents={string.Join(", ", _hub.Live.Agents.Values.Select(a => $"{a.Type}:{a.Status}"))}\nfiles={string.Join(", ", _hub.Live.FileMap.Values.Select(f => f.Relative + "[" + string.Join("+", f.Confirmed) + "]"))}\n");
            }
            catch { }
        };
        t.Start();
    }

    /// <summary>Screenshot mode: renders each view (and the most active agent's window) to PNG files.</summary>
    public async Task CaptureAllAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await Task.Delay(1500);
        foreach (var key in new[] { "overview", "team", "code", "research", "history", "runs" })
        {
            _navButtons[key].IsChecked = true;
            Tick(force: true);
            await Task.Delay(900);
            Save(this, System.IO.Path.Combine(folder, $"center-{key}.png"));
        }
        var busiest = _hub.Live.Agents.Values.Where(a => !a.IsMain).OrderByDescending(a => a.Progress.Count + a.Tools.Count).FirstOrDefault()
                      ?? _hub.Live.Agents.Values.OrderByDescending(a => a.LastSeen).FirstOrDefault();
        if (busiest is not null)
        {
            var w = new AgentWindow(_hub, busiest.Id) { Left = -6000, Top = -6000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show(); w.Update(_hub.Live, DateTimeOffset.Now);
            await Task.Delay(900);
            Save(w, System.IO.Path.Combine(folder, "center-agent.png"));
            w.Close();
        }
    }

    private static void Save(Window w, string path)
    {
        w.UpdateLayout();
        var el = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Ui.Bg, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        bmp.Render(bg);
        bmp.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void OpenAgentById(string id)
    {
        if (!_hub.Live.Agents.ContainsKey(id)) return;
        if (_agentWindows.TryGetValue(id, out var w)) { w.Activate(); return; }
        w = new AgentWindow(_hub, id) { Owner = null };
        w.Closed += (_, _) => _agentWindows.Remove(id);
        _agentWindows[id] = w;
        w.Show();
        w.Update(_hub.Live, DateTimeOffset.Now);
    }
}
