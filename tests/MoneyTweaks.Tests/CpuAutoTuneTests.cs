using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Tests;

/// <summary>CPU auto tuning: boost scan ranking and restore, the per-core undervolt assistant's decisions, core topology.</summary>
public class CpuAutoTuneTests
{
    private sealed class FakeCpu : ICpuTuneBackend
    {
        public (int b, int e) Now = (2, 0);
        public int Restores, Measures;
        public bool Refuse;
        public int? CancelAt;
        public Func<int, int, CpuMeasure> Perf = (_, _) => new(100, 1000, 80, 120);

        public Task<bool> SetAsync(int boostMode, int epp) { if (Refuse) return Task.FromResult(false); Now = (boostMode, epp); return Task.FromResult(true); }
        public Task RestoreAsync() { Restores++; Now = (2, 0); return Task.CompletedTask; }
        public Task<CpuMeasure> MeasureAsync(CancellationToken ct)
        {
            if (CancelAt is { } c && Measures >= c) throw new OperationCanceledException();
            Measures++;
            return Task.FromResult(Perf(Now.b, Now.e));
        }
    }

    [Fact]
    public async Task Recommends_the_lowest_power_option_that_is_as_fast_and_puts_settings_back()
    {
        var cpu = new FakeCpu
        {
            Perf = (b, e) => (b, e) switch
            {
                (2, 0) => new(100, 1000, 85, 140),
                (4, 25) => new(99.5, 995, 78, 115),   // within noise of the fastest, less power
                (3, 50) => new(93, 900, 70, 95),       // clearly slower
                _ => new(85, 800, 65, 80),
            },
        };
        var r = await CpuAutoTune.RunAsync(cpu, null, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.Equal("Balanced boost", r.Recommended!.Candidate.Name);
        Assert.Equal("Maximum boost", r.Fastest!.Candidate.Name);
        Assert.Equal((2, 0), cpu.Now);       // original restored
        Assert.Equal(1, cpu.Restores);
        Assert.Equal(CpuAutoTune.Candidates.Count + 2, cpu.Measures); // before + every candidate + after
        Assert.Contains("already as fast", r.Summary);              // current settings were the fastest
    }

    [Fact]
    public void Without_sensors_only_speed_counts_and_noise_hides_tiny_differences()
    {
        CpuMeasure M(double s, double m) => new(s, m, null, null);
        var raw = CpuAutoTune.Candidates.Select((c, i) => (c, M(100 + (i == 1 ? 6 : 0), 1000))).ToList();
        var r = CpuAutoTune.Evaluate(M(100, 1000), M(102, 1000), raw);
        Assert.Equal("Balanced boost", r.Recommended!.Candidate.Name);
        Assert.Equal(r.Fastest, r.Recommended);
        Assert.True(r.NoisePct >= 1.9);
        Assert.Contains("only speed was compared", r.Summary);
        var flat = CpuAutoTune.Evaluate(M(100, 1000), M(100, 1000), CpuAutoTune.Candidates.Select(c => (c, M(100.5, 1000))).ToList());
        Assert.Contains("already as fast", flat.Summary);
    }

    [Fact]
    public async Task Stop_refusal_and_instability_all_restore_the_original_settings()
    {
        var cancel = new FakeCpu { CancelAt = 2 };
        var r1 = await CpuAutoTune.RunAsync(cancel, null, CancellationToken.None);
        Assert.False(r1.Completed); Assert.Equal((2, 0), cancel.Now); Assert.Equal(1, cancel.Restores);

        var refuse = new FakeCpu { Refuse = true };
        var r2 = await CpuAutoTune.RunAsync(refuse, null, CancellationToken.None);
        Assert.False(r2.Completed); Assert.Equal(1, refuse.Restores);

        var unstable = new FakeCpu { Perf = (_, _) => throw new InvalidOperationException("The CPU produced 1 wrong result(s)") };
        var r3 = await CpuAutoTune.RunAsync(unstable, null, CancellationToken.None);
        Assert.False(r3.Completed); Assert.Contains("wrong result", r3.Summary); Assert.Equal(1, unstable.Restores);
    }

    private static CoreTestResult Pass(int core) => new(core, core * 2, true, null, 100);
    private static CoreTestResult Fail(int core) => new(core, core * 2, false, "wrong calculation results", 100);

    [Fact]
    public void Curve_optimizer_steps_down_backs_off_once_and_finishes_per_core()
    {
        var st = new CurveState();
        st.Reset(2, perCore: true);
        st.Values = [-15, -20];

        // Core 0 passes at -15 → -20. Core 1 fails at -20 → -15.
        var a = CurveAssistant.Advise(st, [Pass(0), Fail(1)], CurveRules.Amd);
        Assert.Equal([-20, -15], a.Next);
        Assert.Equal(-20, st.FailedAt[1]);
        st.Values = a.Next;

        // Core 0 fails at -20 → -15; core 1 passes at -15 and -20 already failed → done at -15.
        var b = CurveAssistant.Advise(st, [Fail(0), Pass(1)], CurveRules.Amd);
        Assert.Equal([-15, -15], b.Next);
        Assert.True(st.Done[1]);
        st.Values = b.Next;

        // Core 0 passes at -15, -20 failed before → done. Everything finished, nothing left to change.
        var c = CurveAssistant.Advise(st, [Pass(0), Pass(1)], CurveRules.Amd);
        Assert.True(c.AllDone);
        Assert.False(c.Changed);
        Assert.Equal([-15, -15], c.Next);
    }

    [Fact]
    public void Floor_unstable_at_zero_and_intel_single_offset()
    {
        var st = new CurveState();
        st.Reset(2, perCore: true);
        st.Values = [-30, 0];
        var a = CurveAssistant.Advise(st, [Pass(0), Fail(1)], CurveRules.Amd);
        Assert.True(st.Done[0]);                       // -35 is past Ryzen Master's -30
        Assert.Equal(0, a.Next[1]);
        Assert.Contains("unstable without any undervolt", a.Lines[1]);

        var intel = new CurveState();
        intel.Reset(1, perCore: false);
        intel.Values = [-40];
        var b = CurveAssistant.Advise(intel, [Pass(0), Pass(1), Fail(5)], CurveRules.Intel);
        Assert.Equal([-30], b.Next);                    // any core failing fails the shared offset
        intel.Values = b.Next;
        var c = CurveAssistant.Advise(intel, [Pass(0), Pass(1)], CurveRules.Intel);
        Assert.True(c.AllDone);                          // -40 already failed
        Assert.Equal(" mV", CurveRules.Intel.Unit);
    }

    [Fact]
    public void Physical_cores_are_one_logical_cpu_each()
    {
        var cores = CurveAssistant.PhysicalCores();
        Assert.NotEmpty(cores);
        Assert.True(cores.Count <= Environment.ProcessorCount);
        Assert.Equal(cores.Count, cores.Distinct().Count());
        Assert.All(cores, c => Assert.InRange(c, 0, Environment.ProcessorCount - 1));
    }

    [Fact]
    public async Task Per_core_test_pins_one_thread_and_checks_its_answers()
    {
        var core = CurveAssistant.PhysicalCores()[0];
        var r = await CurveAssistant.TestCoreAsync(0, core, TimeSpan.FromSeconds(2), CancellationToken.None, wheaSince: _ => 0);
        Assert.True(r.Passed, r.Failure);
        Assert.True(r.Iterations > 0);
        var whea = await CurveAssistant.TestCoreAsync(0, core, TimeSpan.FromSeconds(1), CancellationToken.None, wheaSince: _ => 2);
        Assert.False(whea.Passed);
        Assert.Contains("WHEA", whea.Failure);
        var hot = await CurveAssistant.TestCoreAsync(0, core, TimeSpan.FromSeconds(3), CancellationToken.None, wheaSince: _ => 0, cpuTemp: () => 96);
        Assert.False(hot.Passed);
    }
}
