using System.Globalization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>What nvidia-smi reports about one NVIDIA GPU's tunable limits.</summary>
public sealed record NvidiaGpuLimits(int Index, string Name, string Driver,
    double? PowerLimitW, double? DefaultPowerLimitW, double? MinPowerLimitW, double? MaxPowerLimitW,
    IReadOnlyList<int> GraphicsClocks, IReadOnlyList<int> MemoryClocks, bool MemoryLockSupported)
{
    public bool PowerLimitSupported => MinPowerLimitW is > 0 && MaxPowerLimitW > MinPowerLimitW;
    public bool ClockLockSupported => GraphicsClocks.Count > 1;
    public int? MinGraphics => GraphicsClocks.Count > 0 ? GraphicsClocks.Min() : null;
    public int? MaxGraphics => GraphicsClocks.Count > 0 ? GraphicsClocks.Max() : null;
    /// <summary>Step between supported graphics clocks (15 MHz on Ada/Ampere).</summary>
    public int GraphicsStep => GraphicsClocks.Count > 1 ? GraphicsClocks.Zip(GraphicsClocks.Skip(1), (a, b) => Math.Abs(a - b)).Where(d => d > 0).DefaultIfEmpty(15).Min() : 15;
}

/// <summary>Live GPU readings used by the tuning monitor and stability tests.</summary>
public sealed record NvidiaGpuReading(double? TempC, double? PowerW, double? PowerLimitW, int? CoreMhz, int? MemMhz, int? LoadPercent, ulong? ThrottleReasons)
{
    /// <summary>Thermal or power-brake throttling (hardware slowdown, thermal slowdown, HW power brake).</summary>
    public bool HardwareThrottling => ThrottleReasons is { } r && (r & (0x8UL | 0x20UL | 0x40UL | 0x80UL)) != 0;
    public bool PowerCapped => ThrottleReasons is { } r && (r & 0x4UL) != 0;
}

/// <summary>
/// NVIDIA's own command-line tool (nvidia-smi, installed with the driver) for the GeForce/RTX controls NVIDIA documents
/// publicly: power limit (-pl, within the driver's min/max), locked GPU clocks (-lgc/-rgc) and locked memory clocks
/// (-lmc/-rmc). Changes need administrator rights and are not persistent: the driver drops them at restart. Clock offsets,
/// voltage/frequency curves and fan control are not in nvidia-smi for GeForce, so Money Tweaks doesn't offer them.
/// </summary>
public static class NvidiaSmi
{
    public static string? Path()
    {
        foreach (var p in new[]
                 {
                     System.IO.Path.Combine(ProcessRunner.System32, "nvidia-smi.exe"),
                     System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe"),
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    public static bool Available => Path() is not null;

    private static ProcessResult Run(string args, int timeoutMs = 15_000) =>
        Path() is { } p ? ProcessRunner.Run(p, args, timeoutMs) : new ProcessResult(-1, "", "nvidia-smi was not found (NVIDIA driver not installed).");

    // ---- read ------------------------------------------------------------------------------------------------------

    public static List<NvidiaGpuLimits> Limits()
    {
        var list = new List<NvidiaGpuLimits>();
        var q = Run("--query-gpu=index,name,driver_version,power.limit,power.default_limit,power.min_limit,power.max_limit --format=csv,noheader,nounits");
        if (!q.Ok) return list;
        foreach (var line in q.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var c = line.Split(',').Select(x => x.Trim()).ToArray();
            if (c.Length < 7 || !int.TryParse(c[0], out var idx)) continue;
            var (gfx, mem) = SupportedClocks(idx);
            var lmci = Run($"-i {idx} -lmci");
            list.Add(new NvidiaGpuLimits(idx, c[1], c[2], Num(c[3]), Num(c[4]), Num(c[5]), Num(c[6]), gfx, mem,
                lmci.Ok && !lmci.StdOut.Contains("not supported", StringComparison.OrdinalIgnoreCase) && mem.Count > 0));
        }
        return list;
    }

    /// <summary>Supported graphics/memory clocks from "nvidia-smi -q -d SUPPORTED_CLOCKS" (the values -lgc/-lmc accept).</summary>
    public static (List<int> graphics, List<int> memory) SupportedClocks(int index)
    {
        var r = Run($"-i {index} -q -d SUPPORTED_CLOCKS");
        return r.Ok ? ParseSupportedClocks(r.StdOut) : ([], []);
    }

    public static (List<int> graphics, List<int> memory) ParseSupportedClocks(string text)
    {
        var gfx = new SortedSet<int>(); var mem = new SortedSet<int>();
        foreach (Match m in Regex.Matches(text, @"^\s*(Memory|Graphics)\s*:\s*(\d+)\s*MHz", RegexOptions.Multiline))
            (m.Groups[1].Value == "Memory" ? mem : gfx).Add(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return (gfx.ToList(), mem.ToList());
    }

    public static NvidiaGpuReading? Read(int index)
    {
        var r = Run($"-i {index} --query-gpu=temperature.gpu,power.draw,power.limit,clocks.gr,clocks.mem,utilization.gpu,clocks_event_reasons.active --format=csv,noheader,nounits", 5000);
        if (!r.Ok) return null;
        var c = r.StdOut.Trim().Split(',').Select(x => x.Trim()).ToArray();
        if (c.Length < 7) return null;
        ulong? reasons = c[6].StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(c[6][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
        return new(Num(c[0]), Num(c[1]), Num(c[2]), (int?)Num(c[3]), (int?)Num(c[4]), (int?)Num(c[5]), reasons);
    }

    /// <summary>Video memory in use and total, in MB.</summary>
    public static (double usedMb, double totalMb)? ReadMemory(int index)
    {
        var r = Run($"-i {index} --query-gpu=memory.used,memory.total --format=csv,noheader,nounits", 5000);
        if (!r.Ok) return null;
        var c = r.StdOut.Trim().Split(',').Select(x => x.Trim()).ToArray();
        return c.Length >= 2 && Num(c[0]) is { } u && Num(c[1]) is { } t ? (u, t) : null;
    }

    private static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // ---- write (admin) ---------------------------------------------------------------------------------------------

    public static (bool ok, string message) SetPowerLimit(int index, double watts) => Exec($"-i {index} -pl {watts.ToString("0.##", CultureInfo.InvariantCulture)}");
    public static (bool ok, string message) LockGpuClocks(int index, int min, int max) => Exec($"-i {index} -lgc {min},{max}");
    public static (bool ok, string message) ResetGpuClocks(int index) => Exec($"-i {index} -rgc");
    public static (bool ok, string message) LockMemoryClocks(int index, int min, int max) => Exec($"-i {index} -lmc {min},{max}");
    public static (bool ok, string message) ResetMemoryClocks(int index) => Exec($"-i {index} -rmc");

    private static (bool, string) Exec(string args)
    {
        if (!Admin.IsElevated) return (false, "Needs administrator rights (nvidia-smi refuses GPU changes otherwise).");
        var r = Run(args);
        var text = r.Combined.Replace("All done.", "").Trim();
        Log.Info($"nvidia-smi {args} -> {r.ExitCode} {text}");
        // nvidia-smi can exit 0 while printing a refusal for one GPU; treat those words as failure too.
        bool refused = Regex.IsMatch(r.Combined, @"not supported|insufficient permissions|failed|invalid|not allowed", RegexOptions.IgnoreCase);
        return (r.Ok && !refused, text.Length == 0 ? "Accepted by the driver." : text);
    }
}
