using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Evidence;

/// <summary>One metric of a shared trial: only the comparison, never raw samples, timestamps or conditions.</summary>
public sealed record SharedMetric(string K, double Pct, string Verdict, int NBefore, int NAfter);

/// <summary>
/// What is sent when the user shares one trial with the Community Hub (Tweak Truth Index). Contains no account, name,
/// path, serial number, IP-derived data or timestamp: a random-looking key (a hash of the local study id, so the same
/// test isn't counted twice), the tweak, how it was tested, coarse hardware/Windows groups, and the measured changes.
/// </summary>
public sealed record EvidenceSubmission(
    int V, string Key, string Tweak, string Kind, string? Game, string Cpu, string Gpu, int RamGb, int OsBuild, string App,
    IReadOnlyList<SharedMetric> Metrics);

/// <summary>Community aggregate for one group of submissions (published only once enough exist).</summary>
public sealed class CommunityMetric
{
    public string K { get; set; } = "";
    public int N { get; set; }
    public double? MedianPct { get; set; }
    public double? BetterShare { get; set; }
    public double? WorseShare { get; set; }
}

public sealed class CommunityGroup
{
    /// <summary>"all", "gpu", "cpu", "os" or "game".</summary>
    public string Dimension { get; set; } = "";
    public string Value { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Submissions { get; set; }
    public List<CommunityMetric> Metrics { get; set; } = [];
}

public sealed class CommunityEvidence
{
    public string Tweak { get; set; } = "";
    public int MinSubmissions { get; set; }
    public List<CommunityGroup> Groups { get; set; } = [];
}

public static partial class EvidenceShare
{
    public const int Version = 1;
    public static readonly IReadOnlyDictionary<TrialKind, string> KindCodes = new Dictionary<TrialKind, string>
    {
        [TrialKind.SyntheticScene] = "synthetic", [TrialKind.RealityLab] = "lab", [TrialKind.OwnTest] = "own",
    };

    /// <summary>Stable but meaningless key for a local study (so re-sharing doesn't count twice; reveals nothing).</summary>
    public static string KeyFor(BenchmarkStudy s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("money-tweaks-evidence:" + s.Id)))[..32].ToLowerInvariant();

    [GeneratedRegex(@"\((R|TM|tm|r)\)|®|™")] private static partial Regex Marks();
    [GeneratedRegex(@"\s+\d+-Core Processor|\s+CPU\s*@.*$|\s+Processor$|\s+with Radeon.*$", RegexOptions.IgnoreCase)] private static partial Regex CpuNoise();
    [GeneratedRegex(@"[^A-Za-z0-9 .+\-]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex Spaces();

    /// <summary>A hardware model name reduced to letters, digits and a few separators (at most 48 characters).</summary>
    public static string CleanModel(string? name, bool cpu)
    {
        var s = Marks().Replace(name ?? "", "");
        if (cpu) s = CpuNoise().Replace(s, "");
        s = Spaces().Replace(Unsafe().Replace(s, " "), " ").Trim();
        if (s.Length > 48) s = s[..48].Trim();
        return s.Length == 0 ? "Unknown" : s;
    }

    private static readonly string[] VirtualGpu = ["virtual", "basic display", "basic render", "remote", "parsec", "meta ", "citrix", "vmware", "hyper-v", "indirect"];

    /// <summary>The graphics card that runs games: a discrete NVIDIA/AMD/Intel Arc card if there is one, else the first real adapter.</summary>
    public static string PickGpu(string? adapters)
    {
        var all = (adapters ?? "").Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => !VirtualGpu.Any(v => a.Contains(v, StringComparison.OrdinalIgnoreCase))).ToList();
        var discrete = all.FirstOrDefault(a => a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || a.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || a.Contains("Arc", StringComparison.OrdinalIgnoreCase));
        return CleanModel(discrete ?? all.FirstOrDefault(), cpu: false);
    }

    private static readonly int[] RamSizes = [4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256];

    /// <summary>Installed RAM rounded to a common size (31.2 GB → 32).</summary>
    public static int RamBucket(double gb) => RamSizes.MinBy(s => Math.Abs(s - gb));

    /// <summary>The submission for one valid trial, or null if it can't be shared (invalid, nothing measured).</summary>
    public static EvidenceSubmission? Build(Trial t, SystemSpecs specs, int osBuild, string appVersion)
    {
        if (!t.Valid) return null;
        var metrics = t.Metrics.Values.Where(c => c.Pct is not null && c.Verdict != Verdict.NotEnoughData)
            .Select(c => new SharedMetric(c.Metric.Key, Math.Round(Math.Clamp(c.Pct!.Value, -90, 300), 2),
                c.Verdict switch { Verdict.Better => "better", Verdict.Worse => "worse", _ => "same" }, c.Before!.N, c.After!.N))
            .ToList();
        if (metrics.Count == 0) return null;
        return new(Version, KeyFor(t.Study), t.TweakId, KindCodes[t.Kind], t.Game, CleanModel(specs.Cpu, cpu: true), PickGpu(specs.Gpus),
            RamBucket(specs.RamGb), osBuild, appVersion, metrics);
    }

    // ---- which trials were shared (local) ---------------------------------------------------------------------------

    public static string SharedFile => Path.Combine(AppPaths.Root, "evidence-shared.json");

    public static HashSet<string> LoadShared()
    {
        try { if (File.Exists(SharedFile)) return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(SharedFile)) ?? []; }
        catch (Exception ex) { Log.Warn("evidence-shared.json unreadable: " + ex.Message); }
        return [];
    }

    public static void MarkShared(IEnumerable<string> keys)
    {
        var set = LoadShared();
        set.UnionWith(keys);
        AtomicFile.WriteAllText(SharedFile, JsonSerializer.Serialize(set));
    }
}
