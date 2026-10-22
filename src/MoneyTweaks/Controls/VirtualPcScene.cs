using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MoneyTweaks.Controls;

/// <summary>
/// Readings that drive the virtual PC. Null means Windows gave no reading: that part is drawn idle and grey, never
/// with an invented value.
/// </summary>
public sealed record PcReading(double CpuPct, double? CpuTempC, double? GpuPct, double? GpuTempC, double RamUsedGb, double RamTotalGb);

/// <summary>
/// Reality Lab's 3D virtual PC: an ATX motherboard (printed circuit board with traces, silkscreen, VRM heatsinks,
/// capacitors, chipset, M.2, power connectors), a tower CPU cooler with copper heat pipes and a fan, the RAM modules this
/// PC really has in the slots it really has, and a triple-fan graphics card standing in its PCIe slot. Materials are
/// procedurally textured once (brushed aluminium, PCB, anodised metal) and lit by key/fill/rim lights plus glow lights whose
/// colour follows the readings: CPU and GPU heat (temperature sensor, or load when there's none), RAM in use. Fans spin
/// with load. Animates at most 30 fps, only while <see cref="Running"/> and visible; reduced motion keeps it still and only
/// updates colours and levels. Drag to orbit, scroll to zoom, click a part to select it.
/// </summary>
public sealed class VirtualPcScene : Grid
{
    public event Action<string>? PartSelected;

    private readonly Viewport3D _view = new() { ClipToBounds = true, IsHitTestVisible = true };
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 36, NearPlaneDistance = 0.5, FarPlaneDistance = 120 };
    private readonly Canvas _labels = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<Model3D, string> _parts = [];
    private readonly Dictionary<string, (ModelVisual3D anchor, Point3D at, Border label, TextBlock text)> _tags = [];

    // Live-tinted materials and lights (kept unfrozen so they can follow the readings).
    private readonly SolidColorBrush _cpuHeat = new(), _gpuHeat = new(), _ramLed = new(), _pulse = new(), _select = new();
    private readonly PointLight _cpuGlow = new(Colors.Black, new Point3D(-2.3, 1.2, -1.2)) { Range = 6, ConstantAttenuation = 1, LinearAttenuation = 0.35 };
    private readonly PointLight _gpuGlow = new(Colors.Black, new Point3D(-1.3, 0.25, 3.2)) { Range = 6, ConstantAttenuation = 1, LinearAttenuation = 0.3 };
    private readonly PointLight _ramGlow = new(Colors.Black, new Point3D(1.2, 1.9, -2.5)) { Range = 4, ConstantAttenuation = 1, LinearAttenuation = 0.5 };
    private readonly AxisAngleRotation3D _cpuFan = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D[] _gpuFans = [new(new Vector3D(0, 1, 0), 0), new(new Vector3D(0, 1, 0), 40), new(new Vector3D(0, 1, 0), 80)];
    private readonly List<ScaleTransform3D> _ramLevel = [];
    private readonly List<(TranslateTransform3D t, Point3D a, Point3D b, double phase, bool gpu)> _packets = [];
    private readonly TranslateTransform3D _selectAt = new();
    private readonly ScaleTransform3D _selectSize = new(0, 0, 0);
    private readonly int _modules, _slots;

    private PcReading? _target;
    private double _cpu, _gpu, _ram, _cpuHeatT, _gpuHeatT; // eased display values (0..1)
    private bool _gpuKnown;
    private double _yaw = 30, _pitch = 38, _dist = 30, _t;
    private Point? _down;
    private bool _dragged, _running;
    private DateTime _lastInput = DateTime.MinValue;

    public int Triangles { get; private set; }

    /// <param name="modules">Memory modules installed (0 = unknown: two are drawn as an illustration).</param>
    /// <param name="slots">Memory slots on the board (0 = unknown: four).</param>
    /// <param name="detailed">Full detail; false halves the geometry (software rendering).</param>
    public VirtualPcScene(int modules = 0, int slots = 0, bool detailed = true)
    {
        _slots = Math.Clamp(slots > 0 ? slots : 4, 1, 4);
        _modules = Math.Clamp(modules > 0 ? modules : 2, 1, _slots);
        Background = Brushes.Transparent; // whole area takes mouse input for orbiting
        ClipToBounds = true;
        _view.Camera = _cam;
        Children.Add(_view);
        Children.Add(_labels);
        Build(detailed);
        PlaceCamera();
        _tick.Tick += (_, _) => Frame(0.033);
        IsVisibleChanged += (_, _) => UpdateTimer();
        SizeChanged += (_, _) => PlaceLabels();
        MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(this); _dragged = false; CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (!_dragged && (p - d).Length < 4) return;
            _dragged = true;
            _yaw -= (p.X - d.X) * 0.4;
            _pitch = Math.Clamp(_pitch + (p.Y - d.Y) * 0.3, 6, 82);
            _down = p;
            _lastInput = DateTime.UtcNow;
            PlaceCamera();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            if (!_dragged) Pick(e.GetPosition(_view));
            _down = null;
        };
        MouseWheel += (_, e) => { _dist = Math.Clamp(_dist - e.Delta / 120.0 * 1.3, 11, 36); _lastInput = DateTime.UtcNow; PlaceCamera(); e.Handled = true; };
        Cursor = Cursors.Hand;
        ToolTip = "Drag to turn, scroll to zoom, click the CPU, GPU or RAM for details";
    }

    /// <summary>Animate (true) or hold still (false). The page turns this off while hidden or while measuring.</summary>
    public bool Running
    {
        get => _running;
        set { _running = value; UpdateTimer(); }
    }

    public void Show(PcReading r)
    {
        _target = r;
        _gpuKnown = r.GpuPct is not null;
        if (!_tick.IsEnabled) { Snap(); Frame(0); } // still: jump straight to the new values
    }

    public void Select(string? part)
    {
        if (part is null || !_tags.ContainsKey(part)) { _selectSize.ScaleX = _selectSize.ScaleY = _selectSize.ScaleZ = 0; return; }
        var (x, z, sx, sz) = part switch { "cpu" => (-2.3, -2.4, 3.6, 3.6), "ram" => (0.55 + (_slots - 1) * 0.2, -2.5, 0.6 + _slots * 0.4, 5.0), _ => (-1.3, 1.75, 8.8, 2.4) };
        _selectAt.OffsetX = x; _selectAt.OffsetZ = z;
        _selectSize.ScaleX = sx; _selectSize.ScaleY = 1; _selectSize.ScaleZ = sz;
    }

    private void UpdateTimer()
    {
        bool on = _running && IsVisible && !App.ReduceMotion;
        if (on && !_tick.IsEnabled) _tick.Start();
        else if (!on && _tick.IsEnabled) _tick.Stop();
    }

    // ---- procedural textures (drawn once per app run, frozen) ------------------------------------------------------

    private static ImageSource Bitmap(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Brushed aluminium: fine horizontal streaks over a soft gradient.</summary>
    private static readonly Lazy<Brush> Brushed = new(() =>
    {
        var rnd = new Random(7);
        return Frozen(new ImageBrush(Bitmap(256, 256, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x86, 0x8C, 0x95), Color.FromRgb(0x5C, 0x62, 0x6B), 90), null, new Rect(0, 0, 256, 256));
            for (int i = 0; i < 420; i++)
            {
                byte v = (byte)rnd.Next(0x40, 0xC0);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)rnd.Next(18, 60), v, v, v)), rnd.NextDouble() * 1.2 + 0.3);
                double y = rnd.NextDouble() * 256, x = rnd.NextDouble() * 200 - 40;
                dc.DrawLine(pen, new Point(x, y), new Point(x + rnd.Next(60, 300), y));
            }
        })));
    });

    /// <summary>Dark anodised metal with a faint angular accent, for RAM spreaders and the GPU shroud.</summary>
    private static readonly Lazy<Brush> Gunmetal = new(() => Frozen(new ImageBrush(Bitmap(256, 128, dc =>
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2A, 0x2E, 0x36), Color.FromRgb(0x15, 0x18, 0x1E), 70), null, new Rect(0, 0, 256, 128));
        var rnd = new Random(3);
        for (int i = 0; i < 160; i++)
        {
            double y = rnd.NextDouble() * 128;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)), 0.6), new Point(0, y), new Point(256, y));
        }
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(new Point(150, 0), true, true); c.LineTo(new Point(190, 0), true, false); c.LineTo(new Point(120, 128), true, false); c.LineTo(new Point(80, 128), true, false); }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), null, g);
    }))));

    /// <summary>The board seen from above: matte solder mask, copper traces and buses, vias, pads, mounting holes, silkscreen.</summary>
    private static readonly Lazy<Brush> Pcb = new(() =>
    {
        const int W = 1200, H = 1000; // 100 px per world unit: board x -6..6 → 0..1200, z -5..5 → 0..1000
        static Point P(double x, double z) => new((x + 6) * 100, (z + 5) * 100);
        var rnd = new Random(42);
        return Frozen(new ImageBrush(Bitmap(W, H, dc =>
        {
            dc.DrawRectangle(new RadialGradientBrush(Color.FromRgb(0x1B, 0x21, 0x27), Color.FromRgb(0x10, 0x13, 0x17)) { RadiusX = 0.8, RadiusY = 0.8 }, null, new Rect(0, 0, W, H));
            var trace = new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x31, 0x37)), 2.2);
            var traceHi = new Pen(new SolidColorBrush(Color.FromRgb(0x2F, 0x3D, 0x43)), 1.4);
            // Random orthogonal/45° routing.
            for (int i = 0; i < 420; i++)
            {
                var a = new Point(rnd.Next(W), rnd.Next(H));
                var pen = rnd.Next(4) == 0 ? traceHi : trace;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(a, false, false);
                    for (int k = 0; k < 3; k++)
                    {
                        int len = rnd.Next(20, 140);
                        a = rnd.Next(3) switch { 0 => new Point(a.X + len * (rnd.Next(2) * 2 - 1), a.Y), 1 => new Point(a.X, a.Y + len * (rnd.Next(2) * 2 - 1)), _ => new Point(a.X + len * 0.7, a.Y + len * 0.7) };
                        c.LineTo(a, true, false);
                    }
                }
                dc.DrawGeometry(null, pen, g);
            }
            // Memory bus: CPU socket → DIMM slots, and PCIe lanes: CPU → x16 slot.
            var bus = new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x3A, 0x40)), 1.6);
            for (int i = 0; i < 26; i++) { var y = P(0, -3.6 + i * 0.09).Y; dc.DrawLine(bus, new Point(P(-1.0, 0).X, y), new Point(P(0.45, 0).X, y)); }
            for (int i = 0; i < 18; i++) { var x = P(-3.1 + i * 0.09, 0).X; dc.DrawLine(bus, new Point(x, P(0, -1.0).Y), new Point(x, P(0, 0.85).Y)); }
            // Vias and SMD pads.
            var via = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x4C));
            for (int i = 0; i < 900; i++) dc.DrawEllipse(via, null, new Point(rnd.Next(W), rnd.Next(H)), 1.8, 1.8);
            var pad = new SolidColorBrush(Color.FromRgb(0x8C, 0x7A, 0x52));
            for (int i = 0; i < 260; i++) { var p = new Point(rnd.Next(W), rnd.Next(H)); dc.DrawRectangle(pad, null, new Rect(p.X, p.Y, 5, 3)); dc.DrawRectangle(pad, null, new Rect(p.X + 8, p.Y, 5, 3)); }
            // Mounting holes (plated rings).
            var ring = new Pen(new SolidColorBrush(Color.FromRgb(0xB5, 0xB0, 0xA0)), 7);
            foreach (var (x, z) in new[] { (-5.6, -4.6), (-5.6, 0.6), (-5.6, 4.6), (2.6, -4.6), (2.6, 0.6), (2.6, 4.6), (5.6, -4.6), (5.6, 4.6) })
                dc.DrawEllipse(Brushes.Black, ring, P(x, z), 11, 11);
            // Silkscreen.
            var silk = new SolidColorBrush(Color.FromArgb(0xB0, 0xE6, 0xE8, 0xEA));
            var silkPen = new Pen(silk, 1.6);
            var face = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            void Text(string s, double x, double z, double size = 15)
                => dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, silk, 1.0), P(x, z));
            dc.DrawRectangle(null, silkPen, new Rect(P(-3.75, -3.85), P(-0.85, -0.95)));
            Text("CPU1", -3.7, -0.9); Text("CPU_FAN", -1.4, -4.65, 13); Text("PCIE_X16_1", -4.3, 0.55); Text("PCIE_X16_2", -4.3, 3.25);
            Text("M2_1", -0.9, 0.0, 13); Text("ATX_24P", 5.0, -3.9, 13); Text("SATA6G", 5.0, 4.6, 13); Text("CHA_FAN1", 4.2, 4.7, 13); Text("BIOS", 3.0, 0.6, 13);
            for (int i = 0; i < 4; i++) Text(new[] { "A1", "A2", "B1", "B2" }[i], 0.47 + i * 0.4, -4.95, 12);
            dc.DrawRectangle(null, silkPen, new Rect(P(2.75, 1.95), P(4.45, 3.65)));
            Text("REV 1.02", 3.2, 4.6, 12);
        })));
    });

    /// <summary>Soft round contact shadow (black, transparent at the edge).</summary>
    private static readonly Lazy<Brush> Shadow = new(() => Frozen(new RadialGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(0xB0, 0, 0, 0), 0), new(Color.FromArgb(0x60, 0, 0, 0), 0.55), new(Color.FromArgb(0, 0, 0, 0), 1) })));

    // ---- materials ------------------------------------------------------------------------------------------------

    private static Material Mat(Brush diffuse, double specular = 0, double power = 30)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(diffuse));
        if (specular > 0) g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb((byte)(specular * 255), 255, 255, 255)), power));
        g.Freeze();
        return g;
    }

    private static Material Mat(Color c, double specular = 0, double power = 30) => Mat(new SolidColorBrush(c), specular, power);

    private static Material Glow(SolidColorBrush b, Brush baseBrush, double specular = 0)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(baseBrush));
        if (specular > 0) g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb((byte)(specular * 255), 255, 255, 255)), 40));
        g.Children.Add(new EmissiveMaterial(b));
        return g;
    }

    // ---- meshes ---------------------------------------------------------------------------------------------------

    private GeometryModel3D Add(Model3DGroup g, MeshGeometry3D m, Material mat, string? part = null, Transform3D? t = null, Material? back = null)
    {
        m.Freeze();
        var model = new GeometryModel3D(m, mat) { BackMaterial = back, Transform = t };
        g.Children.Add(model);
        if (part is not null) _parts[model] = part;
        Triangles += m.TriangleIndices.Count / 3;
        return model;
    }

    /// <summary>Box with its bottom face at y.</summary>
    private static MeshGeometry3D Box(double x, double y, double z, double sx, double sy, double sz) => PotatoScene.Box(new Point3D(x, y + sy / 2, z), sx, sy, sz);

    /// <summary>Cylinder along Y from the base centre, with caps.</summary>
    private static MeshGeometry3D Cyl(Point3D b, double r, double h, int seg, bool caps = true)
    {
        var m = new MeshGeometry3D();
        for (int i = 0; i <= seg; i++)
        {
            double a = 2 * Math.PI * i / seg, x = Math.Cos(a), z = Math.Sin(a);
            m.Positions.Add(new Point3D(b.X + x * r, b.Y, b.Z + z * r)); m.Normals.Add(new Vector3D(x, 0, z)); m.TextureCoordinates.Add(new Point(i / (double)seg, 1));
            m.Positions.Add(new Point3D(b.X + x * r, b.Y + h, b.Z + z * r)); m.Normals.Add(new Vector3D(x, 0, z)); m.TextureCoordinates.Add(new Point(i / (double)seg, 0));
        }
        for (int i = 0; i < seg; i++)
        {
            int k = i * 2;
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 1); m.TriangleIndices.Add(k + 2);
            m.TriangleIndices.Add(k + 2); m.TriangleIndices.Add(k + 1); m.TriangleIndices.Add(k + 3);
        }
        if (caps)
            foreach (var (y, up) in new[] { (b.Y + h, true), (b.Y, false) })
            {
                int c = m.Positions.Count;
                m.Positions.Add(new Point3D(b.X, y, b.Z)); m.Normals.Add(new Vector3D(0, up ? 1 : -1, 0)); m.TextureCoordinates.Add(new Point(0.5, 0.5));
                for (int i = 0; i <= seg; i++)
                {
                    double a = 2 * Math.PI * i / seg;
                    m.Positions.Add(new Point3D(b.X + Math.Cos(a) * r, y, b.Z + Math.Sin(a) * r)); m.Normals.Add(new Vector3D(0, up ? 1 : -1, 0));
                    m.TextureCoordinates.Add(new Point(0.5 + Math.Cos(a) / 2, 0.5 + Math.Sin(a) / 2));
                }
                for (int i = 1; i <= seg; i++)
                {
                    if (up) { m.TriangleIndices.Add(c); m.TriangleIndices.Add(c + i + 1); m.TriangleIndices.Add(c + i); }
                    else { m.TriangleIndices.Add(c); m.TriangleIndices.Add(c + i); m.TriangleIndices.Add(c + i + 1); }
                }
            }
        return m;
    }

    /// <summary>Fan rotor around the Y axis (centred at the origin): curved, pitched blades.</summary>
    private static MeshGeometry3D Rotor(double r, int blades)
    {
        var m = new MeshGeometry3D();
        const int n = 7;
        for (int b = 0; b < blades; b++)
        {
            double a0 = 2 * Math.PI * b / blades;
            int start = m.Positions.Count;
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n, rad = r * (0.3 + 0.66 * t), sweep = a0 + t * 0.55, w = (2 * Math.PI / blades) * (0.62 - 0.12 * t);
                m.Positions.Add(new Point3D(Math.Cos(sweep) * rad, 0.05, Math.Sin(sweep) * rad));
                m.Positions.Add(new Point3D(Math.Cos(sweep + w) * rad, -0.05, Math.Sin(sweep + w) * rad));
                var nrm = new Vector3D(0.25 * Math.Cos(sweep + w / 2), 1, 0.25 * Math.Sin(sweep + w / 2)); nrm.Normalize();
                m.Normals.Add(nrm); m.Normals.Add(nrm);
                m.TextureCoordinates.Add(new Point(t, 0)); m.TextureCoordinates.Add(new Point(t, 1));
            }
            for (int i = 0; i < n; i++)
            {
                int k = start + i * 2;
                m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 2); m.TriangleIndices.Add(k + 1);
                m.TriangleIndices.Add(k + 1); m.TriangleIndices.Add(k + 2); m.TriangleIndices.Add(k + 3);
            }
        }
        return m;
    }

    /// <summary>Flat quad lying on y (contact shadows, selection plate), UV 0..1.</summary>
    private static MeshGeometry3D Quad(double x, double y, double z, double sx, double sz)
    {
        var m = new MeshGeometry3D();
        foreach (var (dx, dz, u, v) in new[] { (-1, -1, 0, 0), (1, -1, 1, 0), (1, 1, 1, 1), (-1, 1, 0, 1) })
        {
            m.Positions.Add(new Point3D(x + dx * sx / 2, y, z + dz * sz / 2)); m.Normals.Add(new Vector3D(0, 1, 0)); m.TextureCoordinates.Add(new Point(u, v));
        }
        m.TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 };
        return m;
    }

    /// <summary>Board top as a grid, so the glow lights can light it locally (WPF lights per vertex).</summary>
    private static MeshGeometry3D BoardTop(double w, double d, int nx, int nz)
    {
        var m = new MeshGeometry3D();
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                m.Positions.Add(new Point3D(-w / 2 + w * i / nx, 0, -d / 2 + d * j / nz));
                m.Normals.Add(new Vector3D(0, 1, 0));
                m.TextureCoordinates.Add(new Point(i / (double)nx, j / (double)nz));
            }
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + nx + 1;
                m.TriangleIndices.Add(a); m.TriangleIndices.Add(b); m.TriangleIndices.Add(a + 1);
                m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b); m.TriangleIndices.Add(b + 1);
            }
        return m;
    }

    private static Transform3D Place(double x, double y, double z, double rotX = 0, double rotZ = 0, Rotation3D? spin = null)
    {
        var t = new Transform3DGroup();
        if (spin is not null) t.Children.Add(new RotateTransform3D(spin));
        if (rotX != 0) t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rotX)));
        if (rotZ != 0) t.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rotZ)));
        t.Children.Add(new TranslateTransform3D(x, y, z));
        return t;
    }

    // ---- scene ----------------------------------------------------------------------------------------------------

    private void Build(bool detailed)
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x2A, 0x2E, 0x38)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xE8, 0xE4, 0xDC), new Vector3D(-0.45, -1, -0.55)));  // key, warm
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x38, 0x46, 0x68), new Vector3D(0.8, -0.35, 0.6)));  // fill, cool
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x58, 0x70), new Vector3D(0.2, -0.3, 1)));     // rim
        root.Children.Add(_cpuGlow);
        root.Children.Add(_gpuGlow);
        root.Children.Add(_ramGlow);

        var brushed = Mat(Brushed.Value, 0.45, 60);
        var gun = Mat(Gunmetal.Value, 0.35, 30);
        var black = Mat(Color.FromRgb(0x14, 0x16, 0x1A), 0.25, 20);
        var plastic = Mat(Color.FromRgb(0x22, 0x25, 0x2B), 0.2, 15);
        var copper = Mat(Color.FromRgb(0xC2, 0x6F, 0x3E), 0.8, 50);
        var silver = Mat(Color.FromRgb(0xC9, 0xCC, 0xD1), 0.8, 70);
        var hole = Mat(Color.FromRgb(0x05, 0x06, 0x08));
        var pcbGreen = Mat(Color.FromRgb(0x11, 0x3A, 0x2A), 0.3);
        var gold = Mat(Color.FromRgb(0xC9, 0xA2, 0x4A), 0.7, 40);

        // Motherboard: textured top (tessellated), dark edges.
        Add(root, Box(0, -0.16, 0, 12, 0.15, 10), Mat(Color.FromRgb(0x0E, 0x10, 0x13)));
        Add(root, BoardTop(12, 10, detailed ? 48 : 12, detailed ? 40 : 10), Mat(Pcb.Value, 0.12, 12));

        // Contact shadows (drawn before the parts that cast them).
        var shadow = Mat(Shadow.Value);
        Add(root, Quad(-2.3, 0.004, -2.4, 4.4, 3.6), shadow);
        Add(root, Quad(-1.3, 0.004, 1.9, 10.0, 3.2), shadow);
        Add(root, Quad(1.2, 0.004, -2.5, 2.6, 5.4), shadow);
        Add(root, Quad(-5.4, 0.004, -2.8, 1.8, 5.2), shadow);

        // Selection plate (scaled to the selected part).
        var plate = new Transform3DGroup();
        plate.Children.Add(_selectSize);
        plate.Children.Add(_selectAt);
        _select.Color = TryFindResource("Accent") is SolidColorBrush acc ? Color.FromArgb(0x55, acc.Color.R, acc.Color.G, acc.Color.B) : Color.FromArgb(0x55, 0x4C, 0x8D, 0xFF);
        Add(root, Quad(0, 0.008, 0, 1, 1), Glow(_select, Brushes.Transparent), null, plate);

        // Rear I/O shroud and VRM heatsinks.
        Add(root, Box(-5.45, 0, -2.8, 1.0, 1.05, 4.4), gun);
        Add(root, Box(-5.45, 1.05, -2.8, 1.0, 0.03, 4.4), brushed);
        Add(root, Box(-4.45, 0, -2.9, 0.75, 0.75, 3.4), brushed);
        Add(root, Box(-2.6, 0, -4.55, 3.1, 0.65, 0.7), brushed);
        for (int i = 0; i < 6; i++) Add(root, Box(-4.45, 0.75, -4.4 + i * 0.6, 0.76, 0.02, 0.18), black); // fin grooves
        // Solid capacitors and chokes along the VRM.
        int caps = detailed ? 8 : 4;
        for (int i = 0; i < caps; i++)
        {
            Add(root, Cyl(new Point3D(-3.85, 0, -4.0 + i * 0.36), 0.12, 0.34, detailed ? 14 : 8), silver);
            Add(root, Cyl(new Point3D(-3.85, 0.34, -4.0 + i * 0.36), 0.08, 0.005, 8), Mat(Color.FromRgb(0x50, 0x55, 0x60)));
        }
        for (int i = 0; i < caps; i++) Add(root, Box(-3.4 + i * 0.32, 0, -3.95, 0.24, 0.2, 0.24), Mat(Color.FromRgb(0x2B, 0x2D, 0x31), 0.4));

        // CPU socket, retention frame, substrate.
        Add(root, Box(-2.3, 0, -2.4, 2.6, 0.1, 2.6), plastic, "cpu");
        Add(root, Box(-2.3, 0.1, -2.4, 2.2, 0.06, 2.2), silver, "cpu");
        Add(root, Box(-2.3, 0.16, -2.4, 1.7, 0.08, 1.7), Mat(Color.FromRgb(0x18, 0x4A, 0x32)), "cpu");

        // Tower cooler: copper base, four heat pipes, aluminium fin stack (tinted by heat), top cap, 120 mm fan.
        Add(root, Box(-2.3, 0.24, -2.4, 1.5, 0.22, 1.5), copper, "cpu");
        foreach (var dx in new[] { -0.45, -0.15, 0.15, 0.45 })
        {
            Add(root, Cyl(new Point3D(-2.3 + dx, 0.46, -2.4), 0.09, 3.05, detailed ? 12 : 6), copper, "cpu");
            Add(root, Cyl(new Point3D(-2.3 + dx, 3.51, -2.4), 0.09, 0.06, detailed ? 12 : 6), copper, "cpu");
        }
        var fin = Glow(_cpuHeat, Brushed.Value, 0.3);
        int fins = detailed ? 32 : 14;
        for (int i = 0; i < fins; i++) Add(root, Box(-2.3, 1.0 + i * (2.4 / fins), -2.4, 2.4, 0.028, 1.7), fin, "cpu");
        Add(root, Box(-2.3, 3.45, -2.4, 2.5, 0.09, 1.75), gun, "cpu");
        // Fan on the RAM side, blowing toward the rear I/O: frame, dark opening, rotor, hub.
        const double fanX = -0.88;
        foreach (var (y, z, h, d) in new[] { (1.05, -2.4, 0.16, 1.9), (3.25, -2.4, 0.16, 1.9), (1.13, -3.27, 2.28, 0.16), (1.13, -1.53, 2.28, 0.16) })
            Add(root, Box(fanX, y, z, 0.28, h, d), plastic, "cpu");
        Add(root, Cyl(new Point3D(0, -0.13, 0), 0.86, 0.26, detailed ? 28 : 12, caps: false), plastic, "cpu", Place(fanX, 2.25, -2.4, rotZ: 90), plastic);
        Add(root, Cyl(new Point3D(0, -0.11, 0), 0.84, 0.01, 20), hole, "cpu", Place(fanX - 0.12, 2.25, -2.4, rotZ: 90));
        Add(root, Rotor(0.82, 9), Mat(Color.FromRgb(0x2E, 0x32, 0x3A), 0.35), "cpu", Place(fanX + 0.02, 2.25, -2.4, rotZ: -90, spin: _cpuFan), Mat(Color.FromRgb(0x2E, 0x32, 0x3A)));
        Add(root, Cyl(new Point3D(0, -0.02, 0), 0.26, 0.1, 18), Mat(Color.FromRgb(0x30, 0x34, 0x3C), 0.4), "cpu", Place(fanX + 0.08, 2.25, -2.4, rotZ: -90, spin: _cpuFan));

        // Memory: the slots this board has, with this PC's number of modules in the usual population order.
        int[] filled = (_slots, _modules) switch
        {
            (4, 1) => [1], (4, 2) => [1, 3], (4, 3) => [0, 1, 3], (4, _) => [0, 1, 2, 3],
            (3, 1) => [1], (3, 2) => [0, 2], (2, 1) => [0], _ => Enumerable.Range(0, _modules).ToArray(),
        };
        var led = Glow(_ramLed, new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x26)));
        for (int i = 0; i < _slots; i++)
        {
            double x = 0.55 + i * 0.4;
            Add(root, Box(x, 0, -2.5, 0.24, 0.2, 4.6), black, "ram");
            Add(root, Box(x, 0, -4.85, 0.26, 0.32, 0.18), plastic, "ram"); // latches
            Add(root, Box(x, 0, -0.15, 0.26, 0.32, 0.18), plastic, "ram");
            if (!filled.Contains(i)) continue;
            Add(root, Box(x, 0.12, -2.5, 0.05, 1.35, 4.2), pcbGreen, "ram");
            Add(root, Box(x, 0.12, -2.5, 0.06, 0.08, 4.2), gold, "ram");
            Add(root, Box(x, 0.2, -2.5, 0.16, 1.3, 4.25), gun, "ram");                                         // heat spreader
            Add(root, Box(x, 1.5, -2.5, 0.12, 0.12, 4.15), Mat(Color.FromRgb(0x1A, 0x1C, 0x20), 0.3), "ram"); // diffuser housing
            // Light bar: its length shows memory in use.
            var level = new ScaleTransform3D(1, 1, 0.01, x, 1.62, -4.575);
            _ramLevel.Add(level);
            Add(root, Box(x, 1.62, -2.5, 0.1, 0.07, 4.15), led, "ram", level);
        }

        // M.2 SSD with heatsink, chipset heatsink, power connectors, SATA, second PCIe slot, BIOS chip.
        Add(root, Box(-1.6, 0, 0.35, 2.4, 0.06, 0.55), pcbGreen);
        Add(root, Box(-1.6, 0.06, 0.35, 2.3, 0.14, 0.6), brushed);
        Add(root, Box(3.6, 0, 2.8, 1.6, 0.36, 1.6), brushed);
        Add(root, Box(3.6, 0.36, 2.8, 1.0, 0.02, 1.0), gun);
        Add(root, Box(5.65, 0, -2.4, 0.4, 0.55, 2.4), black);
        Add(root, Box(5.65, 0.55, -2.4, 0.3, 0.005, 2.3), Mat(Color.FromRgb(0x05, 0x05, 0x06)));
        Add(root, Box(-3.0, 0, -4.75, 0.8, 0.45, 0.35), black);                                                // 8-pin CPU power
        for (int i = 0; i < 4; i++) Add(root, Box(5.65, 0, 3.0 + i * 0.42, 0.45, 0.32, 0.3), Mat(Color.FromRgb(0x1C, 0x2A, 0x3A), 0.3));
        Add(root, Box(-1.2, 0, 3.7, 6.4, 0.2, 0.26), black);
        Add(root, Box(3.2, 0, 0.9, 0.5, 0.08, 0.5), black);

        // Graphics card standing in the x16 slot: PCB, backplate, shroud with three fans, fin edge, bracket, power plug.
        Add(root, Box(-1.2, 0, 1.0, 6.4, 0.22, 0.28), black, "gpu");
        const double gx = -1.3, z0 = 1.08, z1 = 2.45, top = 2.75;
        Add(root, Box(gx, 0.14, 0.92, 8.2, top - 0.2, 0.06), Mat(Brushed.Value, 0.5, 40), "gpu");                // backplate
        Add(root, Box(gx, 0.1, z0 - 0.06, 8.2, top - 0.1, 0.06), pcbGreen, "gpu");
        Add(root, Box(gx + 0.6, 0.0, z0 - 0.06, 3.0, 0.14, 0.07), gold, "gpu");                                 // edge connector
        int finsN = detailed ? 40 : 16;
        for (int i = 0; i < finsN; i++) Add(root, Box(gx - 3.9 + i * (7.8 / finsN), top - 0.35, (z0 + z1) / 2, 0.025, 0.32, z1 - z0 - 0.12), brushed, "gpu");
        Add(root, Box(gx, 0.25, (z0 + z1) / 2, 8.3, top - 0.6, z1 - z0), gun, "gpu");                           // shroud body
        Add(root, Box(gx, top - 0.04, (z0 + z1) / 2, 8.3, 0.04, z1 - z0), gun, "gpu");                          // shroud top rail
        Add(root, Box(gx, 0.28, z1, 8.0, 0.05, 0.03), Glow(_gpuHeat, Brushes.Black), "gpu");                 // light bar, lower edge
        Add(root, Box(gx - 4.22, 0.0, (z0 + z1) / 2 - 0.1, 0.06, top + 0.2, z1 - z0 + 0.3), Mat(Brushed.Value, 0.8, 70), "gpu"); // bracket
        Add(root, Box(gx + 2.6, top, (z0 + z1) / 2, 0.9, 0.25, 0.45), black, "gpu");                            // 12V power plug
        for (int f = 0; f < 3; f++)
        {
            double fx = gx - 2.6 + f * 2.6, fy = 1.42;
            Add(root, Cyl(new Point3D(0, 0, 0), 1.08, 0.08, detailed ? 32 : 12, caps: false), Mat(Color.FromRgb(0x3A, 0x3E, 0x46), 0.5), "gpu", Place(fx, fy, z1, rotX: 90), plastic);
            Add(root, Cyl(new Point3D(0, 0, 0), 1.06, 0.01, 24), hole, "gpu", Place(fx, fy, z1 + 0.005, rotX: 90));
            Add(root, Rotor(1.0, 11), Mat(Color.FromRgb(0x26, 0x29, 0x30), 0.35), "gpu", Place(fx, fy, z1 + 0.09, rotX: 90, spin: _gpuFans[f]), Mat(Color.FromRgb(0x26, 0x29, 0x30)));
            Add(root, Cyl(new Point3D(0, 0, 0), 0.3, 0.1, 20), Mat(Color.FromRgb(0x30, 0x33, 0x3A), 0.5), "gpu", Place(fx, fy, z1 + 0.08, rotX: 90, spin: _gpuFans[f]));
        }

        // Data pulses along the memory bus and PCIe lanes.
        var pulse = Glow(_pulse, Brushes.Black);
        var pulseMesh = Box(0, 0, 0, 0.14, 0.03, 0.05);
        for (int i = 0; i < 8; i++)
        {
            bool toGpu = i % 2 == 1;
            var a = toGpu ? new Point3D(-2.9 + (i % 4) * 0.18, 0.01, -1.0) : new Point3D(-1.0, 0.01, -3.4 + (i % 4) * 0.2);
            var b = toGpu ? new Point3D(a.X, 0.01, 0.85) : new Point3D(0.45, 0.01, a.Z);
            var t = new TranslateTransform3D(a.X, a.Y, a.Z);
            _packets.Add((t, a, b, i / 8.0, toGpu));
            Add(root, pulseMesh, pulse, null, t);
        }

        var holder = new ModelVisual3D { Content = root };
        _view.Children.Add(holder);
        AddLabel("cpu", holder, new Point3D(-2.3, 3.75, -2.4), "CPU");
        AddLabel("ram", holder, new Point3D(0.55 + (_slots - 1) * 0.2, 1.9, -2.5), "RAM");
        AddLabel("gpu", holder, new Point3D(-1.3, 3.0, 1.75), "GPU");
    }

    private void AddLabel(string id, ModelVisual3D anchor, Point3D at, string name)
    {
        var text = new TextBlock { Text = name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        var label = new Border
        {
            Child = text, Padding = new Thickness(7, 2, 7, 3), CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0E, 0x14)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        _labels.Children.Add(label);
        _tags[id] = (anchor, at, label, text);
    }

    private void PlaceCamera()
    {
        double y = _yaw * Math.PI / 180, p = _pitch * Math.PI / 180;
        var look = new Point3D(-0.5, 1.1, -0.9);
        var pos = new Point3D(look.X + _dist * Math.Cos(p) * Math.Sin(y), look.Y + _dist * Math.Sin(p), look.Z + _dist * Math.Cos(p) * Math.Cos(y));
        _cam.Position = pos;
        _cam.LookDirection = look - pos;
        _cam.UpDirection = new Vector3D(0, 1, 0);
        PlaceLabels();
    }

    private void PlaceLabels()
    {
        if (ActualWidth <= 0) return;
        foreach (var (anchor, at, label, _) in _tags.Values)
        {
            if (anchor.TransformToAncestor(_view) is not { } tr || !tr.TryTransform(at, out var pt)) { label.Visibility = Visibility.Hidden; continue; }
            label.Visibility = Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, pt.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, pt.Y - label.DesiredSize.Height - 4);
        }
    }

    private void Pick(Point p)
    {
        string? hit = null;
        VisualTreeHelper.HitTest(_view, null, r =>
        {
            if (r is RayMeshGeometry3DHitTestResult m && _parts.TryGetValue(m.ModelHit, out var part)) { hit = part; return HitTestResultBehavior.Stop; }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        Select(hit);
        if (hit is not null) PartSelected?.Invoke(hit);
    }

    // ---- animation ------------------------------------------------------------------------------------------------

    private static double Heat(double? temp, double load, double coolC, double hotC) =>
        temp is { } t ? Math.Clamp((t - coolC) / (hotC - coolC), 0, 1) : Math.Clamp(load, 0, 1) * 0.85;

    private (double cpu, double gpu, double ram, double cpuHeat, double gpuHeat) Targets()
    {
        if (_target is not { } r) return (0, 0, 0, 0, 0);
        double cpu = r.CpuPct / 100, gpu = (r.GpuPct ?? 0) / 100, ram = r.RamTotalGb > 0 ? r.RamUsedGb / r.RamTotalGb : 0;
        return (cpu, gpu, ram, Heat(r.CpuTempC, cpu, 35, 90), Heat(r.GpuTempC, gpu, 35, 85));
    }

    private void Snap() => (_cpu, _gpu, _ram, _cpuHeatT, _gpuHeatT) = Targets();

    /// <summary>Blue (cool) → amber → red (hot).</summary>
    private static Color HeatColor(double h)
    {
        h = Math.Clamp(h, 0, 1);
        Color a = Color.FromRgb(0x2A, 0x7B, 0xFF), b = Color.FromRgb(0xFF, 0xA6, 0x2B), c = Color.FromRgb(0xFF, 0x3B, 0x4E);
        var (from, to, k) = h < 0.55 ? (a, b, h / 0.55) : (b, c, (h - 0.55) / 0.45);
        byte L(byte x, byte y) => (byte)(x + (y - x) * k);
        return Color.FromRgb(L(from.R, to.R), L(from.G, to.G), L(from.B, to.B));
    }

    private static Color Scale(Color c, double k) => Color.FromRgb((byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));

    private void Frame(double dt)
    {
        var (tc, tg, tr, tch, tgh) = Targets();
        double k = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 3); // ease toward the latest reading
        _cpu += (tc - _cpu) * k; _gpu += (tg - _gpu) * k; _ram += (tr - _ram) * k; _cpuHeatT += (tch - _cpuHeatT) * k; _gpuHeatT += (tgh - _gpuHeatT) * k;
        _t += dt;

        // Heat: a subtle tint on the fins and a coloured glow light on the board around the part.
        var cpuC = HeatColor(_cpuHeatT);
        _cpuHeat.Color = Scale(cpuC, 0.12 + 0.3 * _cpuHeatT);
        _cpuGlow.Color = Scale(cpuC, 0.35 + 0.65 * _cpuHeatT);
        var gpuC = _gpuKnown ? HeatColor(_gpuHeatT) : Color.FromRgb(0x40, 0x44, 0x4A);
        _gpuHeat.Color = Scale(gpuC, _gpuKnown ? 0.7 + 0.3 * _gpuHeatT : 0.5);
        _gpuGlow.Color = Scale(gpuC, _gpuKnown ? 0.35 + 0.65 * _gpuHeatT : 0.15);
        var ramC = _ram > 0.85 ? Color.FromRgb(0xFF, 0x5C, 0x7A) : _ram > 0.7 ? Color.FromRgb(0xF5, 0xB8, 0x4A) : Color.FromRgb(0x3D, 0xDC, 0x97);
        _ramLed.Color = ramC;
        _ramGlow.Color = Scale(ramC, 0.5);
        foreach (var f in _ramLevel) f.ScaleZ = Math.Max(0.01, _ram);

        // Fans: the CPU fan idles slowly and speeds up with CPU load; GPU fans with GPU load, still when there's no reading.
        if (dt > 0)
        {
            _cpuFan.Angle = (_cpuFan.Angle + dt * (160 + 900 * _cpu)) % 360;
            if (_gpuKnown) foreach (var r in _gpuFans) r.Angle = (r.Angle + dt * (60 + 1100 * _gpu)) % 360;
        }

        double glow = 0.45 + 0.55 * _cpu;
        _pulse.Color = Color.FromRgb((byte)(0x6C * glow), (byte)(0xC8 * glow), (byte)(0xFF * glow));
        foreach (var (t, a, b, phase, toGpu) in _packets)
        {
            var s = ((phase + _t * (toGpu ? 0.15 + 0.9 * _gpu : 0.15 + 0.9 * _cpu)) % 1 + 1) % 1;
            t.OffsetX = a.X + (b.X - a.X) * s; t.OffsetY = a.Y; t.OffsetZ = a.Z + (b.Z - a.Z) * s;
        }

        // Slow turntable when nobody has touched it for a while.
        if (dt > 0 && DateTime.UtcNow - _lastInput > TimeSpan.FromSeconds(6)) { _yaw += dt * 3; PlaceCamera(); }

        if (_target is { } rd)
        {
            SetTag("cpu", $"CPU {rd.CpuPct:0}%" + (rd.CpuTempC is { } ct ? $" · {ct:0}°C" : ""));
            SetTag("gpu", rd.GpuPct is { } g ? $"GPU {g:0}%" + (rd.GpuTempC is { } gt ? $" · {gt:0}°C" : "") : "GPU · no reading");
            SetTag("ram", $"RAM {rd.RamUsedGb:0.0}/{rd.RamTotalGb:0} GB");
        }
    }

    private void SetTag(string id, string text)
    {
        if (_tags.TryGetValue(id, out var tag) && tag.text.Text != text) { tag.text.Text = text; PlaceLabels(); }
    }
}
