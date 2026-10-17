using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MoneyTweaks.Controls;

/// <summary>
/// Money Tweaks' own option picker (used instead of a drop-down for sorting): every option is visible in one rounded bar
/// and a highlight slides to the chosen one. Options are radio buttons, so keyboard and screen readers work as usual.
/// </summary>
public sealed class SegmentedPicker : Border
{
    private readonly StackPanel _options = new() { Orientation = Orientation.Horizontal };
    private readonly Border _highlight;
    private readonly TranslateTransform _slide = new();
    private readonly string _group = "seg-" + Guid.NewGuid().ToString("N");
    private RadioButton? _current;

    /// <summary>Raised with the chosen option's value after the user picks it.</summary>
    public event Action<string>? Changed;
    public string Selected { get; private set; }

    public SegmentedPicker(IEnumerable<(string label, string value, string? tip)> options, string selected)
    {
        Selected = selected;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x18));
        BorderBrush = Ui.Brush("BorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(3);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;

        _highlight = new Border
        {
            CornerRadius = new CornerRadius(8), Background = Ui.Brush("AccentSoft"), BorderBrush = Ui.Brush("AccentGradient"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _slide, IsHitTestVisible = false, Opacity = 0,
        };
        foreach (var (label, value, tip) in options)
        {
            var rb = new RadioButton { Content = label, Tag = value, GroupName = _group, Style = Ui.Res<Style>("SegmentOption"), ToolTip = tip, IsChecked = value == selected };
            rb.Checked += (_, _) =>
            {
                _current = rb;
                Selected = (string)rb.Tag;
                MoveHighlight(animate: true);
                Changed?.Invoke(Selected);
            };
            if (rb.IsChecked == true) _current = rb;
            _options.Children.Add(rb);
        }
        var grid = new Grid();
        grid.Children.Add(_highlight);
        grid.Children.Add(_options);
        Child = grid;
        // Position without animation once the options have a size (and again if fonts/DPI change their widths).
        _options.SizeChanged += (_, _) => MoveHighlight(animate: false);
    }

    private void MoveHighlight(bool animate)
    {
        if (_current is null || _current.ActualWidth <= 0) return;
        double x = _current.TranslatePoint(new Point(0, 0), _options).X, w = _current.ActualWidth;
        _highlight.Height = _current.ActualHeight;
        _highlight.VerticalAlignment = VerticalAlignment.Center;
        if (!animate || _highlight.Opacity == 0)
        {
            Motion.Tween(_slide, 0, 0, 0, _ => { }); // cancel any running slide
            _slide.X = x; _highlight.Width = w; _highlight.Opacity = 1;
            return;
        }
        double fromX = _slide.X, fromW = double.IsNaN(_highlight.Width) ? w : _highlight.Width;
        Motion.Tween(_slide, 0, 1, 240, p => { _slide.X = fromX + (x - fromX) * p; _highlight.Width = fromW + (w - fromW) * p; });
    }
}
