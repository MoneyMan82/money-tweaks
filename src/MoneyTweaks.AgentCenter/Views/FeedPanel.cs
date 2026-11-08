using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>Right column: the live activity feed (newest on top), filterable by text and kind.</summary>
public sealed class FeedPanel : Border
{
    private readonly ListBox _list = new();
    private readonly ObservableCollection<FrameworkElement> _items = new();
    private readonly TextBox _filter = new() { Margin = new Thickness(0, 6, 0, 0), ToolTip = "Filter by agent, kind or text" };
    private readonly CheckBox _notes = new() { Content = "Progress notes & reports only", Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Action<string> _open;
    private long _lastSeq;   // newest feed item already rendered
    private TeamState? _state, _rendered;
    private const int Max = 1500;

    public FeedPanel(Action<string> open)
    {
        _open = open;
        Background = Ui.Panel; BorderBrush = Ui.Line; BorderThickness = new Thickness(1);
        var dock = new DockPanel();
        var head = new StackPanel { Margin = new Thickness(14, 12, 14, 6) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Ui.Label("Live activity"));
        var live = new System.Windows.Shapes.Rectangle { Width = 6, Height = 6, Fill = Ui.Green, Margin = new Thickness(8, 0, 0, 4) };
        Ui.Breathe(live);
        title.Children.Add(live);
        head.Children.Add(title);
        head.Children.Add(Ui.T("Progress notes are written by the agents; tool activity is observed by Claude Code hooks.", 11, Ui.Faint, wrap: true));
        head.Children.Add(_filter);
        head.Children.Add(_notes);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        _list.ItemsSource = _items;
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        dock.Children.Add(_list);
        Child = dock;
        _filter.TextChanged += (_, _) => Rerender();
        _notes.Checked += (_, _) => Rerender();
        _notes.Unchecked += (_, _) => Rerender();
    }

    private bool Matches(FeedItem f)
    {
        if (_notes.IsChecked == true && f.Kind is not ("progress" or "report" or "research" or "delegate" or "message" or "task" or "error" or "blocked")) return false;
        var q = _filter.Text.Trim();
        return q.Length == 0 || f.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || f.AgentType.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Kind.Contains(q, StringComparison.OrdinalIgnoreCase) || (f.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Rerender() { _items.Clear(); _lastSeq = 0; if (_state is not null) Update(_state); }

    public void Update(TeamState s)
    {
        _state = s;
        if (!ReferenceEquals(s, _rendered)) { _items.Clear(); _lastSeq = 0; _rendered = s; }
        var fresh = s.Feed.Where(f => f.Seq > _lastSeq).ToList();
        if (fresh.Count > 0) _lastSeq = fresh[^1].Seq;
        fresh = fresh.Where(Matches).TakeLast(Max).ToList();
        foreach (var f in fresh) { var el = Item(f); _items.Insert(0, el); }
        while (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
        if (fresh.Count is > 0 and < 5 && _items.Count > 0) Ui.FadeIn(_items[0], 260);
    }

    private FrameworkElement Item(FeedItem f)
    {
        var g = new Grid { Margin = new Thickness(12, 6, 12, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Ui.T(Ui.Clock(f.Time), 11, Ui.Faint, font: Ui.Mono));
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new Border { Width = 3, Height = 11, Background = Ui.KindBrush(f.Kind), Margin = new Thickness(0, 1, 6, 0) });
        top.Children.Add(Ui.T(f.AgentType == "main" ? (_state?.NameOf(f.AgentId) ?? "main session") : f.AgentType, 11.5, Ui.KindBrush(f.Kind), FontWeights.SemiBold, Ui.Display));
        top.Children.Add(Ui.T("  " + f.Kind.ToUpperInvariant(), 10, Ui.Faint, font: Ui.Display));
        sp.Children.Add(top);
        sp.Children.Add(Ui.T(f.Text, 12.5, Ui.Fg, wrap: true));
        if (f.Detail is { Length: > 0 } d && f.Kind is "progress" or "error" or "blocked" or "test" or "report")
            sp.Children.Add(Ui.T(d.Length > 600 ? d[..600] + "…" : d, 11.5, Ui.Dim, wrap: true));
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        var b = new Border { Child = g, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = f.Detail, Background = Brushes.Transparent };
        b.MouseLeftButtonUp += (_, _) => _open(f.AgentId);
        return b;
    }
}
