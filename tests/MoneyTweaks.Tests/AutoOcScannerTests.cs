using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Tests;

/// <summary>The automatic overclock scan against a simulated GPU: it fails any step above its real limits. Nothing touches hardware.</summary>
public class AutoOcScannerTests
{
    private sealed class FakeGpu(int coreLimit, int memLimit) : IOcScanBackend
    {
        public int Core, Mem, Recoveries;
        public bool FailsAtStock, RefuseSet;
        public double Temp = 65;
        public int? CancelAfterLoads;
        public List<(int core, int mem, TimeSpan d)> Loads = [];

        public Task<bool> SetOffsetsAsync(int core, int mem) { if (RefuseSet) return Task.FromResult(false); Core = core; Mem = mem; return Task.FromResult(true); }

        public Task<ScanStepResult> LoadAsync(TimeSpan d, CancellationToken ct)
        {
            Loads.Add((Core, Mem, d));
            if (CancelAfterLoads is { } n && Loads.Count > n) throw new OperationCanceledException();
            if (Temp >= 87) return Task.FromResult(new ScanStepResult(false, "too hot", Fatal: true, MaxTempC: Temp));
            bool ok = !FailsAtStock && Core <= coreLimit && Mem <= memLimit;
            return Task.FromResult(new ScanStepResult(ok, ok ? null : "GPU driver reset", MaxTempC: Temp, AvgCoreMhz: 2700 + Core));
        }

        public Task RecoverAsync() { Recoveries++; Core = 0; Mem = 0; return Task.CompletedTask; }
    }

    private static readonly ScanOptions Opts = new(CoreMax: 300, MemMax: 1500, StepTime: TimeSpan.FromSeconds(1), ConfirmTime: TimeSpan.FromSeconds(2));

    private static Task<ScanResult> Run(FakeGpu g, ScanOptions? o = null, CancellationToken ct = default) => AutoOcScanner.RunAsync(g, o ?? Opts, null, ct);

    [Fact]
    public async Task Finds_the_limit_refines_once_and_takes_a_safety_margin()
    {
        var g = new FakeGpu(coreLimit: 195, memLimit: 1100);
        var r = await Run(g);
        Assert.Equal(ScanOutcome.Found, r.Outcome);
        Assert.Equal(195, r.HighestCorePassed);      // 180 passed, 210 failed, fine step 195 passed
        Assert.Equal(165, r.CoreMhz);                // minus the 30 MHz margin
        Assert.Equal(1000, r.HighestMemPassed);      // 1250 failed
        Assert.Equal(750, r.MemMhz);                 // minus the 250 MHz margin
        Assert.True(r.KeepsOffsets);
        Assert.Equal((165, 750), (g.Core, g.Mem));   // left at the result
        Assert.Contains(g.Loads, l => l.core == 165 && l.mem == 750 && l.d == Opts.Confirm); // confirmed with the longer run
        Assert.Contains("not proven stable", r.Summary);
    }

    [Fact]
    public async Task Memory_is_scanned_with_the_core_at_its_final_value_and_never_past_the_limits()
    {
        var g = new FakeGpu(coreLimit: 1000, memLimit: 5000);
        var r = await Run(g);
        Assert.All(g.Loads, l => Assert.True(l.core <= Opts.CoreMax && l.mem <= Opts.MemMax));
        Assert.All(g.Loads.Where(l => l.mem > 0), l => Assert.Equal(r.CoreMhz, l.core));
        Assert.Equal(270, r.CoreMhz);
        Assert.Contains("every step up to the scan limit", string.Join("\n", r.Log));
    }

    [Fact]
    public async Task A_gpu_that_fails_at_stock_is_not_overclocked()
    {
        var g = new FakeGpu(500, 5000) { FailsAtStock = true };
        var r = await Run(g);
        Assert.Equal(ScanOutcome.FailsAtStock, r.Outcome);
        Assert.Single(g.Loads);
        Assert.False(r.KeepsOffsets);
    }

    [Fact]
    public async Task Too_hot_stops_the_scan_without_calling_it_an_overclock_limit()
    {
        var g = new FakeGpu(500, 5000) { Temp = 90 };
        var r = await Run(g);
        Assert.Equal(ScanOutcome.Aborted, r.Outcome);
        Assert.Contains("cooling", r.Summary);
        Assert.Equal(0, g.Recoveries);
    }

    [Fact]
    public async Task No_headroom_puts_offsets_back_at_zero()
    {
        var g = new FakeGpu(coreLimit: 20, memLimit: 100);
        var r = await Run(g);
        Assert.Equal(ScanOutcome.NoHeadroom, r.Outcome);
        Assert.Equal((0, 0), (g.Core, g.Mem));
        Assert.False(r.KeepsOffsets);
    }

    [Fact]
    public async Task Cancel_and_refused_changes_keep_nothing()
    {
        var g = new FakeGpu(500, 5000) { CancelAfterLoads = 3 };
        var r = await Run(g);
        Assert.Equal(ScanOutcome.Cancelled, r.Outcome);
        Assert.False(r.KeepsOffsets);

        var refused = new FakeGpu(500, 5000) { RefuseSet = true };
        var r2 = await Run(refused);
        Assert.Equal(ScanOutcome.Aborted, r2.Outcome);
        Assert.Empty(refused.Loads);
    }

    [Fact]
    public void Options_follow_afterburners_ranges_with_realistic_caps()
    {
        var g = new AbGpu("gpu", 0, new AbRange(0, -1000, 1000, 0), new AbRange(0, -1000, 3000, 0), null, null, null, true, true, [], false);
        var o = ScanOptions.For(g);
        Assert.Equal(300, o.CoreMax);
        Assert.Equal(1500, o.MemMax);
        Assert.True(o.ScanMemory);
        var noMem = ScanOptions.For(g with { MemOffset = new AbRange(0, 0, 0, 0) });
        Assert.False(noMem.ScanMemory);
    }
}
