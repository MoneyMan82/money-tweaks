using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>Game Autopilot: one session at a time, recovery after a crash, presets, readiness and the session store.</summary>
[Collection("registry")]
public class GameAutopilotTests
{
    private static AutopilotFile File(bool enabled = true) => new()
    {
        Enabled = enabled,
        Rules =
        {
            ["fortnite"] = new AutopilotRule { GameKey = "fortnite", GameName = "Fortnite", Enabled = true, Preset = AutopilotPreset.Light },
            ["gd"] = new AutopilotRule { GameKey = "gd", GameName = "Geometry Dash", Enabled = true, Preset = AutopilotPreset.Light },
            ["roblox"] = new AutopilotRule { GameKey = "roblox", GameName = "Roblox", Enabled = false },
        },
    };

    [Fact]
    public void A_running_game_with_a_rule_starts_a_session_and_disabled_ones_never_do()
    {
        var f = File();
        Assert.Equal("none", GameAutopilot.Decide(f, [("roblox", 30)], _ => true).Kind);          // rule off
        var d = GameAutopilot.Decide(f, [("fortnite", 10)], _ => true);
        Assert.Equal(("start", "fortnite", 10), (d.Kind, d.GameKey, d.Pid));
        Assert.Equal("none", GameAutopilot.Decide(File(enabled: false), [("fortnite", 10)], _ => true).Kind); // master switch off
    }

    [Fact]
    public void Overlapping_games_wait_for_the_running_session_instead_of_starting_a_second()
    {
        var f = File();
        f.Active = new AutopilotSession { GameKey = "fortnite", GameName = "Fortnite", Pid = 10 };
        var d = GameAutopilot.Decide(f, [("fortnite", 10), ("gd", 20)], _ => true);
        Assert.Equal("busy", d.Kind);
        Assert.Equal(["Geometry Dash"], d.Waiting!);
        // The first game exits: its session ends; on the next poll the waiting game starts.
        var end = GameAutopilot.Decide(f, [("gd", 20)], pid => pid != 10);
        Assert.Equal(("end", "exited"), (end.Kind, end.Reason));
        f.Active = null;
        Assert.Equal(("start", "gd"), (GameAutopilot.Decide(f, [("gd", 20)], _ => true).Kind, GameAutopilot.Decide(f, [("gd", 20)], _ => true).GameKey));
    }

    [Fact]
    public void A_session_ended_by_hand_does_not_restart_for_the_same_process()
    {
        var f = File();
        var suppressed = new HashSet<int> { 10 };
        Assert.Equal("none", GameAutopilot.Decide(f, [("fortnite", 10)], _ => true, suppressed).Kind);
        Assert.Equal("start", GameAutopilot.Decide(f, [("fortnite", 11)], _ => true, suppressed).Kind);   // restarted game: new pid
    }

    [Fact]
    public void After_an_unexpected_shutdown_the_session_resumes_or_is_restored()
    {
        var f = File();
        Assert.Equal("none", GameAutopilot.Recover(f, _ => true).Kind);
        f.Active = new AutopilotSession { GameKey = "fortnite", Pid = 10 };
        Assert.Equal("resume", GameAutopilot.Recover(f, _ => true).Kind);
        var end = GameAutopilot.Recover(f, _ => false);
        Assert.Equal("end", end.Kind);
        Assert.Contains("unexpectedly", end.Reason);
    }

    [Fact]
    public void Presets_only_switch_on_what_they_say()
    {
        var light = GameAutopilot.Apply(new AutopilotRule { GameKey = "x", GameName = "X" }, AutopilotPreset.Light, hasGameProfile: true);
        Assert.True(light.Enabled);
        Assert.Equal(AutopilotOptimizations.None, light.Optimizations);
        Assert.Null(GameAutopilot.ProfileFor(light, new GamingProfile { Name = "P" }));                 // Light changes no settings
        var comp = GameAutopilot.Apply(new AutopilotRule { GameKey = "x", GameName = "X" }, AutopilotPreset.Competitive, hasGameProfile: true);
        var linked = new GamingProfile { Id = "profile.x", Name = "X Competitive" };
        Assert.Same(linked, GameAutopilot.ProfileFor(comp, linked));                                     // the game's own profile
        var custom = GameAutopilot.Apply(new AutopilotRule { GameKey = "x", GameName = "X" }, AutopilotPreset.Competitive, hasGameProfile: false);
        var p = GameAutopilot.ProfileFor(custom, null)!;
        Assert.StartsWith(GameAutopilot.ProfilePrefix, p.Id);
        Assert.Equal(PowerChoice.HighPerformance, p.Power);
        Assert.False(p.RaiseGamePriority);                                                                // priority is done per process, not by name
        var off = GameAutopilot.Apply(custom, AutopilotPreset.Off, false);
        Assert.False(off.Enabled);
    }

    [Fact]
    public void Every_action_is_described_with_how_it_is_undone()
    {
        var r = GameAutopilot.Apply(new AutopilotRule { GameKey = "x", GameName = "X" }, AutopilotPreset.Competitive, false);
        r.ShowOverlay = true;
        var (pre, during, post) = GameAutopilot.Describe(r, null);
        Assert.NotEmpty(pre);
        Assert.Contains(during, l => l.Contains("High performance"));
        Assert.Contains(post, l => l.Contains("Restore every setting"));
        Assert.Contains(post, l => l.Contains("Hide the overlay"));
        Assert.Contains(post, l => l.Contains("priority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Session_tweaks_exist_and_never_include_services()
    {
        foreach (var id in GameAutopilot.SessionTweaks)
        {
            var t = TweakCatalog.Find(id);
            Assert.NotNull(t);
            Assert.NotEqual(Core.Model.TweakCategory.Services, t!.Category);
        }
    }

    [Fact]
    public void Readiness_reports_fine_readings_as_ok_and_names_real_problems()
    {
        var fine = GameAutopilot.Readiness(new(45, 40, 12, 32, 1, 12, 300, "C:", null, 4, DateTime.Now.AddMonths(-2), "GPU"));
        Assert.All(fine, i => Assert.True(i.Ok, i.Text));
        var bad = GameAutopilot.Readiness(new(85, 80, 1.5, 16, 7, 8, 6, "D:", "chrome 22%", 30, DateTime.Now.AddYears(-2), "GPU"));
        Assert.All(bad, i => Assert.False(i.Ok, i.Area));
        Assert.Contains(bad, i => i.Text.Contains("chrome"));
        var unknown = GameAutopilot.Readiness(new(null, null, 12, 32, null, null, null, null, null, 2, null, null));
        Assert.DoesNotContain(unknown, i => i.Area.Contains("temperature"));                              // nothing invented
    }

    [Fact]
    public void Sessions_are_saved_before_anything_changes_and_history_is_capped()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mt-autopilot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            AppPaths.OverrideRoot(tmp);
            var f = File();
            f.Active = new AutopilotSession { GameKey = "fortnite", GameName = "Fortnite", Pid = 42, OwnedProfileId = "autopilot:fortnite", OriginalPriority = "Normal" };
            for (int i = 0; i < AutopilotStore.MaxHistory + 20; i++) f.History.Add(new AutopilotSession { GameName = "g" + i });
            AutopilotStore.Save(f);
            var back = AutopilotStore.Load();
            Assert.Equal(42, back.Active!.Pid);
            Assert.Equal("autopilot:fortnite", back.Active.OwnedProfileId);
            Assert.Equal(AutopilotStore.MaxHistory, back.History.Count);
            Assert.Equal("g" + (AutopilotStore.MaxHistory + 19), back.History[^1].GameName);
            Assert.True(back.Rules["fortnite"].Enabled);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void A_crash_check_for_an_unknown_program_finds_nothing() =>
        Assert.False(GameAutopilot.CrashedRecently("money-tweaks-no-such-game-" + Guid.NewGuid().ToString("N") + ".exe", TimeSpan.FromMinutes(5)));
}
