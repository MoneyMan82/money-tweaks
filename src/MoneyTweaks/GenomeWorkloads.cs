using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Genome;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks;

/// <summary>Sensors sampled every 2 s during a trial, and the safety limits that end it.</summary>
internal sealed class TrialSensors : IAsyncDisposable
{
    public const double GpuSafetyC = 90, CpuSafetyC = 95;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    private readonly int? _gpu;
    public readonly List<double> GpuTemp = [], GpuPower = [], CpuTemp = [], CpuPower = [];
    public double? MaxGpuMhz;
    public string? SafetyStop;

    public TrialSensors(int? gpuIndex, Action? onSafety)
    {
        _gpu = gpuIndex;
        _task = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (_gpu is { } i && NvidiaSmi.Read(i) is { } r)
                    {
                        lock (this)
                        {
                            if (r.TempC is { } t) GpuTemp.Add(t);
                            if (r.PowerW is { } p) GpuPower.Add(p);
                            if (r.CoreMhz is { } m) MaxGpuMhz = Math.Max(MaxGpuMhz ?? 0, m);
                        }
                    }
                    var readings = HwInfo.Readings();
                    var ct = Afterburner.CpuTemperature() ?? HwInfo.CpuTemperature(readings);
                    var cp = HwInfo.CpuPower(readings);
                    lock (this) { if (ct is { } c) CpuTemp.Add(c); if (cp is { } w) CpuPower.Add(w); }
                    string? stop = GpuTemp.Count > 0 && GpuTemp[^1] >= GpuSafetyC ? $"GPU reached {GpuTemp[^1]:0} °C (safety limit {GpuSafetyC:0} °C)"
                        : ct >= CpuSafetyC ? $"CPU reached {ct:0} °C (safety limit {CpuSafetyC:0} °C)" : null;
                    if (stop is not null && SafetyStop is null) { SafetyStop = stop; onSafety?.Invoke(); }
                }
                catch (Exception ex) { Log.Debug("genome sensors: " + ex.Message); }
                try { await Task.Delay(2000, _stop.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    public void Fill(Measurement m)
    {
        lock (this)
        {
            if (GpuTemp.Count > 0) m.Values["gputemp"] = GpuTemp.Max();
            if (GpuPower.Count > 0) m.Values["gpupower"] = GpuPower.Average();
            if (CpuTemp.Count > 0) m.Values["cputemp"] = CpuTemp.Max();
            if (CpuPower.Count > 0) m.Values["cpupower"] = CpuPower.Average();
            m.MaxGpuMhz = MaxGpuMhz;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _task; } catch { }
        _stop.Dispose();
    }

    /// <summary>Frame-time metrics: average and 1% low FPS, 99th percentile frame time, variability (CV).</summary>
    public static void FillFrames(Measurement m, IReadOnlyList<double> ms, double refreshHz, string source)
    {
        if (ms.Count < 30) return;
        var st = FrameTimeCsv.FromFrameTimes(ms, source);
        m.Frames = ms.Count;
        m.RefreshHz = refreshHz;
        m.Values["fps"] = st.AvgFps;
        if (st.Low1Fps is { } l) m.Values["low1"] = l;
        if (st.FrameTimeP99Ms is { } p) m.Values["p99"] = p;
        double mean = ms.Average(), sd = Math.Sqrt(ms.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, ms.Count - 1));
        m.Values["cv"] = sd / mean * 100;
        // Synced to the display: at the refresh rate, the FPS shows the monitor, not the hardware.
        m.FpsCapped = refreshHz > 0 && st.AvgFps >= refreshHz * 0.95;
    }

    public static string? StabilityProblem(DateTime since)
    {
        var (whea, resets) = StabilityTest.CountEventsSince(since);
        return resets > 0 ? $"{resets} GPU driver reset(s)" : whea > 0 ? $"{whea} WHEA hardware error(s)" : null;
    }
}

/// <summary>
/// The built-in repeatable benchmark: the seeded 3D stress scene. Calibrated once so FPS stays below the display's refresh
/// rate (otherwise the monitor, not the hardware, sets the FPS and frame metrics would be meaningless).
/// </summary>
public sealed class SyntheticGenomeWorkload : IGenomeWorkload
{
    private readonly int? _gpu = NvidiaSmi.Available ? NvidiaSmi.Limits().FirstOrDefault()?.Index : null;

    public static async Task<string> CalibrateAsync(GenomeWorkload w, Action<string> status, CancellationToken ct)
    {
        int refresh = StressScene.RefreshRate();
        double maxW = SystemParameters.PrimaryScreenWidth, maxH = SystemParameters.PrimaryScreenHeight;
        var steps = new[] { (3, 1280, 720), (4, 1600, 900), (5, 1920, 1080), (5, 2560, 1440) }.Where(s => s.Item2 <= maxW && s.Item3 <= maxH).ToList();
        if (steps.Count == 0) steps.Add((3, 1280, 720));
        (int i, int w, int h) chosen = steps[^1];
        double fps = 0;
        foreach (var (i, wd, ht) in steps)
        {
            status($"Calibrating: intensity {i}, {wd}×{ht}…");
            var r = await StressScene.RunAsync(new SceneOptions(i, 6, wd, ht, "Performance Genome · calibration"), ct);
            if (!r.Completed) throw new OperationCanceledException(r.StopReason);
            var ms = r.FrameMs;
            fps = ms.Count > 0 ? ms.Count * 1000.0 / ms.Sum() : 0;
            chosen = (i, wd, ht);
            if (refresh <= 0 || fps < refresh * 0.85) break;
        }
        w.Intensity = chosen.i; w.Width = chosen.w; w.Height = chosen.h;
        bool capped = refresh > 0 && fps >= refresh * 0.85;
        w.Scene = $"3D scene, intensity {w.Intensity}, {w.Width}×{w.Height}" + (refresh > 0 ? $", display {refresh} Hz" : "");
        return capped
            ? $"Even the heaviest scene runs at about {fps:0} FPS on your {refresh} Hz display, so FPS results are limited by the display. Temperatures and power are still measured; frame metrics at the cap are left out."
            : $"Calibrated: {w.Scene} runs at about {fps:0} FPS, below the refresh rate, so FPS differences are measurable.";
    }

    public async Task<WorkloadResult> RunAsync(GenomeWorkload w, bool warmup, Action<string> status, CancellationToken ct)
    {
        int secs = warmup ? 10 : w.DurationSec;
        var since = DateTime.Now;
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var sensors = new TrialSensors(_gpu, () => safety.Cancel());
        status(warmup ? "warming up (not measured)…" : $"measuring for {secs} s (+3 s warm-up)…");
        SceneResult r;
        try { r = await StressScene.RunAsync(new SceneOptions(w.Intensity, secs, w.Width, w.Height, warmup ? "Performance Genome · warm-up" : "Performance Genome · measuring"), safety.Token); }
        catch (Exception ex) { return new(null, false, "the 3D load failed: " + ex.Message); }
        if (sensors.SafetyStop is { } hot) return new(null, false, hot);
        ct.ThrowIfCancellationRequested();
        if (!r.Completed)
        {
            if (r.StopReason is "Stopped by user" or "Window closed" or "Stopped") throw new OperationCanceledException();
            return new(null, false, "the 3D load stopped: " + r.StopReason);
        }
        if (TrialSensors.StabilityProblem(since) is { } bad) return new(null, false, bad);
        if (warmup) return new(null, true, null);
        var m = new Measurement();
        TrialSensors.FillFrames(m, r.FrameMs, r.RefreshHz, "3D scene");
        sensors.Fill(m);
        return m.Values.Count == 0 ? new(null, false, "no data") : new(m, true, null);
    }
}

/// <summary>
/// A controlled manual comparison in a real game: before every capture you go to the same place and do the same thing;
/// Money Tweaks then records the game's own frame timing (Present events, read-only, nothing is injected) for a fixed
/// time. Less repeatable than the built-in scene, so results say so.
/// </summary>
public sealed class GameGenomeWorkload(Func<string, string, Task<bool>> ask) : IGenomeWorkload
{
    private readonly int? _gpu = NvidiaSmi.Available ? NvidiaSmi.Limits().FirstOrDefault()?.Index : null;
    public const int CountdownSec = 8;

    public static int? FindPid(GenomeWorkload w)
    {
        if (w.GameExe is not { Length: > 0 } exe) return null;
        var name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
                using (p) return p.Id;
        }
        catch { }
        return null;
    }

    public async Task<WorkloadResult> RunAsync(GenomeWorkload w, bool warmup, Action<string> status, CancellationToken ct)
    {
        if (warmup) return new(null, true, null);
        if (PresentMonitor.State != CaptureState.Running && PresentMonitor.Start() != CaptureState.Running)
            return new(null, false, PresentMonitor.Message, Fatal: true);
        if (FindPid(w) is null) return new(null, false, $"{w.Name} isn't running", Fatal: true);
        if (!await ask("Ready for the next capture?", $"Go to your test spot in {w.Name} ({(w.Scene.Length > 0 ? w.Scene : "the same place and action every time")}).\n\n" +
                $"Press Start, switch to the game within {CountdownSec} s, and do the same thing as every time. Money Tweaks records {w.DurationSec} s; a sound marks the start and the end."))
            throw new OperationCanceledException();
        for (int i = CountdownSec; i > 0; i--) { status($"capture starts in {i} s: switch to the game…"); await Task.Delay(1000, ct); }
        var pid = FindPid(w);
        if (pid is null) return new(null, false, $"{w.Name} closed or crashed", Fatal: true);
        var since = DateTime.Now;
        SystemSounds.Asterisk.Play();
        await using var sensors = new TrialSensors(_gpu, null);
        var times = new List<long>();
        long last = PresentMonitor.Now;
        var end = DateTime.Now.AddSeconds(w.DurationSec);
        while (DateTime.Now < end)
        {
            await Task.Delay(1000, ct);
            status($"recording {w.Name}: {(end - DateTime.Now).TotalSeconds:0} s left…");
            var add = PresentMonitor.Frames.TimesAfter(pid.Value, times.Count > 0 ? times[^1] : last);
            times.AddRange(add);
            if (FindPid(w) is null) return new(null, false, $"{w.Name} closed or crashed during the capture");
        }
        await Task.Delay(1500, ct); // ETW delivers in buffers: collect the tail
        times.AddRange(PresentMonitor.Frames.TimesAfter(pid.Value, times.Count > 0 ? times[^1] : last).Where(t => t <= last + (long)(w.DurationSec + 1) * 10_000_000));
        SystemSounds.Exclamation.Play();
        if (TrialSensors.StabilityProblem(since) is { } bad) return new(null, false, bad);
        var ms = new List<double>();
        for (int i = 1; i < times.Count; i++) ms.Add((times[i] - times[i - 1]) / 10_000.0);
        var m = new Measurement();
        TrialSensors.FillFrames(m, ms, StressScene.RefreshRate(), w.Name);
        sensors.Fill(m);
        if (m.Frames < 30) return new(null, false, "too few frames recorded: was the game in front and drawing?");
        return new(m, true, null);
    }
}
