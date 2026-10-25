using System.Diagnostics;
using System.Globalization;

namespace MoneyTweaks.Linux;

/// <summary>A temperature from /sys/class/hwmon: chip driver name, sensor label and the file it came from.</summary>
public sealed record HwmonTemp(string Chip, string Label, string Path, double Celsius);

/// <summary>One sample. Null = this PC doesn't provide the reading (the matching *Source says where it would come from).</summary>
public sealed record Snapshot(
    double? CpuPercent, IReadOnlyList<double> CorePercent, double? CpuTempC, string? CpuTempSource, double? CpuMhz,
    long MemTotalKb, long MemAvailableKb, double? GpuPercent, double? GpuTempC, string? GpuSource,
    double NetDownKBps, double NetUpKBps, TimeSpan? Uptime, DateTime At)
{
    /// <summary>/proc/stat could be read (CPU usage appears from the second sample).</summary>
    public bool CpuReadable { get; init; }
    /// <summary>/proc/net/dev could be read.</summary>
    public bool NetReadable { get; init; }
    public bool MemReadable => MemTotalKb > 0;
    public double MemUsedPercent => MemTotalKb > 0 ? 100.0 * (MemTotalKb - MemAvailableKb) / MemTotalKb : 0;
}

/// <summary>Parsers for the kernel's text formats. Pure functions, tested with real file contents.</summary>
public static class Proc
{
    /// <summary>/proc/stat "cpu" lines: (busy, total) jiffies for the total (index 0) and each core.</summary>
    public static List<(long busy, long total)> CpuTimes(string stat)
    {
        var list = new List<(long, long)>();
        foreach (var line in stat.Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // user nice system idle iowait irq softirq steal (guest time is already inside user/nice)
            var n = f.Skip(1).Take(8).Select(x => long.TryParse(x, out var v) ? v : 0).ToArray();
            if (n.Length < 4) continue;
            long idle = n[3] + (n.Length > 4 ? n[4] : 0), total = n.Sum();
            list.Add((total - idle, total));
        }
        return list;
    }

    public static double Percent((long busy, long total) a, (long busy, long total) b)
    {
        long dt = b.total - a.total;
        return dt <= 0 ? 0 : Math.Clamp(100.0 * (b.busy - a.busy) / dt, 0, 100);
    }

    /// <summary>/proc/meminfo MemTotal and MemAvailable in kB.</summary>
    public static (long total, long available) Memory(string meminfo)
    {
        long Get(string key)
        {
            foreach (var line in meminfo.Split('\n'))
                if (line.StartsWith(key + ":", StringComparison.Ordinal))
                    return long.TryParse(line[(key.Length + 1)..].Trim().Split(' ')[0], out var v) ? v : 0;
            return 0;
        }
        return (Get("MemTotal"), Get("MemAvailable"));
    }

    /// <summary>/proc/net/dev: received and sent bytes over all interfaces except loopback.</summary>
    public static (long rx, long tx) NetBytes(string netdev)
    {
        long rx = 0, tx = 0;
        foreach (var line in netdev.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (line[..colon].Trim() == "lo") continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            rx += long.TryParse(f[0], out var r) ? r : 0;
            tx += long.TryParse(f[8], out var t) ? t : 0;
        }
        return (rx, tx);
    }

    /// <summary>Every temperature under /sys/class/hwmon with its chip and label.</summary>
    public static List<HwmonTemp> HwmonTemps(LinuxFs fs)
    {
        var list = new List<HwmonTemp>();
        foreach (var dir in fs.List("/sys/class/hwmon", @"hwmon\d+"))
        {
            string chip = fs.ReadLine(dir + "/name") ?? "";
            foreach (var input in fs.List(dir, @"temp\d+_input"))
            {
                if (fs.ReadLong(input) is not { } milli) continue;
                double c = milli / 1000.0;
                if (c is <= 0 or >= 150) continue;   // unconnected sensors report 0 or nonsense
                string label = fs.ReadLine(input.Replace("_input", "_label")) ?? System.IO.Path.GetFileName(input).Replace("_input", "");
                list.Add(new(chip, label, input, c));
            }
        }
        return list;
    }

    /// <summary>The CPU package temperature: AMD k10temp Tctl/Tdie, zenpower, Intel coretemp package, or the x86 package thermal zone.</summary>
    public static HwmonTemp? CpuTemp(IReadOnlyList<HwmonTemp> temps, LinuxFs fs)
    {
        string[] order = ["k10temp:Tctl", "k10temp:Tdie", "zenpower:Tdie", "zenpower:Tctl", "coretemp:Package id 0", "cpu_thermal:temp1", "k10temp:temp1"];
        foreach (var key in order)
            if (temps.FirstOrDefault(t => $"{t.Chip}:{t.Label}" == key) is { } hit) return hit;
        foreach (var zone in fs.List("/sys/class/thermal", @"thermal_zone\d+"))
            if (fs.ReadLine(zone + "/type") == "x86_pkg_temp" && fs.ReadLong(zone + "/temp") is { } m && m > 0)
                return new("x86_pkg_temp", "package", zone + "/temp", m / 1000.0);
        return null;
    }

    /// <summary>/proc/uptime: seconds since boot.</summary>
    public static TimeSpan? Uptime(string? uptime) =>
        uptime is not null && double.TryParse(uptime.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;

    /// <summary>"utilization, temperature" from nvidia-smi's CSV output (first GPU).</summary>
    public static (double util, double temp)? NvidiaSmi(string csv)
    {
        var f = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',');
        if (f is not { Length: >= 2 }) return null;
        return double.TryParse(f[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var u)
            && double.TryParse(f[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? (u, t) : null;
    }
}

/// <summary>Takes samples; keeps the previous CPU and network counters to turn them into rates.</summary>
public sealed class Sampler(LinuxFs fs)
{
    private List<(long busy, long total)>? _cpuPrev;
    private (long rx, long tx)? _netPrev;
    private DateTime _netAt;
    private DateTime _nvAt = DateTime.MinValue;
    private (double util, double temp)? _nv;
    private bool? _hasNvidiaSmi;

    public Snapshot Sample()
    {
        var now = DateTime.UtcNow;
        double? cpu = null; var cores = new List<double>();
        string? statText = fs.Read("/proc/stat"), netText = fs.Read("/proc/net/dev");
        if (statText is { } stat)
        {
            var cur = Proc.CpuTimes(stat);
            if (_cpuPrev is { } prev && prev.Count == cur.Count && cur.Count > 0)
            {
                cpu = Proc.Percent(prev[0], cur[0]);
                for (int i = 1; i < cur.Count; i++) cores.Add(Proc.Percent(prev[i], cur[i]));
            }
            _cpuPrev = cur;
        }
        var temps = Proc.HwmonTemps(fs);
        var cpuTemp = Proc.CpuTemp(temps, fs);
        var freqs = fs.List("/sys/devices/system/cpu", @"cpu\d+").Select(c => fs.ReadLong(c + "/cpufreq/scaling_cur_freq")).OfType<long>().ToList();
        var (memTotal, memAvail) = fs.Read("/proc/meminfo") is { } mi ? Proc.Memory(mi) : (0, 0);

        double down = 0, up = 0;
        if (netText is { } nd)
        {
            var n = Proc.NetBytes(nd);
            if (_netPrev is { } p)
            {
                double secs = Math.Max(0.001, (now - _netAt).TotalSeconds);
                down = Math.Max(0, (n.rx - p.rx) / 1024.0 / secs); up = Math.Max(0, (n.tx - p.tx) / 1024.0 / secs);
            }
            _netPrev = n; _netAt = now;
        }

        // GPU: AMD/Intel through amdgpu's sysfs files; NVIDIA through its nvidia-smi tool (every 2 s, it takes ~50 ms).
        double? gpu = null, gpuTemp = null; string? gpuSrc = null;
        foreach (var card in fs.List("/sys/class/drm", @"card\d+"))
            if (fs.ReadLong(card + "/device/gpu_busy_percent") is { } busy) { gpu = busy; gpuSrc = card + "/device/gpu_busy_percent"; break; }
        if (temps.FirstOrDefault(t => t.Chip == "amdgpu" && t.Label is "edge" or "junction") is { } at) gpuTemp = at.Celsius;
        if (gpu is null && fs.Root == "/" && NvidiaSmiPresent())
        {
            if (now - _nvAt > TimeSpan.FromSeconds(2)) { _nvAt = now; _nv = QueryNvidia(); }
            if (_nv is { } nv) { gpu = nv.util; gpuTemp = nv.temp; gpuSrc = "nvidia-smi"; }
        }
        return new(cpu, cores, cpuTemp?.Celsius, cpuTemp is null ? null : $"{cpuTemp.Chip} {cpuTemp.Label} ({cpuTemp.Path})",
            freqs.Count > 0 ? freqs.Average() / 1000.0 : null, memTotal, memAvail, gpu, gpuTemp, gpuSrc, down, up,
            Proc.Uptime(fs.Read("/proc/uptime")), now) { CpuReadable = statText is not null, NetReadable = netText is not null };
    }

    private bool NvidiaSmiPresent() => _hasNvidiaSmi ??= Which("nvidia-smi") is not null;

    private static (double, double)? QueryNvidia()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Which("nvidia-smi")!, "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            string output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(3000) && p.ExitCode == 0 ? Proc.NvidiaSmi(output) : null;
        }
        catch { return null; }
    }

    /// <summary>The full path of a program on PATH, or null.</summary>
    public static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':').Select(d => System.IO.Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
