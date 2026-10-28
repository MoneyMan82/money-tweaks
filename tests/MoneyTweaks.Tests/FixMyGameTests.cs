using MoneyTweaks.Core.Advisor;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Scanner;

namespace MoneyTweaks.Tests;

/// <summary>Fix My Game: causes only from evidence, at most three actions, honest re-test verdicts.</summary>
public class FixMyGameTests
{
    private static TelemetrySample S(double gpu, double core, double? gpuT = null, double? cpuT = null, bool thermal = false, int frames = 60, double other = 2) =>
        new(core * 0.5, core, 100, cpuT, gpu, 1800, gpuT, 200, 220, false, thermal, 4000, 8192, 8000, 16384, 50, 5, 0.1, 1, 30, other, frames);

    private static (Capture c, Diagnosis d) Run(double frameMs, double gpu, double core, int seconds = 20, double? gpuT = null, double? cpuT = null, bool thermal = false, double spikeEvery = 0)
    {
        var c = new Capture { What = "Test game", Seconds = seconds, RefreshHz = 144 };
        int perSecond = (int)(1000 / frameMs);
        for (int i = 0; i < seconds; i++) c.Samples.Add(S(gpu, core, gpuT, cpuT, thermal, perSecond));
        for (int i = 0; i < seconds * perSecond; i++) c.FrameMs.Add(spikeEvery > 0 && i % (int)spikeEvery == 0 ? frameMs * 5 : frameMs);
        var hw = new HardwareFacts { Threads = 16, RamGb = 16, VramGb = 8 };
        return (c, BottleneckEngine.Diagnose(c, hw, new AdvisorGoal { TargetFps = 144 }));
    }

    private static Finding Finding(string id, params string[] tweaks) =>
        new(id, id, Severity.Low, "Test", ["evidence for " + id], "why " + id, "benefit", Confidence.Medium, tweaks, tweaks.Length == 0 ? "do this manually" : null);

    [Fact]
    public void Gpu_limited_low_fps_recommends_lighter_graphics_and_never_more_than_three_actions()
    {
        var (c, d) = Run(1000 / 70.0, gpu: 99, core: 50);
        var m = FixMyGame.FromCapture(GameProblem.LowFps, c, d, 144);
        var plan = FixMyGame.Plan(m, [Finding("power-saver"), Finding("game-dvr", "gen.background-recording"), Finding("ram-pressure"), Finding("reverted", "gen.game-mode")]);
        Assert.Contains("graphics card", plan.Causes[0].Title);
        Assert.Equal("step:gpu-settings", plan.Actions[0].Id);
        Assert.InRange(plan.Actions.Count, 1, 3);
        Assert.All(plan.Actions, a => Assert.DoesNotContain("+", a.Expect));   // no promised FPS numbers
        Assert.True(FixMyGame.StillAProblem(m));                                  // 70 FPS on a 144 Hz target
    }

    [Fact]
    public void A_flat_frame_rate_at_the_refresh_rate_is_a_cap_not_slow_hardware()
    {
        var (c, d) = Run(1000 / 144.0, gpu: 60, core: 40);
        var plan = FixMyGame.Plan(FixMyGame.FromCapture(GameProblem.LowFps, c, d, 144), []);
        Assert.Contains("limit", plan.Causes[0].Title);
        Assert.Equal("step:uncap", plan.Actions[0].Id);
        var delay = FixMyGame.Plan(FixMyGame.FromCapture(GameProblem.InputDelay, c, d, 144), []);
        Assert.Contains(delay.Actions, a => a.Id == "step:vsync");
        Assert.Contains(delay.Notes, n => n.Contains("can't measure"));
    }

    [Fact]
    public void Nothing_measured_means_no_invented_cause()
    {
        var (c, d) = Run(1000 / 240.0, gpu: 60, core: 40);
        var plan = FixMyGame.Plan(FixMyGame.FromCapture(GameProblem.Overheating, c, d, 144), []);
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Causes, x => x.Title.Contains("No clear cause") || x.Title.Contains("normal"));
        var ping = FixMyGame.FromNetwork(new DiagnosticReport(
            [new("Router", "192.168.1.1", new PingStats(80, 0, 1, 1, 1, 2, 0.3)), new("Cloudflare", "1.1.1.1", new PingStats(80, 0, 12, 12, 11, 14, 0.8))],
            [], "Ethernet 1000 Mbps", [], DateTime.Now));
        var p = FixMyGame.Plan(ping, [Finding("adapter-power", "net.adapter-power")]);
        Assert.Empty(p.Actions);                                                 // a measured-stable link gets no setting changes
        Assert.Contains("stable", p.Causes[0].Title);
        Assert.False(FixMyGame.StillAProblem(ping));
    }

    [Fact]
    public void Hot_gpu_gets_cooling_first_and_tuning_is_only_a_link()
    {
        var (c, d) = Run(1000 / 100.0, gpu: 99, core: 50, gpuT: 89, thermal: true);
        var plan = FixMyGame.Plan(FixMyGame.FromCapture(GameProblem.Overheating, c, d, 144), []);
        Assert.Equal("step:cooling", plan.Actions[0].Id);
        var tool = plan.Actions.FirstOrDefault(a => a.Kind == FixKind.Tool);
        Assert.True(tool is null || tool.PageTag == "tuning");
        Assert.DoesNotContain(plan.Actions, a => a.Kind == FixKind.Tweak);   // no system change for heat
    }

    [Fact]
    public void Wifi_trouble_inside_the_home_points_at_the_router_link()
    {
        var m = FixMyGame.FromNetwork(new DiagnosticReport(
            [new("Router", "192.168.1.1", new PingStats(80, 4, 9, 12, 2, 80, 18)), new("Cloudflare", "1.1.1.1", new PingStats(80, 5, 30, 35, 20, 120, 22))],
            [], "Wi-Fi 433 Mbps", [], DateTime.Now));
        var plan = FixMyGame.Plan(m, [Finding("adapter-power", "net.adapter-power")]);
        Assert.Contains("router", plan.Causes[0].Title);
        Assert.Equal("step:cable", plan.Actions[0].Id);
        Assert.True(FixMyGame.StillAProblem(m));
    }

    [Fact]
    public void Tweaks_are_only_offered_from_findings_and_stay_reversible_catalog_tweaks()
    {
        var (c, d) = Run(1000 / 60.0, gpu: 70, core: 98);
        var plan = FixMyGame.Plan(FixMyGame.FromCapture(GameProblem.LowFps, c, d, 144), [Finding("power-balanced", "gen.ultimate-plan")]);
        var t = Assert.Single(plan.Actions, a => a.Kind == FixKind.Tweak);
        Assert.Equal("gen.ultimate-plan", t.TweakId);
        Assert.NotNull(Core.Catalog.TweakCatalog.Find(t.TweakId!));
    }

    [Fact]
    public void Retest_verdicts_respect_run_to_run_noise()
    {
        var (c1, d1) = Run(1000 / 70.0, gpu: 99, core: 50);
        var (c2, d2) = Run(1000 / 72.0, gpu: 99, core: 50);   // +3%: noise
        var (c3, d3) = Run(1000 / 90.0, gpu: 99, core: 50);   // +29%
        var (c4, d4) = Run(1000 / 55.0, gpu: 99, core: 50);
        var before = FixMyGame.FromCapture(GameProblem.LowFps, c1, d1, 144);
        Assert.Equal(FixVerdict.Same, FixMyGame.Compare(before, FixMyGame.FromCapture(GameProblem.LowFps, c2, d2, 144)).Verdict);
        var better = FixMyGame.Compare(before, FixMyGame.FromCapture(GameProblem.LowFps, c3, d3, 144));
        Assert.Equal(FixVerdict.Improved, better.Verdict);
        Assert.True(better.StillAProblem);                                      // better, but still far from 144
        Assert.Equal(FixVerdict.Worse, FixMyGame.Compare(before, FixMyGame.FromCapture(GameProblem.LowFps, c4, d4, 144)).Verdict);
    }

    [Fact]
    public void Stutter_is_measured_from_frame_spikes_and_resolves_when_they_go()
    {
        var (c1, d1) = Run(1000 / 120.0, gpu: 80, core: 60, spikeEvery: 40);
        var (c2, d2) = Run(1000 / 120.0, gpu: 80, core: 60);
        var before = FixMyGame.FromCapture(GameProblem.Stutter, c1, d1, 144);
        Assert.True(FixMyGame.StillAProblem(before));
        var cmp = FixMyGame.Compare(before, FixMyGame.FromCapture(GameProblem.Stutter, c2, d2, 144));
        Assert.Equal(FixVerdict.Improved, cmp.Verdict);
        Assert.False(cmp.StillAProblem);
    }
}
