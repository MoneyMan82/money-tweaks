using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoneyTweaks.Core.Macros;

namespace MoneyTweaks.Controls;

public sealed record PickResult(int X, int Y, int Width, int Height, string Hex);

/// <summary>
/// Full-virtual-screen overlay to pick a pixel (click) or region (drag). Positions come from GetCursorPos in physical
/// pixels, so results are correct on every monitor and at any scaling; the drawn rectangle is only a visual guide.
/// </summary>
public sealed class ScreenPicker : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _sel = new() { Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0x8D, 0xFF)), Visibility = Visibility.Collapsed };
    private readonly Border _info;
    private readonly Border _swatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private (int x, int y)? _start;
    private Point _startLocal;
    private string _hex = "#000000";
    public PickResult? Result { get; private set; }

    public ScreenPicker()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)); // hit-testable but visually transparent
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        Cursor = Cursors.Cross;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_swatch);
        row.Children.Add(_text);
        _info = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x14, 0x1C)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 10, 6), Child = row, IsHitTestVisible = false };
        var help = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x14, 0x1C)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 14, 8), IsHitTestVisible = false,
            Child = new TextBlock { Text = "Click a pixel, or drag to select a region (max 200×200).  Esc to cancel.", Foreground = Brushes.White, FontSize = 13 }
        };
        Canvas.SetLeft(help, 30); Canvas.SetTop(help, 30);
        _canvas.Children.Add(_sel);
        _canvas.Children.Add(help);
        _canvas.Children.Add(_info);
        Content = _canvas;
        _timer.Tick += (_, _) => UpdateInfo();
        Loaded += (_, _) => { _timer.Start(); Activate(); Focus(); };
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { _start = ScreenCapture.CursorPosition(); _startLocal = e.GetPosition(_canvas); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_start is null) return;
            var p = e.GetPosition(_canvas);
            _sel.Visibility = Visibility.Visible;
            Canvas.SetLeft(_sel, Math.Min(p.X, _startLocal.X)); Canvas.SetTop(_sel, Math.Min(p.Y, _startLocal.Y));
            _sel.Width = Math.Abs(p.X - _startLocal.X); _sel.Height = Math.Abs(p.Y - _startLocal.Y);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (_start is not { } s) return;
            ReleaseMouseCapture();
            var (ex, ey) = ScreenCapture.CursorPosition();
            int x = Math.Min(s.x, ex), y = Math.Min(s.y, ey), w = Math.Abs(ex - s.x) + 1, h = Math.Abs(ey - s.y) + 1;
            if (w <= 3 && h <= 3) { x = s.x; y = s.y; w = h = 1; }
            w = Math.Min(w, ColorTrigger.MaxSide); h = Math.Min(h, ColorTrigger.MaxSide);
            var c = ScreenCapture.PixelAt(s.x, s.y);
            Result = new PickResult(x, y, w, h, c is { } col ? ColorTrigger.ToHex(col.r, col.g, col.b) : _hex);
            Close();
        };
    }

    private void UpdateInfo()
    {
        var (x, y) = ScreenCapture.CursorPosition();
        if (ScreenCapture.PixelAt(x, y) is { } c)
        {
            _hex = ColorTrigger.ToHex(c.r, c.g, c.b);
            _swatch.Background = new SolidColorBrush(Color.FromRgb(c.r, c.g, c.b));
            _text.Text = $"{_hex}   RGB {c.r}, {c.g}, {c.b}   at {x}, {y}";
        }
        var local = Mouse.GetPosition(_canvas);
        Canvas.SetLeft(_info, local.X + 18);
        Canvas.SetTop(_info, local.Y + 18);
    }

    /// <summary>Hides the owner while picking so it doesn't cover the target, then restores it.</summary>
    public static PickResult? Pick(Window owner)
    {
        var prev = owner.WindowState;
        owner.WindowState = WindowState.Minimized;
        var p = new ScreenPicker();
        p.ShowDialog();
        owner.WindowState = prev == WindowState.Minimized ? WindowState.Normal : prev;
        owner.Activate();
        return p.Result;
    }
}
