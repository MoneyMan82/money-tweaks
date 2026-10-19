using MoneyTweaks.Core.Advisor;

namespace MoneyTweaks.Tests;

/// <summary>
/// Bottleneck & Upgrade Advisor against synthetic captures with known causes: GPU-bound, CPU-bound, frame-capped,
/// memory pressure and "neither saturated". The diagnosis must use several signals, say uncertain when it is, and the
/// recommendations must follow the evidence, the budget and the "free before paid" rule, with exactly three or a reason.
/// </summary>
public class AdvisorTests
{
    private static HardwareFacts Hw(bool xmpOff = false, bool single = false) => new()
    {
        Cpu = "AMD Ryzen 5 7600X 6-Core Processor", CpuVendor = "AMD", Cores = 6, Threads = 12, Socket = "AM5", Platform = "AMD AM5 (DDR5)",
        Gpu = "NVIDIA GeForce RTX 4070 SUPER", GpuVendor = "NVIDIA", VramGb = 12, RamGb = single ? 16 : 32, RamType = "DDR5", RamModules = single ? 1 : 2, RamSlots = 4,
        RamRatedMts = 6000, RamConfiguredMts = xmpOff ? 4800 : 6000, DualChannel = !single, GpuPowerLimitW = 220, GpuMaxPowerLimitW = 242,
    };

    private static Capture Cap(double fps, double gpu, double core, double total, int seconds = 30, double jitter = 0.02, double refresh = 240, bool powerCapped = false,
        double availMb = 12000, double commit = 50, double faults = 2, double other = 3, double vramUsed = 6000, double latency = 2, double stutterEvery = 0)
    {
        var c = new Capture { Seconds = seconds, RefreshHz = refresh, What = "test" };
        var rnd = new Random(3);
        for (int s = 0; s < seconds; s++)
        {
            double f = fps * (1 + (rnd.NextDouble() - 0.5) * 2 * jitter);
            int frames = (int)Math.Round(f);
            for (int i = 0; i < frames; i++)
                c.FrameMs.Add(stutterEvery > 0 && i % (int)stutterEvery == 0 ? 1000.0 / f * 4 : 1000.0 / f);
            c.Samples.Add(new TelemetrySample(total, core, 105, 70, gpu, 2700, 68, 200, 220, powerCapped, false, vramUsed, 12282,
                availMb, 32000, commit, faults, latency > 20 ? 5 : 0.3, latency, total - other, other, frames));
        }
        return c;
    }

    private static AdvisorGoal Goal(double target = 240, double? budget = null) => new() { Game = "Fortnite", TargetFps = target, BudgetEur = budget, Width = 1920, Height = 1080 };

    [Fact]
    public void Gpu_bound_is_diagnosed_from_gpu_load_and_cpu_headroom_and_free_fixes_come_first()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 140, gpu: 98, core: 55, total: 30, powerCapped: true), Hw(), Goal());
        Assert.Equal(Limiter.Gpu, d.Primary.Kind);
        Assert.Contains(d.Factors, f => f.Kind == Limiter.GpuPower);
        Assert.True(d.CpuCeiling!.Low > 140);                                 // the CPU could do more
        Assert.Contains(d.Primary.Evidence, e => e.Contains("CPU has headroom"));
        var r = RecommendationEngine.Recommend(d, Hw(), Goal(), new Dictionary<string, PriceEntry>());
        Assert.Equal(3, r.Top.Count);
        Assert.Equal(RecTier.Free, r.Top[0].Tier);
        Assert.Contains(r.Top, x => x.Id == "free.settings");
        Assert.DoesNotContain(r.All, x => x.Id == "hw.cpu");                    // the CPU isn't the limit: never suggested
        var gpuUp = r.All.Single(x => x.Id == "hw.gpu");
        Assert.False(gpuUp.CostKnown);                                           // no invented price
        Assert.Contains("Price not entered", gpuUp.CostText);
        Assert.All(r.Top, x => Assert.False(string.IsNullOrEmpty(x.WhyRanked)));
    }

    [Fact]
    public void Cpu_bound_needs_an_underused_gpu_and_a_busy_core_and_xmp_off_is_a_free_fix()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 120, gpu: 62, core: 96, total: 45), Hw(xmpOff: true), Goal());
        Assert.Equal(Limiter.Cpu, d.Primary.Kind);
        Assert.True(d.GpuCeiling!.Low > 120);
        var r = RecommendationEngine.Recommend(d, Hw(xmpOff: true), Goal(), new Dictionary<string, PriceEntry>());
        Assert.Contains(r.Top, x => x.Id == "free.xmp");
        Assert.DoesNotContain(r.All, x => x.Id is "hw.gpu" or "free.settings");  // GPU fixes don't help a CPU limit
        Assert.Contains(r.All, x => x.Id == "hw.cpu" && x.Requirements.Any(q => q.Contains("AM5")));
    }

    [Fact]
    public void A_frame_cap_is_recognised_and_removing_it_is_the_top_free_fix()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 144, gpu: 55, core: 50, total: 25, jitter: 0.005, refresh: 144), Hw(), Goal(target: 200));
        Assert.Equal(Limiter.FrameCap, d.Primary.Kind);
        Assert.Equal(144, d.CapFps);
        var r = RecommendationEngine.Recommend(d, Hw(), Goal(target: 200), new Dictionary<string, PriceEntry>());
        Assert.Equal("free.uncap", r.Top[0].Id);
        Assert.NotNull(r.Top[0].Projected);
        Assert.DoesNotContain(r.Top, x => x.Tier == RecTier.Hardware);          // a free fix is projected to reach the goal
        Assert.Contains(r.Top[0].Requirements, q => q.Contains("144 Hz"));
    }

    [Fact]
    public void Neither_side_saturated_says_uncertain_and_does_not_pad_the_list()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 90, gpu: 55, core: 50, total: 25, jitter: 0.15), Hw(), Goal());
        Assert.Equal(Limiter.Uncertain, d.Primary.Kind);
        var r = RecommendationEngine.Recommend(d, Hw(), Goal(), new Dictionary<string, PriceEntry>());
        Assert.True(r.Top.Count < 3);
        Assert.NotNull(r.WhyFewer);
        Assert.DoesNotContain(r.All, x => x.Tier == RecTier.Hardware);
    }

    [Fact]
    public void Memory_pressure_and_storage_stutter_need_their_own_evidence()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 100, gpu: 70, core: 70, total: 50, availMb: 600, commit: 94, faults: 450, latency: 80, stutterEvery: 40), Hw(), Goal());
        Assert.Contains(d.Factors, f => f.Kind == Limiter.Memory && f.Score >= 0.75);
        Assert.Contains(d.Factors, f => f.Kind == Limiter.Storage);
        var calm = BottleneckEngine.Diagnose(Cap(fps: 100, gpu: 97, core: 50, total: 30), Hw(), Goal());
        Assert.DoesNotContain(calm.Factors, f => f.Kind is Limiter.Memory or Limiter.Storage);
    }

    [Fact]
    public void Entered_prices_and_budget_change_the_ranking()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 120, gpu: 62, core: 96, total: 45), Hw(single: true), Goal(target: 200, budget: 150));
        var prices = new Dictionary<string, PriceEntry> { ["hw.cpu"] = new() { Eur = 320, Source = "Jimm's", Product = "Ryzen 7 7800X3D" }, ["lowcost.dualchannel"] = new() { Eur = 60, Source = "Verkkokauppa.com" } };
        var r = RecommendationEngine.Recommend(d, Hw(single: true), Goal(target: 200, budget: 150), prices);
        Assert.Contains(r.OverBudget, x => x.Id == "hw.cpu");
        Assert.DoesNotContain(r.Top, x => x.Id == "hw.cpu");
        var dual = r.All.Single(x => x.Id == "lowcost.dualchannel");
        Assert.True(dual.CostKnown);
        Assert.Contains("Verkkokauppa.com", dual.CostText);
    }

    [Fact]
    public void Already_at_target_means_nothing_to_buy_and_simulator_labels_projections()
    {
        var d = BottleneckEngine.Diagnose(Cap(fps: 250, gpu: 97, core: 40, total: 25), Hw(), Goal(target: 240));
        var r = RecommendationEngine.Recommend(d, Hw(), Goal(target: 240), new Dictionary<string, PriceEntry>());
        Assert.Empty(r.Top);
        Assert.Contains("already reach your target", r.WhyFewer);
        Assert.NotEmpty(r.All);                                                  // the planner still shows the options
        var sim = RecommendationEngine.Simulate(d, Goal(target: 240), r);
        Assert.True(sim[0].Measured);
        Assert.All(sim.Skip(1), s => Assert.False(s.Measured));
        Assert.Contains(sim, s => s.Label.Contains("CPU upgrade") && s.Low is null); // not the limit: not projected
    }

    [Fact]
    public void Socket_inference_only_claims_what_it_recognises_and_retailers_are_local()
    {
        Assert.Equal("AM5", HardwareFactsDetector.Socket("AMD Ryzen 5 7600X 6-Core Processor").socket);
        Assert.Equal("AM4", HardwareFactsDetector.Socket("AMD Ryzen 7 5800X3D 8-Core Processor").socket);
        Assert.Equal("LGA1700", HardwareFactsDetector.Socket("13th Gen Intel(R) Core(TM) i5-13600K").socket);
        Assert.StartsWith("laptop", HardwareFactsDetector.Socket("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics").socket);
        Assert.Null(HardwareFactsDetector.Socket("Some Unknown CPU 9000").socket);
        Assert.Contains(RecommendationEngine.RetailerSearch("DDR5 32GB", "FI"), x => x.url.StartsWith("https://www.jimms.fi", StringComparison.Ordinal));
        Assert.All(RecommendationEngine.RetailerSearch("graphics card", "XX"), x => Assert.StartsWith("https://", x.url));
    }
}
