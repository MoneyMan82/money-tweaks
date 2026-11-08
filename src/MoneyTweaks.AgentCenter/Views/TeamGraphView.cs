using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>
/// Team communication diagram: the coordinator in the centre, agents on rings by group. Lines are real delegations
/// (Agent tool calls) and messages (SendMessage); active ones flow, completed ones are dim, fresh ones pulse.
/// </summary>
public sealed class TeamGraphView : Border
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Action<string> _open;
    private readonly TextBlock _legend = Ui.T("", 11, Ui.Dim, wrap: true);
    private string _signature = "";
    private DateTimeOffset _lastDraw;

    public TeamGraphView(Action<string> open)
    {
        _open = open;
        var dock = new DockPanel();
        var head = new StackPanel { Margin = new Thickness(4, 4, 0, 6) };
        head.Children.Add(Ui.Head("Team communication"));
        head.Children.Add(_legend);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new Border { Background = Ui.Panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _canvas });
        Child = dock;
        _canvas.SizeChanged += (_, _) => _signature = "";
    }

    private sealed record Node(string Id, string Label, string Group, AgentInstance? Instance);

    public void Update(List<Lane> lanes, TeamState s, DateTimeOffset now)
    {
        // Nodes: one per lane (latest instance), plus main sessions that delegated.
        var nodes = new List<Node>();
        var instanceToNode = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lanes) nodes.Add(new Node("lane:" + l.Name, l.Name, l.Group, l.Instance));
        foreach (var a in s.Agents.Values)
        {
            var lane = a.IsMain ? (s.IsCoordinator(a) ? "chief-coordinator" : "Claude · your session") : a.Type;
            if (nodes.Any(n => n.Id == "lane:" + lane)) instanceToNode[a.Id] = "lane:" + lane;
        }
        var edges = s.Edges.Values
            .Select(e => (e, from: instanceToNode.GetValueOrDefault(e.From), to: instanceToNode.GetValueOrDefault(e.To)))
            .Where(x => x.from is not null && x.to is not null && x.from != x.to).ToList();
        var sig = string.Join(";", edges.Select(x => $"{x.from}>{x.to}:{x.e.Kind}:{x.e.Completed}:{x.e.Count}:{(now - x.e.LastActivity).TotalSeconds < 6}"))
                  + "|" + string.Join(",", nodes.Select(n => n.Instance?.Status ?? "-")) + "|" + _canvas.ActualWidth;
        if (sig == _signature && now - _lastDraw < TimeSpan.FromSeconds(5)) return;
        _signature = sig; _lastDraw = now;
        Draw(nodes, edges.Select(x => (x.e, x.from!, x.to!)).ToList(), s, now);
        var active = edges.Count(x => !x.e.Completed);
        _legend.Text = $"{edges.Count} connection(s) · {active} active · flowing line = work in progress, dim = finished, violet dashed = message. Only real Agent / SendMessage calls are drawn.";
    }

    private void Draw(List<Node> nodes, List<(Edge E, string From, string To)> edges, TeamState s, DateTimeOffset now)
    {
        _canvas.Children.Clear();
        double w = Math.Max(400, _canvas.ActualWidth), h = Math.Max(300, _canvas.ActualHeight);
        var center = new Point(w / 2, h / 2);
        var pos = new Dictionary<string, Point>();
        var lead = nodes.FirstOrDefault(n => n.Label == "chief-coordinator");
        if (lead is not null) pos[lead.Id] = center;
        var groups = new[] { "Discovery", "Building", "Verification", "Improvement", "Other agents" };
        var ring = nodes.Where(n => n != lead).OrderBy(n => Array.IndexOf(groups, n.Group) is var i && i < 0 ? 9 : i).ToList();
        double rx = w / 2 - 90, ry = h / 2 - 50;
        for (int i = 0; i < ring.Count; i++)
        {
            var ang = -Math.PI / 2 + 2 * Math.PI * i / Math.Max(1, ring.Count);
            var k = i % 2 == 0 ? 1.0 : 0.72;   // alternate between two rings so names don't overlap
            pos[ring[i].Id] = new Point(center.X + rx * k * Math.Cos(ang), center.Y + ry * k * Math.Sin(ang));
        }

        foreach (var (e, from, to) in edges)
        {
            var a = pos[from]; var b = pos[to];
            var fresh = now - e.LastActivity < TimeSpan.FromSeconds(6);
            var brush = e.Kind == "message" ? Ui.Violet : e.Completed ? Ui.LineBright : Ui.Accent;
            var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = e.Completed ? 1 : 2, Opacity = e.Completed ? 0.55 : 0.95 };
            if (e.Kind == "message" || !e.Completed)
            {
                line.StrokeDashArray = e.Kind == "message" ? new DoubleCollection { 2, 3 } : new DoubleCollection { 6, 4 };
                if (SystemParameters.ClientAreaAnimation)
                    line.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(20, 0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            _canvas.Children.Add(line);
            if (fresh && SystemParameters.ClientAreaAnimation)
            {
                // A packet travelling along the line for a just-sent task or message.
                var dot = new Rectangle { Width = 7, Height = 7, Fill = brush };
                _canvas.Children.Add(dot);
                var dur = TimeSpan.FromSeconds(1.1);
                dot.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(a.X - 3.5, b.X - 3.5, dur) { RepeatBehavior = new RepeatBehavior(3) });
                dot.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(a.Y - 3.5, b.Y - 3.5, dur) { RepeatBehavior = new RepeatBehavior(3) });
            }
            if (e.Label is { Length: > 0 } label && !e.Completed)
            {
                var t = Ui.T(label.Length > 42 ? label[..42] + "…" : label, 10, Ui.Dim);
                Canvas.SetLeft(t, (a.X + b.X) / 2 + 4); Canvas.SetTop(t, (a.Y + b.Y) / 2 - 14);
                _canvas.Children.Add(t);
            }
        }

        foreach (var n in nodes)
        {
            var p = pos[n.Id];
            var a = n.Instance;
            var status = a?.Status ?? AgentStatus.Idle;
            var isLead = n == lead;
            var waiting = a is not null && s.IsWaiting(a);
            var box = new StackPanel();
            box.Children.Add(Ui.T(n.Label, isLead ? 13 : 11.5, a is null ? Ui.Faint : Ui.Fg, FontWeights.SemiBold, Ui.Display));
            box.Children.Add(Ui.T(a is null ? "idle" : waiting ? "WAITING FOR REPLIES" : status, 9.5, waiting ? Ui.Amber : Ui.StatusBrush(status), FontWeights.SemiBold, Ui.Display));
            var border = new Border
            {
                Child = box, Background = isLead ? Ui.Panel2 : Ui.Bg, BorderBrush = a is null ? Ui.Line : Ui.StatusBrush(status),
                BorderThickness = new Thickness(isLead ? 2 : 1), Padding = new Thickness(8, 4, 8, 4), Cursor = a is null ? null : System.Windows.Input.Cursors.Hand,
                ToolTip = a is null ? n.Label + ": no activity yet" : $"{n.Label}\n{a.Task}\n{a.Summary}",
            };
            if (a is not null && AgentStatus.IsActive(status) && !a.IsStale(now)) Ui.Breathe(border, 0.6, 1.4);
            if (a is not null) border.MouseLeftButtonUp += (_, _) => _open(a.Id);
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(border, p.X - border.DesiredSize.Width / 2);
            Canvas.SetTop(border, p.Y - border.DesiredSize.Height / 2);
            _canvas.Children.Add(border);
        }
    }
}
