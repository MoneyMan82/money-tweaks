using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks.Controls;

/// <summary>
/// Time-series chart for the Hardware Monitor. Draws real samples from <see cref="MetricHistory"/> (gaps stay gaps) over the
/// selected window; long windows are reduced to a min/max envelope per pixel so spikes never disappear.
///
/// Motion is cheap by design: the plot is redrawn once per sample, then slid left by one sample's width with a transform
/// animation that the render thread runs on its own, which reads as a continuously scrolling line. The scale eases when it
/// changes, a new range wipes in, and hovering shows a cursor with the time and every series' value. No motion with
/// reduced motion on.
/// </summary>
public sealed class LiveChart : Grid
{
    public sealed record Series(MetricInfo Info, Brush Stroke);

    private readonly List<Series> _series = [];
    private readonly PlotLayer _plot;
    private readonly GuideLayer _guides = new();
    private readonly TranslateTransform _shift = new();
    private readonly RectangleGeometry _wipe = new();
    private readonly Canvas _hover = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Rectangle _cursor = new() { Width = 1, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) };
    private readonly List<Rectangle> _markers = [];
    private readonly Border _tip;
    private readonly StackPanel _tipBody = new();
    private readonly TextBlock _axisTop = Axis(), _axisMid = Axis(), _axisLeft = Axis(), _axisRight = Axis();
    private DateTime _newest = DateTime.UtcNow;
    private bool _hovering;
    private Point _mouse;

    /// <summary>Visible history in seconds.</summary>
    public double RangeSeconds { get; private set; } = 60;
    /// <summary>Fixed scale (100 for percentages); null scales to the data.</summary>
    public double? FixedMax { get; set; }
    /// <summary>Small sparkline: no axis labels and no hover.</summary>
    public bool Compact { get; }

    internal double YMax = 1;
    internal readonly List<List<(long ticks, float value)>> Data = [];
    internal IReadOnlyList<Series> SeriesList => _series;
    internal DateTime Newest => _newest;

    public LiveChart(bool compact = false, double height = 120)
    {
        Compact = compact;
        Height = height;
        ClipToBounds = true;
        Background = Brushes.Transparent;   // hit-testable for hover
        _plot = new PlotLayer(this) { RenderTransform = _shift, Clip = _wipe, IsHitTestVisible = false };
        Children.Add(_guides);
        Children.Add(_plot);
        if (!compact)
        {
            _axisTop.VerticalAlignment = VerticalAlignment.Top;
            _axisMid.VerticalAlignment = VerticalAlignment.Center;
            _axisLeft.VerticalAlignment = _axisRight.VerticalAlignment = VerticalAlignment.Bottom;
            _axisRight.HorizontalAlignment = HorizontalAlignment.Right;
            foreach (var a in new[] { _axisTop, _axisMid, _axisLeft, _axisRight }) Children.Add(a);
            _tip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x13, 0x19)), BorderBrush = Ui.Brush("ControlBorderHover"), BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 6, 9, 7), Child = _tipBody,
            };
            _hover.Children.Add(_cursor);
            _hover.Children.Add(_tip);
            Children.Add(_hover);
            MouseMove += (_, e) => { _hovering = true; _mouse = e.GetPosition(this); ShowHover(); };
            MouseLeave += (_, _) => { _hovering = false; _hover.Visibility = Visibility.Collapsed; };
            Cursor = Cursors.Cross;
        }
        else _tip = new Border();
        SizeChanged += (_, _) => { _wipe.Rect = new Rect(0, -4, ActualWidth + 4, ActualHeight + 8); _plot.InvalidateVisual(); };
    }

    private static TextBlock Axis() => new() { FontSize = 10, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(4, 1, 4, 1), IsHitTestVisible = false, FontFamily = Ui.Res<FontFamily>("DisplayFont") };

    public void SetSeries(params Series[] series)
    {
        _series.Clear();
        _series.AddRange(series);
        // Readings with a natural ceiling (100 %, installed RAM, VRAM, the power limit) are drawn against it.
        if (FixedMax is null && series.Length > 0 && series.All(s => s.Info.Max is > 0)) FixedMax = series.Max(s => s.Info.Max);
    }

    /// <summary>Changes the window; the plot wipes in from the left.</summary>
    public void SetRange(double seconds, MetricHistory history, int intervalMs)
    {
        RangeSeconds = seconds;
        Update(history, _newest, intervalMs, glide: false);
        if (!Motion.Enabled || ActualWidth <= 0) return;
        var full = new Rect(0, -4, ActualWidth + 4, ActualHeight + 8);
        _wipe.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, -4, 0, ActualHeight + 8), full, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Redraws with the samples up to <paramref name="newestUtc"/>; <paramref name="glide"/> slides the new sample in.</summary>
    public void Update(MetricHistory history, DateTime newestUtc, int intervalMs, bool glide = true)
    {
        _newest = newestUtc;
        var since = newestUtc.AddSeconds(-RangeSeconds).AddMilliseconds(-2 * intervalMs);
        Data.Clear();
        double max = 0;
        foreach (var s in _series)
        {
            var d = history.Range(s.Info.Id, since);
            Data.Add(d);
            foreach (var (_, v) in d) if (!float.IsNaN(v) && v > max) max = v;
        }
        double target = FixedMax ?? Nice(Math.Max(max * 1.15, MinScale(_series.FirstOrDefault()?.Info.Unit)));
        if (Math.Abs(target - YMax) > 1e-9)
        {
            if (Motion.Enabled && YMax > 0 && Data.Any(d => d.Count > 2))
                Motion.Tween(this, YMax, target, 320, v => { YMax = v; _plot.InvalidateVisual(); });
            else YMax = target;
            UpdateAxis(target);
        }
        else if (_axisTop.Text.Length == 0) UpdateAxis(target);
        _plot.InvalidateVisual();
        double dx = ActualWidth * intervalMs / 1000.0 / RangeSeconds;
        if (glide && Motion.Enabled && dx > 0.4)
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(intervalMs)));
        else _shift.BeginAnimation(TranslateTransform.XProperty, null);
        if (_hovering) ShowHover();
    }

    private void UpdateAxis(double max)
    {
        if (Compact) return;
        var unit = _series.FirstOrDefault()?.Info;
        _axisTop.Text = unit?.Format(max) ?? "";
        _axisMid.Text = unit?.Format(max / 2) ?? "";
        _axisLeft.Text = RangeSeconds >= 3600 ? "−1 h" : RangeSeconds >= 60 ? $"−{RangeSeconds / 60:0} min" : $"−{RangeSeconds:0} s";
        _axisRight.Text = "now";
    }

    private static double MinScale(string? unit) => unit switch { Metric.Celsius => 60, Metric.Mhz => 1000, Metric.Watts => 20, Metric.KBps => 64, Metric.MBps => 1, Metric.Rpm => 1000, _ => 1 };

    /// <summary>Rounds a scale up to a round step (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8 × 10ⁿ) so axis labels stay readable.</summary>
    public static double Nice(double v)
    {
        if (v <= 0 || double.IsNaN(v)) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v))), m = v / p;
        return (m <= 1 ? 1 : m <= 1.5 ? 1.5 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 3 ? 3 : m <= 4 ? 4 : m <= 5 ? 5 : m <= 6 ? 6 : m <= 8 ? 8 : 10) * p;
    }

    internal double X(long ticks) => ActualWidth * (1 - (_newest.Ticks - ticks) / (RangeSeconds * TimeSpan.TicksPerSecond));
    internal double Y(double v) => ActualHeight - 2 - Math.Clamp(v / Math.Max(1e-9, YMax), 0, 1.02) * (ActualHeight - 6);

    private void ShowHover()
    {
        if (Compact || Data.Count == 0 || ActualWidth <= 0) return;
        long t = _newest.Ticks - (long)((1 - _mouse.X / ActualWidth) * RangeSeconds * TimeSpan.TicksPerSecond);
        // Nearest sample in the first series that has data decides the cursor position.
        var lead = Data.FirstOrDefault(d => d.Count > 0);
        if (lead is null) { _hover.Visibility = Visibility.Collapsed; return; }
        int i = Nearest(lead, t);
        long at = lead[i].ticks;
        double x = Math.Round(X(at)) + 0.5;
        _cursor.Height = ActualHeight;
        Canvas.SetLeft(_cursor, x);
        _tipBody.Children.Clear();
        _tipBody.Children.Add(new TextBlock { Text = new DateTime(at, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), FontSize = 10.5, Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 0, 0, 3), FontFamily = Ui.Res<FontFamily>("DisplayFont") });
        while (_markers.Count < _series.Count) { var m = new Rectangle { Width = 7, Height = 7, Stroke = Ui.Brush("Bg"), StrokeThickness = 1.5 }; _markers.Add(m); _hover.Children.Insert(1, m); }
        for (int s = 0; s < _series.Count; s++)
        {
            var d = Data[s];
            double? v = null;
            if (d.Count > 0) { int j = Nearest(d, at); if (Math.Abs(d[j].ticks - at) < TimeSpan.TicksPerSecond * 3 && !float.IsNaN(d[j].value)) v = d[j].value; }
            var mk = _markers[s];
            mk.Fill = _series[s].Stroke;
            mk.Visibility = v is null ? Visibility.Collapsed : Visibility.Visible;
            if (v is { } vv) { Canvas.SetLeft(mk, x - 3.5); Canvas.SetTop(mk, Y(vv) - 3.5); }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Rectangle { Width = 7, Height = 7, Fill = _series[s].Stroke, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = (_series.Count > 1 ? _series[s].Info.Label + "  " : ""), FontSize = 11.5, Foreground = Ui.Brush("Muted") });
            row.Children.Add(new TextBlock { Text = _series[s].Info.Format(v), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
            _tipBody.Children.Add(row);
        }
        for (int k = _series.Count; k < _markers.Count; k++) _markers[k].Visibility = Visibility.Collapsed;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _tip.DesiredSize.Width;
        Canvas.SetLeft(_tip, x + 10 + tw > ActualWidth ? x - 10 - tw : x + 10);
        Canvas.SetTop(_tip, 4);
        _hover.Visibility = Visibility.Visible;
    }

    private static int Nearest(List<(long ticks, float value)> d, long t)
    {
        int lo = 0, hi = d.Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (d[mid].ticks < t) lo = mid + 1; else hi = mid; }
        if (lo > 0 && Math.Abs(d[lo - 1].ticks - t) < Math.Abs(d[lo].ticks - t)) lo--;
        return lo;
    }

    /// <summary>Faint horizontal guides at 0, ½ and full scale.</summary>
    private sealed class GuideLayer : FrameworkElement
    {
        private static readonly Pen Line = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        private static readonly Pen Base = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        private static Pen Frozen(Color c) { var p = new Pen(new SolidColorBrush(c), 1); p.Freeze(); return p; }
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawLine(Line, new Point(0, 0.5), new Point(w, 0.5));
            dc.DrawLine(Line, new Point(0, Math.Round(h / 2) + 0.5), new Point(w, Math.Round(h / 2) + 0.5));
            dc.DrawLine(Base, new Point(0, h - 1.5), new Point(w, h - 1.5));
            // Vertical ticks every quarter of the window.
            for (int i = 1; i < 4; i++) dc.DrawLine(Line, new Point(Math.Round(w * i / 4) + 0.5, h - 5), new Point(Math.Round(w * i / 4) + 0.5, h - 1.5));
        }
    }

    private sealed class PlotLayer(LiveChart owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            double w = owner.ActualWidth, h = owner.ActualHeight;
            if (w <= 0 || h <= 0) return;
            for (int s = 0; s < owner.SeriesList.Count && s < owner.Data.Count; s++)
            {
                var data = owner.Data[s];
                if (data.Count == 0) continue;
                var stroke = owner.SeriesList[s].Stroke;
                var line = new StreamGeometry();
                var fill = new StreamGeometry();
                using (var lc = line.Open())
                using (var fc = fill.Open())
                {
                    bool open = false; double firstX = 0, lastX = 0;
                    void Begin(Point p) { if (open) EndFill(); lc.BeginFigure(p, false, false); fc.BeginFigure(new Point(p.X, h - 1.5), true, true); fc.LineTo(p, false, false); open = true; firstX = p.X; lastX = p.X; }
                    void To(Point p) { lc.LineTo(p, true, false); fc.LineTo(p, false, false); lastX = p.X; }
                    void EndFill() { fc.LineTo(new Point(lastX, h - 1.5), false, false); }
                    // Envelope: more samples than pixels → min and max per pixel column.
                    bool envelope = data.Count > w * 1.5;
                    int col = int.MinValue; double cMin = 0, cMax = 0; bool gap = true;
                    foreach (var (t, v) in data)
                    {
                        if (float.IsNaN(v)) { if (envelope && col != int.MinValue && !gap) Flush(); gap = true; col = int.MinValue; continue; }
                        double x = owner.X(t);
                        if (!envelope)
                        {
                            var p = new Point(x, owner.Y(v));
                            if (gap) { Begin(p); gap = false; } else To(p);
                            continue;
                        }
                        int c = (int)Math.Floor(x);
                        if (c != col)
                        {
                            if (col != int.MinValue && !gap) Flush();
                            col = c; cMin = cMax = v;
                        }
                        else { cMin = Math.Min(cMin, v); cMax = Math.Max(cMax, v); }
                        void Flush()
                        {
                            var a = new Point(col + 0.5, owner.Y(cMax));
                            if (gap) { Begin(a); gap = false; } else To(a);
                            if (cMin != cMax) To(new Point(col + 0.5, owner.Y(cMin)));
                        }
                    }
                    if (envelope && col != int.MinValue)
                    {
                        var a = new Point(col + 0.5, owner.Y(cMax));
                        if (gap) Begin(a); else To(a);
                        if (cMin != cMax) To(new Point(col + 0.5, owner.Y(cMin)));
                    }
                    if (open) EndFill();
                    _ = firstX;
                }
                line.Freeze(); fill.Freeze();
                if (s == 0 || owner.SeriesList.Count <= 2)
                    dc.DrawGeometry(Fade(stroke, (byte)(owner.SeriesList.Count > 1 ? 0x22 : 0x3A)), null, fill);
                dc.DrawGeometry(null, Pen(stroke, owner.Compact ? 1.5 : 1.8), line);
            }
        }

        private static readonly Dictionary<(Brush, byte), Brush> Fades = [];
        private static readonly Dictionary<(Brush, double), Pen> Pens = [];
        private static Brush Fade(Brush b, byte alpha)
        {
            if (Fades.TryGetValue((b, alpha), out var f)) return f;
            var c = b is SolidColorBrush sc ? sc.Color : Color.FromRgb(0x8A, 0x6B, 0xFF);
            var g = new LinearGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            g.Freeze();
            return Fades[(b, alpha)] = g;
        }
        private static Pen Pen(Brush b, double w)
        {
            if (Pens.TryGetValue((b, w), out var p)) return p;
            p = new Pen(b, w) { LineJoin = PenLineJoin.Miter, MiterLimit = 2 };
            p.Freeze();
            return Pens[(b, w)] = p;
        }
    }
}
