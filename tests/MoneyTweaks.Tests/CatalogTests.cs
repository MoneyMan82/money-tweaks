using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class CatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void Ids_are_unique_and_well_formed()
    {
        var dupes = TweakCatalog.All.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
        Assert.All(TweakCatalog.All, t => Assert.Matches(@"^[a-z]+\.[a-z0-9.\-]+$", t.Id));
    }

    [Fact]
    public void Names_are_unique()
    {
        var dupes = TweakCatalog.All.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Every_tweak_is_complete()
    {
        foreach (var t in TweakCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name), t.Id);
            Assert.True(t.Description.Length > 20, $"{t.Id} needs a real description");
            if (t.Kind == TweakKind.Toggle) Assert.NotEmpty(t.Ops);
            else Assert.NotEmpty(t.GuideSteps);
        }
    }

    [Fact]
    public void Every_category_is_populated()
    {
        foreach (var c in Enum.GetValues<TweakCategory>())
        {
            var n = TweakCatalog.InCategory(c).Count();
            output.WriteLine($"{CategoryInfo.DisplayName(c),-14} {n}");
            Assert.True(n > 0, c.ToString());
        }
        output.WriteLine($"TOTAL          {TweakCatalog.All.Count}");
    }

    [Fact]
    public void No_duplicate_services_or_packages()
    {
        Assert.Equal(TweakCatalog.ServiceTable.Length, TweakCatalog.ServiceTable.Select(s => s.svc.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(TweakCatalog.AppTable.Length, TweakCatalog.AppTable.Select(s => s.pkg.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Every_state_check_runs_without_throwing()
    {
        // Read-only: evaluates every tweak's current state on this machine.
        var states = TweakCatalog.All.Select(t => (t, s: TweakEngine.GetState(t))).ToList();
        foreach (var g in states.GroupBy(x => x.s)) output.WriteLine($"{g.Key}: {g.Count()}");
        Assert.Equal(TweakCatalog.All.Count, states.Count);
    }
}

[Collection("registry")]
public class EngineTests : IDisposable
{
    private const string TestKey = @"Software\MoneyTweaks.Tests";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-tests-" + Guid.NewGuid().ToString("N"));

    public EngineTests()
    {
        AppPaths.OverrideRoot(_tmp);
        Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact]
    public void RegValueOp_restores_original_value()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("A", 7, RegistryValueKind.DWord);
        var op = new RegValueOp(Hive.HKCU, TestKey, "A", RegistryValueKind.DWord, 0);
        Assert.Equal(OpState.NotApplied, op.Check());
        var b = op.Capture();
        op.Apply();
        Assert.Equal(OpState.Applied, op.Check());
        op.Undo(b);
        using var k2 = Registry.CurrentUser.OpenSubKey(TestKey)!;
        Assert.Equal(7, k2.GetValue("A"));
    }

    [Fact]
    public void RegValueOp_deletes_value_that_did_not_exist()
    {
        var op = new RegValueOp(Hive.HKCU, TestKey, "New", RegistryValueKind.String, "x");
        var b = op.Capture();
        op.Apply();
        op.Undo(b);
        using var k = Registry.CurrentUser.OpenSubKey(TestKey);
        Assert.Null(k?.GetValue("New"));
    }

    [Fact]
    public void RegValueOp_removes_marker_key_it_created()
    {
        var path = TestKey + @"\Marker\InprocServer32";
        var op = new RegValueOp(Hive.HKCU, path, "", RegistryValueKind.String, "") { RemoveKeyIfCreated = true };
        var b = op.Capture();
        op.Apply();
        Assert.Equal(OpState.Applied, op.Check());
        op.Undo(b);
        Assert.Null(Registry.CurrentUser.OpenSubKey(path));
    }

    [Fact]
    public void Uint_dword_roundtrip()
    {
        var op = new RegValueOp(Hive.HKCU, TestKey, "U", RegistryValueKind.DWord, 0xFFFFFFFFu);
        var b = op.Capture();
        op.Apply();
        Assert.Equal(OpState.Applied, op.Check());
        op.Undo(b);
        Assert.Equal(OpState.NotApplied, op.Check());
    }

    [Fact]
    public void Binary_value_roundtrip()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("Bin", new byte[] { 2, 0, 0, 0 }, RegistryValueKind.Binary);
        var disabled = new byte[12]; disabled[0] = 3;
        var op = new RegValueOp(Hive.HKCU, TestKey, "Bin", RegistryValueKind.Binary, disabled);
        var b = op.Capture();
        op.Apply();
        Assert.Equal(OpState.Applied, op.Check());
        op.Undo(b);
        using var k2 = Registry.CurrentUser.OpenSubKey(TestKey)!;
        Assert.Equal(new byte[] { 2, 0, 0, 0 }, (byte[])k2.GetValue("Bin")!);
    }

    [Fact]
    public void Engine_apply_and_undo_keep_first_backup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("V", "orig");
        var t = new Tweak
        {
            Id = "test.v", Category = TweakCategory.General, Name = "t", Description = "test tweak description",
            Ops = [new RegValueOp(Hive.HKCU, TestKey, "V", RegistryValueKind.String, "new")]
        };
        Assert.Equal(TweakState.NotApplied, TweakEngine.GetState(t));
        Assert.True(TweakEngine.Apply(t).Success);
        Assert.True(TweakEngine.Apply(t).Success); // re-apply must not overwrite the original backup
        Assert.Equal(TweakState.Applied, TweakEngine.GetState(t));
        Assert.True(TweakEngine.Undo(t).Success);
        using var k2 = Registry.CurrentUser.OpenSubKey(TestKey)!;
        Assert.Equal("orig", k2.GetValue("V"));
        Assert.False(BackupStore.Exists(t.Id));
    }

    [Fact]
    public void Engine_rolls_back_when_a_step_fails()
    {
        var t = new Tweak
        {
            Id = "test.rollback", Category = TweakCategory.General, Name = "t", Description = "test tweak description",
            Ops =
            [
                new RegValueOp(Hive.HKCU, TestKey, "Step1", RegistryValueKind.DWord, 1),
                new CommandOp { ApplyCommand = "exit /b 5", UndoCommand = "exit /b 0", Admin = false },
            ]
        };
        var r = TweakEngine.Apply(t);
        Assert.False(r.Success);
        using var k = Registry.CurrentUser.OpenSubKey(TestKey);
        Assert.Null(k?.GetValue("Step1"));
        Assert.False(BackupStore.Exists(t.Id));
    }

    [Fact]
    public void Guide_tweaks_toggle_done_marker()
    {
        var g = TweakCatalog.Find("bios.rebar")!;
        Assert.Equal(TweakState.NotApplied, TweakEngine.GetState(g));
        TweakEngine.Apply(g);
        Assert.Equal(TweakState.Applied, TweakEngine.GetState(g));
        TweakEngine.Undo(g);
        Assert.Equal(TweakState.NotApplied, TweakEngine.GetState(g));
    }
}
