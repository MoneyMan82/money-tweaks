using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Scanner;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class HealthRuleTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0);
    private static DriverInfo D(string dev, string cls, string prov, string ver, string id, string inf = "oem1.inf", DateTime? date = null) =>
        new(dev, cls, prov, prov, ver, date ?? new DateTime(2026, 9, 1), id, inf);

    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.7680", "576.80")]
    [InlineData("1.2.3", null)]
    public void Nvidia_version_from_windows_version(string win, string? expected) => Assert.Equal(expected, HealthChecks.NvidiaVersion(win));

    [Fact]
    public void Basic_display_adapter_is_a_missing_gpu_driver()
    {
        var r = HealthChecks.AnalyzeDrivers([D("Microsoft Basic Display Adapter", "DISPLAY", "Microsoft", "10.0.26100.1", @"PCI\VEN_10DE&DEV_2786", "display.inf")], [], "AMD Ryzen 7 7800X3D", Now);
        var gpu = Assert.Single(r, x => x.Id.StartsWith("drv-gpu"));
        Assert.Equal(HealthStatus.Problem, gpu.Status);
        Assert.Contains(gpu.Links, l => l.Target.Contains("nvidia.com"));
    }

    [Fact]
    public void Nvidia_driver_passes_with_official_link_and_uncertainty_and_no_outdated_claim()
    {
        var r = HealthChecks.AnalyzeDrivers([D("NVIDIA GeForce RTX 4070", "DISPLAY", "NVIDIA", "32.0.15.6094", @"PCI\VEN_10DE&DEV_2786", date: new DateTime(2024, 9, 1))], [], "Intel Core i7", Now);
        var gpu = Assert.Single(r, x => x.Id.StartsWith("drv-gpu"));
        Assert.Equal(HealthStatus.Passed, gpu.Status); // age alone is never reported as "outdated"
        Assert.Contains("560.94", gpu.Summary);
        Assert.NotNull(gpu.Uncertainty);
        Assert.Contains(gpu.Links, l => l.Target == "https://www.nvidia.com/en-us/drivers/");
    }

    [Fact]
    public void Virtual_displays_are_not_listed_as_gpus()
    {
        var r = HealthChecks.AnalyzeDrivers([D("NVIDIA GeForce RTX 4070 SUPER", "DISPLAY", "NVIDIA", "32.0.16.1714", @"PCI\VEN_10DE"), D("Meta Virtual Monitor", "DISPLAY", "Meta Inc.", "5.3.57.114", @"ROOT\DISPLAY\0000")], [], "", Now);
        Assert.Single(r, x => x.Id.StartsWith("drv-gpu"));
        Assert.Contains(r.Single(x => x.Id == "drv-inventory").Details, d => d.Contains("Meta Virtual Monitor"));
    }

    [Fact]
    public void Device_errors_missing_driver_restart_and_disabled()
    {
        var gpu = D("NVIDIA GeForce RTX 4070", "DISPLAY", "NVIDIA", "32.0.15.6094", @"PCI\VEN_10DE");
        var missing = HealthChecks.AnalyzeDrivers([gpu], [new("PCI Device", "", 28, @"PCI\VEN_1022")], "", Now).Single(x => x.Id == "drv-errors");
        Assert.Equal(HealthStatus.Problem, missing.Status);
        Assert.Equal(Severity.High, missing.Severity);
        Assert.Equal(SafeRepairs.PnpRescan, missing.RepairId);
        var restart = HealthChecks.AnalyzeDrivers([gpu], [new("Wi-Fi", "Net", 14, @"PCI\X")], "", Now).Single(x => x.Id == "drv-errors");
        Assert.Equal(HealthStatus.Warning, restart.Status);
        var disabled = HealthChecks.AnalyzeDrivers([gpu], [new("Bluetooth", "Bluetooth", 22, @"USB\X"), new("Ghost", "", 45, @"USB\Y")], "", Now).Single(x => x.Id == "drv-errors");
        Assert.Equal(HealthStatus.Passed, disabled.Status);
        Assert.DoesNotContain(disabled.Details, d => d.Contains("Ghost"));
    }

    [Fact]
    public void Dual_ccd_x3d_without_vcache_driver_warns_single_ccd_does_not()
    {
        var gpu = D("NVIDIA GeForce RTX 4070", "DISPLAY", "NVIDIA", "32.0.15.6094", @"PCI\VEN_10DE");
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeDrivers([gpu], [], "AMD Ryzen 9 7950X3D 16-Core Processor", Now).Single(x => x.Id == "drv-chipset").Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeDrivers([gpu], [], "AMD Ryzen 7 7800X3D 8-Core Processor", Now).Single(x => x.Id == "drv-chipset").Status);
        var withDriver = HealthChecks.AnalyzeDrivers([gpu, D("AMD 3D V-Cache Performance Optimizer", "SYSTEM", "Advanced Micro Devices", "1.0.0.10", @"ACPI\AMDI0101")], [], "AMD Ryzen 9 7950X3D", Now);
        Assert.Equal(HealthStatus.Passed, withDriver.Single(x => x.Id == "drv-chipset").Status);
    }

    [Fact]
    public void Windows_support_dates()
    {
        Assert.Equal(HealthStatus.Warning, HealthChecks.WindowsSupport(19045, "22H2", "Professional", Now).Item1);
        Assert.Equal(HealthStatus.Warning, HealthChecks.WindowsSupport(26100, "24H2", "Professional", Now).Item1); // ended 13 Oct 2026
        Assert.Equal(HealthStatus.Passed, HealthChecks.WindowsSupport(26100, "24H2", "Enterprise", Now).Item1);   // until Oct 2027
        Assert.Equal(HealthStatus.Passed, HealthChecks.WindowsSupport(26200, "25H2", "Core", Now).Item1);
        Assert.Equal(HealthStatus.Passed, HealthChecks.WindowsSupport(26300, "", "Professional", Now).Item1);     // newer/insider: no claim
        var soon = HealthChecks.WindowsSupport(26200, "25H2", "Core", new DateTime(2027, 9, 1));
        Assert.Equal((HealthStatus.Warning, Severity.Low), (soon.Item1, soon.Item2));
    }

    [Fact]
    public void Disabled_update_service_is_a_problem_with_repair()
    {
        var ok = HealthChecks.AnalyzeServices(new Dictionary<string, (string, string)> { ["wuauserv"] = ("Manual", "Stopped"), ["BITS"] = ("Manual", "Stopped") });
        Assert.Equal(HealthStatus.Passed, ok.Status);
        var bad = HealthChecks.AnalyzeServices(new Dictionary<string, (string, string)> { ["wuauserv"] = ("Disabled", "Stopped") });
        Assert.Equal(HealthStatus.Problem, bad.Status);
        Assert.Equal(SafeRepairs.UpdateServices, bad.RepairId);
        Assert.Equal(HealthStatus.Unknown, HealthChecks.AnalyzeServices(new Dictionary<string, (string, string)>()).Status);
    }

    [Fact]
    public void Failed_update_counts_only_if_not_installed_later()
    {
        var h = new List<UpdateHistoryEntry>
        {
            new("2026-10 Cumulative Update (KB5050001)", 4, unchecked((int)0x800F081F), Now.AddDays(-5)),
            new("Security Intelligence Update - KB2267602 (Version 1.1)", 4, unchecked((int)0x80240034), Now.AddDays(-3)),
            new("Security Intelligence Update - KB2267602 (Version 1.2)", 2, 0, Now.AddDays(-2)),
            new("2026-09 Cumulative Update (KB5040000)", 2, 0, Now.AddDays(-30)),
        };
        var r = HealthChecks.AnalyzeHistory(h, Now);
        var failed = r.Single(x => x.Id == "wu-failed");
        Assert.Equal(HealthStatus.Warning, failed.Status);
        Assert.Single(failed.Details);
        Assert.Contains("source files", failed.Details[0]);
        Assert.Equal(SafeRepairs.DismRestore, failed.RepairId);
        var last = r.Single(x => x.Id == "wu-last");
        Assert.Contains("KB5040000", last.Summary); // Defender definitions don't count as "last update"
        Assert.Equal(HealthStatus.Passed, last.Status);

        h.Add(new("2026-10 Cumulative Update (KB5050001)", 2, 0, Now.AddDays(-1)));
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeHistory(h, Now).Single(x => x.Id == "wu-failed").Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeHistory([new("Old (KB1234567)", 2, 0, Now.AddDays(-80))], Now).Single(x => x.Id == "wu-last").Status);
        // Store app installs through Windows Update are not Windows health.
        var store = HealthChecks.AnalyzeHistory([new("9NMN2Z9TZP22-47492CenterpointGaming.ProSightCrosshairExtension", 4, unchecked((int)0x8024000B), Now.AddDays(-1))], Now);
        Assert.Equal(HealthStatus.Passed, store.Single(x => x.Id == "wu-failed").Status);
    }

    [Fact]
    public void Pending_updates_and_driver_offers()
    {
        var r = HealthChecks.AnalyzePending([new("Cumulative", false, "", "", "Critical"), new("NVIDIA - Display - 32.0.15.6094", true, "Display", "NVIDIA GeForce RTX 4070", "")]);
        Assert.Equal((HealthStatus.Warning, Severity.Medium), (r[0].Status, r[0].Severity));
        Assert.Equal(HealthArea.Drivers, r[1].Area);
        Assert.Equal(HealthStatus.Warning, r[1].Status);
        Assert.All(HealthChecks.AnalyzePending([]), x => Assert.Equal(HealthStatus.Passed, x.Status));
    }

    private static EventHit E(int id, string provider, int level = 2, params string[] data) => new(Now.AddDays(-1), id, provider, level, data);

    [Fact]
    public void Restarts_bluescreens_and_power_loss()
    {
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeRestarts([], [], []).Status);
        var loss = HealthChecks.AnalyzeRestarts([E(41, "Microsoft-Windows-Kernel-Power", 1, "0")], [E(6008, "EventLog")], []);
        Assert.Equal(HealthStatus.Warning, loss.Status);
        Assert.NotNull(loss.Uncertainty);
        var bsod = HealthChecks.AnalyzeRestarts([E(41, "Microsoft-Windows-Kernel-Power", 1, "278")], [], [E(1001, "Microsoft-Windows-WER-SystemErrorReporting", 2, "0x00000116 (0x1, 0x2)")]);
        Assert.Equal(HealthStatus.Problem, bsod.Status);
        Assert.Contains(bsod.Details, d => d.Contains("VIDEO_TDR_FAILURE"));
        Assert.Equal(1u * 0x116, HealthChecks.ParseBugcheck("278"));
    }

    [Fact]
    public void Gpu_resets_app_crashes_whea_and_disk_events()
    {
        Assert.Equal(HealthStatus.Problem, HealthChecks.AnalyzeGpuDriverEvents([E(4101, "Display"), E(4101, "Display"), E(4101, "Display")], [], []).Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeGpuDriverEvents([E(4101, "Display")], [], []).Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeGpuDriverEvents([], [], []).Status);

        var gpuCrash = HealthChecks.AnalyzeAppCrashes([E(1000, "Application Error", 2, "FortniteClient-Win64-Shipping.exe", "1.0", "x", "nvwgf2umx.dll", "", "", "0xc0000005")], []);
        Assert.Equal(HealthStatus.Warning, gpuCrash.Status);
        Assert.Contains("graphics driver", gpuCrash.Title);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeAppCrashes([E(1000, "Application Error", 2, "notepad.exe", "1", "x", "ntdll.dll")], []).Status);
        var repeat = Enumerable.Range(0, 3).Select(_ => E(1000, "Application Error", 2, "game.exe", "1", "x", "game.exe")).ToList();
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeAppCrashes(repeat, []).Status);

        Assert.Equal(HealthStatus.Problem, HealthChecks.AnalyzeWhea([E(18, "Microsoft-Windows-WHEA-Logger", 2)]).Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeWhea([E(17, "Microsoft-Windows-WHEA-Logger", 3)]).Status);
        Assert.Equal(HealthStatus.Problem, HealthChecks.AnalyzeDiskEvents([E(7, "disk", 2, @"\Device\Harddisk1\DR1")]).Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeDiskEvents([E(153, "disk", 3)]).Status);
    }

    [Fact]
    public void Thermal_load_memory_and_disk_rules()
    {
        Assert.Equal(HealthStatus.Unknown, HealthChecks.AnalyzeThermal(0, null, null, null, [], "no log").Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeThermal(0, null, 100, null, [], null).Status);
        var gpuHot = HealthChecks.AnalyzeThermal(0, null, 100, null, HealthChecks.ParseNvidiaSmi("NVIDIA GeForce RTX 4070, 84, 99, 0x0000000000000040\r\n"), null);
        Assert.Equal((HealthStatus.Warning, Severity.Medium), (gpuHot.Status, gpuHot.Severity));
        Assert.Contains(gpuHot.Details, d => d.Contains("hardware thermal slowdown"));
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeLoad(92, 10, [("x", 80)]).Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeLoad(10, 5, []).Status);

        Assert.Equal(HealthStatus.Problem, HealthChecks.AnalyzeMemory(32, 50, 95, 40, 8192, true, 0, null).Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeMemory(32, 50, 50, 40, 0, false, 0, null).Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeMemory(32, 40, 40, 40, 8192, true, 0, null).Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeMemory(32, 74, 89, 35, 3900, true, 0, null).Status);

        Assert.Equal(HealthStatus.Problem, HealthChecks.AnalyzeDisks([new(@"C:\", "", 3, 500, true)]).Status);
        Assert.Equal(HealthStatus.Warning, HealthChecks.AnalyzeDisks([new(@"C:\", "", 200, 500, true), new(@"D:\", "Games", 20, 2000, false)]).Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeDisks([new(@"C:\", "", 200, 500, true)]).Status);
        Assert.Equal(HealthStatus.Problem, HealthChecks.AnalyzeDriveHealth([("Samsung 980", 2, "4", "17")]).Status);
        Assert.Equal(HealthStatus.Passed, HealthChecks.AnalyzeDriveHealth([("Samsung 980", 0, "4", "17")]).Status);
    }

    [Fact]
    public void Sfc_result_from_cbs_log_is_language_independent()
    {
        const string clean = "2026-10-16 12:00:00, Info CSI 00000001 [SR] Verify complete\n";
        Assert.Equal(HealthStatus.Passed, SystemFileCheck.ParseSfc(FileCheckKind.SfcVerifyOnly, 0, "Järjestelmän tarkistus 100 % valmis.", clean).Status);
        const string corrupt = "Info CSI Hashes for file member [l:10]'dxgi.dll' do not match actual file [l:10]'dxgi.dll'\nInfo CSI [SR] Verify complete\n";
        var v = SystemFileCheck.ParseSfc(FileCheckKind.SfcVerifyOnly, 0, "", corrupt);
        Assert.Equal(HealthStatus.Problem, v.Status);
        const string unrep = "Info CSI [SR] Cannot repair member file [l:10]'dxgi.dll'\nInfo CSI [SR] Repair complete\n";
        Assert.Equal(HealthStatus.Problem, SystemFileCheck.ParseSfc(FileCheckKind.SfcScanNow, 0, "", unrep).Status);
        const string fixedLog = "Info CSI [SR] Repairing corrupted file \\??\\C:\\Windows\\System32\\dxgi.dll\nInfo CSI [SR] Verify complete\n";
        Assert.Equal(HealthStatus.Passed, SystemFileCheck.ParseSfc(FileCheckKind.SfcScanNow, 0, "", fixedLog).Status);
        // No CBS data and unreadable (localized) output: unable to check, never "passed".
        Assert.Equal(HealthStatus.Unknown, SystemFileCheck.ParseSfc(FileCheckKind.SfcVerifyOnly, 0, "Järjestelmän tarkistus valmis.", null).Status);
    }

    [Fact]
    public void Dism_results()
    {
        Assert.Equal(HealthStatus.Passed, SystemFileCheck.ParseDism(FileCheckKind.DismCheckHealth, 0, "No component store corruption detected.\r\nThe operation completed successfully.").Status);
        Assert.Equal(HealthStatus.Problem, SystemFileCheck.ParseDism(FileCheckKind.DismScanHealth, 0, "The component store is repairable.").Status);
        Assert.Equal(HealthStatus.Problem, SystemFileCheck.ParseDism(FileCheckKind.DismRestoreHealth, unchecked((int)0x800F081F), "Error: 0x800f081f").Status);
        var offline = SystemFileCheck.ParseDism(FileCheckKind.DismRestoreHealth, unchecked((int)0x800F0906), "");
        Assert.Equal(HealthStatus.Unknown, offline.Status);
        Assert.Contains("downloaded", offline.Summary);
        Assert.Equal(SafeRepairs.DismRestore, SystemFileCheck.AsResult(SystemFileCheck.ParseDism(FileCheckKind.DismScanHealth, 0, "The component store is repairable.")).RepairId);
    }

    [Fact]
    public void Overall_status_is_the_worst_real_status()
    {
        HealthResult X(HealthStatus s) => new("x", HealthArea.Windows, "x", s, Severity.Info, "", [], Now);
        Assert.Equal(HealthStatus.Problem, HealthState.Overall([X(HealthStatus.Passed), X(HealthStatus.Problem), X(HealthStatus.Unknown)]));
        Assert.Equal(HealthStatus.Passed, HealthState.Overall([X(HealthStatus.Passed), X(HealthStatus.Unknown)]));
        Assert.Equal(HealthStatus.Unknown, HealthState.Overall([X(HealthStatus.Unknown)]));
    }
}

/// <summary>Runs the real, read-only checks on this machine (skips the slow Windows Update search).</summary>
public class HealthLiveTests(ITestOutputHelper output)
{
    [Fact]
    public void Read_only_checks_run_on_this_pc()
    {
        foreach (var c in HealthChecks.All.Where(c => c.Id is not "wu-search"))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = c.Run(CancellationToken.None);
            output.WriteLine($"== {c.Id} ({sw.ElapsedMilliseconds} ms)");
            foreach (var r in res)
            {
                output.WriteLine($"  [{r.Status}/{r.Severity}] {r.Title}: {r.Summary}");
                foreach (var d in r.Details.Take(6)) output.WriteLine("      " + d);
            }
            Assert.NotEmpty(res);
            Assert.All(res, r => Assert.False(string.IsNullOrWhiteSpace(r.Summary)));
        }
    }
}
