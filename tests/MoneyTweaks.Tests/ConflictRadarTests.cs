using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>Tweak Conflict Radar: conflicts only from shared settings and catalog notes, read-only, with non-changing defaults.</summary>
[Collection("registry")]
public class ConflictRadarTests : IDisposable
{
    private const string Scratch = @"Software\MoneyTweaksTests\Radar";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-radar-" + Guid.NewGuid().ToString("N"));

    public ConflictRadarTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }

    public void Dispose()
    {
        Compatibility.Universe = () => TweakCatalog.All;
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static Tweak T(string id, params TweakOp[] ops) => new() { Id = id, Category = TweakCategory.General, Name = id, Description = "", Ops = ops };
    private static RegValueOp Mode(int v, string name = "Mode") => new(Hive.HKCU, Scratch, name, RegistryValueKind.DWord, v);

    [Fact]
    public void Different_values_in_one_selection_are_a_contradiction_with_evidence()
    {
        var a = T("test.a", Mode(1)); var b = T("test.b", Mode(2));
        var c = Assert.Single(ConflictRadar.Scan([a, b], []));
        Assert.Equal(RadarKind.Contradiction, c.Kind);
        Assert.Same(b, c.Selected); Assert.Same(a, c.Other);
        Assert.Contains($@"HKCU\{Scratch}\Mode: 'test.b' sets 2 (DWord), 'test.a' (also selected) sets 1 (DWord)", c.Evidence);
        Assert.All(c.Fixes, f => Assert.False(f.ChangesSystem));
        Assert.Equal(["test.b", "test.a"], c.Fixes.Select(f => f.TweakId));   // keep the earlier one by default
        Assert.Null(Registry.CurrentUser.OpenSubKey(Scratch));                 // scanning changed nothing
    }

    [Fact]
    public void Same_value_is_a_duplicate_and_unrelated_values_are_nothing()
    {
        var a = T("test.a", Mode(1)); var b = T("test.b", Mode(1)); var other = T("test.c", Mode(1, "Other"));
        var c = Assert.Single(ConflictRadar.Scan([a, b, other], []));
        Assert.Equal(RadarKind.Duplicate, c.Kind);
        Assert.Equal(RadarFixKind.SkipSelected, c.Fixes[0].Kind);
        Assert.Empty(ConflictRadar.Scan([a, other], []));
    }

    [Fact]
    public void Contradiction_with_an_applied_tweak_offers_skip_by_default_and_undo_as_an_option()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 0, RegistryValueKind.DWord);
        var applied = T("test.applied", Mode(1)); var wanted = T("test.wanted", Mode(2)); var bystander = T("test.bystander", Mode(5, "Elsewhere"));
        Compatibility.Universe = () => [applied, wanted, bystander];
        Assert.True(TweakEngine.Apply(applied).Success);

        var c = Assert.Single(ConflictRadar.ScanForApply([wanted]));
        Assert.True(c.OtherApplied);
        Assert.Equal(RadarKind.Contradiction, c.Kind);
        Assert.Equal(RadarFixKind.SkipSelected, c.Fixes[0].Kind);
        Assert.False(c.Fixes[0].ChangesSystem);
        Assert.Equal(RadarFixKind.UndoAppliedFirst, c.Fixes[1].Kind);
        Assert.True(c.Fixes[1].ChangesSystem);
        Assert.Equal("test.applied", c.Fixes[1].TweakId);
        using (var k = Registry.CurrentUser.OpenSubKey(Scratch)) Assert.Equal(1, k!.GetValue("Mode"));   // still the applied value

        // The fix path: Undo through the backup, then the wanted tweak applies cleanly.
        Assert.True(TweakEngine.Undo(applied).Success);
        Assert.Empty(ConflictRadar.ScanForApply([wanted]));
        Assert.True(TweakEngine.Apply(wanted).Success);
        TweakEngine.Undo(wanted);
        using (var k = Registry.CurrentUser.OpenSubKey(Scratch)) Assert.Equal(0, k!.GetValue("Mode"));
    }

    [Fact]
    public void A_raw_registry_Start_value_and_a_service_step_are_the_same_setting()
    {
        var reg = T("test.reg", new RegValueOp(Hive.HKLM, @"SYSTEM\CurrentControlSet\Services\XblGameSave", "Start", RegistryValueKind.DWord, 4));
        var svc = T("test.svc", new ServiceStartOp("XblGameSave", 3));
        var c = Assert.Single(ConflictRadar.Scan([svc, reg], []));
        Assert.Equal(RadarKind.Contradiction, c.Kind);
        Assert.Contains(c.Evidence, e => e.Contains("4 (Disabled)") && e.Contains("3 (Manual)"));
        Assert.Equal(Compatibility.Overlap.Conflict, Compatibility.Between(svc, reg));   // the engine's block sees it too
    }

    [Fact]
    public void String_entries_tasks_and_store_apps_are_compared()
    {
        var f1 = T("test.f1", new RegStringFlagOp(Hive.HKCU, Scratch, "Flags", "VRROptimizeEnable", "0"));
        var f2 = T("test.f2", new RegStringFlagOp(Hive.HKCU, Scratch, "Flags", "VRROptimizeEnable", "1"));
        var f3 = T("test.f3", new RegStringFlagOp(Hive.HKCU, Scratch, "Flags", "SwapEffectUpgradeEnable", "1"));
        Assert.Equal(RadarKind.Contradiction, Assert.Single(ConflictRadar.Scan([f1, f2, f3], [])).Kind);   // f3 is another entry
        var t1 = T("test.t1", new ScheduledTaskOp(@"\Microsoft\Windows\Test\Task")); var t2 = T("test.t2", new ScheduledTaskOp(@"\Microsoft\Windows\Test\Task"));
        Assert.Equal(RadarKind.Duplicate, Assert.Single(ConflictRadar.Scan([t1, t2], [])).Kind);
        var a1 = T("test.a1", new AppxRemoveOp("Contoso.App")); var a2 = T("test.a2", new AppxRemoveOp("contoso.app"));
        Assert.Equal(RadarKind.Duplicate, Assert.Single(ConflictRadar.Scan([a1, a2], [])).Kind);
    }

    [Fact]
    public void Power_plan_settings_before_a_plan_switch_are_order_dependent()
    {
        var setting = T("test.setting", new PowerCfgOp("sub", "set", 0, "USB selective suspend"));
        var plan = T("test.plan", new CommandOp { ApplyCommand = "powercfg /setactive 99999999-9999-9999-9999-999999999999", UndoCommand = "powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e", Admin = false });
        var c = Assert.Single(ConflictRadar.Scan([setting, plan], []));
        Assert.Equal(RadarKind.OrderMatters, c.Kind);
        Assert.Equal(RadarFixKind.MoveFirst, c.Fixes[0].Kind);
        Assert.Equal("test.plan", c.Fixes[0].TweakId);
        Assert.Empty(ConflictRadar.Scan([plan, setting], []));   // right order: nothing to report
    }

    [Fact]
    public void Real_catalog_combinations_are_found_and_only_with_evidence()
    {
        var cat = TweakCatalog.All.Where(t => t.Kind == TweakKind.Toggle).ToList();
        // The DNS choices check and undo the same setting: picking two is a contradiction.
        var dns = cat.Where(t => t.Id.StartsWith("net.dns-", StringComparison.Ordinal)).Take(2).ToList();
        Assert.Equal(2, dns.Count);
        Assert.Equal(RadarKind.Contradiction, Assert.Single(ConflictRadar.Scan(dns, [])).Kind);
        // The catalog documents that turning hibernation off also turns Fast Startup off.
        var hib = cat.First(t => t.Id == "gen.hibernate-off"); var fast = cat.First(t => t.Id == "gen.fast-startup-off");
        var note = Assert.Single(ConflictRadar.Scan([fast, hib], []));
        Assert.Equal(RadarKind.KnownInteraction, note.Kind);
        Assert.StartsWith("Catalog note", note.Evidence[0]);
        var reversed = Assert.Single(ConflictRadar.Scan([hib, fast], []));   // either order, reported on the later one
        Assert.Same(fast, reversed.Selected);
        // Plan settings after the plan switch is the right order: nothing to report; before it: order matters.
        var plan = cat.First(t => t.Id == "gen.ultimate-plan"); var usb = cat.First(t => t.Id == "gen.usb-suspend");
        Assert.Empty(ConflictRadar.Scan([plan, usb], []));
        Assert.Equal(RadarKind.OrderMatters, Assert.Single(ConflictRadar.Scan([usb, plan], [])).Kind);
        // Applying every tweak at once: each Duplicate/Contradiction really shares a setting with the other tweak.
        foreach (var c in ConflictRadar.Scan(cat.Where(t => !t.Ops.OfType<DynamicRegOp>().Any()).ToList(), []))
        {
            if (c.Kind is RadarKind.Duplicate or RadarKind.Contradiction)
                Assert.NotEmpty(ConflictRadar.Shared(ConflictRadar.Effects(c.Selected, false), ConflictRadar.Effects(c.Other, false)));
            Assert.NotEmpty(c.Evidence);
            Assert.False(c.Fixes[0].ChangesSystem);
        }
    }
}
