using MoneyTweaks.Core.Genome;

namespace MoneyTweaks.Tests;

/// <summary>
/// Performance Genome end to end against a simulated PC whose true behaviour is known: main effects, one interaction,
/// run-to-run noise, slow thermal drift, an unstable setting and a driver update. The engine must find the real effects,
/// not invent any, learn from the failure, restore the original settings and mark old results stale after the update.
/// </summary>
public class GenomeTests
{
    private static GenomeFactor F(string id, string name, FactorKind kind, params (string id, string label, double v)[] levels) =>
        new() { Id = id, Name = name, Kind = kind, Levels = levels.Select(l => new FactorLevel { Id = l.id, Label = l.label, Value = l.v }).ToList() };

    private static List<GenomeFactor> Factors() =>
    [
        F("cpu.boost", "CPU boost mode", FactorKind.CpuBoostMode, ("b2", "Aggressive", 2), ("b4", "Efficient Aggressive", 4), ("b3", "Efficient Enabled", 3)),
        F("gpu.power", "GPU power limit", FactorKind.GpuPowerLimit, ("p100", "100%", 220), ("p85", "85%", 187), ("p70", "70%", 154)),
        F("gpu.cap", "GPU clock cap", FactorKind.GpuClockCap, ("none", "No cap", 0), ("c80", "80%", 2490)),
    ];

    /// <summary>The simulated PC. Truth (vs. b2/p100/none): FPS ln-ratio and GPU °C.</summary>
    private sealed class SimPc : IGenomeBackend, IGenomeWorkload
    {
        public readonly Dictionary<string, string> Now = new() { ["cpu.boost"] = "b2", ["gpu.power"] = "p100", ["gpu.cap"] = "none" };
        public string Driver = "576.02";
        public string? UnstableLevel; // a level that makes the load fail
        public bool PendingSaved, PendingCleared;
        public int Runs;
        private readonly Random _noise;
        public SimPc(int seed = 7) => _noise = new Random(seed * 7919);

        public static double TrueFps(IReadOnlyDictionary<string, string> c)
        {
            double y = 0;
            if (c["gpu.power"] == "p85") y -= 0.015;
            if (c["gpu.power"] == "p70") y -= 0.06;
            if (c["cpu.boost"] == "b3") y -= 0.02;
            if (c["gpu.cap"] == "c80") y -= 0.04;
            if (c["gpu.power"] == "p70" && c["gpu.cap"] == "c80") y += 0.03; // power-limited already: the cap costs less
            return y;
        }

        public static double TrueTemp(IReadOnlyDictionary<string, string> c)
        {
            double t = 0;
            if (c["gpu.power"] == "p85") t -= 3;
            if (c["gpu.power"] == "p70") t -= 8;
            if (c["gpu.cap"] == "c80") t -= 5;
            if (c["gpu.power"] == "p70" && c["gpu.cap"] == "c80") t += 3;
            return t;
        }

        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - _noise.NextDouble())) * Math.Cos(2 * Math.PI * _noise.NextDouble());

        public Dictionary<string, string> ReadCurrent(IReadOnlyList<GenomeFactor> factors) => factors.ToDictionary(f => f.Id, f => Now[f.Id]);
        public (bool ok, string message) Apply(GenomeFactor f, FactorLevel level) { Now[f.Id] = level.Id; return (true, "ok"); }
        public Fingerprint Fingerprint(GenomeWorkload w) => new() { Cpu = "Ryzen 5 7600X", Gpu = "RTX 4070 SUPER", GpuDriver = Driver, OsBuild = "26300.1" };
        public (double idleCpu, int processes, bool? onAc) Conditions() => (3, 200, true);
        public Task SettleAsync(Action<string> status, CancellationToken ct) => Task.CompletedTask;
        public void SavePending(Dictionary<string, string> original) => PendingSaved = true;
        public void ClearPending() => PendingCleared = true;

        public Task<WorkloadResult> RunAsync(GenomeWorkload w, bool warmup, Action<string> status, CancellationToken ct)
        {
            if (warmup) return Task.FromResult(new WorkloadResult(null, true, null));
            Runs++;
            if (UnstableLevel is { } bad && Now.ContainsValue(bad)) return Task.FromResult(new WorkloadResult(null, false, "GPU driver reset"));
            double drift = -0.0008 * Runs; // slow thermal drift across the session
            double fps = 140 * Math.Exp(TrueFps(Now) + drift + 0.008 * Gauss());
            double temp = 70 + TrueTemp(Now) + 0.03 * Runs + 0.6 * Gauss();
            var m = new Measurement { Values = { ["fps"] = fps, ["low1"] = fps * 0.7 * Math.Exp(0.01 * Gauss()), ["gputemp"] = temp, ["gpupower"] = 200 * Math.Exp(TrueFps(Now) * 2 + 0.01 * Gauss()) }, Frames = 3000, RefreshHz = 240 };
            return Task.FromResult(new WorkloadResult(m, true, null));
        }
    }

    private static (GenomeStore s, GenomeWorkload w) NewStore(string goal = "cool")
    {
        var s = new GenomeStore { Factors = Factors(), Goal = GenomeGoals.FromPreset(goal) };
        var w = new GenomeWorkload { Id = "synthetic", Name = "3D scene", Kind = "synthetic" };
        s.Workloads.Add(w);
        return (s, w);
    }

    private static Task<GenomeSession> Run(GenomeStore s, GenomeWorkload w, SimPc pc, int trials, int seed = 1) =>
        GenomeRunner.RunAsync(s, w, trials, pc, pc, (_, _) => Task.FromResult(true), null, CancellationToken.None, new Random(seed), () => { });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Learns_the_real_effects_and_an_interaction_and_restores_your_settings(int seed)
    {
        var (s, w) = NewStore("cool");
        s.Goal.MaxFpsLossPct = 10; // the 70% power limit costs ~6% FPS; with the preset's 5% limit it would (rightly) never be combined
        var pc = new SimPc(seed);
        var session = await Run(s, w, pc, trials: 24, seed);

        Assert.Contains("restored and read back", session.Outcome);
        Assert.Equal(new Dictionary<string, string> { ["cpu.boost"] = "b2", ["gpu.power"] = "p100", ["gpu.cap"] = "none" }, pc.Now);
        Assert.True(pc.PendingSaved && pc.PendingCleared);
        Assert.Equal(GenomeRunner.TotalRuns(24), s.Trials.Count);
        Assert.True(s.Trials.Count(t => t.IsBaseline) >= 8); // start, every 3 trials, end

        var a = GenomeAnalysis.Build(s, w, pc.Fingerprint(w), session.Original);
        // Main effects recovered within their stated uncertainty, and close to the truth.
        foreach (var (factor, level, truth) in new[] { ("gpu.power", "p70", -0.06), ("gpu.power", "p85", -0.015), ("cpu.boost", "b3", -0.02), ("gpu.cap", "c80", -0.04) })
        {
            var single = new Dictionary<string, string>(session.Original) { [factor] = level };
            var (mean, sd) = a.Predict("fps", single)!.Value;
            Assert.True(Math.Abs(mean - truth) < Math.Max(0.012, 3 * sd), $"{factor}={level}: {mean:0.000} ± {sd:0.000}, truth {truth}");
        }
        // The power-limit × clock-cap interaction (the cap costs less when power-limited) is found for temperature or FPS.
        var both = new Dictionary<string, string>(session.Original) { ["gpu.power"] = "p70", ["gpu.cap"] = "c80" };
        Assert.True(a.EvidenceFor(both)?.N >= 2, "the interaction combination was tested: " + string.Join(" | ", s.Trials.Select(t => t.Reason)));
        Assert.Contains(a.Interactions(), i => i.feature.FactorA == "gpu.cap" && i.feature.FactorB == "gpu.power" || i.feature.FactorA == "gpu.power" && i.feature.FactorB == "gpu.cap");
        var (mBoth, sdBoth) = a.Predict("fps", both)!.Value;
        Assert.True(Math.Abs(mBoth - SimPc.TrueFps(both)) < Math.Max(0.015, 3 * sdBoth),
            $"combination predicted {mBoth:0.0000} ± {sdBoth:0.0000}, truth {SimPc.TrueFps(both)}, measured {a.EvidenceFor(both)?.Measured.GetValueOrDefault("fps")}; interactions: {string.Join(" / ", a.Interactions().Select(i => i.text))}");
    }

    [Fact]
    public async Task Recommends_only_measured_configurations_and_shows_honest_uncertainty()
    {
        var (s, w) = NewStore("cool");
        var pc = new SimPc();
        var session = await Run(s, w, pc, trials: 24);
        var a = GenomeAnalysis.Build(s, w, pc.Fingerprint(w), session.Original);
        var recs = a.Recommend(s.Goal);
        Assert.NotEmpty(recs);
        Assert.All(recs, r => Assert.True(r.Evidence is { N: >= 2 } && r.Evidence.Failed.Count == 0 && r.Score.Measured));
        var cool = recs.First(r => r.Goals.Contains("Your goal"));
        Assert.NotEqual("b2/p100/none", $"{cool.Config["cpu.boost"]}/{cool.Config["gpu.power"]}/{cool.Config["gpu.cap"]}"); // cooler than stock is measurable here
        Assert.True(cool.Score.Ci95 > 0);
        // Max-FPS: nothing in this PC beats stock, so the recommendation must not claim a gain.
        var maxfps = recs.First(r => r.Goals.Contains("Maximum FPS"));
        Assert.True(maxfps.Score.Low <= 0.5, maxfps.Verdict);
        // Frontier: measured points with intervals; Pareto from measured only.
        var front = a.Frontier("gputemp", [], new Dictionary<string, string>());
        Assert.Contains(front, p => p.Pareto && p.Measured);
        Assert.All(front.Where(p => p.Pareto), p => Assert.True(p.Measured));
        Assert.Contains(a.Facts(), f => f.Factor.Id == "gpu.power" && f.Metric.Key == "gputemp" && f.Clear && f.Mean < 0);
    }

    [Fact]
    public async Task A_failing_setting_is_learned_blocked_and_never_retried()
    {
        var (s, w) = NewStore("maxfps");
        var pc = new SimPc { UnstableLevel = "c80" };
        var session = await Run(s, w, pc, trials: 15);
        Assert.Contains(s.Failures, f => f.Levels.GetValueOrDefault("gpu.cap") == "c80");
        int failed = s.Trials.Count(t => t.Status == TrialStatus.Failed);
        Assert.Equal(1, failed);                                            // tried once, never again
        Assert.DoesNotContain(s.Trials.Skip(s.Trials.FindIndex(t => t.Status == TrialStatus.Failed) + 1), t => t.Config["gpu.cap"] == "c80");
        Assert.Equal("none", pc.Now["gpu.cap"]);                            // restored
        Assert.Contains(s.Journal, j => j.Contains("Learned"));
        var a = GenomeAnalysis.Build(s, w, pc.Fingerprint(w), session.Original);
        Assert.DoesNotContain(a.Recommend(s.Goal), r => r.Config["gpu.cap"] == "c80");
    }

    [Fact]
    public async Task Driver_update_makes_old_results_stale_and_screening_starts_again()
    {
        var (s, w) = NewStore("cool");
        var pc = new SimPc();
        var first = await Run(s, w, pc, trials: 9);
        pc.Driver = "580.10";
        var fp = pc.Fingerprint(w);
        var a = GenomeAnalysis.Build(s, w, fp, first.Original);
        Assert.Equal(s.Trials.Count, a.StaleTrials);
        Assert.Empty(a.Recommend(s.Goal));                                  // nothing is "measured" for the new driver yet
        Assert.Contains("GPU driver 576.02 → 580.10", fp.ChangesSince(s.Trials[0].Fingerprint));
        var plan = GenomePlanner.Next(a, s.Goal, new Random(1));
        Assert.StartsWith("screening", plan.Reason);                         // re-evaluates from scratch
    }

    [Fact]
    public void Statistics_are_honest_with_little_data()
    {
        var one = GenomeStats.Summarize([0.05], 0.02)!;
        Assert.True(one.Ci95 >= 0.035);                                      // one run: interval from the measured noise
        var lucky = GenomeStats.Summarize([0.03, 0.03], 0.02)!;
        Assert.True(lucky.Ci95 > 0.02);                                      // two identical runs don't make it certain
        var many = GenomeStats.Summarize(Enumerable.Repeat(0.03, 12).ToList(), 0.02)!;
        Assert.True(many.Ci95 < lucky.Ci95);
        var inv = GenomeStats.Invert(new double[,] { { 4, 1 }, { 1, 3 } });
        Assert.Equal(3.0 / 11, inv[0, 0], 6);
        Assert.Equal(-1.0 / 11, inv[0, 1], 6);
    }

    [Fact]
    public async Task Stop_restores_and_a_crash_can_be_recovered()
    {
        var (s, w) = NewStore();
        var pc = new SimPc();
        using var cts = new CancellationTokenSource();
        int runs = 0;
        var progress = new Progress<GenomeProgress>(_ => { });
        var task = GenomeRunner.RunAsync(s, w, 12, pc, new StopAfter(pc, 4, cts), (_, _) => Task.FromResult(true), progress, cts.Token, new Random(2), () => runs++);
        var session = await task;
        Assert.Contains("stopped by you", session.Outcome);
        Assert.Equal("p100", pc.Now["gpu.power"]);
        Assert.Equal("none", pc.Now["gpu.cap"]);

        // Simulated crash: settings left changed, pending original saved.
        pc.Now["gpu.power"] = "p70"; pc.PendingCleared = false;
        var problems = GenomeRunner.Recover(s, pc, new Dictionary<string, string> { ["cpu.boost"] = "b2", ["gpu.power"] = "p100", ["gpu.cap"] = "none" });
        Assert.Empty(problems);
        Assert.Equal("p100", pc.Now["gpu.power"]);
        Assert.True(pc.PendingCleared);
    }

    private sealed class StopAfter(SimPc pc, int runs, CancellationTokenSource cts) : IGenomeWorkload
    {
        public Task<WorkloadResult> RunAsync(GenomeWorkload w, bool warmup, Action<string> status, CancellationToken ct)
        {
            if (!warmup && pc.Runs >= runs) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return pc.RunAsync(w, warmup, status, ct);
        }
    }

    [Fact]
    public void Store_round_trips_and_keys_are_order_independent()
    {
        var (s, w) = NewStore();
        s.Trials.Add(new Trial { WorkloadId = w.Id, Config = new() { ["gpu.power"] = "p85", ["cpu.boost"] = "b2" }, Measured = new Measurement { Values = { ["fps"] = 120 } } });
        var path = Path.Combine(Path.GetTempPath(), $"genome-test-{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var back = GenomeStore.Load(path);
            Assert.Equal(3, back.Factors.Count);
            Assert.Equal(120, back.Trials[0].Measured!.Get("fps"));
            Assert.Equal(GenomeStore.Key(new Dictionary<string, string> { ["b"] = "1", ["a"] = "2" }), GenomeStore.Key(new Dictionary<string, string> { ["a"] = "2", ["b"] = "1" }));
        }
        finally { File.Delete(path); }
    }
}
