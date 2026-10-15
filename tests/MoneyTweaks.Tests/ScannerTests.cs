using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

[Collection("registry")]
public class ScannerTests(ITestOutputHelper output)
{
    private static ScanFacts Healthy() => new()
    {
        PowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", PowerPlanName = "High performance", OnAc = true, Cpu = "CPU", Cores = 8, Threads = 16,
        Gpus = "NVIDIA GeForce RTX 4070", RamGb = 32, RamRatedMts = 6000, RamConfiguredMts = 6000, RamUsedPercent = 40, HagsOn = true, Build = 26100,
        Adapters = [new("Intel Ethernet", "Ethernet", 1_000_000_000)], SystemFreeGb = 300, SystemFreePercent = 40,
        States = OptimizationScanner.Candidates.ToDictionary(id => id, _ => TweakState.Applied),
    };

    [Fact]
    public void Healthy_pc_has_no_findings()
    {
        Assert.Empty(OptimizationScanner.Analyze(Healthy()));
    }

    [Fact]
    public void Real_problems_are_detected_with_evidence_and_fixes()
    {
        var f = Healthy();
        f.RamConfiguredMts = 4800;
        f.PowerPlanGuid = OptimizationScanner.Balanced; f.PowerPlanName = "Balanced";
        f.States["gen.ultimate-plan"] = TweakState.NotApplied;
        f.States["gen.game-mode"] = TweakState.NotApplied;
        f.RamUsedPercent = 93;
        f.TopMemory = [("chrome", 6000)];
        f.Adapters = [new("Wi-Fi 6", "Wi-Fi", 866_000_000)];
        f.SystemFreeGb = 8; f.SystemFreePercent = 3;
        var list = OptimizationScanner.Analyze(f);
        foreach (var x in list) output.WriteLine($"{x.Severity} {x.Id}: {string.Join(" | ", x.Evidence)}");
        Assert.Equal(Severity.High, list[0].Severity);
        var xmp = list.Single(x => x.Id == "ram-xmp");
        Assert.Contains("4800", xmp.Evidence[0]);
        Assert.Empty(xmp.FixTweakIds);             // firmware: manual steps only
        Assert.Equal(["gen.ultimate-plan"], list.Single(x => x.Id == "power-balanced").FixTweakIds);
        Assert.Equal(["gen.game-mode"], list.Single(x => x.Id == "game-mode").FixTweakIds);
        Assert.Contains(list, x => x.Id == "ram-pressure" && x.Severity == Severity.High && x.Evidence.Any(e => e.Contains("chrome")));
        Assert.Contains(list, x => x.Id == "wifi-only" && x.FixTweakIds.Count == 0);
        Assert.Contains(list, x => x.Id == "disk-space" && x.Severity == Severity.High);
        Assert.All(list, x => { Assert.NotEmpty(x.Evidence); Assert.False(string.IsNullOrWhiteSpace(x.Benefit)); });
    }

    [Fact]
    public void Laptops_and_already_applied_settings_are_not_nagged()
    {
        var f = Healthy();
        f.HasBattery = true; f.OnAc = true;
        f.PowerPlanGuid = OptimizationScanner.Balanced;
        f.States["gen.ultimate-plan"] = TweakState.NotApplied;
        Assert.DoesNotContain(OptimizationScanner.Analyze(f), x => x.Id == "power-balanced"); // laptops keep Balanced
    }

    [Fact]
    public void Fixes_never_include_services_or_disabled_security_tweaks()
    {
        Assert.False(OptimizationScanner.Fixable("adv.vbs-hvci"));
        Assert.False(OptimizationScanner.Fixable("svc.diagtrack"));
        Assert.All(OptimizationScanner.Candidates, id => Assert.True(OptimizationScanner.Fixable(id), id));
        Assert.All(OptimizationScanner.Candidates, id => Assert.NotEqual(TweakCategory.Services, TweakCatalog.Find(id)!.Category));
    }

    [Fact]
    public void Real_collection_is_read_only_and_produces_findings()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mt-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        AppPaths.OverrideRoot(tmp);
        try
        {
            var f = OptimizationScanner.Collect();
            Assert.True(f.RamGb > 0);
            Assert.True(f.Threads > 0);
            Assert.NotEmpty(f.States);
            var list = OptimizationScanner.Analyze(f);
            output.WriteLine($"{f.Cpu} | {f.Gpus} | {f.RamGb} GB {f.RamUsedPercent}% | plan {f.PowerPlanName} | battery {f.HasBattery} | adapters {string.Join(",", f.Adapters.Select(a => a.Type))}");
            foreach (var x in list) output.WriteLine($"{x.Severity} {x.Id} [{x.Confidence}] fix: {string.Join(",", x.FixTweakIds)}");
            Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "backups"))); // nothing applied
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
