using System.Text.Json;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Evidence;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Tests;

public class TweakEvidenceTests
{
    private const string Menu = "gen.menu-delay";
    private static int _seed;

    private static BenchmarkRun Run(string study, string phase, double fps, double cpu = 10, int dur = 20, bool? ac = true, double idle = 3) => new()
    {
        StudyId = study, Phase = phase,
        Conditions = new TestConditions { DurationSec = dur, OnAcPower = ac, IdleCpuBefore = idle, ProcessCount = 300 },
        CpuAvg = cpu, Frames = new FrameStats { AvgFps = fps, Low1Fps = fps * 0.8, FrameTimeP99Ms = 1000 / (fps * 0.8), Frames = 1000 },
    };

    /// <summary>A study with runs at the given FPS values (before / after); tiny jitter so run-to-run variation exists.</summary>
    private static (BenchmarkStudy, List<BenchmarkRun>) Study(IReadOnlyList<string> switched, double before, double after, int runs = 2, string kind = "synthetic", string scene = "", int dur = 20)
    {
        var s = new BenchmarkStudy { TargetKind = kind, SwitchedIds = [.. switched], Scene = scene };
        var list = new List<BenchmarkRun>();
        for (int i = 0; i < runs; i++)
        {
            double j = (++_seed % 3 - 1) * 0.15;
            list.Add(Run(s.Id, "Baseline", before + j, dur: dur));
            list.Add(Run(s.Id, "After", after - j, dur: dur));
        }
        return (s, list);
    }

    private static BenchmarkFile File(params (BenchmarkStudy s, List<BenchmarkRun> r)[] studies) =>
        new() { Studies = studies.Select(x => x.s).ToList(), Runs = studies.SelectMany(x => x.r).ToList() };

    private static TweakEvidenceReport Report(BenchmarkFile f, string id = Menu) =>
        TweakEvidence.For(id, f, TweakCatalog.Find, n => TweakCatalog.All.FirstOrDefault(t => t.Name == n));

    private static MetricEvidence Fps(TweakEvidenceReport r) => r.Groups.Single().Metrics.Single(m => m.Metric.Key == "fps");

    [Fact]
    public void No_conclusion_and_no_effect_size_before_three_valid_tests()
    {
        var r = Report(File(Study([Menu], 100, 110), Study([Menu], 100, 110)));
        var fps = Fps(r);
        Assert.Equal(2, fps.Trials);
        Assert.Equal(EvidenceLevel.Insufficient, fps.Level);
        Assert.Null(fps.Conclusion);
        Assert.Null(fps.Consensus);
    }

    [Fact]
    public void Consistent_tests_give_a_conclusion_with_repeatability_and_regressions()
    {
        var r = Report(File(Study([Menu], 100, 110), Study([Menu], 100, 109), Study([Menu], 100, 111), Study([Menu], 100, 110), Study([Menu], 100, 110)));
        var fps = Fps(r);
        Assert.Equal(5, fps.Trials);
        Assert.Equal(Verdict.Better, fps.Consensus);
        Assert.Equal(EvidenceLevel.High, fps.Level);
        Assert.Equal(1.0, fps.Repeatability);
        Assert.Equal(0.0, fps.RegressionRate);
        Assert.InRange(fps.MedianPct!.Value, 9, 11);
        Assert.StartsWith("Better", fps.Conclusion);
    }

    [Fact]
    public void Disagreeing_tests_are_reported_as_inconsistent_not_as_an_effect()
    {
        var r = Report(File(Study([Menu], 100, 112), Study([Menu], 100, 88), Study([Menu], 100, 100)));
        var fps = Fps(r);
        Assert.Equal(EvidenceLevel.Inconsistent, fps.Level);
        Assert.Contains("No consistent effect", fps.Conclusion);
        Assert.InRange(fps.RegressionRate, 0.3, 0.34);
    }

    [Fact]
    public void One_wild_test_does_not_swing_the_median()
    {
        var r = Report(File(Study([Menu], 100, 102.5), Study([Menu], 100, 103), Study([Menu], 100, 102.5), Study([Menu], 100, 300)));
        Assert.InRange(Fps(r).MedianPct!.Value, 2, 4);
    }

    [Fact]
    public void Invalid_and_incomparable_tests_are_filtered_with_reasons()
    {
        var single = Study([Menu], 100, 110, runs: 1);
        var (mixed, mixedRuns) = Study([Menu], 100, 110);
        mixedRuns[0].Conditions.DurationSec = 60;                    // different run lengths
        var (battery, batteryRuns) = Study([Menu], 100, 110);
        batteryRuns[1].Conditions.OnAcPower = false;                  // battery vs mains
        var r = Report(File(single, (mixed, mixedRuns), (battery, batteryRuns)));
        Assert.Equal(0, r.ValidTrials);
        Assert.Equal(3, r.Excluded.Count);
        Assert.Contains(r.Excluded, t => t.Issues.Any(i => i.Contains("2 runs")));
        Assert.Contains(r.Excluded, t => t.Issues.Any(i => i.Contains("different lengths")));
        Assert.Contains(r.Excluded, t => t.Issues.Any(i => i.Contains("battery")));
    }

    [Fact]
    public void Frame_rate_capped_by_the_display_is_not_used()
    {
        var r = Report(File(Study([Menu], 143, 144, scene: "Synthetic 3D · display 144 Hz")));
        var t = r.Groups.Single().Trials.Single();
        Assert.False(t.Metrics.ContainsKey("fps"));
        Assert.True(t.Metrics.ContainsKey("cpu")); // other metrics still count
    }

    [Fact]
    public void Tests_that_changed_several_tweaks_are_never_attributed()
    {
        var r = Report(File(Study([Menu, "gen.transparency"], 100, 120), Study([Menu, "gen.transparency"], 100, 120), Study([Menu, "gen.transparency"], 100, 120)));
        Assert.Equal(0, r.ValidTrials);
        Assert.Equal(3, r.CombinedTests.Count);
    }

    [Fact]
    public void Different_test_kinds_are_never_pooled()
    {
        var r = Report(File(Study([Menu], 100, 110), Study([Menu], 100, 110, kind: "lab")));
        Assert.Equal(2, r.Groups.Count);
        Assert.All(r.Groups, g => Assert.Single(g.Trials));
    }

    [Fact]
    public void Older_studies_are_attributed_from_their_saved_fields()
    {
        var synth = new BenchmarkStudy { TargetKind = "synthetic", TargetName = "Competitive Fortnite → Instant menus" };
        var own = new BenchmarkStudy { TargetKind = "tweak", TargetId = Menu, AppliedIds = [Menu] };
        var notApplied = new BenchmarkStudy { TargetKind = "tweak", TargetId = Menu };
        var presetWide = new BenchmarkStudy { TargetKind = "synthetic", TargetName = "Competitive Fortnite" };
        Tweak? ByName(string n) => TweakCatalog.All.FirstOrDefault(t => t.Name == n);
        Assert.Equal([Menu], TweakEvidence.SwitchedIds(synth, TweakCatalog.Find, ByName));
        Assert.Equal([Menu], TweakEvidence.SwitchedIds(own, TweakCatalog.Find, ByName));
        Assert.Empty(TweakEvidence.SwitchedIds(notApplied, TweakCatalog.Find, ByName));   // nothing was switched between phases
        Assert.Empty(TweakEvidence.SwitchedIds(presetWide, TweakCatalog.Find, ByName));
    }

    // ---- sharing ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hardware_names_are_reduced_to_safe_model_strings()
    {
        Assert.Equal("AMD Ryzen 5 7600X", EvidenceShare.CleanModel("AMD Ryzen 5 7600X 6-Core Processor", cpu: true));
        Assert.Equal("Intel Core i7-9700K", EvidenceShare.CleanModel("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", cpu: true));
        Assert.Equal("Unknown", EvidenceShare.CleanModel("", cpu: true));
        Assert.DoesNotContain("\\", EvidenceShare.CleanModel(@"C:\Users\bob <x>", cpu: false));
        Assert.Equal("NVIDIA GeForce RTX 4070 SUPER", EvidenceShare.PickGpu("Meta Virtual Monitor + AMD Radeon(TM) Graphics + NVIDIA GeForce RTX 4070 SUPER"));
        Assert.Equal("AMD Radeon Graphics", EvidenceShare.PickGpu("Microsoft Basic Display Adapter + AMD Radeon(TM) Graphics"));
        Assert.Equal(32, EvidenceShare.RamBucket(31.2));
        Assert.Equal(16, EvidenceShare.RamBucket(15.8));
    }

    [Fact]
    public void A_shared_result_contains_only_whitelisted_fields()
    {
        var r = Report(File(Study([Menu], 100, 110), Study([Menu], 100, 110), Study([Menu], 100, 110)));
        var trial = r.Groups.Single().Trials[0];
        var specs = new SystemSpecs("Windows 11 Pro", "AMD Ryzen 5 7600X 6-Core Processor", 6, 12, "NVIDIA GeForce RTX 4070 SUPER", 31.2, 4800, 6000, "ACME B650 board SN123", "BIOS 1.2");
        var sub = EvidenceShare.Build(trial, specs, 26100, "1.54")!;
        var json = JsonSerializer.Serialize(sub, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["app", "cpu", "game", "gpu", "key", "kind", "metrics", "osBuild", "ramGb", "tweak", "v"], doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(trial.Study.Id, json);
        Assert.DoesNotContain("ACME", json);    // motherboard / serials never sent
        Assert.DoesNotContain("BIOS", json);
        Assert.Matches("^[0-9a-f]{32}$", sub.Key);
        Assert.Equal(sub.Key, EvidenceShare.KeyFor(trial.Study));
        Assert.All(sub.Metrics, m => Assert.True(m.NBefore >= 2 && m.NAfter >= 2));
        Assert.Contains(sub.Metrics, m => m.K == "fps" && m.Verdict == "better");
    }

    [Fact]
    public void Invalid_tests_cannot_be_shared()
    {
        var r = Report(File(Study([Menu], 100, 110, runs: 1)));
        var specs = new SystemSpecs("", "", 1, 1, "", 8, null, null, "", "");
        Assert.Null(EvidenceShare.Build(r.Excluded.Single(), specs, 26100, "1.54"));
    }
}
