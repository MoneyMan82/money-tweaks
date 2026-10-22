using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Controls;

/// <summary>Normalized look parameters for the preview scene (0 = lowest, 1 = highest).</summary>
public sealed record PreviewLook(double MeshDetail, double Texture, double Foliage, bool Grass, double Shadows,
    double Effects, double PostFx, double Lighting, double ViewDistance, int RenderScale)
{
    public static PreviewLook HighDefaults { get; } = new(1, 1, 1, true, 1, 1, 1, 1, 1, 100);

    /// <summary>Maps real Fortnite settings to what the illustration shows. In Performance mode Fortnite drops dynamic
    /// shadows, post-processing and advanced lighting regardless of their sliders, so the preview does too.</summary>
    public static PreviewLook From(FortnitePotatoSettings s) => new(
        MeshDetail: s.PerformanceMode ? (s.LowMeshes ? 0 : 0.55) : 1,
        Texture: s.Textures / 3.0,
        Foliage: s.Foliage / 3.0,
        Grass: s.ShowGrass,
        Shadows: s.PerformanceMode ? 0 : s.Shadows / 3.0,
        Effects: s.Effects / 3.0,
        PostFx: s.PerformanceMode ? 0 : s.PostProcessing / 3.0,
        Lighting: s.PerformanceMode ? 0 : s.Lighting / 3.0,
        ViewDistance: s.ViewDistance / 3.0,
        RenderScale: s.RenderScale);
}

/// <summary>Procedurally builds an original, Fortnite-inspired island scene. No game assets are used.</summary>
public static class PotatoScene
{
    private static readonly Color Grass = Color.FromRgb(0x5C, 0xB8, 0x46), Leaf = Color.FromRgb(0x3E, 0x9E, 0x3C), LeafDark = Color.FromRgb(0x2F, 0x86, 0x33),
        Bark = Color.FromRgb(0x7A, 0x4E, 0x2D), Rock = Color.FromRgb(0x93, 0x9A, 0xA3), Wood = Color.FromRgb(0xC8, 0x8E, 0x4E), WoodDark = Color.FromRgb(0x9C, 0x67, 0x34),
        Wall = Color.FromRgb(0xE8, 0xD9, 0xB5), Roof = Color.FromRgb(0xC9, 0x4B, 0x3A), Water = Color.FromRgb(0x3A, 0xA7, 0xE0), Mountain = Color.FromRgb(0x7E, 0x9C, 0x7A),
        Snow = Color.FromRgb(0xF2, 0xF5, 0xF7), Gold = Color.FromRgb(0xF5, 0xC4, 0x51), ChestWood = Color.FromRgb(0x8C, 0x5A, 0x2B), Blade = Color.FromRgb(0x6C, 0xC2, 0x4A),
        LeafLight = Color.FromRgb(0x56, 0xBE, 0x48), Sand = Color.FromRgb(0xF0, 0xD9, 0x9C), Ocean = Color.FromRgb(0x2E, 0x8F, 0xD6),
        Brick = Color.FromRgb(0xB8, 0x55, 0x3C), BrickDark = Color.FromRgb(0x91, 0x3F, 0x2D), Metal = Color.FromRgb(0x9E, 0xA8, 0xB6), MetalDark = Color.FromRgb(0x6F, 0x79, 0x88),
        Dirt = Color.FromRgb(0xC9, 0xA2, 0x6B), Pine = Color.FromRgb(0x2E, 0x8C, 0x4A), PineLight = Color.FromRgb(0x3F, 0xA6, 0x5A),
        Skin = Color.FromRgb(0xE8, 0xB8, 0x8F), Hoodie = Color.FromRgb(0x6C, 0x5C, 0xE7), Pants = Color.FromRgb(0x2D, 0x34, 0x48), Shoe = Color.FromRgb(0xF2, 0xF4, 0xF8), Hair = Color.FromRgb(0x3B, 0x24, 0x18),
        Storm = Color.FromRgb(0x9B, 0x4D, 0xFF);

    private static readonly Dictionary<(Color, int, bool), Material> MatCache = [];

    /// <summary>Rolling hills that fall off into the sea, so the scene reads as a small island.</summary>
    public static double Height(double x, double z, double detail)
    {
        double r = Math.Sqrt(x * x + z * z), f = Math.Clamp((12.5 - r) / 3.5, 0, 1);
        f = f * f * (3 - 2 * f);
        double hills = 0.55 * Math.Sin(x * 0.33) * Math.Cos(z * 0.29) + 0.35 * Math.Cos(x * 0.18 + z * 0.21)
            + detail * (0.12 * Math.Sin(x * 1.7 + z * 1.3) * Math.Cos(z * 1.9 - x * 0.7));
        return hills * f + (f - 1) * 1.4 + 0.15;
    }

    public static (Model3DGroup model, int triangles) Build(PreviewLook L)
    {
        var g = new Model3DGroup();
        int tris = 0;
        void Add(MeshGeometry3D m, Material mat, Material? back = null)
        {
            m.Freeze();
            tris += m.TriangleIndices.Count / 3;
            g.Children.Add(new GeometryModel3D(m, mat) { BackMaterial = back });
        }
        int Seg(int low, int high) => (int)Math.Round(low + (high - low) * L.MeshDetail);

        // Lighting: high = directional key light with specular reflections; low = flat, even, "clean" light.
        if (L.Lighting > 0.01)
        {
            g.Children.Add(new AmbientLight(Lerp(Color.FromRgb(140, 140, 150), Color.FromRgb(70, 78, 92), L.Lighting)));
            g.Children.Add(new DirectionalLight(Lerp(Color.FromRgb(200, 200, 200), Color.FromRgb(255, 236, 200), L.Lighting), new Vector3D(-0.6, -1, -0.45)));
            g.Children.Add(new DirectionalLight(Color.FromRgb((byte)(60 * L.Lighting), (byte)(80 * L.Lighting), (byte)(120 * L.Lighting)), new Vector3D(0.7, -0.3, 0.6)));
        }
        else
        {
            // Simple "clay" lighting: soft key + cool fill + ambient. No shadows or reflections, but rounded shapes still
            // read as smooth 3D forms instead of flat cut-outs: the clean, toy-like look.
            g.Children.Add(new AmbientLight(Color.FromRgb(112, 112, 116)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(182, 178, 168), new Vector3D(-0.55, -1, -0.5)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(44, 56, 74), new Vector3D(0.6, -0.2, 0.7)));
        }

        Material M(Color c, double texScale = 1) => Mat(c, L.Texture, L.Lighting > 0.5 && texScale < 0);
        bool toy = L.MeshDetail < 0.3; // Fortnite "Meshes: Low" equivalent in the illustration

        // Ocean and beach ring around the island.
        Add(Disc(new Point3D(0, -0.38, 0), 70, toy ? 24 : 64), L.Lighting > 0.3 ? Glossy(Ocean, L.Lighting, L.Texture * 0.5) : Mat(Ocean, 0, false));
        Add(Disc(new Point3D(0, -0.22, 0), 13.1, Seg(16, 64)), M(Sand));

        // Terrain
        int n = Seg(12, 52);
        Add(Terrain(14, n, L.MeshDetail), Mat(Grass, L.Texture, false));
        // Pond: glossy with reflections at high lighting, flat blue otherwise.
        var pond = Disc(new Point3D(-4.2, Height(-4.2, 3.6, L.MeshDetail) + 0.08, 3.6), 1.9, Seg(8, 40));
        Add(pond, L.Lighting > 0.3 ? Glossy(Water, L.Lighting) : Mat(Water, 0, false));

        // Trees: low mesh detail = chunky trunk + smooth stacked blobs (toy-like); high = clustered, bumpy foliage.
        (double x, double z, double s)[] trees = [(-6, -4, 1.2), (-7.5, 0.5, 1.0), (5.8, -5.5, 1.3), (7.2, 3.8, 1.1), (-2.5, -7, 0.9), (1.5, 7.2, 1.0)];
        foreach (var (x, z, s) in trees)
        {
            var y = Height(x, z, L.MeshDetail);
            if (toy)
            {
                // Chunky toy tree: thick stubby trunk, two smooth stacked blobs.
                Add(Cylinder(new Point3D(x, y - 0.1, z), 0.3 * s, 1.3 * s, 9), M(Bark));
                Add(Ellipsoid(new Point3D(x, y + 1.85 * s, z), 1.3 * s, 1.05 * s, 1.3 * s, 12, 8, 0, 0), M(Leaf));
                Add(Ellipsoid(new Point3D(x + 0.1 * s, y + 2.7 * s, z - 0.05 * s), 0.85 * s, 0.75 * s, 0.85 * s, 11, 7, 0, 0), M(LeafLight));
                continue;
            }
            Add(Cylinder(new Point3D(x, y, z), 0.18 * s, 1.6 * s, Seg(5, 14)), M(Bark));
            {
                int sl = Seg(9, 22);
                var bump = 0.12 * L.MeshDetail;
                Add(Ellipsoid(new Point3D(x, y + 2.1 * s, z), 1.05 * s, 1.0 * s, 1.05 * s, sl, sl / 2 + 2, bump, (int)(x * 7)), M(Leaf));
                Add(Ellipsoid(new Point3D(x + 0.55 * s, y + 2.7 * s, z + 0.2 * s), 0.75 * s, 0.75 * s, 0.75 * s, sl, sl / 2 + 2, bump, (int)(z * 5)), M(LeafDark));
                Add(Ellipsoid(new Point3D(x - 0.5 * s, y + 2.65 * s, z - 0.35 * s), 0.7 * s, 0.7 * s, 0.7 * s, sl, sl / 2 + 2, bump, 3), M(Leaf));
            }
        }

        // Rocks: smooth pebbles at low detail, craggy at high detail.
        (double x, double z, double s)[] rocks = [(3.2, -2.6, 0.8), (-1.2, 4.6, 0.6), (8.4, -1.2, 1.0), (-8.6, -6.5, 1.1)];
        foreach (var (x, z, s) in rocks)
            Add(Ellipsoid(new Point3D(x, Height(x, z, L.MeshDetail) + 0.25 * s, z), 1.1 * s, 0.65 * s, 0.9 * s, Seg(7, 22), Seg(5, 14), 0.22 * L.MeshDetail * s, (int)(x * 13)),
                L.Lighting > 0.6 ? Glossy(Rock, L.Lighting * 0.5, L.Texture) : M(Rock));

        // Dirt path winding across the island.
        Add(PathRibbon(L.MeshDetail, Seg(14, 70)), M(Dirt));

        // Pine trees: tiered cones (high) or stacked smooth blobs (low meshes).
        (double x, double z, double s)[] pines = [(-8.2, -2.6, 1.1), (6.8, 6.6, 1.0), (-4.6, 8.2, 0.9), (8.8, -4.0, 1.2)];
        foreach (var (x, z, s) in pines)
        {
            var y = Height(x, z, L.MeshDetail);
            Add(Cylinder(new Point3D(x, y - 0.1, z), (toy ? 0.26 : 0.16) * s, 1.1 * s, toy ? 9 : Seg(6, 14)), M(Bark));
            for (int k = 0; k < 3; k++)
            {
                double r = (1.25 - k * 0.32) * s, yy = y + (1.0 + k * 0.85) * s;
                if (toy) Add(Ellipsoid(new Point3D(x, yy + 0.35 * s, z), r, 0.62 * s, r, 11, 7, 0, 0), M(k % 2 == 0 ? Pine : PineLight));
                else Add(Cone(new Point3D(x, yy, z), r, 1.25 * s, Seg(8, 22)), M(k % 2 == 0 ? Pine : PineLight));
            }
        }

        // Bushes come from the Foliage setting.
        int bushes = (int)Math.Round(8 * L.Foliage);
        for (int i = 0; i < bushes; i++)
        {
            double a = i * 2.4, r = 4.5 + (i % 3) * 1.6, x = Math.Cos(a) * r, z = Math.Sin(a) * r;
            Add(Ellipsoid(new Point3D(x, Height(x, z, L.MeshDetail) + 0.25, z), 0.55, 0.42, 0.55, Seg(7, 16), Seg(5, 10), 0.08 * L.MeshDetail, i), M(LeafDark));
        }

        // Grass blades (Show Grass).
        if (L.Grass) Add(GrassBlades(600 + (int)(900 * L.Foliage), L.MeshDetail), Mat(Blade, 0, false), Mat(Blade, 0, false));

        // House
        var hx = -1.8; var hz = -3.2; var hy = Height(hx, hz, L.MeshDetail);
        if (toy)
        {
            // Puffy walls, a soft dome-like roof and a rounded door: no sharp edges, no window/chimney detail.
            Add(Puffy(new Point3D(hx, hy + 1.0, hz), 3.2, 2.2, 2.6, 0.42, 10), M(Wall));
            Add(Puffy(new Point3D(hx, hy + 2.35, hz), 3.9, 1.35, 3.3, 0.62, 10), M(Roof));
            Add(Puffy(new Point3D(hx + 0.4, hy + 0.7, hz + 1.3), 0.75, 1.3, 0.18, 0.5, 7), M(WoodDark));
        }
        else
        {
            Add(Box(new Point3D(hx, hy + 1.05, hz), 3.2, 2.1, 2.6), M(Wall));
            Add(RoofPrism(new Point3D(hx, hy + 2.1, hz), 3.6, 1.2, 3.0), M(Roof), M(Roof));
            Add(Box(new Point3D(hx + 0.4, hy + 0.65, hz + 1.31), 0.7, 1.3, 0.04), M(WoodDark));
        }
        if (L.MeshDetail > 0.5)
        {
            Add(Box(new Point3D(hx - 0.9, hy + 1.35, hz + 1.31), 0.6, 0.55, 0.04), Glossy(Water, 0.6));
            Add(Box(new Point3D(hx + 1.1, hy + 2.9, hz - 0.6), 0.35, 0.9, 0.35), M(Rock));
        }

        // Player build: wood, brick and metal walls, a floor, a ramp and a roof.
        // High = planks/bricks/ribs; medium = plain panels; low meshes = puffy, rounded "bubble-wrap" pieces.
        var bx = 3.0; var bz = 1.2; var by = Height(bx, bz, L.MeshDetail);
        void BuildWall(Point3D c, bool alongX, Color main, Color alt, string kind)
        {
            double w = 2.4, h = 2.4, t = 0.22;
            double sx = alongX ? w : t, sz = alongX ? t : w;
            if (toy) { Add(Puffy(c, sx + 0.08, h, sz + 0.16, 0.38, 9), Mat(main, 0, false)); return; }
            if (L.MeshDetail < 0.8) { Add(Box(c, sx, h, sz), M(main)); return; }
            switch (kind)
            {
                case "wood":
                    for (int i = 0; i < 6; i++) Add(Box(new Point3D(c.X, c.Y - h / 2 + 0.2 + i * 0.4, c.Z), alongX ? w : t, 0.34, alongX ? t : w), M(i % 2 == 0 ? main : alt));
                    Add(Box(new Point3D(c.X - (alongX ? w / 2 - 0.07 : 0), c.Y, c.Z - (alongX ? 0 : w / 2 - 0.07)), alongX ? 0.14 : t + 0.08, h, alongX ? t + 0.08 : 0.14), M(alt));
                    Add(Box(new Point3D(c.X + (alongX ? w / 2 - 0.07 : 0), c.Y, c.Z + (alongX ? 0 : w / 2 - 0.07)), alongX ? 0.14 : t + 0.08, h, alongX ? t + 0.08 : 0.14), M(alt));
                    break;
                case "brick":
                    Add(Box(c, sx * 0.98, h, sz * 0.8), M(Color.FromRgb(0xD9, 0xC9, 0xB4)));
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 4; col++)
                        {
                            double off = (row % 2) * 0.3, u = -w / 2 + 0.3 + col * 0.6 + off;
                            if (u > w / 2 - 0.25) continue;
                            var pc = new Point3D(c.X + (alongX ? u : 0), c.Y - h / 2 + 0.17 + row * 0.34, c.Z + (alongX ? 0 : u));
                            Add(Box(pc, alongX ? 0.54 : t, 0.28, alongX ? t : 0.54), M((row + col) % 3 == 0 ? alt : main));
                        }
                    break;
                default: // metal
                    Add(Box(c, sx, h, sz), M(main));
                    for (int i = 0; i < 5; i++)
                    {
                        double u = -w / 2 + 0.25 + i * 0.475;
                        Add(Box(new Point3D(c.X + (alongX ? u : 0), c.Y, c.Z + (alongX ? 0 : u)), alongX ? 0.09 : t + 0.1, h * 0.96, alongX ? t + 0.1 : 0.09),
                            L.Lighting > 0.5 ? Glossy(alt, 0.5) : M(alt));
                    }
                    break;
            }
        }
        BuildWall(new Point3D(bx, by + 1.2, bz + 1.2), true, Wood, WoodDark, "wood");
        BuildWall(new Point3D(bx, by + 1.2, bz - 1.2), true, Brick, BrickDark, "brick");
        BuildWall(new Point3D(bx - 1.2, by + 1.2, bz), false, Brick, BrickDark, "brick");
        BuildWall(new Point3D(bx + 1.2, by + 1.2, bz), false, Metal, MetalDark, "metal");
        // Second-storey floor + roof
        if (toy)
        {
            Add(Puffy(new Point3D(bx, by + 2.48, bz), 2.6, 0.26, 2.6, 0.4, 9), Mat(WoodDark, 0, false));
            Add(Ellipsoid(new Point3D(bx, by + 2.6, bz), 1.75, 1.15, 1.75, 12, 7, 0, 0), M(Metal));
        }
        else
        {
            Add(Box(new Point3D(bx, by + 2.48, bz), 2.5, 0.16, 2.5), M(WoodDark));
            Add(Cone(new Point3D(bx, by + 2.56, bz), 1.8, 1.3, 4), L.Lighting > 0.5 ? Glossy(Metal, 0.5, L.Texture) : M(Metal));
        }
        // Ramp up to the second storey (wood).
        if (toy) AddTransformed(g, Puffy(new Point3D(0, 0, 0), 3.3, 0.3, 2.4, 0.4, 9), Mat(Wood, 0, false), new Point3D(bx - 2.35, by + 1.2, bz), -45, ref tris);
        else if (L.MeshDetail < 0.8) AddTransformed(g, Box(new Point3D(0, 0, 0), 3.3, 0.2, 2.4), M(Wood), new Point3D(bx - 2.35, by + 1.2, bz), -45, ref tris);
        else
            for (int i = 0; i < 7; i++)
                AddTransformed(g, Box(new Point3D(-1.4 + i * 0.47, 0, 0), 0.43, 0.18, 2.4), M(i % 2 == 0 ? Wood : WoodDark), new Point3D(bx - 2.35, by + 1.2, bz), -45, ref tris);

        // Loot chest + effect particles.
        var cx = 0.6; var cz = 3.4; var cy = Height(cx, cz, L.MeshDetail);
        if (L.MeshDetail < 0.3) Add(Puffy(new Point3D(cx, cy + 0.3, cz), 0.9, 0.6, 0.6, 0.45, 8), Mat(ChestWood, 0, false));
        else
        {
            Add(Box(new Point3D(cx, cy + 0.25, cz), 0.9, 0.5, 0.6), M(ChestWood));
            Add(Box(new Point3D(cx, cy + 0.56, cz), 0.94, 0.14, 0.64), Glossy(Gold, 0.4 + 0.6 * L.Lighting));
        }
        int sparks = (int)Math.Round(26 * L.Effects);
        var rnd = new Random(7);
        for (int i = 0; i < sparks; i++)
        {
            var p = new Point3D(cx + (rnd.NextDouble() - 0.5) * 1.6, cy + 0.6 + rnd.NextDouble() * 1.6, cz + (rnd.NextDouble() - 0.5) * 1.6);
            Add(Box(p, 0.07, 0.07, 0.07), new EmissiveMaterial(new SolidColorBrush(Gold)));
        }

        // An original stylized player character (not based on any game's skins).
        {
            double px = cx + 1.6, pz = cz - 0.6, py = Height(px, pz, L.MeshDetail);
            int cs = toy ? 9 : Seg(10, 22), cst = toy ? 6 : Seg(7, 14);
            Add(Cylinder(new Point3D(px - 0.14, py + 0.08, pz), toy ? 0.15 : 0.12, 0.72, cs), M(Pants));
            Add(Cylinder(new Point3D(px + 0.14, py + 0.08, pz), toy ? 0.15 : 0.12, 0.72, cs), M(Pants));
            Add(Ellipsoid(new Point3D(px - 0.14, py + 0.07, pz + 0.06), 0.13, 0.08, 0.2, cs, cst, 0, 0), Mat(Shoe, 0, false));
            Add(Ellipsoid(new Point3D(px + 0.14, py + 0.07, pz + 0.06), 0.13, 0.08, 0.2, cs, cst, 0, 0), Mat(Shoe, 0, false));
            Add(Ellipsoid(new Point3D(px, py + 1.12, pz), toy ? 0.4 : 0.36, 0.44, toy ? 0.3 : 0.24, cs, cst, 0, 0), M(Hoodie));
            Add(Cylinder(new Point3D(px - 0.44, py + 0.72, pz), toy ? 0.12 : 0.09, 0.62, cs), M(Hoodie));
            Add(Cylinder(new Point3D(px + 0.44, py + 0.72, pz), toy ? 0.12 : 0.09, 0.62, cs), M(Hoodie));
            Add(Ellipsoid(new Point3D(px - 0.44, py + 0.68, pz), 0.09, 0.09, 0.09, cs, cst, 0, 0), M(Skin));
            Add(Ellipsoid(new Point3D(px + 0.44, py + 0.68, pz), 0.09, 0.09, 0.09, cs, cst, 0, 0), M(Skin));
            Add(Ellipsoid(new Point3D(px, py + 1.78, pz), toy ? 0.3 : 0.26, toy ? 0.3 : 0.28, toy ? 0.3 : 0.26, cs, cst, 0, 0), M(Skin));
            Add(Ellipsoid(new Point3D(px, py + 1.92, pz - 0.03), toy ? 0.31 : 0.27, 0.17, toy ? 0.31 : 0.27, cs, cst, 0, 0), M(Hair));
        }

        // Shadows (blob shadows offset along the key light) and ambient occlusion (contact darkening).
        if (L.Shadows > 0.01)
        {
            var shadow = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb((byte)(110 * L.Shadows), 10, 25, 10)));
            foreach (var (x, z, s) in trees) Add(Disc(new Point3D(x + 0.8 * s, Height(x + 0.8 * s, z + 0.6 * s, L.MeshDetail) + 0.05, z + 0.6 * s), 1.4 * s, 20), shadow);
            Add(Disc(new Point3D(hx + 1, hy + 0.05, hz + 1.2), 2.3, 20), shadow);
            Add(Disc(new Point3D(bx + 0.9, by + 0.05, bz - 0.3), 1.8, 20), shadow);
        }
        if (L.PostFx > 0.01)
        {
            var ao = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb((byte)(90 * L.PostFx), 0, 0, 0)));
            foreach (var (x, z, s) in rocks) Add(Disc(new Point3D(x, Height(x, z, L.MeshDetail) + 0.04, z), 1.0 * s, 16), ao);
            foreach (var (x, z, s) in trees) Add(Disc(new Point3D(x, Height(x, z, L.MeshDetail) + 0.04, z), 0.45 * s, 12), ao);
        }

        // View distance: distant mountains and tree lines beyond the island.
        if (L.ViewDistance > 0.01)
        {
            int mountains = (int)Math.Round(3 + 7 * L.ViewDistance);
            for (int i = 0; i < mountains; i++)
            {
                double a = i * (Math.PI * 2 / mountains) + 0.3, r = 26 + (i % 3) * 4;
                var h = 6 + (i % 4) * 2.5;
                double mx = Math.Cos(a) * r, mz = Math.Sin(a) * r, mr = 5 + (i % 3);
                if (toy)
                {
                    // Distant islands become soft, rounded hills with a smooth snow cap.
                    Add(Ellipsoid(new Point3D(mx, -0.4, mz), mr, h * 0.85, mr, 12, 7, 0, 0), M(Mountain));
                    if (L.ViewDistance > 0.6) Add(Ellipsoid(new Point3D(mx, -0.4 + h * 0.62, mz), mr * 0.42, h * 0.3, mr * 0.42, 10, 6, 0, 0), Mat(Snow, 0, false));
                    continue;
                }
                Add(Cone(new Point3D(mx, -1, mz), mr, h, Seg(6, 18)), M(Mountain));
                if (L.ViewDistance > 0.6) Add(Cone(new Point3D(mx, -1 + h * 0.72, mz), mr * 0.28, h * 0.28, Seg(6, 18)), Mat(Snow, 0, false));
            }
            if (L.ViewDistance > 0.4)
                for (int i = 0; i < 16; i++)
                {
                    double a = i * 0.39 + 0.2, r = 10.3, tx = Math.Cos(a) * r, tz = Math.Sin(a) * r;
                    if (Math.Abs(tx - 2.6) < 4 && Math.Abs(tz - 1.6) < 3) continue; // keep the builds visible
                    Add(Ellipsoid(new Point3D(tx, Height(tx, tz, L.MeshDetail) + 0.8, tz), 0.75, 1.1, 0.75, Seg(9, 14), Seg(6, 10), 0.06 * L.MeshDetail, i), M(LeafDark));
                }
        }
        // Storm wall at the edge of the play area (translucent, drawn last so it blends over everything behind it).
        {
            var storm = new MaterialGroup();
            storm.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0x48, Storm.R, Storm.G, Storm.B))));
            storm.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(0x30, Storm.R, Storm.G, Storm.B))));
            storm.Freeze();
            Add(Tube(24, -1.2, 16, toy ? 24 : 72), storm, storm);
        }
        g.Freeze();
        return (g, tris);
    }

    private static void AddTransformed(Model3DGroup g, MeshGeometry3D m, Material mat, Point3D at, double tiltDeg, ref int tris)
    {
        m.Freeze();
        tris += m.TriangleIndices.Count / 3;
        var t = new Transform3DGroup();
        t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), tiltDeg)));
        t.Children.Add(new TranslateTransform3D(at.X, at.Y, at.Z));
        g.Children.Add(new GeometryModel3D(m, mat) { Transform = t, BackMaterial = mat });
    }

    // ---- materials --------------------------------------------------------------------------------------------
    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Flat colour when texture detail is 0; tiled noise texture whose contrast grows with texture quality.</summary>
    public static Material Mat(Color c, double texture, bool _)
    {
        int bucket = (int)Math.Round(texture * 3);
        if (MatCache.TryGetValue((c, bucket, false), out var m)) return m;
        Brush brush = bucket == 0 ? new SolidColorBrush(c) : NoiseBrush(c, bucket / 3.0, c.GetHashCode());
        brush.Freeze();
        m = new DiffuseMaterial(brush);
        m.Freeze();
        MatCache[(c, bucket, false)] = m;
        return m;
    }

    private static Material Glossy(Color c, double shine, double texture = 0)
    {
        var mg = new MaterialGroup();
        mg.Children.Add(Mat(c, texture, false));
        mg.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(shine, 0, 1)), 255, 255, 255)), 40));
        mg.Freeze();
        return mg;
    }

    private static ImageBrush NoiseBrush(Color c, double amount, int seed)
    {
        const int S = 64;
        var rnd = new Random(seed);
        var px = new byte[S * S * 4];
        // Two octaves of blocky value noise: reads as surface grain/detail at a distance.
        var coarse = new double[9, 9];
        for (int i = 0; i < 9; i++) for (int j = 0; j < 9; j++) coarse[i, j] = rnd.NextDouble() - 0.5;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double fx = x / 8.0, fy = y / 8.0;
                int ix = (int)fx, iy = (int)fy;
                double tx = fx - ix, ty = fy - iy;
                double v = coarse[ix, iy] * (1 - tx) * (1 - ty) + coarse[ix + 1, iy] * tx * (1 - ty) + coarse[ix, iy + 1] * (1 - tx) * ty + coarse[ix + 1, iy + 1] * tx * ty;
                v = v * 0.9 + (rnd.NextDouble() - 0.5) * 0.6;
                double k = 1 + v * 0.55 * amount;
                int o = (y * S + x) * 4;
                px[o] = (byte)Math.Clamp(c.B * k, 0, 255);
                px[o + 1] = (byte)Math.Clamp(c.G * k, 0, 255);
                px[o + 2] = (byte)Math.Clamp(c.R * k, 0, 255);
                px[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(S, S, 96, 96, PixelFormats.Bgra32, null, px, S * 4);
        bmp.Freeze();
        return new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 0.25, 0.25), ViewportUnits = BrushMappingMode.RelativeToBoundingBox };
    }

    // ---- geometry ---------------------------------------------------------------------------------------------
    private static MeshGeometry3D Terrain(double half, int n, double detail)
    {
        var m = new MeshGeometry3D();
        double step = half * 2 / n;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                double x = -half + i * step, z = -half + j * step;
                m.Positions.Add(new Point3D(x, Height(x, z, detail), z));
                double e = 0.05;
                var nx = Height(x - e, z, detail) - Height(x + e, z, detail);
                var nz = Height(x, z - e, detail) - Height(x, z + e, detail);
                var nv = new Vector3D(nx, 2 * e, nz); nv.Normalize();
                m.Normals.Add(nv);
                m.TextureCoordinates.Add(new Point(i / (double)n * 4, j / (double)n * 4));
            }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int a = i * (n + 1) + j, b = a + n + 1;
                m.TriangleIndices.Add(a); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b);
                m.TriangleIndices.Add(b); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b + 1);
            }
        return m;
    }

    /// <summary>Smooth-shaded ellipsoid; <paramref name="bump"/> adds craggy noise for high mesh detail.</summary>
    private static MeshGeometry3D Ellipsoid(Point3D c, double rx, double ry, double rz, int slices, int stacks, double bump, int seed)
    {
        var m = new MeshGeometry3D();
        for (int i = 0; i <= stacks; i++)
        {
            double v = Math.PI * i / stacks, sv = Math.Sin(v), cv = Math.Cos(v);
            for (int j = 0; j <= slices; j++)
            {
                double u = 2 * Math.PI * (j % slices) / slices, su = Math.Sin(u), cu = Math.Cos(u);
                var nrm = new Vector3D(sv * cu, cv, sv * su);
                double k = 1 + bump * (Math.Sin(u * 5 + seed) * Math.Sin(v * 4 + seed * 0.7) + 0.5 * Math.Sin(u * 11 - seed) * Math.Cos(v * 9));
                m.Positions.Add(new Point3D(c.X + rx * nrm.X * k, c.Y + ry * nrm.Y * k, c.Z + rz * nrm.Z * k));
                var n = new Vector3D(nrm.X / rx, nrm.Y / ry, nrm.Z / rz); n.Normalize();
                m.Normals.Add(n);
                m.TextureCoordinates.Add(new Point(j / (double)slices, i / (double)stacks));
            }
        }
        for (int i = 0; i < stacks; i++)
            for (int j = 0; j < slices; j++)
            {
                int a = i * (slices + 1) + j, b = a + slices + 1;
                m.TriangleIndices.Add(a); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b);
                m.TriangleIndices.Add(b); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b + 1);
            }
        return m;
    }

    /// <summary>Superellipsoid "puffy" panel: the rounded bubble-wrap look of low meshes.</summary>
    private static MeshGeometry3D Puffy(Point3D c, double sx, double sy, double sz, double e, int seg)
    {
        var m = new MeshGeometry3D();
        static double P(double v, double e) => Math.Sign(v) * Math.Pow(Math.Abs(v), e);
        int stacks = seg, slices = seg * 2;
        for (int i = 0; i <= stacks; i++)
        {
            double v = -Math.PI / 2 + Math.PI * i / stacks;
            for (int j = 0; j <= slices; j++)
            {
                double u = -Math.PI + 2 * Math.PI * (j % slices) / slices;
                double x = P(Math.Cos(v), e) * P(Math.Cos(u), e), y = P(Math.Sin(v), e), z = P(Math.Cos(v), e) * P(Math.Sin(u), e);
                m.Positions.Add(new Point3D(c.X + x * sx / 2, c.Y + y * sy / 2, c.Z + z * sz / 2));
                // Normal of a superellipsoid: uses the (2-e) power; scaled by the inverse size for non-uniform axes.
                double nx = P(Math.Cos(v), 2 - e) * P(Math.Cos(u), 2 - e) / sx, ny = P(Math.Sin(v), 2 - e) / sy, nz = P(Math.Cos(v), 2 - e) * P(Math.Sin(u), 2 - e) / sz;
                var n = new Vector3D(nx, ny, nz);
                if (n.Length > 1e-9) n.Normalize(); else n = new Vector3D(0, Math.Sign(y), 0);
                m.Normals.Add(n);
                m.TextureCoordinates.Add(new Point(j / (double)slices, i / (double)stacks));
            }
        }
        for (int i = 0; i < stacks; i++)
            for (int j = 0; j < slices; j++)
            {
                int a = i * (slices + 1) + j, b = a + slices + 1;
                m.TriangleIndices.Add(a); m.TriangleIndices.Add(b); m.TriangleIndices.Add(a + 1);
                m.TriangleIndices.Add(b); m.TriangleIndices.Add(b + 1); m.TriangleIndices.Add(a + 1);
            }
        return m;
    }

    /// <summary>Axis-aligned box with flat normals (also used by Reality Lab's virtual PC).</summary>
    internal static MeshGeometry3D Box(Point3D c, double sx, double sy, double sz)
    {
        var m = new MeshGeometry3D();
        double x0 = c.X - sx / 2, x1 = c.X + sx / 2, y0 = c.Y - sy / 2, y1 = c.Y + sy / 2, z0 = c.Z - sz / 2, z1 = c.Z + sz / 2;
        void Face(Point3D a, Point3D b, Point3D cc, Point3D d, Vector3D n)
        {
            int i = m.Positions.Count;
            foreach (var p in new[] { a, b, cc, d }) { m.Positions.Add(p); m.Normals.Add(n); }
            m.TextureCoordinates.Add(new Point(0, 1)); m.TextureCoordinates.Add(new Point(1, 1)); m.TextureCoordinates.Add(new Point(1, 0)); m.TextureCoordinates.Add(new Point(0, 0));
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 2); m.TriangleIndices.Add(i + 3);
        }
        Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(0, 0, 1));
        Face(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(0, 0, -1));
        Face(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), new(-1, 0, 0));
        Face(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(1, 0, 0));
        Face(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0), new(0, 1, 0));
        Face(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), new(0, -1, 0));
        return m;
    }

    private static MeshGeometry3D RoofPrism(Point3D baseCenter, double w, double h, double d)
    {
        var m = new MeshGeometry3D();
        double x0 = baseCenter.X - w / 2, x1 = baseCenter.X + w / 2, y0 = baseCenter.Y, y1 = baseCenter.Y + h, z0 = baseCenter.Z - d / 2, z1 = baseCenter.Z + d / 2, xm = baseCenter.X;
        void Tri(Point3D a, Point3D b, Point3D c)
        {
            var n = Vector3D.CrossProduct(b - a, c - a); n.Normalize();
            int i = m.Positions.Count;
            foreach (var p in new[] { a, b, c }) { m.Positions.Add(p); m.Normals.Add(n); m.TextureCoordinates.Add(new Point(p.X, p.Z)); }
            m.TriangleIndices.Add(i); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i + 2);
        }
        Tri(new(x0, y0, z1), new(xm, y1, z1), new(xm, y1, z0)); Tri(new(x0, y0, z1), new(xm, y1, z0), new(x0, y0, z0));
        Tri(new(x1, y0, z0), new(xm, y1, z0), new(xm, y1, z1)); Tri(new(x1, y0, z0), new(xm, y1, z1), new(x1, y0, z1));
        Tri(new(x0, y0, z1), new(x1, y0, z1), new(xm, y1, z1));
        Tri(new(x1, y0, z0), new(x0, y0, z0), new(xm, y1, z0));
        return m;
    }

    private static MeshGeometry3D PathRibbon(double detail, int seg)
    {
        var m = new MeshGeometry3D();
        for (int i = 0; i <= seg; i++)
        {
            double t = i / (double)seg, x = -11 + 22 * t, z = -3 + 6 * t + 2.2 * Math.Sin(t * 3.4), w = 0.7;
            double dx = 22, dz = 6 + 2.2 * 3.4 * Math.Cos(t * 3.4), len = Math.Sqrt(dx * dx + dz * dz);
            double nx = -dz / len * w, nz = dx / len * w;
            m.Positions.Add(new Point3D(x + nx, Height(x + nx, z + nz, detail) + 0.04, z + nz));
            m.Positions.Add(new Point3D(x - nx, Height(x - nx, z - nz, detail) + 0.04, z - nz));
            m.Normals.Add(new Vector3D(0, 1, 0)); m.Normals.Add(new Vector3D(0, 1, 0));
            m.TextureCoordinates.Add(new Point(t * 8, 0)); m.TextureCoordinates.Add(new Point(t * 8, 1));
            if (i == 0) continue;
            int k = i * 2;
            m.TriangleIndices.Add(k - 2); m.TriangleIndices.Add(k); m.TriangleIndices.Add(k - 1);
            m.TriangleIndices.Add(k - 1); m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 1);
        }
        return m;
    }

    private static MeshGeometry3D Tube(double r, double y0, double h, int seg)
    {
        var m = new MeshGeometry3D();
        for (int i = 0; i <= seg; i++)
        {
            double a = 2 * Math.PI * i / seg, x = Math.Cos(a), z = Math.Sin(a);
            m.Positions.Add(new Point3D(x * r, y0, z * r)); m.Positions.Add(new Point3D(x * r, y0 + h, z * r));
            m.Normals.Add(new Vector3D(-x, 0, -z)); m.Normals.Add(new Vector3D(-x, 0, -z));
            m.TextureCoordinates.Add(new Point(i / (double)seg, 1)); m.TextureCoordinates.Add(new Point(i / (double)seg, 0));
        }
        for (int i = 0; i < seg; i++)
        {
            int a = i * 2;
            m.TriangleIndices.Add(a); m.TriangleIndices.Add(a + 2); m.TriangleIndices.Add(a + 1);
            m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(a + 2); m.TriangleIndices.Add(a + 3);
        }
        return m;
    }

    private static MeshGeometry3D Cylinder(Point3D b, double r, double h, int seg)
    {
        var m = new MeshGeometry3D();
        for (int i = 0; i <= seg; i++)
        {
            double a = 2 * Math.PI * (i % seg) / seg, x = Math.Cos(a), z = Math.Sin(a);
            m.Positions.Add(new Point3D(b.X + x * r, b.Y, b.Z + z * r)); m.Normals.Add(new Vector3D(x, 0, z)); m.TextureCoordinates.Add(new Point(i / (double)seg, 1));
            m.Positions.Add(new Point3D(b.X + x * r * 0.8, b.Y + h, b.Z + z * r * 0.8)); m.Normals.Add(new Vector3D(x, 0, z)); m.TextureCoordinates.Add(new Point(i / (double)seg, 0));
        }
        for (int i = 0; i < seg; i++)
        {
            int a = i * 2;
            m.TriangleIndices.Add(a); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(a + 2);
            m.TriangleIndices.Add(a + 2); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(a + 3);
        }
        return m;
    }

    private static MeshGeometry3D Cone(Point3D b, double r, double h, int seg)
    {
        var m = new MeshGeometry3D();
        var apex = new Point3D(b.X, b.Y + h, b.Z);
        for (int i = 0; i < seg; i++)
        {
            double a0 = 2 * Math.PI * i / seg, a1 = 2 * Math.PI * (i + 1) / seg;
            var p0 = new Point3D(b.X + Math.Cos(a0) * r, b.Y, b.Z + Math.Sin(a0) * r);
            var p1 = new Point3D(b.X + Math.Cos(a1) * r, b.Y, b.Z + Math.Sin(a1) * r);
            int k = m.Positions.Count;
            m.Positions.Add(p0); m.Positions.Add(apex); m.Positions.Add(p1);
            var n0 = new Vector3D(Math.Cos(a0), r / h, Math.Sin(a0)); n0.Normalize();
            var n1 = new Vector3D(Math.Cos(a1), r / h, Math.Sin(a1)); n1.Normalize();
            var na = n0 + n1; na.Normalize();
            m.Normals.Add(n0); m.Normals.Add(na); m.Normals.Add(n1);
            m.TextureCoordinates.Add(new Point(0, 1)); m.TextureCoordinates.Add(new Point(0.5, 0)); m.TextureCoordinates.Add(new Point(1, 1));
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 1); m.TriangleIndices.Add(k + 2);
        }
        return m;
    }

    private static MeshGeometry3D Disc(Point3D c, double r, int seg)
    {
        var m = new MeshGeometry3D();
        m.Positions.Add(c); m.Normals.Add(new Vector3D(0, 1, 0)); m.TextureCoordinates.Add(new Point(0.5, 0.5));
        for (int i = 0; i <= seg; i++)
        {
            double a = 2 * Math.PI * i / seg;
            m.Positions.Add(new Point3D(c.X + Math.Cos(a) * r, c.Y, c.Z + Math.Sin(a) * r)); m.Normals.Add(new Vector3D(0, 1, 0));
            m.TextureCoordinates.Add(new Point(0.5 + Math.Cos(a) / 2, 0.5 + Math.Sin(a) / 2));
        }
        for (int i = 1; i <= seg; i++) { m.TriangleIndices.Add(0); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i); }
        return m;
    }

    private static MeshGeometry3D GrassBlades(int count, double detail)
    {
        var m = new MeshGeometry3D();
        var rnd = new Random(42);
        for (int i = 0; i < count; i++)
        {
            double x = (rnd.NextDouble() - 0.5) * 22, z = (rnd.NextDouble() - 0.5) * 22;
            if (Math.Abs(x + 4.2) < 2.1 && Math.Abs(z - 3.6) < 2.1) continue; // keep the pond clear
            double y = Height(x, z, detail), a = rnd.NextDouble() * Math.PI, h = 0.18 + rnd.NextDouble() * 0.22, w = 0.05;
            int k = m.Positions.Count;
            m.Positions.Add(new Point3D(x - Math.Cos(a) * w, y, z - Math.Sin(a) * w));
            m.Positions.Add(new Point3D(x + Math.Cos(a) * w, y, z + Math.Sin(a) * w));
            m.Positions.Add(new Point3D(x + (rnd.NextDouble() - 0.5) * 0.08, y + h, z + (rnd.NextDouble() - 0.5) * 0.08));
            for (int j = 0; j < 3; j++) { m.Normals.Add(new Vector3D(0, 1, 0)); m.TextureCoordinates.Add(new Point(0, 0)); }
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 1); m.TriangleIndices.Add(k + 2);
        }
        return m;
    }
}

/// <summary>Before/after split view of the preview scene with drag-to-compare, drag-to-orbit and auto-rotate.</summary>
public sealed class PotatoPreview : Grid
{
    private readonly Viewport3D _before = new(), _after = new();
    private readonly PerspectiveCamera _camB = new() { FieldOfView = 48 }, _camA = new() { FieldOfView = 48 };
    private readonly ModelVisual3D _sceneB = new(), _sceneA = new();
    private readonly RectangleGeometry _clip = new();
    private readonly RectangleGeometry _imgClip = new();
    private readonly Image _imgBefore = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    private readonly Image _imgAfter = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    public bool ShowingScreenshots { get; private set; }
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
    private readonly Border _divider = new() { Width = 2, Background = Brushes.White, IsHitTestVisible = false };
    private readonly Border _handle;
    private readonly TextBlock _beforeLabel, _afterLabel, _hint;
    private double _split = 0.5, _angle = 0.6;
    private long _lastSpin;
    // 30 fps timer that only runs while rotating and visible. CompositionTarget.Rendering kept WPF rendering at
    // 60 fps for as long as a handler was attached, even when nothing moved.
    private readonly System.Windows.Threading.DispatcherTimer _spin = new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private Window? _window;
    private bool _dragSplit, _dragOrbit;
    private Point _dragStart;
    private double _dragAngle;
    private bool _autoRotate = true;
    public bool AutoRotate { get => _autoRotate; set { _autoRotate = value; UpdateSpin(); } }
    public int BeforeTriangles { get; private set; }
    public int AfterTriangles { get; private set; }
    public event Action? TrianglesChanged;

    public PotatoPreview()
    {
        ClipToBounds = true;
        Background = new LinearGradientBrush(Color.FromRgb(0x3C, 0x8C, 0xF5), Color.FromRgb(0xB8, 0xE8, 0xFF), 90);
        _before.Camera = _camB; _after.Camera = _camA;
        _before.Children.Add(_sceneB); _after.Children.Add(_sceneA);
        _after.Clip = _clip;
        RenderOptions.SetEdgeMode(_before, EdgeMode.Unspecified);
        Children.Add(_before);
        Children.Add(_after);
        _imgAfter.Clip = _imgClip;
        RenderOptions.SetBitmapScalingMode(_imgBefore, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_imgAfter, BitmapScalingMode.HighQuality);
        Children.Add(_imgBefore);
        Children.Add(_imgAfter);

        _handle = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(0), Background = Brushes.White, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.45 },
            Child = new TextBlock { Text = "◀ ▶", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x18)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _overlay.Children.Add(_divider);
        _overlay.Children.Add(_handle);
        Children.Add(_overlay);
        _beforeLabel = CornerLabel("BEFORE · high settings", HorizontalAlignment.Left);
        _afterLabel = CornerLabel("AFTER · your settings", HorizontalAlignment.Right);
        _hint = new TextBlock
        {
            Text = "Drag the handle to compare · drag the scene to rotate", FontSize = 11, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9 }
        };
        Children.Add(_hint);

        SizeChanged += (_, _) => Layout();
        _overlay.MouseLeftButtonDown += OnDown;
        _overlay.MouseMove += OnMove;
        _overlay.MouseLeftButtonUp += (_, _) => { _dragSplit = _dragOrbit = false; _overlay.ReleaseMouseCapture(); };
        _spin.Tick += OnSpin;
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) { _window.Activated += WindowChanged; _window.Deactivated += WindowChanged; _window.StateChanged += WindowChanged; }
            UpdateSpin();
        };
        Unloaded += (_, _) =>
        {
            _spin.Stop();
            if (_window is not null) { _window.Activated -= WindowChanged; _window.Deactivated -= WindowChanged; _window.StateChanged -= WindowChanged; }
        };
        IsVisibleChanged += (_, _) => UpdateSpin();

        var (b, bt) = PotatoScene.Build(PreviewLook.HighDefaults);
        _sceneB.Content = b;
        BeforeTriangles = bt;
        UpdateCameras();
    }

    private TextBlock CornerLabel(string text, HorizontalAlignment h)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Padding = new Thickness(10, 5, 10, 5), IsHitTestVisible = false };
        var b = new Border
        {
            Child = t, CornerRadius = new CornerRadius(0), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x08, 0x0C, 0x0A)),
            Margin = new Thickness(12), HorizontalAlignment = h, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        };
        Children.Add(b);
        return t;
    }

    public void SetAfter(PreviewLook look, string label)
    {
        using var perf = Core.Sys.Perf.Measure("potato scene build", 10);
        var (a, at) = PotatoScene.Build(look);
        _sceneA.Content = a;
        AfterTriangles = at;
        if (!ShowingScreenshots) _afterLabel.Text = "AFTER · " + label;
        // 3D resolution below 100% renders fewer pixels: shown as softness on the "after" half only.
        _after.Effect = look.RenderScale < 100 ? new BlurEffect { Radius = (100 - look.RenderScale) / 14.0, RenderingBias = RenderingBias.Performance } : null;
        TrianglesChanged?.Invoke();
    }

    /// <summary>Compare two real screenshots instead of the 3D illustration (3D rendering stops while shown).</summary>
    public void ShowScreenshots(ImageSource before, ImageSource after)
    {
        _imgBefore.Source = before;
        _imgAfter.Source = after;
        _imgBefore.Visibility = _imgAfter.Visibility = Visibility.Visible;
        _before.Visibility = _after.Visibility = Visibility.Collapsed;
        _beforeLabel.Text = "BEFORE · your screenshot";
        _afterLabel.Text = "AFTER · your screenshot";
        ShowingScreenshots = true;
        UpdateSpin();
    }

    public void ShowIllustration(string afterLabel)
    {
        _imgBefore.Visibility = _imgAfter.Visibility = Visibility.Collapsed;
        _imgBefore.Source = _imgAfter.Source = null;
        _before.Visibility = _after.Visibility = Visibility.Visible;
        _beforeLabel.Text = "BEFORE · high settings";
        _afterLabel.Text = "AFTER · " + afterLabel;
        ShowingScreenshots = false;
        UpdateSpin();
    }

    /// <summary>Loads and downscales an image off the UI thread (frozen, so it can cross threads).</summary>
    public static Task<BitmapSource> LoadImageAsync(string path) => Task.Run<BitmapSource>(() =>
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 1920;
        bi.EndInit();
        bi.Freeze();
        return bi;
    });

    private void Layout()
    {
        var x = ActualWidth * _split;
        _clip.Rect = new Rect(x, 0, Math.Max(0, ActualWidth - x), ActualHeight);
        _imgClip.Rect = _clip.Rect;
        Canvas.SetLeft(_divider, x - 1); _divider.Height = ActualHeight;
        Canvas.SetLeft(_handle, x - 17); Canvas.SetTop(_handle, ActualHeight / 2 - 17);
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _dragStart = p;
        if (Math.Abs(p.X - ActualWidth * _split) < 24) _dragSplit = true;
        else { _dragOrbit = true; _dragAngle = _angle; }
        _overlay.CaptureMouse();
        _hint.Visibility = Visibility.Collapsed;
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragSplit) { _split = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0.03, 0.97); Layout(); }
        else if (_dragOrbit) { _angle = _dragAngle - (p.X - _dragStart.X) * 0.01; UpdateCameras(); }
        else _overlay.Cursor = Math.Abs(p.X - ActualWidth * _split) < 24 ? Cursors.SizeWE : Cursors.Hand;
    }

    private void WindowChanged(object? s, EventArgs e) => UpdateSpin();

    private void UpdateSpin()
    {
        bool run = _autoRotate && !ShowingScreenshots && IsLoaded && IsVisible && !App.ReduceMotion
            && _window is { IsActive: true } w && w.WindowState != WindowState.Minimized;
        if (run && !_spin.IsEnabled) { _lastSpin = System.Diagnostics.Stopwatch.GetTimestamp(); _spin.Start(); }
        else if (!run && _spin.IsEnabled) _spin.Stop();
    }

    private void OnSpin(object? s, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = Math.Min(0.1, System.Diagnostics.Stopwatch.GetElapsedTime(_lastSpin, now).TotalSeconds);
        _lastSpin = now;
        if (_dragOrbit) return;
        _angle += dt * 0.12;
        UpdateCameras();
    }

    private void UpdateCameras()
    {
        double r = 19, h = 8.5;
        var pos = new Point3D(Math.Cos(_angle) * r, h, Math.Sin(_angle) * r);
        var look = new Vector3D(-pos.X, 1.2 - pos.Y, -pos.Z);
        foreach (var c in new[] { _camB, _camA }) { c.Position = pos; c.LookDirection = look; c.UpDirection = new Vector3D(0, 1, 0); }
    }
}
