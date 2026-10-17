using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Profiles;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class GameScannerParserTests
{
    [Fact]
    public void Steam_library_folders_and_manifest()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"322170\"\t\t\"336109904\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameScanner.SteamLibraries(vdf));
        const string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"4704690\"\n\t\"name\"\t\t\"MECCHA CHAMELEON\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"MECCHA CHAMELEON\"\n}";
        var app = GameScanner.ParseAppManifest(acf)!;
        Assert.Equal((4704690, "MECCHA CHAMELEON", "MECCHA CHAMELEON"), (app.AppId, app.Name, app.InstallDir));
    }

    [Fact]
    public void Epic_item_and_riot_settings()
    {
        var e = GameScanner.ParseEpicItem("{\"DisplayName\":\"Fortnite\",\"AppName\":\"Fortnite\",\"InstallLocation\":\"C:\\\\Program Files\\\\Epic Games\\\\Fortnite\",\"LaunchExecutable\":\"FortniteGame/Binaries/Win64/FortniteBootstrapper.exe\",\"CatalogNamespace\":\"fn\",\"CatalogItemId\":\"4fe75bbc5a674f4f9b356b5c90567da5\",\"MainGameAppName\":\"\",\"bIsIncompleteInstall\":false}")!;
        Assert.Equal("Fortnite", e.AppName);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", e.InstallLocation);
        Assert.False(e.Incomplete);
        Assert.Equal(@"C:\Riot Games\VALORANT\live", GameScanner.ParseRiotSettings("product_install_full_path: \"C:/Riot Games/VALORANT/live\"\nproduct_install_root: \"C:/Riot Games\"\n"));
    }

    [Fact]
    public void Steam_and_epic_resolution_with_exe_check_and_dlc_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-games-" + Guid.NewGuid().ToString("N"));
        try
        {
            var meccha = Directory.CreateDirectory(Path.Combine(root, "common", "MECCHA CHAMELEON")).FullName;
            File.WriteAllText(Path.Combine(meccha, "PenguinHotel.exe"), "x");
            var fn = Directory.CreateDirectory(Path.Combine(root, "Fortnite", "FortniteGame", "Binaries", "Win64")).FullName;
            File.WriteAllText(Path.Combine(fn, "FortniteClient-Win64-Shipping.exe"), "x");
            var gta = Directory.CreateDirectory(Path.Combine(root, "GTAV")).FullName;
            File.WriteAllText(Path.Combine(gta, "GTA5.exe"), "x"); // Legacy only
            var d = new LauncherData
            {
                SteamAvailable = true, SteamApps = [new SteamApp(4704690, "MECCHA CHAMELEON", meccha)],
                EpicAvailable = true,
                EpicItems =
                [
                    new("dlc1", "LEGO Fortnite Content", Path.Combine(root, "Fortnite"), "", "", "", "Fortnite", false),
                    new("Fortnite", "Fortnite", Path.Combine(root, "Fortnite"), "FortniteGame/Binaries/Win64/FortniteBootstrapper.exe", "fn", "id", "", false),
                    new("gtav", "Grand Theft Auto V", gta, "PlayGTAV.exe", "", "", "", false),
                ],
            };
            var m = GameScanner.Resolve(GameCatalog.Find("meccha-chameleon")!, d);
            Assert.EndsWith("PenguinHotel.exe", m.Install!.ExePath);
            Assert.Equal("steam://rungameid/4704690", m.Install.LaunchUri);
            var f = GameScanner.Resolve(GameCatalog.Find("fortnite")!, d);
            Assert.EndsWith("FortniteClient-Win64-Shipping.exe", f.Install!.ExePath);
            Assert.Contains("fn%3Aid%3AFortnite", f.Install.LaunchUri);
            Assert.NotNull(GameScanner.Resolve(GameCatalog.Find("gta5-legacy")!, d).Install);
            var enh = GameScanner.Resolve(GameCatalog.Find("gta5-enhanced")!, d);
            Assert.Null(enh.Install); // the Epic GTA folder has no GTA5_Enhanced.exe
            // Steam + Epic were both checked and neither has CS2's or Fall Guys' files: "not installed", not "not detected".
            var cs2 = GameScanner.Resolve(GameCatalog.Find("cs2")!, d);
            Assert.Equal(GameStatus.NotInstalled, GameDetector.StatusOf(cs2, new GameMatch(null, [])));
            var noSteam = GameScanner.Resolve(GameCatalog.Find("cs2")!, new LauncherData { SteamAvailable = false, SteamDetail = "none" });
            Assert.Equal(GameStatus.NotDetected, GameDetector.StatusOf(noSteam, new GameMatch(null, [])));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Candidate_exes_skip_helpers()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mt-cand-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var n in new[] { "GeometryDash.exe", "GeodeUpdater.exe", "GeodeUninstaller.exe", "UnityCrashHandler64.exe", "unins000.exe", "vc_redist.x64.exe" })
                File.WriteAllText(Path.Combine(root, n), new string('x', n.StartsWith("Geometry") ? 5000 : 10));
            var c = GameScanner.CandidateExes(root);
            Assert.Equal("GeometryDash.exe", Path.GetFileName(c[0].path));
            Assert.Single(c);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class GameDetectorTests
{
    private static ProcInfo P(int pid, string exe, string? path, string? title = null) => new(pid, exe, path, title);

    [Fact]
    public void Roblox_player_vs_studio_and_installers()
    {
        var player = GameCatalog.Find("roblox-player")!;
        var studio = GameCatalog.Find("roblox-studio")!;
        var procs = new[]
        {
            P(10, "RobloxStudioBeta.exe", @"C:\Users\a\AppData\Local\Roblox\Versions\version-1\RobloxStudioBeta.exe"),
            P(11, "RobloxPlayerInstaller.exe", @"C:\Users\a\Downloads\RobloxPlayerInstaller.exe"),
        };
        var m = GameDetector.Match(player, null, procs);
        Assert.Null(m.Process);
        Assert.Contains(m.Notes, n => n.Contains("Roblox Studio"));
        Assert.Equal(10, GameDetector.Match(studio, null, procs).Process!.Pid);
        var withPlayer = procs.Append(P(12, "RobloxPlayerBeta.exe", @"C:\Users\a\AppData\Local\Bloxstrap\Versions\version-2\RobloxPlayerBeta.exe")).ToList();
        Assert.Equal(12, GameDetector.Match(player, null, withPlayer).Process!.Pid);
        // Same file name from an unrelated folder (e.g. a renamed tool) is not Roblox.
        Assert.Null(GameDetector.Match(player, null, [P(13, "RobloxPlayerBeta.exe", @"C:\Temp\RobloxPlayerBeta.exe")]).Process);
    }

    [Fact]
    public void Minecraft_java_needs_runtime_path_or_window_never_bare_javaw()
    {
        var mc = GameCatalog.Find("minecraft-java")!;
        Assert.Null(GameDetector.Match(mc, null, [P(1, "javaw.exe", @"C:\Program Files\Java\jdk-21\bin\javaw.exe", "IntelliJ IDEA")]).Process);
        Assert.NotNull(GameDetector.Match(mc, null, [P(2, "javaw.exe", @"C:\Users\a\AppData\Roaming\.minecraft\runtime\java-runtime-delta\windows-x64\bin\javaw.exe")]).Process);
        Assert.NotNull(GameDetector.Match(mc, null, [P(3, "javaw.exe", @"C:\Users\a\AppData\Roaming\PrismLauncher\java\bin\javaw.exe", "Minecraft 1.21.4")]).Process);
        Assert.Null(GameDetector.Match(mc, null, [P(4, "Minecraft.exe", @"C:\XboxGames\Minecraft Launcher\Content\Minecraft.exe")]).Process); // launcher ≠ game
    }

    [Fact]
    public void Process_must_run_from_the_install_folder_when_known()
    {
        var gd = GameCatalog.Find("geometry-dash")!;
        var install = new GameInstall("Steam", @"C:\Steam\steamapps\common\Geometry Dash", @"C:\Steam\steamapps\common\Geometry Dash\GeometryDash.exe", null, null, null);
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mt-gd-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var inst = install with { InstallDir = root, ExePath = Path.Combine(root, "GeometryDash.exe") };
            Assert.NotNull(GameDetector.Match(gd, inst, [P(5, "GeometryDash.exe", Path.Combine(root, "GeometryDash.exe"))]).Process);
            var wrong = GameDetector.Match(gd, inst, [P(6, "GeometryDash.exe", @"C:\Downloads\copy\GeometryDash.exe")]);
            Assert.Null(wrong.Process);
            Assert.Contains(wrong.Notes, n => n.Contains("not from"));
            // Windows wouldn't give the path (protected process): matched by name, and the report says so.
            var unknown = GameDetector.Match(gd, inst, [P(7, "GeometryDash.exe", null)]);
            Assert.NotNull(unknown.Process);
            Assert.Contains(unknown.Notes, n => n.Contains("didn't give the path"));
        }
        finally { Directory.Delete(root, true); }
    }
}

[Collection("registry")]
public class GameLibraryFileTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-lib-" + Guid.NewGuid().ToString("N"));
    public GameLibraryFileTests() { Directory.CreateDirectory(_tmp); MoneyTweaks.Core.Sys.AppPaths.OverrideRoot(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Remove_never_touches_files_and_catalogue_games_can_be_restored()
    {
        var f = new LibraryFile();
        var exe = Path.Combine(_tmp, "MyIndieGame.exe");
        File.WriteAllText(exe, "game");
        var (added, _) = GameLibrary.AddExe(f, exe, "My Indie Game");
        Assert.True(added.Custom);
        GameLibrary.Remove(f, added);
        Assert.True(File.Exists(exe)); // only the entry is gone
        Assert.DoesNotContain(GameLibrary.Items(f), g => g.Key == added.Key);

        var cs2 = GameLibrary.Items(f).First(g => g.Def.Id == "cs2");
        GameLibrary.EntryFor(f, cs2).Favourite = true;
        GameLibrary.Remove(f, cs2);
        Assert.DoesNotContain(GameLibrary.Items(f), g => g.Def.Id == "cs2");
        Assert.Contains(GameLibrary.Removed(f), d => d.Id == "cs2");
        GameLibrary.Restore(f, "cs2");
        Assert.Contains(GameLibrary.Items(f), g => g.Def.Id == "cs2");

        GameLibrary.Save(f);
        var back = GameLibrary.Load();
        Assert.Empty(back.RemovedCatalog);
    }

    [Fact]
    public void Adding_a_catalogue_exe_updates_that_game_instead_of_duplicating()
    {
        var f = new LibraryFile { RemovedCatalog = ["geometry-dash"] };
        var dir = Directory.CreateDirectory(Path.Combine(_tmp, "GD moved")).FullName;
        var exe = Path.Combine(dir, "GeometryDash.exe");
        File.WriteAllText(exe, "x");
        var (g, msg) = GameLibrary.AddExe(f, exe);
        Assert.False(g.Custom);
        Assert.Equal("geometry-dash", g.Def.Id);
        Assert.Equal(exe, g.Entry!.ExePath);
        Assert.Contains("already in the catalogue", msg);
        var r = GameLibrary.Resolve(g, new LauncherData());
        Assert.Equal(exe, r.Install!.ExePath); // updated exe path wins
        File.Delete(exe);
        Assert.Contains(GameLibrary.Resolve(g, new LauncherData()).Checks, c => c.Detail.Contains("doesn't exist any more"));
    }

    [Fact]
    public void Search_favourites_categories_and_linked_profiles()
    {
        var f = new LibraryFile();
        var all = GameLibrary.Items(f);
        GameLibrary.EntryFor(f, all.First(g => g.Def.Id == "brawlhalla")).Favourite = true;
        all = GameLibrary.Items(f);
        Assert.Equal("Brawlhalla", GameLibrary.Filter(all, null, null, true).Single().Def.Name);
        Assert.Contains(GameLibrary.Filter(all, "roblox", null, false), g => g.Def.Id == "roblox-studio");
        Assert.All(GameLibrary.Filter(all, null, GameCategory.Party, false), g => Assert.Equal(GameCategory.Party, g.Def.Category));
        Assert.Contains(GameLibrary.Filter(all, "PenguinHotel", null, false), g => g.Def.Id == "meccha-chameleon"); // search by exe too
        var custom = new GamingProfile { Name = "My GD", GameExes = ["GeometryDash.exe"] };
        var builtIn = ProfileStore.BuiltIns.First(p => p.GameExes.Contains("GeometryDash.exe"));
        var linked = GameLibrary.LinkedCustomProfiles(all.First(g => g.Def.Id == "geometry-dash"), [custom, builtIn]);
        Assert.Single(linked); // built-in profiles are never offered for deletion
    }

    [Fact]
    public void Catalogue_has_the_main_games_and_no_bare_generic_exes()
    {
        foreach (var id in new[] { "fortnite", "geometry-dash", "roblox-player", "roblox-studio", "meccha-chameleon", "minecraft-java", "minecraft-bedrock", "gta5-legacy", "gta5-enhanced",
                     "rocket-league", "brawlhalla", "fall-guys", "among-us", "phasmophobia", "lethal-company", "cs2", "valorant", "apex", "marvel-rivals" })
            Assert.NotNull(GameCatalog.Find(id));
        Assert.Equal(GameCatalog.All.Count, GameCatalog.All.Select(g => g.Id).Distinct().Count());
        foreach (var g in GameCatalog.All)
            foreach (var r in g.Processes.Where(r => r.Exe is "javaw.exe" or "java.exe" or "hl2.exe" or "Windows10Universal.exe"))
                Assert.True(r.HasIdentityRule, $"{g.Name}: {r.Exe} needs a path/window rule");
    }
}

/// <summary>Read-only: real launcher data and processes on this PC.</summary>
public class GameLibraryLiveTests(ITestOutputHelper output)
{
    [Fact]
    public void Scan_this_pc()
    {
        var d = GameRuntime.Data(refresh: true);
        output.WriteLine($"Steam: {d.SteamAvailable} ({d.SteamDetail}); Epic: {d.EpicAvailable} ({d.EpicItems.Count} manifests); Riot: {d.RiotAvailable}; uninstall entries: {d.Uninstall.Count}");
        var games = GameLibrary.Items(new LibraryFile());
        foreach (var s in GameRuntime.Statuses(games).Where(s => s.status != GameStatus.NotInstalled).OrderBy(s => s.status))
            output.WriteLine($"  {GameDetector.Label(s.status),-13} {s.game.Def.Name}{(s.install.Install is { } i ? $" · {i.Source} · {i.ExePath ?? i.InstallDir}" : "")}{(s.match.Process is { } p ? $" · pid {p.Pid}" : "")}");
        output.WriteLine($"  Not installed: {string.Join(", ", GameRuntime.Statuses(games).Where(s => s.status == GameStatus.NotInstalled).Select(s => s.game.Def.Name))}");
        foreach (var id in new[] { "fortnite", "geometry-dash", "meccha-chameleon", "roblox-player", "roblox-studio", "minecraft-java" })
        {
            output.WriteLine("---");
            foreach (var l in GameRuntime.Test(games.First(g => g.Def.Id == id)).Lines) output.WriteLine("  " + l);
        }
    }
}
