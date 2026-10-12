using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>
/// Applies and reverts real per-user (HKCU) catalog tweaks on this PC, then verifies every touched value is exactly
/// restored. Opt-in: set MT_INTEGRATION=1. Never runs in CI.
/// </summary>
[Collection("registry")]
public class IntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-int-" + Guid.NewGuid().ToString("N"));
    private static bool Enabled => Environment.GetEnvironmentVariable("MT_INTEGRATION") == "1";

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static string Snapshot(RegValueOp op)
    {
        using var k = (op.Hive == Hive.HKCU ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(op.Path);
        if (k is null) return "<no key>";
        var v = k.GetValue(op.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return v is null ? "<no value>" : $"{k.GetValueKind(op.Name)}:{(v is byte[] b ? Convert.ToHexString(b) : v)}";
    }

    [Fact]
    public void Hkcu_tweaks_apply_and_restore_exactly()
    {
        if (!Enabled) { output.WriteLine("skipped (set MT_INTEGRATION=1)"); return; }
        AppPaths.OverrideRoot(_tmp);
        // Every catalog tweak made only of HKCU registry values (no admin, no side effects beyond the registry).
        var tweaks = TweakCatalog.All.Where(t => t.Kind == TweakKind.Toggle && t.Ops.All(o => o is RegValueOp { Hive: Hive.HKCU })).ToList();
        Assert.NotEmpty(tweaks);
        int tested = 0;
        foreach (var t in tweaks)
        {
            if (TweakEngine.GetState(t) != TweakState.NotApplied) continue; // only test what we can fully round-trip
            var ops = t.Ops.Cast<RegValueOp>().ToList();
            var before = ops.Select(Snapshot).ToList();
            var a = TweakEngine.Apply(t);
            Assert.True(a.Success, $"{t.Id}: {a.Message}");
            Assert.Equal(TweakState.Applied, TweakEngine.GetState(t));
            var u = TweakEngine.Undo(t);
            Assert.True(u.Success, $"{t.Id}: {u.Message}");
            var after = ops.Select(Snapshot).ToList();
            for (int i = 0; i < ops.Count; i++)
                Assert.True(before[i] == after[i] || (before[i] == "<no key>" && after[i] == "<no value>"),
                    $"{t.Id} {ops[i].Describe()}: before {before[i]} after {after[i]}");
            tested++;
        }
        output.WriteLine($"Round-tripped {tested} of {tweaks.Count} HKCU-only tweaks");
        Assert.True(tested > 0);
    }

    [Fact]
    public void Batched_powershell_checks_match_individual_checks()
    {
        if (!Enabled) return;
        var net = TweakCatalog.All.Where(t => t.Ops.OfType<CommandOp>().Any(o => o.HasSlowCheck)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TweakEngine.PrewarmChecks(net);
        var batched = net.ToDictionary(t => t.Id, TweakEngine.GetState);
        output.WriteLine($"{net.Count} PowerShell-checked tweaks, batched prewarm + states: {sw.ElapsedMilliseconds} ms");
        System.Threading.Thread.Sleep(16000); // let the 15 s cache expire so each check runs on its own
        sw.Restart();
        var single = net.ToDictionary(t => t.Id, TweakEngine.GetState);
        output.WriteLine($"individual checks: {sw.ElapsedMilliseconds} ms");
        foreach (var t in net) Assert.True(batched[t.Id] == single[t.Id], $"{t.Id}: batched {batched[t.Id]} vs single {single[t.Id]}");
    }

    [Fact]
    public void Powercfg_state_is_readable()
    {
        if (!Enabled) return;
        var t = TweakCatalog.Find("gen.usb-suspend")!;
        var s = TweakEngine.GetState(t);
        output.WriteLine($"USB selective suspend tweak state: {s}");
        Assert.True(s is TweakState.Applied or TweakState.NotApplied, s.ToString());
    }
}
