using System.Globalization;

namespace MoneyTweaks.Core.Genome;

public sealed record GoalPreset(string Id, string Name, string Blurb, IReadOnlyDictionary<string, double> Weights, double? MaxFpsLossPct, double? MaxGpuTempC);

public static class GenomeGoals
{
    public static readonly IReadOnlyList<GoalPreset> Presets =
    [
        new("competitive", "Competitive consistency", "Steady frame pacing: high 1% lows, low 99th-percentile frame time, little variation.",
            new Dictionary<string, double> { ["low1"] = 0.35, ["p99"] = 0.35, ["cv"] = 0.2, ["fps"] = 0.1 }, 2, null),
        new("maxfps", "Maximum FPS", "Highest average FPS, with 1% lows kept in view.",
            new Dictionary<string, double> { ["fps"] = 0.75, ["low1"] = 0.25 }, null, 88),
        new("latency", "Minimum latency", "Shortest frame times (a render-latency proxy; Money Tweaks can't measure end-to-end input latency without a hardware tester).",
            new Dictionary<string, double> { ["p99"] = 0.55, ["fps"] = 0.35, ["cv"] = 0.1 }, null, null),
        new("cool", "Lower temperatures", "Cooler CPU and GPU with only a small performance cost.",
            new Dictionary<string, double> { ["gputemp"] = 0.4, ["cputemp"] = 0.3, ["fps"] = 0.3 }, 5, null),
        new("quiet", "Quiet efficiency", "Less power (so less heat and fan noise) for the performance you keep. Fan noise itself isn't measured.",
            new Dictionary<string, double> { ["gpupower"] = 0.4, ["cpupower"] = 0.3, ["gputemp"] = 0.15, ["fps"] = 0.15 }, 8, null),
    ];

    public static GoalPreset? Find(string id) => Presets.FirstOrDefault(p => p.Id == id);

    /// <summary>A goal with the preset's weights and limits (custom goals keep their own).</summary>
    public static GoalSettings FromPreset(string id)
    {
        var p = Find(id) ?? Presets[0];
        return new GoalSettings { Preset = p.Id, Weights = p.Weights.ToDictionary(kv => kv.Key, kv => kv.Value), MaxFpsLossPct = p.MaxFpsLossPct, MaxGpuTempC = p.MaxGpuTempC };
    }

    public static IReadOnlyDictionary<string, double> WeightsOf(GoalSettings g) =>
        g.Weights.Count > 0 ? g.Weights : (Find(g.Preset) ?? Presets[0]).Weights;
}

/// <summary>Everything measured for one exact configuration on this workload.</summary>
public sealed class ConfigEvidence
{
    public required string Key { get; init; }
    public required Dictionary<string, string> Config { get; init; }
    public List<Trial> Ok { get; } = [];
    public List<Trial> Failed { get; } = [];
    public int Stale { get; set; }
    public Dictionary<string, MetricSummary> Measured { get; } = [];
    public bool IsReference { get; set; }
    public int N => Ok.Count;
    public double Stability => Ok.Count + Failed.Count == 0 ? 0 : (double)Ok.Count / (Ok.Count + Failed.Count);
}

/// <summary>A goal score: points vs. your current settings (positive = better), with a 95% interval.</summary>
public sealed record GoalScore(double Mean, double Ci95, bool Measured, int N, IReadOnlyList<string> MissingMetrics)
{
    public double Low => Mean - Ci95;
}

public sealed record Recommendation(string Name, IReadOnlyList<string> Goals, Dictionary<string, string> Config, ConfigEvidence? Evidence, GoalScore Score,
    string Confidence, string Verdict, IReadOnlyList<string> Explanation, IReadOnlyList<string> Violations);

public sealed record FrontierPoint(string Key, Dictionary<string, string> Config, double X, double XCi, double Y, double YCi, bool Measured, bool Pareto, int N, string Label);

/// <summary>A learned fact about this PC: what one setting level does on this workload, with its uncertainty.</summary>
public sealed record HardwareFact(GenomeFactor Factor, FactorLevel Level, GenomeMetric Metric, double Mean, double Sd, int Support, bool Clear);

/// <summary>
/// The model of this PC for one workload: per-metric fits, measured evidence per configuration, scores for a goal,
/// recommendations (measured only), predictions (clearly separate), the frontier and explanations.
/// </summary>
public sealed class GenomeAnalysis
{
    public required GenomeStore Store { get; init; }
    public required GenomeWorkload Workload { get; init; }
    public required Fingerprint? Current { get; init; }
    /// <summary>Your settings now: the baseline everything is compared with.</summary>
    public required Dictionary<string, string> Reference { get; init; }
    public required List<GenomeFactor> Factors { get; init; }
    public Dictionary<string, MetricFit?> Fits { get; } = [];
    public Dictionary<string, double> BaselineAbs { get; } = [];
    public List<ConfigEvidence> Evidence { get; } = [];
    public int StaleTrials { get; private set; }

    public static GenomeAnalysis Build(GenomeStore s, GenomeWorkload w, Fingerprint? current, Dictionary<string, string> reference)
    {
        var factors = s.Factors.Where(f => f.Enabled && (!f.IsManual || w.IsGame)).ToList();
        var a = new GenomeAnalysis { Store = s, Workload = w, Current = current, Reference = Complete(reference, s.Factors), Factors = factors };
        var ids = factors.Select(f => f.Id).ToList();
        var mine = s.Trials.Where(t => t.WorkloadId == w.Id).ToList();
        a.StaleTrials = current is null ? 0 : mine.Count(t => !t.Fingerprint.SameAs(current));
        var fresh = mine.Where(t => current is null || t.Fingerprint.SameAs(current)).ToList();
        foreach (var m in GenomeMetrics.All)
        {
            var obs = GenomeStats.Observe(s, w.Id, m, current);
            a.Fits[m.Key] = GenomeStats.Fit(m, obs, factors);
            var b = fresh.Where(t => t.IsBaseline && t.Status == TrialStatus.Ok && t.Measured?.Get(m.Key) is not null && !(m.FrameBased && t.Measured.FpsCapped)).Select(t => t.Measured!.Get(m.Key)!.Value).ToList();
            if (b.Count > 0) a.BaselineAbs[m.Key] = b.Average();
            // Measured evidence per exact configuration (over the enabled factors).
            foreach (var g in obs.GroupBy(o => GenomeStore.Key(o.Config, ids)))
            {
                var ev = a.Evidence.FirstOrDefault(e => e.Key == g.Key);
                if (ev is null) { ev = new ConfigEvidence { Key = g.Key, Config = ids.ToDictionary(id => id, id => g.First().Config.GetValueOrDefault(id, "?")) }; a.Evidence.Add(ev); }
                if (GenomeStats.Summarize(g.Select(o => o.Y).ToList(), a.Fits[m.Key]?.Sigma ?? GenomeStats.Priors(m).noise) is { } sum) ev.Measured[m.Key] = sum;
            }
        }
        foreach (var t in mine)
        {
            var key = GenomeStore.Key(t.Config, ids);
            var ev = a.Evidence.FirstOrDefault(e => e.Key == key);
            if (ev is null) { ev = new ConfigEvidence { Key = key, Config = ids.ToDictionary(id => id, id => t.Config.GetValueOrDefault(id, "?")) }; a.Evidence.Add(ev); }
            if (current is not null && !t.Fingerprint.SameAs(current)) { ev.Stale++; continue; }
            if (t.Status == TrialStatus.Ok) ev.Ok.Add(t); else if (t.Status == TrialStatus.Failed) ev.Failed.Add(t);
        }
        var refKey = GenomeStore.Key(a.Reference, ids);
        foreach (var e in a.Evidence) e.IsReference = e.Key == refKey;
        return a;
    }

    /// <summary>Fills factors missing from a config with their first level (a factor added later).</summary>
    public static Dictionary<string, string> Complete(IReadOnlyDictionary<string, string> c, IEnumerable<GenomeFactor> factors) =>
        factors.ToDictionary(f => f.Id, f => c.TryGetValue(f.Id, out var v) && f.Level(v) is not null ? v : f.Levels.FirstOrDefault()?.Id ?? "");

    public string Key(IReadOnlyDictionary<string, string> c) => GenomeStore.Key(c, Factors.Select(f => f.Id));
    public ConfigEvidence? EvidenceFor(IReadOnlyDictionary<string, string> c) { var k = Key(c); return Evidence.FirstOrDefault(e => e.Key == k); }

    public (double mean, double sd)? Predict(string metric, IReadOnlyDictionary<string, string> c) =>
        Fits.TryGetValue(metric, out var f) && f is not null ? f.Predict(c, Reference) : null;

    // ---- scoring ------------------------------------------------------------------------------------------------------

    /// <summary>Score from per-metric (mean, se). Metrics without data are left out and reported; their weight is spread over the rest.</summary>
    public static GoalScore? Combine(IReadOnlyDictionary<string, double> weights, Func<string, (double mean, double se)?> get, bool measured, int n)
    {
        double total = 0, var = 0, wsum = 0;
        var missing = new List<string>();
        foreach (var (key, w) in weights)
        {
            if (w <= 0) continue;
            if (get(key) is not { } v) { missing.Add(GenomeMetrics.Get(key).Label); continue; }
            var m = GenomeMetrics.Get(key);
            double slope = (m.Kind == MetricKind.Ratio ? 100 * Math.Exp(v.mean) : 1) * m.PointsPerUnit;
            total += w * m.Points(v.mean);
            var += Math.Pow(w * slope * v.se, 2);
            wsum += w;
        }
        if (wsum <= 0) return null;
        return new(total / wsum, 1.96 * Math.Sqrt(var) / wsum, measured, n, missing);
    }

    /// <summary>Measured score: only from this configuration's own trials.</summary>
    public GoalScore? MeasuredScore(ConfigEvidence e, GoalSettings goal) =>
        e.N == 0 ? null : Combine(GenomeGoals.WeightsOf(goal), k => e.Measured.TryGetValue(k, out var s) ? (s.Mean, s.Ci95 / 1.96) : null, true, e.N);

    /// <summary>Predicted score from the model (for untested configurations; never shown as a measured result).</summary>
    public GoalScore? PredictedScore(IReadOnlyDictionary<string, string> c, GoalSettings goal) =>
        Combine(GenomeGoals.WeightsOf(goal), k => Predict(k, c) is { } p ? (p.mean, p.sd) : null, false, 0);

    /// <summary>Hard-limit violations for per-metric mean changes.</summary>
    public List<string> Violations(Func<string, double?> change, GoalSettings g)
    {
        var v = new List<string>();
        double? Abs(string k) => change(k) is { } y && BaselineAbs.TryGetValue(k, out var b) ? (GenomeMetrics.Get(k).Kind == MetricKind.Ratio ? b * Math.Exp(y) : b + y) : null;
        if (g.MaxGpuTempC is { } gt && Abs("gputemp") is { } a1 && a1 > gt) v.Add($"GPU {a1:0} °C > limit {gt:0} °C");
        if (g.MaxCpuTempC is { } ct && Abs("cputemp") is { } a2 && a2 > ct) v.Add($"CPU {a2:0} °C > limit {ct:0} °C");
        if (g.MaxGpuPowerW is { } pw && Abs("gpupower") is { } a3 && a3 > pw) v.Add($"GPU {a3:0} W > limit {pw:0} W");
        if (g.MaxFpsLossPct is { } fl && change("fps") is { } f && (1 - Math.Exp(f)) * 100 > fl) v.Add($"FPS {(Math.Exp(f) - 1) * 100:+0.0;−0.0}% (limit −{fl:0.#}%)");
        if (g.MaxLow1LossPct is { } ll && change("low1") is { } l && (1 - Math.Exp(l)) * 100 > ll) v.Add($"1% low {(Math.Exp(l) - 1) * 100:+0.0;−0.0}% (limit −{ll:0.#}%)");
        return v;
    }

    public List<string> MeasuredViolations(ConfigEvidence e, GoalSettings g) => Violations(k => e.Measured.TryGetValue(k, out var s) ? s.Mean : null, g);
    public List<string> PredictedViolations(IReadOnlyDictionary<string, string> c, GoalSettings g) => Violations(k => Predict(k, c)?.mean, g);

    /// <summary>
    /// For choosing what to test: temperature and power limits use the predicted mean (they protect your hardware and
    /// comfort), but FPS limits only rule a candidate out when even the optimistic end of its 95% interval breaks them,
    /// because an untested interaction may well keep it inside (testing it costs nothing but time).
    /// </summary>
    public List<string> ExplorationViolations(IReadOnlyDictionary<string, string> c, GoalSettings g) => Violations(k =>
        Predict(k, c) is not { } p ? null : k is "fps" or "low1" ? p.mean + 1.96 * p.sd : p.mean, g);

    // ---- recommendations ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Distinct recommended profiles: for your goal and each preset goal, the best configuration that has been MEASURED at
    /// least twice on the current driver/Windows/game, never failed, and stays within the goal's limits. Identical winners
    /// merge into one profile. Untested configurations are never recommended (see <see cref="WorthTesting"/>).
    /// </summary>
    public List<Recommendation> Recommend(GoalSettings yourGoal)
    {
        var goals = new List<(string name, GoalSettings g)> { ("Your goal", yourGoal) };
        foreach (var p in GenomeGoals.Presets)
        {
            var g = GenomeGoals.FromPreset(p.Id);
            g.MaxGpuTempC ??= yourGoal.MaxGpuTempC; g.MaxCpuTempC ??= yourGoal.MaxCpuTempC; g.MaxGpuPowerW ??= yourGoal.MaxGpuPowerW;
            goals.Add((p.Name, g));
        }
        var winners = new List<(string goal, ConfigEvidence e, GoalScore s, GoalSettings g)>();
        var runnerUp = new Dictionary<string, (ConfigEvidence e, GoalScore s)>();
        foreach (var (name, g) in goals)
        {
            var best = Evidence.Where(e => e.N >= 2 && e.Failed.Count == 0 && MeasuredViolations(e, g).Count == 0)
                .Select(e => (e, s: MeasuredScore(e, g))).Where(x => x.s is not null)
                .OrderByDescending(x => x.s!.Mean).FirstOrDefault();
            if (best.e is null) continue;
            // Not clearly better than your settings (interval includes zero): recommend keeping yours, if they were measured.
            var mine = Evidence.FirstOrDefault(e => e.IsReference && e.N >= 2);
            if (!best.e.IsReference && best.s!.Low <= 0 && mine is not null && MeasuredScore(mine, g) is { } ms && MeasuredViolations(mine, g).Count == 0)
            {
                winners.Add((name, mine, ms with { }, g));
                runnerUp[name] = (best.e, best.s);
                continue;
            }
            winners.Add((name, best.e, best.s!, g));
        }
        var result = new List<Recommendation>();
        foreach (var grp in winners.GroupBy(w => w.e.Key))
        {
            var first = grp.First();
            var e = first.e;
            var s = first.s;
            string confidence = e.N >= 3 && s.Ci95 <= Math.Max(1.5, Math.Abs(s.Mean) / 2) ? "High" : e.N >= 2 ? "Medium" : "Low";
            var close = grp.Select(x => runnerUp.TryGetValue(x.goal, out var ru) ? ru : default).FirstOrDefault(ru => ru.e is not null);
            string verdict = e.IsReference
                ? close.e is not null
                    ? $"Keep your current settings: nothing tested is clearly better. Closest: {Describe(Store, close.e.Config, Reference)} ({close.s.Mean:+0.0;−0.0} ± {close.s.Ci95:0.0} points, within the noise; more runs would tell)."
                    : "These are your current settings: nothing tested beats them for this goal."
                : s.Low > 0 ? $"Measured better than your current settings for this goal ({s.Mean:+0.0;−0.0} points, 95% range {s.Low:+0.0;−0.0} to {s.Mean + s.Ci95:+0.0;−0.0})."
                : $"Best tested, but not clearly better than your current settings ({s.Mean:+0.0;−0.0} ± {s.Ci95:0.0} points): the difference is within the noise.";
            result.Add(new(grp.Count() > 1 ? string.Join(" · ", grp.Select(x => x.goal)) : first.goal, grp.Select(x => x.goal).ToList(), e.Config, e, s, confidence, verdict, Explain(e.Config), []));
        }
        return result;
    }

    /// <summary>The most promising configurations nobody has measured yet, by predicted score. Shown as predictions only.</summary>
    public List<(Dictionary<string, string> config, GoalScore score)> WorthTesting(GoalSettings goal, IEnumerable<Dictionary<string, string>> candidates, int take = 3) =>
        candidates.Where(c => EvidenceFor(c) is not { N: > 0 } && PredictedViolations(c, goal).Count == 0)
            .Select(c => (c, s: PredictedScore(c, goal))).Where(x => x.s is not null && x.s.Mean > 0)
            .OrderByDescending(x => x.s!.Mean).Take(take).Select(x => (x.c, x.s!)).ToList();

    // ---- frontier -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Performance (FPS change, %) against a cost (°C or % power). Measured configurations carry their 95% intervals; the
    /// Pareto front is computed from measured points only. Predicted points (hollow) are the best untested candidates.
    /// </summary>
    public List<FrontierPoint> Frontier(string costMetric, IEnumerable<(Dictionary<string, string> config, GoalScore score)> predicted, IReadOnlyDictionary<string, string> labels)
    {
        var fps = GenomeMetrics.Get("fps");
        var cost = GenomeMetrics.Get(costMetric);
        double X(double y) => (Math.Exp(y) - 1) * 100;
        double Y(double y) => cost.Kind == MetricKind.Ratio ? (Math.Exp(y) - 1) * 100 : y;
        var pts = new List<FrontierPoint>();
        foreach (var e in Evidence.Where(e => e.N > 0 && e.Measured.ContainsKey("fps") && e.Measured.ContainsKey(costMetric)))
        {
            var f = e.Measured["fps"]; var c = e.Measured[costMetric];
            pts.Add(new(e.Key, e.Config, X(f.Mean), f.Ci95 * 100, Y(c.Mean), cost.Kind == MetricKind.Ratio ? c.Ci95 * 100 : c.Ci95, true, false, e.N,
                labels.TryGetValue(e.Key, out var l) ? l : e.IsReference ? "Your settings" : ""));
        }
        var pareto = pts.Where(p => !pts.Any(q => q != p && q.X >= p.X && q.Y <= p.Y && (q.X > p.X || q.Y < p.Y))).Select(p => p.Key).ToHashSet();
        pts = pts.Select(p => p with { Pareto = pareto.Contains(p.Key) }).ToList();
        foreach (var (config, _) in predicted)
            if (Predict("fps", config) is { } f && Predict(costMetric, config) is { } c)
                pts.Add(new(Key(config), config, X(f.mean), 1.96 * f.sd * 100, Y(c.mean), 1.96 * (cost.Kind == MetricKind.Ratio ? c.sd * 100 : c.sd), false, false, 0, "Predicted, untested"));
        return pts;
    }

    // ---- explanations / learned facts ---------------------------------------------------------------------------------

    /// <summary>What each changed setting does according to the model (with how many trials support it), plus clear interactions.</summary>
    public List<string> Explain(IReadOnlyDictionary<string, string> c)
    {
        var lines = new List<string>();
        foreach (var f in Factors)
        {
            if (!c.TryGetValue(f.Id, out var lv) || Reference.GetValueOrDefault(f.Id) == lv) continue;
            var parts = new List<string>();
            int support = 0;
            foreach (var key in new[] { "fps", "low1", "p99", "gputemp", "cputemp", "gpupower", "cpupower" })
            {
                if (Fits.GetValueOrDefault(key) is not { } fit) continue;
                var single = new Dictionary<string, string>(Reference) { [f.Id] = lv };
                var (mean, sd) = fit.Predict(single, Reference);
                if (Math.Abs(mean) < 1.96 * sd || sd == 0) continue; // not clearly different: don't claim it
                var m = fit.Metric;
                parts.Add($"{m.Label} {m.Change(mean)} (±{(m.Kind == MetricKind.Ratio ? $"{1.96 * sd * 100:0.0}%" : $"{1.96 * sd:0.0} {m.Unit}")})");
                support = Math.Max(support, fit.Effects().Where(e => e.Feature.FactorA == f.Id || e.Feature.FactorB == f.Id).Select(e => e.Support).DefaultIfEmpty(0).Max());
            }
            string from = f.LabelOf(Reference.GetValueOrDefault(f.Id, "")), to = f.LabelOf(lv);
            lines.Add(parts.Count == 0
                ? $"{f.Name}: {from} → {to}. No clear effect measured on its own (within the noise)."
                : $"{f.Name}: {from} → {to}. Model, from {support} trial(s): {string.Join(", ", parts)}.");
        }
        foreach (var inter in Interactions().Where(i => c.GetValueOrDefault(i.feature.FactorA) == i.feature.LevelA && c.GetValueOrDefault(i.feature.FactorB!) == i.feature.LevelB))
            lines.Add("Interaction: " + inter.text);
        if (lines.Count == 0) lines.Add("Same settings as yours now.");
        return lines;
    }

    /// <summary>Interactions the data shows clearly (95% interval excludes zero): two settings that together do something different from the sum of each alone.</summary>
    public List<(GenomeFeature feature, string text)> Interactions()
    {
        var list = new List<(GenomeFeature, string)>();
        foreach (var (key, fit) in Fits)
        {
            if (fit is null) continue;
            foreach (var e in fit.Effects().Where(e => e.Feature.IsInteraction && e.Clear && e.Support >= 2))
            {
                var fa = Store.Factor(e.Feature.FactorA)!; var fb = Store.Factor(e.Feature.FactorB!)!;
                list.Add((e.Feature, $"{fa.Name} {fa.LabelOf(e.Feature.LevelA)} + {fb.Name} {fb.LabelOf(e.Feature.LevelB!)}: {fit.Metric.Label} {fit.Metric.Change(e.Mean)} beyond their separate effects (±{(fit.Metric.Kind == MetricKind.Ratio ? $"{1.96 * e.Sd * 100:0.0}%" : $"{1.96 * e.Sd:0.0} {fit.Metric.Unit}")}, {e.Support} trials)."));
            }
        }
        return list;
    }

    /// <summary>The evolving hardware profile: every factor level's effect on the key metrics vs. your current level.</summary>
    public List<HardwareFact> Facts()
    {
        var facts = new List<HardwareFact>();
        foreach (var f in Factors)
            foreach (var l in f.Levels)
            {
                if (Reference.GetValueOrDefault(f.Id) == l.Id) continue;
                var single = new Dictionary<string, string>(Reference) { [f.Id] = l.Id };
                foreach (var key in new[] { "fps", "low1", "gputemp", "gpupower", "cputemp", "cpupower" })
                {
                    if (Fits.GetValueOrDefault(key) is not { } fit) continue;
                    int support = fit.Effects().Where(e => !e.Feature.IsInteraction && e.Feature.FactorA == f.Id).Select(e => e.Support).DefaultIfEmpty(0).Sum();
                    if (support == 0) continue;
                    var (mean, sd) = fit.Predict(single, Reference);
                    facts.Add(new(f, l, fit.Metric, mean, sd, support, Math.Abs(mean) > 1.96 * sd && sd > 0));
                }
            }
        return facts;
    }

    public static string Describe(GenomeStore s, IReadOnlyDictionary<string, string> c, IReadOnlyDictionary<string, string>? reference = null) =>
        string.Join(" · ", s.Factors.Where(f => f.Enabled && c.ContainsKey(f.Id) && (reference is null || reference.GetValueOrDefault(f.Id) != c[f.Id]))
            .Select(f => $"{f.Name} {f.LabelOf(c[f.Id])}")) is { Length: > 0 } d ? d : "Your current settings";

    public static string Pct(double v) => v.ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture);
}
