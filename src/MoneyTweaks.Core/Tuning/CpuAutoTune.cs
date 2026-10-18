using System.Diagnostics;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>A Windows boost setting combination to measure: boost mode (Microsoft's values) and energy performance preference.</summary>
public sealed record CpuCandidate(string Name, int BoostMode, int Epp, string Why);

/// <summary>One measurement: work done per second on one thread (what most games lean on) and on all threads, plus the
/// hottest temperature and average package power while loaded (null when no sensor source runs).</summary>
public sealed record CpuMeasure(double SingleScore, double MultiScore, double? MaxTempC, double? AvgPowerW);

public sealed record CpuCandidateResult(CpuCandidate Candidate, CpuMeasure Measure, double SinglePct, double MultiPct)
{
    /// <summary>Gaming-weighted performance vs. the current settings: single thread counts more.</summary>
    public double ScorePct => SinglePct * 0.6 + MultiPct * 0.4;
}

public sealed record CpuTuneResult(bool Completed, IReadOnlyList<CpuCandidateResult> Results, CpuCandidateResult? Fastest, CpuCandidateResult? Recommended,
    double NoisePct, string Summary);

public interface ICpuTuneBackend
{
    /// <summary>Sets boost mode and EPP (plugged in and on battery) on the active plan; false = Windows refused.</summary>
    Task<bool> SetAsync(int boostMode, int epp);
    Task<CpuMeasure> MeasureAsync(CancellationToken ct);
    /// <summary>Puts back the exact settings from before the scan.</summary>
    Task RestoreAsync();
}

/// <summary>
/// Automatic CPU boost tuning through Windows' own processor power settings (documented by Microsoft, every Intel and AMD
/// CPU). Each combination is measured under real load: single- and multi-threaded throughput, temperature and package
/// power. The current settings are measured first and again last, so the run's own noise is known and differences
/// smaller than that are not presented as gains. Nothing is kept: the original settings are put back and the user
/// chooses what to apply. This tunes boost behaviour, not clocks or voltages (those are in the Curve Optimizer assistant).
/// </summary>
public static class CpuAutoTune
{
    public static readonly IReadOnlyList<CpuCandidate> Candidates =
    [
        new("Maximum boost", 2, 0, "Aggressive boost, maximum performance preference."),
        new("Balanced boost", 4, 25, "Efficient Aggressive boost: nearly full boost, slightly less eager."),
        new("Efficient", 3, 50, "Efficient Enabled boost: boosts less often, cooler and quieter."),
        new("Cool & quiet", 3, 80, "Efficient boost with a strong power-saving preference."),
    ];

    public static async Task<CpuTuneResult> RunAsync(ICpuTuneBackend b, IProgress<(int step, int total, string text)>? progress, CancellationToken ct)
    {
        int total = Candidates.Count + 2, step = 0;
        void Say(string s) => progress?.Report((step, total, s));
        try
        {
            Say("Measuring your current settings…");
            var before = await b.MeasureAsync(ct); step++;
            var raw = new List<(CpuCandidate c, CpuMeasure m)>();
            foreach (var c in Candidates)
            {
                ct.ThrowIfCancellationRequested();
                Say($"Measuring {c.Name}…");
                if (!await b.SetAsync(c.BoostMode, c.Epp)) { await b.RestoreAsync(); return Fail("Windows didn't accept the boost settings, so the scan stopped. Your settings were put back."); }
                raw.Add((c, await b.MeasureAsync(ct)));
                step++;
            }
            await b.RestoreAsync();
            Say("Measuring your current settings again…");
            var after = await b.MeasureAsync(ct); step++;
            Say("Done.");
            return Evaluate(before, after, raw);
        }
        catch (OperationCanceledException)
        {
            await b.RestoreAsync();
            return Fail("Stopped. Your settings were put back.");
        }
        catch (Exception ex)
        {
            await b.RestoreAsync();
            return Fail(ex.Message + " Your settings were put back.");
        }
    }

    private static CpuTuneResult Fail(string why) => new(false, [], null, null, 0, why);

    /// <summary>Pure: rank the measurements against the current settings (average of the first and last run).</summary>
    public static CpuTuneResult Evaluate(CpuMeasure before, CpuMeasure after, IReadOnlyList<(CpuCandidate c, CpuMeasure m)> raw)
    {
        double baseSingle = (before.SingleScore + after.SingleScore) / 2, baseMulti = (before.MultiScore + after.MultiScore) / 2;
        if (baseSingle <= 0 || baseMulti <= 0) return Fail("The measurement didn't produce usable numbers.");
        double noise = Math.Max(Math.Abs(before.SingleScore - after.SingleScore) / baseSingle, Math.Abs(before.MultiScore - after.MultiScore) / baseMulti) * 100;
        noise = Math.Max(noise, 1.0); // never claim better than 1% precision
        var results = raw.Select(x => new CpuCandidateResult(x.c, x.m, x.m.SingleScore / baseSingle * 100, x.m.MultiScore / baseMulti * 100)).ToList();
        var fastest = results.MaxBy(r => r.ScorePct)!;
        // Recommended: the coolest (or lowest power) option that is within the noise of the fastest. Without a sensor the
        // trade-off can't be judged, so the fastest is recommended.
        bool hasTemp = results.All(r => r.Measure.MaxTempC is not null), hasPower = results.All(r => r.Measure.AvgPowerW is not null);
        var close = results.Where(r => r.ScorePct >= fastest.ScorePct - Math.Max(noise, 2)).ToList();
        var rec = hasPower ? close.MinBy(r => r.Measure.AvgPowerW!.Value)!
                : hasTemp ? close.MinBy(r => r.Measure.MaxTempC!.Value)!
                : fastest;
        var lines = new List<string>();
        lines.Add(fastest.ScorePct - 100 <= noise
            ? $"Your current settings are already as fast as any option (differences are within this run's {noise:0.#}% measurement noise)."
            : $"Fastest: {fastest.Candidate.Name}, {fastest.ScorePct - 100:+0.#;-0.#}% vs. your current settings (noise {noise:0.#}%).");
        if (rec != fastest)
            lines.Add($"Recommended: {rec.Candidate.Name}: about the same performance ({rec.ScorePct - 100:+0.#;-0.#}%) with " +
                (hasPower ? $"{rec.Measure.AvgPowerW:0} W instead of {fastest.Measure.AvgPowerW:0} W" : $"{rec.Measure.MaxTempC:0} °C instead of {fastest.Measure.MaxTempC:0} °C") + ".");
        else if (!hasTemp && !hasPower)
            lines.Add("No CPU temperature or power source was running (MSI Afterburner or HWiNFO), so only speed was compared.");
        lines.Add("These are short synthetic measurements; check your games with Test & Benchmark.");
        return new(true, results, fastest, rec, noise, string.Join("\n", lines));
    }
}

/// <summary>The real thing: powercfg for the settings, the self-checking CPU workload for the score, Afterburner/HWiNFO for sensors.</summary>
public sealed class LiveCpuTuneBackend(TimeSpan? phase = null) : ICpuTuneBackend
{
    private readonly TimeSpan _phase = phase ?? TimeSpan.FromSeconds(7);
    private readonly CpuPowerSetting _origBoost = CpuPowerSettings.Read(CpuPowerSettings.BoostMode), _origEpp = CpuPowerSettings.Read(CpuPowerSettings.Epp);

    /// <summary>False when Windows doesn't report these settings (then nothing can be measured or put back).</summary>
    public bool Supported => _origBoost.Readable && _origEpp.Readable;

    public Task RestoreAsync() => Task.Run(() =>
    {
        if (_origBoost.Readable) CpuPowerSettings.Set(CpuPowerSettings.BoostMode, _origBoost.Ac!.Value, _origBoost.Dc ?? _origBoost.Ac!.Value);
        if (_origEpp.Readable) CpuPowerSettings.Set(CpuPowerSettings.Epp, _origEpp.Ac!.Value, _origEpp.Dc ?? _origEpp.Ac!.Value);
    });

    public Task<bool> SetAsync(int boostMode, int epp) => Task.Run(() =>
    {
        bool ok = CpuPowerSettings.Set(CpuPowerSettings.BoostMode, boostMode, boostMode).ok & CpuPowerSettings.Set(CpuPowerSettings.Epp, epp, epp).ok;
        if (!ok) return false;
        var b = CpuPowerSettings.Read(CpuPowerSettings.BoostMode);
        var e = CpuPowerSettings.Read(CpuPowerSettings.Epp);
        return (!b.Readable || b.Ac == boostMode) && (!e.Readable || e.Ac == epp);
    });

    public async Task<CpuMeasure> MeasureAsync(CancellationToken ct)
    {
        await Task.Delay(2500, ct); // let the power manager settle on the new settings
        double single = await Task.Run(() => Throughput(1, _phase, ct, null), ct);
        var temps = new List<double>(); var power = new List<double>();
        double multi = await Task.Run(() => Throughput(Environment.ProcessorCount, _phase, ct, () =>
        {
            var readings = HwInfo.Parse(HwInfo.Reader());
            if ((Afterburner.CpuTemperature() ?? HwInfo.CpuTemperature(readings)) is { } t) temps.Add(t);
            if (HwInfo.CpuPower(readings) is { } p) power.Add(p);
        }), ct);
        return new(single, multi, temps.Count > 0 ? temps.Max() : null, power.Count > 0 ? power.Average() : null);
    }

    /// <summary>Workload iterations per second across <paramref name="threads"/> threads; wrong answers throw (instability).</summary>
    private static double Throughput(int threads, TimeSpan d, CancellationToken ct, Action? sample)
    {
        long count = 0; int errors = 0;
        var expected = Enumerable.Range(1, 8).Select(StabilityTest.Workload).ToArray();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(w => Task.Factory.StartNew(() =>
        {
            int k = w;
            while (!stop.IsCancellationRequested)
            {
                int seed = k++ % 8 + 1;
                if (StabilityTest.Workload(seed) != expected[seed - 1]) Interlocked.Increment(ref errors);
                Interlocked.Increment(ref count);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        while (sw.Elapsed < d && !ct.IsCancellationRequested) { Thread.Sleep(1000); sample?.Invoke(); }
        stop.Cancel();
        Task.WaitAll(workers);
        ct.ThrowIfCancellationRequested();
        if (errors > 0) throw new InvalidOperationException($"The CPU produced {errors} wrong result(s) during the measurement: it is unstable. Undo any CPU overclock or undervolt first.");
        return count / sw.Elapsed.TotalSeconds;
    }
}
