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

    /// <summary>Standard page: eyebrow, title, gradient underline, blurb, optional actions on the right.</summary>
    public static (ScrollViewer root, StackPanel body) Page(string title, string blurb, UIElement? headerRight = null, string? eyebrow = null, string? icon = null)
    {
        var body = new StackPanel { Margin = new Thickness(28, 6, 28, 32) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 20), LastChildFill = true };
        if (headerRight is not null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            if (headerRight is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Bottom; fe.Margin = new Thickness(16, 0, 0, 2); }
            head.Children.Add(headerRight);
        }
        var t = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            titleRow.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), Background = Brush("AccentSoft"),
                BorderBrush = Brush("CardRim"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = icon, Style = Res<Style>("Icon"), FontSize = 18, Foreground = Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Center }
            });
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (eyebrow is not null) titles.Children.Add(new TextBlock { Text = eyebrow.ToUpperInvariant(), Style = Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 2) });
        titles.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H1") });
        titleRow.Children.Add(titles);
        t.Children.Add(titleRow);
        t.Children.Add(new Border { Width = 56, Height = 3, CornerRadius = new CornerRadius(2), Background = Brush("AccentGradientH"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 10) });
        t.Children.Add(new TextBlock { Text = blurb, Style = Res<Style>("MutedText"), FontSize = 13.5, LineHeight = 20, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left });
        head.Children.Add(t);
        body.Children.Add(head);
        return (new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly }, body);
    }

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

    public static StackPanel Section(string title, string? sub = null)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H2") });
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

    /// <summary>Small coloured label. <paramref name="tone"/>: Accent, Gold, Success, Danger, Violet, Muted.</summary>
    public static Border Tag(string text, string tone) => new()
    {
        Style = Res<Style>("Badge"), Margin = new Thickness(0, 0, 6, 0),
        Background = tone == "Muted" ? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) : Brush(tone + "Soft"),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brush(tone == "Muted" ? "Muted" : tone) }
    };

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
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1, 1, 1, 2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x46)), Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x28)),
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
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 18) };
        sp.Children.Add(new Border
        {
            Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = Brush("AccentSoft"), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = icon, Style = Res<Style>("Icon"), FontSize = 22, Foreground = Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Center }
        });
        sp.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H2"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4) });
        sp.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("MutedText"), TextAlignment = TextAlignment.Center, MaxWidth = 380 });
        return sp;
    }

    /// <summary>Indeterminate shimmer bar with a caption, for work in progress.</summary>
    public static UIElement Loading(string caption)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        sp.Children.Add(Muted(caption, 12));
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

/// <summary>Circular gauge drawn in OnRender; animates only when its value changes.</summary>
public sealed class RingArc : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingArc),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get; set; } = 10;
    public Brush? Stroke { get; set; }

    private static readonly Pen TrackPen = CreateTrackPen();
    private static Pen CreateTrackPen() { var p = new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x32)), 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight), r = (size - Thickness) / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = TrackPen.Thickness == Thickness ? TrackPen : new Pen(TrackPen.Brush, Thickness);
        dc.DrawEllipse(null, track, c, r, r);
        var v = Math.Clamp(Value, 0, 100) / 100.0;
        if (v <= 0.001) return;
        var pen = new Pen(Stroke ?? Ui.Brush("AccentGradient"), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a0 = -Math.PI / 2, a1 = a0 + v * 2 * Math.PI;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
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
        Ui.Animate(_arc, RingArc.ValueProperty, percent ?? 0, 450);
    }

    public string Caption { set => _caption.Text = value; }
}

/// <summary>Live metric tile with a rolling gradient sparkline. Geometry is rebuilt into one reused StreamGeometry.</summary>
public sealed class StatTile : Border
{
    private readonly TextBlock _value = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("DisplayFont") };
    private readonly TextBlock _sub = Ui.Muted("", 11.5);
    private readonly Path _line = new() { StrokeThickness = 2, Stroke = Ui.Brush("AccentGradientH"), StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _fill = new();
    private readonly double[] _values = new double[60];
    private int _count;
    private readonly Grid _chart = new() { Height = 48, ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0) };
    public double Max { get; set; } = 100;

    public StatTile(string label, string icon)
    {
        Style = Ui.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 14, 14);
        var fill = new LinearGradientBrush(Color.FromArgb(0x40, 0x4C, 0x8D, 0xFF), Color.FromArgb(0x00, 0x8B, 0x6C, 0xFF), 90);
        fill.Freeze();
        _fill.Fill = fill;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), Foreground = Ui.Brush("Accent"), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10.5, Foreground = Ui.Brush("Muted"), FontWeight = FontWeights.Bold });
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
