using System.Globalization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>One Windows processor power setting on the active plan (documented by Microsoft, set with powercfg).</summary>
public sealed record CpuPowerSetting(string Alias, string Label, string Tooltip, int Min, int Max, int Step, IReadOnlyList<string>? Choices, int? Ac, int? Dc)
{
    public bool Readable => Ac is not null;
    public string Describe(int v) => Choices is { } c && v >= 0 && v < c.Count ? c[v] : $"{v}%";
}

/// <summary>
/// CPU tuning that Windows itself supports on every Intel and AMD CPU: processor performance boost mode, maximum
/// processor state and energy performance preference (EPP), on the active power plan, through powercfg (Microsoft's
/// documented interface). These don't overclock: they let you trade boost for temperature, noise and power. Values are
/// read with /qh because Windows hides boost mode and EPP from the normal listing.
/// </summary>
public static class CpuPowerSettings
{
    public const string BoostMode = "PERFBOOSTMODE", MaxState = "PROCTHROTTLEMAX", Epp = "PERFEPP";

    // Microsoft's documented boost-mode values (Processor performance boost mode).
    public static readonly IReadOnlyList<string> BoostChoices =
        ["Disabled", "Enabled", "Aggressive", "Efficient Enabled", "Efficient Aggressive", "Aggressive At Guaranteed", "Efficient Aggressive At Guaranteed"];

    private static string PowerCfg => Path.Combine(ProcessRunner.System32, "powercfg.exe");

    public static CpuPowerSetting Read(string alias)
    {
        var r = ProcessRunner.Run(PowerCfg, $"/qh scheme_current sub_processor {alias}", 15_000);
        var (min, max, step, ac, dc, choices) = Parse(r.Ok ? r.StdOut : "");
        return alias switch
        {
            BoostMode => new(alias, "Boost mode", "How eagerly the CPU boosts above base clock. Disabled = base clock only (cool and quiet, much slower). Efficient modes boost less eagerly.",
                0, Math.Max(0, (choices > 0 ? choices : BoostChoices.Count) - 1), 1, BoostChoices.Take(choices > 0 ? choices : BoostChoices.Count).ToList(), ac, dc),
            MaxState => new(alias, "Maximum processor state", "Upper limit for CPU speed as a share of its maximum. On many CPUs 99% already turns off turbo boost.",
                min ?? 0, max ?? 100, step ?? 1, null, ac, dc),
            _ => new(alias, "Energy performance preference", "0% = maximum performance, 100% = maximum power saving. A hint to the CPU's own power management (EPP).",
                min ?? 0, max ?? 100, step ?? 1, null, ac, dc),
        };
    }

    /// <summary>Hex values in the output: for range settings min, max, step, AC, DC; for choice settings just AC and DC (labels are localized, numbers are not).</summary>
    public static (int? min, int? max, int? step, int? ac, int? dc, int choices) Parse(string text)
    {
        var hex = Regex.Matches(text, @":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToList();
        int choices = Regex.Matches(text, @":\s*(\d{3})\s*$", RegexOptions.Multiline).Count;
        if (hex.Count >= 5) return (hex[^5], hex[^4], hex[^3], hex[^2], hex[^1], choices);
        if (hex.Count >= 2) return (null, null, null, hex[^2], hex[^1], choices);
        return (null, null, null, null, null, choices);
    }

    /// <summary>Sets AC and DC values on the active plan and re-activates it. Read back afterwards with <see cref="Read"/>.</summary>
    public static (bool ok, string message) Set(string alias, int ac, int dc)
    {
        var a = ProcessRunner.Run(PowerCfg, $"/setacvalueindex scheme_current sub_processor {alias} {ac}");
        var d = ProcessRunner.Run(PowerCfg, $"/setdcvalueindex scheme_current sub_processor {alias} {dc}");
        var s = ProcessRunner.Run(PowerCfg, "/setactive scheme_current");
        Log.Info($"powercfg {alias} AC={ac} DC={dc} -> {a.ExitCode}/{d.ExitCode}/{s.ExitCode}");
        return a.Ok && d.Ok && s.Ok ? (true, "Accepted by Windows.") : (false, (a.Combined + " " + d.Combined).Trim());
    }
}
