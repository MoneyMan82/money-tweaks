using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>Draws a crosshair from settings onto a canvas, centered.</summary>
public static class CrosshairRenderer
{
    public static void Draw(Canvas c, CrosshairSettings s, double cx, double cy)
    {
        c.Children.Clear();
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(s.Color); } catch { color = Colors.Lime; }
        var brush = new SolidColorBrush(color) { Opacity = s.Opacity };
        var outline = new SolidColorBrush(Colors.Black) { Opacity = s.Opacity * 0.85 };
        cx += s.OffsetX; cy += s.OffsetY;
        double t = s.Thickness, g = s.Gap, len = s.Size;

        void Rect(double x, double y, double w, double h)
        {
            if (s.Outline) Add(new Rectangle { Width = w + 2, Height = h + 2, Fill = outline }, x - 1, y - 1);
            Add(new Rectangle { Width = w, Height = h, Fill = brush }, x, y);
        }
        void Add(FrameworkElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); c.Children.Add(e); }
        void Dot(double d) => Rect(cx - d / 2, cy - d / 2, d, d);

        switch (s.Style)
        {
            case "Dot":
                Dot(Math.Max(2, t * 2));
                break;
            case "Circle":
                var r = len + g;
                if (s.Outline) Add(new Ellipse { Width = r * 2 + 2, Height = r * 2 + 2, Stroke = outline, StrokeThickness = t + 2 }, cx - r - 1, cy - r - 1);
                Add(new Ellipse { Width = r * 2, Height = r * 2, Stroke = brush, StrokeThickness = t }, cx - r, cy - r);
                Dot(Math.Max(2, t));
                break;
            default: // Cross, T, CrossDot
                Rect(cx - g - len, cy - t / 2, len, t);          // left
                Rect(cx + g, cy - t / 2, len, t);                // right
                Rect(cx - t / 2, cy + g, t, len);                // bottom
                if (s.Style != "T") Rect(cx - t / 2, cy - g - len, t, len); // top
                if (s.Style == "CrossDot") Dot(Math.Max(2, t));
                break;
        }
    }
}

/// <summary>Transparent, click-through, always-on-top window. Works over borderless/windowed games, not exclusive fullscreen.</summary>
public sealed class CrosshairOverlay : Window
{
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint h, int i, int v);
    private readonly Canvas _canvas = new();
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };

    public CrosshairOverlay()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Content = _canvas;
        Title = "Money Tweaks crosshair";
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        // Games sometimes take the topmost slot; re-assert it periodically.
        _topmost.Tick += (_, _) => { Topmost = false; Topmost = true; };
        _topmost.Start();
        Closed += (_, _) => _topmost.Stop();
    }

    public void Render(CrosshairSettings s)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var scr = screens[Math.Clamp(s.MonitorIndex, 0, screens.Length - 1)].Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = scr.Left / dpi.DpiScaleX; Top = scr.Top / dpi.DpiScaleY;
        Width = scr.Width / dpi.DpiScaleX; Height = scr.Height / dpi.DpiScaleY;
        // Snap to whole device pixels so thin lines stay crisp.
        CrosshairRenderer.Draw(_canvas, s, Math.Round(Width / 2), Math.Round(Height / 2));
    }
}

public sealed class CrosshairView : UserControl, IPageLifecycle
{
    private static CrosshairOverlay? _overlay;
    private readonly Canvas _preview = new() { Width = 260, Height = 200, Background = new SolidColorBrush(Color.FromRgb(0x30, 0x3C, 0x36)), ClipToBounds = true };
    private readonly CrosshairSettings _s = App.Settings.Crosshair;
    private readonly Button _show;
    private const int HotkeyId = 0x4D54;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);
    private static bool _hotkeyRegistered;

    public CrosshairView()
    {
        _show = Ui.Btn("Show crosshair", (_, _) => ToggleOverlay(), "PrimaryButton");
        var (root, body) = Ui.Page("Custom Crosshair", "A click-through overlay that sits on top of your game. It works in borderless and windowed modes; exclusive fullscreen draws over every overlay, so switch the game to borderless. Toggle it anywhere with Ctrl + Alt + X.",
            Ui.Row(_show));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var editor = new StackPanel();
        editor.Children.Add(Ui.Section("Style"));
        var styles = new WrapPanel();
        foreach (var st in new[] { "Cross", "CrossDot", "T", "Dot", "Circle" })
        {
            var rb = new RadioButton { Content = st == "CrossDot" ? "Cross + dot" : st, Style = Ui.Res<Style>("Chip"), GroupName = "xs", IsChecked = _s.Style == st, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _s.Style = st; Changed(); };
            styles.Children.Add(rb);
        }
        editor.Children.Add(styles);
        editor.Children.Add(Ui.Section("Colour"));
        var colors = new WrapPanel();
        foreach (var hex in new[] { "#FF2BE38B", "#FF00FFFF", "#FFFF2D55", "#FFFFFF00", "#FFFFFFFF", "#FFFF00FF", "#FFFF8C00" })
        {
            var b = new Button { Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Background = (Brush)new BrushConverter().ConvertFrom(hex)!, Style = Ui.Res<Style>("BaseButton"), Padding = new Thickness(0), ToolTip = hex };
            b.Click += (_, _) => { _s.Color = hex; Changed(); };
            colors.Children.Add(b);
        }
        editor.Children.Add(colors);
        editor.Children.Add(Slider("Size", 1, 40, _s.Size, v => _s.Size = v));
        editor.Children.Add(Slider("Thickness", 1, 10, _s.Thickness, v => _s.Thickness = v));
        editor.Children.Add(Slider("Gap", 0, 20, _s.Gap, v => _s.Gap = v));
        editor.Children.Add(Slider("Opacity", 0.1, 1, _s.Opacity, v => _s.Opacity = v));
        editor.Children.Add(Slider("Offset X", -50, 50, _s.OffsetX, v => _s.OffsetX = v));
        editor.Children.Add(Slider("Offset Y", -50, 50, _s.OffsetY, v => _s.OffsetY = v));
        var outline = new CheckBox { Content = "Black outline", IsChecked = _s.Outline, Margin = new Thickness(0, 8, 0, 0) };
        outline.Checked += (_, _) => { _s.Outline = true; Changed(); };
        outline.Unchecked += (_, _) => { _s.Outline = false; Changed(); };
        editor.Children.Add(outline);
        var monitors = System.Windows.Forms.Screen.AllScreens.Length;
        if (monitors > 1)
        {
            var mp = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < monitors; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = $"Monitor {i + 1}", Style = Ui.Res<Style>("Chip"), GroupName = "xm", IsChecked = _s.MonitorIndex == i };
                rb.Checked += (_, _) => { _s.MonitorIndex = idx; Changed(); };
                mp.Children.Add(rb);
            }
            editor.Children.Add(mp);
        }
        grid.Children.Add(Ui.Card(editor, new Thickness(0, 0, 12, 12)));
        var pv = new StackPanel();
        pv.Children.Add(Ui.Section("Preview"));
        pv.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _preview });
        var pc = Ui.Card(pv);
        pc.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(pc, 1);
        grid.Children.Add(pc);
        body.Children.Add(grid);
        Content = root;
        Changed();
        Loaded += (_, _) => RegisterHotkey();
    }

    private UIElement Slider(string label, double min, double max, double value, Action<double> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var name = Ui.Muted(label, 12.5); name.Width = 80;
        var val = Ui.Text(value.ToString("0.##"), 12.5); val.Width = 44;
        var sl = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = max <= 1 ? 0.05 : 1, IsSnapToTickEnabled = true, TickFrequency = max <= 1 ? 0.05 : 1, VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (_, e) => { set(e.NewValue); val.Text = e.NewValue.ToString("0.##"); Changed(); };
        DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(name); row.Children.Add(val); row.Children.Add(sl);
        return row;
    }

    private void Changed()
    {
        CrosshairRenderer.Draw(_preview, _s, 130, 100);
        _overlay?.Render(_s);
    }

    private void ToggleOverlay()
    {
        if (_overlay is null)
        {
            _overlay = new CrosshairOverlay();
            _overlay.Show();
            _overlay.Render(_s);
            Log.Info("Crosshair overlay shown");
        }
        else
        {
            _overlay.Close();
            _overlay = null;
        }
        _show.Content = _overlay is null ? "Show crosshair" : "Hide crosshair";
        App.Settings.Save();
    }

    private void RegisterHotkey()
    {
        if (_hotkeyRegistered || Application.Current.MainWindow is not { } w) return;
        var h = new WindowInteropHelper(w).Handle;
        if (RegisterHotKey(h, HotkeyId, 0x0002 | 0x0001 | 0x4000, 0x58)) // Ctrl+Alt (no repeat) + X
        {
            _hotkeyRegistered = true;
            HwndSource.FromHwnd(h)?.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
            {
                if (msg == 0x0312 && wp == HotkeyId) { ToggleOverlay(); handled = true; }
                return 0;
            });
            w.Closed += (_, _) => { UnregisterHotKey(h, HotkeyId); _overlay?.Close(); };
        }
        else Log.Warn("Ctrl+Alt+X is already used by another app; the crosshair hotkey is unavailable.");
    }

    public void OnShown() { }
    public void OnHidden() => App.Settings.Save();
}
