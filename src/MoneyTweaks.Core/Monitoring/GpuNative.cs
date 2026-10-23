using System.Runtime.InteropServices;
using System.Text;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>One NVIDIA GPU reading from NVML. Every field is null when the driver doesn't report it.</summary>
public sealed record NvmlReading(double? LoadPercent, double? MemControllerPercent, double? TempC, double? CoreMhz, double? MemMhz,
    double? PowerW, double? PowerLimitW, double? VramUsedMb, double? VramTotalMb, double? FanPercent, ulong? ThrottleReasons);

/// <summary>
/// NVIDIA Management Library (nvml.dll), the read-only telemetry API that ships with every NVIDIA driver (nvidia-smi is
/// built on it). User-mode only: nothing is installed. Each call that a GPU doesn't support returns an error code, which
/// becomes a null reading rather than a guess.
/// </summary>
public static class Nvml
{
    private const string Dll = "nvml.dll";
    private const int Success = 0;
    // Clock-event (throttle) reason bits from nvml.h.
    public const ulong ReasonIdle = 0x1, ReasonSwPowerCap = 0x4, ReasonHwSlowdown = 0x8, ReasonSwThermal = 0x20, ReasonHwThermal = 0x40, ReasonPowerBrake = 0x80;

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] private static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int GetCount(out uint count);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")] private static extern int GetName(IntPtr device, byte[] name, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int GetUtilization(IntPtr device, out Utilization u);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr device, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] private static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] private static extern int GetMemory(IntPtr device, out Memory m);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")] private static extern int GetFan(IntPtr device, out uint percent);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] private static extern int GetThrottle(IntPtr device, out ulong reasons);

    private static bool _ready;
    private static readonly object Gate = new();
    private static List<(IntPtr handle, string name)> _devices = [];

    /// <summary>The NVIDIA GPUs NVML sees (empty without an NVIDIA driver).</summary>
    public static IReadOnlyList<(IntPtr handle, string name)> Devices { get { lock (Gate) { Start(); return _devices; } } }

    private static void Start()
    {
        if (_ready) return;
        _ready = true;
        try
        {
            if (!NativeLibrary.TryLoad(Dll, out _) && !NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, Dll), out _)) return;
            if (Init() != Success) return;
            if (GetCount(out var n) != Success) return;
            for (uint i = 0; i < n; i++)
            {
                if (GetHandle(i, out var h) != Success) continue;
                var buf = new byte[96];
                var name = GetName(h, buf, (uint)buf.Length) == Success ? Encoding.ASCII.GetString(buf, 0, Math.Max(0, Array.IndexOf(buf, (byte)0))).Trim() : $"NVIDIA GPU {i}";
                _devices.Add((h, name));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Debug("nvml unavailable: " + ex.Message);
            _devices = [];
        }
    }

    public static NvmlReading? Read(IntPtr h)
    {
        try
        {
            double? load = null, mc = null, temp = null, core = null, mem = null, pw = null, pl = null, used = null, total = null, fan = null; ulong? thr = null;
            if (GetUtilization(h, out var u) == Success) { load = u.Gpu; mc = u.Memory; }
            if (GetTemperature(h, 0, out var t) == Success && t is > 0 and < 150) temp = t;
            if (GetClock(h, 0, out var c) == Success && c > 0) core = c;
            if (GetClock(h, 2, out var m) == Success && m > 0) mem = m;
            if (GetPower(h, out var p) == Success) pw = p / 1000.0;
            if (GetPowerLimit(h, out var l) == Success && l > 0) pl = l / 1000.0;
            if (GetMemory(h, out var mi) == Success && mi.Total > 0) { used = mi.Used / 1048576.0; total = mi.Total / 1048576.0; }
            if (GetFan(h, out var f) == Success && f <= 100) fan = f;   // NOT_SUPPORTED on fanless/laptop GPUs
            if (GetThrottle(h, out var r) == Success) thr = r;
            return new NvmlReading(load, mc, temp, core, mem, pw, pl, used, total, fan, thr);
        }
        catch (Exception ex) { Log.Debug("nvml read: " + ex.Message); return null; }
    }

    /// <summary>Plain-language list of what is holding the GPU's clocks down right now (empty when nothing is).</summary>
    public static List<string> Reasons(ulong r)
    {
        var list = new List<string>();
        if ((r & (ReasonSwThermal | ReasonHwThermal)) != 0) list.Add("thermal limit");
        if ((r & (ReasonHwSlowdown | ReasonPowerBrake)) != 0) list.Add("hardware slowdown");
        if ((r & ReasonSwPowerCap) != 0) list.Add("power limit");
        return list;
    }

    public static bool IsThermal(ulong r) => (r & (ReasonSwThermal | ReasonHwThermal | ReasonHwSlowdown)) != 0;
}

/// <summary>A display adapter as DXGI reports it: name, vendor, dedicated VRAM and its LUID (which the GPU counters use).</summary>
public sealed record DxgiAdapter(string Name, uint VendorId, ulong DedicatedVideoMemory, long Luid)
{
    public string Vendor => VendorId switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", _ => "" };
    /// <summary>The LUID as the "GPU Engine" / "GPU Adapter Memory" counter instances spell it ("luid_0x00000000_0x0000d1c3").</summary>
    public string CounterKey => $"luid_0x{(uint)(Luid >> 32):x8}_0x{(uint)Luid:x8}";
}

/// <summary>Lists the hardware display adapters through DXGI (no device is created).</summary>
public static unsafe class Dxgi
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);
    private static readonly Guid FactoryGuid = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static List<DxgiAdapter> Adapters()
    {
        var list = new List<DxgiAdapter>();
        try
        {
            if (CreateDXGIFactory1(FactoryGuid, out var f) < 0 || f == IntPtr.Zero) return list;
            try
            {
                var fv = *(IntPtr**)f;
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fv[12];
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr a;
                    if (enumAdapters1(f, i, &a) < 0) break;   // DXGI_ERROR_NOT_FOUND ends the list
                    try
                    {
                        var av = *(IntPtr**)a;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)av[10];
                        var desc = stackalloc byte[312];  // DXGI_ADAPTER_DESC1
                        if (getDesc1(a, desc) < 0) continue;
                        uint flags = *(uint*)(desc + 304);
                        if ((flags & 2) != 0) continue;   // software adapter (Microsoft Basic Render Driver)
                        var name = new string((char*)desc, 0, 128).TrimEnd('\0').Trim();
                        uint vendor = *(uint*)(desc + 256);
                        ulong vram = *(ulong*)(desc + 272);
                        long luid = ((long)*(int*)(desc + 300) << 32) | *(uint*)(desc + 296);
                        list.Add(new DxgiAdapter(name, vendor, vram, luid));
                    }
                    finally { Marshal.Release(a); }
                }
            }
            finally { Marshal.Release(f); }
        }
        catch (Exception ex) { Log.Debug("dxgi: " + ex.Message); }
        return list;
    }
}
