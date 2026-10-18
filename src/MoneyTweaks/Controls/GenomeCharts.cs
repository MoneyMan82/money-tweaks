using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MoneyTweaks.Core.Genome;

namespace MoneyTweaks.Controls;

/// <summary>
/// Performance frontier: FPS change (x) against a cost such as GPU temperature or power (y). Measured configurations are
/// filled dots with 95% interval whiskers; the Pareto front (nothing measured is both faster and cheaper) is connected;
/// predicted, untested candidates are hollow and never on the front. Click a point to select it.
/// </summary>
public sealed class FrontierChart : Border
{
    private readonly Canvas _c = new() { ClipToBounds = true };
    private IReadOnlyList<FrontierPoint> _pts = [];
    private string _xTitle = "", _yTitle = "";
    private string? _selected;
    public event Action<FrontierPoint>? PointClicked;
    public const double H = 340;

    public FrontierChart()
    {
        Height = H;
        Child = _c;
        SizeChanged += (_, _) => Draw();
    }

    public void Show(IReadOnlyList<FrontierPoint> pts, string xTitle, string yTitle, string? selectedKey)
    {
        _pts = pts; _xTitle = xTitle; _yTitle = yTitle; _selected = selectedKey;
        Draw();
    }

    private void Draw()
    {
        _c.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 50 || h < 50) return;
        const double left = 64, right = 18, top = 14, bottom = 44;
        double pw = w - left - right, ph = h - top - bottom;
        if (_pts.Count == 0)
        {
            Add(new TextBlock { Text = "No measured configurations yet. Run an experiment to map this PC.", Foreground = Ui.Brush("Muted"), FontSize = 13 }, left + 10, top + ph / 2 - 10);
            return;
        }
        double x0 = _pts.Min(p => p.X - p.XCi), x1 = _pts.Max(p => p.X + p.XCi), y0 = _pts.Min(p => p.Y - p.YCi), y1 = _pts.Max(p => p.Y + p.YCi);
        x0 = Math.Min(x0, 0); x1 = Math.Max(x1, 0); y0 = Math.Min(y0, 0); y1 = Math.Max(y1, 0);
        double padX = Math.Max((x1 - x0) * 0.08, 0.5), padY = Math.Max((y1 - y0) * 0.08, 0.5);
        x0 -= padX; x1 += padX; y0 -= padY; y1 += padY;
        double X(double v) => left + (v - x0) / (x1 - x0) * pw;
        double Y(double v) => top + (1 - (v - y0) / (y1 - y0)) * ph;

        // Quadrant shading: faster and cheaper is the good corner.
        var good = new Rectangle { Width = Math.Max(0, left + pw - X(0)), Height = Math.Max(0, top + ph - Y(0)), Fill = Ui.Brush("SuccessSoft"), Opacity = 0.35 };
        Add(good, X(0), Y(0));
        Add(new TextBlock { Text = "faster and cooler / cheaper", Foreground = Ui.Brush("Success"), FontSize = 10.5, Opacity = 0.8 }, left + pw - 160, top + ph - 18);

        foreach (var (v, horizontal) in Ticks(x0, x1).Select(t => (t, false)).Concat(Ticks(y0, y1).Select(t => (t, true))))
        {
            var line = horizontal ? new Line { X1 = left, X2 = left + pw, Y1 = Y(v), Y2 = Y(v) } : new Line { X1 = X(v), X2 = X(v), Y1 = top, Y2 = top + ph };
            line.Stroke = Ui.Brush("BorderBrush"); line.StrokeThickness = v == 0 ? 1.4 : 0.6;
            if (v == 0) line.Stroke = Ui.Brush("Subtle");
            _c.Children.Add(line);
            var lbl = new TextBlock { Text = v.ToString("+0.#;−0.#;0", CultureInfo.CurrentCulture), Foreground = Ui.Brush("Subtle"), FontSize = 10.5 };
            if (horizontal) { lbl.Width = 30; lbl.TextAlignment = TextAlignment.Right; Add(lbl, left - 36, Y(v) - 8); }
            else Add(lbl, X(v) - 10, top + ph + 6);
        }
        Add(new TextBlock { Text = _xTitle, Foreground = Ui.Brush("Muted"), FontSize = 11.5 }, left + pw / 2 - 80, h - 20);
        var yt = new TextBlock { Text = _yTitle, Foreground = Ui.Brush("Muted"), FontSize = 11.5, RenderTransform = new RotateTransform(-90) };
        Add(yt, 0, top + ph / 2 + 80);

        // Pareto front.
        var front = _pts.Where(p => p.Pareto).OrderBy(p => p.X).ToList();
        if (front.Count > 1)
        {
            var pl = new Polyline { Stroke = Ui.Brush("Accent"), StrokeThickness = 2, StrokeDashArray = [4, 3], Opacity = 0.8 };
            foreach (var p in front) pl.Points.Add(new Point(X(p.X), Y(p.Y)));
            _c.Children.Add(pl);
        }
        var placed = new List<Point>();
        foreach (var p in _pts.OrderBy(p => p.Measured))
        {
            double cx = X(p.X), cy = Y(p.Y);
            var tone = !p.Measured ? Ui.Brush("Violet") : p.Label == "Your settings" ? Ui.Brush("Gold") : p.Pareto ? Ui.Brush("Accent") : Ui.Brush("Muted");
            // 95% whiskers.
            _c.Children.Add(new Line { X1 = X(p.X - p.XCi), X2 = X(p.X + p.XCi), Y1 = cy, Y2 = cy, Stroke = tone, StrokeThickness = 1, Opacity = 0.55 });
            _c.Children.Add(new Line { X1 = cx, X2 = cx, Y1 = Y(p.Y - p.YCi), Y2 = Y(p.Y + p.YCi), Stroke = tone, StrokeThickness = 1, Opacity = 0.55 });
            double r = p.Measured ? 5 + Math.Min(4, p.N) : 6;
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2, Stroke = tone, StrokeThickness = p.Key == _selected ? 3 : 1.6,
                Fill = p.Measured ? tone : Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = $"{(p.Label.Length > 0 ? p.Label + "\n" : "")}{(p.Measured ? $"Measured, {p.N} run(s)" : "PREDICTED, not measured")}\nFPS {p.X:+0.0;−0.0}% (±{p.XCi:0.0})\n{_yTitle}: {p.Y:+0.0;−0.0} (±{p.YCi:0.0})",
            };
            if (p.Key == _selected) dot.Stroke = Ui.Brush("Text");
            dot.MouseLeftButtonUp += (_, _) => PointClicked?.Invoke(p);
            Add(dot, cx - r, cy - r);
            if (p.Label.Length > 0 && p.Measured)
            {
                // Simple collision avoidance: move a label down (then up) until it doesn't overlap one already placed.
                double lx = cx + r + 4, ly = cy - 8;
                for (int k = 0; k < 6 && placed.Any(q => Math.Abs(q.X - lx) < 150 && Math.Abs(q.Y - ly) < 14); k++) ly = cy - 8 + (k % 2 == 0 ? 1 : -1) * 15 * (k / 2 + 1);
                placed.Add(new Point(lx, ly));
                Add(new TextBlock { Text = p.Label, Foreground = Ui.Brush("Text"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis }, lx, ly);
            }
        }
    }

    private void Add(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); _c.Children.Add(e); }

    private static IEnumerable<double> Ticks(double a, double b)
    {
        double span = b - a, raw = span / 5, mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        for (double v = Math.Ceiling(a / step) * step; v <= b; v += step) yield return Math.Round(v / step) * step;
    }
}

/// <summary>Diverging bars: each metric's measured change vs. your settings for up to 3 configurations, with 95% whiskers.
/// Bars point right when the change is better for that metric.</summary>
public sealed class CompareChart : StackPanel
{
    public static readonly string[] Palette = ["Accent", "Violet", "Gold"];

    public void Show(IReadOnlyList<(string name, ConfigEvidence e)> items)
    {
        Children.Clear();
        if (items.Count == 0) { Children.Add(Ui.Muted("Pick profiles or click points on the frontier to compare them.", 12)); return; }
        var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        for (int i = 0; i < items.Count; i++)
        {
            legend.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = Ui.Brush(Palette[i]), Margin = new Thickness(0, 2, 6, 0) });
            legend.Children.Add(new TextBlock { Text = $"{items[i].name} ({items[i].e.N} runs)", Foreground = Ui.Brush("Text"), FontSize = 12, Margin = new Thickness(0, 0, 16, 0) });
        }
        Children.Add(legend);
        foreach (var m in GenomeMetrics.All)
        {
            var vals = items.Select(x => x.e.Measured.TryGetValue(m.Key, out var s) ? s : null).ToList();
            if (vals.All(v => v is null)) continue;
            // Displayed in "goodness" units: % for ratio metrics, units for delta metrics, sign flipped so right = better.
            double Good(double y) => (m.Kind == MetricKind.Ratio ? (Math.Exp(y) - 1) * 100 : y) * (m.HigherIsBetter ? 1 : -1);
            double Ci(MetricSummary s) => m.Kind == MetricKind.Ratio ? s.Ci95 * 100 : s.Ci95;
            double scale = Math.Max(1, vals.Where(v => v is not null).Max(v => Math.Abs(Good(v!.Mean)) + Ci(v!)));
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = m.Label + (m.Kind == MetricKind.Ratio ? " (%)" : $" ({m.Unit})"), Foreground = Ui.Brush("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Source: " + m.Source };
            row.Children.Add(label);
            var bars = new Canvas { Height = 17 * items.Count + 4, ClipToBounds = true };
            Grid.SetColumn(bars, 1);
            row.Children.Add(bars);
            bars.SizeChanged += (_, _) => Paint();
            void Paint()
            {
                bars.Children.Clear();
                double w = bars.ActualWidth, mid = (w - 120) / 2, half = Math.Max(10, mid - 8);
                bars.Children.Add(Pos(new Line { X1 = 0, X2 = 0, Y1 = 0, Y2 = bars.Height, Stroke = Ui.Brush("Subtle"), StrokeThickness = 1 }, mid, 0));
                for (int i = 0; i < items.Count; i++)
                {
                    if (vals[i] is not { } s) continue;
                    double g = Good(s.Mean), ci = Ci(s), y = 2 + i * 17;
                    double len = Math.Abs(g) / scale * half;
                    var bar = new Rectangle { Width = Math.Max(1, len), Height = 10, RadiusX = 2, RadiusY = 2, Fill = Ui.Brush(Palette[i]), Opacity = Math.Abs(g) > ci ? 1 : 0.45 };
                    bars.Children.Add(Pos(bar, g >= 0 ? mid : mid - len, y));
                    double a = mid + (g - ci) / scale * half, b = mid + (g + ci) / scale * half;
                    bars.Children.Add(Pos(new Line { X1 = 0, X2 = b - a, Y1 = 5, Y2 = 5, Stroke = Ui.Brush("Text"), StrokeThickness = 1, Opacity = 0.7 }, a, y));
                    var txt = new TextBlock { Text = $"{m.Change(s.Mean)} ±{(m.Kind == MetricKind.Ratio ? $"{ci:0.0}%" : $"{ci:0.0}")}", FontSize = 10.5, Foreground = Ui.Brush(Palette[i]) };
                    bars.Children.Add(Pos(txt, w - 110, y - 3));
                }
            }
            Children.Add(row);
        }
        Children.Add(Ui.Muted("Bars point right when the change is better for that metric. Faded bars are within the 95% interval of no change.", 11.5));
    }

    private static UIElement Pos(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }
}

/// <summary>The evolving hardware profile: what each setting level does on this PC, per metric, with clear effects in
/// colour (green better, red worse) and unclear ones greyed out.</summary>
public sealed class EffectMap : Grid
{
    private static readonly string[] Keys = ["fps", "low1", "gputemp", "gpupower", "cputemp", "cpupower"];

    public void Show(IReadOnlyList<HardwareFact> facts)
    {
        Children.Clear(); RowDefinitions.Clear(); ColumnDefinitions.Clear();
        var keys = Keys.Where(k => facts.Any(f => f.Metric.Key == k)).ToList();
        if (facts.Count == 0 || keys.Count == 0) { Children.Add(Ui.Muted("Nothing learned yet. Each experiment adds to this map.", 12)); return; }
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        foreach (var _ in keys) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        void Cell(UIElement e, int r, int c) { SetRow(e, r); SetColumn(e, c); Children.Add(e); }
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < keys.Count; c++)
            Cell(new TextBlock { Text = GenomeMetrics.Get(keys[c]).Label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 6) }, 0, c + 1);
        int row = 1;
        foreach (var grp in facts.GroupBy(f => (f.Factor.Id, f.Level.Id)))
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var first = grp.First();
            Cell(new TextBlock { Text = $"{first.Factor.Name}: {first.Level.Label}", FontSize = 12, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 2, 12, 2), VerticalAlignment = VerticalAlignment.Center }, row, 0);
            for (int c = 0; c < keys.Count; c++)
            {
                var f = grp.FirstOrDefault(x => x.Metric.Key == keys[c]);
                if (f is null) continue;
                double good = f.Metric.Points(f.Mean);
                var tone = !f.Clear ? "Subtle" : good >= 0 ? "Success" : "Danger";
                double ci = 1.96 * f.Sd;
                var text = $"{f.Metric.Change(f.Mean)}";
                var b = new Border
                {
                    Background = Ui.Brush(f.Clear ? (good >= 0 ? "SuccessSoft" : "DangerSoft") : "Surface"), CornerRadius = new CornerRadius(5), Margin = new Thickness(3, 2, 3, 2), Padding = new Thickness(6, 3, 6, 3),
                    Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Ui.Brush(tone), FontWeight = f.Clear ? FontWeights.SemiBold : FontWeights.Normal },
                    ToolTip = $"{f.Factor.Name} {f.Level.Label} vs. your setting: {f.Metric.Label} {text} (95% ±{(f.Metric.Kind == MetricKind.Ratio ? $"{ci * 100:0.0}%" : $"{ci:0.0} {f.Metric.Unit}")}), from {f.Support} trial(s).{(f.Clear ? "" : " Not clearly different from no change.")}",
                };
                Cell(b, row, c + 1);
            }
            row++;
        }
    }
}
