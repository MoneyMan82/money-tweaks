using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Hardware Monitor: a live monitoring center over the shared sensor hub, plus the Driver &amp; Windows Health Center
/// (HealthCenterView.cs). Toolbar (history range, refresh rate, pause, CSV export, logging, compact window), active
/// warnings, pinned metrics, a compact overview, then collapsible detailed sections per device: CPU with every logical
/// processor, each GPU, memory, each drive, each network adapter, fans, warning settings, the hardware models and system
/// facts. Every number is a real reading; what this PC can't report is shown as "Not available" with the reason.
/// The page only updates while visible, and at most one UI update is queued at a time.
/// </summary>
public sealed partial class MonitorView : UserControl, IPageLifecycle
{
    public static bool IsShown { get; private set; }
    private static SensorHub Hub => SensorHub.Shared;
    private static MonitorSettings S => App.Settings.Monitor;

    private readonly TextBlock _status = Ui.Muted("Starting sensors…", 12);
    private readonly Border _liveDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause;
    private readonly ToggleButton _logSwitch, _compactSwitch;
    private readonly Button _openLogs;
    private readonly StackPanel _alerts = new();
    private readonly ResponsiveGrid _pinned = new() { MaxColumns = 4, MinItemWidth = 230 };
    private readonly ResponsiveGrid _overview = new() { MaxColumns = 5, MinItemWidth = 190 };
    private readonly StackPanel _details = new();
    private readonly List<MetricCard> _cards = [];
    private readonly List<CoreCell> _coreCells = [];
    private readonly Dictionary<string, TextBlock> _gpuReasons = [];
    private readonly Dictionary<string, FrameworkElement> _sectionAnchors = [];
    private readonly TextBlock _uptime = Ui.Muted(""), _timer = Ui.Muted(""), _cost = Ui.Muted("");
    private HardwareInventory? _inventory;
    private bool _dirty = true, _framePending;
    private string _alertSig = "";
    private SensorFrame? _last;

    public MonitorView()
    {
        var (root, body) = Ui.Page("Hardware Monitor & Health Center",
            "Live CPU, GPU, memory, storage and network readings from Windows performance counters, NVIDIA's driver telemetry (NVML), LibreHardwareMonitor, Windows storage counters, and HWiNFO or MSI Afterburner when they run. Nothing is installed. A reading this PC can't provide is marked Not available with the reason, never estimated. Below: driver, Windows and stability health checks.",
            HealthScanButton(), eyebrow: "Tools", icon: "\uE9D9");

        // ---- toolbar ----
        var range = new SegmentedPicker([("1 min", "1", "Last minute"), ("5 min", "5", "Last 5 minutes"), ("15 min", "15", "Last 15 minutes"), ("1 hour", "60", "Last hour")], S.RangeMinutes.ToString(CultureInfo.InvariantCulture));
        range.Changed += v => { S.RangeMinutes = int.Parse(v, CultureInfo.InvariantCulture); App.SaveSettingsSoon(); foreach (var c in _cards) c.Chart.SetRange(RangeSeconds, Hub.History, S.RefreshMs); if (_last is { } f) ApplyFrame(f, glide: false); };
        var refresh = new SegmentedPicker([("0.5 s", "500", "Sample twice a second"), ("1 s", "1000", "Sample every second (default)"), ("2 s", "2000", "Sample every 2 seconds"), ("5 s", "5000", "Sample every 5 seconds (lightest)")], S.RefreshMs.ToString(CultureInfo.InvariantCulture));
        refresh.Changed += v => { S.RefreshMs = int.Parse(v, CultureInfo.InvariantCulture); App.SaveSettingsSoon(); };
        _pause = Ui.Btn("Pause", (_, _) => TogglePause(), "BaseButton");
        _pause.ToolTip = "Freeze the charts and stop recording history and the log (the dashboard keeps updating)";
        var export = Ui.Btn("Export CSV…", (_, _) => Export(), "BaseButton");
        export.ToolTip = "Save the recorded history (up to the last hour) as a CSV file";
        _logSwitch = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = S.LogToFile, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Write every sample to a CSV file in the logs folder while Money Tweaks runs" };
        _logSwitch.Click += (_, _) => { MonitorService.SetLogging(_logSwitch.IsChecked == true); UpdateLogUi(); };
        _openLogs = Ui.Btn("Open log folder", (_, _) => OpenFolder(SensorHub.LogFolder), "GhostButton");
        _compactSwitch = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = CompactMonitorWindow.IsOpen, VerticalAlignment = VerticalAlignment.Center, ToolTip = "A small window with your pinned metrics that can stay on top of other windows" };
        _compactSwitch.Click += (_, _) => CompactMonitorWindow.Toggle();
        CompactMonitorWindow.OpenChanged += () => _compactSwitch.IsChecked = CompactMonitorWindow.IsOpen;

        var bar = new WrapPanel();
        bar.Children.Add(Labeled("History", range));
        bar.Children.Add(Labeled("Refresh", refresh));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 10) };
        actions.Children.Add(_pause);
        export.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(export);
        bar.Children.Add(actions);
        bar.Children.Add(Labeled("Log to file", SwitchRow(_logSwitch, _openLogs)));
        bar.Children.Add(Labeled("Compact window", SwitchRow(_compactSwitch, null)));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _liveDot.Background = Ui.Brush("Success");
        statusRow.Children.Add(_liveDot);
        statusRow.Children.Add(_status);
        var tb = new StackPanel();
        tb.Children.Add(bar);
        tb.Children.Add(statusRow);
        body.Children.Add(Ui.Card(tb, new Thickness(0, 0, 12, 14)));

        body.Children.Add(_alerts);
        var pinHead = Ui.Section("Pinned", "Your favourite readings. Pin any metric below with its pin button; the compact window shows the same list.");
        body.Children.Add(pinHead);
        body.Children.Add(_pinned);
        body.Children.Add(Ui.Section("Overview", "Click a tile to jump to its details."));
        body.Children.Add(_overview);
        _overview.Children.Add(Ui.Loading("Reading sensors… the first GPU and disk counter read can take a few seconds."));
        body.Children.Add(_details);
        BuildHealth(body);
        Content = root;
        UpdateLogUi();

        Hub.MetricsChanged += () => _dirty = true;
        Hub.Sampled += OnFrame;
    }

    private static double RangeSeconds => S.RangeMinutes * 60.0;

    private static FrameworkElement Labeled(string label, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 18, 10) };
        sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Subtle"), FontFamily = Ui.Res<FontFamily>("DisplayFont"), Margin = new Thickness(0, 0, 0, 5) });
        sp.Children.Add(control);
        return sp;
    }

    private static UIElement SwitchRow(ToggleButton sw, UIElement? extra)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Height = 32 };
        sp.Children.Add(sw);
        if (extra is FrameworkElement fe) { fe.Margin = new Thickness(10, 0, 0, 0); fe.VerticalAlignment = VerticalAlignment.Center; sp.Children.Add(fe); }
        return sp;
    }

    // ---- lifecycle ----

    public void OnShown()
    {
        IsShown = true;
        MonitorService.MonitorShown();
        if (_inventory is null) _ = LoadInventoryAsync();
        if (Hub.Last is { } f) ApplyFrame(f, glide: false);
    }

    public void OnHidden()
    {
        IsShown = false;
        MonitorService.MonitorHidden();
    }

    private async Task LoadInventoryAsync()
    {
        try { _inventory = await Task.Run(HardwareInventory.Read); _dirty = true; if (_last is { } f) ApplyFrame(f, glide: false); }
        catch (Exception ex) { Log.Debug("monitor inventory: " + ex.Message); }
    }

    private void OnFrame(SensorFrame f)
    {
        if (!IsShown || _framePending) return;
        _framePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _framePending = false; if (IsShown && !Hub.Paused) ApplyFrame(f, glide: true); });
    }

    private void TogglePause()
    {
        Hub.Paused = !Hub.Paused;
        _pause.Content = Hub.Paused ? "Resume" : "Pause";
        _liveDot.Background = Ui.Brush(Hub.Paused ? "Gold" : "Success");
        if (!Hub.Paused && Hub.Last is { } f) ApplyFrame(f, glide: false);
        else _status.Text = "Paused · charts frozen, history and logging stopped · hover the charts to inspect";
    }

    private void UpdateLogUi() => _openLogs.Visibility = S.LogToFile || Directory.Exists(SensorHub.LogFolder) ? Visibility.Visible : Visibility.Collapsed;

    private static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MainWindow.Current?.ShowToast("Couldn't open the folder: " + ex.Message, "Gold"); }
    }

    private void Export()
    {
        if (Hub.History.Count == 0) { MainWindow.Current?.ShowToast("Nothing recorded yet. Leave the monitor open for a few seconds first.", "Gold"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"MoneyTweaks-sensors-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV file (*.csv)|*.csv", DefaultExt = ".csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            int rows = Hub.ExportCsv(dlg.FileName);
            MainWindow.Current?.ShowToast($"Exported {rows} samples × {Hub.Metrics.Count} sensors to {Path.GetFileName(dlg.FileName)}.", "Success");
        }
        catch (Exception ex) { MainWindow.Current?.ShowToast("Export failed: " + ex.Message, "Danger"); }
    }

    // ---- building ----

    private void TogglePin(string id, bool on)
    {
        S.Pinned.Remove(id);
        if (on) S.Pinned.Add(id);
        App.SaveSettingsSoon();
        _dirty = true;
        if (_last is { } f) ApplyFrame(f, glide: false);
        CompactMonitorWindow.Current?.PinsChanged();
    }

    private MetricCard Card(IEnumerable<MetricInfo> metrics, bool compact = false, string? title = null, string? subtitle = null)
    {
        var list = metrics.ToList();
        var c = new MetricCard(list, compact, list.Count == 1 && S.Pinned.Contains(list[0].Id), TogglePin, title, subtitle);
        c.Chart.SetRange(RangeSeconds, Hub.History, S.RefreshMs);
        _cards.Add(c);
        return c;
    }

    private void Rebuild()
    {
        _dirty = false;
        _cards.Clear(); _coreCells.Clear(); _gpuReasons.Clear(); _sectionAnchors.Clear();
        _pinned.Children.Clear(); _overview.Children.Clear(); _details.Children.Clear();
        var metrics = Hub.Metrics;
        var unsupported = Hub.Unsupported;
        MetricInfo? M(string id) => metrics.FirstOrDefault(m => m.Id == id);
        IEnumerable<MetricInfo> Ms(params string[] ids) => ids.Select(M).OfType<MetricInfo>();

        // Pinned
        foreach (var id in S.Pinned) if (M(id) is { } m) _pinned.Children.Add(Card([m], compact: true, title: m.Group == MetricGroup.Gpu || m.Device.Length == 0 ? m.ShortName : m.Label));
        if (_pinned.Children.Count == 0) _pinned.Children.Add(Ui.Muted("Nothing pinned yet. Use the pin button on any reading below.", 12));

        // Overview
        var cpuName = _inventory?.Cpu.Name ?? "";
        var gpus = metrics.Where(m => m.Group == MetricGroup.Gpu).Select(m => m.Id.Split('.')[0]).Distinct().ToList();
        void Over(string title, string section, string? subtitle, params string[] ids)
        {
            var ms = Ms(ids).ToList();
            if (ms.Count == 0) return;
            var c = Card(ms, compact: true, title: title, subtitle: subtitle);
            c.Cursor = Cursors.Hand;
            c.MouseLeftButtonUp += (_, _) => { if (_sectionAnchors.TryGetValue(section, out var a)) { Expand(section); Ui.ScrollTo(a); } };
            Motion.HoverLift(c, 2);
            _overview.Children.Add(c);
        }
        Over("CPU", "cpu", cpuName, "cpu.load");
        foreach (var g in gpus.Take(2)) Over("GPU", g, M(g + ".load")?.Device ?? M(g + ".3d")?.Device, M(g + ".load") is not null ? g + ".load" : g + ".3d");
        Over("Memory", "mem", _inventory is { } inv ? $"{inv.Memory.Sum(x => x.CapacityGb):0} GB installed" : null, "mem.load");
        Over("Storage", "disks", "Busiest drive", "disk.active");
        Over("Network", "net", "All adapters", "net.down", "net.up");

        // CPU
        var cpu = Section("cpu", "Processor", "\uE950", _inventory is { } i1 ? $"{i1.Cpu.Name} · {i1.Cpu.Cores} cores / {i1.Cpu.Threads} threads" : "CPU");
        var cpuGrid = Grid3();
        foreach (var m in Ms("cpu.load", "cpu.clock", "cpu.temp", "cpu.power", "cpu.limit")) cpuGrid.Children.Add(Card([m]));
        // Per-CCD / per-core temperatures, physical core clocks and voltages (LibreHardwareMonitor with its driver).
        foreach (var m in metrics.Where(m => m.Group == MetricGroup.Cpu && (m.Id.StartsWith("cpu.temp.", StringComparison.Ordinal) || m.Id.StartsWith("cpu.core.", StringComparison.Ordinal) || m.Id.StartsWith("cpu.volt.", StringComparison.Ordinal))))
            cpuGrid.Children.Add(Card([m]));
        foreach (var u in unsupported.Where(u => u.Group == MetricGroup.Cpu)) cpuGrid.Children.Add(new UnsupportedCard(u, false));
        cpu.Children.Add(cpuGrid);
        var threads = metrics.Where(m => m.Id.StartsWith("cpu.t", StringComparison.Ordinal) && m.Id.EndsWith(".load", StringComparison.Ordinal))
            .Select(m => int.Parse(m.Id[5..m.Id.IndexOf('.', 5)], CultureInfo.InvariantCulture)).Order().ToList();
        if (threads.Count > 0)
        {
            cpu.Children.Add(SubHead("Every logical processor", "Usage and effective clock of each hardware thread, as Windows measures them."));
            var cores = new ResponsiveGrid { MaxColumns = Math.Min(12, threads.Count), MinItemWidth = 86, Margin = new Thickness(0, 0, 6, 12) };
            foreach (var t in threads)
            {
                var cell = new CoreCell(t, $"cpu.t{t}.load", $"cpu.t{t}.clock");
                _coreCells.Add(cell);
                cores.Children.Add(cell);
            }
            cpu.Children.Add(cores);
        }

        // GPUs
        foreach (var g in gpus)
        {
            var any = metrics.First(m => m.Id.StartsWith(g + ".", StringComparison.Ordinal));
            var gm = _inventory?.Gpus.FirstOrDefault(x => x.Name == any.Device);
            var sec = Section(g, any.Device, "\uE7F8", gm is null ? "Graphics" : $"{gm.Vendor} · {(gm.VramGb is { } v ? $"{v:0.#} GB VRAM" : "shared memory")}{(gm.Driver.Length > 0 ? " · driver " + gm.Driver : "")}");
            var grid = Grid3();
            string[] main = [g + ".load", g + ".3d", g + ".temp", g + ".clock", g + ".memclock", g + ".power", g + ".vram", g + ".fan"];
            foreach (var m in Ms(main)) grid.Children.Add(Card([m], title: m.Label));
            // Hot spot, memory junction, fan RPM, voltages and other rails (LibreHardwareMonitor).
            foreach (var m in metrics.Where(m => m.Id.StartsWith(g + ".", StringComparison.Ordinal) && !main.Contains(m.Id) && !m.Id.EndsWith(".thermal", StringComparison.Ordinal)))
                grid.Children.Add(Card([m], title: m.Label));
            foreach (var u in unsupported.Where(u => u.Group == MetricGroup.Gpu && u.Device == any.Device)) grid.Children.Add(new UnsupportedCard(u, false));
            sec.Children.Add(grid);
            var reasons = Ui.Muted("", 12);
            reasons.Margin = new Thickness(0, 0, 0, 12);
            _gpuReasons[g] = reasons;
            sec.Children.Add(reasons);
        }
        foreach (var u in unsupported.Where(u => u.Group == MetricGroup.Gpu && u.Device.Length == 0)) Section("gpu-none", "Graphics", "\uE7F8", "").Children.Add(new UnsupportedCard(u, false));

        // Memory
        var mem = Section("mem", "Memory", "\uE964", _inventory is { } i2 ? MemorySummary(i2) : "RAM");
        var memGrid = Grid3();
        foreach (var m in Ms("mem.load", "mem.used", "mem.avail", "mem.commit", "mem.pagereads")) memGrid.Children.Add(Card([m]));
        mem.Children.Add(memGrid);
        if (_inventory is { Memory.Count: > 0 } i3)
        {
            mem.Children.Add(SubHead("Installed modules", $"{i3.Memory.Count} of {(i3.MemorySlots > 0 ? i3.MemorySlots.ToString(CultureInfo.CurrentCulture) : "?")} slots used."));
            mem.Children.Add(Table(i3.Memory.Select(x => new[] { x.Slot, x.Manufacturer, x.PartNumber, $"{x.CapacityGb:0.#} GB", x.RatedMts is { } r ? $"{r} MT/s rated" : "", x.ConfiguredMts is { } c ? $"running {c} MT/s" : "" })));
        }

        // Storage
        var disks = metrics.Where(m => m.Group == MetricGroup.Storage && m.Id.StartsWith("disk", StringComparison.Ordinal) && m.Id != "disk.active")
            .GroupBy(m => m.Id.Split('.')[0]).ToList();
        var st = Section("disks", "Storage", "\uEDA2", _inventory is { } i4 ? string.Join(" · ", i4.Drives.Select(d => $"{d.Model} ({d.SizeGb:0} GB {d.Bus} {d.Media})".Replace("  ", " ").Replace(" )", ")"))) : "Drives");
        foreach (var d in disks)
        {
            var dev = d.First().Device;
            st.Children.Add(SubHead(dev, ""));
            var grid = Grid3();
            foreach (var m in d.Where(m => m.Id.EndsWith(".active") || m.Id.EndsWith(".latency") || m.Id.EndsWith(".temp"))) grid.Children.Add(Card([m], title: m.Label));
            var rw = d.Where(m => m.Id.EndsWith(".read") || m.Id.EndsWith(".write")).ToList();
            if (rw.Count > 0) grid.Children.Insert(Math.Min(1, grid.Children.Count), Card(rw, title: "Throughput"));
            foreach (var u in unsupported.Where(u => u.Group == MetricGroup.Storage && u.Device == dev)) grid.Children.Add(new UnsupportedCard(u, false));
            st.Children.Add(grid);
            int num = int.TryParse(d.Key[4..], out var n) ? n : -1;
            if (_inventory?.Drives.FirstOrDefault(x => x.Number == num) is { Volumes.Count: > 0 } drive) st.Children.Add(Volumes(drive));
        }
        var hwTemps = metrics.Where(m => m.Id.StartsWith("hwdisk.", StringComparison.Ordinal)).ToList();
        if (hwTemps.Count > 0)
        {
            st.Children.Add(SubHead("Drive temperatures from HWiNFO", ""));
            var grid = Grid3();
            foreach (var m in hwTemps) grid.Children.Add(Card([m]));
            st.Children.Add(grid);
        }

        // Network
        var net = Section("net", "Network", "\uE968", _inventory is { } i5 ? string.Join(" · ", i5.Adapters.Select(a => a.LinkMbps is { } l ? $"{a.Name} {LinkSpeed(l)}" : a.Name)) : "Adapters");
        var netGrid = Grid3();
        netGrid.Children.Add(Card(Ms("net.down", "net.up"), title: "All adapters"));
        foreach (var a in metrics.Where(m => m.Group == MetricGroup.Network && m.Id.EndsWith(".down") && m.Id != "net.down"))
            netGrid.Children.Add(Card(Ms(a.Id, a.Id[..^5] + ".up"), title: a.Device.Split(" · ")[0], subtitle: a.Device.Contains(" · ") ? a.Device.Split(" · ")[1] : null));
        net.Children.Add(netGrid);

        // Fans
        var fans = metrics.Where(m => m.Group == MetricGroup.Fans).ToList();
        var fanSec = Section("fans", "Fans & motherboard", "\uE9CA", fans.Count > 0 ? $"{fans.Count} fan and board readings" : "Motherboard and case fans, board temperatures");
        var fanGrid = Grid3();
        foreach (var m in fans) fanGrid.Children.Add(Card([m]));
        foreach (var u in unsupported.Where(u => u.Group == MetricGroup.Fans)) fanGrid.Children.Add(new UnsupportedCard(u, false));
        fanSec.Children.Add(fanGrid);

        BuildWarningSettings(Section("warn", "Warnings", "\uE7BA", "When to warn about heat, throttling, memory pressure and unusual usage"));
        if (_inventory is { } i6) BuildInventory(Section("hw", "Hardware details", "\uE7F4", "The actual models in this PC"), i6);
        var sys = Section("sys", "System", "\uE770", "Uptime, processes, kernel timer and the monitor itself");
        var sysBody = new StackPanel();
        foreach (var t in new[] { _uptime, _timer, _cost }) (t.Parent as Panel)?.Children.Remove(t);   // kept across rebuilds
        sysBody.Children.Add(_uptime);
        sysBody.Children.Add(_timer);
        sysBody.Children.Add(_cost);
        sys.Children.Add(Ui.Card(sysBody, new Thickness(0, 0, 12, 12)));
        sys.Children.Add(Ui.Card(SensorSources(), new Thickness(0, 0, 12, 12)));

        if (Motion.Enabled) { Motion.Stagger(_overview); Motion.Stagger(_pinned); }
    }

    /// <summary>LibreHardwareMonitor switch and status, with what its driver path needs on this PC.</summary>
    private UIElement SensorSources()
    {
        var sp = new StackPanel();
        var sw = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = S.UseLibreHardwareMonitor, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => { S.UseLibreHardwareMonitor = sw.IsChecked == true; App.SaveSettingsSoon(); };
        sp.Children.Add(SwitchLine(sw, "Use LibreHardwareMonitor sensors",
            "Open-source sensor library (MPL-2.0). Adds GPU hot spot, memory junction and fan RPM, AMD GPU sensors and, with its PawnIO driver, CPU package and core temperatures, power, clocks, voltages and motherboard sensors. Read-only: it never changes fans or clocks."));
        sp.Children.Add(Ui.Muted(Hub.LhmStatus, 12));
        var reason = Core.Monitoring.LhmSource.DriverReason;
        sp.Children.Add(Ui.Muted(reason is null ? "CPU and motherboard sensors: available (PawnIO driver active, running as administrator)." : "CPU and motherboard sensors: " + reason + ".", 12));
        if (Core.Monitoring.LhmSource.PawnIoInstalled && !Admin.IsElevated)
        {
            var b = Ui.Btn("Restart as administrator", (_, _) => { if (Admin.RestartElevated()) { App.ExitRequested = true; Application.Current.Shutdown(); } }, "GoldButton", new Thickness(0, 8, 0, 0));
            b.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(b);
        }
        return sp;
    }

    private static string MemorySummary(HardwareInventory inv)
    {
        var speed = inv.Memory.Select(m => m.ConfiguredMts ?? m.RatedMts).FirstOrDefault(x => x is not null);
        return $"{inv.Memory.Sum(m => m.CapacityGb):0} GB in {inv.Memory.Count} module{(inv.Memory.Count == 1 ? "" : "s")}{(speed is { } s ? $" · {s} MT/s" : "")}";
    }

    private static string LinkSpeed(double mbps) => mbps >= 1000 ? $"{mbps / 1000:0.#} Gbit/s" : $"{mbps:0} Mbit/s";

    private static ResponsiveGrid Grid3() => new() { MaxColumns = 3, MinItemWidth = 260 };

    private static FrameworkElement SubHead(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("HeadingFont") });
        if (sub.Length > 0) sp.Children.Add(Ui.Muted(sub, 11.5));
        return sp;
    }

    private static UIElement Table(IEnumerable<string[]> rows)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 12, 14) };
        int r = 0, cols = 0;
        foreach (var row in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderBrush = Ui.Brush("Hairline"), BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, 99);
            g.Children.Add(line);
            for (int c = 0; c < row.Length; c++)
            {
                while (cols <= c) { g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols == row.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto }); cols++; }
                var t = new TextBlock { Text = row[c], Margin = new Thickness(0, 6, 18, 6), FontSize = 12.5, Foreground = Ui.Brush(c == 0 ? "Muted" : "Text") };
                Grid.SetRow(t, r); Grid.SetColumn(t, c);
                g.Children.Add(t);
            }
            r++;
        }
        return new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static UIElement Volumes(DriveModel d)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 14) };
        foreach (var (letter, free, total) in d.Volumes)
        {
            double used = total <= 0 ? 0 : 1 - free / total;
            var head = new DockPanel();
            var right = Ui.Muted($"{free:0} GB free of {total:0} GB", 11.5);
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            head.Children.Add(new TextBlock { Text = $"Volume {letter}", FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(head);
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = used, Height = 6, Margin = new Thickness(0, 5, 0, 10), Foreground = Ui.Brush(used > 0.9 ? "Gold" : "Accent") };
            sp.Children.Add(bar);
        }
        return sp;
    }

    /// <summary>A collapsible section; its open/closed state is remembered.</summary>
    private StackPanel Section(string key, string title, string icon, string subtitle)
    {
        var body = new StackPanel();
        bool collapsed = S.Collapsed.Contains(key);
        var chevron = new TextBlock { Text = "\uE70D", Style = Ui.Res<Style>("Icon"), FontSize = 12, Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(collapsed ? -90 : 0) };
        var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 12, 12, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Ui.IconTile(icon, 34, 15);
        tile.Margin = new Thickness(0, 0, 12, 0);
        head.Children.Add(tile);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Res<FontFamily>("HeadingFont"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle.Length > 0) names.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Ui.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        Grid.SetColumn(chevron, 2);
        head.Children.Add(chevron);
        var wrap = new StackPanel();
        wrap.Children.Add(head);
        wrap.Children.Add(Ui.Rule(new Thickness(0, 0, 12, 12)));
        wrap.Children.Add(body);
        body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        head.MouseLeftButtonUp += (_, _) =>
        {
            bool open = body.Visibility != Visibility.Visible;
            if (open) { S.Collapsed.Remove(key); Motion.Expand(body, 260); if (_last is { } f) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyFrame(f, glide: false)); }
            else { S.Collapsed.Add(key); Motion.Collapse(body, 220); }
            Motion.Rotate(chevron, open ? 0 : -90, 220);
            App.SaveSettingsSoon();
        };
        _sectionAnchors[key] = wrap;
        wrap.Tag = body;
        _details.Children.Add(wrap);
        return body;
    }

    private void Expand(string key)
    {
        if (!_sectionAnchors.TryGetValue(key, out var wrap) || wrap.Tag is not FrameworkElement body || body.Visibility == Visibility.Visible) return;
        S.Collapsed.Remove(key);
        App.SaveSettingsSoon();
        Motion.Expand(body, 260);
        if (((Panel)wrap).Children[0] is Grid head && head.Children[^1] is UIElement chev) Motion.Rotate(chev, 0, 220);
    }

    private void BuildWarningSettings(StackPanel sec)
    {
        var card = new StackPanel();
        var on = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = S.AlertsEnabled, VerticalAlignment = VerticalAlignment.Center };
        on.Click += (_, _) => { S.AlertsEnabled = on.IsChecked == true; App.SaveSettingsSoon(); };
        var toasts = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = S.AlertToasts, VerticalAlignment = VerticalAlignment.Center };
        toasts.Click += (_, _) => { S.AlertToasts = toasts.IsChecked == true; App.SaveSettingsSoon(); };
        var bg = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = S.RecordInBackground, VerticalAlignment = VerticalAlignment.Center };
        bg.Click += (_, _) => MonitorService.SetRecordInBackground(bg.IsChecked == true);
        card.Children.Add(SwitchLine(on, "Show warnings", "Highlights the affected reading and lists the warning at the top of this page."));
        card.Children.Add(SwitchLine(toasts, "Notify on other pages", "A short notice when a warning starts while you are elsewhere in Money Tweaks (only while monitoring runs)."));
        card.Children.Add(SwitchLine(bg, "Keep recording in the background", "After you open this page once, history keeps recording while other pages are open, so the 1-hour view is complete. Uses a few milliseconds of CPU per sample."));
        var sliders = new ResponsiveGrid { MaxColumns = 2, MinItemWidth = 300, Margin = new Thickness(0, 8, 0, 0) };
        sliders.Children.Add(Threshold("CPU temperature", "°C", 60, 105, 1, () => S.CpuTempWarnC, v => S.CpuTempWarnC = v));
        sliders.Children.Add(Threshold("GPU temperature", "°C", 60, 100, 1, () => S.GpuTempWarnC, v => S.GpuTempWarnC = v));
        sliders.Children.Add(Threshold("Drive temperature", "°C", 40, 85, 1, () => S.DiskTempWarnC, v => S.DiskTempWarnC = v));
        sliders.Children.Add(Threshold("RAM in use", "%", 70, 99, 1, () => S.RamWarnPercent, v => S.RamWarnPercent = v));
        sliders.Children.Add(Threshold("Commit charge", "%", 70, 99, 1, () => S.CommitWarnPercent, v => S.CommitWarnPercent = v));
        sliders.Children.Add(Threshold("Sustained CPU usage", "%", 50, 100, 1, () => S.SustainedCpuPercent, v => S.SustainedCpuPercent = v));
        sliders.Children.Add(Threshold("Sustained upload", "MB/s", 1, 100, 1, () => S.SustainedUploadMBps, v => S.SustainedUploadMBps = v));
        sliders.Children.Add(Threshold("Sustained means at least", "s", 30, 600, 10, () => S.SustainedSeconds, v => S.SustainedSeconds = (int)v));
        card.Children.Add(sliders);
        card.Children.Add(Ui.Muted("Thermal throttling (CPU performance limit below 95 %, or the GPU driver reporting a thermal limit) is always flagged while warnings are on. A condition must last 5–10 seconds before it shows, so brief spikes don't raise warnings.", 11.5));
        sec.Children.Add(Ui.Card(card, new Thickness(0, 0, 12, 12)));
    }

    private static UIElement SwitchLine(ToggleButton sw, string title, string sub)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        sw.Margin = new Thickness(0, 0, 12, 0);
        g.Children.Add(sw);
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        t.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    private static UIElement Threshold(string label, string unit, double min, double max, double step, Func<double> get, Action<double> set)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 24, 12) };
        var head = new DockPanel();
        var val = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, FontFamily = Ui.Res<FontFamily>("DisplayFont") };
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Ui.Brush("Muted") });
        var s = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(get(), min, max), Margin = new Thickness(0, 4, 0, 0) };
        void Show() => val.Text = $"{s.Value.ToString("0.#", CultureInfo.CurrentCulture)} {unit}";
        s.ValueChanged += (_, _) => { set(s.Value); Show(); App.SaveSettingsSoon(); };
        Show();
        sp.Children.Add(head);
        sp.Children.Add(s);
        return sp;
    }

    private static void BuildInventory(StackPanel sec, HardwareInventory inv)
    {
        var rows = new List<string[]>
        {
            new[] { "Processor", inv.Cpu.Name, $"{inv.Cpu.Cores} cores / {inv.Cpu.Threads} threads" + (inv.Cpu.BaseMhz is { } b ? $" · base {b / 1000.0:0.0#} GHz" : "") + (inv.Cpu.L2Kb is { } l2 ? $" · L2 {l2 / 1024.0:0.#} MB" : "") + (inv.Cpu.L3Kb is { } l3 ? $" · L3 {l3 / 1024.0:0.#} MB" : "") + (inv.Cpu.Socket.Length > 0 ? " · " + inv.Cpu.Socket : "") },
        };
        foreach (var g in inv.Gpus) rows.Add(["Graphics", g.Name, (g.VramGb is { } v ? $"{v:0.#} GB VRAM" : "shared memory") + (g.Driver.Length > 0 ? " · driver " + g.Driver : "")]);
        rows.Add(["Memory", $"{inv.Memory.Sum(m => m.CapacityGb):0} GB", string.Join(" + ", inv.Memory.Select(m => $"{m.CapacityGb:0} GB {m.Manufacturer} {m.PartNumber}".Trim()))]);
        foreach (var d in inv.Drives) rows.Add(["Drive", d.Model, string.Join(" · ", new[] { d.SizeGb > 0 ? $"{d.SizeGb:0} GB" : "", d.Media, d.Bus, string.Join(" ", d.Volumes.Select(v => v.Letter)) }.Where(x => x.Length > 0))]);
        foreach (var a in inv.Adapters) rows.Add(["Network", a.Description, a.Type + (a.LinkMbps is { } l ? " · " + LinkSpeed(l) : "")]);
        rows.Add(["Motherboard", inv.Motherboard, "BIOS " + inv.Bios]);
        sec.Children.Add(Ui.Card(Table(rows), new Thickness(0, 0, 12, 12)));
    }

    // ---- live update ----

    private void ApplyFrame(SensorFrame f, bool glide)
    {
        _last = f;
        if (_dirty) { Rebuild(); glide = false; }
        var since = f.Utc.AddSeconds(-RangeSeconds);
        int ms = S.RefreshMs;
        var alertMap = f.Alerts.GroupBy(a => AlertMetric(a.Key)).ToDictionary(g => g.Key, g => g.Max(a => a.Level));
        // Only charts on screen glide: an animation off screen would keep the renderer busy for nothing.
        var view = Content is ScrollViewer sv ? new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight) : Rect.Empty;
        foreach (var c in _cards)
        {
            c.SetAlert(c.Metrics.Count == 1 && alertMap.TryGetValue(c.Metrics[0].Id, out var lv) ? lv : null);
            if (!c.IsVisible) continue;
            bool onScreen = Content is ScrollViewer s2 && c.TransformToAncestor(s2).TransformBounds(new Rect(c.RenderSize)).IntersectsWith(view);
            c.Update(f, Hub.History, since, ms, glide && f.Recorded && onScreen);
        }
        foreach (var cell in _coreCells)
        {
            if (!cell.IsVisible) break;
            var l = f.Values.GetValueOrDefault(cell.LoadId);
            var clk = f.Values.GetValueOrDefault(cell.ClockId);
            cell.Update(l, clk, Metric.Format(l, Metric.Percent), clk is null ? "" : Metric.Format(clk, Metric.Mhz));
        }
        foreach (var (g, tb) in _gpuReasons)
        {
            var reasons = f.Flags.GetValueOrDefault(g + ".reasons");
            tb.Text = reasons is null ? "" : reasons.Length == 0 ? "Clock limiters: none active right now." : $"Clock limiters active now: {reasons}. A power limit under load is normal boost behaviour; a thermal limit means the GPU is slowing down to cool off.";
            tb.Visibility = reasons is null ? Visibility.Collapsed : Visibility.Visible;
        }
        RenderAlerts(f.Alerts);
        var snap = f.Snapshot;
        _uptime.Text = $"Uptime: {(int)snap.Uptime.TotalDays}d {snap.Uptime.Hours}h {snap.Uptime.Minutes}m · {snap.ProcessCount} processes running";
        var (tmin, tmax, tcur) = TimerResolution.Query();
        _timer.Text = $"Kernel timer resolution: {tcur:0.###} ms (range {tmax:0.###} – {tmin:0.###} ms)";
        _cost.Text = $"Monitor: {Hub.Metrics.Count} sensors, last sample took {Hub.LastSampleCost.TotalMilliseconds:0} ms on a background thread · history holds {Hub.History.Count} samples" + (Hub.LogFile is { } lf && S.LogToFile ? $" · logging to {Path.GetFileName(lf)}" : "");
        if (!Hub.Paused)
            _status.Text = $"Live · every {ms / 1000.0:0.#} s · {Hub.Metrics.Count} sensors · {Hub.Unsupported.Count} not available on this PC" +
                (S.LogToFile ? " · logging to CSV" : "") + (Hub.LastSampleCost.TotalMilliseconds > 0 ? $" · sample {Hub.LastSampleCost.TotalMilliseconds:0} ms" : "");
        if (Motion.Enabled && glide && f.Recorded) Motion.Pulse(_liveDot, 0.35, 420);
    }

    /// <summary>The metric whose card a warning highlights.</summary>
    private static string AlertMetric(string key) => key switch
    {
        "cpu.throttle" => "cpu.limit",
        "cpu.sustained" => "cpu.load",
        "net.upload" => "net.up",
        "disk.sustained" => "disk.active",
        _ when key.EndsWith(".reasons", StringComparison.Ordinal) => key[..^8] + ".temp",
        _ => key,
    };

    private void RenderAlerts(IReadOnlyList<MonitorAlert> alerts)
    {
        var sig = string.Join("|", alerts.Select(a => a.Key + a.Level + a.Detail));
        if (sig == _alertSig) return;
        _alertSig = sig;
        _alerts.Children.Clear();
        foreach (var a in alerts.OrderByDescending(a => a.Level))
        {
            string tone = a.Level == AlertLevel.Critical ? "Danger" : a.Level == AlertLevel.Warning ? "Gold" : "Violet";
            var g = new Grid { Margin = new Thickness(0, 0, 12, 8), Background = Ui.Brush(tone + "Soft") };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border { Background = Ui.Brush(tone) });
            var t = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = a.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Ui.Brush(tone) });
            head.Children.Add(Ui.Muted($"   since {a.SinceUtc.ToLocalTime():HH:mm:ss}", 11.5));
            t.Children.Add(head);
            t.Children.Add(new TextBlock { Text = a.Detail, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            _alerts.Children.Add(g);
            Motion.FadeIn(g, 0, 8, 260);
        }
    }
}
