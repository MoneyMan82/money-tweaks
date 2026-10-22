using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MoneyTweaks.Controls;

/// <summary>
/// Thin cyan L-shaped brackets at the four corners of its area: the signature frame of feature panels (see Ui.Hud).
/// They draw out from the corners each time the panel appears (one short animation), then stay still.
/// </summary>
public sealed class HudCorners : FrameworkElement
{
    private static readonly Pen Bright = Make(0xC0), Dim = Make(0x55);
    private static Pen Make(byte a) { var p = new Pen(new SolidColorBrush(Color.FromArgb(a, 0x2F, 0xD8, 0xEE)), 1.5) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }; p.Freeze(); return p; }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(HudCorners),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public double Length { get; set; } = 16;
    public double Inset { get; set; } = 7;

    public HudCorners()
    {
        Loaded += (_, _) =>
        {
            if (!Motion.Enabled) return;
            BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = TimeSpan.FromMilliseconds(180), EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, l = Length * Math.Clamp(Progress, 0, 1), i = Inset;
        if (w < Length * 3 || h < Length * 3 || l < 0.5) return;
        // Top-left is lit (brighter), matching the light direction of the cards.
        void L(Pen p, double x, double y, double dx, double dy) { dc.DrawLine(p, new Point(x, y), new Point(x + dx, y)); dc.DrawLine(p, new Point(x, y), new Point(x, y + dy)); }
        L(Bright, i, i, l, l);
        L(Dim, w - i, i, -l, l);
        L(Dim, i, h - i, l, -l);
        L(Dim, w - i, h - i, -l, -l);
    }
}
