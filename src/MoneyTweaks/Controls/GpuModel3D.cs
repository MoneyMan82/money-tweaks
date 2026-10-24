using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MoneyTweaks.Core.Monitoring;
using static MoneyTweaks.Controls.VirtualPcScene;

namespace MoneyTweaks.Controls;

/// <summary>The animated parts of a built card: fan rotors and the live-tinted heat/accent brushes.</summary>
public sealed class GpuRig
{
    public List<AxisAngleRotation3D> Fans { get; } = [];
    /// <summary>Light bars and glow: follows GPU heat.</summary>
    public SolidColorBrush Heat { get; } = new(Color.FromRgb(0x2A, 0x7B, 0xFF));
    /// <summary>Brand lighting (logo, accent strips): fixed per design, dimmed when the GPU is idle.</summary>
    public SolidColorBrush Accent { get; } = new(Colors.White);
    public int Triangles { get; set; }
    /// <summary>Card size in world units (1 = 100 mm), for framing the camera.</summary>
    public Size3D Size { get; set; }
}

/// <summary>
/// Builds a graphics card in 3D from a <see cref="GpuMatch"/>: the reference / Founders Edition shape at its published
/// dimensions, or a typical partner card of that chip, in one of four design families (NVIDIA Founders flow-through,
/// AMD reference, Intel Arc Limited Edition, board-partner). Everything is procedural and drawn by Money Tweaks: no
/// third-party models or logos are used, only the product name as text. A card that isn't in the library gets a neutral
/// glass placeholder; integrated and laptop GPUs get a chip instead of a card. 1 world unit = 100 mm.
/// </summary>
public static class GpuModel3D
{
    private static readonly Dictionary<string, Brush> TextTextures = [];

    /// <summary>A text label texture in <paramref name="color"/> on transparent, cached (drawn once per run).</summary>
    private static Brush Label(string text, double aspect, Color color, FontWeight? weight = null)
    {
        var key = $"{text}|{aspect.ToString(CultureInfo.InvariantCulture)}|{color}";
        if (TextTextures.TryGetValue(key, out var b)) return b;
        int h = 64, w = (int)Math.Round(h * aspect);
        var fill = new SolidColorBrush(color); fill.Freeze();
        b = new ImageBrush(Bitmap(w, h, dc =>
        {
            var face = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.SemiBold, FontStretches.Normal);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, h * 0.62, fill, 1.0);
            if (ft.Width > w * 0.94) ft.SetFontSize(h * 0.62 * w * 0.94 / ft.Width);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
        }));
        b.Freeze();
        return TextTextures[key] = b;
    }

    private static Material Emissive(Brush b)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(Brushes.Black));
        g.Children.Add(new EmissiveMaterial(b));
        return g;
    }

    /// <summary>Glowing text decal: transparent except the glyphs, which emit their own colour.</summary>
    private static Material Decal(Brush text)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(Colors.Transparent)));
        g.Children.Add(new EmissiveMaterial(text));
        g.Freeze();
        return g;
    }

    private static void Add(Model3DGroup g, GpuRig rig, MeshGeometry3D m, Material mat, Transform3D? t = null, Material? back = null)
    {
        m.Freeze();
        g.Children.Add(new GeometryModel3D(m, mat) { BackMaterial = back, Transform = t });
        rig.Triangles += m.TriangleIndices.Count / 3;
    }

    /// <summary>Box by its centre (unlike the scene's bottom-based Box).</summary>
    private static MeshGeometry3D BoxC(double x, double y, double z, double sx, double sy, double sz) => Box(x, y - sy / 2, z, sx, sy, sz);

    /// <summary>A quad facing +Z (front decals, text plates), centred.</summary>
    private static MeshGeometry3D FrontQuad(double x, double y, double z, double sx, double sy)
    {
        var m = new MeshGeometry3D();
        foreach (var (dx, dy, u, v) in new[] { (-1, -1, 0, 1), (1, -1, 1, 1), (1, 1, 1, 0), (-1, 1, 0, 0) })
        {
            m.Positions.Add(new Point3D(x + dx * sx / 2, y + dy * sy / 2, z)); m.Normals.Add(new Vector3D(0, 0, 1)); m.TextureCoordinates.Add(new Point(u, v));
        }
        m.TriangleIndices = new System.Windows.Media.Int32Collection { 0, 1, 2, 0, 2, 3 };
        return m;
    }

    /// <summary>A quad lying on the top edge (facing +Y), its text reading left to right for a viewer in front of the card.</summary>
    private static MeshGeometry3D TopQuad(double x, double y, double z, double sx, double sz)
    {
        var m = new MeshGeometry3D();
        foreach (var (dx, dz, u, v) in new[] { (-1, -1, 0, 0), (1, -1, 1, 0), (1, 1, 1, 1), (-1, 1, 0, 1) })
        {
            m.Positions.Add(new Point3D(x + dx * sx / 2, y, z + dz * sz / 2)); m.Normals.Add(new Vector3D(0, 1, 0)); m.TextureCoordinates.Add(new Point(u, v));
        }
        m.TriangleIndices = new System.Windows.Media.Int32Collection { 0, 2, 1, 0, 3, 2 };
        return m;
    }

    /// <summary>A fan facing +Z (or -Z on the back): frame ring, dark opening, rotor and hub.</summary>
    private static void Fan(Model3DGroup g, GpuRig rig, double x, double y, double z, double r, bool back, Material frame, Material blades, Material hub, bool detailed, int bladeCount = 11)
    {
        double dir = back ? -1 : 1, rot = back ? -90 : 90;
        var spin = new AxisAngleRotation3D(new Vector3D(0, 1, 0), rig.Fans.Count * 37);
        rig.Fans.Add(spin);
        var dark = Mat(Color.FromRgb(0x04, 0x05, 0x07));
        Add(g, rig, Cyl(new Point3D(0, 0, 0), r * 1.04, 0.05, detailed ? 40 : 16, caps: false), frame, Place(x, y, z - dir * 0.05, rotX: rot), frame);
        Add(g, rig, Cyl(new Point3D(0, 0, 0), r, 0.005, detailed ? 32 : 14), dark, Place(x, y, z - dir * 0.045, rotX: rot));
        Add(g, rig, Rotor(r * 0.96, bladeCount), blades, Place(x, y, z - dir * 0.02, rotX: rot, spin: spin), blades);
        Add(g, rig, Cyl(new Point3D(0, 0, 0), r * 0.3, 0.035, detailed ? 24 : 12), hub, Place(x, y, z - dir * 0.03, rotX: rot, spin: spin));
    }

    public static Model3DGroup Build(GpuMatch m, GpuRig rig, bool detailed = true)
    {
        var g = new Model3DGroup();
        if (m.IsCard) Card(g, m, rig, detailed);
        else if (m.Design is GpuDesign.Integrated or GpuDesign.Laptop) Chip(g, m, rig, detailed);
        else Placeholder(g, m, rig);
        return g;
    }

    private static void Card(Model3DGroup g, GpuMatch m, GpuRig rig, bool detailed)
    {
        var spec = m.Spec!;
        var (lenMm, hMm, slots, fans) = m.Shape;
        double L = lenMm / 100.0, H = hMm / 100.0, T = slots * 0.2;
        double x0 = -L / 2, y0 = -H / 2, zb = -T / 2, zf = T / 2;
        rig.Size = new Size3D(L, H, T);

        var brushed = Mat(Brushed.Value, 0.7, 60);
        var silver = Mat(Color.FromRgb(0xC9, 0xCD, 0xD3), 0.9, 80);
        var black = Mat(Color.FromRgb(0x10, 0x11, 0x14), 0.35, 25);
        var gold = Mat(Color.FromRgb(0xC9, 0xA2, 0x4A), 0.8, 50);
        var heatGlow = Emissive(rig.Heat);
        Material shell, trim, fanFrame, blades;
        string brandText;
        Color accent;
        switch (m.Design)
        {
            case GpuDesign.NvidiaFounders:
                shell = Mat(Color.FromRgb(0x2B, 0x2E, 0x33), 0.6, 45); trim = Mat(Brushed.Value, 0.9, 80);
                fanFrame = Mat(Color.FromRgb(0x3A, 0x3D, 0x43), 0.6, 50); blades = Mat(Color.FromRgb(0x1A, 0x1C, 0x20), 0.5, 40);
                brandText = "GEFORCE RTX"; accent = Color.FromRgb(0xF2, 0xF4, 0xF8); break;
            case GpuDesign.AmdReference:
                shell = Mat(Color.FromRgb(0x17, 0x18, 0x1B), 0.45, 30); trim = Mat(Color.FromRgb(0x3C, 0x3F, 0x45), 0.7, 60);
                fanFrame = Mat(Color.FromRgb(0x1C, 0x1D, 0x21), 0.4, 30); blades = Mat(Color.FromRgb(0x14, 0x15, 0x18), 0.45, 35);
                brandText = "RADEON"; accent = Color.FromRgb(0xE3, 0x1C, 0x2B); break;
            case GpuDesign.IntelLimited:
                shell = Mat(Color.FromRgb(0x12, 0x13, 0x16), 0.35, 25); trim = Mat(Color.FromRgb(0x2A, 0x2C, 0x31), 0.6, 50);
                fanFrame = Mat(Color.FromRgb(0x16, 0x17, 0x1A), 0.35, 25); blades = Mat(Color.FromRgb(0x18, 0x19, 0x1C), 0.4, 30);
                brandText = "INTEL ARC"; accent = Color.FromRgb(0x6C, 0xC8, 0xFF); break;
            default:
                shell = Mat(Gunmetal.Value, 0.5, 35); trim = Mat(Color.FromRgb(0x40, 0x44, 0x4C), 0.7, 55);
                fanFrame = Mat(Color.FromRgb(0x24, 0x27, 0x2D), 0.45, 35); blades = Mat(Color.FromRgb(0x1C, 0x1E, 0x23), 0.45, 35);
                brandText = spec.Vendor switch { "NVIDIA" => "GEFORCE RTX", "AMD" => "RADEON", _ => "INTEL ARC" }; accent = Color.FromRgb(0xE6, 0xEA, 0xF0); break;
        }
        rig.Accent.Color = accent;
        var accentGlow = Emissive(rig.Accent);

        // PCB, backplate with vents, PCIe x16 fingers, bracket with outputs, power connector.
        Add(g, rig, BoxC(0, 0, zb + 0.035, L - 0.02, H - 0.04, 0.016), Mat(Color.FromRgb(0x0E, 0x14, 0x12), 0.3));
        Add(g, rig, BoxC(0, 0.01, zb + 0.012, L, H - 0.02, 0.024), brushed);
        for (int i = 0; i < (detailed ? 14 : 6); i++) Add(g, rig, BoxC(x0 + L * 0.55 + i * L * 0.028, 0.05, zb - 0.001, L * 0.012, H * 0.55, 0.004), black);
        Add(g, rig, BoxC(x0 + L * 0.18 + 0.445, y0 - 0.06, zb + 0.035, 0.89, 0.12, 0.016), gold);
        double bw = Math.Ceiling(slots) * 0.2;
        Add(g, rig, BoxC(x0 - 0.02, 0.06, zf - bw / 2, 0.02, H + 0.12, bw), Mat(Brushed.Value, 0.95, 90));
        for (int i = 0; i < 4; i++) Add(g, rig, BoxC(x0 - 0.035, -H / 2 + 0.22 + i * (H - 0.35) / 4, zf - bw / 2 + 0.03, 0.01, 0.12, 0.08), black);
        bool newPlug = spec.Vendor == "NVIDIA" && spec.Id.StartsWith("rtx", StringComparison.Ordinal) && spec.Id[3] is '4' or '5';
        if (newPlug) Add(g, rig, BoxC(x0 + L * 0.66, y0 + H + 0.03, 0, 0.21, 0.07, 0.11), black);                 // 12V-2x6 connector
        else for (int i = 0; i < (spec.BoardPowerW > 225 ? 2 : 1); i++) Add(g, rig, BoxC(x0 + L * 0.66 + i * 0.22, y0 + H + 0.035, 0, 0.2, 0.08, 0.1), black); // 8-pin

        // Shroud body and top edge.
        double bodyZ = (zb + 0.045 + zf) / 2, bodyT = zf - (zb + 0.045);
        Add(g, rig, BoxC(0, 0.01, bodyZ, L, H - 0.02, bodyT), shell);
        Add(g, rig, BoxC(0, y0 + H - 0.01, bodyZ, L + 0.004, 0.02, bodyT + 0.004), trim);                        // top rail
        Add(g, rig, BoxC(0, y0 + 0.02, zf - 0.006, L * 0.96, 0.012, 0.012), heatGlow);                          // light bar, lower front edge

        var topZ = bodyZ;
        Add(g, rig, TopQuad(x0 + L * 0.28, y0 + H + 0.002, topZ, L * 0.36, Math.Min(bodyT * 0.6, 0.18)), Decal(Label(brandText, 6, accent)));
        switch (m.Design)
        {
            case GpuDesign.NvidiaFounders:
            {
                // Founders flow-through: metal frame, one fan on the front near the bracket, fin stack to the end with the
                // second fan on the back pulling air through it.
                double r = H * 0.38;
                foreach (var (y, h) in new[] { (y0 + 0.06, 0.08), (y0 + H - 0.08, 0.08) }) Add(g, rig, BoxC(0, y, zf + 0.004, L, h, 0.012), trim);
                Add(g, rig, BoxC(x0 + 0.06, 0.01, zf + 0.004, 0.08, H - 0.1, 0.012), trim);
                Add(g, rig, BoxC(x0 + L - 0.06, 0.01, zf + 0.004, 0.08, H - 0.1, 0.012), trim);
                Add(g, rig, BoxC(x0 + L * 0.5, 0.01, zf + 0.004, 0.05, H - 0.1, 0.012), trim);
                Fan(g, rig, x0 + L * 0.27, 0.01, zf + 0.012, r, back: false, fanFrame, blades, Mat(Color.FromRgb(0x2E, 0x31, 0x37), 0.7, 60), detailed, 7);
                int finN = detailed ? 34 : 12;
                for (int i = 0; i < finN; i++) Add(g, rig, BoxC(x0 + L * 0.53 + i * (L * 0.44 / finN), 0.01, bodyZ, 0.008, H - 0.2, bodyT + 0.01), silver);
                Fan(g, rig, x0 + L * 0.74, 0.01, zb - 0.002, r, back: true, fanFrame, blades, Mat(Color.FromRgb(0x2E, 0x31, 0x37), 0.7, 60), detailed, 7);
                Add(g, rig, FrontQuad(x0 + L * 0.74, y0 + 0.17, zf + 0.012, L * 0.3, 0.07), Decal(Label("GEFORCE RTX", 8, accent)));
                break;
            }
            default:
            {
                int n = Math.Max(1, fans);
                double r = Math.Min(H * 0.4, L / n * 0.44);
                for (int i = 0; i < n; i++)
                {
                    double fx = x0 + L * (i + 0.5) / n;
                    Fan(g, rig, fx, 0.02, zf + 0.012, r, back: false, fanFrame, blades, Mat(Color.FromRgb(0x2A, 0x2D, 0x33), 0.6, 50), detailed);
                    if (m.Design == GpuDesign.IntelLimited) Add(g, rig, BoxC(fx, 0.02, zf + 0.004, r * 2.2, 0.012, 0.01), accentGlow); // light strip through the fans
                }
                // Angular front plate between the fans.
                for (int i = 1; i < n; i++) Add(g, rig, BoxC(x0 + L * i / n, 0.02, zf + 0.004, 0.035, H * 0.82, 0.012), trim);
                if (m.Design == GpuDesign.AmdReference)
                    foreach (var y in new[] { y0 + H - 0.05, y0 + H - 0.08, y0 + H - 0.11 }) Add(g, rig, BoxC(x0 + L * 0.82, y, zf + 0.004, L * 0.3, 0.008, 0.01), accentGlow); // red accent stripes
                Add(g, rig, FrontQuad(x0 + L * 0.5, y0 + 0.14, zf + 0.012, L * 0.4, 0.07), Decal(Label(spec.Name.ToUpperInvariant(), 9, accent)));
                break;
            }
        }
    }

    /// <summary>Not in the library: a clean glass card outline, so no wrong model is ever shown.</summary>
    private static void Placeholder(Model3DGroup g, GpuMatch m, GpuRig rig)
    {
        double L = 2.4, H = 1.12, T = 0.4;
        rig.Size = new Size3D(L, H, T);
        rig.Accent.Color = Color.FromRgb(0x6C, 0xC8, 0xFF);
        var glass = new MaterialGroup();
        glass.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0xA0, 0xD0))));
        glass.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 60));
        Add(g, rig, BoxC(0, 0, 0, L, H, T), glass, back: glass);
        var edge = Emissive(rig.Accent);
        double e = 0.012;
        foreach (var (y, z) in new[] { (-H / 2, -T / 2), (-H / 2, T / 2), (H / 2, -T / 2), (H / 2, T / 2) }) Add(g, rig, BoxC(0, y, z, L, e, e), edge);
        foreach (var (x, z) in new[] { (-L / 2, -T / 2), (-L / 2, T / 2), (L / 2, -T / 2), (L / 2, T / 2) }) Add(g, rig, BoxC(x, 0, z, e, H, e), edge);
        foreach (var (x, y) in new[] { (-L / 2, -H / 2), (-L / 2, H / 2), (L / 2, -H / 2), (L / 2, H / 2) }) Add(g, rig, BoxC(x, y, 0, e, e, T), edge);
        Add(g, rig, FrontQuad(0, 0.08, T / 2 + 0.002, L * 0.8, 0.22), Decal(Label("MODEL NOT IN LIBRARY", 9, rig.Accent.Color)));
        Add(g, rig, FrontQuad(0, -0.16, T / 2 + 0.002, L * 0.8, 0.12), Decal(Label(Short(m.AdapterName), 12, rig.Accent.Color, FontWeights.Normal)));
    }

    /// <summary>Integrated or laptop graphics: the chip itself on its package, not a card.</summary>
    private static void Chip(Model3DGroup g, GpuMatch m, GpuRig rig, bool detailed)
    {
        bool integrated = m.Design == GpuDesign.Integrated;
        double S = integrated ? 0.45 : 0.55;
        rig.Size = new Size3D(S * 2.2, S * 1.4, S * 2.2);
        rig.Accent.Color = Color.FromRgb(0x6C, 0xC8, 0xFF);
        var substrate = Mat(Color.FromRgb(0x14, 0x4A, 0x32), 0.4, 30);
        Add(g, rig, BoxC(0, -0.04, 0, S * 2, 0.04, S * 2), substrate);
        if (integrated)
        {
            // CPU package with heat spreader; the graphics part glows through it.
            Add(g, rig, BoxC(0, 0.02, 0, S * 1.6, 0.08, S * 1.6), Mat(Brushed.Value, 0.9, 80));
            Add(g, rig, Quad(S * 0.35, 0.061, 0, S * 0.6, S * 1.2), Emissive(rig.Heat));
            Add(g, rig, TopQuad(-S * 0.25, 0.062, 0, S * 0.9, S * 0.35), Decal(Label("CPU + GPU", 4, rig.Accent.Color)));
        }
        else
        {
            // Bare laptop GPU die with memory chips around it.
            Add(g, rig, BoxC(0, 0.01, 0, S * 0.9, 0.04, S * 0.9), Mat(Color.FromRgb(0x18, 0x1A, 0x1E), 0.95, 90));
            Add(g, rig, Quad(0, 0.031, 0, S * 0.7, S * 0.7), Emissive(rig.Heat));
            for (int i = 0; i < 4; i++)
            {
                double a = Math.PI / 2 * i;
                Add(g, rig, BoxC(Math.Cos(a) * S * 0.75, -0.005, Math.Sin(a) * S * 0.75, S * 0.28, 0.03, S * 0.28), Mat(Color.FromRgb(0x15, 0x16, 0x19), 0.5, 40));
            }
        }
        for (int i = 0; i < (detailed ? 24 : 8); i++)
        {
            double a = 2 * Math.PI * i / (detailed ? 24 : 8);
            Add(g, rig, BoxC(Math.Cos(a) * S * 0.92, -0.01, Math.Sin(a) * S * 0.92, 0.03, 0.02, 0.016), Mat(Color.FromRgb(0x8C, 0x7A, 0x52), 0.6, 40));
        }
    }

    private static string Short(string name) => name.Replace("(R)", "").Replace("(TM)", "").Trim();
}
