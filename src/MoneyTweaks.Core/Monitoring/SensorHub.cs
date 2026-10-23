using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>One sample: every metric's value (null = no reading this time), the classic snapshot, flags and active warnings.</summary>
public sealed record SensorFrame(DateTime Utc, IReadOnlyDictionary<string, double?> Values, HardwareSnapshot Snapshot,
    IReadOnlyDictionary<string, string> Flags, IReadOnlyList<MonitorAlert> Alerts, bool Recorded);

/// <summary>
/// The single sampler behind the Hardware Monitor, the dashboard gauges, the in-game overlay and the compact window.
/// Sensors are discovered at start and re-checked periodically (a GPU tool started later adds its readings). Every value
/// comes from a real source; a reading this PC can't provide is listed in <see cref="Unsupported"/> with the reason.
///
/// Sources (all user-mode, nothing installed): Windows performance counters (CPU load, per-core load and effective clock,
/// performance limit, disks, GPU engines and adapter memory), GlobalMemoryStatusEx, IP statistics, NVIDIA's NVML (GPU
/// temperature, clocks, power, VRAM, fan, throttle reasons), Windows storage reliability counters (drive temperatures, admin
/// only), firmware ACPI thermal zones, and the shared memory MSI Afterburner and HWiNFO publish when they run.
///
/// Sampling runs on a background timer that never overlaps itself and only while something is subscribed, so the UI thread
/// never waits on a sensor and a closed monitor costs nothing.
/// </summary>
public sealed class SensorHub : IDisposable
{
    public static SensorHub Shared { get; } = new();

    public MetricHistory History { get; } = new();
    public MonitorAlerts AlertEngine { get; } = new();
    public Func<MonitorSettings> Settings { get; set; } = () => new MonitorSettings();

    /// <summary>Raised on a background thread after every sample.</summary>
    public event Action<SensorFrame>? Sampled;
    /// <summary>Raised (background thread) when sensors appear or disappear.</summary>
    public event Action? MetricsChanged;
    /// <summary>Raised (background thread) once when a warning starts.</summary>
    public event Action<MonitorAlert>? AlertStarted;

    public SensorFrame? Last { get; private set; }
    public IReadOnlyList<MetricInfo> Metrics { get { lock (_gate) return [.. _metrics.Values]; } }
    public IReadOnlyList<UnsupportedReading> Unsupported { get { lock (_gate) return [.. _unsupported.Values]; } }
    public MetricInfo? Find(string id) { lock (_gate) return _metrics.GetValueOrDefault(id); }

    /// <summary>History recording and file logging pause; live readers (dashboard, overlay) keep updating.</summary>
    public bool Paused { get; set; }
    public string? LogFile { get; private set; }
    public TimeSpan LastSampleCost { get; private set; }

    private readonly object _gate = new();
    private readonly Dictionary<string, MetricInfo> _metrics = [];
    private readonly Dictionary<string, UnsupportedReading> _unsupported = [];
    private readonly Dictionary<object, bool> _users = [];
    private Timer? _timer;
    private bool _running, _initialized, _disposed;
    private readonly HashSet<string> _alertKeys = [];

    // Sources
    private HardwareMonitor? _legacy;            // CPU temperature chain (ACPI → Afterburner → HWiNFO), shared with older views
    private PerformanceCounterCategory? _cpuCat, _diskCat, _gpuEngCat, _gpuMemCat;
    private InstanceDataCollectionCollection? _cpuPrev, _diskPrev, _gpuEngPrev;
    private PerformanceCounter? _procs, _pageReads;
    private string _cpuName = "CPU";
    private int _cores, _threads;
    private List<DxgiAdapter> _adapters = [];
    private readonly List<(int index, IntPtr nvml, DxgiAdapter? dx, string name)> _gpus = [];
    private readonly Dictionary<string, (string label, long rx, long tx)> _nicPrev = [];
    private NetworkInterface[] _nics = [];
    private DateTime _nicsAt = DateTime.MinValue, _lastNet = DateTime.UtcNow, _gpuEngAt = DateTime.MinValue, _diskTempAt = DateTime.MinValue, _extrasAt = DateTime.MinValue;
    private readonly Dictionary<string, (string model, int number)> _disks = [];
    private Dictionary<string, double> _diskTemps = [];
    private bool _diskTempDenied;
    private List<HwInfoReading>? _hwinfo;
    private string _cpuTop = "";
    private DateTime _cpuTopAt = DateTime.MinValue;
    private StreamWriter? _log;
    private List<string>? _logIds;
    private int _logRows;

    /// <summary>Starts sampling for <paramref name="user"/> (any object; release with the same object). <paramref name="record"/>:
    /// this user wants history and logging (the monitor, compact window); false for live-only readers.</summary>
    public void Acquire(object user, bool record = true)
    {
        lock (_gate)
        {
            _users[user] = record;
            if (_running || _disposed) return;
            _running = true;
            _timer ??= new Timer(_ => Tick());
            _timer.Change(0, Timeout.Infinite);
        }
    }

    public void Release(object user)
    {
        lock (_gate)
        {
            _users.Remove(user);
            if (_users.Count == 0) { _running = false; _timer?.Change(Timeout.Infinite, Timeout.Infinite); }
        }
    }

    public bool IsHeldBy(object user) { lock (_gate) return _users.ContainsKey(user); }
    private bool Recording { get { lock (_gate) return _users.Values.Any(r => r); } }

    private void Tick()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!_initialized) Initialize();
            var frame = Sample();
            Last = frame;
            Sampled?.Invoke(frame);
        }
        catch (Exception ex) { Log.Debug("sensor hub: " + ex.Message); }
        finally
        {
            LastSampleCost = sw.Elapsed;
            lock (_gate)
            {
                if (_running && !_disposed)
                {
                    int interval = Math.Clamp(Settings().RefreshMs, 250, 10000);
                    // Live views (dashboard gauges, in-game overlay) keep their one-second pace even at a slower monitor rate.
                    if (_users.Values.Any(r => !r)) interval = Math.Min(interval, 1000);
                    _timer?.Change(Math.Max(50, interval - (int)sw.ElapsedMilliseconds), Timeout.Infinite);
                }
            }
        }
    }

    // ---- discovery --------------------------------------------------------------------------------------------------

    private void Initialize()
    {
        _initialized = true;
        using var _ = Perf.Measure("sensor hub init", 0);
        _legacy = new HardwareMonitor();
        _cpuCat = Category("Processor Information") ?? Category("Processor");
        _diskCat = Category("PhysicalDisk");
        _gpuEngCat = Category("GPU Engine");
        _gpuMemCat = Category("GPU Adapter Memory");
        try { _procs = new PerformanceCounter("System", "Processes"); _procs.NextValue(); } catch { _procs = null; }
        try { _pageReads = new PerformanceCounter("Memory", "Page Reads/sec"); _pageReads.NextValue(); } catch { _pageReads = null; }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var o in s.Get()) { _cpuName = o["Name"]?.ToString()?.Trim() ?? _cpuName; _cores += Convert.ToInt32(o["NumberOfCores"]); _threads += Convert.ToInt32(o["NumberOfLogicalProcessors"]); }
        }
        catch { _threads = Environment.ProcessorCount; }
        // GPUs: DXGI adapters (all vendors), matched with NVML devices by name for NVIDIA telemetry.
        // Prime the rate counters so the first real sample has a baseline (the first GPU Engine read takes seconds).
        _cpuPrev = Read(_cpuCat); _diskPrev = Read(_diskCat); _gpuEngPrev = Read(_gpuEngCat);
        // DXGI can list one physical GPU twice (e.g. a second LUID for a hybrid or remote display path); only adapters that
        // own GPU engines are real render devices.
        var engineLuids = _gpuEngPrev?["utilization percentage"]?.Keys.Cast<string>()
            .Select(i => i.IndexOf("luid_", StringComparison.Ordinal) is var li and >= 0 && i.IndexOf("_phys", StringComparison.Ordinal) is var pi and > 0 && pi > li ? i[li..pi].ToLowerInvariant() : "")
            .ToHashSet() ?? [];
        _adapters = [.. Dxgi.Adapters().Where(a => engineLuids.Count == 0 || engineLuids.Contains(a.CounterKey.ToLowerInvariant()))];
        var nv = Nvml.Devices.ToList();
        int gi = 0;
        foreach (var a in _adapters)
        {
            int k = nv.FindIndex(d => string.Equals(d.name, a.Name, StringComparison.OrdinalIgnoreCase));
            if (k < 0 && a.VendorId == 0x10DE && nv.Count == _adapters.Count(x => x.VendorId == 0x10DE)) k = 0;
            IntPtr h = IntPtr.Zero;
            if (k >= 0) { h = nv[k].handle; nv.RemoveAt(k); }
            _gpus.Add((gi++, h, a, a.Name));
        }
        foreach (var d in nv) _gpus.Add((gi++, d.handle, null, d.name));   // NVML device DXGI didn't list (rare)
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, FriendlyName FROM MSFT_PhysicalDisk");
            foreach (var o in s.Get()) _disks[o["DeviceId"]?.ToString() ?? ""] = (o["FriendlyName"]?.ToString()?.Trim() ?? "Drive", int.TryParse(o["DeviceId"]?.ToString(), out var n) ? n : -1);
        }
        catch (Exception ex) { Log.Debug("monitor: physical disks: " + ex.Message); }
        Thread.Sleep(250);
    }

    private static PerformanceCounterCategory? Category(string name)
    {
        try { return PerformanceCounterCategory.Exists(name) ? new PerformanceCounterCategory(name) : null; }
        catch { return null; }
    }

    private static InstanceDataCollectionCollection? Read(PerformanceCounterCategory? c)
    {
        if (c is null) return null;
        try { return c.ReadCategory(); } catch (Exception ex) { Log.Debug("counter category: " + ex.Message); return null; }
    }

    private void Define(string id, MetricGroup g, string device, string label, string unit, string source, double? max = null)
    {
        bool added = false;
        lock (_gate)
        {
            if (!_metrics.TryGetValue(id, out var m) || m.Source != source || m.Device != device)
            {
                _metrics[id] = new MetricInfo(id, g, device, label, unit, source, max);
                added = m is null;
            }
            _unsupported.Remove(UnsupportedKey(g, device, label));
        }
        if (added) MetricsChanged?.Invoke();
    }

    private void Missing(MetricGroup g, string device, string label, string reason)
    {
        bool changed;
        lock (_gate)
        {
            var key = UnsupportedKey(g, device, label);
            changed = !_unsupported.TryGetValue(key, out var u) || u.Reason != reason;
            if (changed) _unsupported[key] = new UnsupportedReading(g, device, label, reason);
        }
        if (changed) MetricsChanged?.Invoke();
    }

    private static string UnsupportedKey(MetricGroup g, string device, string label) => $"{g}|{device}|{label}";

    // ---- sampling ---------------------------------------------------------------------------------------------------

    private SensorFrame Sample()
    {
        var now = DateTime.UtcNow;
        var v = new Dictionary<string, double?>();
        var flags = new Dictionary<string, string>();
        if (now - _extrasAt > TimeSpan.FromSeconds(5)) { _hwinfo = HwInfo.Readings(); _extrasAt = now; }

        SampleCpu(v, flags);
        SampleMemory(v);
        double? gpu3dTotal = SampleGpus(v, flags);
        SampleDisks(v, now);
        var (down, up) = SampleNetwork(v, now);
        SampleHwInfoExtras(v);

        var (total, avail, load) = MemoryInfo.Status();
        int procs = 0;
        try { procs = (int)(_procs?.NextValue() ?? 0); } catch { }
        if (procs > 0) v["sys.procs"] = procs;
        var snap = new HardwareSnapshot(v.GetValueOrDefault("cpu.load") ?? 0, (total - avail) / 1073741824.0, total / 1073741824.0, load,
            gpu3dTotal, v.GetValueOrDefault("disk.active"), down, up, v.GetValueOrDefault("cpu.temp"), procs, TimeSpan.FromMilliseconds(Environment.TickCount64))
            { GpuTempC = _gpus.Count > 0 ? v.GetValueOrDefault("gpu0.temp") : null };

        var s = Settings();
        bool record = Recording && !Paused;
        // Look up the busiest processes only while a sustained-CPU warning is building (at most every 30 s).
        if (AlertEngine.Building("cpu.sustained") && now - _cpuTopAt > TimeSpan.FromSeconds(30)) { _cpuTopAt = now; _cpuTop = TopProcesses(); }
        if (_cpuTop.Length > 0) flags["cpu.top"] = _cpuTop;
        var metrics = Metrics;
        var alerts = AlertEngine.Evaluate(now, v, metrics, flags, s);
        foreach (var a in alerts.Where(a => _alertKeys.Add(a.Key))) AlertStarted?.Invoke(a);
        _alertKeys.IntersectWith(alerts.Select(a => a.Key));

        if (record)
        {
            History.Add(now, v);
            if (s.LogToFile) WriteLog(now, v, metrics); else CloseLog();
        }
        return new SensorFrame(now, v, snap, flags, alerts, record);
    }

    private static double Rate(InstanceDataCollectionCollection? prev, InstanceDataCollectionCollection cur, string counter, string instance)
    {
        var c = cur[counter]?[instance];
        if (c is null) return double.NaN;
        var p = prev?[counter]?[instance];
        return p is null ? double.NaN : CounterSample.Calculate(p.Sample, c.Sample);
    }

    private void SampleCpu(Dictionary<string, double?> v, Dictionary<string, string> flags)
    {
        const string dev = "";
        var cur = Read(_cpuCat);
        if (cur is not null)
        {
            bool utility = cur.Contains("% processor utility");
            string loadCounter = utility ? "% processor utility" : "% processor time";
            const string total = "_Total";
            double load = Rate(_cpuPrev, cur, loadCounter, total);
            if (!double.IsNaN(load)) { v["cpu.load"] = Math.Clamp(load, 0, 100); Define("cpu.load", MetricGroup.Cpu, dev, "Usage", Metric.Percent, "Windows performance counters", 100); }
            bool actual = cur.Contains("actual frequency");
            double baseMhz = cur.Contains("processor frequency") ? Rate(_cpuPrev, cur, "processor frequency", total) : double.NaN;
            double Clock(string inst)
            {
                if (actual) { var a = Rate(_cpuPrev, cur, "actual frequency", inst); if (a > 0) return a; }
                if (double.IsNaN(baseMhz) || !cur.Contains("% processor performance")) return double.NaN;
                var perf = Rate(_cpuPrev, cur, "% processor performance", inst);
                return double.IsNaN(perf) ? double.NaN : baseMhz * perf / 100;
            }
            double clk = Clock(total);
            if (clk > 0) { v["cpu.clock"] = clk; Define("cpu.clock", MetricGroup.Cpu, dev, "Effective clock", Metric.Mhz, "Windows performance counters"); }
            if (cur.Contains("% performance limit"))
            {
                double lim = Rate(_cpuPrev, cur, "% performance limit", total);
                if (!double.IsNaN(lim) && lim > 0) { v["cpu.limit"] = Math.Min(100, lim); Define("cpu.limit", MetricGroup.Cpu, dev, "Performance limit", Metric.Percent, "Windows performance counters (100 % = not limited)", 100); }
            }
            // Per logical processor: instances "0,0", "0,1" … (group,index) on Processor Information, "0", "1" … on Processor.
            var insts = cur[loadCounter]?.Keys.Cast<string>().Where(i => !i.Contains("_Total")).ToList() ?? [];
            foreach (var inst in insts.OrderBy(i => int.TryParse(i.Split(',').Last(), out var n) ? n : 0))
            {
                int idx = int.TryParse(inst.Split(',').Last(), out var n) ? n : -1;
                if (idx < 0) continue;
                double l = Rate(_cpuPrev, cur, loadCounter, inst);
                if (!double.IsNaN(l)) { v[$"cpu.t{idx}.load"] = Math.Clamp(l, 0, 100); Define($"cpu.t{idx}.load", MetricGroup.Cpu, $"CPU {idx}", "Usage", Metric.Percent, "Windows performance counters", 100); }
                double c = Clock(inst);
                if (c > 0) { v[$"cpu.t{idx}.clock"] = c; Define($"cpu.t{idx}.clock", MetricGroup.Cpu, $"CPU {idx}", "Clock", Metric.Mhz, "Windows performance counters"); }
            }
            _cpuPrev = cur;
        }
        else Missing(MetricGroup.Cpu, dev, "Usage", "Windows performance counters are unavailable (they may be disabled or corrupt; 'lodctr /r' rebuilds them).");

        var t = _legacy?.CpuTemperature();
        if (t is not null) { v["cpu.temp"] = t; Define("cpu.temp", MetricGroup.Cpu, dev, "Temperature", Metric.Celsius, HardwareMonitor.CpuTempSource(t)); }
        else Missing(MetricGroup.Cpu, dev, "Temperature", "Your firmware doesn't expose it to Windows, and reading it directly needs a kernel driver (Money Tweaks installs none). Start HWiNFO with Shared Memory Support, or MSI Afterburner, and it appears here.");

        if (HwInfo.CpuPower(_hwinfo) is { } pw) { v["cpu.power"] = pw; Define("cpu.power", MetricGroup.Cpu, dev, "Package power", Metric.Watts, "HWiNFO shared memory"); }
        else Missing(MetricGroup.Cpu, dev, "Package power", "Windows doesn't report CPU power without a kernel driver. HWiNFO with Shared Memory Support provides it.");
    }

    private void SampleMemory(Dictionary<string, double?> v)
    {
        var (total, avail, load) = MemoryInfo.Status();
        double gb = 1073741824.0;
        v["mem.load"] = load; Define("mem.load", MetricGroup.Memory, "", "In use", Metric.Percent, "GlobalMemoryStatusEx", 100);
        v["mem.used"] = (total - avail) / gb; Define("mem.used", MetricGroup.Memory, "", "Used", Metric.Gb, "GlobalMemoryStatusEx", Math.Round(total / gb, 1));
        v["mem.avail"] = avail / gb; Define("mem.avail", MetricGroup.Memory, "", "Available", Metric.Gb, "GlobalMemoryStatusEx", Math.Round(total / gb, 1));
        var (limit, cavail) = MemoryInfo.Commit();
        if (limit > 0) { v["mem.commit"] = 100.0 * (limit - cavail) / limit; Define("mem.commit", MetricGroup.Memory, "", "Commit charge", Metric.Percent, "GlobalMemoryStatusEx (RAM + page file)", 100); }
        try { if (_pageReads is not null) { v["mem.pagereads"] = _pageReads.NextValue(); Define("mem.pagereads", MetricGroup.Memory, "", "Page reads (hard faults)", Metric.PerSec, "Windows performance counters"); } } catch { }
    }

    private double? SampleGpus(Dictionary<string, double?> v, Dictionary<string, string> flags)
    {
        // Engine utilization per adapter from the "GPU Engine" counters (per process × engine instances, re-read each sample).
        var eng = _gpuEngCat is not null ? Read(_gpuEngCat) : null;
        var per3d = new Dictionary<string, double>(); var perMax = new Dictionary<string, Dictionary<string, double>>();
        double? total3d = null;
        if (eng is not null && eng["utilization percentage"] is { } col)
        {
            foreach (string inst in col.Keys)
            {
                double u = Rate(_gpuEngPrev, eng, "utilization percentage", inst);
                if (double.IsNaN(u)) continue;
                int li = inst.IndexOf("luid_", StringComparison.Ordinal), pi = inst.IndexOf("_phys", StringComparison.Ordinal), ei = inst.IndexOf("engtype_", StringComparison.Ordinal);
                if (li < 0 || pi < li || ei < 0) continue;
                string luid = inst[li..pi].ToLowerInvariant(), type = inst[(ei + 8)..];
                if (type.Equals("3D", StringComparison.OrdinalIgnoreCase)) { per3d[luid] = per3d.GetValueOrDefault(luid) + u; total3d = (total3d ?? 0) + u; }
                var m = perMax.TryGetValue(luid, out var d) ? d : perMax[luid] = [];
                m[type] = m.GetValueOrDefault(type) + u;
            }
            _gpuEngPrev = eng;
        }
        if (total3d is { } t3) total3d = Math.Min(100, t3);
        var mem = Read(_gpuMemCat);

        foreach (var (i, h, dx, name) in _gpus)
        {
            string p = $"gpu{i}";
            string key = dx?.CounterKey.ToLowerInvariant() ?? "";
            var r = h != IntPtr.Zero ? Nvml.Read(h) : null;
            const string nvSrc = "NVIDIA NVML (driver)", ctr = "Windows GPU counters";
            // Load: NVML when present (what nvidia-smi shows), else the busiest engine type, as Task Manager shows it.
            if (r?.LoadPercent is { } l) { v[$"{p}.load"] = l; Define($"{p}.load", MetricGroup.Gpu, name, "Usage", Metric.Percent, nvSrc, 100); }
            else if (perMax.TryGetValue(key, out var types) && types.Count > 0) { v[$"{p}.load"] = Math.Min(100, types.Values.Max()); Define($"{p}.load", MetricGroup.Gpu, name, "Usage", Metric.Percent, ctr + " (busiest engine)", 100); }
            if (per3d.TryGetValue(key, out var d3)) { v[$"{p}.3d"] = Math.Min(100, d3); Define($"{p}.3d", MetricGroup.Gpu, name, "3D engine", Metric.Percent, ctr, 100); }

            double? temp = r?.TempC;
            string tempSrc = nvSrc;
            if (temp is null && dx?.VendorId != 0x10DE)
            {
                temp = HwInfoGpu("GPU Temperature", HwInfo.TypeTemperature); tempSrc = "HWiNFO shared memory";
                if (temp is null && _gpus.Count(g => g.dx?.VendorId != 0x10DE) == 1) { temp = Afterburner.MonitoringValue(Afterburner.SrcGpuTemperature, cpu: false); tempSrc = "MSI Afterburner"; }
            }
            if (temp is not null) { v[$"{p}.temp"] = temp; Define($"{p}.temp", MetricGroup.Gpu, name, "Temperature", Metric.Celsius, tempSrc); }
            else Missing(MetricGroup.Gpu, name, "Temperature", r is null ? "Only NVIDIA GPUs report it to Money Tweaks directly. For AMD and Intel GPUs, start HWiNFO with Shared Memory Support or MSI Afterburner." : "The driver didn't report a temperature for this GPU.");

            double? clock = r?.CoreMhz ?? (r is null ? HwInfoGpu("GPU Clock", 6) : null);
            if (clock is not null) { v[$"{p}.clock"] = clock; Define($"{p}.clock", MetricGroup.Gpu, name, "Core clock", Metric.Mhz, r is not null ? nvSrc : "HWiNFO shared memory"); }
            else Missing(MetricGroup.Gpu, name, "Core clock", "Not reported for this GPU (NVIDIA via NVML, others via HWiNFO).");
            if (r?.MemMhz is { } mm) { v[$"{p}.memclock"] = mm; Define($"{p}.memclock", MetricGroup.Gpu, name, "Memory clock", Metric.Mhz, nvSrc); }

            double? power = r?.PowerW ?? (r is null ? HwInfoGpu("GPU Power", HwInfo.TypePower) ?? HwInfoGpu("Total Board Power", HwInfo.TypePower) ?? HwInfoGpu("GPU ASIC Power", HwInfo.TypePower) : null);
            if (power is not null) { v[$"{p}.power"] = power; Define($"{p}.power", MetricGroup.Gpu, name, "Power", Metric.Watts, r is not null ? nvSrc : "HWiNFO shared memory", r?.PowerLimitW); }
            else Missing(MetricGroup.Gpu, name, "Power", "Not reported for this GPU (NVIDIA via NVML, others via HWiNFO).");
            if (r?.PowerLimitW is { } plw) flags[$"{p}.powerlimit"] = plw.ToString("0", CultureInfo.InvariantCulture);

            double? vramUsed = r?.VramUsedMb, vramTotal = r?.VramTotalMb ?? (dx is { DedicatedVideoMemory: > 0 } ? dx.DedicatedVideoMemory / 1048576.0 : null);
            string vramSrc = nvSrc;
            if (vramUsed is null && mem is not null && mem["dedicated usage"] is { } du)
                foreach (string inst in du.Keys)
                    if (inst.ToLowerInvariant().StartsWith(key, StringComparison.Ordinal) && key.Length > 0) { vramUsed = (vramUsed ?? 0) + du[inst].RawValue / 1048576.0; vramSrc = ctr; }
            if (vramUsed is not null) { v[$"{p}.vram"] = vramUsed; Define($"{p}.vram", MetricGroup.Gpu, name, "VRAM used", Metric.Mb, vramSrc, vramTotal); }
            if (vramTotal is not null) flags[$"{p}.vramtotal"] = vramTotal.Value.ToString("0", CultureInfo.InvariantCulture);

            double? fan = r?.FanPercent;
            if (fan is not null) { v[$"{p}.fan"] = fan; Define($"{p}.fan", MetricGroup.Gpu, name, "Fan speed", Metric.Percent, nvSrc, 100); }
            else Missing(MetricGroup.Gpu, name, "Fan speed", r is not null ? "This GPU's driver doesn't report a fan (common on laptops and passively cooled cards)." : "Not reported for this GPU (NVIDIA via NVML, others via HWiNFO's fan readings below).");

            if (r?.ThrottleReasons is { } tr)
            {
                var reasons = Nvml.Reasons(tr);
                flags[$"{p}.reasons"] = reasons.Count == 0 ? "" : string.Join(", ", reasons);
                v[$"{p}.thermal"] = Nvml.IsThermal(tr) ? 1 : 0;
            }
        }
        if (_gpus.Count == 0) Missing(MetricGroup.Gpu, "", "GPU", "No hardware display adapter was found.");
        return total3d;
    }

    private double? HwInfoGpu(string label, int type)
    {
        if (_hwinfo is null) return null;
        var r = _hwinfo.FirstOrDefault(x => x.Type == type && x.Sensor.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) && x.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        return r is null || double.IsNaN(r.Value) || r.Value <= 0 ? null : r.Value;
    }

    private void SampleDisks(Dictionary<string, double?> v, DateTime now)
    {
        var cur = Read(_diskCat);
        if (cur is not null && cur["% idle time"] is { } idle)
        {
            double maxActive = 0; bool any = false;
            foreach (string inst in idle.Keys)
            {
                if (inst == "_Total") continue;
                var parts = inst.Split(' ', 2);
                if (!int.TryParse(parts[0], out var num)) continue;
                string model = _disks.Values.FirstOrDefault(d => d.number == num).model ?? $"Disk {num}";
                string dev = $"{model} ({(parts.Length > 1 ? parts[1].ToUpperInvariant() : "no letter")})";
                double act = 100 - Rate(_diskPrev, cur, "% idle time", inst);
                if (!double.IsNaN(act)) { act = Math.Clamp(act, 0, 100); v[$"disk{num}.active"] = act; Define($"disk{num}.active", MetricGroup.Storage, dev, "Active time", Metric.Percent, "Windows performance counters", 100); maxActive = Math.Max(maxActive, act); any = true; }
                double rd = Rate(_diskPrev, cur, "disk read bytes/sec", inst), wr = Rate(_diskPrev, cur, "disk write bytes/sec", inst);
                if (!double.IsNaN(rd)) { v[$"disk{num}.read"] = rd / 1048576; Define($"disk{num}.read", MetricGroup.Storage, dev, "Read", Metric.MBps, "Windows performance counters"); }
                if (!double.IsNaN(wr)) { v[$"disk{num}.write"] = wr / 1048576; Define($"disk{num}.write", MetricGroup.Storage, dev, "Write", Metric.MBps, "Windows performance counters"); }
                double lat = Rate(_diskPrev, cur, "avg. disk sec/transfer", inst);
                if (!double.IsNaN(lat)) { v[$"disk{num}.latency"] = lat * 1000; Define($"disk{num}.latency", MetricGroup.Storage, dev, "Response time", "ms", "Windows performance counters"); }
                if (_diskTemps.TryGetValue(num.ToString(CultureInfo.InvariantCulture), out var temp)) { v[$"disk{num}.temp"] = temp; Define($"disk{num}.temp", MetricGroup.Storage, dev, "Temperature", Metric.Celsius, "Windows storage reliability counters"); }
                else if (_diskTempDenied) Missing(MetricGroup.Storage, dev, "Temperature", "Windows only shares drive temperatures with administrators. Restart Money Tweaks as admin to see them.");
            }
            if (any) { v["disk.active"] = maxActive; Define("disk.active", MetricGroup.Storage, "", "Busiest drive", Metric.Percent, "Windows performance counters", 100); }
            _diskPrev = cur;
        }
        // Drive temperatures change slowly and the WMI query is comparatively expensive: once a minute.
        if (now - _diskTempAt > TimeSpan.FromSeconds(60)) { _diskTempAt = now; ReadDiskTemps(); }
    }

    private void ReadDiskTemps()
    {
        var temps = new Dictionary<string, double>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter");
            foreach (var o in s.Get())
                if (o["Temperature"] is { } t && Convert.ToDouble(t, CultureInfo.InvariantCulture) is var c and > 0 and < 120) temps[o["DeviceId"]?.ToString() ?? ""] = c;
            _diskTempDenied = false;
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.AccessDenied) { _diskTempDenied = true; }
        catch (UnauthorizedAccessException) { _diskTempDenied = true; }
        catch (Exception ex) { Log.Debug("monitor: drive temperatures: " + ex.Message); _diskTempDenied = !Admin.IsElevated; }
        _diskTemps = temps;
    }

    private (double down, double up) SampleNetwork(Dictionary<string, double?> v, DateTime now)
    {
        if (now - _nicsAt > TimeSpan.FromSeconds(15)) { try { _nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { } _nicsAt = now; }
        double secs = Math.Max(0.05, (now - _lastNet).TotalSeconds); _lastNet = now;
        double down = 0, up = 0;
        foreach (var n in _nics)
        {
            if (!HardwareMonitor.CountsTraffic(n)) continue;
            IPInterfaceStatistics st;
            try { st = n.GetIPStatistics(); } catch { continue; }
            string id = "net." + Short(n.Id);
            if (_nicPrev.TryGetValue(id, out var prev))
            {
                double d = Math.Max(0, (st.BytesReceived - prev.rx) / 1024.0 / secs), u = Math.Max(0, (st.BytesSent - prev.tx) / 1024.0 / secs);
                down += d; up += u;
                string dev = n.Name == n.Description ? n.Name : $"{n.Name} · {n.Description}";
                v[id + ".down"] = d; Define(id + ".down", MetricGroup.Network, dev, "Download", Metric.KBps, "Windows IP statistics");
                v[id + ".up"] = u; Define(id + ".up", MetricGroup.Network, dev, "Upload", Metric.KBps, "Windows IP statistics");
            }
            _nicPrev[id] = (n.Description, st.BytesReceived, st.BytesSent);
        }
        v["net.down"] = down; Define("net.down", MetricGroup.Network, "All adapters", "Download", Metric.KBps, "Windows IP statistics");
        v["net.up"] = up; Define("net.up", MetricGroup.Network, "All adapters", "Upload", Metric.KBps, "Windows IP statistics");
        return (down, up);
    }

    /// <summary>Fans and drive temperatures HWiNFO publishes (it reads them with its own driver); nothing when it isn't sharing.</summary>
    private void SampleHwInfoExtras(Dictionary<string, double?> v)
    {
        if (_hwinfo is null)
        {
            Missing(MetricGroup.Fans, "", "Fan speeds", "Motherboard fan speeds need a kernel driver to read. HWiNFO with Shared Memory Support provides them; GPU fans are shown with the GPU.");
            return;
        }
        foreach (var r in _hwinfo)
        {
            if (double.IsNaN(r.Value)) continue;
            if (r.Type == 3 && r.Value >= 0 && r.Value < 20000)
            {
                string id = "fan." + Short(r.Sensor + "|" + r.Label);
                v[id] = r.Value; Define(id, MetricGroup.Fans, r.Sensor, r.Label, Metric.Rpm, "HWiNFO shared memory");
            }
            else if (r.Type == HwInfo.TypeTemperature && r.Label.Contains("Drive Temperature", StringComparison.OrdinalIgnoreCase) && r.Value is > 0 and < 120)
            {
                string id = "hwdisk." + Short(r.Sensor + "|" + r.Label);
                v[id] = r.Value; Define(id, MetricGroup.Storage, r.Sensor, r.Label, Metric.Celsius, "HWiNFO shared memory");
            }
        }
    }

    /// <summary>Stable short id for a name (FNV-1a, 8 hex digits).</summary>
    public static string Short(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static string TopProcesses()
    {
        try
        {
            var first = new Dictionary<int, (string name, TimeSpan cpu)>();
            foreach (var p in Process.GetProcesses()) { try { first[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch { } finally { p.Dispose(); } }
            Thread.Sleep(1000);
            var deltas = new List<(string name, double pct)>();
            foreach (var p in Process.GetProcesses())
            {
                try { if (first.TryGetValue(p.Id, out var f)) deltas.Add((f.name, (p.TotalProcessorTime - f.cpu).TotalMilliseconds / 10.0 / Environment.ProcessorCount)); }
                catch { }
                finally { p.Dispose(); }
            }
            return string.Join(", ", deltas.Where(d => d.name != "Idle").OrderByDescending(d => d.pct).Take(3).Select(d => $"{d.name} {d.pct.ToString("0", CultureInfo.CurrentCulture)} %"));
        }
        catch { return ""; }
    }

    // ---- logging & export -------------------------------------------------------------------------------------------

    public static string LogFolder => Path.Combine(AppPaths.Logs, "sensors");

    private readonly object _logGate = new();

    private void WriteLog(DateTime utc, Dictionary<string, double?> v, IReadOnlyList<MetricInfo> metrics)
    {
        lock (_logGate)
        try
        {
            if (_log is null || _log.BaseStream.Length > 50L * 1024 * 1024)
            {
                CloseLog();
                Directory.CreateDirectory(LogFolder);
                LogFile = Path.Combine(LogFolder, $"sensors-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                _log = new StreamWriter(LogFile, false, new UTF8Encoding(true));
                _logIds = [.. metrics.Select(m => m.Id)];
                _log.WriteLine(Header(_logIds.Select(id => metrics.First(m => m.Id == id))));
            }
            _log.WriteLine(Row(utc, _logIds!.Select(id => v.GetValueOrDefault(id))));
            if (++_logRows % 10 == 0) _log.Flush();
        }
        catch (Exception ex) { Log.Warn("sensor log: " + ex.Message); CloseLog(); }
    }

    public void CloseLog()
    {
        lock (_logGate)
        {
            try { _log?.Flush(); _log?.Dispose(); } catch { }
            _log = null; _logIds = null;
        }
    }

    /// <summary>Writes the recorded history (or the part since <paramref name="sinceUtc"/>) as CSV: local time, then one column per metric.</summary>
    public int ExportCsv(string path, DateTime? sinceUtc = null)
    {
        var metrics = Metrics;
        var (ids, rows) = History.Snapshot(metrics.Select(m => m.Id));
        var sb = new StringBuilder();
        sb.AppendLine(Header(ids.Select(id => metrics.First(m => m.Id == id))));
        int n = 0;
        foreach (var (utc, vals) in rows)
        {
            if (sinceUtc is { } s && utc < s) continue;
            sb.AppendLine(Row(utc, vals)); n++;
        }
        AtomicFile.WriteAllText(path, sb.ToString());
        return n;
    }

    public static string Header(IEnumerable<MetricInfo> metrics) =>
        "Time," + string.Join(",", metrics.Select(m => Csv($"{m.FullName} ({m.Unit})")));

    public static string Row(DateTime utc, IEnumerable<double?> values) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture) + "," +
        string.Join(",", values.Select(x => x is { } d ? Math.Round(d, 3).ToString(CultureInfo.InvariantCulture) : ""));

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _running = false; _timer?.Dispose(); _timer = null; }
        CloseLog();
        _legacy?.Dispose(); _procs?.Dispose(); _pageReads?.Dispose();
    }
}
