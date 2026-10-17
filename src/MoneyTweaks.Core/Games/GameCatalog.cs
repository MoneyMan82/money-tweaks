namespace MoneyTweaks.Core.Games;

public enum GameCategory { Competitive, BattleRoyale, Party, CoOp, Sandbox, Sports, Indie, Creative, Custom }

/// <summary>
/// What counts as the running game. With <see cref="PathAny"/> or <see cref="TitlePrefix"/> set, the process must also have
/// one of those path fragments or a main-window title starting with the prefix (e.g. javaw.exe is only Minecraft when
/// it runs from Minecraft's Java runtime or shows a "Minecraft" window).
/// </summary>
public sealed record ProcessRule(string Exe, string[]? PathAny = null, string? TitlePrefix = null)
{
    public bool HasIdentityRule => PathAny is { Length: > 0 } || TitlePrefix is not null;
}

/// <summary>A game Money Tweaks knows how to find. Launch and process identities are separate (Fortnite launches a bootstrapper).</summary>
public sealed class GameDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public GameCategory Category { get; init; }
    public ProcessRule[] Processes { get; init; } = [];
    /// <summary>Executables that look related but aren't this game (Roblox Studio for Roblox Player, launchers, installers).</summary>
    public string[] NotThisGame { get; init; } = [];
    public int[] SteamAppIds { get; init; } = [];
    /// <summary>Steam manifest "name", used if the app ID differs (regional or re-released builds).</summary>
    public string? SteamName { get; init; }
    public string? EpicAppName { get; init; }
    public string? EpicName { get; init; }
    /// <summary>Riot product id, e.g. "valorant" (read from Riot's product settings).</summary>
    public string? RiotProduct { get; init; }
    /// <summary>Launcher-specific detection: roblox-player, roblox-studio, minecraft-java, minecraft-bedrock.</summary>
    public string? Special { get; init; }
    /// <summary>Registry install keys (HKLM path, value) such as Rockstar's InstallFolder.</summary>
    public (string key, string value)[] RegistryInstalls { get; init; } = [];
    /// <summary>"Apps &amp; features" display names (exact or followed by a space/colon).</summary>
    public string[] UninstallNames { get; init; } = [];
    /// <summary>Known exe locations relative to the install folder (checked before searching).</summary>
    public string[] ExeHints { get; init; } = [];
    /// <summary>Money Tweaks pages with real settings for this game.</summary>
    public string[] Features { get; init; } = [];
    /// <summary>The Game Profiles CPU-priority tweak for this game, if there is one.</summary>
    public string? PriorityTweak { get; init; }

    public IEnumerable<string> ExeNames => Processes.Select(p => p.Exe);
}

/// <summary>
/// Built-in games. Steam app IDs, Epic app names and executables are the real ones; a wrong or changed ID still falls
/// back to the Steam manifest name, and a game that updates its exe can be pointed at the new one in the library.
/// </summary>
public static class GameCatalog
{
    private static readonly string[] RobloxDirs = [@"\Roblox\Versions\", @"\Bloxstrap\Versions\", @"\Fishstrap\Versions\", @"\Voidstrap\Versions\", "ROBLOXCORPORATION.ROBLOX"];
    private static readonly string[] MinecraftRuntime = [@"\.minecraft\runtime\", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", @"\Minecraft Launcher\"];

    public static IReadOnlyList<GameDef> All { get; } =
    [
        new() { Id = "fortnite", Name = "Fortnite", Category = GameCategory.BattleRoyale,
            Processes = [new("FortniteClient-Win64-Shipping.exe")], NotThisGame = ["FortniteLauncher.exe", "FortniteBootstrapper.exe", "EpicGamesLauncher.exe"],
            EpicAppName = "Fortnite", ExeHints = [@"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"],
            Features = ["potato", "fnstretch", "overlay"], PriorityTweak = "game.fortnite" },
        new() { Id = "geometry-dash", Name = "Geometry Dash", Category = GameCategory.Indie,
            Processes = [new("GeometryDash.exe")], NotThisGame = ["GeodeUpdater.exe", "GeodeUninstaller.exe"], SteamAppIds = [322170], SteamName = "Geometry Dash", Features = ["overlay"] },
        new() { Id = "roblox-player", Name = "Roblox Player", Category = GameCategory.Sandbox,
            Processes = [new("RobloxPlayerBeta.exe", RobloxDirs), new("Windows10Universal.exe", ["ROBLOXCORPORATION.ROBLOX"])],
            NotThisGame = ["RobloxStudioBeta.exe", "RobloxPlayerInstaller.exe", "RobloxPlayerLauncher.exe", "RobloxCrashHandler.exe", "RobloxStudioInstaller.exe"],
            Special = "roblox-player", PriorityTweak = "game.roblox", Features = ["overlay"] },
        new() { Id = "roblox-studio", Name = "Roblox Studio", Category = GameCategory.Creative,
            Processes = [new("RobloxStudioBeta.exe", RobloxDirs)], NotThisGame = ["RobloxPlayerBeta.exe", "RobloxStudioInstaller.exe", "RobloxCrashHandler.exe"],
            Special = "roblox-studio" },
        new() { Id = "meccha-chameleon", Name = "MECCHA CHAMELEON", Category = GameCategory.Party,
            Processes = [new("PenguinHotel.exe")], SteamAppIds = [4704690], SteamName = "MECCHA CHAMELEON", Features = ["overlay"] },
        new() { Id = "minecraft-java", Name = "Minecraft: Java Edition", Category = GameCategory.Sandbox,
            Processes = [new("javaw.exe", MinecraftRuntime, "Minecraft"), new("java.exe", MinecraftRuntime, "Minecraft")],
            NotThisGame = ["Minecraft.exe", "MinecraftLauncher.exe"], Special = "minecraft-java" },
        new() { Id = "minecraft-bedrock", Name = "Minecraft: Bedrock Edition", Category = GameCategory.Sandbox,
            Processes = [new("Minecraft.Windows.exe")], Special = "minecraft-bedrock", Features = ["overlay"] },
        new() { Id = "gta5-legacy", Name = "Grand Theft Auto V Legacy", Category = GameCategory.Sandbox,
            Processes = [new("GTA5.exe")], NotThisGame = ["GTA5_Enhanced.exe", "PlayGTAV.exe", "GTAVLauncher.exe"],
            SteamAppIds = [271590], SteamName = "Grand Theft Auto V Legacy", EpicName = "Grand Theft Auto V",
            RegistryInstalls = [(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V", "InstallFolder")], PriorityTweak = "game.gta5", Features = ["overlay"] },
        new() { Id = "gta5-enhanced", Name = "Grand Theft Auto V Enhanced", Category = GameCategory.Sandbox,
            Processes = [new("GTA5_Enhanced.exe")], NotThisGame = ["GTA5.exe", "PlayGTAV.exe"],
            SteamAppIds = [3240220], SteamName = "Grand Theft Auto V Enhanced", EpicName = "Grand Theft Auto V Enhanced",
            RegistryInstalls = [(@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV Enhanced", "InstallFolder")], PriorityTweak = "game.gta5-enhanced", Features = ["overlay"] },
        new() { Id = "rocket-league", Name = "Rocket League", Category = GameCategory.Sports,
            Processes = [new("RocketLeague.exe")], SteamAppIds = [252950], EpicAppName = "Sugar", EpicName = "Rocket League",
            ExeHints = [@"Binaries\Win64\RocketLeague.exe"], PriorityTweak = "game.rocketleague", Features = ["overlay"] },
        new() { Id = "brawlhalla", Name = "Brawlhalla", Category = GameCategory.Competitive, Processes = [new("Brawlhalla.exe")], SteamAppIds = [291550], Features = ["overlay"] },
        new() { Id = "fall-guys", Name = "Fall Guys", Category = GameCategory.Party, Processes = [new("FallGuys_client_game.exe")], SteamAppIds = [1097150], EpicName = "Fall Guys", Features = ["overlay"] },
        new() { Id = "among-us", Name = "Among Us", Category = GameCategory.Party, Processes = [new("Among Us.exe")], SteamAppIds = [945360], EpicName = "Among Us", Features = ["overlay"] },
        new() { Id = "phasmophobia", Name = "Phasmophobia", Category = GameCategory.CoOp, Processes = [new("Phasmophobia.exe")], SteamAppIds = [739630], Features = ["overlay"] },
        new() { Id = "lethal-company", Name = "Lethal Company", Category = GameCategory.CoOp, Processes = [new("Lethal Company.exe")], SteamAppIds = [1966720], Features = ["overlay"] },
        new() { Id = "cs2", Name = "Counter-Strike 2", Category = GameCategory.Competitive, Processes = [new("cs2.exe")], SteamAppIds = [730],
            ExeHints = [@"game\bin\win64\cs2.exe"], PriorityTweak = "game.cs2", Features = ["overlay"] },
        new() { Id = "valorant", Name = "VALORANT", Category = GameCategory.Competitive, Processes = [new("VALORANT-Win64-Shipping.exe")],
            NotThisGame = ["VALORANT.exe", "RiotClientServices.exe"], RiotProduct = "valorant",
            ExeHints = [@"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"], PriorityTweak = "game.valorant", Features = ["overlay"] },
        new() { Id = "apex", Name = "Apex Legends", Category = GameCategory.BattleRoyale, Processes = [new("r5apex.exe"), new("r5apex_dx12.exe")],
            SteamAppIds = [1172470], EpicName = "Apex Legends", UninstallNames = ["Apex Legends"], PriorityTweak = "game.apex", Features = ["overlay"] },
        new() { Id = "marvel-rivals", Name = "Marvel Rivals", Category = GameCategory.Competitive, Processes = [new("Marvel-Win64-Shipping.exe")],
            NotThisGame = ["MarvelRivals_Launcher.exe"], SteamAppIds = [2767030], EpicName = "Marvel Rivals",
            ExeHints = [@"MarvelGame\Marvel\Binaries\Win64\Marvel-Win64-Shipping.exe"], PriorityTweak = "game.marvelrivals", Features = ["overlay"] },
        new() { Id = "gorilla-tag", Name = "Gorilla Tag", Category = GameCategory.Party, Processes = [new("Gorilla Tag.exe")], SteamAppIds = [1533390] },
        new() { Id = "overwatch", Name = "Overwatch 2", Category = GameCategory.Competitive, Processes = [new("Overwatch.exe")], SteamAppIds = [2357570], UninstallNames = ["Overwatch"], Features = ["overlay"] },
        new() { Id = "r6", Name = "Rainbow Six Siege", Category = GameCategory.Competitive, Processes = [new("RainbowSix.exe"), new("RainbowSix_Vulkan.exe")], NotThisGame = ["RainbowSix_BE.exe"], SteamAppIds = [359550], Features = ["overlay"] },
        new() { Id = "dota2", Name = "Dota 2", Category = GameCategory.Competitive, Processes = [new("dota2.exe")], SteamAppIds = [570], Features = ["overlay"] },
        new() { Id = "lol", Name = "League of Legends", Category = GameCategory.Competitive, Processes = [new("League of Legends.exe")],
            NotThisGame = ["LeagueClient.exe", "LeagueClientUx.exe", "RiotClientServices.exe"], RiotProduct = "league_of_legends", ExeHints = [@"Game\League of Legends.exe"], Features = ["overlay"] },
        new() { Id = "pubg", Name = "PUBG: Battlegrounds", Category = GameCategory.BattleRoyale, Processes = [new("TslGame.exe")], SteamAppIds = [578080], Features = ["overlay"] },
        new() { Id = "cod", Name = "Call of Duty", Category = GameCategory.BattleRoyale, Processes = [new("cod.exe")], SteamAppIds = [1938090], UninstallNames = ["Call of Duty"], Features = ["overlay"] },
        new() { Id = "the-finals", Name = "THE FINALS", Category = GameCategory.Competitive, Processes = [new("Discovery.exe")], SteamAppIds = [2073850], Features = ["overlay"] },
        new() { Id = "tf2", Name = "Team Fortress 2", Category = GameCategory.Competitive, Processes = [new("tf_win64.exe"), new("hl2.exe", [@"\Team Fortress 2\"])], SteamAppIds = [440], Features = ["overlay"] },
        new() { Id = "rust", Name = "Rust", Category = GameCategory.Sandbox, Processes = [new("RustClient.exe")], SteamAppIds = [252490], Features = ["overlay"] },
        new() { Id = "dbd", Name = "Dead by Daylight", Category = GameCategory.CoOp, Processes = [new("DeadByDaylight-Win64-Shipping.exe")], SteamAppIds = [381210], EpicName = "Dead by Daylight", Features = ["overlay"] },
        new() { Id = "destiny2", Name = "Destiny 2", Category = GameCategory.CoOp, Processes = [new("destiny2.exe")], SteamAppIds = [1085660], Features = ["overlay"] },
        new() { Id = "warframe", Name = "Warframe", Category = GameCategory.CoOp, Processes = [new("Warframe.x64.exe")], SteamAppIds = [230410], Features = ["overlay"] },
        new() { Id = "deep-rock", Name = "Deep Rock Galactic", Category = GameCategory.CoOp, Processes = [new("FSD-Win64-Shipping.exe")], SteamAppIds = [548430], Features = ["overlay"] },
        new() { Id = "valheim", Name = "Valheim", Category = GameCategory.CoOp, Processes = [new("valheim.exe")], SteamAppIds = [892970], Features = ["overlay"] },
        new() { Id = "palworld", Name = "Palworld", Category = GameCategory.CoOp, Processes = [new("Palworld-Win64-Shipping.exe")], SteamAppIds = [1623730], Features = ["overlay"] },
        new() { Id = "content-warning", Name = "Content Warning", Category = GameCategory.CoOp, Processes = [new("Content Warning.exe")], SteamAppIds = [2881650] },
        new() { Id = "repo", Name = "R.E.P.O.", Category = GameCategory.CoOp, Processes = [new("REPO.exe")], SteamAppIds = [3241660], SteamName = "R.E.P.O." },
        new() { Id = "party-animals", Name = "Party Animals", Category = GameCategory.Party, Processes = [new("PartyAnimals.exe")], SteamAppIds = [1260320] },
        new() { Id = "stumble-guys", Name = "Stumble Guys", Category = GameCategory.Party, Processes = [new("Stumble Guys.exe")], SteamAppIds = [1677740] },
        new() { Id = "terraria", Name = "Terraria", Category = GameCategory.Indie, Processes = [new("Terraria.exe")], SteamAppIds = [105600] },
        new() { Id = "stardew", Name = "Stardew Valley", Category = GameCategory.Indie, Processes = [new("Stardew Valley.exe")], SteamAppIds = [413150] },
        new() { Id = "osu", Name = "osu!", Category = GameCategory.Indie, Processes = [new("osu!.exe")], UninstallNames = ["osu!"], Features = ["overlay"] },
    ];

    public static GameDef? Find(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>Catalogue games whose process list contains this exe name.</summary>
    public static IEnumerable<GameDef> ByExe(string exe) =>
        All.Where(g => g.Processes.Any(p => p.Exe.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)));

    public static string Label(GameCategory c) => c switch
    {
        GameCategory.BattleRoyale => "Battle royale",
        GameCategory.CoOp => "Co-op & horror",
        GameCategory.Sports => "Sports & racing",
        GameCategory.Indie => "Indie & rhythm",
        _ => c.ToString(),
    };
}
