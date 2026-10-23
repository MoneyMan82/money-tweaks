using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>
/// LibreHardwareMonitorLib (MPL-2.0, https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) as a sensor source for
/// <see cref="SensorHub"/>. Used read-only: nothing is written to hardware (no fan control).
///
/// Low-level CPU and motherboard sensors (package and core temperatures, package power, core clocks and voltages, Super I/O
/// temperatures and fans) go through the PawnIO driver, which this library uses when it is installed and Money Tweaks
/// runs as administrator. Money Tweaks never installs it. Without it the library still reports those sensors, but as
/// placeholders (0 °C, 0 W, a constant 1.55 V), so they are only taken while the driver path is active, and zero
/// values are dropped even then. GPU sensors (NVIDIA through NVAPI/NVML, AMD through ADL) need no driver and are always
/// used. Everything runs on the hub's sampler thread; expensive hardware is refreshed less often.
/// </summary>
public sealed class LhmSource : IDisposable
{
    private Computer? _computer;
    private readonly object _gate = new();
    private readonly Dictionary<IHardware, long> _nextUpdate = [];
    private bool _failed;
    public string Status { get; private set; } = "Not started.";

    /// <summary>True when CPU/motherboard sensors can be real: PawnIO installed and this process elevated.</summary>
    public static bool DriverActive => PawnIoInstalled && Admin.IsElevated;

    public static bool PawnIoInstalled
    {
        get { try { return PawnIo.IsInstalled; } catch { return false; } }
    }

    /// <summary>Why low-level CPU and motherboard sensors are missing (null when they are available).</summary>
    public static string? DriverReason => DriverActive ? null
        : !PawnIoInstalled ? "needs the PawnIO driver, which isn't installed (Money Tweaks never installs drivers; LibreHardwareMonitor and other tools offer it), and administrator rights"
        : "needs administrator rights: the PawnIO driver is installed, restart Money Tweaks as administrator to read it";

    private Computer? Open()
    {
        if (_computer is not null || _failed) return _computer;
        var sw = Stopwatch.StartNew();
        try
        {
            bool driver = DriverActive;
            var c = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = driver,
                // Memory, storage and network: Money Tweaks reads these itself (no duplicate polling). No controllers,
                // PSUs or batteries: they open USB/HID devices other apps may be using.
                IsMemoryEnabled = false, IsStorageEnabled = false, IsNetworkEnabled = false, IsControllerEnabled = false, IsPsuEnabled = false, IsBatteryEnabled = false,
            };
            c.Open();
            _computer = c;
            Status = $"LibreHardwareMonitor {typeof(Computer).Assembly.GetName().Version?.ToString(3)}: {c.Hardware.Count} devices, {(driver ? "PawnIO driver active" : "without driver (GPU sensors only)")}; opened in {sw.ElapsedMilliseconds} ms.";
            Log.Info("Sensors: " + Status);
        }
        catch (Exception ex)
        {
            _failed = true;
            Status = "LibreHardwareMonitor couldn't start: " + ex.Message;
            Log.Warn(Status);
        }
        return _computer;
    }

    /// <summary>Refresh interval per device: CPUs every sample (cheap), GPUs every few seconds (NVIDIA's refresh is costly).</summary>
    private static int IntervalMs(IHardware h) => h.HardwareType switch
    {
        HardwareType.Cpu => 0,
        HardwareType.GpuNvidia => 3000,
        HardwareType.GpuAmd or HardwareType.GpuIntel => 2000,
        _ => 2000,
    };

    private static void UpdateTree(IHardware h) { h.Update(); foreach (var s in h.SubHardware) UpdateTree(s); }

    /// <summary>Real readings from the library, keyed by device: (hardware type, device name, sensor type, sensor name, value).</summary>
    internal List<(HardwareType hw, string device, SensorType type, string name, double value)> Read()
    {
        var list = new List<(HardwareType, string, SensorType, string, double)>();
        lock (_gate)
        {
            var c = Open();
            if (c is null) return list;
            long now = Environment.TickCount64;
            bool driver = DriverActive;
            foreach (var h in c.Hardware)
            {
                try
                {
                    if (now >= _nextUpdate.GetValueOrDefault(h)) { UpdateTree(h); _nextUpdate[h] = now + IntervalMs(h); }
                    Collect(h, h.HardwareType, h.Name, driver, list);
                }
                catch (Exception ex) { Log.Debug($"lhm {h.Name}: {ex.Message}"); }
            }
        }
        return list;
    }

    private static void Collect(IHardware h, HardwareType top, string device, bool driver, List<(HardwareType, string, SensorType, string, double)> list)
    {
        bool lowLevel = top is HardwareType.Cpu or HardwareType.Motherboard or HardwareType.SuperIO;
        foreach (var s in h.Sensors)
        {
            if (s.Value is not { } f || float.IsNaN(f) || float.IsInfinity(f)) continue;
            double v = f;
            // CPU load comes from Windows' counters (same source); the library's GPU engine loads duplicate Windows' too.
            if (s.SensorType is SensorType.Load or SensorType.Throughput or SensorType.Data or SensorType.SmallData or SensorType.Factor or SensorType.Control) continue;
            if (lowLevel && !driver) continue;   // placeholders without the driver
            bool ok = s.SensorType switch
            {
                SensorType.Temperature => v is > 0 and < 150,
                SensorType.Clock => v is > 0 and < 20000,
                SensorType.Power => v is > 0 and < 2000,
                SensorType.Voltage => v is > 0 and < 20,
                SensorType.Fan => v is >= 0 and < 20000,   // 0 RPM is real: fans stop at low temperatures
                _ => false,
            };
            if (ok) list.Add((top, h.Parent is null ? device : $"{device} · {h.Name}", s.SensorType, s.Name, v));
        }
        foreach (var sub in h.SubHardware) Collect(sub, top, device, driver, list);
    }

    public static string Slug(string s) => new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

    /// <summary>Releases the library's handles (the driver, NVAPI/ADL). Reopened on the next read.</summary>
    public void Close()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch (Exception ex) { Log.Debug("lhm close: " + ex.Message); }
            _computer = null;
            _nextUpdate.Clear();
        }
    }

    public void Dispose() => Close();

    internal static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
