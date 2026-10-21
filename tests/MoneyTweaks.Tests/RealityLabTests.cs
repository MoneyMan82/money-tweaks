using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Lab;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

public class RealityLabTests
{
    [Fact]
    public void Every_lab_experiment_is_safe_and_switchable_without_a_restart()
    {
        Assert.Equal(RealityLab.Experiments.Count, RealityLab.Experiments.Select(e => e.Id).Distinct().Count());
        foreach (var e in RealityLab.Experiments)
        {
            var (usable, left) = RealityLab.Usable(e.TweakIds);
            Assert.True(left.Count == 0, $"{e.Name}: {string.Join("; ", left)}");
            Assert.Equal(e.TweakIds.Count, usable.Count);
            Assert.All(usable, t => { Assert.Equal(Risk.Safe, t.Risk); Assert.False(t.RequiresReboot); Assert.Equal(TweakKind.Toggle, t.Kind); });
        }
    }

    [Fact]
    public void Caution_and_restart_tweaks_from_presets_are_left_out()
    {
        var caution = TweakCatalog.All.First(t => t.Risk == Risk.Caution && t.Kind == TweakKind.Toggle);
        var reboot = TweakCatalog.All.First(t => t.Risk == Risk.Safe && t.RequiresReboot && t.Kind == TweakKind.Toggle);
        var (usable, left) = RealityLab.Usable([caution.Id, reboot.Id, "gen.menu-delay", "does.not-exist"]);
        Assert.Equal(["gen.menu-delay"], usable.Select(t => t.Id));
        Assert.Equal(3, left.Count);
    }

    [Fact]
    public void Every_metric_the_lab_uses_exists()
    {
        Assert.All(RealityLab.MetricKeys, k => Assert.Contains(BenchmarkStats.Metrics, m => m.Key == k));
        Assert.Subset(RealityLab.MetricKeys.ToHashSet(), RealityLab.SceneOnly);
    }

    private static Tweak Find(string id) => TweakCatalog.Find(id)!;

    [Fact]
    public void Estimates_combine_conservatively()
    {
        // Only "no change" → no change; one "may fall" → may fall.
        Assert.Equal(Expect.NoChange, RealityLab.Combine([Find("gen.menu-delay"), Find("gen.toasts-off")], "cpu").expect);
        Assert.Equal(Expect.MayFall, RealityLab.Combine([Find("gen.menu-delay"), Find("gen.transparency")], "gpu").expect);
        Assert.Equal(Expect.MayRise, RealityLab.Combine([Find("gen.min-processor-state"), Find("gen.core-parking")], "low1").expect);
        // Anything unknown stays unknown rather than being guessed.
        Assert.Equal(Expect.Unknown, RealityLab.Combine([Find("adv.priority-separation")], "fps").expect);
        // Tweaks the 3D scene can't feel are "no change" for FPS (from the synthetic benchmark's own rules).
        Assert.Equal(Expect.NoChange, RealityLab.Combine([Find("net.throttling-index")], "fps").expect);
        Assert.Equal(Expect.Unknown, RealityLab.Combine([Find("net.throttling-index")], "cpu").expect);
    }

    private static MetricComparison Cmp(string key, Verdict v, double diff)
    {
        var m = BenchmarkStats.Metrics.First(x => x.Key == key);
        return new(m, new(2, 10, 0), new(2, 10 + diff, 0), diff, diff * 10, v, "");
    }

    [Fact]
    public void Estimates_are_checked_against_measurements_honestly()
    {
        Assert.Equal(PredictionCheck.Matched, RealityLab.Check(Expect.NoChange, Cmp("cpu", Verdict.NoClearChange, 0.1)));
        Assert.Equal(PredictionCheck.Unexpected, RealityLab.Check(Expect.NoChange, Cmp("cpu", Verdict.Worse, 5)));
        // A "may" estimate is only a possibility: no measured change is "not seen", never a hit.
        Assert.Equal(PredictionCheck.NotSeen, RealityLab.Check(Expect.MayRise, Cmp("cputemp", Verdict.NoClearChange, 0.2)));
        Assert.Equal(PredictionCheck.Matched, RealityLab.Check(Expect.MayRise, Cmp("cputemp", Verdict.Worse, 4)));
        Assert.Equal(PredictionCheck.Opposite, RealityLab.Check(Expect.MayRise, Cmp("cputemp", Verdict.Better, -4)));
        // Direction is about the value, not "better": GPU load falling is a match for "may fall".
        Assert.Equal(PredictionCheck.Matched, RealityLab.Check(Expect.MayFall, Cmp("gpu", Verdict.Better, -3)));
        Assert.Equal(PredictionCheck.Matched, RealityLab.Check(Expect.MayRise, Cmp("low1", Verdict.Better, 6)));
        Assert.Equal(PredictionCheck.NoPrediction, RealityLab.Check(Expect.Unknown, Cmp("fps", Verdict.Better, 6)));
        Assert.Equal(PredictionCheck.NoData, RealityLab.Check(Expect.MayRise, Cmp("fps", Verdict.NotEnoughData, 6)));
    }

    [Fact]
    public void Estimate_wording_never_promises_a_result()
    {
        foreach (var m in BenchmarkStats.Metrics.Where(m => RealityLab.MetricKeys.Contains(m.Key)))
            foreach (var e in Enum.GetValues<Expect>())
            {
                var text = RealityLab.Describe(e, m);
                Assert.DoesNotContain("will", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("guarantee", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotMatch(@"\d", text); // no invented numbers
            }
    }
}

[Collection("registry")]
public class RealityLabStorageTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-lab-" + Guid.NewGuid().ToString("N"));
    private readonly string _orig = AppPaths.Root;

    public RealityLabStorageTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose() { AppPaths.OverrideRoot(_orig); try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Kept_experiments_round_trip()
    {
        Assert.Empty(RealityLab.Load().Kept);
        var f = new LabFile();
        f.Kept.Add(new LabRecord { ExperimentName = "Lighter desktop", AppliedIds = ["gen.menu-delay", "gen.transparency"], StudyId = "s1" });
        RealityLab.Save(f);
        var back = RealityLab.Load();
        Assert.Single(back.Kept);
        Assert.Equal(["gen.menu-delay", "gen.transparency"], back.Kept[0].AppliedIds);
    }

    [Fact]
    public void Lab_restore_file_is_separate_from_the_synthetic_benchmark()
    {
        var lab = new Dictionary<string, bool> { ["gen.menu-delay"] = false };
        TweakStateGuard.SavePending(lab, RealityLab.PendingPath);
        Assert.Null(TweakStateGuard.LoadPending());
        Assert.Equal(lab, TweakStateGuard.LoadPending(RealityLab.PendingPath));
        TweakStateGuard.ClearPending(RealityLab.PendingPath);
        Assert.Null(TweakStateGuard.LoadPending(RealityLab.PendingPath));
    }

    [Fact]
    public void Unreadable_lab_file_is_kept_aside_not_lost()
    {
        File.WriteAllText(RealityLab.FilePath, "{ not json");
        Assert.Empty(RealityLab.Load().Kept);
        Assert.True(File.Exists(RealityLab.FilePath + ".bad"));
    }
}
