using System.Text.Json;
using Microsoft.Win32;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>
/// Emergency Rescue against real, reversible changes: scratch tweaks write only under HKCU\Software\MoneyTweaksTests,
/// and the data folder (backups, logs, rescue sessions) is a temp folder.
/// </summary>
[Collection("registry")]
public class EmergencyRescueTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private const string Key = @"Software\MoneyTweaksTests\Rescue";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-rescue-" + Guid.NewGuid().ToString("N"));

    public EmergencyRescueTests(ITestOutputHelper output)
    {
        this.output = output;
        AppPaths.OverrideRoot(_tmp);
        Registry.CurrentUser.DeleteSubKeyTree(Key, false);
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Key, false); } catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static Tweak Scratch(string id, params TweakOp[] ops) =>
        new() { Id = id, Category = TweakCategory.General, Name = "Scratch " + id, Description = "rescue test tweak", Ops = ops };

    private static object? Read(string name) { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(name); }

    [Fact]
    public void Restores_from_the_existing_backup_keeps_a_copy_of_both_states_and_verifies()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Mode", "original");
        var t = Scratch("test.rescue.a",
            new RegValueOp(Hive.HKCU, Key, "Mode", RegistryValueKind.String, "tweaked"),
            new RegValueOp(Hive.HKCU, Key, "Added", RegistryValueKind.DWord, 1));
        Tweak? Find(string id) => id == t.Id ? t : null;
        Assert.True(TweakEngine.Apply(t).Success);
        var originalJson = File.ReadAllText(BackupStore.PathOf(t.Id));

        var e = Assert.Single(EmergencyRescue.Scan(Find));
        Assert.True(e.CanRestore);
        Assert.Equal(TweakState.Applied, e.State);
        Assert.Contains(e.Values, v => v.Now.Contains("tweaked") && v.Original.Contains("original"));
        Assert.Contains(e.Values, v => v.Original.StartsWith("not set"));

        var s = EmergencyRescue.Restore([e]);
        var o = Assert.Single(s.Outcomes);
        Assert.True(o.Ok, o.Message);
        Assert.Equal("original", Read("Mode"));
        Assert.Null(Read("Added"));
        Assert.False(BackupStore.Exists(t.Id));
        // Same format as the backups folder: the consumed backup byte for byte, and the pre-rescue values as a TweakBackup.
        Assert.Equal(originalJson, File.ReadAllText(Path.Combine(s.Folder, "original", t.Id + ".json")));
        var before = JsonSerializer.Deserialize<TweakBackup>(File.ReadAllText(Path.Combine(s.Folder, "before-rescue", t.Id + ".json")))!;
        Assert.Equal("tweaked", before.Ops[0].Data["value"]);
        Assert.Equal("1", before.Ops[1].Data["value"]);
        Assert.NotNull(s.FinishedAt);

        var (_, diffs) = Assert.Single(EmergencyRescue.Verify(EmergencyRescue.Sessions().First(), Find));
        Assert.Empty(diffs);

        // Undoing the rescue applies it again on the original backup, so a later Undo still restores the true originals.
        var back = EmergencyRescue.PutBack(s, t.Id, Find);
        Assert.True(back.Ok, back.Message);
        Assert.Equal("tweaked", Read("Mode"));
        Assert.Equal(originalJson, File.ReadAllText(BackupStore.PathOf(t.Id)));
        Assert.False(EmergencyRescue.PutBack(s, t.Id, Find).Ok); // applied again: refuses to touch it twice
        Assert.True(TweakEngine.Undo(t).Success);
        Assert.Equal("original", Read("Mode"));
        Assert.Null(Read("Added"));
    }

    [Fact]
    public void Damaged_unknown_and_failing_changes_are_reported_and_their_backups_kept()
    {
        var ok = Scratch("test.rescue.ok", new RegValueOp(Hive.HKCU, Key, "Fine", RegistryValueKind.DWord, 1));
        var failing = Scratch("test.rescue.fail", new CommandOp { ApplyCommand = "exit /b 0", UndoCommand = "exit /b 7", Admin = false, Label = "scratch command" });
        var damaged = Scratch("test.rescue.damaged", new RegValueOp(Hive.HKCU, Key, "Damaged", RegistryValueKind.DWord, 1));
        var known = new[] { ok, failing, damaged }.ToDictionary(x => x.Id);
        Tweak? Find(string id) => known.GetValueOrDefault(id);
        Assert.True(TweakEngine.Apply(ok).Success);
        Assert.True(TweakEngine.Apply(failing).Success);
        using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Damaged", 1);
        File.WriteAllText(BackupStore.PathOf(damaged.Id), "{ not json");
        BackupStore.Save(new TweakBackup { TweakId = "removed.in-a-later-version", AppliedAt = DateTime.Now });

        var entries = EmergencyRescue.Scan(Find);
        Assert.Equal(4, entries.Count);
        Assert.False(entries.Single(x => x.Id == damaged.Id).CanRestore);
        Assert.Equal(RescueBackupStatus.Damaged, entries.Single(x => x.Id == damaged.Id).Status);
        Assert.Equal(RescueBackupStatus.UnknownTweak, entries.Single(x => x.Id == "removed.in-a-later-version").Status);

        var s = EmergencyRescue.Restore(entries);
        foreach (var o in s.Outcomes) output.WriteLine($"{o.Id}: {o.Ok} {o.Message}");
        Assert.True(s.Outcomes.Single(o => o.Id == ok.Id).Ok);
        var fail = s.Outcomes.Single(o => o.Id == failing.Id);
        Assert.False(fail.Ok);
        Assert.Contains("7", fail.Message);
        Assert.True(BackupStore.Exists(failing.Id));                     // kept so it can be retried
        Assert.True(s.Outcomes.Single(o => o.Id == damaged.Id).Skipped);
        Assert.Equal("{ not json", File.ReadAllText(BackupStore.PathOf(damaged.Id)));  // never guessed at or overwritten
        Assert.Equal(1, Read("Damaged"));                                  // and its value untouched
        Assert.True(s.Outcomes.Single(o => o.Id == "removed.in-a-later-version").Skipped);
        Assert.True(BackupStore.Exists("removed.in-a-later-version"));
    }

    [Fact]
    public void System_wide_changes_are_skipped_without_administrator_rights()
    {
        if (Admin.IsElevated) { output.WriteLine("skipped: elevated"); return; }
        var t = Scratch("test.rescue.admin", new RegValueOp(Hive.HKLM, @"SOFTWARE\MoneyTweaksTests\Rescue", "X", RegistryValueKind.DWord, 1));
        BackupStore.Save(new TweakBackup { TweakId = t.Id, AppliedAt = DateTime.Now, Ops = [t.Ops[0].Capture()] });
        var e = Assert.Single(EmergencyRescue.Scan(id => id == t.Id ? t : null));
        Assert.True(e.NeedsAdmin);
        var o = Assert.Single(EmergencyRescue.Restore([e]).Outcomes);
        Assert.False(o.Ok);
        Assert.True(o.NeedsAdmin);
        Assert.True(BackupStore.Exists(t.Id));
    }

    [Fact]
    public void Nothing_to_rescue_on_a_clean_data_folder()
    {
        Assert.Empty(EmergencyRescue.Scan());
        Assert.Empty(EmergencyRescue.Sessions());
        Assert.Empty(EmergencyRescue.RecentChanges());
        Assert.Null(EmergencyRescue.ActiveProfile());
    }

    [Fact]
    public void Recent_changes_come_from_the_existing_log_format()
    {
        var day = DateTime.Today.AddDays(-1);
        File.WriteAllLines(Path.Combine(AppPaths.Logs, $"moneytweaks-{day:yyyy-MM-dd}.log"),
        [
            "09:00:01 [INFO] Applied: Disable mouse acceleration (verified by read-back)",
            "09:00:02 [DEBUG] gen.mouse-accel: HKCU\\Control Panel\\Mouse\\MouseSpeed = 0 (String)",
            "09:05:00 [WARN] Apply 'X': read-back shows Partial; rolling back",
            "09:00:00 [INFO] gen.mouse-accel before step 1: HKCU\\Control Panel\\Mouse\\MouseSpeed = 0 (String) | was 1 (String)",
            "10.00.00 [INFO] Reverted: Disable mouse acceleration",
            "garbage line",
        ]);
        File.WriteAllLines(Path.Combine(AppPaths.Logs, "moneytweaks-2001-01-01.log"), ["09:00:00 [INFO] Applied: Too old"]);
        var lines = EmergencyRescue.RecentChanges();
        Assert.Equal(4, lines.Count);                                     // either time separator, as the culture wrote it
        Assert.Equal("Reverted: Disable mouse acceleration", lines[0].Text);
        Assert.Equal(day.AddHours(10), lines[0].Time);
        Assert.Equal("WARN", lines[1].Level);
        Assert.EndsWith("| was 1 (String)", lines[3].Text);
    }

    [Theory]
    [InlineData("1", 5, new[] { 0 })]
    [InlineData("1,3-5", 5, new[] { 0, 2, 3, 4 })]
    [InlineData(" 2 2 1 ", 5, new[] { 0, 1 })]
    [InlineData("all", 3, new[] { 0, 1, 2 })]
    public void Selection_parses(string text, int count, int[] expected) => Assert.Equal(expected, EmergencyRescue.ParseSelection(text, count));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("1,x")]
    [InlineData("3-1")]
    [InlineData("1-2-3")]
    [InlineData("-1")]
    public void Invalid_selection_selects_nothing(string text) => Assert.Null(EmergencyRescue.ParseSelection(text, 5));
}
