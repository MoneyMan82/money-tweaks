using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MoneyTweaks.Controls;

/// <summary>
/// Readings that drive the virtual PC. Null means Windows gave no reading: that part is drawn idle and grey, never
/// with an invented value.
/// </summary>
public sealed record PcReading(double CpuPct, double? CpuTempC, double? GpuPct, double? GpuTempC, double RamUsedGb, double RamTotalGb);

/// <summary>
/// Reality Lab's 3D virtual PC: a motherboard with a CPU and cooler, four RAM sticks and a graphics card, drawn by WPF
/// (about 1,500 triangles). Heat colour follows the CPU/GPU temperature (or load when there's no sensor), the GPU fans
/// spin with GPU load, the RAM sticks fill with memory in use, and data pulses run along the traces faster with CPU load.
/// Animates at most 30 fps, only while <see cref="Running"/> and visible; reduced motion keeps it still and only
/// updates the colours and levels. Drag to orbit, scroll to zoom, click a part to select it.
/// </summary>
public sealed class VirtualPcScene : Grid
{
    public event Action<string>? PartSelected;

    private readonly Viewport3D _view = new() { ClipToBounds = true, IsHitTestVisible = true };
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 38, NearPlaneDistance = 0.5, FarPlaneDistance = 100 };
    private readonly Canvas _labels = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<Model3D, string> _parts = [];
    private readonly Dictionary<string, (ModelVisual3D anchor, Point3D at, Border label, TextBlock text)> _tags = [];

    // Live-tinted materials (brushes stay unfrozen so their colour can follow the readings).
    private readonly SolidColorBrush _cpuHeat = new(), _gpuHeat = new(), _ramLed = new(), _pulse = new(), _select = new();
    private readonly AxisAngleRotation3D[] _fanSpin = [new(new Vector3D(0, 1, 0), 0), new(new Vector3D(0, 1, 0), 0)];
    private readonly ScaleTransform3D[] _ramFill = new ScaleTransform3D[4];
    private readonly List<(TranslateTransform3D t, Point3D a, Point3D b, double phase, bool gpu)> _packets = [];
    private readonly TranslateTransform3D _selectAt = new();
    private readonly ScaleTransform3D _selectSize = new(0, 0, 0);

    private PcReading? _target;
    private double _cpu, _gpu, _ram, _cpuHeatT, _gpuHeatT; // eased display values (0..1)
    private bool _gpuKnown;
    private double _yaw = -32, _pitch = 38, _dist = 22, _t;
    private Point? _down;
    private bool _dragged, _running;
    private DateTime _lastInput = DateTime.MinValue;
    private string? _selected;

    public int Triangles { get; private set; }

    public VirtualPcScene()
    {
        Background = Brushes.Transparent; // whole area takes mouse input for orbiting
        ClipToBounds = true;
        _view.Camera = _cam;
        Children.Add(_view);
        Children.Add(_labels);
        Build();
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
            _pitch = Math.Clamp(_pitch + (p.Y - d.Y) * 0.3, 8, 80);
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
        MouseWheel += (_, e) => { _dist = Math.Clamp(_dist - e.Delta / 120.0 * 1.2, 11, 32); _lastInput = DateTime.UtcNow; PlaceCamera(); e.Handled = true; };
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
        _selected = part;
        if (part is null || !_tags.TryGetValue(part, out var tag)) { _selectSize.ScaleX = _selectSize.ScaleY = _selectSize.ScaleZ = 0; return; }
        var (sx, sz) = part switch { "cpu" => (3.4, 3.4), "ram" => (3.0, 4.0), _ => (8.2, 3.0) };
        _selectAt.OffsetX = tag.at.X; _selectAt.OffsetZ = tag.at.Z;
        _selectSize.ScaleX = sx; _selectSize.ScaleY = 1; _selectSize.ScaleZ = sz;
    }

    private void UpdateTimer()
    {
        bool on = _running && IsVisible && !App.ReduceMotion;
        if (on && !_tick.IsEnabled) _tick.Start();
        else if (!on && _tick.IsEnabled) _tick.Stop();
    }

    // ---- scene ----------------------------------------------------------------------------------------------------

    private static Material Mat(Color c, double spec = 0)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(c)));
        if (spec > 0) g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb((byte)(spec * 255), 255, 255, 255)), 40));
        g.Freeze();
        return g;
    }

    private static Material Glow(SolidColorBrush b, Color baseColor)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(baseColor)));
        g.Children.Add(new EmissiveMaterial(b));
        return g;
    }

    private GeometryModel3D Add(Model3DGroup g, MeshGeometry3D m, Material mat, string? part = null, Transform3D? t = null)
    {
        m.Freeze();
        var model = new GeometryModel3D(m, mat) { BackMaterial = mat, Transform = t };
        g.Children.Add(model);
        if (part is not null) _parts[model] = part;
        Triangles += m.TriangleIndices.Count / 3;
        return model;
    }

    private static MeshGeometry3D Box(double x, double y, double z, double sx, double sy, double sz) => PotatoScene.Box(new Point3D(x, y + sy / 2, z), sx, sy, sz);

    /// <summary>Flat disc (fan blade hub) lying in the XZ plane.</summary>
    private static MeshGeometry3D Disc(double r, int seg, double y)
    {
        var m = new MeshGeometry3D();
        m.Positions.Add(new Point3D(0, y, 0)); m.Normals.Add(new Vector3D(0, 1, 0));
        for (int i = 0; i <= seg; i++)
        {
            double a = 2 * Math.PI * i / seg;
            m.Positions.Add(new Point3D(Math.Cos(a) * r, y, Math.Sin(a) * r)); m.Normals.Add(new Vector3D(0, 1, 0));
        }
        for (int i = 1; i <= seg; i++) { m.TriangleIndices.Add(0); m.TriangleIndices.Add(i + 1); m.TriangleIndices.Add(i); }
        return m;
    }

    /// <summary>Fan rotor: a hub and angled blades, centred on the origin so it can spin in place.</summary>
    private static MeshGeometry3D Rotor(double r)
    {
        var m = Disc(r * 0.3, 16, 0.02);
        for (int b = 0; b < 7; b++)
        {
            double a0 = 2 * Math.PI * b / 7, a1 = a0 + 0.5;
            int k = m.Positions.Count;
            m.Positions.Add(new Point3D(Math.Cos(a0) * r * 0.28, 0.01, Math.Sin(a0) * r * 0.28));
            m.Positions.Add(new Point3D(Math.Cos(a0 + 0.25) * r, 0.03, Math.Sin(a0 + 0.25) * r));
            m.Positions.Add(new Point3D(Math.Cos(a1 + 0.25) * r, 0.03, Math.Sin(a1 + 0.25) * r));
            m.Positions.Add(new Point3D(Math.Cos(a1) * r * 0.28, 0.01, Math.Sin(a1) * r * 0.28));
            for (int i = 0; i < 4; i++) m.Normals.Add(new Vector3D(0, 1, 0));
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 2); m.TriangleIndices.Add(k + 1);
            m.TriangleIndices.Add(k); m.TriangleIndices.Add(k + 3); m.TriangleIndices.Add(k + 2);
        }
        return m;
    }

    private void Build()
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x3A, 0x40, 0x50)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xD8, 0xDC, 0xE8), new Vector3D(-0.5, -1, -0.6)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x40, 0x48, 0x70), new Vector3D(0.7, -0.4, 0.8)));

        var board = Mat(Color.FromRgb(0x14, 0x1A, 0x22), 0.15);
        var dark = Mat(Color.FromRgb(0x0C, 0x0F, 0x14), 0.2);
        var metal = Mat(Color.FromRgb(0x8A, 0x92, 0xA0), 0.6);
        var silver = Mat(Color.FromRgb(0xB8, 0xBE, 0xC8), 0.8);
        var trace = Mat(Color.FromRgb(0x2A, 0x36, 0x48));
        var slot = Mat(Color.FromRgb(0x22, 0x28, 0x32));

        // Motherboard with copper-ish traces.
        Add(root, Box(0, -0.2, 0, 11, 0.2, 9), board);
        foreach (var (x, z, w, d) in new[] { (-1.2, 0.0, 0.08, 5.0), (0.6, -1.6, 3.2, 0.08), (-3.2, -1.6, 2.0, 0.08), (2.8, 2.6, 0.08, 1.8), (-3.6, 2.0, 0.08, 3.0) })
            Add(root, Box(x, 0, z, w, 0.02, d), trace);

        // CPU: socket, package, heat spreader and a tower cooler whose fins glow with heat.
        Add(root, Box(-2.2, 0, -1.6, 2.6, 0.12, 2.6), slot, "cpu");
        Add(root, Box(-2.2, 0.12, -1.6, 2.0, 0.1, 2.0), Mat(Color.FromRgb(0x1E, 0x5A, 0x3A)), "cpu");
        Add(root, Box(-2.2, 0.22, -1.6, 1.5, 0.12, 1.5), silver, "cpu");
        var fins = Glow(_cpuHeat, Color.FromRgb(0x50, 0x56, 0x60));
        for (int i = 0; i < 9; i++) Add(root, Box(-2.2, 0.55, -2.5 + i * 0.225, 2.2, 1.7, 0.06), fins, "cpu");
        Add(root, Box(-2.2, 0.34, -1.6, 0.5, 0.25, 0.5), metal, "cpu");
        Add(root, Box(-2.2, 2.25, -1.6, 2.3, 0.1, 2.0), metal, "cpu");

        // RAM: four sticks; the inner bar fills with memory in use and the top LED follows it.
        var stick = Mat(Color.FromRgb(0x1A, 0x20, 0x2A), 0.3);
        var led = Glow(_ramLed, Color.FromRgb(0x10, 0x14, 0x18));
        for (int i = 0; i < 4; i++)
        {
            double x = 1.0 + i * 0.6;
            Add(root, Box(x, 0, -1.6, 0.3, 0.14, 3.6), slot, "ram");
            Add(root, Box(x, 0.14, -1.6, 0.16, 1.5, 3.3), stick, "ram");
            _ramFill[i] = new ScaleTransform3D(1, 0.01, 1, x, 0.16, -1.6);
            Add(root, Box(x, 0.16, -1.6, 0.2, 1.4, 3.1), led, "ram", _ramFill[i]);
        }

        // GPU: PCIe slot, board, shroud and two fans spinning with GPU load; edge strip glows with GPU heat.
        Add(root, Box(0, 0, 2.6, 7.2, 0.14, 0.4), slot, "gpu");
        Add(root, Box(0, 0.14, 2.6, 7.6, 0.16, 2.6), Mat(Color.FromRgb(0x10, 0x30, 0x22)), "gpu");
        Add(root, Box(0, 0.3, 2.6, 7.6, 0.55, 2.6), dark, "gpu");
        Add(root, Box(0, 0.32, 3.92, 7.4, 0.12, 0.06), Glow(_gpuHeat, Color.FromRgb(0x18, 0x1C, 0x22)), "gpu");
        Add(root, Box(3.7, 0.3, 2.6, 0.2, 0.55, 2.5), Glow(_gpuHeat, Color.FromRgb(0x18, 0x1C, 0x22)), "gpu");
        var rotor = Mat(Color.FromRgb(0x2C, 0x33, 0x40), 0.3);
        for (int f = 0; f < 2; f++)
        {
            double fx = f == 0 ? -1.7 : 1.7;
            Add(root, Box(fx, 0.85, 2.6, 2.3, 0.02, 2.3), Mat(Color.FromRgb(0x16, 0x1A, 0x22)), "gpu");
            var spin = new Transform3DGroup();
            spin.Children.Add(new RotateTransform3D(_fanSpin[f]));
            spin.Children.Add(new TranslateTransform3D(fx, 0.88, 2.6));
            Add(root, Rotor(1.05), rotor, "gpu", spin);
        }

        // Data pulses along traces: CPU ↔ RAM and CPU ↔ GPU.
        var pulse = Glow(_pulse, Color.FromRgb(0x10, 0x10, 0x10));
        var pulseMesh = Box(0, 0, 0, 0.18, 0.08, 0.18);
        for (int i = 0; i < 6; i++)
        {
            bool gpu = i % 2 == 1;
            var a = new Point3D(-1.0, 0.04, gpu ? -0.4 : -1.6);
            var b = gpu ? new Point3D(-1.0, 0.04, 1.4) : new Point3D(0.8, 0.04, -1.6);
            var t = new TranslateTransform3D(a.X, a.Y, a.Z);
            _packets.Add((t, a, b, i / 6.0, gpu));
            Add(root, pulseMesh, pulse, null, t);
        }

        // Selection plate (scaled to the selected part).
        var plateT = new Transform3DGroup();
        plateT.Children.Add(_selectSize);
        plateT.Children.Add(_selectAt);
        _select.Color = Color.FromArgb(0x60, 0x4C, 0x8D, 0xFF);
        if (TryFindResource("Accent") is SolidColorBrush acc) _select.Color = Color.FromArgb(0x60, acc.Color.R, acc.Color.G, acc.Color.B);
        Add(root, Box(0, 0.001, 0, 1, 0.03, 1), Glow(_select, Color.FromArgb(0, 0, 0, 0)), null, plateT);

        var holder = new ModelVisual3D { Content = root };
        _view.Children.Add(holder);
        AddLabel("cpu", holder, new Point3D(-2.2, 2.5, -1.6), "CPU");
        AddLabel("ram", holder, new Point3D(1.9, 1.8, -1.6), "RAM");
        AddLabel("gpu", holder, new Point3D(0, 1.1, 2.6), "GPU");
        _selectAt.OffsetY = 0;
    }

    private void AddLabel(string id, ModelVisual3D anchor, Point3D at, string name)
    {
        var text = new TextBlock { Text = name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        var label = new Border
        {
            Child = text, Padding = new Thickness(7, 2, 7, 3), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0E, 0x14)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        _labels.Children.Add(label);
        _tags[id] = (anchor, at, label, text);
    }

    private void PlaceCamera()
    {
        double y = _yaw * Math.PI / 180, p = _pitch * Math.PI / 180;
        var pos = new Point3D(_dist * Math.Cos(p) * Math.Sin(y), _dist * Math.Sin(p), _dist * Math.Cos(p) * Math.Cos(y));
        var look = new Point3D(0.2, 0.2, 0.4);
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

    /// <summary>Blue (cool) → amber → red (hot), brighter with heat.</summary>
    private static Color HeatColor(double h)
    {
        h = Math.Clamp(h, 0, 1);
        Color a = Color.FromRgb(0x2A, 0x7B, 0xFF), b = Color.FromRgb(0xFF, 0xA6, 0x2B), c = Color.FromRgb(0xFF, 0x3B, 0x4E);
        var (from, to, k) = h < 0.55 ? (a, b, h / 0.55) : (b, c, (h - 0.55) / 0.45);
        byte L(byte x, byte y) => (byte)(x + (y - x) * k);
        double bright = 0.45 + 0.55 * h;
        return Color.FromRgb((byte)(L(from.R, to.R) * bright), (byte)(L(from.G, to.G) * bright), (byte)(L(from.B, to.B) * bright));
    }

    private void Frame(double dt)
    {
        var (tc, tg, tr, tch, tgh) = Targets();
        double k = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 3); // ease toward the latest reading
        _cpu += (tc - _cpu) * k; _gpu += (tg - _gpu) * k; _ram += (tr - _ram) * k; _cpuHeatT += (tch - _cpuHeatT) * k; _gpuHeatT += (tgh - _gpuHeatT) * k;
        _t += dt;

        _cpuHeat.Color = HeatColor(_cpuHeatT);
        _gpuHeat.Color = _gpuKnown ? HeatColor(_gpuHeatT) : Color.FromRgb(0x30, 0x34, 0x3A);
        var ramC = _ram > 0.85 ? Color.FromRgb(0xFF, 0x5C, 0x7A) : _ram > 0.7 ? Color.FromRgb(0xF5, 0xB8, 0x4A) : Color.FromRgb(0x3D, 0xDC, 0x97);
        _ramLed.Color = Color.FromRgb((byte)(ramC.R * 0.8), (byte)(ramC.G * 0.8), (byte)(ramC.B * 0.8));
        foreach (var f in _ramFill) f.ScaleY = Math.Max(0.01, _ram);

        // Fans: idle trickle up to ~3 turns/s at full load; stopped when the GPU gives no reading.
        if (_gpuKnown && dt > 0)
            foreach (var r in _fanSpin) r.Angle = (r.Angle + dt * (90 + 1000 * _gpu)) % 360;

        double glow = 0.45 + 0.55 * _cpu;
        _pulse.Color = Color.FromRgb((byte)(0x6C * glow), (byte)(0xC8 * glow), (byte)(0xFF * glow));
        double speed = 0.15 + 0.9 * _cpu;
        foreach (var (t, a, b, phase, gpu) in _packets)
        {
            var s = ((phase + _t * (gpu ? 0.15 + 0.9 * _gpu : speed)) % 1 + 1) % 1;
            t.OffsetX = a.X + (b.X - a.X) * s; t.OffsetY = a.Y; t.OffsetZ = a.Z + (b.Z - a.Z) * s;
        }

        // Slow turntable when nobody has touched it for a while.
        if (dt > 0 && DateTime.UtcNow - _lastInput > TimeSpan.FromSeconds(6)) { _yaw += dt * 4; PlaceCamera(); }

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
