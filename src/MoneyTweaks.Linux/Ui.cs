using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MoneyTweaks.Linux.App;

/// <summary>Money Tweaks' colours and a few building blocks (square corners, like the Windows app).</summary>
internal static class Ui
{
    public static readonly IBrush Bg = Brush("#06070A"), Surface = Brush("#0B0D12"), Card = Brush("#111419"), Border = Brush("#1E222B"),
        Accent = Brush("#8A6BFF"), Gold = Brush("#F5B84A"), Success = Brush("#3DDC97"), Danger = Brush("#FF5C7A"), Violet = Brush("#2FD8EE"),
        Text = Brush("#ECEFF6"), Muted = Brush("#8D94A6"), Subtle = Brush("#5A6175");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock T(string text, double size = 13, IBrush? color = null, FontWeight weight = FontWeight.Normal, bool wrap = true) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Text, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    public static TextBlock Eyebrow(string text) => T(text.ToUpperInvariant(), 11, Accent, FontWeight.SemiBold);

    public static Border CardOf(Control child, Thickness? margin = null) => new()
    {
        Child = child, Background = Card, BorderBrush = Border, BorderThickness = new Thickness(1), Padding = new Thickness(16, 14),
        Margin = margin ?? new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(0),
    };

    public static Button Btn(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click, bool primary = false)
    {
        var b = new Button
        {
            Content = text, CornerRadius = new CornerRadius(0), Padding = new Thickness(14, 6), Margin = new Thickness(0, 0, 8, 0),
            Background = primary ? Accent : Surface, Foreground = Text, BorderBrush = Border, BorderThickness = new Thickness(1),
        };
        b.Click += click;
        return b;
    }

    public static Border Tag(string text, IBrush tone) => new()
    {
        Child = T(text, 10.5, tone, FontWeight.Bold, wrap: false), BorderBrush = tone, BorderThickness = new Thickness(1),
        Padding = new Thickness(6, 1), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(0), VerticalAlignment = VerticalAlignment.Center,
    };

    public static ScrollViewer Page(string eyebrow, string title, string blurb, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(28, 22, 28, 28), Spacing = 0 };
        body.Children.Add(Eyebrow(eyebrow));
        body.Children.Add(T(title, 26, Text, FontWeight.SemiBold));
        var b = T(blurb, 13, Muted); b.Margin = new Thickness(0, 4, 0, 18); b.MaxWidth = 860; b.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(b);
        return new ScrollViewer { Content = body };
    }
}
