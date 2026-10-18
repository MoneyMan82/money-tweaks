namespace MoneyTweaks.Core.Tuning;

/// <summary>
/// A GPU fan curve (temperature °C → fan %) that Money Tweaks drives through MSI Afterburner's control memory while it
/// is open. Safety rules: points rise with temperature, never go below the fan's minimum, and must reach 100% by 85 °C;
/// if the temperature can't be read, or Money Tweaks closes, the fan goes back to automatic control.
/// </summary>
public static class FanCurve
{
    public const int MustBeFullAtC = 85;

    public static readonly IReadOnlyList<int[]> Default = [[40, 30], [60, 45], [70, 60], [78, 80], [85, 100]];

    public static List<string> Validate(IReadOnlyList<int[]> points, AbRange? fan)
    {
        var e = new List<string>();
        if (points.Count < 2) { e.Add("A fan curve needs at least two points."); return e; }
        if (points.Any(p => p.Length != 2)) { e.Add("Each point needs a temperature and a fan speed."); return e; }
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i][0] <= points[i - 1][0]) e.Add("Temperatures must go up from point to point.");
            if (points[i][1] < points[i - 1][1]) e.Add("Fan speed may not go down as the temperature rises.");
        }
        int min = fan?.Min ?? 0;
        if (points.Any(p => p[1] < min || p[1] > 100)) e.Add($"Fan speeds must be between {min}% (this fan's minimum) and 100%.");
        if (points.Any(p => p[0] is < 20 or > 100)) e.Add("Temperatures must be between 20 and 100 °C.");
        if (!points.Any(p => p[0] <= MustBeFullAtC && p[1] >= 100)) e.Add($"For safety the curve must reach 100% at or below {MustBeFullAtC} °C.");
        return e.Distinct().ToList();
    }

    /// <summary>Fan % for a temperature: linear between points, flat beyond the ends.</summary>
    public static int Evaluate(IReadOnlyList<int[]> points, double tempC)
    {
        if (tempC <= points[0][0]) return points[0][1];
        for (int i = 1; i < points.Count; i++)
            if (tempC <= points[i][0])
            {
                var (t0, f0, t1, f1) = (points[i - 1][0], points[i - 1][1], points[i][0], points[i][1]);
                return (int)Math.Ceiling(f0 + (f1 - f0) * (tempC - t0) / (t1 - t0) - 1e-9); // round up: a fan should err on the cool side
            }
        return points[^1][1];
    }
}
