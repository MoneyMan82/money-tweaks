using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>
/// Service Nexus, inside the Services page: the real dependency graph from the Service Control Manager around one service
/// at a time. Left: what it depends on; right: what depends on it, and what depends on those. Click any node to move
/// there (animated), search, go back. The inspector shows the service's live state and, for a service Money Tweaks can
/// change, a before/after impact preview. Applying goes through the service's normal tweak (compatibility check,
/// Conflict Radar, admin, backup, read-back); Nexus never changes a service by itself.
/// </summary>
public sealed class ServiceNexusPanel : UserControl
{
    private const double NodeW = 156, NodeH = 42, FocusW = 210, FocusH = 58;
    private const int MaxRing = 16, MaxOuter = 14;

    private readonly Func<string, TweakItem?> _itemFor;
    private ServiceSnapshot? _snap;
    private string? _focus;
    private readonly Stack<string> _back = new();
    private ServiceImpact? _impact;

    private readonly Canvas _edges = new(), _nodesLayer = new();
    private readonly Grid _stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new();
    private readonly Dictionary<string, FrameworkElement> _nodeEls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _pos = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _search = new() { Padding = new Thickness(30, 8, 10, 8), Tag = "Search services…" };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Ui.Brush("Text") };
    private readonly StackPanel _inspector = new();
    private readonly TextBlock _status = new() { FontSize = 11.5, Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _backBtn;
    private string _scope = "tweaks";
    private Point? _dragFrom;

    public ServiceNexusPanel(Func<string, TweakItem?> itemFor)
    {
        _itemFor = itemFor;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        // ---- left: search and list ------------------------------------------------------------------------------------
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var searchBox = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        searchBox.Children.Add(_search);
        searchBox.Children.Add(new TextBlock { Style = Ui.Res<Style>("Icon"), Text = "\uE721", FontSize = 12, Foreground = Ui.Brush("Muted"), Margin = new Thickness(11, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(searchBox, Dock.Top);
        left.Children.Add(searchBox);
        var scope = new SegmentedPicker([("Tweakable", "tweaks", "Services Money Tweaks has a change for"), ("All", "all", "Every service on this PC"), ("Drivers", "drivers", "Kernel drivers services depend on")], "tweaks");
        scope.Changed += v => { _scope = v; FillList(); };
        scope.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(scope, Dock.Top);
        left.Children.Add(scope);
        _search.TextChanged += (_, _) => FillList();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter && _list.Items.Count > 0 && _list.Items[0] is ListBoxItem { Tag: string n }) Center(n); };
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: string n } && !n.Equals(_focus, StringComparison.OrdinalIgnoreCase)) Center(n); };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        left.Children.Add(_list);
        root.Children.Add(Ui.Card(left, new Thickness(0, 0, 12, 0)));

        // ---- centre: graph ---------------------------------------------------------------------------------------------
        var graphDock = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _backBtn = Ui.Btn("‹ Back", (_, _) => { if (_back.Count > 0) Center(_back.Pop(), remember: false); }, "GhostButton", new Thickness(0, 0, 6, 0));
        _backBtn.IsEnabled = false;
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_backBtn);
        tools.Children.Add(Ui.Btn("Fit", (_, _) => ResetView(animate: true), "GhostButton", new Thickness(0, 0, 6, 0)));
        tools.Children.Add(Ui.Btn("Refresh", async (_, _) => await LoadAsync(), "GhostButton", new Thickness(0, 0, 6, 0)));
        DockPanel.SetDock(tools, Dock.Left);
        bar.Children.Add(tools);
        bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top);
        graphDock.Children.Add(bar);
        var legend = Legend();
        DockPanel.SetDock(legend, Dock.Bottom);
        graphDock.Children.Add(legend);
        var world = new Grid { RenderTransform = new TransformGroup { Children = { _zoom, _pan } } };
        world.Children.Add(_edges);
        world.Children.Add(_nodesLayer);
        _stage.Children.Add(Backdrop());
        _stage.Children.Add(world);
        _stage.MinHeight = 520;
        _stage.SizeChanged += (_, _) => { _zoom.CenterX = _stage.ActualWidth / 2; _zoom.CenterY = _stage.ActualHeight / 2; if (_focus is not null) Layout(animate: false); };
        _stage.MouseWheel += (_, e) => { var f = e.Delta > 0 ? 1.12 : 1 / 1.12; var z = Math.Clamp(_zoom.ScaleX * f, 0.5, 2.2); _zoom.ScaleX = _zoom.ScaleY = z; e.Handled = true; };
        _stage.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _stage || e.OriginalSource is Canvas || e.OriginalSource is Rectangle) { _dragFrom = e.GetPosition(_stage); _stage.CaptureMouse(); } };
        _stage.MouseMove += (_, e) => { if (_dragFrom is { } p) { var q = e.GetPosition(_stage); _pan.X += q.X - p.X; _pan.Y += q.Y - p.Y; _dragFrom = q; } };
        _stage.MouseLeftButtonUp += (_, _) => { _dragFrom = null; _stage.ReleaseMouseCapture(); };
        graphDock.Children.Add(_stage);
        var graphCard = Ui.Hud(Ui.Card(graphDock, new Thickness(0)));
        Grid.SetColumn(graphCard, 1);
        root.Children.Add(graphCard);

        // ---- right: inspector ------------------------------------------------------------------------------------------
        var insp = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _inspector, Padding = new Thickness(0, 0, 4, 0) };
        var inspCard = Ui.Card(insp, new Thickness(12, 0, 0, 0));
        Grid.SetColumn(inspCard, 2);
        root.Children.Add(inspCard);
        Content = root;
        _inspector.Children.Add(Ui.Loading("Reading services from Windows…"));
    }

    public bool HasData => _snap is not null;

    /// <summary>Reads the Service Control Manager (background thread) and redraws, keeping the current service.</summary>
    public async Task LoadAsync()
    {
        _status.Text = "Reading the Service Control Manager…";
        try { _snap = await Task.Run(ServiceSnapshot.Read); }
        catch (Exception ex)
        {
            Log.Error("Service Nexus: reading services failed", ex);
            _inspector.Children.Clear();
            _inspector.Children.Add(Ui.Muted("Windows didn't return the service list: " + ex.Message));
            _status.Text = "Couldn't read services.";
            return;
        }
        int services = _snap.Nodes.Values.Count(n => !n.IsDriver), edges = _snap.Nodes.Values.Sum(n => n.DependsOn.Count);
        _status.Text = $"{services} services · {_snap.Nodes.Count - services} drivers · {edges} declared dependencies · read {_snap.TakenAt:T}";
        FillList();
        var start = _focus is { } f && _snap.Get(f) is not null ? f
            : ServiceNexus.Tweakable.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(s => _snap.Get(s)?.Dependents.Count > 0)
              ?? _snap.Nodes.Keys.First();
        Center(start, remember: false, animate: _focus is null);
    }

    // ---- list ------------------------------------------------------------------------------------------------------------
    private void FillList()
    {
        if (_snap is null) return;
        var q = _search.Text.Trim();
        var tweakable = ServiceNexus.Tweakable;
        var rows = _snap.Nodes.Values
            .Where(n => _scope switch { "drivers" => n.IsDriver, "all" => !n.IsDriver, _ => tweakable.Contains(n.Name) })
            .Where(n => q.Length == 0 || n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || n.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(400).ToList();
        _list.Items.Clear();
        foreach (var n in rows)
        {
            var sp = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var dot = new Rectangle { Width = 7, Height = 7, Fill = StateBrush(n), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(45, 3.5, 3.5) };
            sp.Children.Add(dot);
            var tx = new StackPanel();
            tx.Children.Add(new TextBlock { Text = n.DisplayName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("Text") });
            tx.Children.Add(new TextBlock { Text = $"{n.Name} · {n.StartText}", FontSize = 10.5, Foreground = Ui.Brush("Subtle"), TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(tx);
            _list.Items.Add(new ListBoxItem { Content = sp, Tag = n.Name, Padding = new Thickness(6, 4, 6, 4), ToolTip = n.Name });
        }
        if (rows.Count == 0) _list.Items.Add(new ListBoxItem { Content = Ui.Muted("No match.", 12), IsEnabled = false });
    }

    // ---- focus and layout ------------------------------------------------------------------------------------------------
    public void Center(string name, bool remember = true, bool animate = true)
    {
        if (_snap?.Get(name) is null) return;
        if (remember && _focus is not null && !_focus.Equals(name, StringComparison.OrdinalIgnoreCase)) _back.Push(_focus);
        _backBtn.IsEnabled = _back.Count > 0;
        _focus = _snap.Get(name)!.Name;
        _impact = null;
        Layout(animate);
        RenderInspector();
    }

    /// <summary>Scales the graph so every node is visible (never above 100 %) and centres it.</summary>
    private void ResetView(bool animate = false)
    {
        double w = Math.Max(_stage.ActualWidth, 300), h = Math.Max(_stage.ActualHeight, 300);
        double z = Math.Clamp(Math.Min((w - 24) / Math.Max(1, _content.Width), (h - 24) / Math.Max(1, _content.Height)), 0.42, 1);
        _pan.X = _pan.Y = 0;
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (animate && !App.ReduceMotion && Math.Abs(_zoom.ScaleX - z) > 0.01)
        {
            var d = TimeSpan.FromMilliseconds(420);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var from = _zoom.ScaleX;
            _zoom.ScaleX = _zoom.ScaleY = z;
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, z, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, z, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
        else _zoom.ScaleX = _zoom.ScaleY = z;
    }

    private Size _content = new(1, 1);
    private readonly Dictionary<string, string> _roles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Angular column layout, read left to right: [what its dependencies need] · what it needs · the service · what needs
    /// it · what needs those (the impact chain). Columns are centred on the stage; the view zooms to fit.
    /// </summary>
    private Dictionary<string, (Point p, string role)> Positions()
    {
        var s = _snap!;
        var f = s.Get(_focus!)!;
        double w = Math.Max(_stage.ActualWidth, 400), h = Math.Max(_stage.ActualHeight, 400);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { f.Name };
        List<string> Take(IEnumerable<string> names, int max, string role)
        {
            var list = names.Where(n => s.Get(n) is not null && placed.Add(n)).OrderBy(n => s.Get(n)!.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            return Cap(list, max, role);
        }
        var deps = Take(f.DependsOn, MaxRing, "dep");
        var dents = Take(f.Dependents, MaxRing, "dent");
        var outer = Take(f.Dependents.SelectMany(d => s.Get(d)?.Dependents ?? []), MaxOuter, "outer");
        var depout = dents.Count == 0 ? Take(f.DependsOn.SelectMany(d => s.Get(d)?.DependsOn ?? []), MaxOuter, "depout") : [];

        var cols = new List<(List<string> names, string role)>();
        if (depout.Count > 0) cols.Add((depout, "depout"));
        if (deps.Count > 0) cols.Add((deps, "dep"));
        cols.Add(([f.Name], "focus"));
        if (dents.Count > 0) cols.Add((dents, "dent"));
        if (outer.Count > 0) cols.Add((outer, "outer"));
        const double step = NodeW + 70, row = NodeH + 12;
        double total = (cols.Count - 1) * step;
        int tallest = cols.Max(c => c.names.Count);
        _content = new Size(total + FocusW + 20, Math.Max(FocusH, tallest * row) + 20);
        var map = new Dictionary<string, (Point, string)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cols.Count; i++)
        {
            var (names, role) = cols[i];
            double x = w / 2 - total / 2 + i * step;
            for (int j = 0; j < names.Count; j++)
                map[names[j]] = (new Point(x, h / 2 + (j - (names.Count - 1) / 2.0) * row), role);
        }
        _roles.Clear();
        foreach (var (k, v) in map) _roles[k] = v.Item2;
        return map;
    }

    private double HalfWidth(string name) => (_roles.GetValueOrDefault(name) == "focus" ? FocusW : NodeW) / 2;

    /// <summary>At most <paramref name="max"/> names; the rest become one "+N more" node (listed in the inspector).</summary>
    private static List<string> Cap(List<string> names, int max, string role) =>
        names.Count <= max ? names : [.. names.Take(max - 1), $"+{names.Count - (max - 1)}|{role}"];

    private void Layout(bool animate)
    {
        if (_snap is null || _focus is null) return;
        var target = Positions();
        bool motion = animate && !App.ReduceMotion;
        var from = _focus is { } f && _pos.TryGetValue(f, out var fp) ? fp : new Point(_stage.ActualWidth / 2, _stage.ActualHeight / 2);

        foreach (var gone in _nodeEls.Keys.Where(k => !target.ContainsKey(k)).ToList())
        {
            var el = _nodeEls[gone];
            _nodeEls.Remove(gone);
            _pos.Remove(gone);
            if (!motion) { _nodesLayer.Children.Remove(el); continue; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => _nodesLayer.Children.Remove(el);
            el.BeginAnimation(OpacityProperty, fade);
        }
        _edges.Children.Clear();
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        int k = 0;
        foreach (var (name, (p, role)) in target)
        {
            // Rebuilt every time: the focus node is bigger and roles change as you move around.
            bool existed = _nodeEls.Remove(name, out var old);
            var start = existed && _pos.TryGetValue(name, out var op) ? op : from;
            if (old is not null) _nodesLayer.Children.Remove(old);
            var el = Node(name, role);
            _nodeEls[name] = el;
            _pos[name] = p;
            _nodesLayer.Children.Add(el);
            var tt = (TranslateTransform)el.RenderTransform;
            double w = role == "focus" ? FocusW : NodeW, h = role == "focus" ? FocusH : NodeH;
            if (motion)
            {
                var d = TimeSpan.FromMilliseconds(420);
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(start.X - w / 2, p.X - w / 2, d) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(start.Y - h / 2, p.Y - h / 2, d) { EasingFunction = ease });
                // New nodes fade in with a slight stagger, so a column reads as it arrives.
                if (!existed) el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(60 + Math.Min(k++, 20) * 12) });
            }
            else { tt.X = p.X - w / 2; tt.Y = p.Y - h / 2; }
        }
        ResetView(motion);
        if (motion)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
            var focusAt = _focus;
            t.Tick += (_, _) => { t.Stop(); if (_focus == focusAt) DrawEdges(true); };
            t.Start();
        }
        else DrawEdges(false);
    }

    private void DrawEdges(bool fadeIn)
    {
        _edges.Children.Clear();
        if (_snap is null || _focus is null) return;
        var affected = _impact?.Effects.Where(e => e.Level >= ImpactLevel.Possible).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var (name, p) in _pos)
        {
            if (_snap.Get(name) is not { } n) continue;
            foreach (var dep in n.DependsOn)
            {
                if (!_pos.TryGetValue(dep, out var q)) continue;
                bool hot = _impact is not null && affected.Contains(name) && (dep.Equals(_impact.Target.Name, StringComparison.OrdinalIgnoreCase) || affected.Contains(dep));
                bool toFocus = name.Equals(_focus, StringComparison.OrdinalIgnoreCase) || dep.Equals(_focus, StringComparison.OrdinalIgnoreCase);
                _edges.Children.Add(Edge(p, HalfWidth(name), q, HalfWidth(dep), hot, toFocus, fadeIn));
            }
        }
        // "+N more" nodes connect to the focus.
        foreach (var (name, p) in _pos.Where(x => x.Key.StartsWith('+')))
            _edges.Children.Add(Edge(p, NodeW / 2, _pos[_focus], FocusW / 2, false, false, fadeIn, dashed: true));
    }

    /// <summary>
    /// A link from a service to one it depends on: leaves and enters nodes horizontally (S-curve) and ends in a small
    /// angular arrowhead at the dependency. Services in the same column are joined by a bracket out to the side.
    /// </summary>
    private static UIElement Edge(Point from, double fromHalf, Point to, double toHalf, bool hot, bool toFocus, bool fadeIn, bool dashed = false)
    {
        double dir = to.X >= from.X ? 1 : -1;
        Point a, b, c1, c2;
        if (Math.Abs(to.X - from.X) < 4)
        {
            a = new Point(from.X + fromHalf, from.Y); b = new Point(to.X + toHalf + 6, to.Y);
            double bulge = 36 + Math.Min(60, Math.Abs(to.Y - from.Y) * 0.15);
            c1 = new Point(a.X + bulge, a.Y); c2 = new Point(b.X + bulge, b.Y);
        }
        else
        {
            a = new Point(from.X + dir * fromHalf, from.Y); b = new Point(to.X - dir * (toHalf + 6), to.Y);
            double dx = Math.Abs(b.X - a.X) * 0.5;
            c1 = new Point(a.X + dir * dx, a.Y); c2 = new Point(b.X - dir * dx, b.Y);
        }
        var fig = new PathFigure { StartPoint = a, Segments = { new BezierSegment(c1, c2, b, true) } };
        var color = hot ? Ui.Brush("Danger") : toFocus ? Ui.Brush("Accent") : Ui.Brush("Violet");
        var path = new Path { Data = new PathGeometry([fig]), Stroke = color, StrokeThickness = hot ? 2 : 1.3, Opacity = hot ? 0.95 : toFocus ? 0.6 : 0.3, IsHitTestVisible = false };
        if (dashed) path.StrokeDashArray = [3, 4];
        if (hot)
        {
            path.StrokeDashArray = [6, 4];
            if (!App.ReduceMotion) path.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        var ax = b.X - c2.X; var ay = b.Y - c2.Y; var al = Math.Max(1, Math.Sqrt(ax * ax + ay * ay)); ax /= al; ay /= al;
        var tip = new Point(b.X + ax * 6, b.Y + ay * 6);
        var head = new Polygon
        {
            Points = [tip, new Point(tip.X - ax * 8 - ay * 4, tip.Y - ay * 8 + ax * 4), new Point(tip.X - ax * 8 + ay * 4, tip.Y - ay * 8 - ax * 4)],
            Fill = color, Opacity = path.Opacity, IsHitTestVisible = false,
        };
        var g = new Canvas { IsHitTestVisible = false };
        g.Children.Add(path);
        if (!dashed) g.Children.Add(head);
        if (fadeIn && !App.ReduceMotion) g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        return g;
    }

    /// <summary>An angular node: chamfered corners, state-coloured rim, gold notch when Money Tweaks has a change for it.</summary>
    private FrameworkElement Node(string name, string role)
    {
        bool focus = role == "focus";
        double w = focus ? FocusW : NodeW, h = focus ? FocusH : NodeH;
        var grid = new Grid { Width = w, Height = h, RenderTransform = new TranslateTransform(), Cursor = Cursors.Hand };
        if (name.StartsWith('+'))
        {
            var parts = name.Split('|');
            grid.Children.Add(new Path { Data = Chamfer(w, h, 9), Stroke = Ui.Brush("Subtle"), StrokeThickness = 1, StrokeDashArray = [3, 3], Fill = Ui.Brush("Surface") });
            grid.Children.Add(new TextBlock { Text = parts[0] + " more", FontSize = 12, Foreground = Ui.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            grid.ToolTip = "More services than fit here; they're listed in the inspector.";
            return grid;
        }
        var n = _snap!.Get(name)!;
        var affected = _impact?.Effects.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.Level >= ImpactLevel.Possible);
        bool isTarget = _impact is not null && focus;
        var rim = isTarget ? Ui.Brush("Gold") : affected is not null ? Ui.Brush(affected.Level == ImpactLevel.Likely ? "Danger" : "Gold") : focus ? Ui.Brush("AccentGradient") : StateBrush(n);
        var body = new Path { Data = Chamfer(w, h, focus ? 12 : 9), Stroke = rim, StrokeThickness = focus || affected is not null ? 1.8 : 1.1, Fill = Ui.Brush(focus ? "CardHover" : "Card") };
        if (focus || affected is not null)
            body.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = ((rim as SolidColorBrush)?.Color ?? Color.FromRgb(0x8A, 0x6B, 0xFF)), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
        grid.Children.Add(body);
        // State bar on the left edge.
        grid.Children.Add(new Rectangle { Width = 3, Margin = new Thickness(5, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, Fill = StateBrush(n) });
        if (ServiceNexus.TweakFor(n.Name) is not null)
            grid.Children.Add(new Polygon { Points = [new Point(w - 16, 0), new Point(w - 4, 0), new Point(w - 4, 12)], Fill = Ui.Brush("Gold"), ToolTip = "Money Tweaks has a change for this service" });
        var text = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = n.DisplayName, FontSize = focus ? 13.5 : 11.5, FontWeight = focus ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{n.Name} · {(n.Running ? "running" : n.State.ToLowerInvariant())}", FontSize = focus ? 11 : 9.5, Foreground = Ui.Brush("Subtle"), TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(text);
        grid.ToolTip = $"{n.DisplayName} ({n.Name})\n{n.State}, {n.StartText}{(n.IsDriver ? ", driver" : "")}\nDepends on {n.DependsOn.Count} · needed by {n.Dependents.Count}" +
            (affected is not null ? $"\nIf the change is applied: {affected.After}" : "") + (focus ? "" : "\nClick to centre it");
        grid.MouseEnter += (_, _) => body.StrokeThickness += 0.8;
        grid.MouseLeave += (_, _) => body.StrokeThickness -= 0.8;
        grid.MouseLeftButtonUp += (_, e) => { if (!focus) { Center(n.Name); e.Handled = true; } };
        return grid;
    }

    private static Geometry Chamfer(double w, double h, double c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c, 0), true, true);
            ctx.PolyLineTo([new Point(w, 0), new Point(w, h - c), new Point(w - c, h), new Point(0, h), new Point(0, c)], true, true);
        }
        g.Freeze();
        return g;
    }

    private static Brush StateBrush(ServiceNode n) =>
        n.Disabled ? Ui.Brush("Danger") : n.Running ? Ui.Brush("Success") : Ui.Brush("Subtle");

    private static UIElement Backdrop()
    {
        // Faint angular grid behind the graph; static, drawn once.
        var b = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 36, 36), ViewportUnits = BrushMappingMode.Absolute, Opacity = 0.35,
            Drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1), new RectangleGeometry(new Rect(0, 0, 36, 36))),
        };
        b.Freeze();
        return new Rectangle { Fill = b };
    }

    private static UIElement Legend()
    {
        var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Item(string brush, string text) { w.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = Ui.Brush(brush), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(45, 4, 4) }); w.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 0, 16, 0) }); }
        Item("Success", "running"); Item("Subtle", "stopped"); Item("Danger", "disabled"); Item("Gold", "Money Tweaks has a change");
        w.Children.Add(new TextBlock { Text = "Left: what it needs · Right: what needs it · Arrows point to the dependency · Wheel zooms, drag pans", FontSize = 11, Foreground = Ui.Brush("Subtle") });
        return w;
    }

    // ---- inspector -------------------------------------------------------------------------------------------------------
    private void RenderInspector()
    {
        _inspector.Children.Clear();
        if (_snap?.Get(_focus ?? "") is not { } n) return;
        _inspector.Children.Add(new TextBlock { Text = n.IsDriver ? "DRIVER" : "SERVICE", Style = Ui.Res<Style>("Eyebrow") });
        _inspector.Children.Add(new TextBlock { Text = n.DisplayName, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text") });
        _inspector.Children.Add(new TextBlock { Text = n.Name, FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 2, 0, 8) });
        var tags = new WrapPanel();
        tags.Children.Add(Ui.Tag(n.Running ? "Running" : n.State, n.Running ? "Success" : "Muted"));
        tags.Children.Add(Ui.Tag(n.StartText, n.Disabled ? "Danger" : "Muted"));
        if (ServiceNexus.Critical(n.Name) is not null) tags.Children.Add(Ui.Tag("Core Windows", "Gold"));
        _inspector.Children.Add(tags);
        if (ServiceNexus.Critical(n.Name) is { } crit) _inspector.Children.Add(Ui.Muted("Core Windows function: " + crit + ".", 11.5));
        if (!string.IsNullOrWhiteSpace(n.Description)) _inspector.Children.Add(new TextBlock { Text = n.Description, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        if (n.Image is { } img) _inspector.Children.Add(new TextBlock { Text = img, FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 10.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        _inspector.Children.Add(ChangeCard(n));
        if (_impact is not null) _inspector.Children.Add(ImpactView(_impact));

        Links($"DEPENDS ON ({n.DependsOn.Count})", n.DependsOn, "Windows starts these first. Declared by this service.");
        Links($"NEEDED BY ({n.Dependents.Count})", n.Dependents, "These declare this service as a dependency.");
        if (n.Groups.Count > 0) _inspector.Children.Add(Ui.Muted("Also waits for load-order group(s): " + string.Join(", ", n.Groups), 11.5));
    }

    private void Links(string title, IReadOnlyList<string> names, string sub)
    {
        _inspector.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 14, 0, 2) });
        _inspector.Children.Add(Ui.Muted(names.Count == 0 ? "None declared." : sub, 11));
        foreach (var d in names)
        {
            var dn = _snap!.Get(d);
            var b = new Button { Style = Ui.Res<Style>("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 2, 0, 0), IsEnabled = dn is not null };
            var row = new DockPanel();
            row.Children.Add(new Rectangle { Width = 6, Height = 6, Fill = dn is null ? Ui.Brush("Subtle") : StateBrush(dn), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = dn is null ? $"{d} (not installed)" : $"{dn.DisplayName}  ·  {dn.Name}", FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            b.Content = row;
            b.Click += (_, _) => Center(d);
            _inspector.Children.Add(b);
        }
    }

    /// <summary>What Money Tweaks would change here, and the buttons to preview, apply (through the tweak) or undo.</summary>
    private UIElement ChangeCard(ServiceNode n)
    {
        var box = new StackPanel();
        var border = new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), Background = Ui.Brush("Surface"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 0), Child = box };
        box.Children.Add(new TextBlock { Text = "PROPOSED CHANGE", Style = Ui.Res<Style>("Eyebrow") });
        var tweak = ServiceNexus.TweakFor(n.Name);
        var item = tweak is null ? null : _itemFor(tweak.Id);
        if (tweak is null || item is null)
        {
            box.Children.Add(Ui.Muted("Money Tweaks has no change for this service, so Nexus only shows it. You can still preview what disabling it would reach.", 11.5));
            box.Children.Add(Ui.Row(Ui.Btn("What if it were disabled?", (_, _) => Preview(4), "GhostButton")));
            return border;
        }
        var op = tweak.Ops.OfType<ServiceStartOp>().First();
        box.Children.Add(new TextBlock { Text = tweak.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 2, 0, 2) });
        var tags = new WrapPanel();
        tags.Children.Add(Ui.Tag(tweak.Risk == Risk.Caution ? "Caution" : "Safe", tweak.Risk == Risk.Caution ? "Gold" : "Accent"));
        if (item.State == TweakState.Unknown) _ = ReadStateAsync(item);
        tags.Children.Add(Ui.Tag(item.State == TweakState.NotApplied ? "Not applied" : item.StatusText, item.State == TweakState.Applied ? "Success" : "Muted"));
        if (Core.Engine.BackupStore.Exists(tweak.Id)) tags.Children.Add(Ui.Tag("Backup saved", "Violet"));
        box.Children.Add(tags);
        if (n.Start != op.Start) box.Children.Add(Ui.Muted($"{n.StartText} → {ServiceStartOp.StartName(op.Start)}", 11.5));
        if (item.Message is { Length: > 0 } msg) box.Children.Add(new TextBlock { Text = msg, FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (item.State is TweakState.Applied or TweakState.Partial)
        {
            bool backup = Core.Engine.BackupStore.Exists(tweak.Id);
            box.Children.Add(Ui.Muted(backup
                ? "Applied by Money Tweaks. Undo restores the start type saved in its backup and reads it back."
                : $"Already {ServiceStartOp.StartName(op.Start)} on this PC, but Money Tweaks has no backup of an earlier value (Windows or another tool set it). Undo would use the usual Windows default, Manual.", 11.5));
            box.Children.Add(Ui.Row(Ui.Btn(backup ? "Undo (restore backup)" : "Undo (Windows default)", async (_, _) => await RunAsync(item, apply: false), "GhostButton")));
        }
        else
        {
            var row = Ui.Row(Ui.Btn("Preview impact", (_, _) => Preview(op.Start), "GhostButton"));
            if (_impact is not null && _impact.ProposedStart == op.Start)
                row.Children.Add(Ui.Btn("Apply…", async (_, _) => await RunAsync(item, apply: true), tweak.Risk == Risk.Caution || _impact.Affected > 0 ? "GoldButton" : "PrimaryButton"));
            else row.Children.Add(new TextBlock { Text = "Preview first to apply", FontSize = 11, Foreground = Ui.Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(row);
        }
        return border;
    }

    /// <summary>The list reads tweak states in the background; Nexus may get there first.</summary>
    private async Task ReadStateAsync(TweakItem item)
    {
        var state = await Task.Run(() => TweakEngine.GetState(item.Tweak));
        if (item.State != TweakState.Unknown) return;
        item.State = state;
        RenderInspector();
    }

    private void Preview(int start)
    {
        if (_snap is null || _focus is null) return;
        _impact = ServiceNexus.Impact(_snap, _focus, start);
        Log.Info($"Service Nexus: previewed {_focus} → {ServiceStartOp.StartName(start)}: {_impact.Affected} dependent(s) could be affected{(_impact.TouchesCritical ? ", reaches core Windows functions" : "")}");
        Layout(animate: false);
        RenderInspector();
    }

    private UIElement ImpactView(ServiceImpact i)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(new TextBlock { Text = "IMPACT PREVIEW", Style = Ui.Res<Style>("Eyebrow") });
        sp.Children.Add(Ui.Muted($"From the Service Control Manager snapshot taken {_snap!.TakenAt:T}. Nothing has changed yet.", 11));
        var table = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Cell(string text, int r, int c, string brush, bool bold = false)
        {
            while (table.RowDefinitions.Count <= r) table.RowDefinitions.Add(new RowDefinition());
            var t = new TextBlock { Text = text, FontSize = 11.5, Foreground = Ui.Brush(brush), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 2) };
            Grid.SetRow(t, r); Grid.SetColumn(t, c); table.Children.Add(t);
        }
        Cell("BEFORE", 0, 0, "Subtle", true); Cell("AFTER", 0, 1, "Subtle", true);
        Cell($"{i.Target.DisplayName}: {i.Before}", 1, 0, "Text"); Cell(i.After, 1, 1, "Gold");
        sp.Children.Add(table);

        foreach (var w in i.Warnings)
            sp.Children.Add(new Border { BorderBrush = Ui.Brush(i.TouchesCritical ? "Danger" : "Gold"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock { Text = w, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text") } });

        if (i.Effects.Count > 0)
        {
            sp.Children.Add(new TextBlock { Text = $"SERVICES THAT DEPEND ON IT ({i.Effects.Count})", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 10, 0, 2) });
            foreach (var e in i.Effects.OrderByDescending(e => e.Level).ThenBy(e => e.Depth).Take(40))
            {
                var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                var head = new WrapPanel();
                head.Children.Add(Ui.Tag(e.Level switch { ImpactLevel.Likely => "Likely affected", ImpactLevel.Possible => "Possibly affected", _ => "Not affected" },
                    e.Level switch { ImpactLevel.Likely => "Danger", ImpactLevel.Possible => "Gold", _ => "Muted" }));
                if (e.CriticalFunction is not null) head.Children.Add(Ui.Tag("Core Windows", "Gold"));
                head.Children.Add(new TextBlock { Text = e.DisplayName + (e.Depth > 1 ? $"  (level {e.Depth})" : ""), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(head);
                row.Children.Add(new TextBlock { Text = $"{e.Now}  →  {e.After}", FontSize = 11, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = "Evidence: " + e.Evidence + (e.CriticalFunction is { } cf ? $" Used for {cf}." : ""), FontSize = 10.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(row);
            }
            if (i.Effects.Count > 40) sp.Children.Add(Ui.Muted($"…and {i.Effects.Count - 40} more further down the chain.", 11));
        }
        sp.Children.Add(new TextBlock { Text = "WHAT THIS DOESN'T PROVE", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 10, 0, 2) });
        foreach (var u in i.Uncertainty) sp.Children.Add(new TextBlock { Text = "• " + u, FontSize = 11, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    /// <summary>
    /// Confirms with the impact in front of the user, then runs the service's own tweak through TweakService (its
    /// compatibility check, Conflict Radar, admin prompt, backup and read-back), and re-reads the services.
    /// </summary>
    private async Task RunAsync(TweakItem item, bool apply)
    {
        if (apply && _impact is { } i)
        {
            var text = $"{i.Target.DisplayName}: {i.Before} → {i.After}\n\n" +
                (i.Warnings.Count == 0 ? "No declared dependents would be affected.\n" : string.Join("\n", i.Warnings.Select(w => "• " + w)) + "\n") +
                string.Join("\n", i.Effects.Where(e => e.Level >= ImpactLevel.Possible).Take(12).Select(e => $"  – {e.DisplayName}: {e.After}")) +
                "\n\nDeclared dependencies are evidence, not proof: some of these may never be needed on this PC, and undeclared ones can't be seen." +
                "\nThe current start type is backed up first; Undo puts it back and reads it back.";
            if (!Dialog.Confirm($"Apply “{item.Tweak.Name}”?", text, "Continue", i.Affected > 0 || i.TouchesCritical ? DialogKind.Warning : DialogKind.Confirm)) return;
        }
        await TweakService.ToggleAsync(item);
        await LoadAsync();
        RenderInspector();
    }
}
