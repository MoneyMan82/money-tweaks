using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed record HardwareSnapshot(
    double CpuPercent, double RamUsedGb, double RamTotalGb, double RamPercent,
    double? GpuPercent, double? DiskPercent, double NetDownKBps, double NetUpKBps,
    double? CpuTempC, int ProcessCount, TimeSpan Uptime)
{
    /// <summary>NVIDIA GPU temperature through NVAPI (null on other GPUs or if the driver doesn't report it).</summary>
    public double? GpuTempC { get; init; }
}

public sealed record SystemSpecs(string Os, string Cpu, int Cores, int Threads, string Gpus, double RamGb,
    int? RamRatedMts, int? RamConfiguredMts, string Motherboard, string Bios);

/// <summary>
/// Live system counters through Windows performance counters and WMI. No kernel driver is installed, so CPU
/// temperature is only shown when the firmware exposes an ACPI thermal zone (many desktops don't).
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly PerformanceCounter? _cpu;
    private readonly PerformanceCounter? _disk;
    private readonly PerformanceCounter? _procs;
    private NetworkInterface[] _nics = [];
    private DateTime _nicsAt = DateTime.MinValue;
    private DateTime _tempAt = DateTime.MinValue;
    private double? _lastTemp;
    private PerformanceCounterCategory? _gpuCat;
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = [];
    private long _lastRx, _lastTx;
    private DateTime _lastNet;
    private DateTime _lastGpuRefresh = DateTime.MinValue;
    private bool _tempUnavailable;
    private bool _gpuTempUnavailable = !NvApi.DriverPresent;
    private DateTime _gpuTempAt = DateTime.MinValue;
    private double? _lastGpuTemp;

    public HardwareMonitor()
    {
        try { _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); _cpu.NextValue(); }
        catch { try { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpu.NextValue(); } catch { _cpu = null; } }
        try { _disk = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total"); _disk.NextValue(); } catch { _disk = null; }
        try { _gpuCat = new PerformanceCounterCategory("GPU Engine"); } catch { _gpuCat = null; }
        // Process count from a counter: Process.GetProcesses() every second allocated ~300 undisposed Process objects.
        try { _procs = new PerformanceCounter("System", "Processes"); _procs.NextValue(); } catch { _procs = null; }
        (_lastRx, _lastTx) = NetBytes();
        _lastNet = DateTime.UtcNow;
    }

    private (long rx, long tx) NetBytes()
    {
        long rx = 0, tx = 0;
        // Re-enumerating adapters is slow (WMI/IP helper); the adapter list rarely changes, so refresh it every 15 s.
        if (DateTime.UtcNow - _nicsAt > TimeSpan.FromSeconds(15)) { _nics = NetworkInterface.GetAllNetworkInterfaces(); _nicsAt = DateTime.UtcNow; }
        foreach (var n in _nics)
        {
            if (!CountsTraffic(n)) continue;
            try { var s = n.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; } catch { }
        }
        return (rx, tx);
    }

    /// <summary>
    /// True for an adapter whose traffic should be counted: up, not loopback or a tunnel, and not one of the per-adapter
    /// filter-driver layers Windows also lists ("Ethernet-QoS Packet Scheduler-0000", "…-WFP Native MAC Layer LightWeight
    /// Filter-0000"), which carry the same bytes as the adapter itself and would count them two to four times over.
    /// </summary>
    public static bool CountsTraffic(NetworkInterface n) =>
        n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        && !System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"-\d{4}$");

    private double? GpuPercent()
    {
        if (_gpuCat is null) return null;
        try
        {
            if (DateTime.UtcNow - _lastGpuRefresh > TimeSpan.FromSeconds(10))
            {
                _lastGpuRefresh = DateTime.UtcNow;
                var names = _gpuCat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)).ToHashSet();
                foreach (var stale in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList()) { _gpuCounters[stale].Dispose(); _gpuCounters.Remove(stale); }
                foreach (var n in names.Where(n => !_gpuCounters.ContainsKey(n)))
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n);
                    c.NextValue();
                    _gpuCounters[n] = c;
                }
            }
            double sum = 0;
            foreach (var c in _gpuCounters.Values) { try { sum += c.NextValue(); } catch { } }
            return Math.Min(100, sum);
        }
        catch { return null; }
    }

    private double? CpuTemp()
    {
        if (!_tempUnavailable)
        {
            if (DateTime.UtcNow - _tempAt < TimeSpan.FromSeconds(5)) return _lastTemp; // WMI is expensive; temps change slowly
            _tempAt = DateTime.UtcNow;
            _lastTemp = ReadTemp();
            if (_lastTemp is not null) return _lastTemp;
        }
        // Firmware doesn't expose it (common on desktop boards): use the CPU temperature MSI Afterburner or HWiNFO publish
        // when they run (their documented shared memory; they read the sensor with their own drivers, Money Tweaks installs none).
        return Tuning.Afterburner.CpuTemperature() ?? Tuning.HwInfo.CpuTemperature();
    }

    /// <summary>CPU temperature from the firmware (ACPI), MSI Afterburner or HWiNFO, or null. Cached 5 s.</summary>
    public double? CpuTemperature() => CpuTemp();

    /// <summary>Where the CPU temperature comes from right now.</summary>
    public static string CpuTempSource(double? t) => t is null ? "not available"
        : Tuning.Afterburner.CpuTemperature() is not null ? "MSI Afterburner"
        : Tuning.HwInfo.CpuTemperature() is not null ? "HWiNFO" : "firmware (ACPI)";

    /// <summary>What to tell the user when no CPU temperature is available.</summary>
    public const string CpuTempHint = "start MSI Afterburner, or HWiNFO with Shared Memory Support on, to read it";

    private double? ReadTemp()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var o in s.Get())
            {
                var k = Convert.ToDouble(o["CurrentTemperature"]);
                var c = k / 10.0 - 273.15;
                if (c is > 5 and < 125) return Math.Round(c, 1);
            }
        }
        catch { }
        _tempUnavailable = true;
        return null;
    }

    public HardwareSnapshot Sample()
    {
        var (total, avail, load) = MemoryInfo.Status();
        var now = DateTime.UtcNow;
        var (rx, tx) = NetBytes();
        var secs = Math.Max(0.001, (now - _lastNet).TotalSeconds);
        var down = Math.Max(0, (rx - _lastRx) / 1024.0 / secs);
        var up = Math.Max(0, (tx - _lastTx) / 1024.0 / secs);
        (_lastRx, _lastTx, _lastNet) = (rx, tx, now);
        double? disk = null;
        try { if (_disk is not null) disk = Math.Clamp(100 - _disk.NextValue(), 0, 100); } catch { }
        double cpu = 0;
        try { cpu = Math.Clamp(_cpu?.NextValue() ?? 0, 0, 100); } catch { }
        return new HardwareSnapshot(cpu, (total - avail) / 1073741824.0, total / 1073741824.0, load,
            GpuPercent(), disk, down, up, CpuTemp(), ProcessCount(), TimeSpan.FromMilliseconds(Environment.TickCount64)) { GpuTempC = GpuTemp() };
    }

    private double? GpuTemp()
    {
        if (_gpuTempUnavailable) return null;
        if (DateTime.UtcNow - _gpuTempAt < TimeSpan.FromSeconds(2)) return _lastGpuTemp;
        _gpuTempAt = DateTime.UtcNow;
        _lastGpuTemp = NvApi.GpuTemperature();
        if (_lastGpuTemp is null) _gpuTempUnavailable = true; // don't keep asking a driver that can't answer
        return _lastGpuTemp;
    }

    private int ProcessCount()
    {
        try { if (_procs is not null) return (int)_procs.NextValue(); } catch { }
        var all = Process.GetProcesses();
        foreach (var p in all) p.Dispose();
        return all.Length;
    }

    /// <summary>Installed memory modules and the board's slot count from the firmware's SMBIOS tables (0 = unknown).</summary>
    public static (int Modules, int Slots) MemoryLayout()
    {
        int modules = 0, slots = 0;
        try { using var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"); modules = s.Get().Count; } catch { }
        try
        {
            using var a = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            foreach (var o in a.Get()) slots += Convert.ToInt32(o["MemoryDevices"] ?? 0);
        }
        catch { }
        return (modules, slots);
    }

    public static SystemSpecs ReadSpecs()
    {
        string Q(string cls, string prop, string scope = @"root\cimv2")
        {
            try
            {
                using var s = new ManagementObjectSearcher(scope, $"SELECT {prop} FROM {cls}");
                return string.Join(" + ", s.Get().Cast<ManagementBaseObject>().Select(o => o[prop]?.ToString()?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct());
            }
            catch { return "Unknown"; }
        }
        int? rated = null, configured = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            foreach (var o in s.Get())
            {
                if (o["Speed"] is uint sp && sp > 0) rated = (int)Math.Max(rated ?? 0, sp);
                if (o["ConfiguredClockSpeed"] is uint cc && cc > 0) configured = (int)Math.Max(configured ?? 0, cc);
            }
        }
        catch { }
        var (total, _, _) = MemoryInfo.Status();
        return new SystemSpecs(OsInfo.ProductName, Q("Win32_Processor", "Name"), Environment.ProcessorCount / Math.Max(1, HyperThreadFactor()), Environment.ProcessorCount,
            Q("Win32_VideoController", "Name"), Math.Round(total / 1073741824.0, 1), rated, configured,
            $"{Q("Win32_BaseBoard", "Manufacturer")} {Q("Win32_BaseBoard", "Product")}", Q("Win32_BIOS", "SMBIOSBIOSVersion"));
    }

    private static int HyperThreadFactor()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            int cores = 0, logical = 0;
            foreach (var o in s.Get()) { cores += Convert.ToInt32(o["NumberOfCores"]); logical += Convert.ToInt32(o["NumberOfLogicalProcessors"]); }
            return cores > 0 ? Math.Max(1, logical / cores) : 1;
        }
        catch { return 1; }
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        _disk?.Dispose();
        _procs?.Dispose();
        foreach (var c in _gpuCounters.Values) c.Dispose();
    }
}
