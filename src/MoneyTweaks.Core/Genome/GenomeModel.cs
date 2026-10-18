using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Genome;

/// <summary>Ratio metrics are compared as ln(value / baseline) (FPS, frame time, power); delta metrics as value − baseline
/// (temperatures in °C, frame-time variability in percentage points).</summary>
public enum MetricKind { Ratio, Delta }

/// <summary>A measured quantity. <see cref="PointsPerUnit"/> converts a change into goal points: ratio metrics per 1%,
/// delta metrics per unit (so 1 °C cooler = 2 points, 1% more FPS = 1 point). The user's goal weights then mix them.</summary>
public sealed record GenomeMetric(string Key, string Label, string Unit, MetricKind Kind, bool HigherIsBetter, double PointsPerUnit, string Source, bool FrameBased)
{
    /// <summary>Goal points for a normalized change y (ln ratio or delta): positive = better.</summary>
    public double Points(double y) => (Kind == MetricKind.Ratio ? (Math.Exp(y) - 1) * 100 : y) * PointsPerUnit * (HigherIsBetter ? 1 : -1);

    /// <summary>Human change: "+3.1%" or "−2.0 °C".</summary>
    public string Change(double y) => Kind == MetricKind.Ratio ? $"{(Math.Exp(y) - 1) * 100:+0.0;−0.0;0.0}%" : $"{y:+0.0;−0.0;0.0} {Unit}";
}

public static class GenomeMetrics
{
    public static readonly IReadOnlyList<GenomeMetric> All =
    [
        new("fps", "Average FPS", "fps", MetricKind.Ratio, true, 1.0, "frame times (Present events or the 3D scene)", true),
        new("low1", "1% low FPS", "fps", MetricKind.Ratio, true, 1.0, "frame times", true),
        new("p99", "99th percentile frame time", "ms", MetricKind.Ratio, false, 1.0, "frame times; a render-latency proxy, not end-to-end input latency", true),
        new("cv", "Frame-time variability", "pp", MetricKind.Delta, false, 4.0, "standard deviation ÷ mean of frame times (consistency)", true),
        new("gputemp", "GPU temperature (max)", "°C", MetricKind.Delta, false, 2.0, "nvidia-smi", false),
        new("cputemp", "CPU temperature (max)", "°C", MetricKind.Delta, false, 2.0, "MSI Afterburner or HWiNFO (when running)", false),
        new("gpupower", "GPU power (average)", "W", MetricKind.Ratio, false, 0.5, "nvidia-smi", false),
        new("cpupower", "CPU package power (average)", "W", MetricKind.Ratio, false, 0.5, "HWiNFO (when running)", false),
    ];

    public static GenomeMetric Get(string key) => All.First(m => m.Key == key);
}

public enum FactorKind { CpuBoostMode, CpuEpp, CpuMaxState, GpuPowerLimit, GpuClockCap, Tweak, Manual }

/// <summary>One setting value. <see cref="Value"/>: boost-mode index, %, W, MHz (0 = no cap) or 1/0 for tweaks.</summary>
public sealed class FactorLevel
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public double Value { get; set; }
}

/// <summary>A setting dimension the engine may vary. Only settings changed through documented interfaces are factors;
/// manual factors are game settings the user changes in the game when asked (game workloads only).</summary>
public sealed class GenomeFactor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FactorKind Kind { get; set; }
    public string? TweakId { get; set; }
    public int GpuIndex { get; set; }
    public List<FactorLevel> Levels { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public bool NeedsAdmin { get; set; }
    /// <summary>Which documented interface changes it.</summary>
    public string Interface { get; set; } = "";
    public bool IsManual => Kind == FactorKind.Manual;

    public FactorLevel? Level(string id) => Levels.FirstOrDefault(l => l.Id == id);
    public string LabelOf(string levelId) => Level(levelId)?.Label ?? levelId;
}

/// <summary>What the results depend on. A change in driver, Windows build, BIOS or game version makes earlier results "stale".</summary>
public sealed class Fingerprint
{
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string GpuDriver { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Workload { get; set; } = "";

    /// <summary>What changed from <paramref name="old"/> to this one (empty = same).</summary>
    public List<string> ChangesSince(Fingerprint old)
    {
        var c = new List<string>();
        void D(string what, string a, string b) { if (a.Length > 0 && b.Length > 0 && a != b) c.Add($"{what} {a} → {b}"); }
        D("GPU driver", old.GpuDriver, GpuDriver);
        D("Windows build", old.OsBuild, OsBuild);
        D("BIOS", old.Bios, Bios);
        D("game version", old.Workload, Workload);
        if (old.Gpu.Length > 0 && Gpu.Length > 0 && old.Gpu != Gpu) c.Add($"GPU {old.Gpu} → {Gpu}");
        if (old.Cpu.Length > 0 && Cpu.Length > 0 && old.Cpu != Cpu) c.Add($"CPU {old.Cpu} → {Cpu}");
        return c;
    }

    public bool SameAs(Fingerprint other) => ChangesSince(other).Count == 0;
}

/// <summary>Raw measured values of one trial, keyed by <see cref="GenomeMetric.Key"/>.</summary>
public sealed class Measurement
{
    public Dictionary<string, double> Values { get; set; } = [];
    public int Frames { get; set; }
    public double RefreshHz { get; set; }
    /// <summary>FPS hit the display refresh rate: frame metrics don't show the hardware's limit and are left out.</summary>
    public bool FpsCapped { get; set; }
    public double? MaxGpuMhz { get; set; }
    public double? Get(string key) => Values.TryGetValue(key, out var v) && !double.IsNaN(v) ? v : null;
}

public enum TrialStatus { Ok, Failed, Invalid }

public sealed class Trial
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public string WorkloadId { get; set; } = "";
    /// <summary>Every known factor's level during the trial (factor id → level id).</summary>
    public Dictionary<string, string> Config { get; set; } = [];
    public bool IsBaseline { get; set; }
    /// <summary>Why the planner chose it ("screening", "interaction", "repeat", "explore", "baseline", "re-check").</summary>
    public string Reason { get; set; } = "";
    public Measurement? Measured { get; set; }
    public TrialStatus Status { get; set; }
    public string? Problem { get; set; }
    public Fingerprint Fingerprint { get; set; } = new();
    public double IdleCpuBefore { get; set; }
    public int ProcessCount { get; set; }
    public bool? OnAc { get; set; }
}

/// <summary>A setting level (or combination) that failed: apply refused, instability, or a hard temperature limit. The
/// planner never proposes it again for that workload until the user clears it.</summary>
public sealed class FailedSetting
{
    public string WorkloadId { get; set; } = "";
    /// <summary>factor id → level id: one entry = that level failed; several = that combination failed.</summary>
    public Dictionary<string, string> Levels { get; set; } = [];
    public string Reason { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
}

public sealed class GenomeSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime? Ended { get; set; }
    public string WorkloadId { get; set; } = "";
    /// <summary>The user's settings when the session started (the baseline and what is restored at the end).</summary>
    public Dictionary<string, string> Original { get; set; } = [];
    public string Outcome { get; set; } = "";
}

/// <summary>The repeatable workload a trial runs. Synthetic = the built-in 3D scene at a calibrated intensity; game = a
/// controlled manual comparison in a real game (same scene every capture), measured from its Present events.</summary>
public sealed class GenomeWorkload
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "synthetic"; // "synthetic" | "game"
    public string Name { get; set; } = "";
    public int Intensity { get; set; } = 3;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int DurationSec { get; set; } = 20;
    public string? GameExe { get; set; }
    public string Scene { get; set; } = "";
    public bool IsGame => Kind == "game";
}

public sealed class GoalSettings
{
    public string Preset { get; set; } = "competitive";
    public Dictionary<string, double> Weights { get; set; } = [];
    /// <summary>Hard limits: a candidate breaking one is never recommended or tested on purpose.</summary>
    public double? MaxGpuTempC { get; set; }
    public double? MaxCpuTempC { get; set; }
    /// <summary>Largest acceptable FPS loss vs. your current settings, in % (e.g. 3 = at most 3% lower).</summary>
    public double? MaxFpsLossPct { get; set; }
    public double? MaxLow1LossPct { get; set; }
    public double? MaxGpuPowerW { get; set; }
}

public sealed class AppliedGenomeProfile
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Config { get; set; } = [];
    public DateTime At { get; set; } = DateTime.Now;
    public Fingerprint Fingerprint { get; set; } = new();
    /// <summary>Tweak states before the profile was applied (tweak id → was on), for an exact rollback.</summary>
    public Dictionary<string, bool> PreviousTweaks { get; set; } = [];
    public string WorkloadId { get; set; } = "";
}

/// <summary>Everything Performance Genome knows about this PC. genome.json, written atomically after every trial.</summary>
public sealed class GenomeStore
{
    public int Version { get; set; } = 1;
    public List<GenomeFactor> Factors { get; set; } = [];
    public List<GenomeWorkload> Workloads { get; set; } = [];
    public string? ActiveWorkloadId { get; set; }
    public List<GenomeSession> Sessions { get; set; } = [];
    public List<Trial> Trials { get; set; } = [];
    public List<FailedSetting> Failures { get; set; } = [];
    public GoalSettings Goal { get; set; } = new();
    public AppliedGenomeProfile? Applied { get; set; }
    /// <summary>Fingerprint at the last visit, to notice driver/Windows/game updates.</summary>
    public Fingerprint? LastSeen { get; set; }
    /// <summary>Notable events: updates detected, failures learned, profiles applied/rolled back.</summary>
    public List<string> Journal { get; set; } = [];

    [JsonIgnore] public GenomeWorkload? ActiveWorkload => Workloads.FirstOrDefault(w => w.Id == ActiveWorkloadId) ?? Workloads.FirstOrDefault();
    public GenomeFactor? Factor(string id) => Factors.FirstOrDefault(f => f.Id == id);

    public void Note(string text)
    {
        Journal.Add($"{DateTime.Now:yyyy-MM-dd HH:mm} · {text}");
        if (Journal.Count > 400) Journal.RemoveRange(0, Journal.Count - 400);
    }

    public static string PathFor => Path.Combine(AppPaths.Root, "genome.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static GenomeStore Load(string? path = null)
    {
        path ??= PathFor;
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<GenomeStore>(File.ReadAllText(path), Json) ?? new(); }
        catch (Exception ex)
        {
            Log.Warn("genome.json unreadable (kept as .bad): " + ex.Message);
            try { File.Copy(path, path + ".bad", true); } catch { }
        }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= PathFor;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static string Key(IReadOnlyDictionary<string, string> config, IEnumerable<string>? factorIds = null) =>
        string.Join(";", (factorIds ?? config.Keys).OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={(config.TryGetValue(k, out var v) ? v : "?")}"));
}
