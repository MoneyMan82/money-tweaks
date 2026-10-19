using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Advisor;

/// <summary>What the user wants: game, resolution, settings, FPS now/target and an optional budget (euros).</summary>
public sealed class AdvisorGoal
{
    public string Game { get; set; } = "";
    public string? GameKey { get; set; }
    public string? GameExe { get; set; }
    public string? GameInstallDir { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string Settings { get; set; } = "High";
    /// <summary>What the user sees now (used when frame timing can't be measured).</summary>
    public double? CurrentFps { get; set; }
    public double TargetFps { get; set; } = 144;
    public double? BudgetEur { get; set; }
    public double Pixels => (double)Width * Height;
}

public sealed record StorageDevice(string Name, string Media, string Bus, double SizeGb, IReadOnlyList<string> DriveLetters);

/// <summary>This PC, from existing detection plus a few extra Windows queries. Unknown stays unknown (listed in Missing).</summary>
public sealed class HardwareFacts
{
    public string Cpu { get; init; } = "";
    public string CpuVendor { get; init; } = "";
    public int Cores { get; init; }
    public int Threads { get; init; }
    public string? Socket { get; init; }
    public string? Platform { get; init; }
    public string Motherboard { get; init; } = "";
    public string Bios { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string GpuVendor { get; init; } = "";
    public double? VramGb { get; init; }
    public double RamGb { get; init; }
    public string? RamType { get; init; }
    public int RamModules { get; init; }
    public int? RamSlots { get; init; }
    public int? RamRatedMts { get; init; }
    public int? RamConfiguredMts { get; init; }
    public bool? DualChannel { get; init; }
    public List<StorageDevice> Storage { get; init; } = [];
    public string? GameDrive { get; init; }
    public StorageDevice? GameDisk { get; init; }
    public bool? Laptop { get; init; }
    public double? GpuPowerLimitW { get; init; }
    public double? GpuMaxPowerLimitW { get; init; }
    public List<string> Missing { get; init; } = [];

    public bool XmpOff => RamRatedMts is { } r && RamConfiguredMts is { } c && c < r - 100;
    public bool HasSsd => Storage.Any(s => s.Media == "SSD");
}

/// <summary>One second of telemetry. Nulls mean Windows or the driver didn't report it.</summary>
public sealed record TelemetrySample(
    double CpuTotal, double CpuBusiestCore, double? CpuPerfPct, double? CpuTempC,
    double? GpuUtil, double? GpuClockMhz, double? GpuTempC, double? GpuPowerW, double? GpuPowerLimitW, bool GpuPowerCapped, bool GpuThermalLimited,
    double? VramUsedMb, double? VramTotalMb,
    double RamAvailableMb, double RamTotalMb, double? CommitPct, double? HardFaultsPerSec,
    double? DiskQueue, double? DiskLatencyMs,
    double? GameCpuPct, double? OtherCpuPct, int Frames);

/// <summary>A measurement run: per-second samples plus every frame time (ms) when frame timing was available.</summary>
public sealed class Capture
{
    public DateTime At { get; set; } = DateTime.Now;
    public string Source { get; set; } = "game"; // "game" | "benchmark"
    public string What { get; set; } = "";
    public int Seconds { get; set; }
    public double RefreshHz { get; set; }
    public List<TelemetrySample> Samples { get; set; } = [];
    public List<double> FrameMs { get; set; } = [];
    public string? FramesUnavailable { get; set; }
    public string? TopBackground { get; set; }
}

public enum Limiter { FrameCap, Gpu, GpuPower, GpuThermal, Cpu, CpuClocks, CpuThermal, Memory, Vram, Storage, Background, Uncertain }

/// <summary>One possible limiter with how strongly the measurements point at it and the evidence, in words.</summary>
public sealed record LimiterFactor(Limiter Kind, string Label, double Score, string Confidence, IReadOnlyList<string> Evidence);

public sealed record FrameStats(double AvgFps, double Low1Fps, double P99Ms, double MedianMs, double StutterPct, double FpsCv, int Frames);

/// <summary>A projected FPS range (never a measurement) with why it's this wide.</summary>
public sealed record Projection(double Low, double High, string Basis)
{
    public double Mid => (Low + High) / 2;
}

public sealed class Diagnosis
{
    public FrameStats? Frames { get; init; }
    public double? MeasuredFps { get; init; }
    public bool FpsMeasured { get; init; }
    public double? CapFps { get; init; }
    public double RefreshHz { get; init; }
    public List<LimiterFactor> Factors { get; init; } = [];
    public LimiterFactor Primary => Factors.FirstOrDefault() ?? new(Limiter.Uncertain, "Not enough evidence", 0, "Low", ["Measure while playing to get a diagnosis."]);
    public bool Mixed { get; init; }
    /// <summary>Roughly the most FPS the CPU side / GPU side could deliver here (from measured headroom).</summary>
    public Projection? CpuCeiling { get; init; }
    public Projection? GpuCeiling { get; init; }
    public Dictionary<string, double> Summary { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

public enum RecTier { Free, LowCost, Hardware }

public sealed record Recommendation(
    string Id, string Title, RecTier Tier, string Difficulty, double? CostEur, bool CostKnown, string CostText,
    IReadOnlyList<string> Evidence, string Benefit, Projection? Projected, string Confidence,
    IReadOnlyList<string> Requirements, IReadOnlyList<string> TradeOffs, string? LinkTag, string? LinkLabel, string? PriceQuery, double Value)
{
    public string WhyRanked { get; init; } = "";
}

public sealed record Scenario(string Label, double? Low, double? High, bool Measured, string Note);

/// <summary>A price the user found, with where and when (never fetched or invented by Money Tweaks).</summary>
public sealed class PriceEntry
{
    public double Eur { get; set; }
    public string Product { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
}

public sealed class AdvisorStore
{
    public AdvisorGoal Goal { get; set; } = new();
    public Dictionary<string, PriceEntry> Prices { get; set; } = [];
    public List<string> History { get; set; } = [];

    public static string PathFor => Path.Combine(AppPaths.Root, "advisor.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AdvisorStore Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<AdvisorStore>(File.ReadAllText(PathFor), Json) ?? new(); }
        catch (Exception ex) { Log.Warn("advisor.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save() => AtomicFile.WriteAllText(PathFor, JsonSerializer.Serialize(this, Json));
}
