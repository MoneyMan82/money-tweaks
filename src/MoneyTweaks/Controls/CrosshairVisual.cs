using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoneyTweaks.Core.Crosshair;

namespace MoneyTweaks.Controls;

/// <summary>
/// Draws a crosshair profile with <see cref="CrosshairGeometry"/>, in physical pixels (so a 1 px line is one screen
/// pixel at any Windows scaling). The editor preview and the desktop overlay both use this, so they always match.
/// </summary>
public sealed class CrosshairVisual : FrameworkElement
{
    private static readonly Dictionary<string, BitmapSource?> Images = [];

    public CrosshairProfile? Profile { get; set; }
    public double Bloom { get; set; }
    /// <summary>Overlay scale from the overlay options (sizes × scale).</summary>
    public double CrosshairScale { get; set; } = 1;
    /// <summary>Extra opacity multiplier (overlay opacity, hide-while-aiming).</summary>
    public double GlobalOpacity { get; set; } = 1;
    /// <summary>Preview magnification: each screen pixel becomes Zoom×Zoom device pixels.</summary>
    public double Zoom { get; set; } = 1;
    public bool ShowPixelGrid { get; set; }
    /// <summary>Offset of the crosshair centre from this element's centre, in screen pixels.</summary>
    public double CentreX { get; set; }
    public double CentreY { get; set; }

    public CrosshairVisual()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Redraw() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        if (Profile is null || ActualWidth <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double unit = Zoom / dpi.DpiScaleX; // DIPs per screen pixel
        double pxW = ActualWidth / unit, pxH = ActualHeight / unit;
        double ox = Math.Floor(pxW / 2) + Math.Round(CentreX), oy = Math.Floor(pxH / 2) + Math.Round(CentreY);

        dc.PushTransform(new ScaleTransform(unit, unit));
        if (ShowPixelGrid && Zoom >= 4) DrawGrid(dc, pxW, pxH, ox, oy, unit);
        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushOpacity(Math.Clamp(GlobalOpacity, 0, 1));
        foreach (var layer in CrosshairGeometry.Build(Profile, Bloom, CrosshairScale))
        {
            bool rotated = Math.Abs(layer.Rotation % 360) > 0.001;
            if (rotated) dc.PushTransform(new RotateTransform(layer.Rotation, layer.PivotX, layer.PivotY));
            foreach (var s in layer.Shapes) Draw(dc, s);
            if (rotated) dc.Pop();
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private static Brush BrushOf(Argb c, double opacity)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static void Draw(DrawingContext dc, CrosshairShape s)
    {
        switch (s.Kind)
        {
            case ShapeKind.Rect:
                dc.DrawRoundedRectangle(BrushOf(s.Color, s.Opacity), null, new Rect(s.X, s.Y, s.W, s.H), s.CornerRadius, s.CornerRadius);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(BrushOf(s.Color, s.Opacity), null, new Point(s.X + s.W / 2, s.Y + s.H / 2), s.W / 2, s.H / 2);
                break;
            case ShapeKind.Ring:
                var pen = new Pen(BrushOf(s.Color, s.Opacity), s.StrokeThickness);
                pen.Freeze();
                dc.DrawEllipse(null, pen, new Point(s.X + s.W / 2, s.Y + s.H / 2), s.W / 2, s.H / 2);
                break;
            case ShapeKind.Image when s.ImageFile is not null && LoadImage(s.ImageFile) is { } img:
                dc.PushOpacity(Math.Clamp(s.Opacity, 0, 1));
                dc.DrawImage(img, new Rect(s.X, s.Y, s.W, s.H));
                dc.Pop();
                break;
        }
    }

    private static void DrawGrid(DrawingContext dc, double w, double h, double ox, double oy, double unit)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1 / unit);
        pen.Freeze();
        for (double x = 0; x <= w; x++) dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
        for (double y = 0; y <= h; y++) dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        var centre = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x4C, 0x8D, 0xFF)), 1 / unit);
        centre.Freeze();
        dc.DrawRectangle(null, centre, new Rect(ox, oy, 1, 1));
    }

    /// <summary>Decoded once per file and cached; null if the file is gone or not a valid image.</summary>
    public static BitmapSource? LoadImage(string file)
    {
        if (Images.TryGetValue(file, out var cached)) return cached;
        BitmapSource? img = null;
        try
        {
            if (CrosshairImages.Exists(file))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(CrosshairImages.PathOf(file));
                bi.EndInit();
                bi.Freeze();
                img = bi;
            }
        }
        catch { img = null; }
        return Images[file] = img;
    }
}

/// <summary>
/// The desktop crosshair: a small transparent, click-through, always-on-top, never-activated window centred on the
/// chosen monitor. It's an ordinary window drawn by Windows' compositor: nothing is injected into games, so it shows
/// over windowed and borderless games but not over true exclusive fullscreen.
/// </summary>
public sealed class CrosshairOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_NOOWNERZORDER = 0x200;
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    public CrosshairVisual Visual { get; } = new();
    public nint Handle { get; private set; }
    /// <summary>Where the window is, in physical pixels.</summary>
    public (int x, int y, int size) Placement { get; private set; }

    public CrosshairOverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Focusable = false;
        Title = "Money Tweaks crosshair";
        Width = Height = 64;
        Content = Visual;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(Handle, GWL_EXSTYLE, GetWindowLong(Handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        // Games sometimes take the topmost slot; re-assert it periodically.
        _topmost.Tick += (_, _) => { Topmost = false; Topmost = true; };
        _topmost.Start();
        Closed += (_, _) => _topmost.Stop();
        DpiChanged += (_, _) => { var p = Placement; Placement = default; if (p.size > 0) PlaceAt(p.x + p.size / 2, p.y + p.size / 2, p.size); };
    }

    /// <summary>Centres a square window of <paramref name="size"/> physical pixels on the given pixel.</summary>
    public void PlaceAt(int centreX, int centreY, int size)
    {
        size = Math.Max(16, size + size % 2);
        var p = (centreX - size / 2, centreY - size / 2, size);
        if (p == Placement || Handle == 0) { if (Handle == 0) Placement = p; return; }
        Placement = p;
        SetWindowPos(Handle, 0, p.Item1, p.Item2, size, size, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
        Visual.Redraw();
    }

    public void ApplyPendingPlacement()
    {
        var p = Placement;
        Placement = default;
        if (p.size > 0) PlaceAt(p.x + p.size / 2, p.y + p.size / 2, p.size);
    }
}
