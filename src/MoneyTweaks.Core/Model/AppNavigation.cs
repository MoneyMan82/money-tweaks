namespace MoneyTweaks.Core.Model;

/// <summary>A sidebar group.</summary>
public sealed record NavGroup(string Id, string Label, string Icon);

/// <summary>
/// One place in the app. Tag is the page key (unchanged from earlier versions so links and saved favourites keep working);
/// "page#section" opens an existing page and scrolls to a section, it never creates a second copy of a feature.
/// </summary>
public sealed record NavPage(string Tag, string Icon, string Label, string Group, string Description, string Keywords = "", bool InSidebar = true)
{
    public string PageTag => Tag.Split('#')[0];
    public string? Anchor => Tag.Contains('#') ? Tag[(Tag.IndexOf('#') + 1)..] : null;
}

/// <summary>The app's information architecture: every page, the group it belongs to, and what it can be searched by.</summary>
public static class AppNavigation
{
    public static IReadOnlyList<NavGroup> Groups { get; } =
    [
        new("dashboard", "Dashboard", "\uE80F"),
        new("optimize", "Optimization", "\uE9E9"),
        new("gaming", "Gaming", "\uE7FC"),
        new("monitor", "Monitoring & Benchmarks", "\uE9D9"),
        new("tools", "Tools", "\uE90F"),
        new("backup", "Backup & Restore", "\uE777"),
        new("settings", "Settings", "\uE713"),
    ];

    private static readonly Dictionary<TweakCategory, string> CatIcons = new()
    {
        [TweakCategory.General] = "\uE771", [TweakCategory.Debloating] = "\uE74D", [TweakCategory.Networking] = "\uE968",
        [TweakCategory.Advanced] = "\uE9E9", [TweakCategory.Services] = "\uE912", [TweakCategory.AppOptimizer] = "\uEC4A",
        [TweakCategory.AppRemoval] = "\uE71D", [TweakCategory.GameProfiles] = "\uE7FC", [TweakCategory.Bios] = "\uE950",
    };

    public static string CategoryIcon(TweakCategory c) => CatIcons[c];

    private static NavPage Cat(TweakCategory c, string group, string? label = null, string keywords = "") =>
        new("cat:" + c, CatIcons[c], label ?? CategoryInfo.DisplayName(c), group, CategoryInfo.Blurb(c), keywords);

    public static IReadOnlyList<NavPage> Pages { get; } =
    [
        new("dashboard", "\uE80F", "Dashboard", "dashboard", "Live hardware, PC health, active optimizations, game status, recent activity, backups and shortcuts.", "home overview start"),

        new("scanner", "\uE721", "Smart Scanner", "optimize", "Reads this PC and lists evidence-based fixes with backup and undo.", "scan fix recommend analyze"),
        new("tweaks", "\uE8FD", "All Tweaks", "optimize", "Every tweak in one library: search, filters, sorting, categories, status, risk, dependencies and undo.", "tweak library list all registry"),
        new("cleaner", "\uEA99", "PC Cleaner", "optimize", "Close background apps you don't need while playing (Quick, Custom, Clean & Play) and remove verifiably disposable files, with review, quarantine and measured results.", "clean cleaner junk temp storage disk space cache recycle bin background apps processes close ram cpu quarantine dump"),
        new("map", "\uE81E", "Tweak Map", "optimize", "The whole tweak library by purpose, how settings connect, and every conflict, duplicate or overlap, with links to each setting.", "map overview purpose conflicts overlaps duplicates related relationships library"),
        new("presets", "\uE8F1", "Presets", "optimize", "Apply curated or your own sets of tweaks in one go.", "bundle recommended preset"),
        Cat(TweakCategory.General, "optimize", "Windows Settings", "general windows settings game mode power"),
        Cat(TweakCategory.Debloating, "optimize", keywords: "telemetry bloat privacy ads"),
        Cat(TweakCategory.Services, "optimize", keywords: "service svc background"),
        Cat(TweakCategory.Networking, "optimize", keywords: "network tcp nagle dns latency"),
        Cat(TweakCategory.Advanced, "optimize", keywords: "advanced kernel scheduler memory"),
        Cat(TweakCategory.Bios, "optimize", keywords: "bios uefi xmp expo rebar resizable bar"),
        new("genome", "\uE9D2", "Performance Genome", "optimize", "Learns how this PC responds to combinations of CPU, GPU, Windows and game settings from its own measurements; measured profiles for your goal, with uncertainty.", "genome performance discovery experiment model frontier profile goal fps latency temperature quiet efficiency adaptive learn"),
        new("tuning", "\uE950", "CPU & GPU Tuning", "optimize", "GPU power limit and clock cap, CPU boost and efficiency profiles, stability tests, guided PBO/Curve Optimizer/XTU/undervolt steps.", "overclock undervolt pbo curve optimizer power limit clock nvidia gpu cpu xtu ryzen master xmp expo fan efficiency quiet stability"),

        new("library", "\uE8B7", "Game Library", "gaming", "Installed and running games from Steam, Epic, Riot, Roblox and Minecraft, plus your own; per-game profiles and launch.", "games steam epic roblox minecraft detect launch"),
        new("profiles", "\uE7FC", "Gaming Profiles", "gaming", "One-click or automatic sets of settings while a game runs, restored when it closes.", "profile auto switch"),
        Cat(TweakCategory.GameProfiles, "gaming", "Game CPU Priority", "priority ifeo game"),
        new("booster", "\uE945", "Game Booster", "gaming", "Power plan, close background apps, raise game priority and clear standby memory.", "boost session close apps"),
        new("input", "\uE962", "Input Delay Reducer", "gaming", "Timer resolution and input tweaks.", "timer resolution mouse keyboard latency"),
        new("ping", "\uE701", "Ping Stabilizer", "gaming", "Live latency, jitter and loss with network tweaks and a diagnostic.", "ping jitter packet loss lag"),
        new("potato", "\uE790", "Potato Graphics", "gaming", "Low-poly Fortnite and other games' settings, NVIDIA texture presets.", "fortnite low graphics fps nvidia lod bias tutorial video"),
        new("stretched", "\uE7F4", "Stretched Resolution", "gaming", "Custom resolutions with driver checks and countdown rollback.", "resolution display 4:3"),
        new("fnstretch", "\uE740", "Fortnite Stretched", "gaming", "Fortnite's resolution file, 2288×1440 preset, auto-switch while Fortnite runs.", "fortnite stretched 2288 ini tutorial video"),
        new("crosshair", "\uE710", "Custom Crosshair", "gaming", "Layered crosshair editor with profiles, firing animation and a click-through overlay.", "aim reticle crosshair layers profiles share code bloom png image per-game"),
        new("community", "\uE716", "Community Hub", "gaming", "Discover, rate and import settings, gaming profiles, presets and crosshairs shared by other players, or publish your own with screenshots.", "community hub share import publish download upload settings sensitivity keybinds crosshair profile preset rating likes bookmarks screenshots"),
        new("overlay", "\uE9D2", "Performance Overlay", "gaming", "Gaming HUD over your game: FPS, frame-time graph, 1% lows, CPU/GPU load and temperatures, RAM, VRAM and ping, with per-game layouts.", "fps frametime overlay presentmon hud 1% low vram ping click-through hotkey"),

        new("monitor", "\uE9D9", "Hardware Monitor", "monitor", "Live CPU, GPU, memory, storage and network readings with history graphs, per-core details, temperatures, clocks, power, warnings, CSV logging and a compact always-on-top window.", "cpu gpu ram temperature usage clock power vram fan disk network core thread throttling log csv export pin compact sensors"),
        new("monitor#health", "\uE95E", "Driver & Windows Health", "monitor", "Driver, Windows Update and stability checks, SFC/DISM and safe repairs.", "drivers health windows update sfc dism crash bsod"),
        new("advisor", "\uE9D9", "Upgrade Advisor", "monitor", "Finds what holds your game back (CPU, GPU, memory, storage, heat, frame cap) from real measurements, then ranks the three smartest fixes and upgrades by benefit per euro.", "bottleneck upgrade advisor fps goal target cpu gpu ram ssd budget price buy recommend diagnose limit"),
        new("benchmark", "\uE916", "Test & Benchmark", "monitor", "Measure with and without tweaks, synthetic stress scene, history and comparisons.", "benchmark fps test compare graph history"),
        new("lab", "\uE7B8", "Reality Lab", "monitor", "A 3D virtual PC driven by live CPU, GPU and RAM readings. Preview safe tweak experiments, compare the estimate with a real A/B measurement, inspect before/after resource use, then keep or roll back.", "lab reality 3d virtual pc experiment predict estimate preview simulate compare before after resource usage rollback"),

        new("startup", "\uE7E8", "Startup Apps", "tools", "Turn apps that launch at sign-in on or off.", "autostart sign in boot"),
        Cat(TweakCategory.AppOptimizer, "tools", keywords: "apps background launchers"),
        Cat(TweakCategory.AppRemoval, "tools", keywords: "uninstall remove apps bloatware"),
        new("macros", "\uE765", "Gaming Macros", "tools", "Keyboard/mouse macros with an emergency stop.", "macro keys automation"),
        new("logs", "\uE81C", "Activity Log", "tools", "Everything Money Tweaks changed, failures and undos.", "log history activity"),

        new("restore", "\uE777", "Backup & Restore", "backup", "Undo tweaks, restore points and backups.", "undo rollback restore point backup export"),

        new("about", "\uE713", "Settings & About", "settings", "Safety, motion, updates, start with Windows, hotkeys and about.", "settings preferences options"),
        new("about#appearance", "\uE771", "Appearance", "settings", "Motion and effects, sidebar.", "theme animation motion sidebar", InSidebar: false),
        new("about#hotkeys", "\uE765", "Hotkeys", "settings", "Search, emergency stop and overlay hotkeys.", "shortcut keyboard hotkey", InSidebar: false),
        new("about#updates", "\uE896", "Updates", "settings", "Check and install updates.", "update version", InSidebar: false),
        new("about#evidence", "\uE72E", "Result sharing", "settings", "Tweak Truth Index: whether your measured tweak results may be shared with the Community Hub (off by default).", "privacy share evidence results community truth index opt in", InSidebar: false),
        new("about#legacy", "\uE81C", "Legacy UI", "settings", "Switch back to the original Money Tweaks look.", "legacy classic old ui look theme green original", InSidebar: false),
    ];

    public static NavPage? Find(string tag) => Pages.FirstOrDefault(p => p.Tag == tag) ?? Pages.FirstOrDefault(p => p.Tag == tag.Split('#')[0]);
    public static NavGroup GroupOf(NavPage p) => Groups.First(g => g.Id == p.Group);

    /// <summary>Page tags that existed before the reorganization; all must still resolve (checked by tests).</summary>
    public static readonly string[] LegacyTags =
    [
        "dashboard", "profiles", "library", "scanner", "presets", "benchmark", "macros", "potato", "stretched", "fnstretch", "booster", "input", "ping",
        "crosshair", "startup", "monitor", "overlay", "restore", "logs", "about",
        "cat:General", "cat:Debloating", "cat:Networking", "cat:Advanced", "cat:Services", "cat:AppOptimizer", "cat:AppRemoval", "cat:GameProfiles", "cat:Bios",
    ];

    /// <summary>Pages that are always at the top of the sidebar (Dashboard).</summary>
    public static bool IsHome(string tag) => Find(tag) is { Group: "dashboard" };

    /// <summary>Favourites as shown in the sidebar: known pages only, no home page (it's already at the top), no duplicates.</summary>
    public static List<string> SidebarFavourites(IEnumerable<string> favourites) =>
        favourites.Where(t => Pages.Any(p => p.Tag == t) && !IsHome(t)).Distinct().ToList();

    /// <summary>Adds a page to the front of the recent list (max 6, no duplicates).</summary>
    public static List<string> PushRecent(IEnumerable<string> recent, string tag, int max = 6) =>
        new[] { tag }.Concat(recent.Where(t => t != tag)).Take(max).ToList();
}
