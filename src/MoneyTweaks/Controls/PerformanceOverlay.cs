using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Controls;

/// <summary>Everything one overlay refresh shows. Null values are "not available" and are left out.</summary>
public sealed record OverlayFrame(
    PresentStats? Frames, CaptureState Capture, string? CaptureNote,
    double? CpuPercent, double? GpuPercent, double? RamUsedGb, double? RamTotalGb, double? CpuTempC, double? GpuTempC,
    string? Target, GameWindowMode Mode);

/// <summary>Frame-time line graph (last ~120 frames), drawn as one geometry per refresh.</summary>
public sealed class FrametimeGraph : FrameworkElement
{
    private IReadOnlyList<double> _ms = [];
    public Brush Line { get; set; } = Brushes.LimeGreen;
    public Brush Guide { get; set; } = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public void Set(IReadOnlyList<double> ms) { _ms = ms; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        if (_ms.Count < 2) { dc.DrawLine(new Pen(Guide, 1), new Point(0, h - 1), new Point(w, h - 1)); return; }
        // Scale to the worst frame, at least 33 ms (30 FPS), so a stable game draws a flat line low in the box.
        var top = Math.Max(33.4, _ms.Max() * 1.15);
        double Y(double v) => h - 1 - Math.Min(v, top) / top * (h - 2);
        var avg = _ms.Average();
        dc.DrawLine(new Pen(Guide, 1) { DashStyle = DashStyles.Dash }, new Point(0, Y(avg)), new Point(w, Y(avg)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double step = w / Math.Max(1, _ms.Count - 1);
            c.BeginFigure(new Point(0, Y(_ms[0])), false, false);
            for (int i = 1; i < _ms.Count; i++) c.LineTo(new Point(i * step, Y(_ms[i])), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(Line, 1.4) { LineJoin = PenLineJoin.Miter }, g);
    }
}

/// <summary>The overlay's content. Used by the overlay window and by the live preview on the settings page.</summary>
public sealed class OverlayPanel : Border
{
    private readonly StackPanel _stack = new();
    private readonly TextBlock _fps = new(), _hw = new(), _ram = new(), _temps = new(), _target = new(), _note = new();
    private readonly FrametimeGraph _graph = new() { Height = 34, Margin = new Thickness(0, 3, 0, 3) };
    private OverlayLayout _layout = new();

    public OverlayPanel()
    {
        CornerRadius = new CornerRadius(0);
        Padding = new Thickness(8, 5, 8, 6);
        Child = _stack;
        foreach (var t in new[] { _fps, _graph as FrameworkElement, _hw, _ram, _temps, _note, _target }) _stack.Children.Add(t);
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    private static Brush BrushFrom(string hex, Color fallback)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = fallback; }
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void ApplyLayout(OverlayLayout l)
    {
        _layout = l;
        var text = BrushFrom(l.TextColor, Colors.White);
        var accent = BrushFrom(l.AccentColor, Color.FromRgb(0x2B, 0xE3, 0x8B));
        Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(l.Opacity, 0, 1) * 255), 0x05, 0x07, 0x0B));
        foreach (var t in new[] { _fps, _hw, _ram, _temps, _note, _target })
        {
            t.FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI");
            t.Foreground = text;
            t.FontSize = Math.Clamp(l.FontSize, 8, 40);
        }
        _fps.FontSize = Math.Clamp(l.FontSize * 1.25, 9, 50);
        _fps.FontWeight = FontWeights.Bold;
        _fps.Foreground = accent;
        _target.FontSize = _note.FontSize = Math.Clamp(l.FontSize * 0.8, 7, 32);
        _target.Opacity = 0.7;
        _note.Foreground = BrushFrom("#FFF5C26B", Colors.Gold);
        _graph.Line = accent;
        _graph.Width = Math.Clamp(l.FontSize * 13, 110, 520);
        _graph.Height = Math.Clamp(l.FontSize * 2.6, 24, 120);
    }

    private static string N(double v, string f) => v.ToString(f, CultureInfo.InvariantCulture);

    public void Update(OverlayFrame f)
    {
        var l = _layout;
        // FPS / frame time line.
        if (l.Fps || l.FrameTime || l.OnePercentLow)
        {
            string line;
            if (f.Frames is { } s)
            {
                var parts = new List<string>();
                if (l.Fps) parts.Add($"{N(s.Fps, "0")} FPS");
                if (l.FrameTime) parts.Add($"{N(s.AvgMs, "0.0")} ms");
                if (l.OnePercentLow) parts.Add($"1% {N(s.Low1PercentFps, "0")}");
                line = string.Join("  ", parts);
            }
            else line = f.Capture switch
            {
                CaptureState.NeedsAdmin => "FPS  needs admin",
                CaptureState.Failed => "FPS  unavailable",
                CaptureState.Running => "FPS  —",
                _ => "FPS  off",
            };
            _fps.Text = line;
            _fps.Visibility = Visibility.Visible;
        }
        else _fps.Visibility = Visibility.Collapsed;

        _graph.Visibility = l.Graph && f.Frames is not null ? Visibility.Visible : Visibility.Collapsed;
        if (l.Graph && f.Frames is { } fs) _graph.Set(fs.RecentMs);

        var hw = new List<string>();
        if (l.Cpu && f.CpuPercent is { } c) hw.Add($"CPU {N(c, "0")}%");
        if (l.Gpu) hw.Add(f.GpuPercent is { } g ? $"GPU {N(g, "0")}%" : "GPU n/a");
        _hw.Text = string.Join("  ", hw);
        _hw.Visibility = hw.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _ram.Visibility = l.Ram && f.RamUsedGb is not null ? Visibility.Visible : Visibility.Collapsed;
        if (f.RamUsedGb is { } used && f.RamTotalGb is { } total) _ram.Text = $"RAM {N(used, "0.0")}/{N(total, "0")} GB";

        var temps = new List<string>();
        if (f.CpuTempC is { } ct) temps.Add($"CPU {N(ct, "0")}°C");
        if (f.GpuTempC is { } gt) temps.Add($"GPU {N(gt, "0")}°C");
        _temps.Text = "TEMP " + (temps.Count > 0 ? string.Join("  ", temps) : "n/a");
        _temps.Visibility = l.Temps ? Visibility.Visible : Visibility.Collapsed;

        _note.Text = f.CaptureNote ?? "";
        _note.Visibility = string.IsNullOrEmpty(f.CaptureNote) ? Visibility.Collapsed : Visibility.Visible;

        var api = f.Frames?.Api switch { PresentApi.Dxgi => " · DXGI", PresentApi.D3D9 => " · D3D9", _ => "" };
        _target.Text = f.Target is null ? "" : f.Target + api;
        _target.Visibility = l.Target && f.Target is not null ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// Transparent, click-through, topmost, never-activated window (same window styles as the crosshair overlay). It's a normal
/// desktop window: nothing is injected into the game, so it shows over borderless/windowed games but not over true
/// exclusive fullscreen.
/// </summary>
public sealed class PerformanceOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint h, int i, int v);
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    public OverlayPanel Panel { get; } = new();

    public PerformanceOverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Money Tweaks performance overlay";
        Content = Panel;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        _topmost.Tick += (_, _) => { Topmost = false; Topmost = true; };
        _topmost.Start();
        Closed += (_, _) => _topmost.Stop();
    }

    /// <summary>Places the window in a corner of the given monitor (physical pixels), keeping the margin.</summary>
    public void Place(OverlayCorner corner, double margin, (int x, int y, int w, int h) monitor)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double mx = monitor.x / dpi.DpiScaleX, my = monitor.y / dpi.DpiScaleY, mw = monitor.w / dpi.DpiScaleX, mh = monitor.h / dpi.DpiScaleY;
        double w = ActualWidth > 0 ? ActualWidth : Panel.DesiredSize.Width, h = ActualHeight > 0 ? ActualHeight : Panel.DesiredSize.Height;
        double left = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight ? mx + mw - w - margin : mx + margin;
        double top = corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? my + mh - h - margin : my + margin;
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
    }
}
