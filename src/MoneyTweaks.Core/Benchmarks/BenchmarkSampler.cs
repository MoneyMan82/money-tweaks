using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Benchmarks;

public sealed record BenchmarkTick(int Second, int Total, double Cpu, double? Gpu, double? PingMs);

/// <summary>
/// Measures one run: CPU/GPU/RAM/temperature once per second from Windows counters and ICMP latency to the chosen
/// endpoint twice per second. Nothing is estimated; a metric with no reliable source on this PC is left empty.
/// </summary>
public static class BenchmarkSampler
{
    public const string FortniteExe = "FortniteClient-Win64-Shipping";

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus s);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

    public static bool? OnAcPower() => GetSystemPowerStatus(out var s) ? s.AcLineStatus switch { 1 => true, 0 => false, _ => null } : null;

    public static string ActivePowerPlan()
    {
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "powercfg.exe"), "/getactivescheme");
        var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"\(([^)]+)\)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : r.StdOut.Trim();
    }

    public static string? ForegroundProcess()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    public static bool FortniteRunning()
    {
        var ps = Process.GetProcessesByName(FortniteExe);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    public static double? GpuTemp()
    {
        var smi = Path.Combine(ProcessRunner.System32, "nvidia-smi.exe");
        if (!File.Exists(smi)) return null;
        try
        {
            var r = ProcessRunner.Run(smi, "--query-gpu=temperature.gpu --format=csv,noheader,nounits", 5000);
            var first = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t is > 0 and < 130 ? t : null;
        }
        catch { return null; }
    }

    public static string ResolveEndpoint(string endpoint) => endpoint == "gateway" ? PingMonitor.DefaultGateway() ?? "1.1.1.1" : endpoint;

    public static async Task<BenchmarkRun> RunAsync(string studyId, string phase, int durationSec, string? endpoint, string scene,
        IProgress<BenchmarkTick>? progress, CancellationToken ct, string appVersion = "")
    {
        durationSec = Math.Clamp(durationSec, 5, 600);
        var host = endpoint is null ? null : ResolveEndpoint(endpoint);
        using var hw = new HardwareMonitor();
        // Two seconds of settling: primes the counters and records how busy the PC already was.
        await Task.Delay(1000, ct);
        var pre = hw.Sample();
        var procCount = pre.ProcessCount;
        await Task.Delay(1000, ct);
        var idle = (pre.CpuPercent + hw.Sample().CpuPercent) / 2;

        var run = new BenchmarkRun
        {
            StudyId = studyId, Phase = phase, At = DateTime.Now,
            Conditions = new TestConditions
            {
                DurationSec = durationSec, Endpoint = host, PowerPlan = await Task.Run(ActivePowerPlan, ct), OnAcPower = OnAcPower(),
                ProcessCount = procCount, IdleCpuBefore = Math.Round(idle, 1), Foreground = ForegroundProcess(), FortniteRunning = FortniteRunning(),
                OsBuild = OsInfo.Build, AppVersion = appVersion, Scene = scene,
            }
        };

        var pings = new List<double?>();
        double? lastPing = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = host is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var p = new Ping();
            while (!stop.IsCancellationRequested)
            {
                var t0 = Environment.TickCount64;
                double? ms = null;
                try { var r = await p.SendPingAsync(host, 1000); if (r.Status == IPStatus.Success) ms = r.RoundtripTime; } catch { }
                lock (pings) pings.Add(ms);
                lastPing = ms;
                var wait = 500 - (int)(Environment.TickCount64 - t0);
                if (wait > 0) try { await Task.Delay(wait, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });

        var cpu = new List<double>(); var gpu = new List<double>(); var ram = new List<double>(); var cpuT = new List<double>(); var gpuT = new List<double>();
        var sw = Stopwatch.StartNew();
        try
        {
            for (int s = 1; s <= durationSec; s++)
            {
                var due = s * 1000 - sw.ElapsedMilliseconds;
                if (due > 0) await Task.Delay((int)due, ct);
                var snap = await Task.Run(hw.Sample, ct);
                cpu.Add(snap.CpuPercent);
                if (snap.GpuPercent is { } g) gpu.Add(g);
                ram.Add(snap.RamUsedGb);
                if (snap.CpuTempC is { } ct2) cpuT.Add(ct2);
                if (s % 5 == 1 && await Task.Run(GpuTemp, ct) is { } gt) gpuT.Add(gt);
                progress?.Report(new(s, durationSec, snap.CpuPercent, snap.GpuPercent, lastPing));
            }
        }
        finally
        {
            stop.Cancel();
            try { await pingTask; } catch { }
        }

        double? Avg(List<double> l) => l.Count == 0 ? null : Math.Round(l.Average(), 2);
        run.CpuAvg = Avg(cpu);
        run.CpuP95 = cpu.Count == 0 ? null : Math.Round(BenchmarkStats.Percentile(cpu.OrderBy(x => x).ToList(), 95), 2);
        run.GpuAvg = Avg(gpu);
        run.RamUsedGb = Avg(ram);
        run.CpuTempC = Avg(cpuT);
        run.GpuTempC = Avg(gpuT);
        run.CpuSeries = cpu.Select(x => (float)Math.Round(x, 1)).ToList();
        run.GpuSeries = gpu.Select(x => (float)Math.Round(x, 1)).ToList();
        List<double?> pl;
        lock (pings) pl = pings.ToList();
        if (pl.Count > 0)
        {
            var st = PingMonitor.Compute(pl);
            run.PingSent = st.Sent;
            run.LossPct = Math.Round(st.LossPercent, 2);
            if (st.Sent > st.Lost)
            {
                run.PingAvgMs = Math.Round(st.AvgMs, 2); run.PingMinMs = st.MinMs; run.PingMaxMs = st.MaxMs; run.JitterMs = Math.Round(st.JitterMs, 2);
            }
            run.PingSeries = pl.Select(x => x is null ? -1f : (float)x.Value).ToList();
        }
        Log.Info($"Benchmark run ({phase}, {durationSec}s): CPU {run.CpuAvg:0.0}% GPU {run.GpuAvg?.ToString("0.0") ?? "n/a"}% ping {run.PingAvgMs?.ToString("0.0") ?? "n/a"} ms loss {run.LossPct ?? 0:0.#}%");
        return run;
    }
}
