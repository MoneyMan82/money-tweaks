using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter;

/// <summary>Builders for the angular control-center look (views are built in code).</summary>
public static class Ui
{
    public static Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static readonly Brush Bg = B("#07090D"), Panel = B("#0D1117"), Panel2 = B("#121821"), Line = B("#1D2733"), LineBright = B("#2C3B4C"),
        Fg = B("#D9E3EC"), Dim = B("#7C8A98"), Faint = B("#4E5B68"), Accent = B("#00E1FF"), Violet = B("#8B6CFF"), Green = B("#3DDC97"),
        Amber = B("#FFB547"), Red = B("#FF4D5E"), Blue = B("#4DA3FF"), Pink = B("#FF6BD6");

    public static readonly FontFamily Display = new("Bahnschrift, Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static Brush StatusBrush(string? s) => s switch
    {
        AgentStatus.Researching => Blue,
        AgentStatus.Analyzing => Violet,
        AgentStatus.Planning => Accent,
        AgentStatus.Coding => Green,
        AgentStatus.Testing => Amber,
        AgentStatus.Reviewing => Pink,
        AgentStatus.Blocked => Red,
        AgentStatus.Failed => Red,
        AgentStatus.Paused => Amber,
        AgentStatus.Completed => B("#5FA88A"),
        AgentStatus.Queued => Dim,
        _ => Faint,
    };

    public static Brush KindBrush(string kind) => kind switch
    {
        "progress" => Accent, "research" => Blue, "delegate" => Violet, "message" => Violet, "report" => Green,
        "code" => Green, "test" => Amber, "error" or "blocked" => Red, "control" => Amber, "file" => B("#6FCF97"),
        "task" => Fg, "start" => Accent, "stop" => B("#5FA88A"), "plan" => Pink, _ => Dim,
    };

    public static TextBlock T(string? text, double size = 13, Brush? fg = null, FontWeight? weight = null, FontFamily? font = null, bool wrap = false) => new()
    {
        Text = text ?? "", FontSize = size, Foreground = fg ?? Fg, FontWeight = weight ?? FontWeights.Normal, FontFamily = font ?? Body,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    /// <summary>Small uppercase section label.</summary>
    public static TextBlock Label(string text, Brush? fg = null) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 10.5, Foreground = fg ?? Dim, FontFamily = Display, Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock Head(string text, double size = 16) => T(text, size, Fg, FontWeights.SemiBold, Display);

    /// <summary>Panel with a chamfered (cut) top-right corner and a thin border; no rounded corners anywhere.</summary>
    public static Grid Card(UIElement content, Brush? edge = null, double cut = 10, Thickness? padding = null)
    {
        var g = new Grid();
        var shape = new System.Windows.Shapes.Path { Stretch = Stretch.None, Fill = Panel, Stroke = Line, StrokeThickness = 1, SnapsToDevicePixels = true };
        g.Children.Add(shape);
        var host = new Border { Padding = padding ?? new Thickness(12, 10, 12, 10), Child = content };
        g.Children.Add(host);
        if (edge is not null) g.Children.Add(new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Fill = edge, Margin = new Thickness(0, 0, 0, 0) });
        g.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width - 0.5, h = e.NewSize.Height - 0.5;
            if (w <= cut || h <= cut) return;
            shape.Data = Geometry.Parse(FormattableString.Invariant($"M0.5,0.5 L{w - cut},0.5 {w},{cut} {w},{h} 0.5,{h} Z"));
        };
        return g;
    }

    public static Button Btn(string text, Action onClick, string? tip = null)
    {
        var b = new Button { Content = text.ToUpperInvariant(), Style = (Style)Application.Current.Resources["CmdButton"], ToolTip = tip };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Status chip: angular, coloured, with a breathing dot while the agent is working.</summary>
    public static FrameworkElement Badge(string status, bool live)
    {
        var brush = StatusBrush(status);
        var dot = new Rectangle { Width = 7, Height = 7, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (live) Breathe(dot);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(dot);
        sp.Children.Add(T(status, 11, brush, FontWeights.SemiBold, Display));
        return new Border { BorderBrush = brush, BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 8, 2), Child = sp, HorizontalAlignment = HorizontalAlignment.Left };
    }

    public static void Breathe(UIElement e, double low = 0.25, double seconds = 1.1)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var a = new DoubleAnimation(1, low, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        e.BeginAnimation(UIElement.OpacityProperty, a);
    }

    public static void FadeIn(UIElement e, double ms = 220)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        e.Opacity = 0;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase() });
    }

    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{Math.Max(0, (int)t.TotalSeconds)}s";

    public static string Ago(DateTimeOffset t, DateTimeOffset now) => now - t < TimeSpan.FromSeconds(5) ? "now" : Elapsed(now - t) + " ago";

    public static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss");

    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public static string Pretty(string type) => type switch
    {
        "main" => "Main session",
        _ => type,
    };

    /// <summary>Display name for an agent instance: its type, plus a short id for subagents.</summary>
    public static string Name(AgentInstance a) => a.IsMain ? "Main session · " + a.Session[..Math.Min(8, a.Session.Length)] : a.Type;
}
