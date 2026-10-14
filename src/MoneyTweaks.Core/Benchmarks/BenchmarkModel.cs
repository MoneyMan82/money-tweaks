using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Benchmarks;

/// <summary>Everything that could make two runs differ for reasons other than the tweak.</summary>
public sealed class TestConditions
{
    public int DurationSec { get; set; }
    public string? Endpoint { get; set; }
    public string PowerPlan { get; set; } = "";
    public bool? OnAcPower { get; set; }
    public int ProcessCount { get; set; }
    public double IdleCpuBefore { get; set; }
    public string? Foreground { get; set; }
    public bool FortniteRunning { get; set; }
    public int OsBuild { get; set; }
    public string AppVersion { get; set; } = "";
    /// <summary>User's description of the scene/match (e.g. "Creative map X, same spot, 60 s").</summary>
    public string Scene { get; set; } = "";
}

/// <summary>Frame-time data from a PresentMon/CapFrameX CSV, or numbers typed in by the user.</summary>
public sealed class FrameStats
{
    public string Source { get; set; } = "";
    public double AvgFps { get; set; }
    public double? Low1Fps { get; set; }
    public double? Low01Fps { get; set; }
    public double? FrameTimeAvgMs { get; set; }
    public double? FrameTimeP99Ms { get; set; }
    public int Frames { get; set; }
}

public sealed class BenchmarkRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StudyId { get; set; } = "";
    /// <summary>"Baseline" or "After".</summary>
    public string Phase { get; set; } = "Baseline";
    public DateTime At { get; set; } = DateTime.Now;
    public TestConditions Conditions { get; set; } = new();

    // System (Windows performance counters, measured by Money Tweaks).
    public double? CpuAvg { get; set; }
    public double? CpuP95 { get; set; }
    public double? GpuAvg { get; set; }
    public double? RamUsedGb { get; set; }
    public double? CpuTempC { get; set; }
    public double? GpuTempC { get; set; }
    // Generic endpoint latency (ICMP), NOT the Fortnite server ping.
    public double? PingAvgMs { get; set; }
    public double? PingMinMs { get; set; }
    public double? PingMaxMs { get; set; }
    public double? JitterMs { get; set; }
    public double? LossPct { get; set; }
    public int PingSent { get; set; }
    // Game-side numbers: from a frame-time CSV or entered by hand.
    public FrameStats? Frames { get; set; }
    /// <summary>Ping shown by Fortnite itself (Net Debug Stats), typed in by the user.</summary>
    public double? GamePingMs { get; set; }

    public List<float> CpuSeries { get; set; } = [];
    public List<float> GpuSeries { get; set; } = [];
    public List<float> PingSeries { get; set; } = []; // -1 = lost
}

public sealed class BenchmarkStudy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"tweak", "preset" or "none".</summary>
    public string TargetKind { get; set; } = "none";
    public string? TargetId { get; set; }
    public string TargetName { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? AppliedAt { get; set; }
    public DateTime? RolledBackAt { get; set; }
    /// <summary>Tweak ids this study's Apply actually changed (so rollback touches nothing else).</summary>
    public List<string> AppliedIds { get; set; } = [];
    public string Notes { get; set; } = "";
    // Test conditions kept identical for every run of the study.
    public int DurationSec { get; set; } = 30;
    public int RunsPerPhase { get; set; } = 3;
    public string Endpoint { get; set; } = "1.1.1.1";
    public string Scene { get; set; } = "";
}

public sealed class BenchmarkFile
{
    public int Version { get; set; } = 1;
    public List<BenchmarkStudy> Studies { get; set; } = [];
    public List<BenchmarkRun> Runs { get; set; } = [];
}

/// <summary>Benchmark history in %AppData%\MoneyTweaks\benchmarks.json (local only).</summary>
public static class BenchmarkStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string FilePath => Path.Combine(AppPaths.Root, "benchmarks.json");

    public static BenchmarkFile Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<BenchmarkFile>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Error("benchmarks.json is unreadable; starting a new history (old file kept as .bad)", ex); try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new();
    }

    public static void Save(BenchmarkFile f)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
        File.Move(tmp, FilePath, true);
    }

    public static void ExportJson(BenchmarkFile f, IEnumerable<BenchmarkStudy> studies, string path)
    {
        var ids = studies.Select(s => s.Id).ToHashSet();
        var sub = new BenchmarkFile { Studies = f.Studies.Where(s => ids.Contains(s.Id)).ToList(), Runs = f.Runs.Where(r => ids.Contains(r.StudyId)).ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(new { format = "money-tweaks-benchmarks", sub.Version, sub.Studies, sub.Runs }, Json));
    }

    public static void ExportCsv(BenchmarkFile f, IEnumerable<BenchmarkStudy> studies, string path)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double? v) => v?.ToString("0.###", inv) ?? "";
        string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("study,target,phase,timestamp,duration_s,scene,power_plan,on_ac,processes,idle_cpu_before,fortnite_running,endpoint," +
            "cpu_avg,cpu_p95,gpu_avg,ram_used_gb,cpu_temp_c,gpu_temp_c,ping_avg_ms,ping_min_ms,ping_max_ms,jitter_ms,loss_pct,pings," +
            "frame_source,avg_fps,low1_fps,low01_fps,frametime_avg_ms,frametime_p99_ms,frames,game_ping_ms\n");
        foreach (var s in studies)
            foreach (var r in f.Runs.Where(r => r.StudyId == s.Id).OrderBy(r => r.At))
            {
                var c = r.Conditions;
                sb.AppendJoin(',', Q(s.Name), Q(s.TargetName), r.Phase, r.At.ToString("s", inv), c.DurationSec, Q(c.Scene), Q(c.PowerPlan), c.OnAcPower?.ToString() ?? "",
                    c.ProcessCount, N(c.IdleCpuBefore), c.FortniteRunning, Q(c.Endpoint),
                    N(r.CpuAvg), N(r.CpuP95), N(r.GpuAvg), N(r.RamUsedGb), N(r.CpuTempC), N(r.GpuTempC), N(r.PingAvgMs), N(r.PingMinMs), N(r.PingMaxMs), N(r.JitterMs), N(r.LossPct), r.PingSent,
                    Q(r.Frames?.Source), N(r.Frames?.AvgFps), N(r.Frames?.Low1Fps), N(r.Frames?.Low01Fps), N(r.Frames?.FrameTimeAvgMs), N(r.Frames?.FrameTimeP99Ms), r.Frames?.Frames.ToString(inv) ?? "", N(r.GamePingMs));
                sb.Append('\n');
            }
        File.WriteAllText(path, sb.ToString());
    }
}
