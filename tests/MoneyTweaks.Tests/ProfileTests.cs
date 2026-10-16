using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

[Collection("registry")]
public class ProfileTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-prof-" + Guid.NewGuid().ToString("N"));
    public ProfileTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Built_in_profiles_are_valid_and_cover_the_requested_games()
    {
        var all = ProfileStore.BuiltIns;
        Assert.Equal(["Fortnite Competitive", "Fortnite Performance Mode", "Geometry Dash", "General Gaming"], all.Select(p => p.Name));
        foreach (var p in all)
        {
            Assert.Empty(ProfileStore.Validate(p, all));
            Assert.DoesNotContain(ProfileEngine.Conflicts(p), c => !c.StartsWith("Info", StringComparison.Ordinal));
        }
        Assert.Contains("GeometryDash.exe", all.Single(p => p.Name == "Geometry Dash").GameExes);
    }

    [Fact]
    public void Validation_rejects_bad_input_and_unsafe_tweaks()
    {
        var p = new GamingProfile { Name = "Fortnite Competitive", TweakIds = ["adv.vbs-hvci", "net.nagle", "nope"], TimerResolutionMs = 0.1, GameExes = ["game", "..\\x.exe"], RaiseGamePriority = false };
        var e = ProfileStore.Validate(p, ProfileStore.BuiltIns);
        Assert.Contains(e, x => x.Contains("already has this name"));
        Assert.Contains(e, x => x.Contains("Memory Integrity") || x.Contains("disabled"));
        Assert.Contains(e, x => x.Contains("restart"));                     // Nagle needs a restart
        Assert.Contains(e, x => x.Contains("Unknown tweak 'nope'"));
        Assert.Contains(e, x => x.Contains("0.5 and 1.0"));
        Assert.Equal(2, e.Count(x => x.Contains("not a program file name")));
    }

    [Fact]
    public void Export_import_round_trip_renames_duplicates_and_drops_unsupported()
    {
        var mine = new GamingProfile { Name = "Mine", TweakIds = ["gen.game-mode"], GameExes = ["Game.exe"], RaiseGamePriority = true, TimerResolutionMs = 1.0 };
        var path = Path.Combine(_tmp, "p.json");
        ProfileStore.Export([mine, new GamingProfile { Name = "Bad", TweakIds = ["gen.game-mode", "adv.large-system-cache"] }], path);
        var (added, problems) = ProfileStore.Import(path, [mine]);
        Assert.Equal(2, added.Count);
        Assert.Equal("Mine (2)", added[0].Name);
        Assert.NotEqual(mine.Id, added[0].Id);
        Assert.Equal(["gen.game-mode"], added[1].TweakIds);
        Assert.Contains(problems, x => x.Contains("adv.large-system-cache"));
        File.WriteAllText(path, "{\"format\":\"something-else\",\"profiles\":[]}");
        Assert.Throws<InvalidDataException>(() => ProfileStore.Import(path, []));
    }

    [Fact]
    public void Conflicts_are_detected()
    {
        var p = new GamingProfile { Name = "x", TweakIds = ["net.dns-cloudflare", "net.dns-google", "gen.ultimate-plan", "gen.usb-suspend"], Power = PowerChoice.HighPerformance };
        var c = ProfileEngine.Conflicts(p);
        Assert.Contains(c, x => x.Contains("DNS"));
        Assert.Contains(c, x => x.Contains("Ultimate Performance"));
        Assert.Contains(c, x => x.Contains("power-plan tweaks"));
    }

    [Fact]
    public void Session_is_persisted_before_changes_and_fully_released()
    {
        var file = new ProfileFile();
        var p = new GamingProfile { Name = "Timer only", TimerResolutionMs = 1.0 };
        int persisted = 0;
        var (session, problems) = ProfileEngine.Activate(p, "test.exe", s => { persisted++; file.Active = s; ProfileStore.Save(file); });
        Assert.NotNull(session);
        Assert.Empty(problems);
        Assert.True(persisted >= 3);                                       // before any change, after capturing states, at the end
        Assert.Equal("Timer only", ProfileStore.Load().Active!.ProfileName); // crash recovery data is on disk
        Assert.Equal(1.0, session!.TimerMs);
        Assert.InRange(TimerResolution.Query().currentMs, 0.4, 1.01);
        Assert.Empty(ProfileEngine.Deactivate(session));
    }

    [Fact]
    public void Running_game_detection_uses_real_processes()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        Assert.Equal(self, ProfileEngine.RunningGame(["definitely-not-running-123.exe", self]));
        Assert.Null(ProfileEngine.RunningGame(["definitely-not-running-123.exe"]));
    }
}
