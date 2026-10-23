using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks.Controls;

/// <summary>
/// One live metric (or a few related ones sharing a chart, e.g. download + upload): label, device, big current value,
/// min / average / max over the selected window, the chart, its source on hover, a pin toggle, and a warning state that
/// tints the frame when a monitor warning concerns it.
/// </summary>
public sealed class MetricCard : Border
{
    public static readonly string[] Palette = ["Accent", "Cyan", "Gold", "Success", "Danger"];

    public IReadOnlyList<MetricInfo> Metrics { get; }
    public LiveChart Chart { get; }
    private readonly TextBlock _value = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _stats = Ui.Muted("", 11);
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal };
    private readonly List<TextBlock> _legendValues = [];
    private readonly Border _flag = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _flagText = new() { FontSize = 10, FontWeight = FontWeights.Bold };
    private readonly Rectangle _edge = new() { Height = 2, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false };
    private AlertLevel? _alert;
    private bool _first = true;

    public MetricCard(IReadOnlyList<MetricInfo> metrics, bool compact, bool pinned, Action<string, bool>? onPin, string? title = null, string? subtitle = null)
    {
        Metrics = metrics;
        Style = Ui.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 12, 12);
        Padding = compact ? new Thickness(14, 12, 14, 10) : new Thickness(16, 14, 16, 12);
        _value.FontFamily = Ui.Res<FontFamily>("DisplayFont");
        _value.FontSize = compact ? 21 : 26;
        var m0 = metrics[0];
        Chart = new LiveChart(compact, compact ? 44 : 118);
        Chart.SetSeries([.. metrics.Select((m, i) => new LiveChart.Series(m, Ui.Brush(Palette[i % Palette.Length])))]);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = (title ?? (metrics.Count > 1 ? CommonLabel(metrics) : m0.Label)).ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Brush("Muted"), FontFamily = Ui.Res<FontFamily>("DisplayFont"), VerticalAlignment = VerticalAlignment.Center,
        });
        _flag.Child = _flagText;
        titleRow.Children.Add(_flag);
        names.Children.Add(titleRow);
        string sub = subtitle ?? (compact || title is null ? m0.Device : "");
        if (sub.Length > 0)
            names.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Ui.Brush("Subtle"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        head.Children.Add(names);
        if (onPin is not null && metrics.Count == 1)
        {
            bool isPinned = pinned;
            var pin = new Button
            {
                Style = Ui.Res<Style>("GhostButton"), Tag = pinned, Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Top,
                Content = new TextBlock { Text = pinned ? "\uE77A" : "\uE718", Style = Ui.Res<Style>("Icon"), FontSize = 13 },
                ToolTip = pinned ? "Unpin (remove from Pinned and the compact window)" : "Pin to the top and to the compact window",
            };
            pin.Click += (_, _) => { isPinned = !isPinned; pin.Tag = isPinned; onPin(m0.Id, isPinned); };
            Grid.SetColumn(pin, 1);
            head.Children.Add(pin);
        }
        var body = new StackPanel();
        body.Children.Add(head);
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, compact ? 2 : 4, 0, 0) };
        valueRow.Children.Add(_value);
        body.Children.Add(valueRow);
        if (metrics.Count > 1)
        {
            for (int i = 0; i < metrics.Count; i++)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
                item.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = Ui.Brush(Palette[i % Palette.Length]), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = metrics[i].Label + " ", FontSize = 11.5, Foreground = Ui.Brush("Muted") });
                var val = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold };
                _legendValues.Add(val);
                item.Children.Add(val);
                _legend.Children.Add(item);
            }
            _value.Visibility = Visibility.Collapsed;
            body.Children.Add(new ScrollViewer { Content = _legend, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 2, 0, 0) });
        }
        if (!compact) { _stats.Margin = new Thickness(0, 2, 0, 0); body.Children.Add(_stats); }
        Chart.Margin = new Thickness(0, compact ? 8 : 10, 0, 0);
        body.Children.Add(Chart);
        var root = new Grid();
        root.Children.Add(body);
        _edge.Margin = new Thickness(-Padding.Left, -Padding.Top, -Padding.Right, 0);
        root.Children.Add(_edge);
        Child = root;
        ToolTip = string.Join("\n", metrics.Select(m => $"{m.FullName}: {m.Source}"));
        ToolTipService.SetInitialShowDelay(this, 900);
    }

    private static string CommonLabel(IReadOnlyList<MetricInfo> ms) => string.Join(" / ", ms.Select(m => m.Label));

    /// <summary>Shows the latest values and window statistics, then advances the chart.</summary>
    public void Update(SensorFrame f, MetricHistory history, DateTime since, int intervalMs, bool glide)
    {
        if (Metrics.Count == 1)
        {
            var m = Metrics[0];
            var v = f.Values.GetValueOrDefault(m.Id);
            string text = m.Format(v);
            if (m.Id.EndsWith(".vram") && m.Max is { } total) text += " / " + m.Format(total);
            else if (m.Id.EndsWith(".power") && m.Max is { } limit) text += " / " + m.Format(limit);
            if (_value.Text != text) _value.Text = text;
            if (_stats.Visibility == Visibility.Visible)
            {
                var st = history.Stats(m.Id, since);
                _stats.Text = st.Samples == 0 ? "Collecting…" : $"MIN {m.Format(st.Min)}   ·   AVG {m.Format(st.Average)}   ·   MAX {m.Format(st.Max)}";
            }
        }
        else
        {
            for (int i = 0; i < Metrics.Count; i++)
            {
                var st = history.Stats(Metrics[i].Id, since);
                _legendValues[i].Text = Metrics[i].Format(f.Values.GetValueOrDefault(Metrics[i].Id)) + (Chart.Compact ? "" : $"   (max {Metrics[i].Format(st.Max)})");
            }
            _stats.Text = "";
            _stats.Visibility = Visibility.Collapsed;
        }
        Chart.Update(history, f.Utc, intervalMs, glide && !_first);
        if (_first) { _first = false; if (Motion.Enabled) Chart.SetRange(Chart.RangeSeconds, history, intervalMs); }
    }

    /// <summary>Tints the card for an active warning about this metric (null clears it).</summary>
    public void SetAlert(AlertLevel? level)
    {
        if (_alert == level) return;
        _alert = level;
        if (level is null)
        {
            _flag.Visibility = Visibility.Collapsed;
            _edge.BeginAnimation(OpacityProperty, null); _edge.Opacity = 0;
            ClearValue(BorderBrushProperty);
            return;
        }
        string tone = level == AlertLevel.Critical ? "Danger" : "Gold";
        _flagText.Text = level == AlertLevel.Critical ? "CRITICAL" : level == AlertLevel.Warning ? "WARNING" : "NOTICE";
        _flagText.Foreground = Ui.Brush(tone);
        _flag.Background = Ui.Brush(tone + "Soft");
        _flag.Visibility = Visibility.Visible;
        BorderBrush = Ui.Brush(tone);
        _edge.Fill = Ui.Brush(tone);
        _edge.Opacity = 1;
        if (Motion.Enabled)
            _edge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), EasingFunction = new SineEase() });   // pulses, then stays lit (no endless render loop)
    }
}

/// <summary>A reading this PC can't provide: same footprint as a metric, clearly marked, with the reason. Never a fake value.</summary>
public sealed class UnsupportedCard : Border
{
    public UnsupportedCard(UnsupportedReading u, bool showDevice)
    {
        Style = Ui.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 12, 12);
        Padding = new Thickness(16, 14, 16, 14);
        Opacity = 0.9;
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = u.Label.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Muted"), FontFamily = Ui.Res<FontFamily>("DisplayFont"), VerticalAlignment = VerticalAlignment.Center });
        var tag = Ui.Tag("NOT AVAILABLE", "Muted");
        tag.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(tag);
        sp.Children.Add(head);
        if (showDevice && u.Device.Length > 0) sp.Children.Add(new TextBlock { Text = u.Device, FontSize = 11, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 1, 0, 0) });
        sp.Children.Add(new TextBlock { Text = "—", FontSize = 26, Foreground = Ui.Brush("Subtle"), FontFamily = Ui.Res<FontFamily>("DisplayFont"), Margin = new Thickness(0, 4, 0, 2) });
        sp.Children.Add(new TextBlock { Text = u.Reason, FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
        Child = sp;
    }
}

/// <summary>One logical processor: load bar that eases to its value, load and clock.</summary>
public sealed class CoreCell : Border
{
    private readonly ScaleTransform _bar = new(1, 0);
    private readonly TextBlock _load = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _clock = Ui.Muted("", 10.5);
    public string LoadId { get; }
    public string ClockId { get; }

    public CoreCell(int index, string loadId, string clockId)
    {
        LoadId = loadId; ClockId = clockId;
        BorderBrush = Ui.Brush("ControlBorder");
        BorderThickness = new Thickness(1);
        Background = Ui.Brush("InputFill");
        Margin = new Thickness(0, 0, 6, 6);
        Height = 64;
        _load.FontFamily = Ui.Res<FontFamily>("DisplayFont");
        var g = new Grid { ClipToBounds = true };
        g.Children.Add(new Rectangle { Fill = Ui.Brush("AccentGradient"), Opacity = 0.32, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _bar });
        var top = new Rectangle { Height = 2, Fill = Ui.Brush("AccentGradientH"), VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = new Point(0.5, 1) };
        g.Children.Add(top);
        var sp = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        sp.Children.Add(new TextBlock { Text = $"CPU {index}", FontSize = 10, Foreground = Ui.Brush("Subtle"), FontFamily = Ui.Res<FontFamily>("DisplayFont") });
        sp.Children.Add(_load);
        sp.Children.Add(_clock);
        g.Children.Add(sp);
        Child = g;
    }

    public void Update(double? load, double? clock, string loadText, string clockText)
    {
        _load.Text = loadText;
        _clock.Text = clockText;
        double to = Math.Clamp((load ?? 0) / 100, 0, 1);
        if (Motion.Enabled) _bar.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(280)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        else { _bar.BeginAnimation(ScaleTransform.ScaleYProperty, null); _bar.ScaleY = to; }
        BorderBrush = load >= 90 ? Ui.Brush("Accent") : Ui.Brush("ControlBorder");
    }
}
