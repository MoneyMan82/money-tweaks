using MoneyTweaks.Core.Advisor;
using MoneyTweaks.Core.Cleaner;
using MoneyTweaks.Core.Tuning;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Read-only checks against this PC's real Windows data (nothing is changed).</summary>
public class AdvisorWindowsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Hardware_facts_come_from_windows_and_unknowns_are_listed()
    {
        var inv = TuningDetector.Cached() ?? await TuningDetector.ScanAsync();
        var hw = HardwareFactsDetector.Detect(inv, Path.GetTempPath());
        output.WriteLine($"{hw.Cpu} socket={hw.Socket} | {hw.Gpu} VRAM={hw.VramGb:0.0} | RAM {hw.RamGb} GB {hw.RamType} x{hw.RamModules} slots={hw.RamSlots} dual={hw.DualChannel} {hw.RamConfiguredMts}/{hw.RamRatedMts}");
        foreach (var s in hw.Storage) output.WriteLine($"  disk {s.Name} {s.Media} {s.Bus} {s.SizeGb:0} GB [{string.Join(",", s.DriveLetters)}]");
        output.WriteLine("  missing: " + string.Join("; ", hw.Missing));
        Assert.False(string.IsNullOrEmpty(hw.Cpu));
        Assert.Contains(hw.Missing, m => m.Contains("Power supply"));   // never guessed
        Assert.NotEmpty(hw.Storage);
    }

    [Fact]
    public async Task Telemetry_probe_reads_real_counters()
    {
        using var probe = new TelemetryProbe(Environment.ProcessId);
        await Task.Delay(1000);
        var s = probe.Sample(0);
        output.WriteLine($"cpu {s.CpuTotal:0}% busiest {s.CpuBusiestCore:0}% perf {s.CpuPerfPct:0}% gpu {s.GpuUtil} vram {s.VramUsedMb:0}/{s.VramTotalMb:0} ram avail {s.RamAvailableMb:0} commit {s.CommitPct:0}% faults {s.HardFaultsPerSec:0} disk {s.DiskLatencyMs:0.0} ms game {s.GameCpuPct:0.0}%");
        Assert.InRange(s.CpuTotal, 0, 100.5);
        Assert.InRange(s.CpuBusiestCore, 0, 100.5);
        Assert.True(s.RamAvailableMb > 0 && s.RamTotalMb > s.RamAvailableMb);
        Assert.NotNull(s.GameCpuPct);
    }

    [Fact]
    public void Standby_memory_is_measured_from_windows()
    {
        var s = StandbyMemory.Snapshot();
        Assert.NotNull(s);
        output.WriteLine($"standby {StorageCleaner.Size(s!.StandbyBytes)} free {StorageCleaner.Size(s.FreeBytes)} available {StorageCleaner.Size(s.AvailableBytes)}");
        Assert.True(s.StandbyBytes >= 0 && s.TotalBytes > 0 && s.AvailableBytes <= s.TotalBytes);
    }
}
