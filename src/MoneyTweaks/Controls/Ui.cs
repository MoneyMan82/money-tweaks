using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MoneyTweaks.Controls;

/// <summary>Small helpers so code-built pages share the same look as the XAML ones.</summary>
public static class Ui
{
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];
    public static Brush Brush(string key) => Res<Brush>(key);

    public static (ScrollViewer root, StackPanel body) Page(string title, string blurb, UIElement? headerRight = null)
    {
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 24) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (headerRight is not null) { DockPanel.SetDock(headerRight, Dock.Right); head.Children.Add(headerRight); }
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H1") });
        t.Children.Add(new TextBlock { Text = blurb, Style = Res<Style>("MutedText"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left });
        head.Children.Add(t);
        body.Children.Add(head);
        return (new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, body);
    }

    public static Border Card(UIElement child, Thickness? margin = null) =>
        new() { Style = Res<Style>("CardBorder"), Child = child, Margin = margin ?? new Thickness(0, 0, 0, 12) };

    public static StackPanel Section(string title, string? sub = null)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, Style = Res<Style>("H2") });
        if (sub is not null) s.Children.Add(new TextBlock { Text = sub, Style = Res<Style>("MutedText"), Margin = new Thickness(0, 3, 0, 10) });
        else s.Children.Add(new Border { Height = 10 });
        return s;
    }

    public static Button Btn(string text, RoutedEventHandler click, string style = "BaseButton", Thickness? margin = null)
    {
        var b = new Button { Content = text, Style = Res<Style>(style), Margin = margin ?? new Thickness(0, 0, 8, 0) };
        b.Click += click;
        return b;
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

    public static Border Pill(string text, bool caution = false) => new()
    {
        Style = Res<Style>("Badge"), Margin = new Thickness(0, 0, 6, 0),
        Background = Brush(caution ? "GoldSoft" : "AccentSoft"),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brush(caution ? "Gold" : "Accent") }
    };

    /// <summary>Grid of equal-width cells that wraps to fewer columns when narrow.</summary>
    public static UniformGrid Grid(int columns, params UIElement[] children)
    {
        var g = new UniformGrid { Columns = columns };
        foreach (var c in children) g.Children.Add(c);
        return g;
    }
}

/// <summary>Live metric tile with a rolling sparkline.</summary>
public sealed class StatTile : Border
{
    private readonly TextBlock _value = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("DisplayFont") };
    private readonly TextBlock _sub = Ui.Muted("", 11.5);
    private readonly Polyline _line = new() { StrokeThickness = 2, Stroke = Ui.Brush("Accent"), StrokeLineJoin = PenLineJoin.Round };
    private readonly Polygon _fill = new() { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x2B, 0xE3, 0x8B)) };
    private readonly List<double> _values = [];
    private readonly Canvas _canvas = new() { Height = 46, ClipToBounds = true, Margin = new Thickness(0, 8, 0, 0) };
    public double Max { get; set; } = 100;

    public StatTile(string label, string icon)
    {
        Style = Ui.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 12, 12);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), Foreground = Ui.Brush("Accent"), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ui.Brush("Muted"), FontWeight = FontWeights.SemiBold });
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => Redraw();
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(_value);
        sp.Children.Add(_sub);
        sp.Children.Add(_canvas);
        Child = sp;
    }

    public void Push(double? v, string display, string sub = "")
    {
        _value.Text = display;
        _sub.Text = sub;
        if (v is null) return;
        _values.Add(v.Value);
        if (_values.Count > 60) _values.RemoveAt(0);
        Redraw();
    }

    private void Redraw()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w <= 0 || _values.Count < 2) return;
        var max = Math.Max(Max, _values.Max());
        var pts = new PointCollection();
        for (int i = 0; i < _values.Count; i++)
            pts.Add(new Point(w * i / 59.0 + (60 - _values.Count) * w / 59.0, h - 2 - (h - 4) * Math.Clamp(_values[i] / max, 0, 1)));
        _line.Points = pts;
        var fill = new PointCollection(pts) { new Point(pts[^1].X, h), new Point(pts[0].X, h) };
        _fill.Points = fill;
    }
}
