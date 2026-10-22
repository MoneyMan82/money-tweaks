using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MoneyTweaks.Controls;

/// <summary>Small helpers so code-built pages share the same look as the XAML ones.</summary>
public static class Ui
{
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];
    public static Brush Brush(string key) => Res<Brush>(key);

    /// <summary>Every visual descendant of a given type (depth-first).</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    /// <summary>
    /// Standard page header (the "Vector" look): an icon tile with a violet→cyan rim, a cyan eyebrow and the title in
    /// the display face, actions on the right, then a hairline rule with a short signature segment, and the blurb.
    /// </summary>
    public static (ScrollViewer root, StackPanel body) Page(string title, string blurb, UIElement? headerRight = null, string? eyebrow = null, string? icon = null)
    {
        var body = new StackPanel { Margin = new Thickness(32, 8, 32, 36) };
        var head = new DockPanel { LastChildFill = true };
        if (headerRight is not null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            if (headerRight is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
            head.Children.Add(headerRight);
        }
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null) titleRow.Children.Add(IconTile(icon, 46, 19));
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (eyebrow is not null) titles.Children.Add(new TextBlock { Text = eyebrow.ToUpperInvariant(), Style = Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 1) });
        titles.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H1") });
        titleRow.Children.Add(titles);
        head.Children.Add(titleRow);
        var t = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        t.Children.Add(head);
        t.Children.Add(Rule(new Thickness(0, 16, 0, 12)));
        t.Children.Add(new TextBlock { Text = blurb, Style = Res<Style>("MutedText"), FontSize = 13.5, LineHeight = 21, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(t);
        return (new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly }, body);
    }

    /// <summary>Square glyph tile: dark glass with a violet→cyan rim and a gradient glyph. Used by page headers and empty states.</summary>
    public static Border IconTile(string icon, double size = 46, double glyph = 19) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = TileFill, BorderBrush = Brush("AccentGradient"), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = icon, Style = Res<Style>("Icon"), FontSize = glyph, Foreground = Brush("AccentGradient"), HorizontalAlignment = HorizontalAlignment.Center },
    };

    private static readonly Brush TileFill = Frozen(new LinearGradientBrush(Color.FromRgb(0x1B, 0x18, 0x33), Color.FromRgb(0x0E, 0x16, 0x1C), 45));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Hairline divider with a short violet→cyan segment at its start (the signature rule).</summary>
    public static UIElement Rule(Thickness margin)
    {
        var g = new Grid { Height = 2, Margin = margin, ClipToBounds = true };
        g.Children.Add(new Border { Height = 1, Background = Brush("Hairline"), VerticalAlignment = VerticalAlignment.Center });
        var seg = new ScaleTransform(1, 1);
        g.Children.Add(new Border { Width = 64, Height = 2, CornerRadius = new CornerRadius(0), Background = Brush("AccentGradientH"), HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = seg });
        // A light glint that travels along the hairline once each time the page appears.
        var glintX = new TranslateTransform(-160, 0);
        g.Children.Add(new Border { Width = 160, Height = 1, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Background = Glint, RenderTransform = glintX, IsHitTestVisible = false });
        g.Loaded += (_, _) =>
        {
            if (!Motion.Enabled) return;
            seg.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            glintX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-160, Math.Max(400, g.ActualWidth), TimeSpan.FromMilliseconds(1300)) { BeginTime = TimeSpan.FromMilliseconds(260), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        };
        return g;
    }

    private static readonly Brush Glint = Frozen(new LinearGradientBrush(new GradientStopCollection
        { new(Color.FromArgb(0, 0x2F, 0xD8, 0xEE), 0), new(Color.FromArgb(0xC0, 0x9E, 0xEE, 0xFF), 0.5), new(Color.FromArgb(0, 0x8A, 0x6B, 0xFF), 1) }, 0));

    /// <summary>Scrolls the page's ScrollViewer so the element sits at the top (used for "page#section" navigation).</summary>
    public static void ScrollTo(FrameworkElement el)
    {
        DependencyObject? d = VisualTreeHelper.GetParent(el);
        while (d is not null and not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        if (d is not ScrollViewer sv) { el.BringIntoView(); return; }
        sv.UpdateLayout(); // a page opened by this same navigation may not have been measured yet
        var p = el.TransformToAncestor(sv).Transform(new Point(0, 0));
        sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + p.Y - 8));
    }

    public static Border Card(UIElement child, Thickness? margin = null) =>
        new() { Style = Res<Style>("CardBorder"), Child = child, Margin = margin ?? new Thickness(0, 0, 0, 14) };

    /// <summary>
    /// Feature panel: a card with cyan corner brackets (the "command center" frame). For a page's main instrument only
    /// (dashboard hero, the Reality Lab model, scanner readout), never for every card.
    /// </summary>
    public static Grid Hud(Border card)
    {
        var g = new Grid { Margin = card.Margin };
        card.Margin = new Thickness(0);
        g.Children.Add(card);
        g.Children.Add(new HudCorners { IsHitTestVisible = false });
        return g;
    }

    /// <summary>A card that lights up its rim on hover and runs <paramref name="click"/>.</summary>
    public static Border ClickCard(UIElement child, Action click, Thickness? margin = null)
    {
        var c = Card(child, margin);
        c.Cursor = System.Windows.Input.Cursors.Hand;
        c.MouseEnter += (_, _) => { c.BorderBrush = Brush("CardRimHover"); c.Background = Brush("CardHover"); };
        c.MouseLeave += (_, _) => { c.BorderBrush = Brush("CardRim"); c.Background = Brush("CardFill"); };
        c.MouseLeftButtonUp += (_, _) => click();
        Motion.HoverLift(c);
        return c;
    }

    /// <summary>Section heading with the signature marker (a short violet→cyan bar), optional description below.</summary>
    public static StackPanel Section(string title, string? sub = null)
    {
        var s = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border { Width = 3, Height = 15, CornerRadius = new CornerRadius(0), Background = Brush("AccentGradient"), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H2"), VerticalAlignment = VerticalAlignment.Center });
        s.Children.Add(head);
        if (sub is not null) s.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("MutedText"), Margin = new Thickness(0, 4, 0, 12) });
        else s.Children.Add(new Border { Height = 12 });
        return s;
    }

    public static Button Btn(string text, RoutedEventHandler click, string style = "BaseButton", Thickness? margin = null)
    {
        var b = new Button { Content = text, Style = Res<Style>(style), Margin = margin ?? new Thickness(0, 0, 8, 8) };
        b.Click += click;
        return b;
    }

    /// <summary>
    /// A page's video tutorial: the embedded player card, and a "Watch tutorial" header button that scrolls to it and starts
    /// it. Both null when the page has no video.
    /// </summary>
    public static (Button? button, Border? card) Tutorial(string pageTag)
    {
        if (Core.Model.Tutorials.For(pageTag) is not { } t) return (null, null);
        var video = new TutorialVideo(t);
        var card = Card(video);
        var b = Btn("▶  Watch tutorial", (_, _) => { ScrollTo(card); video.Play(); });
        b.ToolTip = "Plays the video tutorial on this page";
        return (b, card);
    }

    public static WrapPanel Row(params UIElement[] children)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var c in children) w.Children.Add(c);
        return w;
    }

    public static TextBlock Muted(string text, double size = 12.5) => new() { Text = text, Style = Res<Style>("MutedText"), FontSize = size };
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };

    public static Border Pill(string text, bool caution = false) => Tag(text, caution ? "Gold" : "Accent");

    /// <summary>Small coloured label. <paramref name="tone"/>: Accent, Gold, Success, Danger, Violet (cyan), Muted.</summary>
    public static Border Tag(string text, string tone) => new()
    {
        Style = Res<Style>("Badge"), Margin = new Thickness(0, 0, 6, 0),
        Background = tone == "Muted" ? MutedTagFill : Brush(tone + "Soft"),
        BorderBrush = tone == "Muted" ? MutedTagRim : TagRim(tone), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brush(tone == "Muted" ? "Muted" : tone) }
    };

    private static readonly Brush MutedTagFill = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush MutedTagRim = Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
    private static readonly Dictionary<string, Brush> TagRims = [];

    /// <summary>The tone's colour at low opacity, for a crisp 1 px tag outline.</summary>
    private static Brush TagRim(string tone)
    {
        if (TagRims.TryGetValue(tone, out var b)) return b;
        var c = Brush(tone) is SolidColorBrush s ? s.Color : Colors.White;
        return TagRims[tone] = Frozen(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)));
    }

    /// <summary>Grid of equal-width cells that drops columns as the window narrows (never squeezes text).</summary>
    public static UniformGrid Grid(int columns, params UIElement[] children)
    {
        var g = new ResponsiveGrid { MaxColumns = columns };
        foreach (var c in children) g.Children.Add(c);
        return g;
    }

    /// <summary>A shortcut drawn as keycaps: "Ctrl+Shift+F10" → [Ctrl] + [Shift] + [F10], each cap sized to its text.</summary>
    public static StackPanel KeyCaps(string keys)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sp.Children.Add(new TextBlock { Text = "+", FontSize = 11, Foreground = Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
            sp.Children.Add(new Border
            {
                // Slightly thicker bottom edge reads as a physical key.
                CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(1, 1, 1, 2),
                BorderBrush = Brush("ControlBorderHover"), Background = Brush("ControlFill"),
                Padding = new Thickness(8, 3, 8, 3), MinWidth = 28, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = parts[i], FontFamily = Res<FontFamily>("MonoFont"), FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        return sp;
    }

    /// <summary>Friendly empty state with an icon.</summary>
    public static UIElement Empty(string icon, string title, string sub)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 22) };
        var tile = IconTile(icon, 52, 21);
        tile.Margin = new Thickness(0);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(tile);
        sp.Loaded += (_, _) => Motion.FadeIn(sp, 60, 10, 420);
        sp.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H2"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4) });
        sp.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("MutedText"), TextAlignment = TextAlignment.Center, MaxWidth = 380 });
        return sp;
    }

    /// <summary>Indeterminate shimmer bar with a caption and a three-block sequencer, for work in progress.</summary>
    public static UIElement Loading(string caption)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        // Three squares light up in turn (opacity only); still squares with reduced motion.
        for (int i = 0; i < 3; i++)
        {
            var b = new Border { Width = 6, Height = 6, Margin = new Thickness(0, 0, 4, 0), Background = Brush("AccentGradient"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 };
            if (!App.ReduceMotion)
                b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 160), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            head.Children.Add(b);
        }
        var cap = Muted(caption, 12);
        cap.Margin = new Thickness(6, 0, 0, 0);
        head.Children.Add(cap);
        sp.Children.Add(head);
        sp.Children.Add(new ProgressBar { IsIndeterminate = !App.ReduceMotion, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Width = 420 });
        return sp;
    }

    /// <summary>Animates a property unless reduced motion is on, in which case it jumps straight to the value.</summary>
    public static void Animate(IAnimatable target, DependencyProperty dp, double to, int ms = 260)
    {
        if (App.ReduceMotion) { target.BeginAnimation(dp, null); ((DependencyObject)target).SetValue(dp, to); return; }
        target.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}

/// <summary>UniformGrid whose column count follows the available width.</summary>
public sealed class ResponsiveGrid : UniformGrid
{
    public int MaxColumns { get; set; } = 3;
    public double MinItemWidth { get; set; } = 230;

    public ResponsiveGrid()
    {
        Columns = MaxColumns;
        SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || e.NewSize.Width <= 0) return;
            var cols = Math.Clamp((int)(e.NewSize.Width / MinItemWidth), 1, Math.Max(1, MaxColumns));
            if (cols != Columns) Columns = cols;
        };
    }
}

/// <summary>
/// Angular gauge drawn in OnRender: a square track, and the value as a stroke running clockwise around it from the top
/// centre, with instrument ticks inside. Animates only when its value changes.
/// </summary>
public sealed class RingArc : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingArc),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get; set; } = 10;
    public Brush? Stroke { get; set; }

    private static readonly Brush TrackBrush = FrozenBrush(Color.FromRgb(0x1A, 0x1D, 0x25));
    private static readonly Pen TickMinor = FrozenPen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen TickMajor = FrozenPen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.2);
    private static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    /// <summary>Point at fraction t (0..1) of the way round a square of half-size h centred on c, starting top centre, clockwise.</summary>
    private static Point Along(Point c, double h, double t)
    {
        double d = (t % 1) * 8 * h; // perimeter = 8h, starting at the top centre
        if (d < h) return new(c.X + d, c.Y - h);
        d -= h; if (d < 2 * h) return new(c.X + h, c.Y - h + d);
        d -= 2 * h; if (d < 2 * h) return new(c.X + h - d, c.Y + h);
        d -= 2 * h; if (d < 2 * h) return new(c.X - h, c.Y + h - d);
        d -= 2 * h; return new(c.X - h + d, c.Y - h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight), h = (size - Thickness) / 2;
        if (h <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen(TrackBrush, Thickness) { LineJoin = PenLineJoin.Miter };
        dc.DrawRectangle(null, track, new Rect(c.X - h, c.Y - h, 2 * h, 2 * h));
        // Ticks along the inside edge: fine every 1/40 of the way round, stronger every 1/8.
        double inner = h - Thickness / 2 - 4;
        if (inner > 20)
            for (int i = 0; i < 40; i++)
            {
                var p = Along(c, inner, i / 40.0);
                var len = i % 5 == 0 ? 5 : 2.5;
                var dir = new Vector(c.X - p.X, c.Y - p.Y);
                // Ticks point straight inwards from the nearest edge.
                var n = Math.Abs(dir.X) > Math.Abs(dir.Y) ? new Vector(Math.Sign(dir.X), 0) : new Vector(0, Math.Sign(dir.Y));
                dc.DrawLine(i % 5 == 0 ? TickMajor : TickMinor, p, p + n * len);
            }
        var v = Math.Clamp(Value, 0, 100) / 100.0;
        if (v <= 0.001) return;
        var pen = new Pen(Stroke ?? Ui.Brush("AccentGradient"), Thickness) { LineJoin = PenLineJoin.Miter, StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(Along(c, h, 0), false, v >= 0.999);
            // Corners in order (fractions of the perimeter where the path turns).
            foreach (var corner in new[] { 0.125, 0.375, 0.625, 0.875 })
                if (v > corner) ctx.LineTo(Along(c, h, corner), true, false);
            ctx.LineTo(Along(c, h, Math.Min(v, 0.9999)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}

/// <summary>Ring gauge with a big centred number and caption.</summary>
public sealed class RingGauge : Grid
{
    private readonly RingArc _arc;
    private readonly TextBlock _value = new() { FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _unit = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _caption;

    public RingGauge(string caption, double size = 132, double thickness = 10, Brush? stroke = null)
    {
        Width = size;
        _arc = new RingArc { Width = size, Height = size, Thickness = thickness, Stroke = stroke };
        _value.FontFamily = Ui.Res<FontFamily>("DisplayFont");
        _unit.Foreground = Ui.Brush("Muted");
        _caption = new TextBlock { Text = caption, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_arc);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(_value);
        center.Children.Add(_unit);
        Children.Add(center);
        SetRow(_caption, 1);
        Children.Add(_caption);
    }

    public void Set(double? percent, string display, string unit = "")
    {
        _value.Text = display;
        _unit.Text = unit;
        if (App.ReduceMotion) { _arc.BeginAnimation(RingArc.ValueProperty, null); _arc.Value = percent ?? 0; return; }
        _arc.BeginAnimation(RingArc.ValueProperty, new DoubleAnimation(percent ?? 0, TimeSpan.FromMilliseconds(650)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
    }

    public string Caption { set => _caption.Text = value; }
}

/// <summary>Live metric tile with a rolling gradient sparkline. Geometry is rebuilt into one reused StreamGeometry.</summary>
public sealed class StatTile : Border
{
    private readonly TextBlock _value = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("DisplayFont") };
    private readonly TextBlock _sub = Ui.Muted("", 11.5);
    private readonly Path _line = new() { StrokeThickness = 2, Stroke = Ui.Brush("AccentGradientH"), StrokeLineJoin = PenLineJoin.Miter };
    private readonly Path _fill = new();
    private readonly double[] _values = new double[60];
    private int _count;
    private readonly Grid _chart = new() { Height = 48, ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0) };
    public double Max { get; set; } = 100;

    public StatTile(string label, string icon)
    {
        Style = Ui.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 14, 14);
        var fill = new LinearGradientBrush(Color.FromArgb(0x38, 0x2F, 0xD8, 0xEE), Color.FromArgb(0x00, 0x8A, 0x6B, 0xFF), 90);
        fill.Freeze();
        _fill.Fill = fill;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), Foreground = Ui.Brush("Violet"), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, Foreground = Ui.Brush("Muted"), FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("DisplayFont") });
        // Faint guide lines so the chart area reads as a chart while the first minute of samples fills it.
        var guide = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)); guide.Freeze();
        foreach (var va in new[] { VerticalAlignment.Top, VerticalAlignment.Center, VerticalAlignment.Bottom })
            _chart.Children.Add(new Border { Height = 1, Background = guide, VerticalAlignment = va });
        _chart.Children.Add(_fill);
        _chart.Children.Add(_line);
        _chart.SizeChanged += (_, _) => Redraw();
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(_value);
        sp.Children.Add(_sub);
        sp.Children.Add(_chart);
        Child = sp;
    }

    public void Push(double? v, string display, string sub = "")
    {
        if (_value.Text != display) _value.Text = display;
        if (_sub.Text != sub) _sub.Text = sub;
        if (v is null) return;
        if (_count < _values.Length) _values[_count++] = v.Value;
        else { Array.Copy(_values, 1, _values, 0, _values.Length - 1); _values[^1] = v.Value; }
        Redraw();
    }

    private void Redraw()
    {
        double w = _chart.ActualWidth, h = _chart.ActualHeight;
        if (w <= 0 || _count < 2) return;
        double max = Max;
        for (int i = 0; i < _count; i++) max = Math.Max(max, _values[i]);
        double step = w / (_values.Length - 1), x0 = w - (_count - 1) * step;
        Point P(int i) => new(x0 + i * step, h - 2 - (h - 6) * Math.Clamp(_values[i] / max, 0, 1));
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (int i = 1; i < _count; i++) c.LineTo(P(i), true, true);
        }
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < _count; i++) c.LineTo(P(i), false, true);
            c.LineTo(new Point(w, h), false, false);
        }
        line.Freeze(); area.Freeze();
        _line.Data = line;
        _fill.Data = area;
    }
}

public static class Fmt
{
    public static string Rate(double kbps) => kbps >= 1024 ? (kbps / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s" : kbps.ToString("0", CultureInfo.CurrentCulture) + " KB/s";
}
