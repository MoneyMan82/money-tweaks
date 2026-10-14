using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Controls;

public sealed record SceneOptions(int Intensity, int DurationSec, int Width, int Height, string Label);

public sealed record SceneResult(bool Completed, string? StopReason, List<double> FrameMs, List<float> FpsPerSecond,
    double? CpuAvg, double? GpuAvg, double? RamGb, double? CpuTemp, double? GpuTemp, int Triangles, int Objects, int RefreshHz);

/// <summary>
/// Synthetic 3D stress scene: procedurally textured meshes, a sun plus orbiting coloured point lights, planar
/// projected shadows and animated objects, rendered by WPF (DirectX). It runs on its own UI thread so the main
/// window stays responsive. Frame times are measured from real render callbacks; WPF presents in sync with the
/// display, so FPS can't exceed the refresh rate. The scene is seeded, so every run draws exactly the same thing.
/// </summary>
public static class StressScene
{
    public const int WarmupSec = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode dm);

    public static int RefreshRate()
    {
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 0;
    }

    /// <summary>Opens the scene window on a dedicated STA thread and completes when the run ends or is stopped.</summary>
    public static Task<SceneResult> RunAsync(SceneOptions o, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SceneResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                var w = new SceneWindow(o, r => tcs.TrySetResult(r));
                using var reg = ct.Register(() => w.Dispatcher.BeginInvoke(() => w.Stop("Stopped")));
                w.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                w.Show();
                Dispatcher.Run();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "StressScene" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary>WPF animations default to 60 FPS; let the scene render up to the display refresh rate.</summary>
    private static int AnimFps => Math.Max(60, RefreshRate());

    private sealed class SceneWindow : Window
    {
        private readonly SceneOptions _o;
        private readonly Action<SceneResult> _done;
        private readonly Stopwatch _clock = new();
        private readonly List<double> _frames = new(20000);
        private readonly List<float> _fpsSeconds = [];
        private readonly TextBlock _hud = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13, LineHeight = 18 };
        private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private TimeSpan _lastRender = TimeSpan.MinValue;
        private double _lastTick;
        private int _secFrames;
        private double _secStart;
        private bool _finished;
        private int _triangles, _objects;
        private readonly int _refresh = RefreshRate();
        // Hardware samples (written by a background task, read by the HUD).
        private readonly List<HardwareSnapshot> _hw = [];
        private readonly List<double> _gpuTemps = [];
        private volatile HardwareSnapshot? _lastHw;
        private readonly CancellationTokenSource _hwStop = new();

        public SceneWindow(SceneOptions o, Action<SceneResult> done)
        {
            _o = o; _done = done;
            Title = "Money Tweaks – synthetic 3D benchmark";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; SizeToContent = SizeToContent.WidthAndHeight; ShowInTaskbar = true;
            var root = new Grid { Width = o.Width, Height = o.Height };
            root.Children.Add(BuildViewport());
            var hudBox = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x08, 0x0B, 0x12)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 14, 10), Margin = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _hud };
            root.Children.Add(hudBox);
            var stop = new Button { Content = "■  Emergency stop (Esc)", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromRgb(0xC2, 0x3B, 0x3B)), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
            stop.Click += (_, _) => Stop("Stopped by user");
            root.Children.Add(stop);
            var tag = new TextBlock { Text = "SYNTHETIC BENCHMARK · not a Fortnite benchmark", Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            root.Children.Add(tag);
            Content = root;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Stop("Stopped by user"); };
            // Render at exactly the requested physical resolution, whatever the DPI scaling.
            SourceInitialized += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                root.Width = o.Width / dpi.DpiScaleX; root.Height = o.Height / dpi.DpiScaleY;
            };
            Loaded += (_, _) => Start();
            Closing += (_, _) => Stop("Window closed");
        }

        private void Start()
        {
            _ = Task.Run(async () =>
            {
                using var hm = new HardwareMonitor();
                var gpuSmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
                int n = 0;
                while (!_hwStop.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, _hwStop.Token); } catch { break; }
                    var s = hm.Sample();
                    _lastHw = s;
                    if (_clock.Elapsed.TotalSeconds >= WarmupSec) lock (_hw) _hw.Add(s);
                    if (n++ % 5 == 2 && File.Exists(gpuSmi) && _clock.Elapsed.TotalSeconds >= WarmupSec)
                    {
                        var gt = Core.Benchmarks.BenchmarkSampler.GpuTemp();
                        if (gt is not null) lock (_gpuTemps) _gpuTemps.Add(gt.Value);
                    }
                }
            });
            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
            _hudTimer.Tick += (_, _) => UpdateHud();
            _hudTimer.Start();
            Activate();
            Focus();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (e is not RenderingEventArgs re || re.RenderingTime == _lastRender) return; // only count real new frames
            _lastRender = re.RenderingTime;
            var now = _clock.Elapsed.TotalMilliseconds;
            var measuring = now >= WarmupSec * 1000;
            if (measuring && _lastTick >= WarmupSec * 1000) _frames.Add(now - _lastTick);
            _lastTick = now;
            if (measuring)
            {
                if (_secStart == 0) _secStart = now;
                _secFrames++;
                if (now - _secStart >= 1000) { _fpsSeconds.Add((float)(_secFrames * 1000 / (now - _secStart))); _secFrames = 0; _secStart = now; }
            }
            if (now >= (WarmupSec + _o.DurationSec) * 1000) Stop(null);
        }

        private void UpdateHud()
        {
            var t = _clock.Elapsed.TotalSeconds;
            var recent = _frames.Count > 0 ? _frames.Skip(Math.Max(0, _frames.Count - 120)).ToList() : [];
            string Fmt(double? v, string f, string unit) => v is null ? "n/a" : v.Value.ToString(f) + unit;
            double? fps = recent.Count > 0 ? 1000 / recent.Average() : null;
            var hw = _lastHw;
            _hud.Text =
                $"{_o.Label}\n" +
                (t < WarmupSec ? $"Warming up… {WarmupSec - t:0.0} s (not measured)\n" : $"Measuring {t - WarmupSec:0}/{_o.DurationSec} s\n") +
                $"FPS {Fmt(fps, "0", "")}   frame {Fmt(recent.Count > 0 ? recent.Average() : null, "0.00", " ms")}   worst {Fmt(recent.Count > 0 ? recent.Max() : null, "0.0", " ms")}\n" +
                $"CPU {Fmt(hw?.CpuPercent, "0", "%")}   GPU {Fmt(hw?.GpuPercent, "0", "%")}   RAM {Fmt(hw?.RamUsedGb, "0.0", " GB")}   CPU temp {Fmt(hw?.CpuTempC, "0", "°C")}\n" +
                $"Intensity {_o.Intensity} · {_objects} objects · {_triangles:N0} triangles · {_o.Width}×{_o.Height}" + (_refresh > 0 ? $" · display {_refresh} Hz (FPS cap)" : "");
        }

        public void Stop(string? reason)
        {
            if (_finished) return;
            _finished = true;
            CompositionTarget.Rendering -= OnRendering;
            _hudTimer.Stop();
            _hwStop.Cancel();
            List<HardwareSnapshot> hw; lock (_hw) hw = [.. _hw];
            List<double> gt; lock (_gpuTemps) gt = [.. _gpuTemps];
            double? Avg(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : Math.Round(l.Average(), 2); }
            _done(new SceneResult(reason is null, reason, _frames, _fpsSeconds,
                Avg(hw.Select(h => (double?)h.CpuPercent)), Avg(hw.Select(h => h.GpuPercent)), Avg(hw.Select(h => (double?)h.RamUsedGb)),
                Avg(hw.Select(h => h.CpuTempC)), Avg(gt.Select(x => (double?)x)), _triangles, _objects, _refresh));
            if (IsLoaded) Dispatcher.BeginInvoke(Close);
        }

        // ---- scene ------------------------------------------------------------------------------------------------
        private Viewport3D BuildViewport()
        {
            var rnd = new Random(1234); // fixed seed: identical scene every run
            int count = _o.Intensity switch { 1 => 40, 2 => 120, 3 => 300, 4 => 600, _ => 1000 };
            int tess = _o.Intensity switch { 1 => 16, 2 => 20, 3 => 28, 4 => 36, _ => 44 };
            var sphere = Sphere(tess); var torus = Torus(tess, tess / 2); var box = Box();
            Material Mat(ImageBrush tex, Color spec, Color? glow = null)
            {
                var g = new MaterialGroup();
                g.Children.Add(new DiffuseMaterial(tex));
                g.Children.Add(new SpecularMaterial(new SolidColorBrush(spec), 40));
                if (glow is { } c) g.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B))));
                g.Freeze();
                return g;
            }
            var mats = new[]
            {
                Mat(Texture(1, Color.FromRgb(70, 130, 255), Color.FromRgb(20, 30, 70)), Colors.White),
                Mat(Texture(2, Color.FromRgb(160, 90, 255), Color.FromRgb(40, 20, 70)), Colors.White, Color.FromRgb(160, 90, 255)),
                Mat(Texture(3, Color.FromRgb(255, 190, 70), Color.FromRgb(90, 50, 10)), Colors.LightYellow),
                Mat(Texture(4, Color.FromRgb(60, 220, 170), Color.FromRgb(10, 60, 50)), Colors.White, Color.FromRgb(60, 220, 170)),
            };
            var shadowMat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0))); shadowMat.Freeze();

            var sunDir = new Vector3D(-0.45, -1, -0.35);
            var world = new Model3DGroup();
            world.Children.Add(new AmbientLight(Color.FromRgb(45, 48, 60)));
            world.Children.Add(new DirectionalLight(Color.FromRgb(225, 225, 235), sunDir));
            var lights = new Model3DGroup();
            foreach (var (c, a) in new[] { (Colors.DeepSkyBlue, 0.0), (Colors.MediumPurple, 2.1), (Colors.Orange, 4.2) })
                lights.Children.Add(new PointLight(c, new Point3D(Math.Cos(a) * 9, 4, Math.Sin(a) * 9)) { ConstantAttenuation = 0.3, LinearAttenuation = 0.06 });
            lights.Transform = Spin(new Vector3D(0, 1, 0), 9);
            world.Children.Add(lights);

            var floorTex = Texture(5, Color.FromRgb(40, 46, 62), Color.FromRgb(16, 18, 26));
            floorTex.TileMode = TileMode.Tile; floorTex.ViewportUnits = BrushMappingMode.Absolute; floorTex.Viewport = new Rect(0, 0, 1, 1);
            var floorMat = new MaterialGroup(); floorMat.Children.Add(new DiffuseMaterial(floorTex)); floorMat.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(60, 60, 70)), 20));
            world.Children.Add(new GeometryModel3D(Plane(40, 10), floorMat) { BackMaterial = floorMat });

            // Shared animations keep the per-frame CPU cost low; the GPU load scales with object and triangle count.
            var spins = Enumerable.Range(0, 6).Select(i => Spin(new Vector3D(rnd.NextDouble() - 0.5, 1, rnd.NextDouble() - 0.5), 3 + i * 1.7)).ToArray();
            var bobs = Enumerable.Range(0, 4).Select(i => Bob(0.6 + i * 0.25, 1.6 + i * 0.5)).ToArray();
            var rings = Enumerable.Range(0, 5).Select(i => Spin(new Vector3D(0, 1, 0), 18 + i * 6, i % 2 == 0)).ToArray();
            var shadowProj = new MatrixTransform3D(new Matrix3D(1, 0, 0, 0, -sunDir.X / sunDir.Y, 0, -sunDir.Z / sunDir.Y, 0, 0, 0, 1, 0,
                sunDir.X / sunDir.Y * 0.02, 0.02, sunDir.Z / sunDir.Y * 0.02, 1));
            shadowProj.Freeze();
            var shadows = new Model3DGroup();
            var objects = new Model3DGroup();
            for (int i = 0; i < count; i++)
            {
                var ring = i % rings.Length;
                double r = 3 + ring * 2.4 + rnd.NextDouble() * 1.5, a = rnd.NextDouble() * Math.PI * 2, s = 0.25 + rnd.NextDouble() * 0.45;
                var mesh = (i % 3) switch { 0 => sphere, 1 => torus, _ => box };
                var tg = new Transform3DGroup();
                tg.Children.Add(new ScaleTransform3D(s, s, s));
                tg.Children.Add(spins[i % spins.Length]);
                tg.Children.Add(new TranslateTransform3D(Math.Cos(a) * r, 0.8 + rnd.NextDouble() * 2.5, Math.Sin(a) * r));
                tg.Children.Add(bobs[i % bobs.Length]);
                tg.Children.Add(rings[ring]);
                objects.Children.Add(new GeometryModel3D(mesh, mats[i % mats.Length]) { Transform = tg });
                var st = new Transform3DGroup(); st.Children.Add(tg); st.Children.Add(shadowProj);
                shadows.Children.Add(new GeometryModel3D(mesh, shadowMat) { BackMaterial = shadowMat, Transform = st });
                _triangles += mesh.TriangleIndices.Count / 3 * 2; // object + its shadow
            }
            var hero = new GeometryModel3D(Torus(tess * 2, tess), mats[1]);
            var heroT = new Transform3DGroup(); heroT.Children.Add(new ScaleTransform3D(2.2, 2.2, 2.2)); heroT.Children.Add(Spin(new Vector3D(1, 1, 0), 7)); heroT.Children.Add(new TranslateTransform3D(0, 3.2, 0));
            hero.Transform = heroT;
            objects.Children.Add(hero);
            var heroShadowT = new Transform3DGroup(); heroShadowT.Children.Add(heroT); heroShadowT.Children.Add(shadowProj);
            shadows.Children.Add(new GeometryModel3D(hero.Geometry, shadowMat) { BackMaterial = shadowMat, Transform = heroShadowT });
            _triangles += ((MeshGeometry3D)hero.Geometry).TriangleIndices.Count / 3 * 2 + 2 * 10 * 10;
            _objects = count + 1;
            world.Children.Add(shadows); // drawn after the floor, before the objects
            world.Children.Add(objects);

            var cam = new PerspectiveCamera(new Point3D(0, 9, 22), new Vector3D(0, -7, -22), new Vector3D(0, 1, 0), 55) { Transform = Spin(new Vector3D(0, 1, 0), 24) };
            var vp = new Viewport3D { Camera = cam, ClipToBounds = true };
            vp.Children.Add(new ModelVisual3D { Content = world });
            return vp;
        }

        private static RotateTransform3D Spin(Vector3D axis, double seconds, bool reverse = false)
        {
            var rot = new AxisAngleRotation3D(axis, 0);
            var an = new DoubleAnimation(0, reverse ? -360 : 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(an, AnimFps);
            rot.BeginAnimation(AxisAngleRotation3D.AngleProperty, an);
            return new RotateTransform3D(rot);
        }

        private static TranslateTransform3D Bob(double amp, double seconds)
        {
            var t = new TranslateTransform3D();
            var an = new DoubleAnimation(-amp / 2, amp / 2, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(an, AnimFps);
            t.BeginAnimation(TranslateTransform3D.OffsetYProperty, an);
            return t;
        }

        /// <summary>Procedural texture: tiles, grid lines and value noise, deterministic per seed.</summary>
        private static ImageBrush Texture(int seed, Color a, Color b)
        {
            const int N = 256;
            var px = new byte[N * N * 4];
            var rnd = new Random(seed);
            var noise = new double[17, 17];
            for (int y = 0; y < 17; y++) for (int x = 0; x < 17; x++) noise[x, y] = rnd.NextDouble();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    double fx = x / 16.0, fy = y / 16.0; int ix = (int)fx, iy = (int)fy; double tx = fx - ix, ty = fy - iy;
                    double n = noise[ix, iy] * (1 - tx) * (1 - ty) + noise[ix + 1, iy] * tx * (1 - ty) + noise[ix, iy + 1] * (1 - tx) * ty + noise[ix + 1, iy + 1] * tx * ty;
                    bool tile = ((x / 32) + (y / 32)) % 2 == 0;
                    bool line = x % 32 < 2 || y % 32 < 2;
                    double k = (tile ? 0.75 : 0.45) + n * 0.35 + (line ? 0.35 : 0);
                    k = Math.Clamp(k, 0, 1.2);
                    int i = (y * N + x) * 4;
                    px[i] = (byte)Math.Clamp(b.B + (a.B - b.B) * k, 0, 255);
                    px[i + 1] = (byte)Math.Clamp(b.G + (a.G - b.G) * k, 0, 255);
                    px[i + 2] = (byte)Math.Clamp(b.R + (a.R - b.R) * k, 0, 255);
                    px[i + 3] = 255;
                }
            var bmp = BitmapSource.Create(N, N, 96, 96, PixelFormats.Bgra32, null, px, N * 4);
            bmp.Freeze();
            return new ImageBrush(bmp);
        }

        private static MeshGeometry3D Sphere(int seg)
        {
            var m = new MeshGeometry3D();
            for (int i = 0; i <= seg; i++)
            {
                double v = (double)i / seg, th = v * Math.PI;
                for (int j = 0; j <= seg; j++)
                {
                    double u = (double)j / seg, ph = u * 2 * Math.PI;
                    var n = new Vector3D(Math.Sin(th) * Math.Cos(ph), Math.Cos(th), Math.Sin(th) * Math.Sin(ph));
                    m.Positions.Add((Point3D)n); m.Normals.Add(n); m.TextureCoordinates.Add(new Point(u * 2, v));
                }
            }
            Grid(m, seg, seg);
            m.Freeze();
            return m;
        }

        private static MeshGeometry3D Torus(int seg, int side)
        {
            var m = new MeshGeometry3D();
            const double R = 1, r = 0.35;
            for (int i = 0; i <= seg; i++)
            {
                double u = (double)i / seg * 2 * Math.PI;
                for (int j = 0; j <= side; j++)
                {
                    double v = (double)j / side * 2 * Math.PI;
                    var c = new Vector3D(Math.Cos(u), 0, Math.Sin(u));
                    var n = new Vector3D(Math.Cos(v) * c.X, Math.Sin(v), Math.Cos(v) * c.Z);
                    m.Positions.Add((Point3D)(c * R + n * r)); m.Normals.Add(n); m.TextureCoordinates.Add(new Point((double)i / seg * 3, (double)j / side));
                }
            }
            Grid(m, seg, side);
            m.Freeze();
            return m;
        }

        private static void Grid(MeshGeometry3D m, int rows, int cols)
        {
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                {
                    int a = i * (cols + 1) + j, b = a + cols + 1;
                    m.TriangleIndices.Add(a); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b);
                    m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(b + 1); m.TriangleIndices.Add(b);
                }
        }

        private static MeshGeometry3D Box()
        {
            var m = new MeshGeometry3D();
            var faces = new[] { new Vector3D(1, 0, 0), new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, -1, 0), new Vector3D(0, 0, 1), new Vector3D(0, 0, -1) };
            foreach (var n in faces)
            {
                var u = Math.Abs(n.Y) > 0.5 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
                var v = Vector3D.CrossProduct(n, u);
                int s = m.Positions.Count;
                foreach (var (du, dv, tu, tv) in new[] { (-1, -1, 0, 0), (1, -1, 1, 0), (1, 1, 1, 1), (-1, 1, 0, 1) })
                {
                    m.Positions.Add((Point3D)(n + u * du + v * dv)); m.Normals.Add(n); m.TextureCoordinates.Add(new Point(tu, tv));
                }
                m.TriangleIndices.Add(s); m.TriangleIndices.Add(s + 1); m.TriangleIndices.Add(s + 2);
                m.TriangleIndices.Add(s); m.TriangleIndices.Add(s + 2); m.TriangleIndices.Add(s + 3);
            }
            m.Freeze();
            return m;
        }

        private static MeshGeometry3D Plane(double size, int tiles)
        {
            var m = new MeshGeometry3D();
            const int Div = 10;
            for (int i = 0; i <= Div; i++)
                for (int j = 0; j <= Div; j++)
                {
                    double x = -size / 2 + size * j / Div, z = -size / 2 + size * i / Div;
                    m.Positions.Add(new Point3D(x, 0, z)); m.Normals.Add(new Vector3D(0, 1, 0)); m.TextureCoordinates.Add(new Point((double)j / Div * tiles, (double)i / Div * tiles));
                }
            Grid(m, Div, Div);
            m.Freeze();
            return m;
        }
    }
}
