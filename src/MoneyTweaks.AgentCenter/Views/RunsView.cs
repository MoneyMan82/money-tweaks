using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>
/// Starting the agents: Claude Code sign-in, "Run cycle now" / "Brainstorm now" / "Stop", the automatic schedule,
/// the live log of the current run and past runs.
/// </summary>
public sealed class RunsView : Border
{
    private readonly AgentRunner _runner = App.Runner;
    private readonly Action<RunKind> _start;
    private readonly TextBlock _cli = Ui.T("", 12, Ui.Dim, wrap: true), _signed = Ui.T("", 13, Ui.Fg, FontWeights.SemiBold, Ui.Display);
    private readonly Button _signIn, _runCycle, _runBrain, _stop;
    private readonly RadioButton _off = new() { Content = "Off", GroupName = "sched", Foreground = Ui.Fg, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _interval = new() { Content = "Every", GroupName = "sched", Foreground = Ui.Fg, Margin = new Thickness(0, 0, 6, 0) };
    private readonly RadioButton _daily = new() { Content = "Every day at", GroupName = "sched", Foreground = Ui.Fg, Margin = new Thickness(16, 0, 6, 0) };
    private readonly ComboBox _hours = new() { Width = 110 };
    private readonly TextBox _time = new() { Width = 64 };
    private readonly CheckBox _skipGaming = new() { Content = "Skip while a game is running", Foreground = Ui.Fg, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _next = Ui.T("", 12, Ui.Accent, FontWeights.SemiBold, Ui.Display);
    private readonly TextBlock _curTitle = Ui.T("", 14, Ui.Fg, FontWeights.SemiBold, Ui.Display);
    private readonly ListBox _log = new() { Height = 300 };
    private readonly ListBox _past = new() { MaxHeight = 260 };
    private string? _shownLog;
    private int _shownLines;
    private bool _loading;

    public RunsView(Action<RunKind> start)
    {
        _start = start;
        _signIn = Ui.Btn("Sign in to Claude Code", SignIn, "Opens the official Claude Code sign-in (claude auth login) in a console window; you finish it in your browser.");
        _runCycle = Ui.Btn("Run cycle now", () => _start(RunKind.Cycle), "Starts one full development cycle right away (team/CYCLE.md).");
        _runBrain = Ui.Btn("Brainstorm now", () => _start(RunKind.Brainstorm), "Starts a brainstorming competition right away: scout, competitor analyst and a domain specialist propose ideas; the idea critic scores and ranks them. Nothing is built.");
        _stop = Ui.Btn("Stop run", Stop, "Stops the Claude Code session this app started.");

        var sp = new StackPanel();
        sp.Children.Add(Ui.Head("Agent runs"));
        sp.Children.Add(Ui.T("Start the agent team from here, now or on a schedule. Each run is a real Claude Code session in the Money Tweaks folder; you watch it live in the other tabs. Runs use your Claude plan's usage. Nothing is pushed, merged or released - finished work waits in team/INBOX.md for your /merge.", 12, Ui.Dim, wrap: true));

        // Claude Code
        var cli = new StackPanel();
        cli.Children.Add(Ui.Label("Claude Code"));
        cli.Children.Add(_signed);
        cli.Children.Add(_cli);
        var cliRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 8, 0, 0) };
        cliRow.Children.Add(_signIn);
        cliRow.Children.Add(Ui.Btn("Check again", () => _ = Refresh(), "Re-checks the sign-in (claude auth status)."));
        cli.Children.Add(cliRow);
        sp.Children.Add(Gap(Ui.Card(cli, Ui.Accent)));

        // Start now
        var now = new StackPanel();
        now.Children.Add(Ui.Label("Start now"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 2, 0, 0) };
        row.Children.Add(_runCycle); row.Children.Add(_runBrain); row.Children.Add(_stop);
        now.Children.Add(row);
        now.Children.Add(Ui.T("One run at a time. A run started while a game is running is refused.", 11.5, Ui.Faint, wrap: true));
        sp.Children.Add(Gap(Ui.Card(now)));

        // Schedule
        var sch = new StackPanel();
        sch.Children.Add(Ui.Label("Automatic cycles"));
        var srow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        srow.Children.Add(_off); srow.Children.Add(_interval); srow.Children.Add(_hours); srow.Children.Add(_daily); srow.Children.Add(_time);
        sch.Children.Add(srow);
        sch.Children.Add(_skipGaming);
        sch.Children.Add(new Border { Height = 8 });
        sch.Children.Add(_next);
        sch.Children.Add(Ui.T("Scheduled cycles run while the Command Center is open (it can stay minimized). A cycle that is due while a game is running waits until you stop playing.", 11.5, Ui.Faint, wrap: true));
        sp.Children.Add(Gap(Ui.Card(sch)));

        // Current run
        var cur = new StackPanel();
        cur.Children.Add(Ui.Label("Current or selected run"));
        cur.Children.Add(_curTitle);
        _log.FontFamily = Ui.Mono; _log.FontSize = 11.5;
        cur.Children.Add(new Border { Margin = new Thickness(0, 6, 0, 0), Background = Ui.Bg, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _log });
        sp.Children.Add(Gap(Ui.Card(cur, Ui.Green)));

        var past = new StackPanel();
        past.Children.Add(Ui.Label("Past runs"));
        past.Children.Add(_past);
        sp.Children.Add(Gap(Ui.Card(past)));
        Child = Ui.Scroll(sp);

        foreach (var h in RunSchedule.Intervals) _hours.Items.Add(new ComboBoxItem { Content = h == 1 ? "1 hour" : $"{h} hours", Tag = h });
        LoadSchedule();
        _off.Checked += (_, _) => SaveSchedule(); _interval.Checked += (_, _) => SaveSchedule(); _daily.Checked += (_, _) => SaveSchedule();
        _hours.SelectionChanged += (_, _) => SaveSchedule();
        _time.LostFocus += (_, _) => SaveSchedule();
        _time.KeyUp += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SaveSchedule(); };
        _skipGaming.Checked += (_, _) => SaveSchedule(); _skipGaming.Unchecked += (_, _) => SaveSchedule();
        _past.SelectionChanged += (_, _) => { if (_past.SelectedItem is FrameworkElement { Tag: RunRecord r }) ShowLog(r, force: true); };
    }

    private static UIElement Gap(UIElement e) => new Border { Margin = new Thickness(0, 10, 0, 0), Child = e };

    private void LoadSchedule()
    {
        _loading = true;
        var s = App.Settings.Schedule;
        (s.Mode switch { ScheduleMode.Interval => _interval, ScheduleMode.Daily => _daily, _ => _off }).IsChecked = true;
        _hours.SelectedItem = _hours.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.IntervalHours) ?? _hours.Items[3];
        _time.Text = s.DailyAt;
        _skipGaming.IsChecked = s.SkipWhileGaming;
        _loading = false;
    }

    private void SaveSchedule()
    {
        if (_loading) return;
        var s = App.Settings.Schedule;
        s.Mode = _interval.IsChecked == true ? ScheduleMode.Interval : _daily.IsChecked == true ? ScheduleMode.Daily : ScheduleMode.Off;
        s.IntervalHours = (_hours.SelectedItem as ComboBoxItem)?.Tag as int? ?? 4;
        if (TimeSpan.TryParse(_time.Text.Trim(), out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)) s.DailyAt = t.ToString(@"hh\:mm");
        else _time.Text = s.DailyAt;
        s.SkipWhileGaming = _skipGaming.IsChecked == true;
        // Turning the interval on starts the clock now instead of running immediately.
        if (s.Mode == ScheduleMode.Interval && s.LastAutoRun is null) s.LastAutoRun = DateTimeOffset.Now;
        App.SaveSettings();
        Update(DateTimeOffset.Now);
    }

    private async Task Refresh() { await _runner.RefreshAsync(App.Settings.ClaudePath); Update(DateTimeOffset.Now); }

    private void SignIn()
    {
        try { _runner.OpenSignIn(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Sign in", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Stop()
    {
        if (!_runner.Running) return;
        if (MessageBox.Show(Window.GetWindow(this), "Stop the current run? The Claude Code session and its agents end now. Work already committed on agent branches stays.", "Stop run", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _runner.Stop();
    }

    public void Update(DateTimeOffset now)
    {
        _signed.Text = _runner.ClaudePath is null ? "Claude Code not found"
            : _runner.SignedIn switch { true => "Signed in · ready", false => "Not signed in", _ => "Checking…" };
        _signed.Foreground = _runner.SignedIn == true ? Ui.Green : _runner.ClaudePath is null || _runner.SignedIn == false ? Ui.Amber : Ui.Dim;
        _cli.Text = _runner.ClaudePath is null
            ? "The Claude Code command line wasn't found (it normally comes with the Claude desktop app). Install Claude Code, or set \"claudePath\" in " + App.SettingsPath
            : (_runner.SignedIn == false ? "The command line has its own sign-in (the desktop app's sign-in isn't shared with other programs). Sign in once; it is remembered.\n" : "") + _runner.ClaudePath;
        _signIn.Visibility = _runner.SignedIn == true ? Visibility.Collapsed : Visibility.Visible;
        var ready = _runner.SignedIn == true && !_runner.Running;
        _runCycle.IsEnabled = ready; _runBrain.IsEnabled = ready; _stop.IsEnabled = _runner.Running;

        var s = App.Settings.Schedule;
        _next.Text = s.NextDue(now) is { } due
            ? (now >= due ? (_runner.Running ? "Next cycle: after the current run" : s.SkipWhileGaming && AgentRunner.GameRunning() ? "Next cycle: due - waiting for your game to close" : "Next cycle: starting…") : $"Next cycle: {due.ToLocalTime():ddd HH:mm} (in {Ui.Elapsed(due - now)})")
            : "Automatic cycles are off.";

        var history = _runner.History();
        if (_runner.Current is { } c && (_past.SelectedItem is null || c.Ended is null)) ShowLog(c, force: false);
        if (_past.Items.Count != history.Count || (history.FirstOrDefault() is { } h && (_past.Items.Count == 0 || ((FrameworkElement)_past.Items[0]).Tag is RunRecord r0 && (r0.Id != h.Id || r0.Ended != h.Ended))))
        {
            _past.Items.Clear();
            foreach (var r in history)
            {
                var dur = (r.Ended ?? now) - r.Started;
                var state = r.Ended is null ? "running" : r.ExitCode == 0 ? "finished" : $"ended (exit {r.ExitCode?.ToString() ?? "?"})";
                var p = new StackPanel { Margin = new Thickness(10, 5, 10, 5) };
                p.Children.Add(Ui.T($"{r.Started.ToLocalTime():ddd d MMM HH:mm} · {(r.Kind == RunKind.Cycle ? "Cycle" : "Brainstorm")}{(r.Scheduled ? " (scheduled)" : "")} · {Ui.Elapsed(dur)} · {state}", 12, r.Ended is null ? Ui.Accent : r.ExitCode == 0 ? Ui.Fg : Ui.Amber, FontWeights.SemiBold, Ui.Display));
                if (r.Result is { Length: > 0 } res) p.Children.Add(Ui.T(res.Length > 240 ? res[..240] + "…" : res, 11.5, Ui.Dim, wrap: true));
                _past.Items.Add(new Border { Child = p, Tag = r });
            }
        }
    }

    private void ShowLog(RunRecord r, bool force)
    {
        var dur = (r.Ended ?? DateTimeOffset.Now) - r.Started;
        _curTitle.Text = $"{(r.Kind == RunKind.Cycle ? "Cycle" : "Brainstorm")} · started {r.Started.ToLocalTime():HH:mm:ss} · {(r.Ended is null ? "running " : "ran ")}{Ui.Elapsed(dur)}" + (r.Ended is not null ? (r.ExitCode == 0 ? " · finished" : $" · ended, exit {r.ExitCode}") : "");
        var lines = AgentRunner.ReadableLog(r.LogPath);
        if (!force && _shownLog == r.LogPath && lines.Count == _shownLines) return;
        _shownLog = r.LogPath; _shownLines = lines.Count;
        _log.Items.Clear();
        foreach (var l in lines)
            _log.Items.Add(new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 2), Foreground = l.StartsWith("RESULT") ? Ui.Green : l.StartsWith("→") ? Ui.Accent : l.StartsWith('!') ? Ui.Amber : Ui.Fg });
        if (_log.Items.Count == 0) _log.Items.Add(new TextBlock { Text = r.Ended is null ? "Starting Claude Code…" : "No output was recorded.", Foreground = Ui.Faint, Margin = new Thickness(8, 4, 8, 4) });
        if (_log.Items.Count > 0) _log.ScrollIntoView(_log.Items[^1]);
    }
}
