using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Tests;

/// <summary>CPU & GPU tuning. All writes go to a fake backend; the only real calls are read-only queries.</summary>
[Collection("registry")]
public class TuningTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-tuning-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBackend _fake = new();
    private readonly TuningEngine.IBackend _real = TuningEngine.Backend;

    public TuningTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); TuningEngine.Backend = _fake; }
    public void Dispose() { TuningEngine.Backend = _real; try { Directory.Delete(_tmp, true); } catch { } }

    private sealed class FakeBackend : TuningEngine.IBackend
    {
        public double Power = 220;
        public bool GpuLocked;
        public readonly Dictionary<string, (int ac, int dc)> Cpu = new() { ["PERFBOOSTMODE"] = (2, 2), ["PROCTHROTTLEMAX"] = (100, 100), ["PERFEPP"] = (0, 50) };
        public string? FailOn;          // alias or "pl"/"lgc" that the "driver" refuses
        public bool IgnorePowerWrite;   // driver says OK but keeps the old value
        public List<string> Calls = [];
        public double? ReadPowerLimit(int gpu) => Power;
        public (bool, string) SetPowerLimit(int gpu, double w) { Calls.Add($"pl {w}"); if (FailOn == "pl") return (false, "refused"); if (!IgnorePowerWrite) Power = w; return (true, "ok"); }
        public (bool, string) LockGpu(int gpu, int min, int max) { Calls.Add($"lgc {min},{max}"); if (FailOn == "lgc") return (false, "not supported"); GpuLocked = true; return (true, "ok"); }
        public (bool, string) ResetGpu(int gpu) { Calls.Add("rgc"); GpuLocked = false; return (true, "ok"); }
        public (bool, string) LockMem(int gpu, int min, int max) => (true, "ok");
        public (bool, string) ResetMem(int gpu) => (true, "ok");
        public (int?, int?) ReadCpu(string alias) => Cpu.TryGetValue(alias, out var v) ? (v.ac, v.dc) : (null, null);
        public (bool, string) SetCpu(string alias, int ac, int dc) { Calls.Add($"{alias} {ac}/{dc}"); if (FailOn == alias) return (false, "refused"); Cpu[alias] = (ac, dc); return (true, "ok"); }
    }

    private static readonly NvidiaGpuLimits Gpu = new(0, "RTX 4070 SUPER", "617.14", 220, 220, 100, 242,
        Enumerable.Range(0, 200).Select(i => 210 + i * 15).Where(c => c <= 3105).ToList(), [405, 10501], true); // 210…3105 MHz in 15 MHz steps, like the real card

    private static Dictionary<string, CpuPowerSetting> Cpu() => new()
    {
        ["PERFBOOSTMODE"] = new("PERFBOOSTMODE", "Boost mode", "", 0, 6, 1, CpuPowerSettings.BoostChoices, 2, 2),
        ["PROCTHROTTLEMAX"] = new("PROCTHROTTLEMAX", "Maximum processor state", "", 0, 100, 1, null, 100, 100),
        ["PERFEPP"] = new("PERFEPP", "Energy performance preference", "", 0, 100, 1, null, 0, 50),
    };

    // ---- apply, verify, rollback ----------------------------------------------------------------------------------

    [Fact]
    public void Apply_backs_up_first_verifies_each_value_and_reverts_to_the_originals()
    {
        var st = new TuningState();
        var (ok, results) = TuningEngine.Apply(new TuningSettings { GpuPowerLimitW = 187, CpuBoostMode = 4, CpuEpp = 33 }, st, "Efficiency");
        Assert.True(ok);
        Assert.All(results, r => Assert.Equal(ItemStatus.Verified, r.Status));
        Assert.Equal(220, st.OrigGpuPowerLimitW);
        Assert.Equal([0, 50], st.OrigCpu["PERFEPP"]);
        Assert.True(st.PendingConfirmation);

        // A second apply must not overwrite the true originals with tuned values.
        TuningEngine.Apply(new TuningSettings { GpuPowerLimitW = 150 }, st, "Quiet");
        Assert.Equal(220, st.OrigGpuPowerLimitW);

        var (rok, _) = TuningEngine.RevertAll(st);
        Assert.True(rok);
        Assert.Equal(220, _fake.Power);
        Assert.Equal((2, 2), _fake.Cpu["PERFBOOSTMODE"]);
        Assert.Equal((0, 50), _fake.Cpu["PERFEPP"]);
        Assert.False(st.PendingConfirmation);
        Assert.True(File.Exists(Path.Combine(_tmp, "tuning.json")));
    }

    [Fact]
    public void A_failed_item_undoes_everything_from_that_apply()
    {
        _fake.FailOn = "PERFEPP";
        var st = new TuningState();
        var (ok, results) = TuningEngine.Apply(new TuningSettings { GpuPowerLimitW = 187, CpuBoostMode = 4, CpuEpp = 33 }, st);
        Assert.False(ok);
        Assert.Contains(results, r => r.Status == ItemStatus.Failed && r.Item.Contains("energy"));
        Assert.All(results.Where(r => r.Status != ItemStatus.Failed), r => Assert.Equal(ItemStatus.RolledBack, r.Status));
        Assert.Equal(220, _fake.Power);                        // power limit put back
        Assert.Equal((2, 2), _fake.Cpu["PERFBOOSTMODE"]);      // boost mode put back
    }

    [Fact]
    public void Accepted_but_not_kept_is_a_failure_not_a_success()
    {
        _fake.IgnorePowerWrite = true; // nvidia-smi says OK, read-back shows the old value
        var (ok, results) = TuningEngine.Apply(new TuningSettings { GpuPowerLimitW = 187 }, new TuningState());
        Assert.False(ok);
        Assert.Equal(ItemStatus.Failed, results.Single().Status);
    }

    [Fact]
    public void Clock_lock_is_applied_but_not_claimed_verified_and_revert_resets_it()
    {
        var st = new TuningState();
        var (ok, results) = TuningEngine.Apply(new TuningSettings { GpuClockLock = (210, 2400) }, st);
        Assert.True(ok);
        Assert.Equal(ItemStatus.Applied, results.Single().Status); // nvidia-smi can't read a lock back; verified only under load
        Assert.True(st.GpuClocksLocked && _fake.GpuLocked);
        TuningEngine.RevertAll(st);
        Assert.False(_fake.GpuLocked);
        Assert.Contains("rgc", _fake.Calls);
    }

    // ---- validation & profiles ---------------------------------------------------------------------------------------

    [Fact]
    public void Values_outside_the_drivers_range_or_unsupported_clocks_are_refused()
    {
        Assert.NotEmpty(TuningEngine.Validate(new TuningSettings { GpuPowerLimitW = 300 }, Gpu, Cpu()));
        Assert.NotEmpty(TuningEngine.Validate(new TuningSettings { GpuPowerLimitW = 50 }, Gpu, Cpu()));
        Assert.NotEmpty(TuningEngine.Validate(new TuningSettings { GpuClockLock = (210, 2401) }, Gpu, Cpu())); // not a supported clock
        Assert.NotEmpty(TuningEngine.Validate(new TuningSettings { CpuMaxState = 120 }, Gpu, Cpu()));
        Assert.NotEmpty(TuningEngine.Validate(new TuningSettings { GpuPowerLimitW = 200 }, null, Cpu())); // no adjustable GPU
        Assert.Empty(TuningEngine.Validate(new TuningSettings { GpuPowerLimitW = 187, GpuClockLock = (210, 2400), CpuMaxState = 99 }, Gpu, Cpu()));
    }

    [Fact]
    public void Profiles_stay_inside_the_reported_limits()
    {
        foreach (var p in TuningEngine.Profiles(Gpu, Cpu()))
            Assert.Empty(TuningEngine.Validate(p.Settings, Gpu, Cpu()));
        var quiet = TuningEngine.Profiles(Gpu, Cpu()).Single(p => p.Id == "quiet");
        Assert.True(quiet.Settings.GpuPowerLimitW < 220 && quiet.Settings.GpuClockLock is { max: < 3105 });
        Assert.DoesNotContain(TuningEngine.Profiles(Gpu, Cpu()), p => p.Settings.GpuPowerLimitW > Gpu.MaxPowerLimitW);
    }

    // ---- parsing ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Parses_nvidia_smi_supported_clocks_and_powercfg_output()
    {
        var (g, m) = NvidiaSmi.ParseSupportedClocks("    Memory                                         : 10501 MHz\n            Graphics                                   : 3105 MHz\n            Graphics                                   : 3090 MHz\n        Memory : 405 MHz\n            Graphics : 210 MHz\n");
        Assert.Equal([210, 3090, 3105], g);
        Assert.Equal([405, 10501], m);

        var range = CpuPowerSettings.Parse("Power Setting GUID: x  (Maximum processor state)\n      Minimum Possible Setting: 0x00000000\n      Maximum Possible Setting: 0x00000064\n      Possible Settings increment: 0x00000001\n      Possible Settings units: %\n    Current AC Power Setting Index: 0x00000063\n    Current DC Power Setting Index: 0x00000064\n");
        Assert.Equal((0, 100, 1, 99, 100), (range.min!.Value, range.max!.Value, range.step!.Value, range.ac!.Value, range.dc!.Value));
        var choice = CpuPowerSettings.Parse("      Possible Setting Index: 000\n      Possible Setting Friendly Name: Disabled\n      Possible Setting Index: 001\n      Possible Setting Friendly Name: Enabled\n    Current AC Power Setting Index: 0x00000002\n    Current DC Power Setting Index: 0x00000001\n");
        Assert.Equal((2, 1, 2), (choice.ac!.Value, choice.dc!.Value, choice.choices));
    }

    // ---- capabilities: honest support levels ------------------------------------------------------------------------

    [Fact]
    public void Only_documented_interfaces_are_direct_everything_else_is_handoff_or_unavailable()
    {
        var ryzen = new TuningInventory { Cpu = "AMD Ryzen 5 7600X 6-Core Processor", CpuVendor = "AMD", Gpus = [new("NVIDIA GeForce RTX 4070 SUPER", "NVIDIA", "32.0.15.7114", "")], RamRatedMts = 6000, RamConfiguredMts = 4800 };
        var caps = TuningCapabilities.For(ryzen, [Gpu]);
        Assert.All(caps.Where(c => c.Support == Support.Direct), c => Assert.True(c.Name.Contains("Power limit") || c.Name.Contains("clock limit") || c.Name.Contains("Boost mode")
            || c.Name == "CPU temperature")); // read-only, from Afterburner's or HWiNFO's published sensor data when one of them runs
        Assert.Equal(Support.Handoff, caps.Single(c => c.Name.StartsWith("Precision Boost Overdrive")).Support);
        Assert.Equal(Support.Handoff, caps.Single(c => c.Name.StartsWith("Curve Optimizer")).Support);
        Assert.Equal(Support.Handoff, caps.Single(c => c.Name.StartsWith("Core/memory clock offsets")).Support);
        Assert.Equal(Support.Unavailable, caps.Single(c => c.Name.StartsWith("Memory timings")).Support);
        Assert.Contains("probably off", caps.Single(c => c.Name.StartsWith("XMP")).Why);

        var lockedIntel = new TuningInventory { Cpu = "Intel(R) Core(TM) i5-12400F", CpuVendor = "Intel" };
        Assert.Equal(Support.Unavailable, TuningCapabilities.For(lockedIntel, []).Single(c => c.Name.StartsWith("Multiplier")).Support);
        var kIntel = lockedIntel with { Cpu = "Intel(R) Core(TM) i7-13700K" };
        Assert.Equal(Support.Handoff, TuningCapabilities.For(kIntel, []).Single(c => c.Name.StartsWith("Multiplier")).Support);
    }

    [Fact]
    public void Integrated_radeon_graphics_is_not_offered_as_a_tunable_amd_gpu()
    {
        var igpu = new TuningInventory { Cpu = "AMD Ryzen 5 7600X", CpuVendor = "AMD", Gpus = [new("AMD Radeon(TM) Graphics", "AMD", "32.0.21045.5002", "")] };
        Assert.False(igpu.HasAmdGpu);
        Assert.DoesNotContain(TuningCapabilities.For(igpu, []), c => c.Name.Contains("(AMD)"));
        var rx = igpu with { Gpus = [new("AMD Radeon RX 7800 XT", "AMD", "32.0.21045.5002", "")] };
        Assert.True(rx.HasAmdGpu);
        Assert.Contains(TuningCapabilities.For(rx, []), c => c.Name.Contains("(AMD)") && c.Support == Support.Handoff);
    }

    // ---- stability test ---------------------------------------------------------------------------------------------

    [Fact]
    public void Workload_is_deterministic_and_the_rules_flag_real_problems()
    {
        Assert.Equal(StabilityTest.Workload(3), StabilityTest.Workload(3));
        Assert.NotEqual(StabilityTest.Workload(3), StabilityTest.Workload(4));
        var cool = new StabilitySample(DateTime.Now, 100, 70, 65, 200, 2700, false, true);
        Assert.Empty(StabilityTest.Evaluate([cool], new(), 0, 0, 0));
        Assert.NotEmpty(StabilityTest.Evaluate([cool], new(), 1, 0, 0));  // wrong result
        Assert.NotEmpty(StabilityTest.Evaluate([cool], new(), 0, 1, 0));  // WHEA
        Assert.NotEmpty(StabilityTest.Evaluate([cool], new(), 0, 0, 1));  // driver reset
        Assert.NotEmpty(StabilityTest.Evaluate([cool with { GpuTempC = 88 }], new(), 0, 0, 0));
    }

    [Fact]
    public async Task Short_clean_run_is_only_preliminary_and_a_hot_gpu_fails_fast()
    {
        var clean = await StabilityTest.RunAsync(TimeSpan.FromSeconds(1.2), () => new(DateTime.Now, 100, null, 60, 150, 2700, false, false), null, CancellationToken.None,
            threads: 2, sampleEvery: TimeSpan.FromMilliseconds(300), events: _ => (0, 0));
        Assert.Equal(StabilityVerdict.NoErrorsPreliminary, clean.Verdict);
        Assert.Contains("does not mean the settings are stable", clean.Summary);
        Assert.True(clean.CpuIterations > 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hot = await StabilityTest.RunAsync(TimeSpan.FromMinutes(5), () => new(DateTime.Now, 100, null, 90, 240, 2800, true, false), null, CancellationToken.None,
            threads: 2, sampleEvery: TimeSpan.FromMilliseconds(200), events: _ => (0, 0));
        Assert.Equal(StabilityVerdict.Failed, hot.Verdict);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    // ---- real, read-only --------------------------------------------------------------------------------------------

    [Fact]
    public void Reads_this_pcs_real_limits_without_changing_anything()
    {
        var max = CpuPowerSettings.Read(CpuPowerSettings.MaxState);
        Assert.True(max.Readable);
        Assert.InRange(max.Ac!.Value, max.Min, max.Max);
        if (!NvidiaSmi.Available) return;
        foreach (var g in NvidiaSmi.Limits())
        {
            if (g.PowerLimitSupported) Assert.InRange(g.PowerLimitW!.Value, g.MinPowerLimitW!.Value, g.MaxPowerLimitW!.Value);
            Assert.All(g.GraphicsClocks, c => Assert.InRange(c, 1, 5000));
        }
    }
}
