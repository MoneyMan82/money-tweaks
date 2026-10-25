using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>Tweak X-Ray: real locations and values, read-only, matching the backup Apply takes. Scratch keys under HKCU only.</summary>
[Collection("registry")]
public class TweakXRayTests : IDisposable
{
    private const string Scratch = @"Software\MoneyTweaksTests\XRay";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-xray-" + Guid.NewGuid().ToString("N"));

    public TweakXRayTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static Tweak Scratchy(params TweakOp[] ops) => new() { Id = "test.xray", Category = TweakCategory.General, Name = "X-Ray test", Description = "Sets test values.", Ops = ops };

    [Fact]
    public void Inspecting_shows_location_current_and_proposed_and_changes_nothing()
    {
        var t = Scratchy(new RegValueOp(Hive.HKCU, Scratch, "Mode", RegistryValueKind.DWord, 1));
        var r = TweakXRay.Inspect(t);
        var c = Assert.Single(r.Changes);
        Assert.Equal($@"HKEY_CURRENT_USER\{Scratch}\Mode", c.Location);
        Assert.Equal("not set (the key doesn't exist)", c.Current);
        Assert.Equal("1 (DWord)", c.Proposed);
        Assert.Equal(XRayReach.Exact, c.Reach);
        Assert.False(c.AlreadySet);
        Assert.Null(Registry.CurrentUser.OpenSubKey(Scratch));   // read-only: the key still doesn't exist
        Assert.False(BackupStore.Exists(t.Id));
        Assert.StartsWith("full", r.Reversibility);
        Assert.Contains($"{Scratch}\\Mode: not set (the key doesn't exist) → 1 (DWord)", r.Compact());
    }

    [Fact]
    public void After_applying_undo_shows_the_backed_up_original_and_matches_the_backup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 7, RegistryValueKind.DWord);
        var t = Scratchy(new RegValueOp(Hive.HKCU, Scratch, "Mode", RegistryValueKind.DWord, 1));
        Assert.Equal("7 (DWord)", TweakXRay.Inspect(t).Changes[0].Current);   // what Apply is about to back up

        Assert.True(TweakEngine.Apply(t).Success);
        var r = TweakXRay.Inspect(t);
        Assert.True(r.Applied);
        Assert.Equal("1 (DWord)", r.Changes[0].Current);
        Assert.Equal("back to 7 (DWord)", r.Changes[0].Undo);
        Assert.Contains("backed up", r.Backup);

        Assert.True(TweakEngine.Undo(t).Success);
        using var k2 = Registry.CurrentUser.OpenSubKey(Scratch);
        Assert.Equal(7, k2!.GetValue("Mode"));
        Assert.Contains("captured when you apply", TweakXRay.Inspect(t).Changes[0].Undo);
    }

    [Fact]
    public void Already_set_values_are_marked_and_left_out_of_the_preview()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) { k.SetValue("A", 1, RegistryValueKind.DWord); k.SetValue("S", "x=1;y=2;", RegistryValueKind.String); }
        var t = Scratchy(new RegValueOp(Hive.HKCU, Scratch, "A", RegistryValueKind.DWord, 1), new RegStringFlagOp(Hive.HKCU, Scratch, "S", "y", "5"));
        var r = TweakXRay.Inspect(t);
        Assert.True(r.Changes[0].AlreadySet);
        Assert.False(r.Changes[1].AlreadySet);
        Assert.StartsWith("y=2", r.Changes[1].Current);
        Assert.Equal("y=5; other entries kept", r.Changes[1].Proposed);
        Assert.DoesNotContain($@"{Scratch}\A:", r.Compact());
        Assert.Contains($@"{Scratch}\S:", r.Compact());
    }

    [Fact]
    public void Commands_are_marked_not_inspectable_and_reversibility_says_so()
    {
        var t = Scratchy(new CommandOp { ApplyCommand = "echo apply", UndoCommand = "echo undo", Admin = false, Label = "Test command" });
        var r = TweakXRay.Inspect(t);
        var c = Assert.Single(r.Changes);
        Assert.Equal(XRayReach.Unknown, c.Reach);
        Assert.StartsWith("unknown", c.Current);
        Assert.Contains("echo apply", c.Proposed);
        Assert.Contains("echo undo", c.Undo);
        Assert.StartsWith("by command", r.Reversibility);
        Assert.Contains("[effect not inspectable]", r.Compact());
        Assert.True(r.HasUnknown);
    }

    [Fact]
    public void Apply_logs_the_captured_original_values()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("Mode", 3, RegistryValueKind.DWord);
        var t = Scratchy(new RegValueOp(Hive.HKCU, Scratch, "Mode", RegistryValueKind.DWord, 1));
        Assert.True(TweakEngine.Apply(t).Success);
        var log = File.ReadAllText(Log.CurrentFile);
        Assert.Contains("test.xray before step 1:", log);
        Assert.Contains("| was 3 (DWord)", log);
        TweakEngine.Undo(t);
    }

    [Fact]
    public void Every_registry_and_service_tweak_in_the_catalog_lists_real_locations()
    {
        // Registry, service and task steps only (no PowerShell or powercfg processes): every step gets a row with a location.
        var tweaks = TweakCatalog.All.Where(TweakXRay.Applies)
            .Where(t => t.Ops.All(o => o is RegValueOp or RegStringFlagOp or ServiceStartOp)).ToList();
        Assert.NotEmpty(tweaks);
        foreach (var t in tweaks)
        {
            var r = TweakXRay.Inspect(t);
            Assert.Equal(t.Ops.Count, r.Changes.Count);
            Assert.All(r.Changes, c =>
            {
                Assert.StartsWith("HKEY_", c.Location);
                Assert.Equal(XRayReach.Exact, c.Reach);
                Assert.False(string.IsNullOrWhiteSpace(c.Current));
                Assert.False(string.IsNullOrWhiteSpace(c.Proposed));
            });
        }
        Assert.DoesNotContain(TweakCatalog.All.Where(t => t.Kind == TweakKind.Guide), TweakXRay.Applies);
    }
}
