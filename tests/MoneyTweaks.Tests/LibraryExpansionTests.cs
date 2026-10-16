using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

[Collection("registry")]
public class LibraryExpansionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-lib-" + Guid.NewGuid().ToString("N"));
    private const string Key = @"Software\MoneyTweaksTests\Flags";
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact]
    public void String_flag_op_changes_only_its_entry_and_restores_exactly()
    {
        Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp);
        Assert.Equal("A=1;B=2;", RegStringFlagOp.With("A=1;B=0;", "B", "2"));
        Assert.Equal("A=1;B=2;", RegStringFlagOp.With("A=1", "B", "2"));
        Assert.Equal("", RegStringFlagOp.With("B=2;", "B", null));
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("S", "VRROptimizeEnable=0;AutoHDREnable=1;", RegistryValueKind.String);
        var t = new Tweak { Id = "t.flag", Category = TweakCategory.Advanced, Name = "f", Description = "f", Ops = [new RegStringFlagOp(Hive.HKCU, Key, "S", "SwapEffectUpgradeEnable", "1")] };
        Assert.True(TweakEngine.Apply(t).Success);
        using (var k = Registry.CurrentUser.OpenSubKey(Key)!) Assert.Equal("VRROptimizeEnable=0;AutoHDREnable=1;SwapEffectUpgradeEnable=1;", k.GetValue("S"));
        Assert.Equal(VerifyStatus.Pass, TweakVerifier.Verify(t).Status);
        Assert.True(TweakEngine.Undo(t).Success);
        using (var k = Registry.CurrentUser.OpenSubKey(Key)!) Assert.Equal("VRROptimizeEnable=0;AutoHDREnable=1;", k.GetValue("S"));
        // Value didn't exist before: Undo removes it again.
        Registry.CurrentUser.DeleteSubKeyTree(Key);
        Assert.Equal(VerifyStatus.Pass, TweakVerifier.RoundTrip(t).Status);
        using (var k = Registry.CurrentUser.OpenSubKey(Key)) Assert.Null(k?.GetValue("S"));
    }

    [Fact]
    public void New_tweaks_exist_verify_read_only_and_have_details()
    {
        Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp);
        string[] ids = ["adv.windowed-optimizations", "gpu.fortnite-high-performance", "net.eee-off", "gen.pcie-aspm-off", "gen.disk-idle-off", "gen.toasts-off"];
        foreach (var id in ids)
        {
            var t = TweakCatalog.Find(id);
            Assert.NotNull(t);
            var r = TweakVerifier.Verify(t!);
            output.WriteLine(r.ToText());
            Assert.NotEqual(VerifyStatus.Fail, r.Status);
            if (id is "gen.pcie-aspm-off" or "gen.disk-idle-off") Assert.NotEqual(TweakState.NotApplicable, r.State); // the power setting exists in every plan
        }
        output.WriteLine("Fortnite exe: " + (TweakCatalog.FortniteExePath() ?? "(not found)"));
        output.WriteLine("EEE adapters: " + TweakCatalog.NicKeysWith("*EEE").Count());
        Assert.Empty(BackupStore.AllIds());
    }

    [Fact]
    public void Every_tweak_has_complete_details_and_ids_are_unique()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in TweakCatalog.All)
        {
            var f = TweakInfo.For(t);
            Assert.False(string.IsNullOrWhiteSpace(f.Benefit), t.Id);
            Assert.Contains(f.Confidence, new[] { "High", "Medium", "Low", "n/a" });
            Assert.DoesNotContain("nothing (no read-back exists)", f.Verification);
            Assert.False(string.IsNullOrWhiteSpace(f.Undo), t.Id);
        }
        output.WriteLine($"Catalog: {TweakCatalog.All.Count} entries, {TweakCatalog.All.Count(t => t.Kind == TweakKind.Toggle)} switchable");
    }

    [Fact]
    public void Updater_compares_double_digit_versions_numerically()
    {
        Assert.True(UpdateService.IsNewer(UpdateService.ParseVersion("v1.10.0"), new Version(1, 9, 0)));
        Assert.False(UpdateService.IsNewer(UpdateService.ParseVersion("v1.9.0"), new Version(1, 10, 0)));
        Assert.False(UpdateService.IsNewer(UpdateService.ParseVersion("v1.10.0"), new Version(1, 10, 0, 0)));
    }
}
