using Microsoft.Win32;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

public static partial class TweakCatalog
{
    internal const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>IFEO PerfOptions: 1 Idle, 2 Normal, 3 High, 5 BelowNormal, 6 AboveNormal.</summary>
    internal static RegValueOp CpuPriority(string exe, int cls) =>
        new(Hive.HKLM, $@"{Ifeo}\{exe}\PerfOptions", "CpuPriorityClass", RegistryValueKind.DWord, cls);

    internal static readonly string[] LauncherExes =
        ["steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "Battle.net.exe", "EADesktop.exe", "UbisoftConnect.exe", "RiotClientServices.exe", "Discord.exe"];

    /// <summary>Common launcher/chat Run-key names, matched case-insensitively as prefixes.</summary>
    internal static readonly string[] AutoStartNames =
        ["Steam", "EpicGamesLauncher", "Discord", "Battle.net", "EADM", "EA Desktop", "Ubisoft", "UbisoftConnect", "RiotClient", "GalaxyClient", "Spotify", "MicrosoftEdgeAutoLaunch", "OneDrive", "Teams", "com.squirrel.Teams.Teams", "Overwolf"];

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static IEnumerable<RegValueOp> LauncherAutostartTargets()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run is null) yield break;
        // 0x03 + 11 zero bytes is exactly what Task Manager writes when you disable a startup app.
        var disabled = new byte[12]; disabled[0] = 3;
        foreach (var n in run.GetValueNames())
            if (AutoStartNames.Any(a => n.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
                yield return new RegValueOp(Hive.HKCU, ApprovedRun, n, RegistryValueKind.Binary, disabled);
    }

    private static IEnumerable<Tweak> AppOptimizer()
    {
        const TweakCategory O = TweakCategory.AppOptimizer;
        yield return T(O, "opt.launcher-priority", "Lower game launcher & Discord CPU priority",
            "Runs Steam's web helper, Epic, Battle.net, EA, Ubisoft Connect, Riot Client and Discord at Below Normal CPU priority, so they never compete with your game. Uses Windows Image File Execution Options and takes effect the next time each app starts.",
            LauncherExes.Select(e => (TweakOp)CpuPriority(e, 5)).ToArray());
        yield return T(O, "opt.launcher-autostart", "Stop launchers & chat apps auto-starting",
            "Disables the sign-in auto-start of Steam, Epic, Discord, Battle.net, EA, Ubisoft, Riot, GOG Galaxy, Spotify, Edge, OneDrive, Teams and Overwolf, exactly like Task Manager's Startup tab does. The apps stay installed and work normally when opened.",
            new DynamicRegOp("Disable known launcher startup entries", false, LauncherAutostartTargets)) with { Risk = Risk.Caution };
        yield return T(O, "opt.search-host-priority", "Lower Windows Search indexer priority",
            "Runs SearchIndexer.exe and SearchProtocolHost.exe at Idle CPU priority, so indexing only happens when the CPU is otherwise free.",
            CpuPriority("SearchIndexer.exe", 1), CpuPriority("SearchProtocolHost.exe", 1));
    }

    /// <summary>(package family name, friendly name, risk, group)</summary>
    internal static readonly (string pkg, string name, Risk risk, string group)[] AppTable =
    [
        ("Microsoft.BingWeather", "Weather", Risk.Safe, "Microsoft apps"),
        ("Microsoft.BingNews", "News", Risk.Safe, "Microsoft apps"),
        ("Microsoft.BingFinance", "Money (Finance)", Risk.Safe, "Microsoft apps"),
        ("Microsoft.BingSports", "Sports", Risk.Safe, "Microsoft apps"),
        ("Microsoft.BingSearch", "Bing Search", Risk.Safe, "Microsoft apps"),
        ("Microsoft.BingTranslator", "Translator", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", Risk.Safe, "Microsoft apps"),
        ("Clipchamp.Clipchamp", "Clipchamp", Risk.Safe, "Microsoft apps"),
        ("Microsoft.GetHelp", "Get Help", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Getstarted", "Tips", Risk.Safe, "Microsoft apps"),
        ("Microsoft.WindowsMaps", "Maps", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Todos", "Microsoft To Do", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Office.OneNote", "OneNote for Windows 10", Risk.Safe, "Microsoft apps"),
        ("Microsoft.People", "People", Risk.Safe, "Microsoft apps"),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", Risk.Safe, "Microsoft apps"),
        ("Microsoft.ZuneVideo", "Movies & TV", Risk.Safe, "Microsoft apps"),
        ("Microsoft.ZuneMusic", "Media Player / Groove Music", Risk.Caution, "Microsoft apps"),
        ("Microsoft.SkypeApp", "Skype", Risk.Safe, "Microsoft apps"),
        ("MicrosoftTeams", "Teams (personal, old)", Risk.Safe, "Microsoft apps"),
        ("MSTeams", "Microsoft Teams", Risk.Caution, "Microsoft apps"),
        ("Microsoft.YourPhone", "Phone Link", Risk.Safe, "Microsoft apps"),
        ("Microsoft.WindowsAlarms", "Clock (Alarms)", Risk.Safe, "Microsoft apps"),
        ("Microsoft.WindowsCamera", "Camera", Risk.Caution, "Microsoft apps"),
        ("Microsoft.WindowsSoundRecorder", "Sound Recorder", Risk.Safe, "Microsoft apps"),
        ("microsoft.windowscommunicationsapps", "Mail and Calendar (legacy)", Risk.Safe, "Microsoft apps"),
        ("Microsoft.OutlookForWindows", "Outlook (new)", Risk.Caution, "Microsoft apps"),
        ("Microsoft.Microsoft3DViewer", "3D Viewer", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Print3D", "Print 3D", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MSPaint", "Paint 3D", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MicrosoftStickyNotes", "Sticky Notes", Risk.Caution, "Microsoft apps"),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", Risk.Safe, "Microsoft apps"),
        ("Microsoft.549981C3F5F10", "Cortana app", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Wallet", "Wallet", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Messaging", "Messaging", Risk.Safe, "Microsoft apps"),
        ("Microsoft.OneConnect", "Mobile Plans", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Windows.DevHome", "Dev Home", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Copilot", "Copilot app", Risk.Safe, "Microsoft apps"),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", Risk.Caution, "Microsoft apps"),
        ("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MicrosoftJournal", "Journal", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Whiteboard", "Whiteboard", Risk.Safe, "Microsoft apps"),
        ("Microsoft.NetworkSpeedTest", "Network Speed Test", Risk.Safe, "Microsoft apps"),
        ("Microsoft.RemoteDesktop", "Remote Desktop (Store app)", Risk.Safe, "Microsoft apps"),
        ("Microsoft.Office.Sway", "Sway", Risk.Safe, "Microsoft apps"),
        ("Microsoft.MicrosoftPowerBIForWindows", "Power BI", Risk.Safe, "Microsoft apps"),
        ("Microsoft.WindowsCalculator", "Calculator", Risk.Caution, "Built-in tools"),
        ("Microsoft.Windows.Photos", "Photos", Risk.Caution, "Built-in tools"),
        ("Microsoft.ScreenSketch", "Snipping Tool", Risk.Caution, "Built-in tools"),
        ("Microsoft.Paint", "Paint", Risk.Caution, "Built-in tools"),
        ("Microsoft.WindowsNotepad", "Notepad (Store)", Risk.Caution, "Built-in tools"),
        ("MicrosoftWindows.Client.WebExperience", "Widgets platform", Risk.Caution, "Built-in tools"),
        ("Microsoft.XboxApp", "Xbox Console Companion", Risk.Safe, "Xbox"),
        ("Microsoft.GamingApp", "Xbox app", Risk.Caution, "Xbox"),
        ("Microsoft.XboxGamingOverlay", "Xbox Game Bar", Risk.Caution, "Xbox"),
        ("Microsoft.XboxGameOverlay", "Xbox Game Overlay", Risk.Caution, "Xbox"),
        ("Microsoft.XboxSpeechToTextOverlay", "Xbox Speech-to-Text overlay", Risk.Safe, "Xbox"),
        ("Microsoft.Xbox.TCUI", "Xbox TCUI", Risk.Caution, "Xbox"),
        ("SpotifyAB.SpotifyMusic", "Spotify (preinstalled)", Risk.Safe, "Third-party bloat"),
        ("Disney.37853FC22B2CE", "Disney+", Risk.Safe, "Third-party bloat"),
        ("4DF9E0F8.Netflix", "Netflix", Risk.Safe, "Third-party bloat"),
        ("AmazonVideo.PrimeVideo", "Prime Video", Risk.Safe, "Third-party bloat"),
        ("BytedancePte.Ltd.TikTok", "TikTok", Risk.Safe, "Third-party bloat"),
        ("Facebook.Facebook", "Facebook", Risk.Safe, "Third-party bloat"),
        ("Facebook.Instagram", "Instagram", Risk.Safe, "Third-party bloat"),
        ("king.com.CandyCrushSaga", "Candy Crush Saga", Risk.Safe, "Third-party bloat"),
        ("king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga", Risk.Safe, "Third-party bloat"),
        ("king.com.BubbleWitch3Saga", "Bubble Witch 3 Saga", Risk.Safe, "Third-party bloat"),
        ("7EE7776C.LinkedInforWindows", "LinkedIn", Risk.Safe, "Third-party bloat"),
        ("5A894077.McAfeeSecurity", "McAfee Security (Store)", Risk.Safe, "Third-party bloat"),
        ("AdobeSystemsIncorporated.AdobePhotoshopExpress", "Photoshop Express", Risk.Safe, "Third-party bloat"),
        ("Flipboard.Flipboard", "Flipboard", Risk.Safe, "Third-party bloat"),
        ("ShazamEntertainmentLtd.Shazam", "Shazam", Risk.Safe, "Third-party bloat"),
        ("Duolingo-LearnLanguagesforFree", "Duolingo", Risk.Safe, "Third-party bloat"),
    ];

    private static IEnumerable<Tweak> AppRemoval()
    {
        foreach (var (pkg, name, risk, group) in AppTable)
        {
            yield return new Tweak
            {
                Category = TweakCategory.AppRemoval,
                Id = "app." + pkg.ToLowerInvariant(),
                Name = "Remove " + name,
                Description = risk == Risk.Caution
                    ? $"Uninstalls {name} for your account. Other apps or features may depend on it; you can reinstall it from the Microsoft Store. [{pkg}]"
                    : $"Uninstalls {name} for your account. Undo re-registers it if Windows still has the files, otherwise reinstall it from the Microsoft Store. [{pkg}]",
                Risk = risk,
                Group = group,
                Ops = [new AppxRemoveOp(pkg)],
            };
        }
        yield return T(TweakCategory.AppRemoval, "app.onedrive-win32", "Remove OneDrive (desktop app)",
            "Runs Microsoft's own OneDrive uninstaller. Your files in the OneDrive folder are left untouched. Undo runs the OneDrive installer that ships with Windows, if it's still present.",
            Cmd(@"if exist ""%SystemRoot%\System32\OneDriveSetup.exe"" (""%SystemRoot%\System32\OneDriveSetup.exe"" /uninstall) else if exist ""%SystemRoot%\SysWOW64\OneDriveSetup.exe"" (""%SystemRoot%\SysWOW64\OneDriveSetup.exe"" /uninstall) else (winget uninstall --id Microsoft.OneDrive -e --silent --accept-source-agreements)",
                @"if exist ""%SystemRoot%\System32\OneDriveSetup.exe"" (start """" ""%SystemRoot%\System32\OneDriveSetup.exe"") else (winget install --id Microsoft.OneDrive -e --silent --accept-source-agreements --accept-package-agreements)",
                @"if exist ""%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe"" (echo present) else if exist ""%ProgramFiles%\Microsoft OneDrive\OneDrive.exe"" (echo present) else (echo absent)", "absent",
                admin: false, label: "OneDriveSetup.exe /uninstall")) with { Risk = Risk.Caution, Group = "Microsoft apps", Warning = "Files that are only stored online (cloud icon) won't be available offline after OneDrive is removed." };
    }

    /// <summary>(id, game, process exe names). Kept to the main games; the Game Library handles any other game at runtime.</summary>
    public static readonly (string id, string game, string[] exes)[] GameTable =
    [
        ("fortnite", "Fortnite", ["FortniteClient-Win64-Shipping.exe"]),
        ("valorant", "VALORANT", ["VALORANT-Win64-Shipping.exe"]),
        ("cs2", "Counter-Strike 2", ["cs2.exe"]),
        ("apex", "Apex Legends", ["r5apex.exe", "r5apex_dx12.exe"]),
        ("rocketleague", "Rocket League", ["RocketLeague.exe"]),
        ("gta5", "Grand Theft Auto V Legacy", ["GTA5.exe"]),
        ("gta5-enhanced", "Grand Theft Auto V Enhanced", ["GTA5_Enhanced.exe"]),
        ("roblox", "Roblox Player", ["RobloxPlayerBeta.exe"]),
        ("marvelrivals", "Marvel Rivals", ["Marvel-Win64-Shipping.exe"]),
    ];

    /// <summary>
    /// Entries removed from the list above. They're only shown (undo-only) if this PC still has the setting applied, so
    /// nobody is left with a registry value they can't undo. Minecraft's was harmful: javaw.exe is every Java program.
    /// </summary>
    internal static readonly (string id, string game, string exe)[] RetiredGameTable =
    [
        ("cod", "Call of Duty (HQ / Warzone)", "cod.exe"),
        ("r6", "Rainbow Six Siege", "RainbowSix.exe"),
        ("overwatch", "Overwatch 2", "Overwatch.exe"),
        ("minecraft", "Minecraft: Java Edition (javaw.exe, affects all Java programs)", "javaw.exe"),
        ("lol", "League of Legends", "League of Legends.exe"),
        ("pubg", "PUBG: Battlegrounds", "TslGame.exe"),
        ("rust", "Rust", "RustClient.exe"),
    ];

    private static bool PriorityApplied(string exe)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{Ifeo}\{exe}\PerfOptions");
            return k?.GetValue("CpuPriorityClass") is int v && v == 3;
        }
        catch { return false; }
    }

    private static IEnumerable<Tweak> GameProfiles()
    {
        foreach (var (id, game, exes) in GameTable)
        {
            yield return T(TweakCategory.GameProfiles, "game." + id, $"{game}: High CPU priority",
                $"Tells Windows to always start {string.Join(" / ", exes)} at High CPU priority, so background apps can't steal cycles mid-match. Applied through Image File Execution Options; no game files are touched. Takes effect on the next launch.",
                exes.Select(e => (TweakOp)CpuPriority(e, 3)).ToArray()) with { Group = "CPU priority" };
        }
        foreach (var (id, game, exe) in RetiredGameTable)
        {
            if (!PriorityApplied(exe)) continue;
            yield return T(TweakCategory.GameProfiles, "game." + id, $"{game}: High CPU priority (retired, undo only)",
                $"No longer offered. It's listed because {exe} still starts at High priority on this PC; undo it here. Use the Game Library's runtime priority option instead, which only affects the verified game process.",
                CpuPriority(exe, 3)) with { Group = "Retired (undo only)" };
        }
    }

    private static IEnumerable<Tweak> Bios()
    {
        yield return new Tweak
        {
            Category = TweakCategory.Bios, Id = "bios.rebar", Kind = TweakKind.Guide, Name = "Enable Resizable BAR",
            Description = "Lets the CPU access all of your GPU's video memory at once. Supported on RTX 30+/RX 6000+/Arc GPUs with a compatible motherboard. Mark it as done once you've enabled it.",
            GuideSteps =
            [
                "Update your motherboard BIOS and GPU driver to the latest version.",
                "Make sure Windows boots in UEFI mode with a GPT disk (msinfo32 → BIOS Mode: UEFI).",
                "Enter BIOS/UEFI (usually Del or F2 at boot).",
                "Enable 'Above 4G Decoding' (sometimes under PCI Subsystem / Advanced).",
                "Enable 'Re-Size BAR Support' (AMD boards may call it 'Smart Access Memory').",
                "Disable CSM / Legacy boot if it's enabled, then save and exit.",
                "Verify in NVIDIA Control Panel → System Information → Resizable BAR: Yes, or in AMD Software → System.",
            ],
        };
        yield return new Tweak
        {
            Category = TweakCategory.Bios, Id = "bios.xmp", Kind = TweakKind.Guide, Risk = Risk.Caution, Name = "Enable XMP / EXPO memory profile",
            Description = "Most RAM runs at a slow JEDEC default (e.g. 4800 MT/s DDR5) until you enable its rated XMP (Intel) or EXPO (AMD) profile. The Dashboard shows your configured vs rated memory speed.",
            Warning = "If the PC fails to boot after enabling it, clear CMOS or let the board retry with defaults, then try a lower profile.",
            GuideSteps =
            [
                "Enter BIOS/UEFI (Del or F2 at boot).",
                "Find the memory section: Ai Tweaker (ASUS), OC / Extreme Memory Profile (MSI), Tweaker (Gigabyte), OC Tweaker (ASRock).",
                "Set XMP / EXPO / DOCP to 'Profile 1'.",
                "Save and exit. The first boot may take longer while memory trains.",
                "Check Task Manager → Performance → Memory → Speed to confirm the rated speed.",
            ],
        };
        yield return new Tweak
        {
            Category = TweakCategory.Bios, Id = "bios.cstates", Kind = TweakKind.Guide, Risk = Risk.Caution, Name = "Disable CPU C-States",
            Description = "Deep CPU sleep states save power but add a little wake-up latency. Disabling them can help consistency in competitive games on desktops, at the cost of higher idle power and temperatures.",
            Warning = "Not recommended on laptops. Some CPUs boost less with C-States off, so benchmark before and after.",
            GuideSteps =
            [
                "Enter BIOS/UEFI (Del or F2 at boot).",
                "Open CPU / Advanced CPU Configuration / CPU Power Management.",
                "Set 'Global C-state Control' (AMD) or 'CPU C-States' (Intel) to Disabled.",
                "Optionally disable 'Package C-State Limit' or set it to C0/C1.",
                "Save and exit. Keep the Money Tweaks 'Ultimate Performance' plan for the Windows side.",
            ],
        };
    }
}
