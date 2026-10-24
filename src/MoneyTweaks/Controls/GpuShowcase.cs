using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks.Controls;

/// <summary>Live readings of the shown graphics card (null = not reported).</summary>
public sealed record GpuTelemetry(double? LoadPct, double? TempC, double? HotSpotC, double? ClockMhz, double? PowerW, double? PowerLimitW,
    double? VramMb, double? VramTotalMb, double? FanPct, double? FanRpm);

/// <summary>
/// The matched graphics card in a small studio: key, fill and rim lights, a heat-tinted glow under the card and a soft
/// floor shadow. Drag to rotate, scroll to zoom, double-click to reset; it turns slowly on its own after a few idle
/// seconds. Fans spin at the card's reported fan speed (and stand still when it reports 0 %, as zero-RPM cards do at
/// idle); without a fan reading they follow GPU load. Heat colour follows the temperature. Animates at most 30 fps, only
/// while <see cref="Running"/> and visible; reduced motion holds it still and only updates colours.
/// </summary>
public sealed class GpuShowcase : Grid
{
    private readonly Viewport3D _view = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 30, NearPlaneDistance = 0.05, FarPlaneDistance = 60 };
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly PointLight _glow = new(Colors.Black, new Point3D(0, -1.2, 1.2)) { Range = 5, ConstantAttenuation = 1, LinearAttenuation = 0.6 };
    private readonly GpuRig _rig = new();
    private readonly double _fit;
    private double _yaw = -32, _pitch = 14, _zoom = 1, _t;
    private double _load, _heat, _fan;
    private GpuTelemetry? _target;
    private Point? _down;
    private bool _running;
    private DateTime _lastInput = DateTime.MinValue;

    public GpuMatch Match { get; }
    /// <summary>The verified third-party model being shown (manifest entry), or null when the generated generic model is.</summary>
    public GpuAsset? Asset { get; }
    public int Triangles => _rig.Triangles;

    public GpuShowcase(GpuMatch match, bool detailed = true)
    {
        Match = match;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        _view.Camera = _cam;
        Children.Add(_view);

        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x24, 0x27, 0x30)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xF0, 0xEA, 0xE0), new Vector3D(-0.5, -0.6, -0.9)));  // key, warm, front left
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x40, 0x52, 0x7A), new Vector3D(0.9, -0.2, -0.3)));   // fill, cool
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x9A, 0xA4, 0xC0), new Vector3D(0.1, -0.35, 1)));     // rim from behind
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x30, 0x34, 0x40), new Vector3D(0, 1, 0)));           // bounce from the floor
        root.Children.Add(_glow);
        // The best verified model whose file reads and passes its checksum; otherwise the generated generic model.
        Model3D? card = null;
        foreach (var a in GpuAssetCatalog.Manifest.Candidates(match))
            if (GpuAssetCatalog.Read(a) is { } glb && Verified(glb, match, a) is { } m) { card = m; Asset = a; break; }
        card ??= GpuModel3D.Build(match, _rig, detailed);
        root.Children.Add(card);

        // Floor: a soft shadow and a faint reflection pool under the card.
        var s = _rig.Size;
        double floorY = -s.Y / 2 - 0.18;
        var pool = new RadialGradientBrush(new GradientStopCollection { new(Color.FromArgb(0x90, 0, 0, 0), 0), new(Color.FromArgb(0x40, 0, 0, 0), 0.5), new(Color.FromArgb(0, 0, 0, 0), 1) });
        pool.Freeze();
        var floor = VirtualPcScene.Quad(0, floorY, 0, Math.Max(s.X, 0.8) * 1.5, Math.Max(s.Z, 0.4) * 4);
        floor.Freeze();
        root.Children.Add(new GeometryModel3D(floor, new DiffuseMaterial(pool)));
        _glow.Position = new Point3D(0, floorY + 0.05, s.Z);
        _view.Children.Add(new ModelVisual3D { Content = root });

        _fit = Math.Max(Math.Max(s.X, s.Y * 1.6), 0.9) * 2.75;   // whole card in view at any angle
        PlaceCamera();
        _tick.Tick += (_, _) => Frame(0.033);
        IsVisibleChanged += (_, _) => UpdateTimer();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _yaw = -32; _pitch = 14; _zoom = 1; PlaceCamera(); return; }
            _down = e.GetPosition(this); CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            _yaw -= (p.X - d.X) * 0.45;
            _pitch = Math.Clamp(_pitch + (p.Y - d.Y) * 0.3, -40, 75);
            _down = p; _lastInput = DateTime.UtcNow;
            PlaceCamera();
        };
        MouseLeftButtonUp += (_, _) => { ReleaseMouseCapture(); _down = null; };
        MouseWheel += (_, e) => { _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 0.9 : 1.1), 0.45, 1.8); _lastInput = DateTime.UtcNow; PlaceCamera(); e.Handled = true; };
        Cursor = Cursors.Hand;
        ToolTip = "Drag to turn, scroll to zoom, double-click to reset";
    }

    /// <summary>The verified GLB, centred and scaled to the card's real length (its longest side).</summary>
    private Model3D? Verified(byte[] glb, GpuMatch match, GpuAsset asset)
    {
        try
        {
            var r = GlbModel.Load(glb);
            // Stand it up (length along X, fans facing the viewer), then centre and scale.
            var stand = new Transform3DGroup();
            if (asset.Rotate is [var rx, var ry, var rz])
            {
                if (rx != 0) stand.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rx)));
                if (ry != 0) stand.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), ry)));
                if (rz != 0) stand.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rz)));
            }
            var b = stand.TransformBounds(r.Bounds);
            double longest = Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ));
            if (longest <= 0) return null;
            double k = match.Shape.lengthMm / 100.0 / longest;
            var t = new Transform3DGroup();
            t.Children.Add(stand);
            t.Children.Add(new TranslateTransform3D(-(b.X + b.SizeX / 2), -(b.Y + b.SizeY / 2), -(b.Z + b.SizeZ / 2)));
            t.Children.Add(new ScaleTransform3D(k, k, k));
            r.Model.Transform = t;
            _rig.Fans.AddRange(r.Fans);
            _rig.Triangles = r.Triangles;
            _rig.Size = new Size3D(b.SizeX * k, b.SizeY * k, b.SizeZ * k);
            return r.Model;
        }
        catch (Exception ex) { MoneyTweaks.Core.Sys.Log.Warn("GPU model couldn't be read: " + ex.Message); return null; }
    }

    public bool Running
    {
        get => _running;
        set { _running = value; UpdateTimer(); }
    }

    public void Show(GpuTelemetry t)
    {
        _target = t;
        if (!_tick.IsEnabled) { Frame(0); }
    }

    private void UpdateTimer()
    {
        bool on = _running && IsVisible && !App.ReduceMotion;
        if (on && !_tick.IsEnabled) _tick.Start();
        else if (!on && _tick.IsEnabled) _tick.Stop();
    }

    private void PlaceCamera()
    {
        double y = _yaw * Math.PI / 180, p = _pitch * Math.PI / 180, d = _fit * _zoom;
        var look = new Point3D(0, 0, 0);
        _cam.Position = new Point3D(d * Math.Cos(p) * Math.Sin(y), d * Math.Sin(p), d * Math.Cos(p) * Math.Cos(y));
        _cam.LookDirection = look - _cam.Position;
        _cam.UpDirection = new Vector3D(0, 1, 0);
    }

    /// <summary>Fan speed 0..1 from the card's own fan reading, or load when it reports none.</summary>
    internal static double FanTarget(GpuTelemetry t) =>
        t.FanPct is { } pct ? Math.Clamp(pct / 100, 0, 1)
        : t.FanRpm is { } rpm ? Math.Clamp(rpm / 3000, 0, 1)
        : Math.Clamp((t.LoadPct ?? 0) / 100, 0, 1) * 0.8 + 0.1;

    private static Color HeatColor(double h)
    {
        h = Math.Clamp(h, 0, 1);
        Color a = Color.FromRgb(0x2A, 0x7B, 0xFF), b = Color.FromRgb(0xFF, 0xA6, 0x2B), c = Color.FromRgb(0xFF, 0x3B, 0x4E);
        var (from, to, k) = h < 0.55 ? (a, b, h / 0.55) : (b, c, (h - 0.55) / 0.45);
        byte L(byte x, byte y) => (byte)(x + (y - x) * k);
        return Color.FromRgb(L(from.R, to.R), L(from.G, to.G), L(from.B, to.B));
    }

    private void Frame(double dt)
    {
        if (_target is { } t)
        {
            double load = Math.Clamp((t.LoadPct ?? 0) / 100, 0, 1);
            double heat = t.TempC is { } c ? Math.Clamp((c - 35) / 50, 0, 1) : load * 0.8;
            double fan = FanTarget(t);
            double k = dt <= 0 ? 1 : 1 - Math.Exp(-dt * 2.5);
            _load += (load - _load) * k; _heat += (heat - _heat) * k; _fan += (fan - _fan) * k;
        }
        _t += dt;
        var hc = HeatColor(_heat);
        _rig.Heat.Color = hc;
        _glow.Color = Color.FromRgb((byte)(hc.R * (0.25 + 0.5 * _heat)), (byte)(hc.G * (0.25 + 0.5 * _heat)), (byte)(hc.B * (0.25 + 0.5 * _heat)));
        if (dt > 0)
        {
            double speed = _fan * 1400;   // degrees per second at full fan speed (visual, not literal RPM)
            foreach (var f in _rig.Fans) f.Angle = (f.Angle + dt * speed) % 360;
            if (DateTime.UtcNow - _lastInput > TimeSpan.FromSeconds(5)) { _yaw += dt * 6; PlaceCamera(); }
        }
    }
}
