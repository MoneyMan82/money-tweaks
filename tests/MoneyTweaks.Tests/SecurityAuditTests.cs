using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Macros;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>
/// Regression tests for the security/safety audit. Everything here uses temp folders and a scratch HKCU key
/// (Software\MoneyTweaksTests); nothing touches real Windows settings, services, drivers or processes.
/// </summary>
[Collection("registry")]
public class SecurityAuditTests : IDisposable
{
    private const string Scratch = @"Software\MoneyTweaksTests\Audit";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-audit-" + Guid.NewGuid().ToString("N"));

    public SecurityAuditTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static Tweak ScratchTweak() => new()
    {
        Id = "test.audit", Category = TweakCategory.General, Name = "audit", Description = "",
        Ops = [new RegValueOp(Hive.HKCU, Scratch, "V", RegistryValueKind.DWord, 1)],
    };

    // ---- backups ------------------------------------------------------------------------------------------------

    [Fact]
    public void Damaged_backup_blocks_apply_and_undo_and_is_left_untouched()
    {
        var t = ScratchTweak();
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("V", 7, RegistryValueKind.DWord); // the user's "original"
        var file = Path.Combine(AppPaths.Backups, t.Id + ".json");
        File.WriteAllText(file, "{ \"TweakId\": \"test.au"); // truncated by a crash

        var apply = TweakEngine.Apply(t);
        Assert.False(apply.Success);
        Assert.Contains("damaged", apply.Message);
        var undo = TweakEngine.Undo(t);
        Assert.False(undo.Success);
        Assert.Equal("{ \"TweakId\": \"test.au", File.ReadAllText(file)); // not overwritten
        using var k2 = Registry.CurrentUser.OpenSubKey(Scratch)!;
        Assert.Equal(7, k2.GetValue("V")); // neither the tweak nor a "default" was written
    }

    [Fact]
    public void Apply_then_undo_restores_the_original_through_an_atomic_backup()
    {
        var t = ScratchTweak();
        using (var k = Registry.CurrentUser.CreateSubKey(Scratch)) k.SetValue("V", 7, RegistryValueKind.DWord);
        Assert.True(TweakEngine.Apply(t).Success);
        Assert.Empty(Directory.GetFiles(AppPaths.Backups, "*.tmp")); // no temp files left behind
        Assert.True(BackupStore.TryLoad(t.Id, out var b) && b is not null);
        Assert.True(TweakEngine.Undo(t).Success);
        using var k2 = Registry.CurrentUser.OpenSubKey(Scratch)!;
        Assert.Equal(7, k2.GetValue("V"));
    }

    [Fact]
    public void Atomic_write_replaces_whole_file_and_leaves_no_temp_files()
    {
        var f = Path.Combine(_tmp, "x.json");
        AtomicFile.WriteAllText(f, new string('a', 10_000));
        AtomicFile.WriteAllText(f, "short");
        Assert.Equal("short", File.ReadAllText(f));
        Assert.Single(Directory.GetFiles(_tmp));
    }

    [Fact]
    public void Undo_of_a_created_marker_key_also_removes_the_empty_parent_only()
    {
        var path = Scratch + @"\{00000000-test}\InprocServer32";
        var op = new RegValueOp(Hive.HKCU, path, "", RegistryValueKind.String, "") { RemoveKeyIfCreated = true };
        var b = op.Capture();
        op.Apply();
        op.Undo(b);
        Assert.Null(Registry.CurrentUser.OpenSubKey(Scratch + @"\{00000000-test}"));
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(Scratch)); // grandparent untouched
    }

    // ---- security-weakening tweak stays blocked --------------------------------------------------------------------

    [Fact]
    public void Memory_integrity_tweak_is_never_applied()
    {
        var hvci = TweakCatalog.Find("adv.vbs-hvci")!;
        Assert.NotNull(TweakAudit.DisabledReason(hvci)); // TweakEngine.Apply returns before touching anything
    }

    [Fact]
    public void Dynamic_tick_check_does_not_count_No_as_applied()
    {
        var rx = new System.Text.RegularExpressions.Regex(TweakCatalog.DynamicTickOnPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Matches(rx, "identifier {current}\r\ndisabledynamictick      Yes\r\n");
        Assert.Matches(rx, "disabledynamictick      Ja\r\n");
        Assert.DoesNotMatch(rx, "disabledynamictick      No\r\n");
        Assert.DoesNotMatch(rx, "identifier {current}\r\nnx OptIn\r\n");
    }

    // ---- updater ------------------------------------------------------------------------------------------------

    [Fact]
    public void Update_urls_must_be_this_repositorys_github_releases()
    {
        Assert.True(UpdateService.IsTrustedDownloadUrl($"https://github.com/{UpdateService.Repo}/releases/download/v1.42.0/MoneyTweaks-Setup.exe"));
        Assert.False(UpdateService.IsTrustedDownloadUrl($"http://github.com/{UpdateService.Repo}/releases/download/v1/x.exe"));
        Assert.False(UpdateService.IsTrustedDownloadUrl("https://github.com/someone-else/repo/releases/download/v1/x.exe"));
        Assert.False(UpdateService.IsTrustedDownloadUrl("https://evil.example/MoneyMan82/money-tweaks/releases/download/x.exe"));
        Assert.False(UpdateService.IsTrustedDownloadUrl(null));
        Assert.True(UpdateService.IsTrustedPageUrl($"https://github.com/{UpdateService.Repo}/releases/tag/v1.42.0"));
        Assert.False(UpdateService.IsTrustedPageUrl(@"C:\Windows\System32\calc.exe"));
        Assert.False(UpdateService.IsTrustedPageUrl("file:///C:/x.exe"));
        Assert.Equal("v1.42.0", UpdateService.SafeTag("v1.42.0"));
        Assert.DoesNotContain("..", UpdateService.SafeTag(@"..\..\Windows"));
        Assert.DoesNotContain("\\", UpdateService.SafeTag(@"..\..\Windows"));
    }

    [Fact]
    public void Update_file_is_rechecked_and_locked_before_it_is_started()
    {
        var f = Path.Combine(_tmp, "MoneyTweaks-Setup.exe");
        File.WriteAllBytes(f, [1, 2, 3, 4]);
        Assert.Throws<InvalidOperationException>(() => UpdateService.OpenVerified(f)); // never verified
        UpdateService.MarkVerifiedForTests(f);
        using (UpdateService.OpenVerified(f))
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(f, [9])); // swapping it while held is impossible
        File.WriteAllBytes(f, [9, 9, 9]); // swapped after verification
        Assert.Throws<InvalidOperationException>(() => UpdateService.OpenVerified(f));
    }

    // ---- health links -------------------------------------------------------------------------------------------

    [Fact]
    public void Health_links_only_open_web_settings_and_known_tools()
    {
        Assert.True(HealthLink.IsAllowedTarget("https://www.nvidia.com/en-us/drivers/"));
        Assert.True(HealthLink.IsAllowedTarget("ms-settings:windowsupdate"));
        Assert.True(HealthLink.IsAllowedTarget("devmgmt.msc"));
        Assert.True(HealthLink.IsAllowedTarget("perfmon.exe /rel"));
        Assert.False(HealthLink.IsAllowedTarget("cmd.exe /c del C:\\x"));
        Assert.False(HealthLink.IsAllowedTarget(@"C:\Users\x\Downloads\evil.exe"));
        Assert.False(HealthLink.IsAllowedTarget("file:///C:/evil.exe"));
        Assert.False(HealthLink.IsAllowedTarget("http://insecure.example"));
        Assert.False(HealthLink.IsAllowedTarget(null));
    }

    // ---- game booster -------------------------------------------------------------------------------------------

    [Fact]
    public void Booster_never_closes_protected_processes_or_the_game_itself()
    {
        var t = GameBooster.CloseTargets(["Discord", "discord.exe", "explorer", "lsass", "MsMpEng", "FortniteClient-Win64-Shipping", " ", "Spotify.exe"],
            "FortniteClient-Win64-Shipping");
        Assert.Equal(["Discord", "Spotify"], t);
    }

    // ---- macros -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Held_key_is_released_when_a_blocked_game_takes_focus_mid_step()
    {
        var sink = new RecordingSink();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string Foreground() => sw.ElapsedMilliseconds > 150 ? "FortniteClient-Win64-Shipping" : "notepad";
        var m = new Macro { Name = "walk", Steps = [new MacroStep { Kind = StepKind.KeyDown, Key = "W" }, new MacroStep { Kind = StepKind.Delay, Ms = 60_000 }] };
        var player = new MacroPlayer(sink, Foreground);
        var reason = await player.RunAsync(m, CancellationToken.None);
        Assert.Equal(StopReason.BlockedGame, reason);
        Assert.Equal("Fortnite", player.BlockedGame);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms"); // not the full 60 s
        Assert.Equal("W:up", sink.Snapshot()[^1]); // the key was let go
    }

    // ---- NVIDIA Profile Inspector -------------------------------------------------------------------------------

    [Fact]
    public void Inspector_runs_only_if_it_is_the_exact_file_the_user_chose()
    {
        var exe = Path.Combine(_tmp, "nvidiaProfileInspector.exe");
        File.WriteAllBytes(exe, [1, 2, 3]);
        var st = new NvProfileState();
        Assert.False(NvidiaProfilePresets.InspectorTrusted(st, exe)); // never chosen
        st.InspectorPath = exe; // a path remembered by an older version, without a hash
        Assert.False(NvidiaProfilePresets.InspectorTrusted(st, exe));
        NvidiaProfilePresets.TrustInspector(st, exe);
        Assert.True(NvidiaProfilePresets.InspectorTrusted(st, exe));
        File.WriteAllBytes(exe, [6, 6, 6]); // replaced later
        Assert.False(NvidiaProfilePresets.InspectorTrusted(st, exe));
    }
}
