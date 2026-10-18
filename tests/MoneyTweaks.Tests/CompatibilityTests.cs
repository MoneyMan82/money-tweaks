using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>
/// Smart Tweak Compatibility Engine. Scratch tweaks write only under HKCU\Software\MoneyTweaksTests; catalog tweaks are
/// only analysed, never applied.
/// </summary>
[Collection("registry")]
public class CompatibilityTests : IDisposable
{
    private const string Scratch = @"Software\MoneyTweaksTests\Compat";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-compat-" + Guid.NewGuid().ToString("N"));

    private static readonly SystemProfile Desktop = new()
    {
        Build = 26100, Edition = "Professional", HasBattery = false, RamGb = 32, SystemDiskIsSsd = true, Gpus = ["NVIDIA GeForce RTX 4070 SUPER"],
        PhysicalPrinters = 0, HasBiometricDevice = false, HasBluetooth = false, RemoteDesktopEnabled = false, HasWiredAdapter = true,
    };

    public CompatibilityTests()
    {
        Directory.CreateDirectory(_tmp);
        AppPaths.OverrideRoot(_tmp);
        SystemProfile.OverrideForTests(Desktop);
    }

    public void Dispose()
    {
        SystemProfile.OverrideForTests(null);
        Compatibility.Universe = () => TweakCatalog.All;
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static Tweak Reg(string id, int value, params TweakOp[] extra) => new()
    {
        Id = id, Category = TweakCategory.General, Name = id, Description = "",
        Ops = [new RegValueOp(Hive.HKCU, Scratch, "Mode", RegistryValueKind.DWord, value), .. extra],
    };

    private static int? ReadMode() { using var k = Registry.CurrentUser.OpenSubKey(Scratch); return k?.GetValue("Mode") as int?; }

    // ---- conflicts ----------------------------------------------------------------------------------------------

    [Fact]
    public void Conflicting_tweak_is_blocked_while_the_other_is_applied_and_allowed_after_undo()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 0, RegistryValueKind.DWord);
        var a = Reg("test.mode-a", 1);
        var b = Reg("test.mode-b", 2);
        Compatibility.Universe = () => [a, b];
        Assert.Equal(Compatibility.Overlap.Conflict, Compatibility.Between(a, b));

        Assert.True(TweakEngine.Apply(a).Success);
        var blocked = TweakEngine.Apply(b);
        Assert.False(blocked.Success);
        Assert.Contains("Undo it first", blocked.Message);
        Assert.Equal(1, ReadMode());                   // a's change untouched
        Assert.False(BackupStore.Exists(b.Id));        // nothing captured for b

        Assert.True(TweakEngine.Undo(a).Success);
        Assert.Equal(0, ReadMode());                   // original restored
        Assert.True(TweakEngine.Apply(b).Success);
        Assert.True(TweakEngine.Undo(b).Success);
        Assert.Equal(0, ReadMode());
    }

    [Fact]
    public void Duplicate_change_is_detected_as_already_applied_and_not_written_again()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 0, RegistryValueKind.DWord);
        var a = Reg("test.dup-a", 1);
        var c = Reg("test.dup-c", 1);
        Compatibility.Universe = () => [a, c];
        Assert.Equal(Compatibility.Overlap.Duplicate, Compatibility.Between(a, c));
        Assert.True(TweakEngine.Apply(a).Success);

        var r = TweakEngine.Apply(c);
        Assert.True(r.Success);
        Assert.Contains("Already applied", r.Message);
        Assert.False(BackupStore.Exists(c.Id)); // no second backup capturing a's value as "original"
        Assert.Contains(Compatibility.Findings(c, Desktop), f => f.OtherTweakId == a.Id && f.Severity == CompatSeverity.Info);
    }

    [Fact]
    public void Catalog_dns_choices_conflict_with_each_other()
    {
        var cf = TweakCatalog.Find("net.dns-cloudflare")!;
        var gg = TweakCatalog.Find("net.dns-google")!;
        Assert.Equal(Compatibility.Overlap.Conflict, Compatibility.Between(cf, gg));
    }

    // ---- unsupported settings ----------------------------------------------------------------------------------

    [Fact]
    public void Unsupported_hardware_and_os_are_blocked_or_warned_from_the_real_profile_fields()
    {
        var gpuPref = TweakCatalog.Find("gpu.fortnite-high-performance")!;
        Assert.Contains(Compatibility.StaticFindings(gpuPref, Desktop), f => f.Severity == CompatSeverity.Block); // one GPU
        Assert.DoesNotContain(Compatibility.StaticFindings(gpuPref, Desktop with { Gpus = ["Intel UHD 770", "RTX 4070"] }), f => f.Severity == CompatSeverity.Block);
        Assert.DoesNotContain(Compatibility.StaticFindings(gpuPref, Desktop with { Gpus = [] }), f => f.Severity == CompatSeverity.Block); // unknown ≠ unsupported

        var hags = TweakCatalog.Find("adv.hags")!;
        Assert.Contains(Compatibility.StaticFindings(hags, Desktop with { Build = 18363 }), f => f.Severity == CompatSeverity.Block);

        var minState = TweakCatalog.Find("gen.min-processor-state")!;
        Assert.DoesNotContain(Compatibility.StaticFindings(minState, Desktop), f => f.Text.Contains("battery"));
        Assert.Contains(Compatibility.StaticFindings(minState, Desktop with { HasBattery = true }), f => f.Severity == CompatSeverity.Warn && f.Text.Contains("battery"));

        var prefetch = TweakCatalog.Find("adv.prefetch")!;
        Assert.Contains(Compatibility.StaticFindings(prefetch, Desktop with { SystemDiskIsSsd = false }), f => f.Severity == CompatSeverity.Warn);

        var spooler = TweakCatalog.Find("svc.spooler")!;
        Assert.Contains(Compatibility.StaticFindings(spooler, Desktop with { PhysicalPrinters = 2 }), f => f.Severity == CompatSeverity.Warn);

        var memc = TweakCatalog.Find("adv.memory-compression")!;
        Assert.Contains(Compatibility.StaticFindings(memc, Desktop with { RamGb = 8 }), f => f.Severity == CompatSeverity.Warn);
    }

    [Fact]
    public void Blocked_tweak_is_refused_by_the_engine_before_any_backup_or_change()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 0, RegistryValueKind.DWord);
        var t = Reg("test.needs-new-windows", 5) with { MinBuild = 99999 };
        Compatibility.Universe = () => [t];
        var r = TweakEngine.Apply(t);
        Assert.False(r.Success);
        Assert.Equal(0, ReadMode());
        Assert.False(BackupStore.Exists(t.Id));
    }

    [Fact]
    public void Security_weakening_tweak_is_blocked_in_the_report()
    {
        var hvci = TweakCatalog.Find("adv.vbs-hvci")!;
        Assert.Contains(Compatibility.StaticFindings(hvci, Desktop), f => f.Severity == CompatSeverity.Block);
    }

    // ---- failed operations and rollback --------------------------------------------------------------------------

    private sealed class FailingOp : TweakOp
    {
        public override bool RequiresAdmin => false;
        public override string Describe() => "always fails";
        public override OpState Check() => OpState.NotApplied;
        public override OpBackup Capture() => new() { Kind = "fail" };
        public override void Apply() => throw new TweakOpException("simulated failure");
        public override void Undo(OpBackup? backup) { }
    }

    [Fact]
    public void Failed_step_rolls_back_the_steps_already_done_and_leaves_no_backup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 7, RegistryValueKind.DWord);
        var t = Reg("test.fails-midway", 1, new FailingOp());
        Compatibility.Universe = () => [t];
        var r = TweakEngine.Apply(t);
        Assert.False(r.Success);
        Assert.Contains("simulated failure", r.Message);
        Assert.Equal(7, ReadMode());               // first step undone
        Assert.False(BackupStore.Exists(t.Id));
    }

    // ---- report ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Report_shows_current_values_effect_risk_restart_and_reversibility_without_promising_fps()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 0, RegistryValueKind.DWord);
        var t = Reg("test.report", 3) with { RequiresReboot = true };
        Compatibility.Universe = () => [t];
        var rep = Compatibility.Analyze(t, Desktop);
        Assert.Equal(TweakState.NotApplied, rep.State);
        Assert.False(rep.Blocked);
        var text = rep.ToText();
        Assert.Contains("Tier: Safe", text);
        Assert.Contains("Restart: needed", text);
        Assert.Contains("Reversible:", text);
        Assert.Contains("No FPS gain is promised", text);
        Assert.NotEmpty(rep.CurrentValues);
    }

    // ---- tiers and catalog audit -------------------------------------------------------------------------------

    [Fact]
    public void Every_catalog_tweak_gets_a_tier_and_recommended_ones_are_all_safe()
    {
        var tiers = TweakCatalog.All.ToDictionary(t => t.Id, Compatibility.TierOf);
        Assert.Equal(TweakTier.Experimental, tiers["adv.timer-dynamic-tick"]); // bcdedit
        Assert.Equal(TweakTier.Experimental, tiers["adv.gpu-msi"]);            // device interrupts
        Assert.All(TweakCatalog.All.Where(t => t.Risk == Risk.Caution), t => Assert.NotEqual(TweakTier.Safe, tiers[t.Id]));
        Assert.All(TweakCatalog.All.Where(t => t.Recommended), t => Assert.Equal(TweakTier.Safe, tiers[t.Id]));
        Assert.Contains(tiers.Values, v => v == TweakTier.Safe);
        Assert.Contains(tiers.Values, v => v == TweakTier.Advanced);
    }

    [Fact]
    public void Every_rule_refers_to_a_real_tweak_and_no_safe_catalog_tweak_is_blocked_on_a_normal_desktop()
    {
        foreach (var id in Compatibility.RuleIds) Assert.NotNull(TweakCatalog.Find(id));
        var blocked = TweakCatalog.All.Where(t => Compatibility.StaticFindings(t, Desktop with { Gpus = ["A", "B"] }).Any(f => f.Severity == CompatSeverity.Block))
            .Select(t => t.Id).ToList();
        // Only the deliberately disabled tweaks and Windows-11-only ones (if any exceed this build) may be blocked.
        Assert.All(blocked, id => Assert.NotNull(TweakAudit.DisabledReason(TweakCatalog.Find(id)!)));
    }

    // ---- real detection ---------------------------------------------------------------------------------------------

    [Fact]
    public void Real_system_profile_is_read_from_this_pc()
    {
        var p = SystemProfile.Detect();
        Assert.Equal(OsInfo.Build, p.Build);
        Assert.True(p.RamGb > 0);
        Assert.False(string.IsNullOrEmpty(p.Edition));
        Assert.All(p.Gpus, g => Assert.True(SystemProfile.IsRealGpu(g)));
    }
}
