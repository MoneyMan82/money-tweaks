using System.Diagnostics;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

public sealed record StabilitySample(DateTime At, double? CpuLoad, double? CpuTempC, double? GpuTempC, double? GpuPowerW, int? GpuCoreMhz, bool GpuHwThrottle, bool GpuPowerCapped);

public sealed record StabilityLimits(double MaxGpuTempC = 87, double MaxCpuTempC = 95);

public enum StabilityVerdict { NoErrorsPreliminary, NoErrorsExtended, Failed, Stopped }

public sealed record StabilityResult(StabilityVerdict Verdict, TimeSpan Duration, string Summary, IReadOnlyList<string> Problems,
    long CpuIterations, double? MaxCpuTempC, double? MaxGpuTempC, double? MaxGpuPowerW, int? MaxGpuMhz, int ThrottleSamples)
{
    public bool Failed => Verdict == StabilityVerdict.Failed;
}

/// <summary>
/// CPU load that checks its own answers: every worker repeats a fixed floating-point + integer workload whose correct
/// result is computed first, and any different answer is a computation error (the classic sign of an unstable overclock
/// or undervolt). While it runs, temperatures, power and throttling are sampled, and new WHEA hardware errors and GPU
/// driver resets in the event log count as failures. A short run can only find gross problems: results never say "stable".
/// </summary>
public static class StabilityTest
{
    public const int QuickSeconds = 60;

    /// <summary>Deterministic workload; the same seed always gives the same checksum on a healthy CPU.</summary>
    public static ulong Workload(int seed)
    {
        double acc = seed * 0.000123 + 1.0;
        ulong h = 1469598103934665603UL ^ (ulong)seed;
        for (int i = 1; i <= 20_000; i++)
        {
            acc = Math.Sqrt(acc * 1.000037 + i) + Math.Sin(acc) * 0.5 - Math.Cos(i * 0.001) * 0.25;
            h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(acc)) * 1099511628211UL;
            h ^= h >> 29;
        }
        return h;
    }

    /// <summary>Pure rules, also used by tests: what in the samples and events makes a run fail.</summary>
    public static List<string> Evaluate(IReadOnlyList<StabilitySample> samples, StabilityLimits limits, int computeErrors, int newWhea, int newGpuResets)
    {
        var p = new List<string>();
        if (computeErrors > 0) p.Add($"{computeErrors} computation error(s): the CPU produced wrong results. That is instability, even if nothing crashed.");
        if (newWhea > 0) p.Add($"{newWhea} new WHEA hardware error(s) in the event log during the test (often an unstable CPU/RAM overclock or undervolt).");
        if (newGpuResets > 0) p.Add($"{newGpuResets} GPU driver reset(s) during the test (often an unstable GPU overclock or undervolt).");
        if (samples.Max(s => s.GpuTempC) is { } g && g >= limits.MaxGpuTempC) p.Add($"GPU reached {g:0} °C (limit {limits.MaxGpuTempC:0} °C).");
        if (samples.Max(s => s.CpuTempC) is { } c && c >= limits.MaxCpuTempC) p.Add($"CPU reached {c:0} °C (limit {limits.MaxCpuTempC:0} °C).");
        return p;
    }

    /// <summary>Runs the CPU load for <paramref name="duration"/> (with an optional GPU load running alongside, started by
    /// the caller), sampling once per <paramref name="sampleEvery"/>. Stops early at the first failure.</summary>
    public static async Task<StabilityResult> RunAsync(TimeSpan duration, Func<StabilitySample> sample, IProgress<(double fraction, StabilitySample s)>? progress,
        CancellationToken ct, StabilityLimits? limits = null, int? threads = null, TimeSpan? sampleEvery = null, Func<DateTime, (int whea, int gpuResets)>? events = null)
    {
        limits ??= new();
        events ??= CountEventsSince;
        var start = DateTime.Now;
        var sw = Stopwatch.StartNew();
        var expected = Enumerable.Range(1, 8).Select(Workload).ToArray(); // reference answers, computed before the load starts
        int errors = 0; long iterations = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int n = Math.Max(1, threads ?? Environment.ProcessorCount);
        var workers = Enumerable.Range(0, n).Select(w => Task.Factory.StartNew(() =>
        {
            int k = w;
            while (!stop.IsCancellationRequested)
            {
                int seed = k % 8 + 1;
                if (Workload(seed) != expected[seed - 1]) Interlocked.Increment(ref errors);
                Interlocked.Increment(ref iterations);
                k++;
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var samples = new List<StabilitySample>();
        var problems = new List<string>();
        bool cancelled = false;
        try
        {
            while (sw.Elapsed < duration)
            {
                await Task.Delay(sampleEvery ?? TimeSpan.FromSeconds(2), ct);
                var s = sample();
                samples.Add(s);
                progress?.Report((Math.Min(1, sw.Elapsed / duration), s));
                problems = Evaluate(samples, limits, Volatile.Read(ref errors), 0, 0);
                if (problems.Count > 0) break;
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(workers); } catch { }
        }
        var (whea, resets) = events(start);
        problems = Evaluate(samples.Count > 0 ? samples : [new(DateTime.Now, null, null, null, null, null, false, false)], limits, errors, whea, resets);

        var dur = sw.Elapsed;
        var verdict = problems.Count > 0 ? StabilityVerdict.Failed : cancelled ? StabilityVerdict.Stopped
            : dur.TotalMinutes >= 10 ? StabilityVerdict.NoErrorsExtended : StabilityVerdict.NoErrorsPreliminary;
        string mins = dur.TotalMinutes >= 1 ? $"{dur.TotalMinutes:0} min" : $"{dur.TotalSeconds:0} s";
        var summary = verdict switch
        {
            StabilityVerdict.Failed => $"Failed after {mins}: " + string.Join(" ", problems),
            StabilityVerdict.Stopped => $"Stopped after {mins} with no errors so far. That says nothing about stability.",
            StabilityVerdict.NoErrorsExtended => $"No errors in {mins} of load. A good sign, not a guarantee: play your games for a while and watch for crashes, artifacts or corrupted files.",
            _ => $"No errors in a {mins} preliminary scan. This only catches gross problems; it does not mean the settings are stable. Run an extended test (10+ minutes) before relying on them.",
        };
        return new(verdict, dur, summary, problems, iterations,
            samples.Max(s => s.CpuTempC), samples.Max(s => s.GpuTempC), samples.Max(s => s.GpuPowerW), samples.Max(s => s.GpuCoreMhz),
            samples.Count(s => s.GpuHwThrottle));
    }

    /// <summary>New WHEA errors and GPU driver resets (Display 4101) since <paramref name="since"/>, from the System log.</summary>
    public static (int whea, int gpuResets) CountEventsSince(DateTime since)
    {
        int Count(string provider, int[]? ids)
        {
            try { return HealthChecks.QueryEvents("System", provider, ids, null, 50).Count(e => e.Time >= since); }
            catch (Exception ex) { Log.Debug("stability events: " + ex.Message); return 0; }
        }
        return (Count("Microsoft-Windows-WHEA-Logger", null), Count("Display", [4101]));
    }
}
