using Microsoft.Win32;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

[Collection("registry")]
public class VerificationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-ver-" + Guid.NewGuid().ToString("N"));
    private const string TestKey = @"Software\MoneyTweaksTests\Verify";

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private void Isolate() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }

    [Fact]
    public void Every_tweak_verifies_read_only()
    {
        Isolate();
        TweakEngine.PrewarmChecks(TweakCatalog.All);
        var reports = TweakCatalog.All.AsParallel().WithDegreeOfParallelism(4).Select(TweakVerifier.Verify).ToList();
        Assert.Equal(TweakCatalog.All.Count, reports.Count);
        Assert.Empty(BackupStore.AllIds()); // verification never applies anything
        foreach (var g in reports.GroupBy(r => r.Status)) output.WriteLine($"{g.Key}: {g.Count()}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt-audit-latest.txt"), string.Join(Environment.NewLine + Environment.NewLine, reports.OrderBy(r => r.Status).Select(r => r.ToText())));
        foreach (var r in reports.Where(r => r.Status is VerifyStatus.Fail or VerifyStatus.Warning).Take(60)) output.WriteLine(r.ToText());
        Assert.All(reports.Where(r => r.Tweak.Kind == TweakKind.Toggle && r.State != TweakState.NotApplicable), r => Assert.NotEmpty(r.Evidence));
        // A tweak that is off and has no backup must never be reported as a failure.
        Assert.DoesNotContain(reports, r => r.Status == VerifyStatus.Fail && r.State == TweakState.NotApplied && !BackupStore.Exists(r.Tweak.Id));
    }

    /// <summary>Opt-in (MT_AUDIT_REAL=1): read-only audit against this PC's real Money Tweaks backups.</summary>
    [Fact]
    public void Real_audit_report()
    {
        if (Environment.GetEnvironmentVariable("MT_AUDIT_REAL") != "1") { output.WriteLine("skipped (set MT_AUDIT_REAL=1)"); return; }
        AppPaths.OverrideRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoneyTweaks"));
        var before = BackupStore.AllIds().OrderBy(x => x).ToList();
        TweakEngine.PrewarmChecks(TweakCatalog.All);
        var reports = TweakCatalog.All.AsParallel().WithDegreeOfParallelism(4).Select(TweakVerifier.Verify).ToList();
        Assert.Equal(before, BackupStore.AllIds().OrderBy(x => x).ToList());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt-audit-real.txt"), string.Join(Environment.NewLine + Environment.NewLine, reports.OrderBy(r => r.Status).Select(r => r.ToText())));
    }

    [Fact]
    public void Security_tweak_is_disabled_and_not_in_presets()
    {
        var hvci = TweakCatalog.Find("adv.vbs-hvci")!;
        Assert.NotNull(TweakAudit.DisabledReason(hvci));
        var r = TweakEngine.Apply(hvci);
        Assert.False(r.Success);
        Assert.Contains("security", r.Message, StringComparison.OrdinalIgnoreCase);
        foreach (var p in PresetStore.BuiltIns)
            Assert.DoesNotContain(p.TweakIds, id => TweakCatalog.Find(id) is { } t && TweakAudit.DisabledReason(t) is not null);
        Assert.False(TweakCatalog.Find("net.nagle")!.Recommended);
        Assert.DoesNotContain("net.nagle", PresetStore.BuiltIns.First(p => p.Id == "builtin.competitive-fortnite").TweakIds);
    }

    [Fact]
    public void Audit_finds_dns_conflicts_but_not_unrelated_bcdedit_checks()
    {
        var dns = TweakAudit.IssuesFor(TweakCatalog.Find("net.dns-cloudflare")!);
        Assert.Contains(dns, i => i.Kind == AuditKind.Conflict && i.Other == "net.dns-google");
        var hpet = TweakAudit.IssuesFor(TweakCatalog.Find("adv.timer-hpet")!);
        Assert.DoesNotContain(hpet, i => i.Other == "adv.timer-dynamic-tick");
        // Every command op that is not a pure fix-up has a read-back now.
        var noCheck = TweakCatalog.All.Where(t => t.Ops.Any(o => o is CommandOp { HasCheck: false })).Select(t => t.Id).ToList();
        output.WriteLine("Command tweaks without read-back: " + string.Join(", ", noCheck));
        Assert.Empty(noCheck);
    }

    /// <summary>An op that "succeeds" but Windows doesn't keep, like a policy-managed value.</summary>
    private sealed class RevertedOp : TweakOp
    {
        public int Applied, Undone;
        public override bool RequiresAdmin => false;
        public override string Describe() => "fake";
        public override OpState Check() => OpState.NotApplied;
        public override OpBackup Capture() => new() { Kind = "fake" };
        public override void Apply() => Applied++;
        public override void Undo(OpBackup? backup) => Undone++;
    }

    [Fact]
    public void Apply_rolls_back_when_read_back_does_not_match()
    {
        Isolate();
        var op = new RevertedOp();
        var t = new Tweak { Id = "test.reverted", Category = TweakCategory.General, Name = "x", Description = "x", Ops = [op] };
        var r = TweakEngine.Apply(t);
        Assert.False(r.Success);
        Assert.Contains("rolled back", r.Message);
        Assert.Equal(1, op.Applied);
        Assert.Equal(1, op.Undone);
        Assert.False(BackupStore.Exists(t.Id));
    }

    [Fact]
    public void Round_trip_restores_exact_original_and_undo_detects_drift()
    {
        Isolate();
        using (var k = Registry.CurrentUser.CreateSubKey(TestKey)) k.SetValue("Existing", 7, RegistryValueKind.DWord);
        var t = new Tweak
        {
            Id = "test.roundtrip", Category = TweakCategory.General, Name = "rt", Description = "rt",
            Ops = [new RegValueOp(Hive.HKCU, TestKey, "Existing", RegistryValueKind.DWord, 1), new RegValueOp(Hive.HKCU, TestKey, "New", RegistryValueKind.String, "on")],
        };
        var report = TweakVerifier.RoundTrip(t);
        output.WriteLine(report.ToText());
        Assert.Equal(VerifyStatus.Pass, report.Status);
        using (var k = Registry.CurrentUser.OpenSubKey(TestKey)!)
        {
            Assert.Equal(7, k.GetValue("Existing"));
            Assert.Null(k.GetValue("New"));
        }

        // Apply, then something else changes the value after Undo restores it: the restore check must notice.
        Assert.True(TweakEngine.Apply(t).Success);
        var backup = BackupStore.Load(t.Id);
        Assert.True(TweakEngine.Undo(t).Success);
        using (var k = Registry.CurrentUser.OpenSubKey(TestKey, true)!) k.SetValue("Existing", 9, RegistryValueKind.DWord);
        var diffs = TweakVerifier.RestoreMismatches(t, backup);
        Assert.Single(diffs);
        Assert.Contains("backup had 7", diffs[0]);
    }

    [Fact]
    public void Verify_reports_fail_when_an_applied_tweak_was_reverted_externally()
    {
        Isolate();
        var t = new Tweak { Id = "test.ext", Category = TweakCategory.General, Name = "ext", Description = "ext", Ops = [new RegValueOp(Hive.HKCU, TestKey, "V", RegistryValueKind.DWord, 1)] };
        Assert.True(TweakEngine.Apply(t).Success);
        Assert.Equal(VerifyStatus.Pass, TweakVerifier.Verify(t).Status);
        using (var k = Registry.CurrentUser.OpenSubKey(TestKey, true)!) k.SetValue("V", 0, RegistryValueKind.DWord); // "Windows Update" put it back
        var r = TweakVerifier.Verify(t);
        Assert.Equal(VerifyStatus.Fail, r.Status);
        Assert.Contains(r.Evidence, e => e.Contains("reads 0") && e.Contains("target 1"));
        TweakEngine.Undo(t);
    }

    [Fact]
    public void Store_round_trip_and_exports()
    {
        Isolate();
        var f = new BenchmarkFile();
        var s = new BenchmarkStudy { Name = "S, \"quoted\"", TargetName = "T" };
        f.Studies.Add(s);
        f.Runs.Add(new BenchmarkRun { StudyId = s.Id, Phase = "Baseline", CpuAvg = 12.5, PingAvgMs = 20, Frames = new FrameStats { Source = "Manual entry", AvgFps = 144 } });
        BenchmarkStore.Save(f);
        var g = BenchmarkStore.Load();
        Assert.Equal(12.5, g.Runs.Single().CpuAvg);
        Assert.Equal(144, g.Runs.Single().Frames!.AvgFps);
        var csv = Path.Combine(_tmp, "b.csv");
        BenchmarkStore.ExportCsv(g, g.Studies, csv);
        var lines = File.ReadAllLines(csv);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"S, \"\"quoted\"\"\"", lines[1]);
        var json = Path.Combine(_tmp, "b.json");
        BenchmarkStore.ExportJson(g, g.Studies, json);
        Assert.Contains("money-tweaks-benchmarks", File.ReadAllText(json));
    }
}

public class BenchmarkStatsTests
{
    private static BenchmarkRun R(double ping, string phase = "Baseline") => new() { Phase = phase, PingAvgMs = ping, Conditions = new() { DurationSec = 30, Endpoint = "1.1.1.1", ProcessCount = 200 } };

    [Fact]
    public void Compare_is_conclusive_only_beyond_run_to_run_variation()
    {
        var ping = BenchmarkStats.Metrics.First(m => m.Key == "ping");
        var clear = BenchmarkStats.Compare(ping, [R(30), R(31), R(30.5)], [R(20, "After"), R(21, "After"), R(20.4, "After")]);
        Assert.Equal(Verdict.Better, clear.Verdict);
        Assert.True(clear.Pct < -30);
        var noisy = BenchmarkStats.Compare(ping, [R(30), R(45), R(20)], [R(28, "After"), R(40, "After"), R(22, "After")]);
        Assert.Equal(Verdict.NoClearChange, noisy.Verdict);
        var tiny = BenchmarkStats.Compare(ping, [R(30), R(30)], [R(29.5, "After"), R(29.5, "After")]); // below 1 ms noise floor
        Assert.Equal(Verdict.NoClearChange, tiny.Verdict);
        var single = BenchmarkStats.Compare(ping, [R(30)], [R(10, "After")]);
        Assert.Equal(Verdict.NotEnoughData, single.Verdict);
        var worse = BenchmarkStats.Compare(ping, [R(20), R(20.2)], [R(30, "After"), R(30.3, "After")]);
        Assert.Equal(Verdict.Worse, worse.Verdict);
    }

    [Fact]
    public void Condition_warnings_flag_unfair_comparisons()
    {
        var a = R(20); var b = R(20, "After");
        b.Conditions.DurationSec = 60;
        b.Conditions.FortniteRunning = true;
        var w = BenchmarkStats.ConditionWarnings([a], [b]);
        Assert.Contains(w, x => x.Contains("durations"));
        Assert.Contains(w, x => x.Contains("Fortnite"));
        Assert.Empty(BenchmarkStats.ConditionWarnings([R(20)], [R(21, "After")]));
    }

    [Fact]
    public void PresentMon_csv_is_parsed_per_process()
    {
        var path = Path.Combine(Path.GetTempPath(), "mt-pm-" + Guid.NewGuid().ToString("N") + ".csv");
        var lines = new List<string> { "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,MsBetweenPresents" };
        for (int i = 0; i < 990; i++) lines.Add($"FortniteClient-Win64-Shipping.exe,1,0x1,DXGI,0,0,0,{(i * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture)},10.0");
        for (int i = 0; i < 10; i++) lines.Add($"FortniteClient-Win64-Shipping.exe,1,0x1,DXGI,0,0,0,{(10 + i * 0.02).ToString(System.Globalization.CultureInfo.InvariantCulture)},20.0");
        for (int i = 0; i < 2000; i++) lines.Add($"dwm.exe,2,0x2,DXGI,0,0,0,{(i * 0.016).ToString(System.Globalization.CultureInfo.InvariantCulture)},16.6");
        File.WriteAllLines(path, lines);
        try
        {
            var (s, proc) = FrameTimeCsv.Parse(path);
            Assert.Equal("FortniteClient-Win64-Shipping.exe", proc);
            Assert.Equal(1000, s.Frames);
            Assert.Equal(1000 / 10.1, s.AvgFps, 1);
            Assert.InRange(s.Low1Fps!.Value, 49, 100); // the 20 ms frames dominate the 99th percentile
            Assert.NotNull(s.Low01Fps);
        }
        finally { File.Delete(path); }
        Assert.Throws<InvalidDataException>(() =>
        {
            var p2 = Path.GetTempFileName();
            try { File.WriteAllText(p2, "a,b\n1,2\n"); FrameTimeCsv.Parse(p2); } finally { File.Delete(p2); }
        });
    }

    [Fact]
    public async Task Sampler_measures_real_counters_and_latency()
    {
        var ticks = 0;
        var run = await BenchmarkSampler.RunAsync("s", "Baseline", 5, "127.0.0.1", "unit test", new Progress<BenchmarkTick>(_ => Interlocked.Increment(ref ticks)), CancellationToken.None);
        Assert.NotNull(run.CpuAvg);
        Assert.Equal(5, run.CpuSeries.Count);
        Assert.NotNull(run.RamUsedGb);
        Assert.True(run.PingSent >= 4, $"pings {run.PingSent}"); // ~10 in 5 s when idle; CI runners can be slower
        Assert.Equal(0, run.LossPct);
        Assert.NotNull(run.PingAvgMs);
        Assert.False(string.IsNullOrEmpty(run.Conditions.PowerPlan));
        Assert.Equal("127.0.0.1", run.Conditions.Endpoint);
    }
}

[Collection("registry")]
public class TweakStateGuardTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-guard-" + Guid.NewGuid().ToString("N"));
    private const string Key = @"Software\MoneyTweaksTests\Guard";
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static Tweak T(string id, string name) => new() { Id = id, Category = TweakCategory.General, Name = id, Description = id, Ops = [new RegValueOp(Hive.HKCU, Key, name, RegistryValueKind.DWord, 1)] };

    [Fact]
    public void Switches_both_ways_and_restores_original_states_and_values()
    {
        Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp);
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("A", 5, RegistryValueKind.DWord);
        var a = T("t.a", "A"); var b = T("t.b", "B");
        var map = new Dictionary<string, Tweak> { [a.Id] = a, [b.Id] = b };
        Assert.True(TweakEngine.Apply(b).Success);                       // b was already on before the test
        var original = TweakStateGuard.Capture([a, b]);
        Assert.Equal(new Dictionary<string, bool> { ["t.a"] = false, ["t.b"] = true }, original);
        TweakStateGuard.SavePending(original);
        Assert.Empty(TweakStateGuard.SetAll(original.Keys, true, map.GetValueOrDefault));
        Assert.Equal(TweakState.Applied, TweakEngine.GetState(a));
        Assert.Empty(TweakStateGuard.SetAll(original.Keys, false, map.GetValueOrDefault));
        Assert.Equal(TweakState.NotApplied, TweakEngine.GetState(b));
        Assert.Equal(original, TweakStateGuard.LoadPending());
        Assert.Empty(TweakStateGuard.Restore(original, map.GetValueOrDefault));
        Assert.Equal(TweakState.NotApplied, TweakEngine.GetState(a));
        Assert.Equal(TweakState.Applied, TweakEngine.GetState(b));
        using (var k = Registry.CurrentUser.OpenSubKey(Key)!) Assert.Equal(5, k.GetValue("A")); // A's original value is back
        TweakStateGuard.ClearPending();
        Assert.Null(TweakStateGuard.LoadPending());
        TweakEngine.Undo(b);
    }

    [Fact]
    public void Settings_made_outside_money_tweaks_are_left_alone()
    {
        Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp);
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("C", 1, RegistryValueKind.DWord); // user set it, no backup
        var c = T("t.c", "C");
        Assert.Equal(TweakState.Applied, TweakEngine.GetState(c));
        var skipped = new List<string>();
        var original = TweakStateGuard.Capture([c], skipped);
        Assert.Empty(original);
        Assert.Contains(skipped, s => s.Contains("outside Money Tweaks"));
    }

    [Fact]
    public void Reboot_and_disabled_tweaks_are_not_switched()
    {
        Assert.False(TweakStateGuard.Eligible(TweakCatalog.Find("net.nagle")!, out var why));
        Assert.Contains("restart", why);
        Assert.False(TweakStateGuard.Eligible(TweakCatalog.Find("adv.vbs-hvci")!, out _));
        Assert.True(TweakStateGuard.Eligible(TweakCatalog.Find("gen.game-mode")!, out _));
    }
}
