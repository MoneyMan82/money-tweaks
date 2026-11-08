using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.AgentCenter.Core;
using MoneyTweaks.AgentCenter.Views;

namespace MoneyTweaks.AgentCenter;

/// <summary>
/// One agent's workspace in its own window (drag it to another monitor): assignment, live progress, findings, decisions,
/// files with diffs, tool activity, who it works with, subtasks, problems and its final report. Updates live.
/// </summary>
public sealed class AgentWindow : Window
{
    private readonly Hub _hub;
    private readonly string _id;
    private readonly ContentControl _badge = new();
    private readonly TextBlock _title = Ui.T("", 20, Ui.Fg, FontWeights.SemiBold, Ui.Display), _sub = Ui.T("", 12, Ui.Dim, wrap: true), _timing = Ui.T("", 12, Ui.Dim, font: Ui.Mono);
    private readonly StackPanel _assignment = new(), _progress = new(), _findings = new(), _decisions = new(), _files = new(), _tools = new(), _peers = new(), _todos = new(), _errors = new(), _report = new();
    private readonly DiffViewer _diff = new() { MaxHeight = 380 };
    private readonly Button _stop;
    private string _sig = "", _status = "";

    public AgentWindow(Hub hub, string id)
    {
        _hub = hub; _id = id;
        Width = 1100; Height = 860; MinWidth = 760; MinHeight = 500;
        Background = Ui.Bg; Foreground = Ui.Fg; FontFamily = Ui.Body;
        _stop = Ui.Btn("Stop agent", ToggleStop, "Its next tool call is refused with an instruction to report and stop (through the publisher hook). Claude Code offers no supported way to kill or restart an agent from another program.");
        var restart = Ui.Btn("Restart", () => { }, "Not supported: Claude Code has no interface for another program to restart an agent. Ask the coordinator (or /cycle) to run it again.");
        restart.IsEnabled = false;

        var head = new Grid { Margin = new Thickness(20, 16, 20, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hs = new StackPanel();
        var tline = new StackPanel { Orientation = Orientation.Horizontal };
        tline.Children.Add(_title);
        _badge.Margin = new Thickness(14, 4, 0, 0);
        tline.Children.Add(_badge);
        hs.Children.Add(tline);
        hs.Children.Add(_sub);
        hs.Children.Add(_timing);
        head.Children.Add(hs);
        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        acts.Children.Add(_stop); acts.Children.Add(restart);
        Grid.SetColumn(acts, 1); head.Children.Add(acts);

        var cols = new Grid { Margin = new Thickness(20, 0, 20, 16) };
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Section("Current assignment", _assignment, Ui.Accent));
        left.Children.Add(Section("Live progress", _progress));
        left.Children.Add(Section("Findings and discoveries", _findings));
        left.Children.Add(Section("Decisions (summarized reasons, as the agent reported them)", _decisions));
        left.Children.Add(Section("Final report", _report, Ui.Green));
        var right = new StackPanel();
        right.Children.Add(Section("Subtasks", _todos));
        right.Children.Add(Section("Files and code changes", _files));
        right.Children.Add(Section("Diff", new StackPanel { Children = { _diff } }));
        right.Children.Add(Section("Working with", _peers));
        right.Children.Add(Section("Tool activity (observed by Claude Code)", _tools));
        right.Children.Add(Section("Errors and warnings", _errors, Ui.Red));
        cols.Children.Add(Ui.Scroll(left));
        var rs = Ui.Scroll(right); Grid.SetColumn(rs, 2); cols.Children.Add(rs);

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(cols);
        Content = dock;
    }

    private static UIElement Section(string title, Panel body, System.Windows.Media.Brush? edge = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Label(title));
        sp.Children.Add(body);
        return new Border { Margin = new Thickness(0, 0, 0, 10), Child = Ui.Card(sp, edge, 10) };
    }

    private void ToggleStop()
    {
        if (!_hub.Live.Agents.TryGetValue(_id, out var a)) return;
        var target = a.IsMain ? "main" : a.Id;
        if (_hub.Controls.IsStopped(target)) { _hub.Controls.ClearStop(target); return; }
        if (MessageBox.Show(this, $"Stop {Ui.Name(a)}?\n\nIts next tool call will be refused with the instruction to report what it has done and stop. It can still report back. Work already done stays (agents work on their own branches).",
                "Stop agent", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _hub.Controls.Stop(target);
    }

    public void Update(TeamState s, DateTimeOffset now)
    {
        if (!s.Agents.TryGetValue(_id, out var a)) return;
        Title = $"{Ui.Name(a)} — Agent workspace";
        _title.Text = Ui.Name(a);
        var reg = _hub.Registry.FirstOrDefault(r => r.Name == (a.IsMain ? "chief-coordinator" : a.Type));
        _sub.Text = (reg?.Description ?? (a.IsMain ? "The session's main agent (acts as Chief Coordinator during /cycle)." : "Built-in Claude Code agent type.")) + $"\nId {a.Id}" + (a.Worktree is null ? "" : $" · worktree {a.Worktree}");
        _timing.Text = $"started {a.Started.ToLocalTime():HH:mm:ss} · {Ui.Elapsed(a.Elapsed(now))} · last activity {Ui.Ago(a.LastSeen, now)}" + (a.IsStale(now) ? " · no activity for 15+ min (its session may be closed)" : "");
        var stopKey = a.IsMain ? "main" : a.Id;
        _stop.Content = _hub.Controls.IsStopped(stopKey) ? "ALLOW AGAIN" : "STOP AGENT";
        _stop.IsEnabled = a.Ended is null;
        if (_status != a.Status) { _status = a.Status; _badge.Content = Ui.Badge(a.Status, AgentStatus.IsActive(a.Status)); }

        var sig = $"{a.LastSeen.ToUnixTimeMilliseconds()}|{a.Progress.Count}|{a.Tools.Count}|{a.Files.Count}|{a.Errors.Count}|{a.Todos.Count}|{a.Report?.Length}|{a.Status}";
        if (sig == _sig) return;
        _sig = sig;

        Fill(_assignment,
            ("Task", a.Task), ("Phase", a.Phase), ("Now", a.CurrentTool is { } t ? $"{t.Tool} · {t.Target}" : null),
            ("Next", a.Next), ("Needs help from", a.NeedsHelp), ("Assigned by", a.ParentId is { } p && s.Agents.TryGetValue(p, out var par) ? Ui.Name(par) : null),
            ("Waiting", s.IsWaiting(a) ? "Waiting for agents it delegated to" : null));
        List(_progress, a.Progress.AsEnumerable().Reverse().Take(80).Select(n => $"{Ui.Clock(n.Time)}  {n.Text}"), "No progress notes yet. Agents publish them at milestones (team/RULES.md).");
        List(_findings, a.Findings.AsEnumerable().Reverse().Concat(a.Sources.Select(x => "Source: " + x)), "None reported.");
        List(_decisions, a.Decisions.AsEnumerable().Reverse().Select(d => $"{Ui.Clock(d.Time)}  {d.What}" + (d.Why is null ? "" : $" — because {d.Why}")), "None reported.");
        List(_report, a.Report is null ? [] : [a.Report], a.Ended is null ? "Not finished yet." : "No report text.");
        List(_todos, a.Todos.Select(td => (td.Status == "completed" ? "✓ " : td.Status == "in_progress" ? "▶ " : "○ ") + td.Content)
            .Concat(a.CompletedSubtasks.Where(c => a.Todos.All(td => td.Content != c)).Select(c => "✓ " + c)), "No subtask list (agents can keep one with TodoWrite).");
        List(_peers, a.Peers.Select(pid => s.Agents.TryGetValue(pid, out var peer) ? $"{Ui.Name(peer)} · {peer.Status}" : pid), "Not communicating with other agents.");
        List(_tools, a.Tools.AsEnumerable().Reverse().Take(60).Select(t => $"{Ui.Clock(t.Time)}  {t.Tool}{(t.Ok == false ? " ✗" : "")}  {t.Target}{(t.Ms is { } ms ? $"  ({ms} ms)" : "")}"), "No tools used yet.");
        List(_errors, a.Errors.AsEnumerable().Reverse().Select(e => $"{Ui.Clock(e.Time)}  {e.Text}"), "None.", Ui.Red);

        _files.Children.Clear();
        if (a.Files.Count == 0) _files.Children.Add(Ui.T("No files modified.", 12, Ui.Faint));
        foreach (var f in a.Files.OrderBy(f => f))
        {
            var fa = s.FileMap.GetValueOrDefault(f);
            var b = new Button { Content = _hub.Workspace.Relative(f) + (fa is null ? "" : $"   +{fa.Added?.ToString() ?? "?"} −{fa.Removed?.ToString() ?? "?"}"), Style = (Style)Application.Current.Resources["CmdButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2), FontFamily = Ui.Mono, FontSize = 11 };
            var path = f;
            b.Click += async (_, _) => { var d = await Task.Run(() => _hub.Workspace.Diff(path)); _diff.Show(d); };
            _files.Children.Add(b);
        }
    }

    private static void Fill(Panel p, params (string Label, string? Value)[] rows)
    {
        p.Children.Clear();
        foreach (var (l, v) in rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
        {
            p.Children.Add(new TextBlock { Text = l.ToUpperInvariant(), FontFamily = Ui.Display, FontSize = 9.5, Foreground = Ui.Faint, Margin = new Thickness(0, 6, 0, 1) });
            p.Children.Add(Ui.T(v, 12.5, Ui.Fg, wrap: true));
        }
        if (p.Children.Count == 0) p.Children.Add(Ui.T("No assignment recorded.", 12, Ui.Faint));
    }

    private static void List(Panel p, IEnumerable<string> items, string empty, System.Windows.Media.Brush? fg = null)
    {
        p.Children.Clear();
        foreach (var i in items.Take(120)) p.Children.Add(new TextBlock { Text = i, Foreground = fg ?? Ui.Fg, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        if (p.Children.Count == 0) p.Children.Add(Ui.T(empty, 12, Ui.Faint, wrap: true));
    }
}
