namespace MoneyTweaks.Core.Tuning;

/// <summary>One load step: passed, or why not. <see cref="Fatal"/> = stop the whole scan (too hot, can't control the GPU),
/// which is not the same as finding the overclock limit.</summary>
public sealed record ScanStepResult(bool Passed, string? Failure = null, bool Fatal = false, double? MaxTempC = null, int? AvgCoreMhz = null);

/// <summary>What the scan needs from the outside world. The app implements it with MSI Afterburner (offsets, read back),
/// the 3D stress scene (load), nvidia-smi (temperature, clocks) and the event log (GPU driver resets).</summary>
public interface IOcScanBackend
{
    /// <summary>Sets both offsets and verifies them; false = couldn't apply or read back.</summary>
    Task<bool> SetOffsetsAsync(int coreMhz, int memMhz);
    Task<ScanStepResult> LoadAsync(TimeSpan duration, CancellationToken ct);
    /// <summary>After a failed step: give the driver time to recover before the next change.</summary>
    Task RecoverAsync();
}

public sealed record ScanOptions(int CoreMax, int MemMax, bool ScanMemory = true, int CoreStep = 30, int CoreFine = 15, int CoreMargin = 30,
    int MemStep = 250, int MemMargin = 250, TimeSpan? StepTime = null, TimeSpan? ConfirmTime = null)
{
    public TimeSpan Step => StepTime ?? TimeSpan.FromSeconds(45);
    public TimeSpan Confirm => ConfirmTime ?? TimeSpan.FromMinutes(3);

    /// <summary>Scan limits from Afterburner's own ranges, capped so a scan never goes past what GPUs realistically do.</summary>
    public static ScanOptions For(AbGpu g) => new(
        CoreMax: Math.Min(g.CoreOffset?.Max ?? 0, 300),
        MemMax: Math.Min(g.MemOffset?.Max ?? 0, 1500),
        ScanMemory: g.MemOffset?.Adjustable == true);

    /// <summary>Upper bound on the number of load steps (for progress).</summary>
    public int MaxSteps => 1 + CoreMax / CoreStep + 1 + (ScanMemory ? MemMax / MemStep : 0) + 2;
}

public enum ScanOutcome { Found, NoHeadroom, FailsAtStock, Aborted, Cancelled }

public sealed record ScanResult(ScanOutcome Outcome, int CoreMhz, int MemMhz, int? HighestCorePassed, int? HighestMemPassed, string Summary, IReadOnlyList<string> Log)
{
    /// <summary>The offsets the GPU is left at: the found values, or 0/0 when nothing usable was found. Cancelled = caller restores.</summary>
    public bool KeepsOffsets => Outcome == ScanOutcome.Found;
}

/// <summary>
/// Automatic GPU overclock scan, the same idea as NVIDIA's and MSI's OC scanners: raise the core clock offset step by step
/// under a real 3D load until a step fails (GPU driver reset, the load crashing, the GPU stopping to respond), refine
/// once, back off a safety margin, then do the same for the memory clock, and finally confirm the combination with a
/// longer run. The result is a starting point, never "stable": games stress GPUs differently and only longer testing
/// (and playing) shows that. Pure logic; all hardware access goes through <see cref="IOcScanBackend"/>.
/// </summary>
public static class AutoOcScanner
{
    public static async Task<ScanResult> RunAsync(IOcScanBackend b, ScanOptions o, IProgress<(int step, string text)>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        int step = 0;
        void Say(string s) { log.Add(s); progress?.Report((step, s)); }
        ScanResult Done(ScanOutcome outcome, int core, int mem, int? hc, int? hm, string summary) { Say(summary); return new(outcome, core, mem, hc, hm, summary, log); }

        // One step: set, load, report. Returns null when the offsets couldn't be set (that ends the scan).
        async Task<ScanStepResult?> Try(int core, int mem, TimeSpan d, string what)
        {
            ct.ThrowIfCancellationRequested();
            step++;
            Say($"{what}: core {core:+0;-0;0} MHz, memory {mem:+0;-0;0} MHz…");
            if (!await b.SetOffsetsAsync(core, mem)) { Say("MSI Afterburner didn't accept or confirm the offsets."); return null; }
            var r = await b.LoadAsync(d, ct);
            Say(r.Passed ? $"  passed{Details(r)}" : $"  failed: {r.Failure}{Details(r)}");
            if (!r.Passed && !r.Fatal) await b.RecoverAsync();
            return r;
        }

        try
        {
            // 1. Stock baseline: if the GPU already fails without an overclock, don't overclock it.
            var baseR = await Try(0, 0, o.Step, "Baseline (no overclock)");
            if (baseR is null) return Done(ScanOutcome.Aborted, 0, 0, null, null, "Scan stopped: Money Tweaks couldn't set the GPU offsets through MSI Afterburner. Nothing was kept.");
            if (!baseR.Passed)
                return Done(baseR.Fatal ? ScanOutcome.Aborted : ScanOutcome.FailsAtStock, 0, 0, null, null, baseR.Fatal
                    ? $"Scan stopped at stock settings: {baseR.Failure} Fix cooling first; overclocking would make it worse."
                    : $"The GPU failed the load without any overclock ({baseR.Failure}). Don't overclock it: check drivers, cooling and power first.");

            // 2. Core clock: coarse steps until a failure, then one finer step between the last pass and the failure.
            int core = 0; bool stopped = false; string? stopWhy = null;
            int? failedAt = null;
            for (int off = o.CoreStep; off <= o.CoreMax; off += o.CoreStep)
            {
                var r = await Try(off, 0, o.Step, "Core clock");
                if (r is null) { stopped = true; stopWhy = "Afterburner stopped accepting changes"; break; }
                if (r.Passed) { core = off; continue; }
                if (r.Fatal) { stopped = true; stopWhy = r.Failure; break; }
                failedAt = off; break;
            }
            if (failedAt is { } f && o.CoreFine > 0 && core + o.CoreFine < f && !stopped)
            {
                var r = await Try(core + o.CoreFine, 0, o.Step, "Core clock (finer step)");
                if (r?.Passed == true) core += o.CoreFine;
                else if (r is null || r.Fatal) { stopped = true; stopWhy = r?.Failure ?? "Afterburner stopped accepting changes"; }
            }
            int highestCore = core;
            int coreFinal = Math.Max(0, core - o.CoreMargin);
            Say(failedAt is null && !stopped ? $"Core clock passed every step up to the scan limit (+{o.CoreMax} MHz)." : $"Highest core offset that passed: +{highestCore} MHz.");

            // 3. Memory clock, with the core at its final value.
            int mem = 0; int? highestMem = null;
            if (o.ScanMemory && !stopped && o.MemMax > 0)
            {
                highestMem = 0;
                for (int off = o.MemStep; off <= o.MemMax; off += o.MemStep)
                {
                    var r = await Try(coreFinal, off, o.Step, "Memory clock");
                    if (r is null) { stopped = true; stopWhy = "Afterburner stopped accepting changes"; break; }
                    if (r.Passed) { mem = off; highestMem = off; continue; }
                    if (r.Fatal) { stopped = true; stopWhy = r.Failure; }
                    break;
                }
            }
            int memFinal = Math.Max(0, mem - o.MemMargin);

            if (coreFinal == 0 && memFinal == 0)
            {
                await b.SetOffsetsAsync(0, 0);
                return Done(ScanOutcome.NoHeadroom, 0, 0, highestCore, highestMem, stopped
                    ? $"Scan stopped early ({stopWhy}) before finding usable headroom. Offsets are back at 0."
                    : "No usable overclock headroom after the safety margin. Offsets are back at 0; this GPU is best left at stock (an undervolt may still help heat and noise).");
            }

            // 4. Confirm the combination with a longer run; back off once more if it fails.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var r = await Try(coreFinal, memFinal, o.Confirm, attempt == 0 ? "Confirmation run" : "Confirmation run (lower)");
                if (r?.Passed == true)
                    return Done(ScanOutcome.Found, coreFinal, memFinal, highestCore, highestMem,
                        $"Found: core {coreFinal:+0;-0;0} MHz, memory {memFinal:+0;-0;0} MHz (highest that passed: core +{highestCore}" +
                        (highestMem is { } hm ? $", memory +{hm}" : "") + " MHz; a safety margin was taken off)." +
                        (stopped ? $" The scan stopped early ({stopWhy}), so there may be a little more headroom." : "") +
                        " This is a starting point, not proven stable: run an extended test and play for a while before relying on it.");
                if (r is null || r.Fatal) break;
                coreFinal = Math.Max(0, coreFinal - o.CoreMargin);
                memFinal = Math.Max(0, memFinal - o.MemMargin);
                if (coreFinal == 0 && memFinal == 0) break;
            }
            await b.SetOffsetsAsync(0, 0);
            return Done(ScanOutcome.NoHeadroom, 0, 0, highestCore, highestMem, "The combination didn't pass the confirmation run even after backing off. Offsets are back at 0.");
        }
        catch (OperationCanceledException)
        {
            return new(ScanOutcome.Cancelled, 0, 0, null, null, "Scan cancelled. The GPU's previous settings are restored.", log);
        }
    }

    private static string Details(ScanStepResult r) =>
        (r.AvgCoreMhz is { } m ? $" · {m} MHz average" : "") + (r.MaxTempC is { } t ? $" · {t:0} °C max" : "");
}
