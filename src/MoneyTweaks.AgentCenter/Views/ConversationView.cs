using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>
/// The team talking: your instruction, the coordinator's briefs to each agent, what each agent writes while it works,
/// their messages to each other, progress notes and reports back - newest at the bottom, like a chat. Above it, a plain
/// "what is happening right now" summary. Briefs, reports and statements come from Claude Code's transcripts; notes
/// and tool calls from the hooks. Nothing here is invented: an item exists only if an agent actually wrote or did it.
/// </summary>
public sealed class ConversationView : Border
{
    private readonly Action<string> _open;
    private readonly StackPanel _now = new();
    private readonly ListBox _chat = new();
    private readonly ComboBox _who = new() { Width = 220 };
    private readonly CheckBox _say = Check("What agents write", true), _notes = Check("Progress notes", true), _tools = Check("Tool calls", false), _system = Check("Start / stop", true);
    private readonly Button _jump;
    private TeamState? _state, _rendered;
    private long _lastSeq;
    private DateTimeOffset _lastTime;
    private string _whoKey = "";
    private const int Max = 700;

    private static CheckBox Check(string text, bool on) => new() { Content = text, IsChecked = on, Foreground = Ui.Dim, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public ConversationView(Action<string> open)
    {
        _open = open;
        _jump = Ui.Btn("Jump to latest", ScrollToEnd);
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new StackPanel { Margin = new Thickness(4, 4, 0, 8) };
        head.Children.Add(Ui.Head("Team conversation"));
        head.Children.Add(Ui.T("Your instructions, the coordinator's briefs, what every agent writes while it works, their messages and their reports - as it happens. Agents' hidden reasoning is never shown, only what they actually wrote.", 11.5, Ui.Faint, wrap: true));
        g.Children.Add(head);

        var nowCard = Ui.Card(_now, Ui.Accent, 10, new Thickness(14, 10, 14, 10));
        var nowBox = new Border { Margin = new Thickness(0, 0, 0, 8), Child = nowCard };
        Grid.SetRow(nowBox, 1); g.Children.Add(nowBox);

        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var filters = new WrapPanel();
        filters.Children.Add(_who);
        foreach (var c in new[] { _say, _notes, _tools, _system }) { filters.Children.Add(c); c.Checked += (_, _) => Rerender(); c.Unchecked += (_, _) => Rerender(); }
        _jump.Margin = new Thickness(14, 0, 0, 0);
        _jump.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_jump, Dock.Right);
        filterRow.Children.Add(_jump);
        filterRow.Children.Add(filters);
        Grid.SetRow(filterRow, 2); g.Children.Add(filterRow);

        _chat.Background = Ui.Panel; _chat.BorderBrush = Ui.Line; _chat.BorderThickness = new Thickness(1);
        VirtualizingPanel.SetScrollUnit(_chat, ScrollUnit.Pixel);
        Grid.SetRow(_chat, 3); g.Children.Add(_chat);
        Child = g;
        _who.SelectionChanged += (_, _) => Rerender();
    }

    private string? WhoFilter => (_who.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Rerender() { _rendered = null; if (_state is not null) Update(_state, DateTimeOffset.Now); }

    public void Update(TeamState s, DateTimeOffset now)
    {
        _state = s;
        RenderNow(s, now);
        RefreshWho(s);
        var atEnd = IsAtEnd();
        var fresh = s.Conversation.Where(c => c.Seq > _lastSeq).ToList();
        // Transcript lines can land between earlier items; then redraw (the list is kept in time order).
        if (!ReferenceEquals(s, _rendered) || fresh.Any(c => c.Time < _lastTime))
        {
            _chat.Items.Clear();
            _rendered = s;
            fresh = s.Conversation.Where(Visible).TakeLast(Max).ToList();
            _lastSeq = s.Conversation.Count == 0 ? 0 : s.Conversation.Max(c => c.Seq);
            _lastTime = s.Conversation.Count == 0 ? default : s.Conversation[^1].Time;
            foreach (var c in fresh) _chat.Items.Add(Bubble(c, s));
            ScrollToEnd();
            return;
        }
        if (fresh.Count == 0) return;
        _lastSeq = fresh.Max(c => c.Seq);
        _lastTime = s.Conversation[^1].Time;
        foreach (var c in fresh.Where(Visible))
        {
            var b = Bubble(c, s);
            _chat.Items.Add(b);
            Ui.FadeIn(b, 260);
        }
        while (_chat.Items.Count > Max) _chat.Items.RemoveAt(0);
        if (atEnd) ScrollToEnd();
    }

    private bool Visible(ChatItem c)
    {
        if (c.Kind == "say" && _say.IsChecked != true) return false;
        if (c.Kind == "note" && _notes.IsChecked != true) return false;
        if (c.Kind == "tool" && _tools.IsChecked != true) return false;
        if (c.Kind == "system" && _system.IsChecked != true) return false;
        if (WhoFilter is { } who && _state is { } s)
        {
            var from = s.NameOf(c.FromId);
            if (from != who && c.ToName != who) return false;
        }
        return true;
    }

    private void RefreshWho(TeamState s)
    {
        var names = s.Conversation.Select(c => s.NameOf(c.FromId)).Distinct().OrderBy(n => n).ToList();
        var key = string.Join("|", names);
        if (key == _whoKey) return;
        _whoKey = key;
        var keep = WhoFilter;
        _who.Items.Clear();
        _who.Items.Add(new ComboBoxItem { Content = "Everyone", Tag = null });
        foreach (var n in names) _who.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        _who.SelectedItem = _who.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == keep) ?? _who.Items[0];
    }

    private bool IsAtEnd()
    {
        var sv = FindScroll(_chat);
        return sv is null || sv.VerticalOffset >= sv.ScrollableHeight - 40;
    }

    private void ScrollToEnd()
    {
        if (_chat.Items.Count > 0) _chat.ScrollIntoView(_chat.Items[^1]);
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) if (FindScroll(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    // ---- "What's happening right now" --------------------------------------------------------------------------------------------

    private static string Verb(string status) => status switch
    {
        AgentStatus.Researching => "researching", AgentStatus.Coding => "writing code", AgentStatus.Testing => "testing",
        AgentStatus.Reviewing => "reviewing", AgentStatus.Planning => "planning", AgentStatus.Analyzing => "reading and analysing",
        AgentStatus.Blocked => "blocked", AgentStatus.Paused => "paused", AgentStatus.Queued => "about to start", _ => status.ToLowerInvariant(),
    };

    private void RenderNow(TeamState s, DateTimeOffset now)
    {
        _now.Children.Clear();
        _now.Children.Add(Ui.Label("What's happening right now"));
        var active = s.Agents.Values.Where(a => a.Ended is null && (AgentStatus.IsActive(a.Status) || a.Status is AgentStatus.Blocked or AgentStatus.Paused) && !a.IsStale(now))
            .OrderBy(a => a.IsMain ? 0 : 1).ThenBy(a => a.Started).ToList();
        if (active.Count == 0)
        {
            var last = s.Conversation.LastOrDefault();
            _now.Children.Add(Ui.T("No agent is working right now.", 13.5, Ui.Fg, FontWeights.SemiBold, Ui.Display));
            _now.Children.Add(Ui.T(last is null ? "Nothing recorded yet. Press RUN CYCLE NOW, or turn on automatic cycles in the RUNS tab."
                : $"Last activity {Ui.Ago(last.Time, now)}: {s.NameOf(last.FromId)} - {Ui.OneLine(last.Summary ?? last.Text)}", 12, Ui.Dim, wrap: true));
            return;
        }
        foreach (var a in active.Take(8))
        {
            var name = s.NameOf(a.Id);
            var what = a.CurrentTool is { } t ? $"{t.Tool} {Ui.OneLine(t.Target)}" : a.Phase ?? a.Summary ?? a.Task;
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
            line.Inlines.Add(new Run(name) { Foreground = Speaker(name), FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display });
            line.Inlines.Add(new Run(" is " + Verb(a.Status)) { Foreground = Ui.StatusBrush(a.Status) });
            if (s.IsWaiting(a))
            {
                var waitingFor = s.Edges.Values.Where(e => e.From == a.Id && e.Kind == "assign" && !e.Completed).Select(e => s.NameOf(e.To)).Distinct().ToList();
                line.Inlines.Add(new Run(" and waiting for " + string.Join(", ", waitingFor)) { Foreground = Ui.Amber });
            }
            if (!string.IsNullOrWhiteSpace(what)) line.Inlines.Add(new Run(" — " + Shorten(Ui.OneLine(what), 150)) { Foreground = Ui.Dim });
            line.Inlines.Add(new Run($"  ({Ui.Elapsed(a.Elapsed(now))})") { Foreground = Ui.Faint, FontFamily = Ui.Mono, FontSize = 11 });
            _now.Children.Add(line);
        }
        if (s.Conversation.LastOrDefault(c => c.Kind == "brief") is { } brief)
            _now.Children.Add(Ui.T($"Latest instruction ({Ui.Ago(brief.Time, now)}): {s.NameOf(brief.FromId)} asked {brief.ToName} to “{Shorten(Ui.OneLine(brief.Summary), 120)}”", 12, Ui.Violet, wrap: true));
    }

    private static string Shorten(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";

    // ---- chat bubbles ----------------------------------------------------------------------------------------------------------

    private static readonly Brush[] Palette = [Ui.Accent, Ui.Green, Ui.Amber, Ui.Pink, Ui.Blue, Ui.Violet, Ui.B("#4FE3C1"), Ui.B("#FF8A5B"), Ui.B("#C3E86B"), Ui.B("#7FB2FF")];

    public static Brush Speaker(string name) => name switch
    {
        "chief-coordinator" => Ui.Violet,
        "Claude · your session" => Ui.B("#C9B8FF"),
        "You" => Ui.B("#FFD166"),
        _ => Palette[(int)((uint)name.Aggregate(17, (h, c) => h * 31 + c) % Palette.Length)],
    };

    private static string KindLabel(string kind) => kind switch
    {
        "owner" => "YOUR INSTRUCTION", "brief" => "BRIEF", "report" => "REPORT BACK", "message" => "MESSAGE", "say" => "",
        "note" => "PROGRESS NOTE", "tool" => "TOOL", "system" => "", _ => kind.ToUpperInvariant(),
    };

    private FrameworkElement Bubble(ChatItem c, TeamState s)
    {
        var from = c.Kind == "owner" ? "You" : s.NameOf(c.FromId);
        var color = Speaker(from);
        if (c.Kind is "system" or "tool")
        {
            var t = Ui.T($"{Ui.Clock(c.Time)}   {from}  {Ui.OneLine(c.Summary)}", 11, Ui.Faint, font: c.Kind == "tool" ? Ui.Mono : Ui.Body);
            t.Margin = new Thickness(20, 1, 12, 1);
            return t;
        }
        var head = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        head.Inlines.Add(new Run(from) { Foreground = color, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, FontSize = 13 });
        if (c.ToName is { } to)
        {
            head.Inlines.Add(new Run("  →  ") { Foreground = Ui.Faint });
            head.Inlines.Add(new Run(to) { Foreground = Speaker(to), FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, FontSize = 13 });
        }
        if (KindLabel(c.Kind) is { Length: > 0 } label) head.Inlines.Add(new Run("   " + label) { Foreground = KindBrush(c.Kind), FontFamily = Ui.Display, FontSize = 10 });
        head.Inlines.Add(new Run("   " + Ui.Clock(c.Time)) { Foreground = Ui.Faint, FontFamily = Ui.Mono, FontSize = 10.5 });

        var full = Plain((c.Text ?? c.Summary ?? "").Trim());
        if (c.Kind == "brief" && c.Summary is { } title && !full.StartsWith(title, StringComparison.Ordinal)) full = title + "\n\n" + full;
        var lines = full.Split('\n');
        var collapsed = lines.Length > 14 || full.Length > 1400;
        var body = new TextBlock { Text = collapsed ? string.Join("\n", lines.Take(12)).Truncate(1200) + " …" : full, TextWrapping = TextWrapping.Wrap, Foreground = c.Kind == "note" ? Ui.Dim : Ui.Fg, FontSize = 12.5, LineHeight = 18 };
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(body);
        if (collapsed)
        {
            var more = new TextBlock { Text = "Show all", Foreground = Ui.Accent, FontSize = 11.5, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            more.MouseLeftButtonUp += (_, e) =>
            {
                var expanded = more.Text == "Show less";
                body.Text = expanded ? string.Join("\n", lines.Take(12)).Truncate(1200) + " …" : full;
                more.Text = expanded ? "Show all" : "Show less";
                e.Handled = true;
            };
            sp.Children.Add(more);
        }
        var card = new Border
        {
            Child = sp, Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(c.Kind == "owner" ? 60 : 10, 5, c.Kind == "owner" ? 10 : 60, 5),
            Background = c.Kind switch { "owner" => Ui.B("#16140C"), "brief" => Ui.B("#13111F"), "report" => Ui.B("#0F1A16"), "message" => Ui.B("#1A1019"), _ => Ui.Panel2 },
            BorderBrush = color, BorderThickness = new Thickness(c.Kind == "owner" ? 0 : 3, 0, c.Kind == "owner" ? 3 : 0, 0),
            Cursor = c.Kind == "owner" ? null : System.Windows.Input.Cursors.Hand,
            ToolTip = c.Kind == "owner" ? null : "Click to open " + from + "'s workspace",
        };
        if (c.Kind != "owner") card.MouseLeftButtonUp += (_, _) => _open(c.FromId);
        return card;
    }

    /// <summary>Agents write Markdown; show it as readable plain text (bold/heading/code markers removed).</summary>
    private static string Plain(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Replace("**", "").Replace("`", ""), @"(?m)^#{1,6}\s+", "");

    private static Brush KindBrush(string kind) => kind switch
    {
        "brief" => Ui.Violet, "report" => Ui.Green, "message" => Ui.Pink, "note" => Ui.Accent, "owner" => Ui.B("#FFD166"), _ => Ui.Dim,
    };
}

internal static class StringExt
{
    public static string Truncate(this string s, int n) => s.Length <= n ? s : s[..n];
}
