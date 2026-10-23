using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Controls;

/// <summary>Everything one overlay refresh shows. Null values are "not available" and are shown as such, never guessed.</summary>
public sealed record OverlayFrame(
    PresentStats? Frames, CaptureState Capture, string? CaptureNote,
    double? CpuPercent, double? GpuPercent, double? RamUsedGb, double? RamTotalGb, double? CpuTempC, double? GpuTempC,
    string? Target, GameWindowMode Mode)
{
    public double? VramUsedMb { get; init; }
    public double? VramTotalMb { get; init; }
    public double? PingMs { get; init; }
    /// <summary>Why ping is missing ("timeout", "no network"), or null.</summary>
    public string? PingNote { get; init; }
    /// <summary>Frame times (ms) of the last ~2 seconds for the graph, oldest first.</summary>
    public IReadOnlyList<double>? GraphMs { get; init; }
    public double CpuWarnC { get; init; } = 90;
    public double GpuWarnC { get; init; } = 83;
}

/// <summary>
/// Shared 30 fps ticker for the overlay's small animations (number transitions, graph glide). Runs only while something is
/// animating, so an idle overlay redraws only when its values change.
/// </summary>
internal static class OverlayAnimator
{
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private static readonly List<Func<bool>> Steps = [];
    static OverlayAnimator() => Timer.Tick += (_, _) =>
    {
        for (int i = Steps.Count - 1; i >= 0; i--) if (!Steps[i]()) Steps.RemoveAt(i);
        if (Steps.Count == 0) Timer.Stop();
    };
    /// <summary>Adds a step that returns false when it's finished.</summary>
    public static void Run(Func<bool> step) { Steps.Add(step); if (!Timer.IsEnabled) Timer.Start(); }
    public static bool Enabled => Motion.Enabled;
}

/// <summary>
/// Crisp overlay text with an optional dark outline (the glyphs drawn around themselves in black), so readings stay
/// legible over bright snow, sky or menus as well as dark scenes. Glyph runs are cached by WPF; no geometry is built.
/// </summary>
public sealed class OverlayText : FrameworkElement
{
    private FormattedText? _ft;
    private string _text = "";
    private Brush _fill = Brushes.White;
    private static readonly Brush Shadow = Frozen(Color.FromArgb(0xE6, 0, 0, 0));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public double Size { get; set; } = 13;
    public FontWeight Weight { get; set; } = FontWeights.Normal;
    public FontFamily Family { get; set; } = new("Bahnschrift, Segoe UI");
    public bool Outline { get; set; } = true;

    public string Text { get => _text; set { if (value == _text) return; _text = value; _ft = null; InvalidateMeasure(); InvalidateVisual(); } }
    public Brush Fill { get => _fill; set { if (ReferenceEquals(value, _fill)) return; _fill = value; _ft = null; InvalidateVisual(); } }

    public void Restyle() { _ft = null; InvalidateMeasure(); InvalidateVisual(); }

    private FormattedText? _shadow;
    private FormattedText Make(Brush b) => new(_text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(Family, FontStyles.Normal, Weight, FontStretches.Normal), Size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private FormattedText Ft() { if (_ft is null) { _ft = Make(_fill); _shadow = null; } return _ft; }

    protected override Size MeasureOverride(Size available) { var f = Ft(); return new Size(Math.Ceiling(f.WidthIncludingTrailingWhitespace) + 2, Math.Ceiling(f.Height) + 2); }

    protected override void OnRender(DrawingContext dc)
    {
        var f = Ft();
        if (Outline)
        {
            var o = _shadow ??= Make(Shadow);
            foreach (var (dx, dy) in new[] { (0.0, 1.0), (2.0, 1.0), (1.0, 0.0), (1.0, 2.0), (2.0, 2.0) }) dc.DrawText(o, new Point(dx, dy));
        }
        dc.DrawText(f, new Point(1, 1));
    }
}

/// <summary>
/// Frame-time graph over the last two seconds, by time (not frame count), so it scrolls at a steady pace. Each refresh
/// glides the line left by the elapsed time; spikes above 1.5× the average are drawn in the warning colour, with the
/// average as a dashed guide and the worst frame labelled.
/// </summary>
public sealed class FrametimeGraph : FrameworkElement
{
    private IReadOnlyList<double> _ms = [];
    private double _shiftMs, _top = 33.4;
    public const double WindowMs = 2000;
    public Brush Line { get; set; } = Brushes.LimeGreen;
    public Brush Spike { get; set; } = Brushes.Orange;
    public Brush Text { get; set; } = Brushes.White;
    public Brush Guide { get; set; } = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    public bool ShowLabel { get; set; } = true;

    public void Set(IReadOnlyList<double> ms, double elapsedMs, bool animate)
    {
        _ms = ms;
        double top = ms.Count > 0 ? Math.Max(33.4, ms.Max() * 1.15) : 33.4;
        if (animate && OverlayAnimator.Enabled && ms.Count > 1)
        {
            double fromTop = _top, start = Environment.TickCount64, dur = Math.Clamp(elapsedMs, 120, 600);
            _shiftMs = Math.Min(elapsedMs, 400);
            double fromShift = _shiftMs;
            OverlayAnimator.Run(() =>
            {
                double p = Math.Min(1, (Environment.TickCount64 - start) / dur), e = 1 - Math.Pow(1 - p, 2);
                _shiftMs = fromShift * (1 - p);
                _top = fromTop + (top - fromTop) * e;
                InvalidateVisual();
                return p < 1;
            });
        }
        else { _shiftMs = 0; _top = top; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(null, new Pen(Guide, 1), new Rect(0.5, 0.5, w - 1, h - 1));
        if (_ms.Count < 2) return;
        double Y(double v) => h - 2 - Math.Min(v, _top) / _top * (h - 4);
        double avg = _ms.Average();
        dc.DrawLine(new Pen(Guide, 1) { DashStyle = DashStyles.Dash }, new Point(1, Math.Round(Y(avg)) + 0.5), new Point(w - 1, Math.Round(Y(avg)) + 0.5));
        // Newest frame at the right edge, older ones to the left by their age. While gliding, the whole line starts shifted
        // right by the time that passed since the last refresh (the new frames just past the edge) and slides into place.
        var pts = new List<(double x, double v)>(_ms.Count);
        double t = -_shiftMs;
        for (int i = _ms.Count - 1; i >= 0 && t <= WindowMs + 50; i--) { pts.Add((w - 1 - t / WindowMs * (w - 2), _ms[i])); t += _ms[i]; }
        pts.Reverse();
        var line = new StreamGeometry();
        var spikes = new StreamGeometry();
        using (var c = line.Open())
        using (var sc = spikes.Open())
        {
            c.BeginFigure(new Point(pts[0].x, Y(pts[0].v)), false, false);
            for (int i = 1; i < pts.Count; i++)
            {
                var p = new Point(pts[i].x, Y(pts[i].v));
                c.LineTo(p, true, false);
                if (pts[i].v > avg * 1.5 && pts[i].v > 8) { sc.BeginFigure(new Point(pts[i].x, h - 2), false, false); sc.LineTo(p, true, false); }
            }
        }
        line.Freeze(); spikes.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(1, 1, w - 2, h - 2)));
        dc.DrawGeometry(null, new Pen(Spike, 1.4), spikes);
        dc.DrawGeometry(null, new Pen(Line, 1.5) { LineJoin = PenLineJoin.Miter, MiterLimit = 2 }, line);
        dc.Pop();
        if (ShowLabel && h >= 26)
        {
            var max = _ms.Max();
            var ft = new FormattedText($"max {max.ToString("0.0", CultureInfo.InvariantCulture)} ms", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Bahnschrift, Segoe UI"), 9.5, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(w - ft.Width - 4, 2));
        }
    }
}

/// <summary>A number that eases to each new value (about 180 ms at 30 fps), then formats itself into its text.</summary>
internal sealed class TweenNumber(Action<double> show)
{
    private double _cur = double.NaN;
    public void To(double v, bool animate)
    {
        if (double.IsNaN(_cur) || !animate || !OverlayAnimator.Enabled || Math.Abs(v - _cur) < 1e-6) { _cur = v; show(v); return; }
        double from = _cur, start = Environment.TickCount64;
        _cur = v;
        OverlayAnimator.Run(() =>
        {
            double p = Math.Min(1, (Environment.TickCount64 - start) / 180.0);
            show(from + (v - from) * (1 - Math.Pow(1 - p, 3)));
            return p < 1 && _cur == v;   // a newer target takes over
        });
    }
    public void Reset() => _cur = double.NaN;
}

/// <summary>The overlay's content. Used by the overlay window and by the live preview on the settings page.</summary>
public sealed class OverlayPanel : Border
{
    private OverlayLayout _layout = new();
    private OverlayTheme.Palette _theme = OverlayTheme.All[0];
    private Brush _text = Brushes.White, _muted = Brushes.Gray, _accent = Brushes.Violet, _accent2 = Brushes.Cyan, _warn = Brushes.Orange;
    private readonly Grid _root = new();
    private readonly Rectangle _topLine = new() { Height = 2, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly List<Item> _items = [];
    private FrametimeGraph? _graph;
    private OverlayText? _target, _note;
    private long _lastUpdate;

    private sealed class Item
    {
        public string Id = "";
        public OverlayText Value = new();
        public OverlayText? Unit;
        public Rectangle? Bar;
        public ScaleTransform? BarScale;
        public TweenNumber? Tween;
        public TweenNumber? BarTween;
    }

    public OverlayPanel()
    {
        Padding = new Thickness(0);
        BorderThickness = new Thickness(1);
        Child = _root;
        LayoutTransform = _scale;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public OverlayLayout Layout => _layout;

    /// <summary>Changes only the size (live while the scale grip is dragged; no rebuild).</summary>
    public void SetScale(double scale) => _scale.ScaleX = _scale.ScaleY = Math.Clamp(scale, 0.6, 2.5);

    private static Brush BrushFrom(string hex, Color fallback, byte? alpha = null)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = fallback; }
        if (alpha is { } a) c.A = a;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Builds the overlay for a layout: style, theme, order of readings, scale and opacity.</summary>
    public void ApplyLayout(OverlayLayout l)
    {
        _layout = l;
        _theme = OverlayTheme.Resolve(l);
        _text = BrushFrom(_theme.Text, Colors.White);
        _muted = BrushFrom(_theme.Text, Colors.White, 0xA8);
        _accent = BrushFrom(_theme.Accent, Colors.MediumPurple);
        _accent2 = BrushFrom(_theme.Accent2, Colors.Cyan);
        _warn = BrushFrom(_theme.Warn, Colors.Orange);
        var plate = BrushFrom(_theme.Plate, Colors.Black, (byte)Math.Round(Math.Clamp(l.Opacity, 0, 1) * 255));
        Background = plate;
        BorderBrush = l.Opacity < 0.05 ? Brushes.Transparent : BrushFrom(_theme.Accent, Colors.MediumPurple, (byte)Math.Round(0x55 * Math.Clamp(l.Opacity * 1.6, 0.3, 1)));
        _topLine.Fill = new LinearGradientBrush(((SolidColorBrush)_accent).Color, ((SolidColorBrush)_accent2).Color, 0);
        _topLine.Visibility = l.Opacity < 0.05 ? Visibility.Collapsed : Visibility.Visible;
        _scale.ScaleX = _scale.ScaleY = Math.Clamp(l.Scale, 0.6, 2.5);
        Rebuild();
    }

    private double Fs => Math.Clamp(_layout.FontSize, 8, 40);

    private OverlayText T(double size, Brush fill, FontWeight? weight = null) => new()
    { Size = size, Fill = fill, Weight = weight ?? FontWeights.Normal, Outline = _layout.Outline, VerticalAlignment = VerticalAlignment.Bottom };

    private static bool IsNumber(string id) => id is not (OverlayMetric.Graph or OverlayMetric.Target);

    private void Rebuild()
    {
        _items.Clear();
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _graph = null; _target = null; _note = null;
        var items = _layout.EffectiveItems();
        var body = new StackPanel { Margin = new Thickness(9, 7, 10, 7) };
        _root.Children.Add(body);
        _root.Children.Add(_topLine);
        bool graphOn = items.Contains(OverlayMetric.Graph) && _layout.Graphs && _layout.Style != OverlayStyle.Minimal;

        switch (_layout.Style)
        {
            case OverlayStyle.Minimal:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var id in items.Where(IsNumber))
                {
                    var it = NewItem(id, id == OverlayMetric.Fps ? Fs * 1.5 : Fs, unitSize: Fs * 0.72);
                    var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, Fs * 0.8, 0), VerticalAlignment = VerticalAlignment.Bottom };
                    cell.Children.Add(it.Value);
                    if (it.Unit is not null) { it.Unit.Margin = new Thickness(2, 0, 0, 1); cell.Children.Add(it.Unit); }
                    row.Children.Add(cell);
                }
                body.Children.Add(row);
                break;
            }
            case OverlayStyle.Compact:
            {
                var wrap = new WrapPanel { MaxWidth = Fs * 34 };
                foreach (var id in items.Where(IsNumber))
                {
                    var it = NewItem(id, id == OverlayMetric.Fps ? Fs * 1.3 : Fs, unitSize: Fs * 0.72);
                    var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, Fs * 0.9, 2) };
                    var label = T(Fs * 0.72, _muted, FontWeights.SemiBold);
                    label.Text = ShortLabel(id);
                    label.Margin = new Thickness(0, 0, 4, 1);
                    if (id != OverlayMetric.Fps) cell.Children.Add(label);
                    cell.Children.Add(it.Value);
                    if (it.Unit is not null) { it.Unit.Margin = new Thickness(2, 0, 0, 1); cell.Children.Add(it.Unit); }
                    wrap.Children.Add(cell);
                }
                body.Children.Add(wrap);
                if (graphOn) body.Children.Add(_graph = NewGraph(Fs * 2.1, Fs * 20));
                break;
            }
            default:
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                int r = 0;
                foreach (var id in items.Where(IsNumber))
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var label = T(Fs * 0.74, _muted, FontWeights.SemiBold);
                    label.Text = ShortLabel(id);
                    label.Margin = new Thickness(0, 0, Fs, 2);
                    Grid.SetRow(label, r);
                    grid.Children.Add(label);
                    var it = NewItem(id, id == OverlayMetric.Fps ? Fs * 1.55 : Fs * 1.05, unitSize: Fs * 0.72);
                    var val = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                    val.Children.Add(it.Value);
                    if (it.Unit is not null) { it.Unit.Margin = new Thickness(2, 0, 0, 2); val.Children.Add(it.Unit); }
                    Grid.SetRow(val, r); Grid.SetColumn(val, 1);
                    grid.Children.Add(val);
                    if (_layout.Graphs && id is OverlayMetric.Cpu or OverlayMetric.Gpu or OverlayMetric.Ram or OverlayMetric.Vram)
                    {
                        var track = new Grid { Width = Fs * 4.2, Height = Math.Max(3, Fs * 0.3), Margin = new Thickness(Fs * 0.7, 0, 0, Fs * 0.42), VerticalAlignment = VerticalAlignment.Bottom, Background = BrushFrom(_theme.Text, Colors.White, 0x22) };
                        it.BarScale = new ScaleTransform(0, 1);
                        it.Bar = new Rectangle { Fill = new LinearGradientBrush(((SolidColorBrush)_accent).Color, ((SolidColorBrush)_accent2).Color, 0), RenderTransform = it.BarScale };
                        it.BarTween = new TweenNumber(v => it.BarScale!.ScaleX = Math.Clamp(v, 0, 1));
                        track.Children.Add(it.Bar);
                        Grid.SetRow(track, r); Grid.SetColumn(track, 2);
                        grid.Children.Add(track);
                    }
                    r++;
                }
                body.Children.Add(grid);
                if (graphOn) body.Children.Add(_graph = NewGraph(Fs * 3, Math.Max(Fs * 15, 160)));
                break;
            }
        }
        _note = T(Fs * 0.74, _warn);
        _note.Margin = new Thickness(0, 3, 0, 0);
        _note.Visibility = Visibility.Collapsed;
        body.Children.Add(_note);
        if (items.Contains(OverlayMetric.Target) && _layout.Style != OverlayStyle.Minimal)
        {
            _target = T(Fs * 0.72, _muted);
            _target.Margin = new Thickness(0, 3, 0, 0);
            body.Children.Add(_target);
        }
    }

    private FrametimeGraph NewGraph(double height, double width) => new()
    {
        Height = Math.Clamp(height, 22, 140), Width = Math.Clamp(width, 120, 600), Margin = new Thickness(0, 5, 0, 1), HorizontalAlignment = HorizontalAlignment.Left,
        Line = _accent2, Spike = _warn, Text = _muted, Guide = BrushFrom(_theme.Text, Colors.White, 0x2E), ShowLabel = _layout.Style == OverlayStyle.Detailed,
    };

    private Item NewItem(string id, double size, double unitSize)
    {
        var it = new Item { Id = id, Value = T(size, id == OverlayMetric.Fps ? _accent : _text, FontWeights.SemiBold) };
        string unit = id switch
        {
            OverlayMetric.Fps => "FPS", OverlayMetric.Low1 => _layout.Style == OverlayStyle.Minimal ? "1%" : "FPS", OverlayMetric.FrameTime or OverlayMetric.Ping => "ms",
            OverlayMetric.Cpu or OverlayMetric.Gpu => "%", OverlayMetric.CpuTemp or OverlayMetric.GpuTemp => "°C", OverlayMetric.Ram or OverlayMetric.Vram => "GB", _ => "",
        };
        if (unit.Length > 0) { it.Unit = T(unitSize, _muted); it.Unit.Text = unit; }
        var v = it.Value;
        it.Tween = new TweenNumber(x => v.Text = Format(id, x));
        _items.Add(it);
        return it;
    }

    private static string ShortLabel(string id) => id switch
    {
        OverlayMetric.Fps => "FPS", OverlayMetric.FrameTime => "FRAME", OverlayMetric.Low1 => "1% LOW", OverlayMetric.Cpu => "CPU", OverlayMetric.CpuTemp => "CPU °",
        OverlayMetric.Gpu => "GPU", OverlayMetric.GpuTemp => "GPU °", OverlayMetric.Ram => "RAM", OverlayMetric.Vram => "VRAM", OverlayMetric.Ping => "PING", _ => id.ToUpperInvariant(),
    };

    private static string Format(string id, double v) => id switch
    {
        OverlayMetric.FrameTime => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture),
        OverlayMetric.Ram or OverlayMetric.Vram => v.ToString("0.0", CultureInfo.InvariantCulture),
        _ => v.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>Shows one refresh. Missing readings say why in a word ("n/a", "admin") instead of a number.</summary>
    public void Update(OverlayFrame f)
    {
        long now = Environment.TickCount64;
        double elapsed = _lastUpdate == 0 ? 250 : Math.Clamp(now - _lastUpdate, 50, 2000);
        _lastUpdate = now;
        bool animate = true;
        string? fpsMissing = f.Frames is not null ? null : f.Capture switch
        {
            CaptureState.NeedsAdmin => "admin", CaptureState.Failed => "n/a", CaptureState.Running => "—", _ => "off",
        };
        foreach (var it in _items)
        {
            (double? v, string? missing, Brush? tint, double? bar) = it.Id switch
            {
                OverlayMetric.Fps => (f.Frames?.Fps, fpsMissing, null, null),
                OverlayMetric.FrameTime => (f.Frames?.AvgMs, fpsMissing, null, null),
                OverlayMetric.Low1 => (f.Frames?.Low1PercentFps, fpsMissing, null, null),
                OverlayMetric.Cpu => (f.CpuPercent, "n/a", null, f.CpuPercent / 100),
                OverlayMetric.Gpu => (f.GpuPercent, "n/a", null, f.GpuPercent / 100),
                OverlayMetric.CpuTemp => (f.CpuTempC, "n/a", f.CpuTempC >= f.CpuWarnC ? _warn : null, null),
                OverlayMetric.GpuTemp => (f.GpuTempC, "n/a", f.GpuTempC >= f.GpuWarnC ? _warn : null, null),
                OverlayMetric.Ram => (f.RamUsedGb, "n/a", null, f.RamUsedGb / f.RamTotalGb),
                OverlayMetric.Vram => (f.VramUsedMb / 1024, "n/a", null, f.VramUsedMb / f.VramTotalMb),
                OverlayMetric.Ping => (f.PingMs, f.PingNote ?? "n/a", null, null),
                _ => ((double?)null, "n/a", (Brush?)null, (double?)null),
            };
            if (v is { } x && double.IsFinite(x))
            {
                it.Value.Fill = tint ?? (it.Id == OverlayMetric.Fps ? _accent : _text);
                it.Tween!.To(x, animate);
                if (it.Unit is not null) it.Unit.Visibility = Visibility.Visible;
            }
            else
            {
                it.Tween!.Reset();
                it.Value.Text = missing ?? "n/a";
                it.Value.Fill = _muted;
                if (it.Unit is not null) it.Unit.Visibility = Visibility.Collapsed;
            }
            if (it.BarTween is not null) it.BarTween.To(bar is { } b && double.IsFinite(b) ? b : 0, animate);
        }
        if (_graph is not null)
        {
            var ms = f.GraphMs ?? f.Frames?.RecentMs ?? [];
            _graph.Set(ms, elapsed, animate);
            _graph.Visibility = Visibility.Visible;
        }
        if (_note is not null)
        {
            _note.Text = f.CaptureNote ?? "";
            _note.Visibility = string.IsNullOrEmpty(f.CaptureNote) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_target is not null)
        {
            var api = f.Frames?.Api switch { PresentApi.Dxgi => " · DirectX", PresentApi.D3D9 => " · Direct3D 9", PresentApi.Other => " · OpenGL/Vulkan", _ => "" };
            _target.Text = f.Target is null ? "No game detected" : f.Target + api;
        }
    }
}

/// <summary>
/// Transparent, topmost, never-activated overlay window (same window styles as the crosshair overlay). Click-through by
/// default; in edit mode (or with click-through off) it can be dragged anywhere and scaled from its corner grip. It's a
/// normal desktop window: nothing is injected into the game, so it shows over borderless/windowed games but not over true
/// exclusive fullscreen.
/// </summary>
public sealed class PerformanceOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint h, int i, int v);
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Grid _host = new();
    private readonly Border _editFrame;
    private readonly Thumb _grip;
    private nint _hwnd;
    private bool _clickThrough = true;
    public OverlayPanel Panel { get; } = new();
    public bool EditMode { get; private set; }
    /// <summary>The user dropped the overlay somewhere (window position in DIPs).</summary>
    public event Action? Moved;
    /// <summary>The user dragged the scale grip: the new scale.</summary>
    public event Action<double>? Scaled;
    /// <summary>"Done" or "Reset" pressed in edit mode (reset = true).</summary>
    public event Action<bool>? EditFinished;

    public PerformanceOverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Money Tweaks performance overlay";
        _host.Children.Add(Panel);
        // Edit chrome: dashed frame, caption with the controls, scale grip.
        var accent = Ui.Brush("Cyan");
        _editFrame = new Border { BorderBrush = accent, BorderThickness = new Thickness(1.5), Visibility = Visibility.Collapsed, Margin = new Thickness(-3) };
        _host.Children.Add(_editFrame);
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(new TextBlock { Text = "Drag to move · drag ◢ to scale", Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) });
        var done = new Button { Content = "Done", Style = Ui.Res<Style>("PrimaryButton"), Padding = new Thickness(10, 2, 10, 2), FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
        done.Click += (_, _) => EditFinished?.Invoke(false);
        var reset = new Button { Content = "Reset to corner", Style = Ui.Res<Style>("BaseButton"), Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
        reset.Click += (_, _) => EditFinished?.Invoke(true);
        bar.Children.Add(done); bar.Children.Add(reset);
        var caption = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0B, 0x0D, 0x12)), BorderBrush = accent, BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = bar, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        var editLayer = new StackPanel { Visibility = Visibility.Collapsed };
        editLayer.Children.Add(caption);
        _grip = new Thumb { Width = 14, Height = 14, Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, -4, -4) };
        _grip.Template = GripTemplate();
        _grip.DragDelta += (_, e) =>
        {
            var l = Panel.Layout;
            double w = Math.Max(40, Panel.ActualWidth * l.Scale);
            Scaled?.Invoke(Math.Clamp(l.Scale * (w + e.HorizontalChange) / w, 0.6, 2.5));
        };
        _host.Children.Add(_grip);
        var root = new DockPanel();
        DockPanel.SetDock(editLayer, Dock.Top);
        root.Children.Add(editLayer);
        root.Children.Add(_host);
        Content = root;
        _editLayer = editLayer;
        _host.MouseLeftButtonDown += (_, e) =>
        {
            if (!EditMode && _clickThrough) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            Moved?.Invoke();
            e.Handled = true;
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ApplyHitTest();
            Motion.SquareCorners(this);
        };
        _topmost.Tick += (_, _) => { Topmost = false; Topmost = true; };
        _topmost.Start();
        Closed += (_, _) => _topmost.Stop();
    }

    private readonly StackPanel _editLayer;

    private static ControlTemplate GripTemplate()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.BackgroundProperty, Ui.Brush("Cyan"));
        b.SetValue(Border.BorderBrushProperty, Brushes.Black);
        b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        t.VisualTree = b;
        return t;
    }

    /// <summary>Clicks pass through to the game (true), or the overlay takes the mouse so it can be dragged (false).</summary>
    public void SetClickThrough(bool on) { _clickThrough = on; ApplyHitTest(); }

    public void SetEditMode(bool on)
    {
        EditMode = on;
        _editFrame.Visibility = _editLayer.Visibility = _grip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _host.Cursor = on || !_clickThrough ? Cursors.SizeAll : null;
        ApplyHitTest();
    }

    private void ApplyHitTest()
    {
        bool transparent = _clickThrough && !EditMode;
        _host.Background = transparent ? null : Brushes.Transparent;   // hit-testable everywhere over the panel when draggable
        if (_hwnd == 0) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = transparent ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
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

    /// <summary>Places the window's top-left at a fraction of the monitor (physical pixels), kept fully on that monitor.</summary>
    public void PlaceAt(double fx, double fy, (int x, int y, int w, int h) monitor)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double mx = monitor.x / dpi.DpiScaleX, my = monitor.y / dpi.DpiScaleY, mw = monitor.w / dpi.DpiScaleX, mh = monitor.h / dpi.DpiScaleY;
        double w = ActualWidth, h = ActualHeight;
        double left = Math.Clamp(mx + fx * mw, mx, Math.Max(mx, mx + mw - w)), top = Math.Clamp(my + fy * mh, my, Math.Max(my, my + mh - h));
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
    }

    /// <summary>Where the window is now: its display and its top-left as fractions of that display.</summary>
    public (string? device, double fx, double fy) CurrentSpot()
    {
        var dev = GameWindow.DeviceOf(_hwnd);
        var d = GameWindow.Displays().FirstOrDefault(x => x.device == dev);
        if (d.device is null || d.w <= 0) return (null, 0.02, 0.03);
        var dpi = VisualTreeHelper.GetDpi(this);
        double px = Left * dpi.DpiScaleX, py = Top * dpi.DpiScaleY;
        return (dev, Math.Clamp((px - d.x) / d.w, 0, 0.98), Math.Clamp((py - d.y) / d.h, 0, 0.98));
    }
}
