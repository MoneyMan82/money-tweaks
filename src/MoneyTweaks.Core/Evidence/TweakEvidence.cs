using System.Globalization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Core.Evidence;

/// <summary>How a test was run. Results of different kinds measure different things, so they're never pooled.</summary>
public enum TrialKind { SyntheticScene, RealityLab, OwnTest }

/// <summary>
/// One measured A/B comparison of a single tweak: the Baseline and After runs of a benchmark study in which only this
/// tweak differed. <see cref="Issues"/> lists why it can't be used (then <see cref="Valid"/> is false).
/// </summary>
public sealed record Trial(
    BenchmarkStudy Study, string TweakId, TrialKind Kind, string? Game, int RunsBefore, int RunsAfter,
    IReadOnlyDictionary<string, MetricComparison> Metrics, IReadOnlyList<string> Issues)
{
    public bool Valid => Issues.Count == 0;
    public DateTime At => Study.Created;
}

/// <summary>How much the measured evidence supports a conclusion.</summary>
public enum EvidenceLevel { Insufficient, Inconsistent, Low, Medium, High }

/// <summary>Effect of a tweak on one metric across valid trials of one kind.</summary>
public sealed record MetricEvidence(
    Metric Metric, int Trials, double? MedianPct, double? SpreadPct, double Repeatability, double RegressionRate,
    EvidenceLevel Level, Verdict? Consensus)
{
    /// <summary>Plain-language conclusion, or null while there isn't enough evidence to state one.</summary>
    public string? Conclusion => Level switch
    {
        EvidenceLevel.Insufficient => null,
        EvidenceLevel.Inconsistent => "No consistent effect: repeated tests disagree.",
        _ => Consensus switch
        {
            Verdict.Better => $"Better: {Fmt(MedianPct)}{Direction} (median of {Trials} tests).",
            Verdict.Worse => $"Worse: {Fmt(MedianPct)}{Direction} (median of {Trials} tests).",
            _ => $"No measurable effect in {Trials} tests.",
        },
    };

    /// <summary>For metrics where lower is better (frame time, CPU load, temperatures), say so next to a negative number.</summary>
    private string Direction => Metric.HigherIsBetter ? "" : " (lower is better)";

    private static string Fmt(double? pct) => pct is { } p ? p.ToString("+0.0;-0.0", CultureInfo.CurrentCulture) + "%" : "–";
}

public sealed record EvidenceGroup(TrialKind Kind, string? Game, IReadOnlyList<Trial> Trials, IReadOnlyList<MetricEvidence> Metrics);

/// <summary>Everything measured on this PC about one tweak.</summary>
public sealed record TweakEvidenceReport(
    string TweakId, IReadOnlyList<EvidenceGroup> Groups, IReadOnlyList<Trial> Excluded, IReadOnlyList<BenchmarkStudy> CombinedTests)
{
    public int ValidTrials => Groups.Sum(g => g.Trials.Count);
    public bool HasAnything => ValidTrials > 0 || Excluded.Count > 0 || CombinedTests.Count > 0;
}

/// <summary>
/// Tweak Truth Index (local part): turns the benchmark history into per-tweak evidence. Only studies that switched exactly
/// one tweak count towards that tweak; tests that changed several tweaks are listed but never attributed. Each trial must
/// have at least two runs per state and comparable conditions. Effects are combined with medians (robust to one odd
/// test), and nothing is concluded before <see cref="MinTrials"/> valid tests agree enough.
/// </summary>
public static class TweakEvidence
{
    public const int MinTrials = 3;

    /// <summary>Metrics the index reports, in display order.</summary>
    public static readonly string[] MetricKeys = ["fps", "low1", "ft99", "cpu", "gpu", "ram", "cputemp", "gputemp"];
    private static readonly HashSet<string> FrameMetrics = ["fps", "low1", "low01", "ft", "ft99"];

    public static string KindLabel(TrialKind k) => k switch
    {
        TrialKind.SyntheticScene => "Synthetic 3D scene",
        TrialKind.RealityLab => "Reality Lab",
        _ => "Your own before/after test",
    };

    /// <summary>
    /// The tweaks a study switched between its Baseline and After runs. Uses <see cref="BenchmarkStudy.SwitchedIds"/>;
    /// for studies saved before it existed: the tweaks a tweak/preset study's Apply changed, or the tweak named in a
    /// "preset → tweak" synthetic study. Empty when it can't be known (older Reality Lab and preset-wide synthetic tests).
    /// </summary>
    public static IReadOnlyList<string> SwitchedIds(BenchmarkStudy s, Func<string, Tweak?> find, Func<string, Tweak?> findByName)
    {
        if (s.SwitchedIds.Count > 0) return s.SwitchedIds;
        // Own before/after studies: what Apply changed between the phases (nothing applied = nothing was compared).
        if (s.TargetKind is "tweak" or "preset" && s.AppliedIds.Count > 0) return s.AppliedIds.Where(id => find(id) is not null).ToList();
        if (s.TargetKind == "synthetic" && s.TargetName.Split(" → ") is [_, var name] && findByName(name.Trim()) is { } t) return [t.Id];
        return [];
    }

    private static readonly Regex DisplayHz = new(@"display (\d+) Hz", RegexOptions.CultureInvariant);

    /// <summary>Turns one single-tweak study into a trial, recording every reason it can't be used.</summary>
    public static Trial ToTrial(BenchmarkStudy s, string tweakId, IReadOnlyList<BenchmarkRun> runs)
    {
        var before = runs.Where(r => r.Phase == "Baseline").ToList();
        var after = runs.Where(r => r.Phase == "After").ToList();
        var kind = s.TargetKind switch { "synthetic" => TrialKind.SyntheticScene, "lab" => TrialKind.RealityLab, _ => TrialKind.OwnTest };
        var all = before.Concat(after).ToList();
        string? game = kind == TrialKind.OwnTest && all.Count > 0 && all.All(r => r.Conditions.FortniteRunning) ? "Fortnite" : null;

        var issues = new List<string>();
        if (before.Count < 2 || after.Count < 2) issues.Add($"needs at least 2 runs with and without (has {before.Count} and {after.Count})");
        if (all.Select(r => r.Conditions.DurationSec).Distinct().Count() > 1) issues.Add("runs have different lengths");
        if (all.Select(r => r.Conditions.OnAcPower).Distinct().Count() > 1) issues.Add("some runs on battery, some on mains power");
        if (all.Select(r => r.Conditions.FortniteRunning).Distinct().Count() > 1) issues.Add("Fortnite ran during some runs but not others");
        if (all.Count(r => r.Conditions.IdleCpuBefore > 20 && !r.Conditions.FortniteRunning) * 2 > all.Count) issues.Add("most runs started with the PC already busy (over 20% CPU)");

        // Frame metrics are useless when every run hit the display's refresh cap.
        var m = DisplayHz.Match(s.Scene);
        bool capped = m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var hz) && hz > 0
                      && all.Count > 0 && all.All(r => r.Frames is { } f && f.AvgFps >= hz * 0.97);

        var metrics = new Dictionary<string, MetricComparison>();
        foreach (var metric in BenchmarkStats.Metrics.Where(x => MetricKeys.Contains(x.Key)))
        {
            if (capped && FrameMetrics.Contains(metric.Key)) continue;
            var c = BenchmarkStats.Compare(metric, before, after);
            if (c.Before is not null && c.After is not null) metrics[metric.Key] = c;
        }
        if (issues.Count == 0 && metrics.Count == 0) issues.Add(capped ? "every run hit the display's refresh cap and nothing else was measured" : "no metric was measured on both sides");
        return new Trial(s, tweakId, kind, game, before.Count, after.Count, metrics, issues);
    }

    /// <summary>Combines valid trials for one metric (median effect, agreement, regressions).</summary>
    public static MetricEvidence Aggregate(Metric metric, IReadOnlyList<Trial> trials)
    {
        var cs = trials.Where(t => t.Metrics.ContainsKey(metric.Key)).Select(t => t.Metrics[metric.Key]).Where(c => c.Pct is not null).ToList();
        int n = cs.Count;
        if (n == 0) return new(metric, 0, null, null, 0, 0, EvidenceLevel.Insufficient, null);
        var pcts = cs.Select(c => c.Pct!.Value).OrderBy(x => x).ToList();
        double median = Median(pcts);
        double spread = Median(pcts.Select(p => Math.Abs(p - median)).OrderBy(x => x).ToList()); // MAD
        var votes = cs.GroupBy(c => c.Verdict).ToDictionary(g => g.Key, g => g.Count());
        var consensus = votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key == Verdict.NoClearChange ? 0 : 1).First().Key;
        double agreement = votes[consensus] / (double)n;
        double regressions = votes.GetValueOrDefault(Verdict.Worse) / (double)n;
        var level = n < MinTrials ? EvidenceLevel.Insufficient
            : agreement < 0.6 ? EvidenceLevel.Inconsistent
            : n >= 5 && agreement >= 0.8 ? EvidenceLevel.High
            : agreement >= 0.67 ? EvidenceLevel.Medium
            : EvidenceLevel.Low;
        return new(metric, n, Math.Round(median, 2), Math.Round(spread, 2), Math.Round(agreement, 3), Math.Round(regressions, 3), level,
            level == EvidenceLevel.Insufficient ? null : consensus);
    }

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count == 0 ? double.NaN : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>Evidence for every tweak that has any, keyed by tweak id.</summary>
    public static Dictionary<string, TweakEvidenceReport> Build(BenchmarkFile f, Func<string, Tweak?> find, Func<string, Tweak?> findByName)
    {
        var runsByStudy = f.Runs.GroupBy(r => r.StudyId).ToDictionary(g => g.Key, g => (IReadOnlyList<BenchmarkRun>)g.ToList());
        var trials = new Dictionary<string, List<Trial>>();
        var combined = new Dictionary<string, List<BenchmarkStudy>>();
        foreach (var s in f.Studies)
        {
            var ids = SwitchedIds(s, find, findByName);
            if (ids.Count == 0 || !runsByStudy.TryGetValue(s.Id, out var runs)) continue;
            if (ids.Count > 1)
            {
                foreach (var id in ids.Distinct()) (combined.TryGetValue(id, out var l) ? l : combined[id] = []).Add(s);
                continue;
            }
            (trials.TryGetValue(ids[0], out var tl) ? tl : trials[ids[0]] = []).Add(ToTrial(s, ids[0], runs));
        }
        var result = new Dictionary<string, TweakEvidenceReport>();
        foreach (var id in trials.Keys.Concat(combined.Keys).Distinct())
        {
            var ts = trials.GetValueOrDefault(id) ?? [];
            var groups = ts.Where(t => t.Valid).GroupBy(t => (t.Kind, t.Game)).OrderBy(g => g.Key.Kind)
                .Select(g => new EvidenceGroup(g.Key.Kind, g.Key.Game, g.OrderBy(t => t.At).ToList(),
                    BenchmarkStats.Metrics.Where(m => MetricKeys.Contains(m.Key)).Select(m => Aggregate(m, g.ToList())).Where(e => e.Trials > 0).ToList()))
                .ToList();
            result[id] = new(id, groups, ts.Where(t => !t.Valid).ToList(), combined.GetValueOrDefault(id) ?? []);
        }
        return result;
    }

    public static TweakEvidenceReport For(string tweakId, BenchmarkFile f, Func<string, Tweak?> find, Func<string, Tweak?> findByName) =>
        Build(f, find, findByName).GetValueOrDefault(tweakId) ?? new(tweakId, [], [], []);
}
