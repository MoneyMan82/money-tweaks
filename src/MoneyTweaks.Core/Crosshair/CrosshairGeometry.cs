using System.Globalization;

namespace MoneyTweaks.Core.Crosshair;

/// <summary>ARGB colour parsed from "#AARRGGBB" or "#RRGGBB".</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static bool TryParse(string? s, out Argb c)
    {
        c = default;
        if (s is null) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        c = new Argb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Argb Parse(string? s, Argb fallback) => TryParse(s, out var c) ? c : fallback;

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public static Argb Lerp(Argb a, Argb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return new Argb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static readonly Argb Green = new(0xFF, 0x2B, 0xE3, 0x8B);
    public static readonly Argb Black = new(0xFF, 0, 0, 0);
}

public enum ShapeKind { Rect, Ellipse, Ring, Image }

/// <summary>
/// A shape in overlay pixels. Coordinates are relative to the top-left corner of the centre pixel, so the exact
/// crosshair centre is (0.5, 0.5). Rings are stroked (X/Y/W/H is the stroke's centre line), everything else is filled.
/// </summary>
public sealed record CrosshairShape(ShapeKind Kind, double X, double Y, double W, double H, Argb Color, double Opacity,
    double StrokeThickness = 0, double CornerRadius = 0, string? ImageFile = null);

/// <summary>A layer's shapes plus its rotation (degrees) around <see cref="PivotX"/>/<see cref="PivotY"/>.</summary>
public sealed record LayerDrawing(string LayerId, double Rotation, double PivotX, double PivotY, IReadOnlyList<CrosshairShape> Shapes);

/// <summary>
/// Turns a profile into shapes. Pure and deterministic, so the editor preview and the desktop overlay draw exactly the
/// same thing and the geometry is unit-testable.
/// </summary>
public static class CrosshairGeometry
{
    public const double Centre = 0.5;

    /// <param name="bloom">0 = at rest, 1 = full firing spread.</param>
    /// <param name="scale">Overlay scale (1 = sizes are pixels).</param>
    public static List<LayerDrawing> Build(CrosshairProfile p, double bloom = 0, double scale = 1)
    {
        bloom = Math.Clamp(bloom, 0, 1);
        scale = Math.Clamp(scale, 0.1, 10);
        var fire = p.Firing;
        double spread = fire.Enabled ? fire.Spread * bloom : 0;
        double fade = fire.Enabled ? 1 + (Math.Clamp(fire.FiringOpacity, 0, 1) - 1) * bloom : 1;
        var flash = Argb.Parse(fire.FlashColor, new Argb(0xFF, 0xFF, 0x3B, 0x3B));
        var result = new List<LayerDrawing>();

        foreach (var l in p.Layers)
        {
            if (!l.Visible) continue;
            var color = Argb.Parse(l.Color, Argb.Green);
            if (fire.Enabled && fire.Flash && l.ReactsToFiring) color = Argb.Lerp(color, flash, bloom);
            var outline = Argb.Parse(l.OutlineColor, Argb.Black);
            double opacity = Math.Clamp(l.Opacity, 0, 1) * fade;
            double grow = l.ReactsToFiring ? spread : 0;
            double s(double v) => v * scale;
            double cx = Centre + s(l.OffsetX), cy = Centre + s(l.OffsetY);
            double o = l.Outline ? Math.Max(0, s(l.OutlineThickness)) : 0;
            bool snap = p.PixelPerfect && Math.Abs(l.Rotation % 360) < 0.001;
            var shapes = new List<CrosshairShape>();

            void Rect(double x, double y, double w, double h, double radius)
            {
                if (w <= 0 || h <= 0) return;
                if (snap) (x, y, w, h) = Snap(x, y, w, h);
                if (o > 0) shapes.Add(new(ShapeKind.Rect, x - o, y - o, w + 2 * o, h + 2 * o, outline, opacity, 0, radius > 0 ? radius + o : 0));
                shapes.Add(new(ShapeKind.Rect, x, y, w, h, color, opacity, 0, radius));
            }

            switch (l.Kind)
            {
                case LayerKind.Lines:
                {
                    double t = Math.Max(0.5, s(l.Thickness)), len = Math.Max(0, s(l.Length)), g = s(l.Gap) + grow * scale;
                    double r = l.RoundEnds ? t / 2 : 0;
                    if (l.Left) Rect(cx - g - len, cy - t / 2, len, t, r);
                    if (l.Right) Rect(cx + g, cy - t / 2, len, t, r);
                    if (l.Top) Rect(cx - t / 2, cy - g - len, t, len, r);
                    if (l.Bottom) Rect(cx - t / 2, cy + g, t, len, r);
                    break;
                }
                case LayerKind.Dot:
                {
                    double d = Math.Max(0.5, s(l.Size) + 2 * grow * scale);
                    if (l.DotShape == DotShape.Round)
                    {
                        if (o > 0) shapes.Add(new(ShapeKind.Ellipse, cx - d / 2 - o, cy - d / 2 - o, d + 2 * o, d + 2 * o, outline, opacity));
                        shapes.Add(new(ShapeKind.Ellipse, cx - d / 2, cy - d / 2, d, d, color, opacity));
                    }
                    else Rect(cx - d / 2, cy - d / 2, d, d, 0);
                    break;
                }
                case LayerKind.Circle:
                {
                    double r = Math.Max(0.5, s(l.Size) + grow * scale), t = Math.Max(0.5, s(l.Thickness));
                    if (o > 0) shapes.Add(new(ShapeKind.Ring, cx - r, cy - r, 2 * r, 2 * r, outline, opacity, t + 2 * o));
                    shapes.Add(new(ShapeKind.Ring, cx - r, cy - r, 2 * r, 2 * r, color, opacity, t));
                    break;
                }
                case LayerKind.Image when l.ImageFile is not null:
                {
                    double w = Math.Max(1, s(l.Size) + 2 * grow * scale), h = w * Math.Clamp(l.ImageAspect, 0.01, 100);
                    double x = cx - w / 2, y = cy - h / 2;
                    if (snap) (x, y, w, h) = Snap(x, y, w, h);
                    shapes.Add(new(ShapeKind.Image, x, y, w, h, color, opacity, 0, 0, l.ImageFile));
                    break;
                }
            }
            if (shapes.Count > 0) result.Add(new LayerDrawing(l.Id, l.Rotation, cx, cy, shapes));
        }
        return result;
    }

    /// <summary>
    /// Moves edges onto whole pixels (keeping at least 1 px), so lines aren't smeared across two pixels. Shapes on one
    /// side of the centre are rounded by their distance from the centre, so left/right and up/down stay mirror images.
    /// </summary>
    public static (double x, double y, double w, double h) Snap(double x, double y, double w, double h)
    {
        var (x0, w0) = SnapAxis(x, w);
        var (y0, h0) = SnapAxis(y, h);
        return (x0, y0, w0, h0);
    }

    private static (double start, double size) SnapAxis(double a, double size)
    {
        double b = a + size;
        if (a < Centre && b > Centre)
        {
            // Straddles the centre pixel: odd sizes are centred exactly, even sizes can't be and lean right/down.
            double n = Math.Max(1, Math.Round(size, MidpointRounding.AwayFromZero));
            return (Math.Floor(Centre - n / 2 + 0.5), n);
        }
        double side = a >= Centre ? 1 : -1;
        double E(double e) { double d = e - Centre; return Centre + (Math.Abs(d) < 1e-9 ? side : Math.Sign(d)) * (Math.Floor(Math.Abs(d) + 1e-9) + 0.5); }
        double s0 = E(a), s1 = E(b);
        if (s1 - s0 < 1) { if (b <= Centre) s0 = s1 - 1; else s1 = s0 + 1; }
        return (s0, s1 - s0);
    }

    /// <summary>Largest distance from the centre that anything can reach at full bloom (for sizing the overlay window).</summary>
    public static double Extent(CrosshairProfile p, double scale = 1)
    {
        double max = 4;
        foreach (var d in Build(p, 1, scale))
        {
            double pivot = Math.Sqrt(Sq(d.PivotX - Centre) + Sq(d.PivotY - Centre));
            foreach (var sh in d.Shapes)
            {
                double half = sh.StrokeThickness / 2;
                double far = 0;
                foreach (var (x, y) in new[] { (sh.X - half, sh.Y - half), (sh.X + sh.W + half, sh.Y - half), (sh.X - half, sh.Y + sh.H + half), (sh.X + sh.W + half, sh.Y + sh.H + half) })
                    far = Math.Max(far, Math.Sqrt(Sq(x - d.PivotX) + Sq(y - d.PivotY)));
                max = Math.Max(max, pivot + far);
            }
        }
        return max;
    }

    private static double Sq(double v) => v * v;
}

/// <summary>
/// Firing bloom over time: rises towards 1 while the trigger is held and falls back to 0 after release, at the
/// profile's speeds. Time-driven so it behaves the same at any polling rate.
/// </summary>
public sealed class BloomState
{
    public double Value { get; private set; }

    /// <returns>True when the value changed (the overlay needs a redraw).</returns>
    public bool Update(bool firing, double elapsedMs, FiringAnimation a)
    {
        var before = Value;
        if (!a.Enabled) Value = 0;
        else if (firing) Value = Math.Min(1, Value + elapsedMs / Math.Max(1, a.RiseMs));
        else Value = Math.Max(0, Value - elapsedMs / Math.Max(1, a.RecoverMs));
        return Math.Abs(Value - before) > 1e-6;
    }

    public void Reset() => Value = 0;

    public static bool IsFiring(FireTrigger t, bool left, bool right) => t switch
    {
        FireTrigger.LeftMouse => left,
        FireTrigger.RightMouse => right,
        _ => left || right,
    };
}
