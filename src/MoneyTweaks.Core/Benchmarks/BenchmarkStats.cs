using System.Globalization;

namespace MoneyTweaks.Core.Benchmarks;

public sealed record Metric(string Key, string Label, string Unit, bool HigherIsBetter, Func<BenchmarkRun, double?> Get, double NoiseFloor, string Source);

public sealed record Summary(int N, double Mean, double Sd)
{
    public double Cv => Mean == 0 ? 0 : Sd / Math.Abs(Mean) * 100;
}

public enum Verdict { NotEnoughData, NoClearChange, Better, Worse }

public sealed record MetricComparison(Metric Metric, Summary? Before, Summary? After, double? Diff, double? Pct, Verdict Verdict, string Explanation);

public static class BenchmarkStats
{
    /// <summary>
    /// Noise floors are the smallest change treated as real even when runs are very consistent (e.g. ICMP ping is
    /// reported in whole milliseconds; FPS counters wobble by about 1%).
    /// </summary>
    public static readonly IReadOnlyList<Metric> Metrics =
    [
        new("fps", "Average FPS", "fps", true, r => r.Frames?.AvgFps, 1.0, "frame-time CSV or manual"),
        new("low1", "1% low FPS", "fps", true, r => r.Frames?.Low1Fps, 2.0, "frame-time CSV or manual"),
        new("low01", "0.1% low FPS", "fps", true, r => r.Frames?.Low01Fps, 3.0, "frame-time CSV"),
        new("ft", "Avg frame time", "ms", false, r => r.Frames?.FrameTimeAvgMs, 0.1, "frame-time CSV"),
        new("ft99", "99th pct frame time", "ms", false, r => r.Frames?.FrameTimeP99Ms, 0.2, "frame-time CSV"),
        new("gameping", "Fortnite ping (in-game)", "ms", false, r => r.GamePingMs, 2.0, "typed in from Fortnite's Net Debug Stats"),
        new("ping", "Endpoint latency", "ms", false, r => r.PingAvgMs, 1.0, "ICMP to the test endpoint (not Fortnite's server)"),
        new("jitter", "Endpoint jitter", "ms", false, r => r.JitterMs, 0.5, "ICMP"),
        new("loss", "Packet loss", "%", false, r => r.LossPct, 0.5, "ICMP"),
        new("cpu", "CPU utilization", "%", false, r => r.CpuAvg, 1.0, "Windows performance counters"),
        new("gpu", "GPU utilization", "%", false, r => r.GpuAvg, 1.0, "Windows GPU Engine counters"),
        new("ram", "RAM in use", "GB", false, r => r.RamUsedGb, 0.1, "GlobalMemoryStatusEx"),
        new("cputemp", "CPU temperature", "°C", false, r => r.CpuTempC, 1.0, "ACPI thermal zone (not on every PC)"),
        new("gputemp", "GPU temperature", "°C", false, r => r.GpuTempC, 1.0, "nvidia-smi (NVIDIA only)"),
    ];

    /// <summary>Changes smaller than this fraction of the baseline are never called meaningful.</summary>
    public const double RelativeFloor = 0.01;

    public static Summary? Summarize(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).ToList();
        if (v.Count == 0) return null;
        var mean = v.Average();
        var sd = v.Count > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) : 0;
        return new(v.Count, mean, sd);
    }

    /// <summary>
    /// Conclusive only with ≥2 runs on each side, and when the difference exceeds both the metric's noise floor and
    /// twice the combined standard error (roughly a 95% confidence bar). Otherwise the change is reported as unclear.
    /// </summary>
    public static MetricComparison Compare(Metric m, IReadOnlyList<BenchmarkRun> before, IReadOnlyList<BenchmarkRun> after)
    {
        var b = Summarize(before.Select(m.Get));
        var a = Summarize(after.Select(m.Get));
        if (b is null || a is null) return new(m, b, a, null, null, Verdict.NotEnoughData, "No data on one side.");
        var diff = a.Mean - b.Mean;
        double? pct = b.Mean == 0 ? null : diff / Math.Abs(b.Mean) * 100;
        if (b.N < 2 || a.N < 2) return new(m, b, a, diff, pct, Verdict.NotEnoughData, "Needs at least 2 runs before and 2 after to judge.");
        var se = Math.Sqrt(b.Sd * b.Sd / b.N + a.Sd * a.Sd / a.N);
        // Absolute floor, a 1% relative floor (so e.g. frame time at 30 FPS needs the same % change as FPS), and 2 x SE.
        var threshold = Math.Max(Math.Max(m.NoiseFloor, RelativeFloor * Math.Abs(b.Mean)), 2 * se);
        if (Math.Abs(diff) <= threshold)
            return new(m, b, a, diff, pct, Verdict.NoClearChange, $"Difference {Fmt(Math.Abs(diff))} {m.Unit} is within run-to-run variation (±{Fmt(threshold)} {m.Unit}).");
        bool better = m.HigherIsBetter ? diff > 0 : diff < 0;
        return new(m, b, a, diff, pct, better ? Verdict.Better : Verdict.Worse, $"Difference exceeds run-to-run variation (±{Fmt(threshold)} {m.Unit}).");
    }

    /// <summary>Warnings about runs whose conditions don't match, which would make a comparison unfair.</summary>
    public static List<string> ConditionWarnings(IReadOnlyList<BenchmarkRun> before, IReadOnlyList<BenchmarkRun> after)
    {
        var all = before.Concat(after).ToList();
        var w = new List<string>();
        if (all.Count == 0) return w;
        if (all.Select(r => r.Conditions.DurationSec).Distinct().Count() > 1) w.Add("Runs have different durations.");
        if (all.Select(r => r.Conditions.Endpoint).Distinct().Count() > 1) w.Add("Runs pinged different endpoints.");
        if (all.Select(r => r.Conditions.OnAcPower).Distinct().Count() > 1) w.Add("Some runs were on battery, some on mains power.");
        if (all.Select(r => r.Conditions.FortniteRunning).Distinct().Count() > 1) w.Add("Fortnite was running during some runs but not others.");
        var plans = all.Select(r => r.Conditions.PowerPlan).Distinct().ToList();
        if (plans.Count > 1) w.Add($"Power plan changed between runs ({string.Join(" / ", plans)}). Expected if the tweak changes the plan.");
        var busy = all.Where(r => r.Conditions.IdleCpuBefore > 20 && !r.Conditions.FortniteRunning).ToList();
        if (busy.Count > 0) w.Add($"{busy.Count} run(s) started with >20% CPU already in use: background activity may skew results.");
        var procs = all.Select(r => r.Conditions.ProcessCount).ToList();
        if (procs.Max() - procs.Min() > Math.Max(15, procs.Min() / 5)) w.Add($"Process count varied a lot ({procs.Min()}–{procs.Max()}): other apps were opened or closed between runs.");
        if (all.Any(r => r.Frames is not null) && all.Any(r => r.Frames is null)) w.Add("Only some runs have FPS data.");
        return w;
    }

    public static string Fmt(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 10 ? "0.0" : "0.##", CultureInfo.CurrentCulture);

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = p / 100 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

/// <summary>Reads frame times from PresentMon (1.x and 2.x) or CapFrameX CSV exports.</summary>
public static class FrameTimeCsv
{
    private static readonly string[] Columns = ["MsBetweenPresents", "msBetweenPresents", "FrameTime", "MsBetweenDisplayChange"];

    /// <summary>
    /// Parses a CSV and computes FPS stats. When several processes are present, uses <paramref name="preferProcess"/>
    /// if found, otherwise the one with the most frames. 1% low = 1000 / 99th-percentile frame time.
    /// </summary>
    public static (FrameStats stats, string process) Parse(string path, string? preferProcess = "FortniteClient-Win64-Shipping.exe")
    {
        using var reader = new StreamReader(path);
        string? header;
        do { header = reader.ReadLine(); } while (header is not null && (header.StartsWith("//") || header.Trim().Length == 0));
        if (header is null) throw new InvalidDataException("The file is empty.");
        var cols = SplitCsv(header);
        int ft = -1;
        foreach (var c in Columns) { ft = cols.FindIndex(x => x.Trim().Equals(c, StringComparison.OrdinalIgnoreCase)); if (ft >= 0) break; }
        if (ft < 0) throw new InvalidDataException("No frame-time column found. Expected a PresentMon or CapFrameX CSV (MsBetweenPresents / FrameTime).");
        int app = cols.FindIndex(x => x.Trim().Equals("Application", StringComparison.OrdinalIgnoreCase) || x.Trim().Equals("ProcessName", StringComparison.OrdinalIgnoreCase));
        var byApp = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            if (ft >= f.Count) continue;
            if (!double.TryParse(f[ft], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 5000) continue;
            var name = app >= 0 && app < f.Count ? f[app] : "";
            if (!byApp.TryGetValue(name, out var list)) byApp[name] = list = [];
            list.Add(ms);
        }
        if (byApp.Count == 0) throw new InvalidDataException("No valid frame times found.");
        var pick = preferProcess is not null && byApp.ContainsKey(preferProcess) ? preferProcess : byApp.MaxBy(kv => kv.Value.Count).Key;
        return (FromFrameTimes(byApp[pick], $"Frame-time CSV ({Path.GetFileName(path)})"), pick);
    }

    public static FrameStats FromFrameTimes(IReadOnlyList<double> ms, string source)
    {
        if (ms.Count < 10) throw new InvalidDataException($"Only {ms.Count} frames: record at least a few seconds.");
        var sorted = ms.OrderBy(x => x).ToList();
        var avg = ms.Average();
        var p99 = BenchmarkStats.Percentile(sorted, 99);
        var p999 = BenchmarkStats.Percentile(sorted, 99.9);
        return new FrameStats
        {
            Source = source, Frames = ms.Count, FrameTimeAvgMs = Math.Round(avg, 3), FrameTimeP99Ms = Math.Round(p99, 3),
            AvgFps = Math.Round(1000 / avg, 1), Low1Fps = Math.Round(1000 / p99, 1), Low01Fps = ms.Count >= 1000 ? Math.Round(1000 / p999, 1) : null,
        };
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }
}
