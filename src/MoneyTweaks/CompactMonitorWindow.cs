using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks;

/// <summary>
/// Small optional monitoring window with the pinned metrics: value plus a one-minute sparkline each. Can stay on top of
/// other windows (toggle in its title bar), is dragged anywhere, and remembers where it was. Not click-through and not
/// drawn into games (that is the in-game overlay's job); it is a regular desktop window.
/// </summary>
public sealed class CompactMonitorWindow : Window
{
    public static CompactMonitorWindow? Current { get; private set; }
    public static bool IsOpen => Current is not null;
    public static event Action? OpenChanged;

    private static MonitorSettings S => App.Settings.Monitor;
    private readonly StackPanel _rows = new();
    private readonly List<(MetricInfo info, TextBlock value, LiveChart chart)> _items = [];
    private readonly TextBlock _empty = Ui.Muted("Pin metrics on the Hardware Monitor page to show them here.", 11.5);
    private readonly object _token = new();
    private bool _queued, _dirty = true;

    public static void Open()
    {
        if (Current is not null) { Current.Activate(); return; }
        var w = new CompactMonitorWindow();
        w.Show();
    }

    public static void Toggle() { if (Current is not null) { S.CompactOpen = false; Current.Close(); } else { S.CompactOpen = true; Open(); } App.SaveSettingsSoon(); }

    private CompactMonitorWindow()
    {
        Current = this;
        Title = "Money Tweaks · Monitor";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 270;
        ShowInTaskbar = false;
        Topmost = S.CompactTopmost;
        Background = Ui.Brush("Surface");
        BorderBrush = Ui.Brush("ControlBorder");
        BorderThickness = new Thickness(1);
        FontFamily = Ui.Res<FontFamily>("UiFont");
        Foreground = Ui.Brush("Text");
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = S.CompactLeft is { } l && l > wa.Left - 200 && l < wa.Right - 40 ? l : wa.Right - Width - 24;
        Top = S.CompactTop is { } t && t > wa.Top - 20 && t < wa.Bottom - 40 ? t : wa.Top + 24;

        var bar = new Grid { Background = Ui.Brush("Card"), Height = 30 };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Border { Width = 3, Height = 12, Background = Ui.Brush("AccentGradient"), Margin = new Thickness(0, 0, 8, 0) });
        title.Children.Add(new TextBlock { Text = "MONITOR", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Muted"), FontFamily = Ui.Res<FontFamily>("DisplayFont") });
        bar.Children.Add(title);
        var top = new Button
        {
            Style = Ui.Res<Style>("GhostButton"), Opacity = Topmost ? 1 : 0.45, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 2, 0),
            Content = new TextBlock { Text = "\uE718", Style = Ui.Res<Style>("Icon"), FontSize = 12 }, ToolTip = "Keep on top of other windows",
        };
        top.Click += (_, _) => { Topmost = S.CompactTopmost = !Topmost; top.Opacity = Topmost ? 1 : 0.45; top.ToolTip = Topmost ? "On top of other windows (click to turn off)" : "Keep on top of other windows"; App.SaveSettingsSoon(); };
        Grid.SetColumn(top, 1);
        bar.Children.Add(top);
        var close = new Button { Style = Ui.Res<Style>("GhostButton"), Padding = new Thickness(8, 2, 8, 2), Content = new TextBlock { Text = "\uE711", Style = Ui.Res<Style>("Icon"), FontSize = 11 }, ToolTip = "Close" };
        close.Click += (_, _) => { S.CompactOpen = false; App.SaveSettingsSoon(); Close(); };
        Grid.SetColumn(close, 2);
        bar.Children.Add(close);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };

        var root = new DockPanel { Background = Ui.Brush("Surface") };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        _rows.Margin = new Thickness(10, 8, 10, 8);
        root.Children.Add(_rows);
        Content = root;

        SourceInitialized += (_, _) => Motion.SquareCorners(this);
        LocationChanged += (_, _) => { S.CompactLeft = Left; S.CompactTop = Top; App.SaveSettingsSoon(); };
        SensorHub.Shared.Sampled += OnFrame;
        SensorHub.Shared.MetricsChanged += OnMetricsChanged;
        SensorHub.Shared.Acquire(_token);
        Loaded += (_, _) => { if (SensorHub.Shared.Last is { } f) Apply(f); Motion.FadeIn(root, 0, 8, 260); };
        Closed += (_, _) =>
        {
            SensorHub.Shared.Sampled -= OnFrame;
            SensorHub.Shared.MetricsChanged -= OnMetricsChanged;
            SensorHub.Shared.Release(_token);
            Current = null;
            OpenChanged?.Invoke();
        };
        OpenChanged?.Invoke();
    }

    /// <summary>The pinned list changed on the monitor page.</summary>
    public void PinsChanged() { _dirty = true; if (SensorHub.Shared.Last is { } f) Apply(f); }

    private void OnMetricsChanged() => _dirty = true;

    private void OnFrame(SensorFrame f)
    {
        if (_queued) return;   // never queue more than one UI update
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _queued = false; Apply(f); });
    }

    private void Build()
    {
        _dirty = false;
        _rows.Children.Clear();
        _items.Clear();
        foreach (var id in S.Pinned)
        {
            if (SensorHub.Shared.Find(id) is not { } m) continue;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = m.ShortName, FontSize = 11, Foreground = Ui.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            var value = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), FontFamily = Ui.Res<FontFamily>("DisplayFont") };
            names.Children.Add(value);
            row.Children.Add(names);
            var chart = new LiveChart(compact: true, height: 30);
            chart.SetSeries(new LiveChart.Series(m, Ui.Brush(MetricCard.Palette[_items.Count % 2])));
            chart.SetRange(60, SensorHub.Shared.History, S.RefreshMs);
            Grid.SetColumn(chart, 1);
            row.Children.Add(chart);
            _rows.Children.Add(row);
            _items.Add((m, value, chart));
        }
        if (_items.Count == 0) _rows.Children.Add(_empty);
    }

    private void Apply(SensorFrame f)
    {
        if (_dirty) Build();
        foreach (var (info, value, chart) in _items)
        {
            value.Text = info.Format(f.Values.GetValueOrDefault(info.Id));
            if (f.Recorded) chart.Update(SensorHub.Shared.History, f.Utc, S.RefreshMs);
        }
    }
}
