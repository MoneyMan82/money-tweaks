using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Tests;

public class SyntheticPlanTests
{
    [Fact]
    public void Abba_order_is_counterbalanced()
    {
        Assert.Equal([false, true], SyntheticPlan.Abba(1));
        Assert.Equal([false, true, true, false], SyntheticPlan.Abba(2));
        Assert.Equal([false, true, true, false, false, true], SyntheticPlan.Abba(3));
        // With an even number of pairs the mean run position of each state is equal, so linear drift cancels.
        var p = SyntheticPlan.Abba(2);
        Assert.Equal(Enumerable.Range(0, 4).Where(i => !p[i]).Average(), Enumerable.Range(0, 4).Where(i => p[i]).Average());
    }

    [Fact]
    public void Competitive_fortnite_preset_cannot_be_judged_by_the_synthetic_scene()
    {
        var preset = PresetStore.BuiltIns.First(p => p.Id == "builtin.competitive-fortnite");
        var switchable = preset.TweakIds.Select(TweakCatalog.Find).Where(t => t is not null && TweakStateGuard.Eligible(t, out _)).ToList();
        Assert.NotEmpty(switchable);
        Assert.All(switchable, t => Assert.NotNull(SyntheticPlan.NotMeasurable(t!)));
        Assert.Null(SyntheticPlan.NotMeasurable(TweakCatalog.Find("gen.ultimate-plan")!));
        Assert.Null(SyntheticPlan.NotMeasurable(TweakCatalog.Find("gen.transparency")!));
    }

    private static BenchmarkRun Run(string phase, double fps) => new() { Phase = phase, Frames = new FrameStats { AvgFps = fps } };

    [Fact]
    public void Users_real_run_is_within_noise()
    {
        // The saved study from 2026-10-15 (order off, on, off, on): the gap came from the cold first run.
        var fps = BenchmarkStats.Metrics.First(m => m.Key == "fps");
        var c = BenchmarkStats.Compare(fps, [Run("Baseline", 101.4), Run("Baseline", 98.2)], [Run("After", 98.7), Run("After", 98.4)]);
        Assert.Equal(Verdict.NoClearChange, c.Verdict);
        Assert.InRange(c.Diff!.Value, -1.3, -1.2);
    }

    [Fact]
    public void Large_system_cache_is_disabled_but_undoable()
    {
        var t = TweakCatalog.Find("adv.large-system-cache")!;
        Assert.NotNull(TweakAudit.DisabledReason(t));
        Assert.Contains(TweakAudit.IssuesFor(t), i => i.Kind == AuditKind.Risky);
        Assert.False(TweakStateGuard.Eligible(t, out _));
        Assert.DoesNotContain(PresetStore.BuiltIns, p => p.TweakIds.Contains(t.Id));
    }
}

public class RelativeNoiseFloorTests
{
    private static BenchmarkRun R(string phase, double fps, double ft) => new() { Phase = phase, Frames = new FrameStats { AvgFps = fps, FrameTimeAvgMs = ft } };

    [Fact]
    public void Tiny_frame_time_change_is_not_meaningful_when_fps_is_not()
    {
        // User's 2026-10-16 run (intensity 5): 34.9 ± 0.12 ms vs 34.7 ± 0.05 ms was wrongly reported as "better".
        var off = new[] { R("Baseline", 28.6, 34.95), R("Baseline", 28.7, 34.85), R("Baseline", 28.8, 34.75) };
        var on = new[] { R("After", 28.85, 34.68), R("After", 28.9, 34.72), R("After", 28.95, 34.7) };
        var ft = BenchmarkStats.Compare(BenchmarkStats.Metrics.First(m => m.Key == "ft"), off, on);
        var fps = BenchmarkStats.Compare(BenchmarkStats.Metrics.First(m => m.Key == "fps"), off, on);
        Assert.Equal(Verdict.NoClearChange, ft.Verdict);
        Assert.Equal(Verdict.NoClearChange, fps.Verdict);
    }
}
