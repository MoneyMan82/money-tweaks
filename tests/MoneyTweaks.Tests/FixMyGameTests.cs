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
        Assert.Contains(plan.Causes, c => c.Key == "gpu" && c.Severity == FixSeverity.High && c.Alternatives.Count > 0);
        Assert.Equal(FixStatus.Confirmed, plan.Causes.Single(c => c.Key == "finding:power-saver").Status);   // a setting read from Windows is a fact
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
        Assert.Equal(FixStatus.NotEnoughEvidence, Assert.Single(plan.Causes).Status);
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

    private static Core.Health.CrashReport Crashes(params Core.Health.CrashIncident[] incidents) =>
        new(DateTime.Now, 7, incidents, [], [], 99, 99, ["System log", "Application log"], [], []);

    private static GameContext Game(string exe = "FortniteClient-Win64-Shipping.exe") =>
        new("fortnite", "Fortnite", null, @"C:\Games\Fortnite\" + exe, "++Fortnite+Release-32.10", @"C:\Games\Fortnite", "profile.fortnite-competitive", "Fortnite Competitive", false, "fortnite");

    [Fact]
    public void Game_crashes_are_confirmed_from_the_logs_and_only_for_that_game()
    {
        var mine = new Core.Health.CrashIncident(DateTime.Now.AddHours(-2), Core.Health.CrashKind.AppCrash, "FortniteClient-Win64-Shipping.exe crashed", "FortniteClient-Win64-Shipping.exe",
            [new(DateTime.Now.AddHours(-2), "Application log", "FortniteClient-Win64-Shipping.exe crashed in d3d12.dll")], []);
        var other = mine with { App = "notepad.exe", Title = "notepad.exe crashed" };
        var ctx = new FixContext { Game = Game(), Crashes = Crashes(mine, mine with { Time = DateTime.Now.AddHours(-1) }, other) };
        var plan = FixMyGame.Plan(FixMyGame.QuickOnly(GameProblem.Crashes, "Quick"), ctx);
        var c = plan.Causes.First(x => x.Key == "crash-game");
        Assert.Equal(FixStatus.Confirmed, c.Status);
        Assert.Contains("2 time(s)", c.Title);
        Assert.Equal(FixStatus.Possible, plan.Causes.Single(x => x.Key == "files").Status);  // damaged files is a hypothesis, labelled so
        Assert.Contains(plan.Actions, a => a.PageTag == "monitor#crash");
        Assert.DoesNotContain(plan.Actions, a => a.Kind == FixKind.Tweak);
        var none = FixMyGame.Plan(FixMyGame.QuickOnly(GameProblem.Crashes, "Quick"), new FixContext { Game = Game(), Crashes = Crashes(other) });
        Assert.Equal("no-crashes", Assert.Single(none.Causes).Key);
    }

    [Fact]
    public void A_monitor_below_its_supported_refresh_rate_is_a_confirmed_finding()
    {
        var mode = new Core.Display.DisplayMode(2560, 1440, 60, 32);
        var display = new Core.Display.DisplayInfo(@"\\.\DISPLAY1", "GPU", "Monitor", true, 0, 0, mode, default, mode,
            [mode, mode with { Hz = 144 }, mode with { Hz = 120 }, new(1920, 1080, 240, 32)]);
        var plan = FixMyGame.Plan(FixMyGame.QuickOnly(GameProblem.LowFps, "Quick"), new FixContext { Displays = [display] });
        var c = plan.Causes.Single(x => x.Key == "refresh");
        Assert.Contains("144 Hz", c.Title);                                       // 240 Hz exists only at another resolution
        Assert.Equal(FixStatus.Confirmed, c.Status);
        Assert.Equal("step:refresh", plan.Actions[0].Id);
        var fine = FixMyGame.Plan(FixMyGame.QuickOnly(GameProblem.LowFps, "Quick"), new FixContext { Displays = [display with { Current = mode with { Hz = 144 } }] });
        Assert.DoesNotContain(fine.Causes, x => x.Key == "refresh");
    }

    [Fact]
    public void History_ranks_fixes_that_did_not_help_lower_and_marks_outdated_ones()
    {
        var (c, d) = Run(1000 / 60.0, gpu: 70, core: 98);
        var m = FixMyGame.FromCapture(GameProblem.LowFps, c, d, 144);
        var findings = new List<Finding> { Finding("power-balanced", "gen.ultimate-plan") };
        var fresh = FixMyGame.Plan(m, new FixContext { Game = Game(), Findings = findings });
        var history = new FixHistory();
        history.Sessions.Add(new FixSession { GameKey = "fortnite", Problem = GameProblem.LowFps, Applied = ["tweak:gen.ultimate-plan"], Verdict = FixVerdict.Same, At = DateTime.Now.AddDays(-3) });
        var tried = FixMyGame.Plan(m, new FixContext { Game = Game(), Findings = findings, History = history });
        var before = fresh.Actions.Concat(fresh.More).Single(a => a.Id == "tweak:gen.ultimate-plan");
        var after = tried.Actions.Concat(tried.More).Single(a => a.Id == "tweak:gen.ultimate-plan");
        Assert.True(after.Score < before.Score);
        Assert.Contains("no measurable improvement", after.History);
        history.Sessions[0].GpuDriver = "31.0.15.1";
        var outdated = FixMyGame.Plan(m, new FixContext { Game = Game(), Findings = findings, History = history, GpuDriver = "32.0.15.6" });
        Assert.Contains("outdated", outdated.Actions.Concat(outdated.More).Single(a => a.Id == "tweak:gen.ultimate-plan").History);
    }

    [Fact]
    public void Gaming_profile_and_potato_graphics_are_orchestrated_not_duplicated()
    {
        var (c, d) = Run(1000 / 70.0, gpu: 99, core: 50);
        var plan = FixMyGame.Plan(FixMyGame.FromCapture(GameProblem.LowFps, c, d, 144), new FixContext { Game = Game() });
        var all = plan.Actions.Concat(plan.More).ToList();
        Assert.Contains(all, a => a.Kind == FixKind.Tool && a.PageTag == "potato");
        Assert.Contains(all, a => a.Kind == FixKind.Profile && a.ProfileId == "profile.fortnite-competitive");
        Assert.All(all, a => Assert.False(string.IsNullOrEmpty(a.Reversible)));
    }

    [Fact]
    public void Repeated_runs_widen_the_noise_margin_and_different_programs_are_inconclusive()
    {
        var (c1, d1) = Run(1000 / 70.0, gpu: 99, core: 50);
        var (c2, d2) = Run(1000 / 60.0, gpu: 99, core: 50);
        var (c3, d3) = Run(1000 / 71.0, gpu: 99, core: 50);
        var before = FixMyGame.Combine([FixMyGame.FromCapture(GameProblem.LowFps, c1, d1, 144), FixMyGame.FromCapture(GameProblem.LowFps, c2, d2, 144)]);
        Assert.Equal(2, before.Runs);
        Assert.True(before.Spread["Average FPS"] > 9);
        var after = FixMyGame.FromCapture(GameProblem.LowFps, c3, d3, 144);   // +6 FPS (9 %) would count on its own, not with a 10 FPS spread
        Assert.Equal(FixVerdict.Same, FixMyGame.Compare(before, after).Verdict);
        Assert.Equal(FixVerdict.Improved, FixMyGame.Compare(FixMyGame.FromCapture(GameProblem.LowFps, c2, d2, 144), after).Verdict); // one run of 60: +11 FPS counts without a spread
        c3.What = "Another game";
        var cmp = FixMyGame.Compare(FixMyGame.FromCapture(GameProblem.LowFps, c1, d1, 144), FixMyGame.FromCapture(GameProblem.LowFps, c3, d3, 144));
        Assert.Equal(FixVerdict.Inconclusive, cmp.Verdict);
        Assert.Contains(cmp.Notes, n => n.Contains("Different programs"));
    }

    [Fact]
    public void History_is_kept_locally_and_recurring_problems_are_counted()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mt-fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Core.Sys.AppPaths.OverrideRoot(tmp);
            var h = FixHistory.Load();
            for (int i = 0; i < 3; i++) h.Save(new FixSession { GameKey = "fortnite", GameName = "Fortnite", Problem = GameProblem.Stutter, At = DateTime.Now.AddDays(-i) });
            var again = FixHistory.Load();
            Assert.Equal(3, again.Recurring("fortnite", GameProblem.Stutter));
            Assert.StartsWith(tmp, FixHistory.FilePath);
            var plan = FixMyGame.Plan(FixMyGame.QuickOnly(GameProblem.Stutter, "Quick"), new FixContext { Game = Game(), History = again });
            Assert.Contains(plan.Causes, c => c.Key == "recurring");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Every_problem_plans_without_frame_timing()
    {
        // Standard user: CPU/GPU telemetry but no frame times. Every rule must cope with Frames == null.
        var (c, _) = Run(1000 / 60.0, gpu: 70, core: 40);
        c.FrameMs.Clear();
        c.FramesUnavailable = "needs administrator rights";
        var d = BottleneckEngine.Diagnose(c, new HardwareFacts { Threads = 16, RamGb = 16 }, new AdvisorGoal { TargetFps = 144 });
        Assert.Null(d.Frames);
        foreach (var p in Enum.GetValues<GameProblem>())
        {
            var plan = FixMyGame.Plan(FixMyGame.FromCapture(p, c, d, 144), new FixContext { Game = Game() });
            Assert.NotEmpty(plan.Causes);
            Assert.InRange(plan.Actions.Count, 0, 3);
        }
    }

    [Fact]
    public void Report_lists_findings_changes_and_verdict()
    {
        var (c1, d1) = Run(1000 / 70.0, gpu: 99, core: 50);
        var (c2, d2) = Run(1000 / 90.0, gpu: 99, core: 50);
        var before = FixMyGame.FromCapture(GameProblem.LowFps, c1, d1, 144);
        var plan = FixMyGame.Plan(before, []);
        var text = FixMyGame.Report(Game(), plan, before, FixMyGame.Compare(before, FixMyGame.FromCapture(GameProblem.LowFps, c2, d2, 144)), ["Lower the heaviest graphics settings"], []);
        Assert.Contains("RE-TEST: Improved", text);
        Assert.Contains("Fortnite ++Fortnite+Release-32.10", text);
        Assert.Contains("CHANGED BY YOU HERE", text);
    }
}
