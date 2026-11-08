using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>Left column: every agent with a live status dot, grouped like team/AGENTS.md.</summary>
public sealed class RosterPanel : Border
{
    private readonly Action<Lane> _open;
    private readonly StackPanel _list = new();
    private readonly Dictionary<string, Row> _rows = new();
    private string _layout = "";

    private sealed class Row
    {
        public required Border Root;
        public required Rectangle Dot;
        public required TextBlock Name, Line, Time, Status;
        public string? LastStatus;
        public Lane Lane = null!;
    }

    public RosterPanel(Action<Lane> open)
    {
        _open = open;
        Background = Ui.Panel; BorderBrush = Ui.Line; BorderThickness = new Thickness(1);
        var dock = new DockPanel();
        var head = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
        head.Children.Add(Ui.Label("Team"));
        head.Children.Add(Ui.T("Click an agent to open its workspace.", 11, Ui.Faint));
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(Ui.Scroll(_list));
        Child = dock;
    }

    public void Update(List<Lane> lanes, TeamState s, DateTimeOffset now)
    {
        var layout = string.Join("|", lanes.Select(l => l.Group + ":" + l.Name));
        if (layout != _layout) { Rebuild(lanes); _layout = layout; }
        foreach (var l in lanes)
        {
            var r = _rows[l.Name];
            r.Lane = l;
            var a = l.Instance;
            var status = a is null ? AgentStatus.Idle : a.IsStale(now) ? a.Status : a.Status;
            if (r.LastStatus != status + (a?.IsStale(now) == true))
            {
                r.LastStatus = status + (a?.IsStale(now) == true);
                r.Dot.Fill = Ui.StatusBrush(status);
                r.Dot.BeginAnimation(OpacityProperty, null);
                if (a is not null && AgentStatus.IsActive(status) && !a.IsStale(now)) Ui.Breathe(r.Dot);
                r.Status.Text = a?.IsStale(now) == true ? status + " · QUIET" : status;
                r.Status.Foreground = Ui.StatusBrush(status);
            }
            r.Line.Text = a is null ? (l.Registered?.Model is { } m ? $"Not run yet · {m}" : "Not run yet")
                : a.CurrentTool is { } t && AgentStatus.IsActive(a.Status) ? $"{t.Tool}: {t.Target}" : a.Phase ?? a.Summary ?? a.Task ?? "";
            r.Time.Text = a is null ? "" : AgentStatus.IsActive(a.Status) ? Ui.Elapsed(a.Elapsed(now)) : Ui.Ago(a.LastSeen, now);
            r.Name.Foreground = a is null ? Ui.Dim : Ui.Fg;
        }
    }

    private void Rebuild(List<Lane> lanes)
    {
        _list.Children.Clear(); _rows.Clear();
        foreach (var grp in lanes.GroupBy(l => l.Group).OrderBy(g => Array.IndexOf(Lanes.GroupOrder, g.Key) is var i && i < 0 ? 99 : i))
        {
            _list.Children.Add(new TextBlock { Text = grp.Key.ToUpperInvariant(), FontFamily = Ui.Display, FontSize = 10.5, Foreground = Ui.Faint, Margin = new Thickness(14, 10, 0, 4) });
            foreach (var l in grp)
            {
                var dot = new Rectangle { Width = 7, Height = 7, Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                var name = Ui.T(l.Name, 13, Ui.Fg, FontWeights.SemiBold, Ui.Display);
                var status = Ui.T("", 10, Ui.Dim, FontWeights.SemiBold, Ui.Display);
                var line = Ui.T("", 11.5, Ui.Dim);
                var time = Ui.T("", 11, Ui.Faint, font: Ui.Mono);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(dot);
                var mid = new StackPanel();
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                top.Children.Add(name);
                status.Margin = new Thickness(8, 2, 0, 0);
                top.Children.Add(status);
                mid.Children.Add(top);
                mid.Children.Add(line);
                Grid.SetColumn(mid, 1); g.Children.Add(mid);
                Grid.SetColumn(time, 2); g.Children.Add(time);
                var root = new Border { Padding = new Thickness(14, 6, 12, 6), Child = g, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = l.Registered?.Description };
                var row = new Row { Root = root, Dot = dot, Name = name, Line = line, Time = time, Status = status, Lane = l };
                root.MouseEnter += (_, _) => root.Background = Ui.Panel2;
                root.MouseLeave += (_, _) => root.Background = Brushes.Transparent;
                root.MouseLeftButtonUp += (_, _) => _open(row.Lane);
                _rows[l.Name] = row;
                _list.Children.Add(root);
            }
        }
    }
}
