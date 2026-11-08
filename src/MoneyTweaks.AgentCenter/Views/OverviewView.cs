using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>Live agent overview: one card per agent with status, task, phase, latest update, timing, files and problems.</summary>
public sealed class OverviewView : Border
{
    private readonly Action<Lane> _open;
    private readonly WrapPanel _wrap = new();
    private readonly CheckBox _activeOnly = new() { Content = "Only agents that ran", Foreground = Ui.Dim, IsChecked = false, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, Card> _cards = new();
    private string _layout = "";

    private sealed class Card
    {
        public required FrameworkElement Root;
        public required ContentControl Badge;
        public required TextBlock Task, Phase, Update, Tool, Timing, Files, Done, Problems, Help;
        public string? Status;
        public Lane Lane = null!;
    }

    public OverviewView(Action<Lane> open)
    {
        _open = open;
        var dock = new DockPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 0, 10) };
        head.Children.Add(Ui.Head("Live agent overview"));
        head.Children.Add(_activeOnly);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(Ui.Scroll(_wrap));
        Child = dock;
        _activeOnly.Checked += (_, _) => _layout = "";
        _activeOnly.Unchecked += (_, _) => _layout = "";
    }

    public void Update(List<Lane> lanes, TeamState s, DateTimeOffset now)
    {
        var shown = _activeOnly.IsChecked == true ? lanes.Where(l => l.Instance is not null).ToList() : lanes;
        // Working agents first, then recently active, then the rest in team order.
        shown = shown.OrderBy(l => l.Instance is { } a && AgentStatus.IsActive(a.Status) ? 0 : l.Instance is null ? 2 : 1).ThenByDescending(l => l.Instance?.LastSeen).ToList();
        var layout = string.Join("|", shown.Select(l => l.Name));
        if (layout != _layout)
        {
            _wrap.Children.Clear();
            foreach (var l in shown) { if (!_cards.TryGetValue(l.Name, out var c)) _cards[l.Name] = c = Build(l); _wrap.Children.Add(c.Root); }
            _layout = layout;
        }
        foreach (var l in shown) Fill(_cards[l.Name], l, s, now);
    }

    private Card Build(Lane l)
    {
        var sp = new StackPanel { Width = 300 };
        var top = new DockPanel();
        var badge = new ContentControl();
        DockPanel.SetDock(badge, Dock.Right);
        top.Children.Add(badge);
        top.Children.Add(Ui.T(l.Name, 15, Ui.Fg, FontWeights.SemiBold, Ui.Display));
        sp.Children.Add(top);
        sp.Children.Add(Ui.T(Role(l), 11, Ui.Faint, wrap: false));
        TextBlock Field(string label)
        {
            sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontFamily = Ui.Display, FontSize = 9.5, Foreground = Ui.Faint, Margin = new Thickness(0, 8, 0, 1) });
            var t = Ui.T("", 12, Ui.Fg, wrap: true);
            t.MaxHeight = 34;
            sp.Children.Add(t);
            return t;
        }
        var c = new Card
        {
            Root = null!, Badge = badge,
            Task = Field("Current task"), Phase = Field("Phase"), Update = Field("Latest update"), Tool = Field("Now"),
            Timing = Ui.T("", 11, Ui.Dim, font: Ui.Mono), Files = Ui.T("", 11, Ui.Dim, wrap: true), Done = Ui.T("", 11, Ui.Dim),
            Problems = Ui.T("", 11, Ui.Red, wrap: true), Help = Ui.T("", 11, Ui.Amber, wrap: true), Lane = l,
        };
        var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var t in new[] { c.Timing, c.Files, c.Done, c.Help, c.Problems }) foot.Children.Add(t);
        sp.Children.Add(foot);
        var card = Ui.Card(sp, null, 12, new Thickness(14, 12, 14, 12));
        var root = new Border { Margin = new Thickness(0, 0, 10, 10), Child = card, Cursor = System.Windows.Input.Cursors.Hand };
        root.MouseLeftButtonUp += (_, _) => _open(c.Lane);
        c.Root = root;
        return c;
    }

    private static string Role(Lane l)
    {
        var d = l.Registered?.Description ?? "Not in .claude/agents (a built-in Claude Code agent type).";
        var cut = d.IndexOf(". ", StringComparison.Ordinal);
        d = d.Replace("Use PROACTIVELY ", "").Replace("MUST BE USED ", "");
        return (l.Registered?.Group ?? l.Group) + " · " + (d.Length > 90 ? d[..90] + "…" : d);
    }

    private static void Fill(Card c, Lane l, TeamState s, DateTimeOffset now)
    {
        c.Lane = l;
        var a = l.Instance;
        var status = a?.Status ?? AgentStatus.Idle;
        var key = status + (a?.IsStale(now) == true ? "~" : "");
        if (c.Status != key)
        {
            c.Status = key;
            c.Badge.Content = Ui.Badge(a?.IsStale(now) == true ? status + " · QUIET" : status, a is not null && AgentStatus.IsActive(status) && !a.IsStale(now));
        }
        c.Task.Text = a?.Task ?? (a is null ? "No activity in the last 12 hours." : "–");
        c.Phase.Text = a?.Phase ?? "–";
        c.Update.Text = a?.Summary ?? a?.Report ?? "–";
        c.Tool.Text = a?.CurrentTool is { } t ? $"{t.Tool} · {t.Target}" : a is not null && s.IsWaiting(a) ? "Waiting for delegated agents" : "–";
        if (a is null) { c.Timing.Text = l.Runs == 0 ? "" : ""; c.Files.Text = c.Done.Text = c.Problems.Text = c.Help.Text = ""; return; }
        c.Timing.Text = $"started {Ui.Clock(a.Started)} · {(a.Ended is null && AgentStatus.IsActive(a.Status) ? "running" : "ran")} {Ui.Elapsed(a.Elapsed(now))}" + (l.Runs > 1 ? $" · {l.Runs} runs" : "");
        c.Files.Text = a.Files.Count == 0 ? "No files modified" : $"Files: " + string.Join(", ", a.Files.Take(3).Select(Path.GetFileName)) + (a.Files.Count > 3 ? $" +{a.Files.Count - 3}" : "");
        var completed = a.CompletedSubtasks.Count + (a.Report is not null ? 1 : 0);
        c.Done.Text = completed == 0 ? "" : $"Completed: {completed}" + (a.LastTests is { } tr ? $" · tests {tr.Passed} passed / {tr.Failed} failed" : "");
        c.Help.Text = a.NeedsHelp is { } h ? "Needs help from: " + h : "";
        c.Problems.Text = a.Errors.Count == 0 ? "" : $"{a.Errors.Count} error(s)/blocker(s): " + a.Errors[^1].Text;
    }
}
