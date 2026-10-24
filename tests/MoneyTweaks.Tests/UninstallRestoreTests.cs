using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Uninstall restore: what each pass does, what it leaves for administrator rights, and exact restores.</summary>
[Collection("registry")]
public class UninstallRestoreTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-uninst-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Nothing_applied_means_nothing_to_do()
    {
        AppPaths.OverrideRoot(_tmp);
        var r = UninstallRestore.Run(adminPass: false);
        Assert.Empty(r.Items);
        Assert.False(UninstallRestore.NeedsAdminPass(r));
    }

    [Fact]
    public void System_wide_tweaks_wait_for_the_admin_pass_and_unknown_backups_are_kept()
    {
        if (Admin.IsElevated) { output.WriteLine("skipped: elevated, the admin tweak would really be undone"); return; }
        AppPaths.OverrideRoot(_tmp);
        var adminTweak = TweakCatalog.All.First(t => t.RequiresAdmin && t.Kind == TweakKind.Toggle);
        BackupStore.Save(new TweakBackup { TweakId = adminTweak.Id, AppliedAt = DateTime.Now });
        BackupStore.Save(new TweakBackup { TweakId = "removed.in-a-later-version", AppliedAt = DateTime.Now });
        var autoStartBefore = AutoStart.Current();

        var r = UninstallRestore.Run(adminPass: false);

        var admin = Assert.Single(r.Items, i => i.Name == adminTweak.Name);
        Assert.False(admin.Ok);
        Assert.True(admin.NeedsAdmin);
        Assert.True(UninstallRestore.NeedsAdminPass(r));
        Assert.True(BackupStore.Exists(adminTweak.Id));                  // kept for the elevated pass (or a reinstall)
        var gone = Assert.Single(r.Items, i => i.Name == "removed.in-a-later-version");
        Assert.False(gone.Ok);
        Assert.True(BackupStore.Exists("removed.in-a-later-version"));
        Assert.Equal(autoStartBefore, AutoStart.Current());                 // a test host never touches the real startup entry

        // The elevated pass's results replace the "needs admin" entries in the final report.
        var adminReport = new RestoreReport { Items = [new("Tweak", adminTweak.Name, true, "Reverted and verified against the backup.")] };
        var back = RestoreReport.FromJson(adminReport.ToJson())!;
        r.Merge(back);
        Assert.DoesNotContain(r.Items, i => i.NeedsAdmin);
        Assert.Single(r.Remaining);                                         // only the unknown tweak is left
    }

    private static string Snapshot(RegValueOp op)
    {
        using var k = (op.Hive == Hive.HKCU ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(op.Path);
        if (k is null) return "<no key>";
        var v = k.GetValue(op.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return v is null ? "<no value>" : $"{k.GetValueKind(op.Name)}:{(v is byte[] b ? Convert.ToHexString(b) : v)}";
    }

    /// <summary>Real per-user tweaks on this PC: applied, then undone by the uninstall restore, every value exactly back. Opt-in (MT_INTEGRATION=1).</summary>
    [Fact]
    public void Uninstall_restore_puts_applied_tweaks_back_exactly()
    {
        if (Environment.GetEnvironmentVariable("MT_INTEGRATION") != "1") { output.WriteLine("skipped (set MT_INTEGRATION=1)"); return; }
        AppPaths.OverrideRoot(_tmp);
        var tweaks = TweakCatalog.All
            .Where(t => t.Kind == TweakKind.Toggle && t.Ops.All(o => o is RegValueOp { Hive: Hive.HKCU }) && TweakEngine.GetState(t) == TweakState.NotApplied)
            .Take(6).ToList();
        Assert.NotEmpty(tweaks);
        var before = tweaks.ToDictionary(t => t.Id, t => t.Ops.Cast<RegValueOp>().Select(Snapshot).ToList());
        foreach (var t in tweaks)
        {
            var a = TweakEngine.Apply(t);
            Assert.True(a.Success, $"{t.Id}: {a.Message}");
            Thread.Sleep(20);   // distinct AppliedAt times, so the newest-first order is exercised
        }

        var r = UninstallRestore.Run(adminPass: false);

        output.WriteLine(string.Join("\n", r.Items.Select(i => $"{i.Name}: {i.Ok} {i.Message}")));
        Assert.Equal(tweaks.Count, r.Items.Count(i => i.Area == "Tweak" && i.Ok));
        // Newest first: the last tweak applied is the first undone.
        Assert.Equal(tweaks.Select(t => t.Name).Reverse(), r.Items.Where(i => i.Area == "Tweak").Select(i => i.Name));
        Assert.False(UninstallRestore.NeedsAdminPass(r));
        foreach (var t in tweaks)
        {
            Assert.Equal(TweakState.NotApplied, TweakEngine.GetState(t));
            Assert.False(BackupStore.Exists(t.Id));
            var after = t.Ops.Cast<RegValueOp>().Select(Snapshot).ToList();
            for (int i = 0; i < after.Count; i++)
                Assert.True(before[t.Id][i] == after[i] || (before[t.Id][i] == "<no key>" && after[i] == "<no value>"), $"{t.Id}: before {before[t.Id][i]} after {after[i]}");
        }
    }
}
