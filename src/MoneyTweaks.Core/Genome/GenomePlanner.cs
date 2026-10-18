namespace MoneyTweaks.Core.Genome;

public sealed record PlannedTrial(Dictionary<string, string> Config, string Reason, bool IsBaseline);

/// <summary>
/// Chooses the next configuration to measure, in this order:
/// 1. Screening: every setting level on its own (one change from your settings), twice, so each main effect is known.
/// 2. Interactions: the best levels of the most promising settings are tested together (twice), so the model sees
///    whether they add up, cancel or amplify. This comes before repeats so it is never starved by them.
/// 3. Repeats: a promising configuration measured only once or twice is repeated until it has 3 runs (noise).
/// 4. Explore/exploit: the candidate with the highest upper confidence bound of the goal score (prediction + uncertainty).
/// Never: a level or combination that failed before, or a candidate predicted to break a hard limit.
/// </summary>
public static class GenomePlanner
{
    public const int ScreenRuns = 2, RepeatTo = 3;

    /// <summary>All configurations of the factors (full factorial when small, otherwise a sample plus every single change
    /// and every pair change from the reference).</summary>
    public static List<Dictionary<string, string>> Candidates(IReadOnlyList<GenomeFactor> factors, IReadOnlyDictionary<string, string> reference, Random rng, int max = 400)
    {
        var list = new List<Dictionary<string, string>>();
        double total = factors.Aggregate(1.0, (acc, f) => acc * Math.Max(1, f.Levels.Count));
        if (total <= max)
        {
            IEnumerable<Dictionary<string, string>> acc = [new Dictionary<string, string>(reference)];
            foreach (var f in factors)
                acc = acc.SelectMany(c => f.Levels.Select(l => new Dictionary<string, string>(c) { [f.Id] = l.Id })).ToList();
            return acc.ToList();
        }
        var seen = new HashSet<string>();
        void Add(Dictionary<string, string> c) { if (seen.Add(GenomeStore.Key(c))) list.Add(c); }
        Add(new Dictionary<string, string>(reference));
        foreach (var f in factors)
            foreach (var l in f.Levels)
            {
                Add(new Dictionary<string, string>(reference) { [f.Id] = l.Id });
                foreach (var g in factors.Where(g => string.CompareOrdinal(g.Id, f.Id) > 0))
                    foreach (var m in g.Levels)
                        Add(new Dictionary<string, string>(reference) { [f.Id] = l.Id, [g.Id] = m.Id });
            }
        while (list.Count < max)
            Add(factors.ToDictionary(f => f.Id, f => f.Levels[rng.Next(f.Levels.Count)].Id));
        return list;
    }

    /// <summary>True when a failed level (or failed combination) is part of this configuration.</summary>
    public static bool Blocked(IReadOnlyDictionary<string, string> c, IReadOnlyDictionary<string, string> reference, GenomeStore s, string workloadId) =>
        s.Failures.Any(f => f.WorkloadId == workloadId && f.Levels.Count > 0
            && f.Levels.All(kv => c.TryGetValue(kv.Key, out var v) && v == kv.Value && reference.GetValueOrDefault(kv.Key) != kv.Value));

    public static PlannedTrial Next(GenomeAnalysis a, GoalSettings goal, Random rng)
    {
        var s = a.Store; var w = a.Workload.Id; var refc = a.Reference;
        bool Ok(Dictionary<string, string> c) => !Blocked(c, refc, s, w);
        int N(Dictionary<string, string> c) => a.EvidenceFor(c)?.N ?? 0;

        // 1. Screening.
        var screen = new List<(Dictionary<string, string> c, string why, int n)>();
        foreach (var f in a.Factors)
            foreach (var l in f.Levels.Where(l => l.Id != refc.GetValueOrDefault(f.Id)))
            {
                var c = new Dictionary<string, string>(refc) { [f.Id] = l.Id };
                if (Ok(c) && N(c) < ScreenRuns) screen.Add((c, $"screening: {f.Name} {l.Label}", N(c)));
            }
        if (screen.Count > 0)
        {
            int min = screen.Min(x => x.n);
            var pick = screen.Where(x => x.n == min).OrderBy(_ => rng.Next()).First();
            return new(pick.c, pick.why, false);
        }

        // 2. Interactions between the most promising settings.
        var bestLevels = a.Factors.Select(f =>
        {
            var lv = f.Levels.Where(l => l.Id != refc.GetValueOrDefault(f.Id))
                .Select(l => (l, sc: a.PredictedScore(new Dictionary<string, string>(refc) { [f.Id] = l.Id }, goal)?.Mean ?? double.MinValue))
                .OrderByDescending(x => x.sc).FirstOrDefault();
            return (f, lv.l, lv.sc);
        }).Where(x => x.l is not null && x.sc > 0).OrderByDescending(x => x.sc).Take(3).ToList();
        for (int i = 0; i < bestLevels.Count; i++)
            for (int j = i + 1; j < bestLevels.Count; j++)
            {
                var c = new Dictionary<string, string>(refc) { [bestLevels[i].f.Id] = bestLevels[i].l!.Id, [bestLevels[j].f.Id] = bestLevels[j].l!.Id };
                if (Ok(c) && N(c) < ScreenRuns && a.ExplorationViolations(c, goal).Count == 0)
                    return new(c, $"interaction: {bestLevels[i].f.Name} {bestLevels[i].l!.Label} + {bestLevels[j].f.Name} {bestLevels[j].l!.Label}", false);
            }

        // 3. Repeat promising configurations that have too few runs.
        var promising = a.Evidence.Where(e => !e.IsReference && e.N is > 0 and < RepeatTo && e.Failed.Count == 0 && Ok(e.Config))
            .Select(e => (e, sc: a.MeasuredScore(e, goal))).Where(x => x.sc is { Mean: > 0 } && a.MeasuredViolations(x.e, goal).Count == 0)
            .OrderByDescending(x => x.sc!.Mean).FirstOrDefault();
        if (promising.e is not null) return new(new(promising.e.Config), $"repeat ({promising.e.N + 1} of {RepeatTo}) to reduce noise", false);

        // 4. Upper confidence bound over all candidates.
        var cands = Candidates(a.Factors, refc, rng);
        var ucb = cands.Where(c => Ok(c) && a.ExplorationViolations(c, goal).Count == 0 && N(c) < 6)
            .Select(c => (c, sc: a.PredictedScore(c, goal)))
            .Where(x => x.sc is not null)
            .Select(x => (x.c, u: x.sc!.Mean + 1.5 * x.sc.Ci95 / 1.96 - 0.3 * N(x.c)))
            .OrderByDescending(x => x.u).FirstOrDefault();
        if (ucb.c is not null) return new(ucb.c, "explore: highest upper confidence bound for your goal", false);

        return new(new(refc), "baseline (nothing left to explore)", true);
    }
}
