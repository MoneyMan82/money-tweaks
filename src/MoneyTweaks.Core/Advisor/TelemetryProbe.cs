using System.Diagnostics;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Core.Advisor;

/// <summary>
/// Lightweight per-second telemetry from Windows performance counters (per-core CPU, CPU clock vs. base, commit, hard
/// page faults, disk queue/latency, GPU engine load and dedicated memory), nvidia-smi on NVIDIA (every 2 s: load, clocks,
/// power vs. limit, throttle reasons, VRAM), CPU temperature from Afterburner/HWiNFO when running, and the game's own CPU
/// share. Nothing is injected into the game.
/// </summary>
public sealed class TelemetryProbe : IDisposable
{
    private readonly List<PerformanceCounter> _cores = [];
    private readonly PerformanceCounter? _total, _perf, _commit, _faults, _queue, _latency;
    private readonly List<PerformanceCounter> _vram = [];
    private readonly HardwareMonitor _hm = new();
    private readonly int? _gamePid;
    private readonly bool _nvidia;
    private TimeSpan _gameCpu0;
    private DateTime _t0 = DateTime.UtcNow;
    private int _tick;
    private NvidiaGpuReading? _gpu;
    private (double usedMb, double totalMb)? _gpuMem;

    private static PerformanceCounter? Counter(string cat, string name, string inst = "")
    {
        try { var c = new PerformanceCounter(cat, name, inst, readOnly: true); c.NextValue(); return c; } catch { return null; }
    }

    public TelemetryProbe(int? gamePid)
    {
        _gamePid = gamePid;
        _nvidia = NvidiaSmi.Available;
        try
        {
            foreach (var inst in new PerformanceCounterCategory("Processor Information").GetInstanceNames()
                         .Where(i => !i.Contains("_Total", StringComparison.Ordinal)).OrderBy(i => i, StringComparer.Ordinal))
                if (Counter("Processor Information", "% Processor Time", inst) is { } c) _cores.Add(c);
        }
        catch (Exception ex) { Log.Debug("per-core counters: " + ex.Message); }
        _total = Counter("Processor Information", "% Processor Time", "_Total");
        _perf = Counter("Processor Information", "% Processor Performance", "_Total");
        _commit = Counter("Memory", "% Committed Bytes In Use");
        _faults = Counter("Memory", "Pages Input/sec");
        _queue = Counter("PhysicalDisk", "Avg. Disk Queue Length", "_Total");
        _latency = Counter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");
        if (!_nvidia)
            try
            {
                foreach (var inst in new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames())
                    if (Counter("GPU Adapter Memory", "Dedicated Usage", inst) is { } c) _vram.Add(c);
            }
            catch { }
        if (gamePid is { } pid) try { using var p = Process.GetProcessById(pid); _gameCpu0 = p.TotalProcessorTime; } catch { }
        _t0 = DateTime.UtcNow;
    }

    private static double? V(PerformanceCounter? c) { try { return c?.NextValue(); } catch { return null; } }

    public TelemetrySample Sample(int frames)
    {
        var cores = _cores.Select(c => V(c) ?? 0).ToList();
        double total = V(_total) ?? (cores.Count > 0 ? cores.Average() : 0);
        var hw = _hm.Sample();
        if (_nvidia && _tick++ % 2 == 0)
        {
            _gpu = NvidiaSmi.Read(0);
            _gpuMem = NvidiaSmi.ReadMemory(0);
        }
        double? gameCpu = null;
        var now = DateTime.UtcNow;
        if (_gamePid is { } pid)
            try
            {
                using var p = Process.GetProcessById(pid);
                var t = p.TotalProcessorTime;
                gameCpu = (t - _gameCpu0).TotalMilliseconds / Math.Max(1, (now - _t0).TotalMilliseconds * Environment.ProcessorCount) * 100;
                _gameCpu0 = t;
            }
            catch { }
        _t0 = now;
        double? vramUsed = _nvidia ? _gpuMem?.usedMb : _vram.Count > 0 ? _vram.Sum(c => V(c) ?? 0) / 1048576.0 : null;
        var cpuTemp = Afterburner.CpuTemperature() ?? HwInfo.CpuTemperature() ?? hw.CpuTempC;
        var (totalRam, avail, _) = MemoryInfo.Status();
        return new TelemetrySample(
            total, cores.Count > 0 ? cores.Max() : total, V(_perf), cpuTemp,
            _nvidia ? _gpu?.LoadPercent : hw.GpuPercent, _gpu?.CoreMhz, _gpu?.TempC ?? hw.GpuTempC, _gpu?.PowerW, _gpu?.PowerLimitW,
            _gpu?.PowerCapped == true, _gpu is { } g && (g.ThrottleReasons is { } r && (r & (0x20UL | 0x40UL)) != 0),
            vramUsed, _nvidia ? _gpuMem?.totalMb : null,
            avail / 1048576.0, totalRam / 1048576.0, V(_commit), V(_faults),
            V(_queue), V(_latency) is { } l ? l * 1000 : null,
            gameCpu, gameCpu is { } gc ? Math.Max(0, total - gc) : null, frames);
    }

    public void Dispose()
    {
        foreach (var c in _cores) c.Dispose();
        foreach (var c in _vram) c.Dispose();
        _total?.Dispose(); _perf?.Dispose(); _commit?.Dispose(); _faults?.Dispose(); _queue?.Dispose(); _latency?.Dispose();
        _hm.Dispose();
    }
}

/// <summary>Captures telemetry while you play: frame times from the game's Present events (administrator) plus the probe.</summary>
public static class GameCapture
{
    public static async Task<Capture> RunAsync(int pid, string game, int seconds, double refreshHz, IProgress<(int second, TelemetrySample s)>? progress, CancellationToken ct)
    {
        var cap = new Capture { Source = "game", What = game, Seconds = seconds, RefreshHz = refreshHz };
        bool frames = PresentMonitor.State == CaptureState.Running || PresentMonitor.Start() == CaptureState.Running;
        if (!frames) cap.FramesUnavailable = PresentMonitor.Message;
        var cpuBefore = ProcessTimes();
        using var probe = await Task.Run(() => new TelemetryProbe(pid), ct);
        long last = PresentMonitor.Now;
        var times = new List<long>();
        for (int i = 0; i < seconds; i++)
        {
            await Task.Delay(1000, ct);
            int n = 0;
            if (frames)
            {
                var add = PresentMonitor.Frames.TimesAfter(pid, times.Count > 0 ? times[^1] : last);
                times.AddRange(add); n = add.Count;
            }
            var s = await Task.Run(() => probe.Sample(n), ct);
            cap.Samples.Add(s);
            progress?.Report((i + 1, s));
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) break; } catch { cap.FramesUnavailable ??= "The game closed during the capture."; break; }
        }
        for (int i = 1; i < times.Count; i++) cap.FrameMs.Add((times[i] - times[i - 1]) / 10_000.0);
        if (frames && cap.FrameMs.Count < 30) cap.FramesUnavailable = "Too few frames were recorded: was the game in front and drawing?";
        cap.TopBackground = TopBackground(cpuBefore, ProcessTimes(), pid, seconds);
        return cap;
    }

    public static Dictionary<int, (string name, TimeSpan cpu)> ProcessTimes()
    {
        var d = new Dictionary<int, (string, TimeSpan)>();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
            using (p)
                try { if (p.Id is not 0 and not 4 && p.Id != self) d[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch { }
        return d;
    }

    /// <summary>The non-game process that used the most CPU during the capture, as "name (x% of all cores)".</summary>
    public static string? TopBackground(Dictionary<int, (string name, TimeSpan cpu)> a, Dictionary<int, (string name, TimeSpan cpu)> b, int? gamePid, int seconds)
    {
        var top = b.Where(kv => kv.Key != gamePid && a.ContainsKey(kv.Key) && kv.Value.name is not "Idle" and not "System")
            .Select(kv => (kv.Value.name, pct: (kv.Value.cpu - a[kv.Key].cpu).TotalMilliseconds / (seconds * 1000.0 * Environment.ProcessorCount) * 100))
            .GroupBy(x => x.name).Select(g => (name: g.Key, pct: g.Sum(x => x.pct))).OrderByDescending(x => x.pct).FirstOrDefault();
        return top.name is not null && top.pct >= 1 ? $"{top.name} ({top.pct:0.#}% of all cores)" : null;
    }
}
