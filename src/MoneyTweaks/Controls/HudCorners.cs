using System.Windows;
using System.Windows.Media;

namespace MoneyTweaks.Controls;

/// <summary>
/// Thin cyan L-shaped brackets at the four corners of its area: the signature frame of feature panels (see Ui.Hud).
/// Drawn once in OnRender with frozen pens; no animation.
/// </summary>
public sealed class HudCorners : FrameworkElement
{
    private static readonly Pen Bright = Make(0xC0), Dim = Make(0x55);
    private static Pen Make(byte a) { var p = new Pen(new SolidColorBrush(Color.FromArgb(a, 0x2F, 0xD8, 0xEE)), 1.5) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }; p.Freeze(); return p; }

    public double Length { get; set; } = 16;
    public double Inset { get; set; } = 7;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, l = Length, i = Inset;
        if (w < l * 3 || h < l * 3) return;
        // Top-left is lit (brighter), matching the light direction of the cards.
        void L(Pen p, double x, double y, double dx, double dy) { dc.DrawLine(p, new Point(x, y), new Point(x + dx, y)); dc.DrawLine(p, new Point(x, y), new Point(x, y + dy)); }
        L(Bright, i, i, l, l);
        L(Dim, w - i, i, -l, l);
        L(Dim, i, h - i, l, -l);
        L(Dim, w - i, h - i, -l, -l);
    }
}
