using System.Diagnostics;
using MoneyTweaks.Core.Cleaner;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>
/// Ultimate PC Cleaner safety. Storage: a real temporary folder tree with old/new/hidden/system/read-only/in-use files, a
/// junction pointing outside, and a quarantine in an isolated data folder. Background: the process classifier.
/// </summary>
[Collection("registry")]
public sealed class CleanerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-cleaner-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _data, _root, _outside;
    private readonly string _origRoot = AppPaths.Root;

    public CleanerTests()
    {
        _data = Path.Combine(_dir, "data"); _root = Path.Combine(_dir, "junk"); _outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(_data); Directory.CreateDirectory(_root); Directory.CreateDirectory(_outside);
        AppPaths.OverrideRoot(_data);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_origRoot);
        try { foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { RemoveJunctions(_dir); Directory.Delete(_dir, true); } catch { }
    }

    private static void RemoveJunctions(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).ToList())
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d, false);
    }

    private string Make(string rel, int ageDays, int bytes = 1000, FileAttributes extra = 0, string? root = null)
    {
        var p = Path.Combine(root ?? _root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
        var t = DateTime.Now.AddDays(-ageDays);
        File.SetCreationTime(p, t); File.SetLastWriteTime(p, t);
        if (extra != 0) File.SetAttributes(p, File.GetAttributes(p) | extra);
        return p;
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    private StorageCategory Cat(int minAge = 2) => new("test", "Test junk", "test", StorageKind.Files, true, false, [new(_root, MinAgeDays: minAge)]);

    private async Task<CategoryScan> Scan(params string[] exclusions) =>
        (await StorageCleaner.ScanAsync([Cat()], exclusions, false, null, CancellationToken.None))[0];

    [Fact]
    public async Task Scan_takes_only_old_files_and_never_follows_links_or_takes_system_files()
    {
        var oldA = Make("a.tmp", 5);
        var oldHidden = Make(@"sub\hidden.tmp", 5, extra: FileAttributes.Hidden);
        Make("new.tmp", 0);
        Make("sys.tmp", 9, extra: FileAttributes.System);
        var personal = Make("precious.docx", 30, root: _outside);
        Junction(Path.Combine(_root, "link"), _outside);

        var s = await Scan();
        var paths = s.Files.Select(f => f.Path).ToList();
        Assert.Contains(StorageCleaner.Normalize(oldA), paths);
        Assert.Contains(StorageCleaner.Normalize(oldHidden), paths);       // hidden doesn't make a file junk, nor protect a temp file
        Assert.DoesNotContain(paths, p => p.EndsWith("new.tmp"));
        Assert.DoesNotContain(paths, p => p.EndsWith("sys.tmp"));
        Assert.DoesNotContain(paths, p => p.Contains("precious"));         // behind the junction: never followed
        Assert.True(s.Skipped.ContainsKey("link or junction (not followed)"));
        Assert.True(s.Skipped.ContainsKey("system file"));
        Assert.Equal(2, s.Count);
        Assert.Equal(2000, s.Bytes);

        var r = await StorageCleaner.CleanAsync([s], quarantine: true, [], null, CancellationToken.None);
        Assert.True(File.Exists(personal));                              // outside the location: untouched
        Assert.True(Directory.Exists(Path.Combine(_root, "link")));      // the junction itself is left alone
        Assert.Equal(2, r.Results[0].Quarantined);
    }

    [Fact]
    public async Task A_root_that_is_a_junction_is_refused()
    {
        Make("x.tmp", 9, root: _outside);
        var linkRoot = Path.Combine(_dir, "linkroot");
        Junction(linkRoot, _outside);
        var cat = new StorageCategory("t", "t", "t", StorageKind.Files, true, false, [new(linkRoot, MinAgeDays: 1)]);
        var s = (await StorageCleaner.ScanAsync([cat], [], false, null, CancellationToken.None))[0];
        Assert.Equal(0, s.Count);
        Assert.True(s.Skipped.ContainsKey("location is a link or junction (not followed)"));
    }

    [Fact]
    public async Task Clean_rechecks_everything_and_never_claims_what_did_not_happen()
    {
        var ok = Make("ok.tmp", 5);
        var locked = Make("locked.tmp", 5);
        var changed = Make("changed.tmp", 5);
        var ro = Make("ro.tmp", 5, extra: FileAttributes.ReadOnly);
        var excluded = Make(@"keep\mine.tmp", 5);
        var s = await Scan();
        Assert.Equal(5, s.Count);

        File.AppendAllText(changed, "x");                                // modified after the scan
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // in use
        var r = (await StorageCleaner.CleanAsync([s], quarantine: false, [Path.Combine(_root, "keep")], null, CancellationToken.None)).Results[0];

        Assert.False(File.Exists(ok));
        Assert.Equal(1, r.Deleted);
        Assert.Equal(1000, r.DeletedBytes);
        Assert.True(File.Exists(locked)); Assert.Equal(1, r.Skipped["in use"]);
        Assert.True(File.Exists(changed)); Assert.Equal(1, r.Skipped["changed since the scan"]);
        Assert.True(File.Exists(ro)); Assert.Equal(1, r.Skipped["read-only"]);
        Assert.True(File.Exists(excluded)); Assert.Equal(1, r.Skipped["in your exclusions"]);
        Assert.Equal(0, r.Quarantined);
    }

    [Fact]
    public async Task Quarantine_can_be_restored_or_purged_and_cancel_is_honest()
    {
        var a = Make(@"deep\a.tmp", 5, 3000);
        var b = Make("b.tmp", 5, 2000);
        var s = await Scan();
        var r = await StorageCleaner.CleanAsync([s], quarantine: true, [], null, CancellationToken.None);
        Assert.Equal(5000, r.QuarantinedBytes);
        Assert.Equal(0, r.FreedBytes);                                   // in quarantine, not freed yet: never reported as freed
        Assert.False(File.Exists(a));
        Assert.False(Directory.Exists(Path.Combine(_root, "deep")));     // emptied folder removed
        var session = Assert.Single(StorageCleaner.QuarantineSessions());
        var (restored, problems) = StorageCleaner.Restore(session);
        Assert.Equal(2, restored); Assert.Empty(problems);
        Assert.True(File.Exists(a)); Assert.True(File.Exists(b));
        Assert.Empty(StorageCleaner.QuarantineSessions());

        s = await Scan();
        await StorageCleaner.CleanAsync([s], quarantine: true, [], null, CancellationToken.None);
        var (freed, failed) = StorageCleaner.Purge(StorageCleaner.QuarantineSessions()[0]);
        Assert.Equal(5000, freed); Assert.Equal(0, failed);
        Assert.Empty(StorageCleaner.QuarantineSessions());

        Make("c.tmp", 5);
        s = await Scan();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await StorageCleaner.CleanAsync([s], quarantine: false, [], null, cts.Token);
        Assert.True(cancelled.Cancelled);
        Assert.True(File.Exists(Path.Combine(_root, "c.tmp")));
    }

    [Fact]
    public void Personal_and_system_locations_are_always_denied()
    {
        string P(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        Assert.NotNull(StorageCleaner.Denied(Path.Combine(P(Environment.SpecialFolder.MyDocuments), "x.tmp")));
        Assert.NotNull(StorageCleaner.Denied(Path.Combine(P(Environment.SpecialFolder.UserProfile), "Downloads", "setup.exe")));
        Assert.NotNull(StorageCleaner.Denied(Path.Combine(P(Environment.SpecialFolder.Windows), "WinSxS", "a.dll")));
        Assert.NotNull(StorageCleaner.Denied(Path.Combine(P(Environment.SpecialFolder.Windows), "Installer", "a.msi")));
        Assert.NotNull(StorageCleaner.Denied(Path.Combine(P(Environment.SpecialFolder.Windows), "Prefetch", "A.pf")));
        Assert.NotNull(StorageCleaner.Denied(Path.Combine(P(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default\Cookies")));
        Assert.Null(StorageCleaner.Denied(Path.Combine(Path.GetTempPath(), "x.tmp")));
        // Categories never point at a denied location and never at Prefetch/WinSxS/Installer.
        foreach (var c in StorageCleaner.Categories())
            foreach (var r in c.Roots)
                Assert.Null(StorageCleaner.Denied(StorageCleaner.Normalize(r.Path)));
        Assert.All(StorageCleaner.Categories().Where(c => c.Id is "recycle-bin" or "shader-caches" or "component-store"), c => Assert.False(c.DefaultSelected));
    }

    [Fact]
    public void Process_classifier_protects_what_must_keep_running()
    {
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "x", "app.exe");
        ProcessClass C(string name, string? path = null, bool window = false) => ProcessCleaner.Classify(name, path ?? pf, window).cls;
        foreach (var n in new[] { "MsMpEng", "vgc", "EasyAntiCheat_EOS", "BEService", "audiodg", "obs64", "DS4Windows", "nordvpn-service", "msiexec", "MoneyTweaks", "nvcontainer", "csrss", "Some_Setup_v2" })
            Assert.Equal(ProcessClass.Protected, C(n));
        Assert.Equal(ProcessClass.Protected, C("randomtool", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "randomtool.exe")));
        Assert.Equal(ProcessClass.Protected, ProcessCleaner.Classify("elevatedthing", null, false).cls); // can't verify → left alone
        Assert.Equal(ProcessClass.Protected, ProcessCleaner.Classify("FortniteClient-Win64-Shipping", pf, true, game: "FortniteClient-Win64-Shipping.exe").cls);
        Assert.Equal(ProcessClass.Protected, ProcessCleaner.Classify("chrome", pf, true, exclusions: ["chrome"]).cls);
        Assert.Equal(ProcessClass.Protected, C("msedgewebview2"));       // hosted by other apps
        Assert.Equal(ProcessClass.Protected, C("GameInputRedistService")); // controllers
        Assert.Equal(ProcessClass.Protected, C("MbamBgNativeMsg"));        // security
        Assert.Equal(ProcessClass.ReviewFirst, C("wooting-bg-service"));   // peripheral settings
        Assert.Equal(ProcessClass.SafeToClose, C("WidgetBoard"));
        Assert.Equal(ProcessClass.SafeToClose, C("OneDrive"));
        Assert.Equal(ProcessClass.SafeToClose, C("AdobeARM"));
        Assert.Equal(ProcessClass.ReviewFirst, C("chrome", window: true));
        Assert.Equal(ProcessClass.ReviewFirst, C("Discord"));
        Assert.Equal(ProcessClass.ReviewFirst, C("steam"));
        Assert.Equal(ProcessClass.ReviewFirst, C("someapp", window: true));
        // Game Booster uses the same protection.
        Assert.Empty(GameBooster.CloseTargets(["vgc", "obs64", "EasyAntiCheat", "audiodg"], null));
        Assert.Equal(["OneDrive"], GameBooster.CloseTargets(["OneDrive", "MsMpEng"], null));
    }

    [Fact]
    public async Task Closing_is_polite_first_force_only_when_allowed_and_results_are_verified()
    {
        // A copy of ping.exe outside the Windows folder, under a unique name: a windowless background process we own.
        var exe = Path.Combine(_dir, "mtclosetest.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        Process Start() => Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        using var p1 = Start();
        await Task.Delay(500);
        var noForce = await ProcessCleaner.CleanAsync(["mtclosetest"], force: false, "test", null, []);
        Assert.Equal(CloseOutcome.StillRunning, noForce.Results[0].Outcome);   // no window to ask, force off: left running
        Assert.False(p1.HasExited);
        var forced = await ProcessCleaner.CleanAsync(["mtclosetest"], force: true, "test", null, []);
        Assert.Equal(CloseOutcome.ForceClosed, forced.Results[0].Outcome);
        Assert.True(p1.WaitForExit(3000));
        Assert.True(forced.Before.RamTotalGb > 0 && forced.After.RamTotalGb > 0); // measured, not assumed
        var again = await ProcessCleaner.CleanAsync(["mtclosetest", "MsMpEng"], force: true, "test", null, []);
        Assert.Equal(CloseOutcome.NotRunning, again.Results[0].Outcome);
        Assert.Contains(again.Results, r => r.Name == "MsMpEng" && r.Outcome is CloseOutcome.Refused or CloseOutcome.NotRunning);
    }

    [Fact]
    public async Task Listing_measures_this_users_real_processes()
    {
        var apps = await ProcessCleaner.ListAsync([], null, TimeSpan.FromMilliseconds(300));
        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.True(a.RamMb >= 0 && a.CpuPercent >= 0 && a.Pids.Count > 0));
        Assert.Contains(apps, a => a.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase) && a.Class == ProcessClass.Protected);
        Assert.DoesNotContain(apps, a => a.Pids.Contains(Environment.ProcessId));
    }
}
