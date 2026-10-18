namespace MoneyTweaks.Core.Genome;

/// <summary>One trial's result for one metric, relative to the baseline runs of its own session.</summary>
public sealed record Observation(Trial Trial, IReadOnlyDictionary<string, string> Config, IReadOnlyDictionary<string, string> Base, double Y);

/// <summary>Mean, spread and 95% interval of repeated normalized results.</summary>
public sealed record MetricSummary(int N, double Mean, double Sd, double Ci95)
{
    public double Low => Mean - Ci95;
    public double High => Mean + Ci95;
}

/// <summary>A feature of the linear model: a factor level (main effect) or two factor levels together (interaction).</summary>
public sealed record GenomeFeature(string FactorA, string LevelA, string? FactorB = null, string? LevelB = null)
{
    public bool IsInteraction => FactorB is not null;
    public double Value(IReadOnlyDictionary<string, string> c) =>
        Has(c, FactorA, LevelA) && (FactorB is null || Has(c, FactorB, LevelB!)) ? 1 : 0;
    private static bool Has(IReadOnlyDictionary<string, string> c, string f, string l) => c.TryGetValue(f, out var v) && v == l;
}

/// <summary>Effect estimate for one feature (in the metric's normalized unit).</summary>
public sealed record EffectEstimate(GenomeFeature Feature, double Mean, double Sd, int Support)
{
    /// <summary>Clearly non-zero: the 95% interval excludes zero.</summary>
    public bool Clear => Math.Abs(Mean) > 1.96 * Sd;
}

/// <summary>
/// Bayesian ridge regression of one metric: y ≈ Σ β·(φ(config) − φ(baseline)). Main effects get a weak prior, interactions
/// a tighter one, so an interaction is only believed when the data shows it. Noise is estimated from the residuals with a
/// prior, so few trials give wide (honest) uncertainty.
/// </summary>
public sealed class MetricFit
{
    public required GenomeMetric Metric { get; init; }
    public required List<GenomeFeature> Features { get; init; }
    public required double[] Beta { get; init; }
    public required double[,] Cov { get; init; }
    public required double Sigma { get; init; }
    public required int N { get; init; }
    public required int[] Support { get; init; }

    public (double mean, double sd) Predict(IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, string> reference)
    {
        int p = Features.Count;
        var x = new double[p];
        for (int i = 0; i < p; i++) x[i] = Features[i].Value(config) - Features[i].Value(reference);
        double mean = 0, v = 0;
        for (int i = 0; i < p; i++) { mean += x[i] * Beta[i]; for (int j = 0; j < p; j++) v += x[i] * Cov[i, j] * x[j]; }
        return (mean, Math.Sqrt(Math.Max(v, 0)));
    }

    public List<EffectEstimate> Effects() => Features.Select((f, i) => new EffectEstimate(f, Beta[i], Math.Sqrt(Math.Max(Cov[i, i], 0)), Support[i])).ToList();
}

public static class GenomeStats
{
    /// <summary>Prior noise per trial (normalized units) and prior effect size, per metric kind.</summary>
    public static (double noise, double effect) Priors(GenomeMetric m) => m.Key switch
    {
        "gputemp" or "cputemp" => (1.0, 8.0),
        "cv" => (0.6, 4.0),
        _ => (0.02, 0.15),
    };

    /// <summary>
    /// Normalized observations of <paramref name="metric"/> for a workload: each OK trial relative to its session's baseline
    /// runs: a locally weighted trend through the session's baseline runs at this trial's position (drift and warm-up cancel out). A baseline run is
    /// compared with the other baselines (leave-one-out), so baselines also measure the noise. Trials from another
    /// fingerprint (driver, Windows, BIOS, game version) are left out when <paramref name="current"/> is given.
    /// </summary>
    public static List<Observation> Observe(GenomeStore s, string workloadId, GenomeMetric metric, Fingerprint? current)
    {
        var list = new List<Observation>();
        bool Valid(Trial t) => t.WorkloadId == workloadId && t.Status == TrialStatus.Ok && t.Measured?.Get(metric.Key) is { } v && (metric.Kind == MetricKind.Delta || v > 0)
            && !(metric.FrameBased && t.Measured.FpsCapped) && (current is null || t.Fingerprint.SameAs(current));
        foreach (var session in s.Trials.Where(Valid).GroupBy(t => t.SessionId))
        {
            // Store order is run order (trials are appended as they finish); interpolate by position, which is robust even
            // when runs are short or the clock jumps.
            var trials = session.ToList();
            var pos = trials.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
            var baselines = trials.Where(t => t.IsBaseline).ToList();
            var baseConfig = s.Sessions.FirstOrDefault(x => x.Id == session.Key)?.Original is { Count: > 0 } o ? o : baselines.FirstOrDefault()?.Config;
            if (baseConfig is null) continue;
            foreach (var t in trials)
            {
                var others = baselines.Where(b => b != t).ToList();
                if (others.Count == 0) continue;
                double Val(Trial b) => b.Measured!.Get(metric.Key)!.Value;
                if (metric.Kind == MetricKind.Ratio && others.Any(b => Val(b) <= 0)) continue;
                // Baseline at this trial's position: a locally weighted linear trend through ALL other baseline runs of the
                // session (ratio metrics on a log scale). Averages baseline noise down instead of leaning on two runs, and
                // still follows slow, even non-linear, drift such as warm-up.
                double Tx(Trial b) => metric.Kind == MetricKind.Ratio ? Math.Log(Val(b)) : Val(b);
                double h = Math.Max(4, trials.Count / 4.0);
                double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
                foreach (var b in others)
                {
                    double x = pos[b] - pos[t], wgt = Math.Exp(-0.5 * (x / h) * (x / h)), v = Tx(b);
                    sw += wgt; sx += wgt * x; sy += wgt * v; sxx += wgt * x * x; sxy += wgt * x * v;
                }
                double denom = sw * sxx - sx * sx;
                double baseAt = denom > 1e-9 * sw * sw ? (sy * sxx - sx * sxy) / denom : sy / sw; // intercept at x = 0
                double v0 = metric.Kind == MetricKind.Ratio ? Math.Log(Val(t)) : Val(t);
                double y = v0 - baseAt;
                list.Add(new(t, t.Config, baseConfig, y));
            }
        }
        return list;
    }

    /// <summary>Fits the model. Null when there is nothing to learn from yet.</summary>
    public static MetricFit? Fit(GenomeMetric metric, IReadOnlyList<Observation> obs, IReadOnlyList<GenomeFactor> factors)
    {
        if (obs.Count == 0) return null;
        var (noise0, effect) = Priors(metric);
        // Main effects: every non-reference level of every factor seen. Reference = first level (any choice works because
        // the model uses differences to the baseline).
        var main = new List<GenomeFeature>();
        foreach (var f in factors)
            foreach (var l in f.Levels.Skip(1))
                main.Add(new(f.Id, l.Id));
        // Interactions: only level pairs that actually occur together (in a trial or its baseline) at least twice.
        var inter = new Dictionary<GenomeFeature, int>();
        foreach (var o in obs)
            foreach (var c in new[] { o.Config, o.Base })
            {
                var present = main.Where(m => m.Value(c) > 0).ToList();
                for (int i = 0; i < present.Count; i++)
                    for (int j = i + 1; j < present.Count; j++)
                    {
                        var (a, b) = string.CompareOrdinal(present[i].FactorA, present[j].FactorA) < 0 ? (present[i], present[j]) : (present[j], present[i]);
                        var key = new GenomeFeature(a.FactorA, a.LevelA, b.FactorA, b.LevelA);
                        inter[key] = inter.GetValueOrDefault(key) + (c == o.Config ? 1 : 0);
                    }
            }
        var all = main.Concat(inter.Where(kv => kv.Value >= 2).Select(kv => kv.Key)).ToList();
        int n = obs.Count;
        var X = new double[n, all.Count];
        var support = new int[all.Count];
        for (int r = 0; r < n; r++)
            for (int k = 0; k < all.Count; k++)
            {
                X[r, k] = all[k].Value(obs[r].Config) - all[k].Value(obs[r].Base);
                if (X[r, k] != 0) support[k]++;
            }
        // Drop features no observation moves (they can't be learned; the prior would just echo zero).
        var keep = Enumerable.Range(0, all.Count).Where(k => support[k] > 0).ToList();
        var feats = keep.Select(k => all[k]).ToList();
        int p = feats.Count;
        var y = obs.Select(o => o.Y).ToArray();
        double sigma2 = noise0 * noise0;
        double[] beta = new double[p];
        double[,] cov = new double[p, p];
        for (int iter = 0; iter < 3; iter++)
        {
            var A = new double[p, p];
            var b = new double[p];
            for (int i = 0; i < p; i++)
            {
                double tau = feats[i].IsInteraction ? effect / 2 : effect;
                A[i, i] += sigma2 / (tau * tau);
                for (int r = 0; r < n; r++)
                {
                    double xi = X[r, keep[i]];
                    if (xi == 0) continue;
                    b[i] += xi * y[r];
                    for (int j = 0; j < p; j++) A[i, j] += xi * X[r, keep[j]];
                }
            }
            var inv = Invert(A);
            beta = new double[p];
            for (int i = 0; i < p; i++) for (int j = 0; j < p; j++) beta[i] += inv[i, j] * b[j];
            double rss = 0;
            for (int r = 0; r < n; r++)
            {
                double pred = 0;
                for (int i = 0; i < p; i++) pred += X[r, keep[i]] * beta[i];
                rss += (y[r] - pred) * (y[r] - pred);
            }
            const double nu0 = 4;
            double df = Math.Max(0, n - Math.Min(p, n / 2.0));
            sigma2 = (nu0 * noise0 * noise0 + rss) / (nu0 + df);
            for (int i = 0; i < p; i++) for (int j = 0; j < p; j++) cov[i, j] = inv[i, j] * sigma2;
        }
        return new MetricFit { Metric = metric, Features = feats, Beta = beta, Cov = cov, Sigma = Math.Sqrt(sigma2), N = n, Support = keep.Select(k => support[k]).ToArray() };
    }

    /// <summary>
    /// Repeated measurements of one configuration. The spread pools this configuration's own scatter with the run-to-run
    /// noise the model measured across all trials (worth 6 runs), so two lucky identical runs can't claim false precision
    /// and one run still gets an honest interval.
    /// </summary>
    public static MetricSummary? Summarize(IReadOnlyList<double> ys, double modelSigma)
    {
        if (ys.Count == 0) return null;
        int n = ys.Count;
        double mean = ys.Average();
        double sd = n > 1 ? Math.Sqrt(ys.Sum(v => (v - mean) * (v - mean)) / (n - 1)) : 0;
        const double pooledDf = 6;
        double s = Math.Sqrt(((n - 1) * sd * sd + pooledDf * modelSigma * modelSigma) / (n - 1 + pooledDf));
        double t = 2.0 + 2.0 / (n - 1 + pooledDf); // about Student's t at 95% for those degrees of freedom
        return new(n, mean, sd, t * s / Math.Sqrt(n));
    }

    /// <summary>Gauss–Jordan inverse with partial pivoting (small symmetric positive-definite matrices).</summary>
    public static double[,] Invert(double[,] a)
    {
        int n = a.GetLength(0);
        var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n + i] = 1; }
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-12) throw new InvalidOperationException("singular matrix");
            if (piv != c) for (int j = 0; j < 2 * n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            double d = m[c, c];
            for (int j = 0; j < 2 * n; j++) m[c, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c || m[r, c] == 0) continue;
                double f = m[r, c];
                for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[c, j];
            }
        }
        var inv = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }
}
