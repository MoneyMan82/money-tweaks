using Microsoft.Win32;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

/// <summary>
/// Library expansion (1.79): settings found by reviewing other Windows tools' tweak lists and Microsoft's policy
/// documentation, re-implemented as Money Tweaks ops with original descriptions. No code, text or assets were copied:
/// the registry locations and values are Windows' own (documented policies and settings). Each tweak names where the idea
/// came from in <see cref="Tweak.Source"/>; docs/TWEAK-SOURCES.md lists every source with its license.
/// </summary>
public static partial class TweakCatalog
{
    private const string SrcWinUtil = "Chris Titus Tech WinUtil (MIT) · github.com/ChrisTitusTech/winutil";
    private const string SrcDebloat = "Win11Debloat by Raphire (MIT) · github.com/Raphire/Win11Debloat";
    private const string SrcSophia = "Sophia Script for Windows (MIT) · github.com/farag2/Sophia-Script-for-Windows";
    private const string SrcOo = "O&O ShutUp10++ setting list (freeware; idea only) · oo-software.com/en/shutup10";
    private const string SrcOptimizer = "Optimizer by hellzerg (GPL-3.0; idea only, no code used) · github.com/hellzerg/optimizer";

    private const string ProEditions = "Pro, Enterprise and Education (Home ignores this policy)";
    private const string ExplorerPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string Blocked = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string EdgePol = @"SOFTWARE\Policies\Microsoft\Edge";

    private static RegValueOp CUk(string path, string name, int v) => new(Hive.HKCU, path, name, RegistryValueKind.DWord, v) { RemoveKeyIfCreated = true };

    /// <summary>NetBIOS settings of every network interface (NetBT's per-interface keys).</summary>
    private static IEnumerable<string> NetBtInterfaces()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces";
        using var k = Registry.LocalMachine.OpenSubKey(root);
        if (k is null) yield break;
        foreach (var n in k.GetSubKeyNames().Where(n => n.StartsWith("Tcpip_", StringComparison.OrdinalIgnoreCase))) yield return $@"{root}\{n}";
    }

    private static IEnumerable<Tweak> Expanded()
    {
        const TweakCategory G = TweakCategory.General, D = TweakCategory.Debloating, N = TweakCategory.Networking, A = TweakCategory.Advanced;
        const string Priv = "Privacy", Ads = "Ads & suggestions", Ai = "Shell & AI", Edge = "Microsoft Edge", Browsers = "Browsers", Wu = "Windows Update";

        // ---- General: Explorer, taskbar and interface ------------------------------------------------------------------
        yield return T(G, "gen.explorer-compact", "Compact view in File Explorer",
            "Uses Windows 11's compact spacing in File Explorer (Folder Options → Decrease space between items), so more files fit on screen.",
            CU(Adv, "UseCompactMode", 1, 0)) with { MinBuild = 22000, Group = "Explorer", Source = SrcSophia };
        yield return T(G, "gen.transfer-details", "Detailed file copy dialog",
            "Opens the copy/move progress window with its details expanded (speed graph and items remaining) instead of the small version.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\OperationStatusManager", "EnthusiastMode", 1)) with { Group = "Explorer", Source = SrcSophia };
        yield return T(G, "gen.shortcut-suffix", "No \"- Shortcut\" in new shortcut names",
            "New shortcuts are named after the file or app only, without the \" - Shortcut\" ending Windows adds. Existing shortcuts keep their names.",
            CUs(@"Software\Microsoft\Windows\CurrentVersion\Explorer\NamingTemplates", "ShortcutNameTemplate", "%s.lnk")) with { Group = "Explorer", Source = SrcSophia };
        yield return T(G, "gen.folder-discovery", "Stop File Explorer guessing folder types",
            "File Explorer inspects each folder's files to choose a layout (pictures, music, documents), which can make large folders open slowly. This uses the general layout for all folders instead.",
            CUs(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified")) with { Group = "Explorer", Source = SrcWinUtil };
        yield return T(G, "gen.explorer-home-gallery", "Hide Home and Gallery in File Explorer",
            "Removes the Home and Gallery entries from File Explorer's navigation pane on Windows 11. The folders themselves are not touched; this only unpins them from the pane.",
            new RegValueOp(Hive.HKCU, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree", RegistryValueKind.DWord, 0) { RemoveKeyIfCreated = true },
            new RegValueOp(Hive.HKCU, @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", RegistryValueKind.DWord, 0) { RemoveKeyIfCreated = true })
            with { MinBuild = 22621, Group = "Explorer", Source = SrcWinUtil };
        yield return T(G, "gen.ctx-give-access", "Remove \"Give access to\" from right-click menus",
            "Blocks the legacy network-sharing entry in the classic context menu, using Windows' own list of blocked shell extensions for your account. Sharing still works from file Properties. Takes effect after Explorer restarts.",
            CUs(Blocked, "{f81e9010-6ea4-11ce-a7ff-00aa003ca9f6}", "")) with { Group = "Explorer", Source = SrcDebloat };
        yield return T(G, "gen.ctx-include-library", "Remove \"Include in library\" from right-click menus",
            "Blocks the Libraries entry in the classic folder context menu (per account, through the blocked shell extensions list). Takes effect after Explorer restarts.",
            CUs(Blocked, "{3dad6c5d-2167-4cae-9914-f99e41c12cfa}", "")) with { Group = "Explorer", Source = SrcDebloat };
        yield return T(G, "gen.ctx-share", "Remove \"Share\" from the classic right-click menu",
            "Blocks the modern Share entry in the classic context menu (Show more options), per account. The Share button in File Explorer's toolbar stays. Takes effect after Explorer restarts.",
            CUs(Blocked, "{e2bf9676-5f8f-435c-97eb-11607a5bedf7}", "")) with { Group = "Explorer", Source = SrcDebloat };
        yield return T(G, "gen.store-open-with", "No \"Look for an app in the Microsoft Store\" in Open with",
            "Removes the Store suggestion from the Open with dialog and the prompt Windows shows for unknown file types.",
            LM(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "NoUseStoreOpenWith", 1)) with { Group = "Explorer", Source = SrcSophia };
        yield return T(G, "gen.taskbar-never-combine", "Never combine taskbar buttons",
            "Shows every open window as its own taskbar button with its title, instead of grouping windows of the same app into one icon.",
            CU(Adv, "TaskbarGlomLevel", 2, 0)) with { MinBuild = 22631, Group = "Taskbar", Source = SrcDebloat };
        yield return T(G, "gen.taskbar-last-active", "Click a grouped taskbar icon to open its last window",
            "Clicking an app with several windows switches straight to the one you used last, instead of showing thumbnails first.",
            CU(Adv, "LastActiveClick", 1)) with { Group = "Taskbar", Source = SrcDebloat };
        yield return T(G, "gen.clock-seconds", "Show seconds in the taskbar clock",
            "Adds seconds to the taskbar clock (Settings → Taskbar behaviours). Uses a little more power on laptops.",
            CU(Adv, "ShowSecondsInSystemClock", 1, 0)) with { MinBuild = 22621, Group = "Taskbar", Source = SrcSophia };
        yield return T(G, "gen.battery-percent", "Battery percentage on the taskbar",
            "Shows the remaining battery percentage next to the battery icon. Laptops and tablets only; does nothing on desktops.",
            CU(Adv, "IsBatteryPercentageEnabled", 1)) with { MinBuild = 26100, Group = "Taskbar", Source = SrcWinUtil };
        yield return T(G, "gen.alttab-no-tabs", "Alt+Tab shows windows only, not browser tabs",
            "Stops Microsoft Edge tabs from appearing as separate entries in Alt+Tab, so it lists only open windows.",
            CU(Adv, "MultiTaskingAltTabFilter", 3)) with { Group = "Interface", Source = SrcDebloat };
        yield return T(G, "gen.snap-assist", "Don't suggest other windows when snapping",
            "After you snap a window to one side, Windows no longer fills the other side with thumbnails of other windows to pick from. Snapping itself keeps working.",
            CU(Adv, "SnapAssist", 0, 1)) with { Group = "Interface", Source = SrcDebloat };
        yield return T(G, "gen.wallpaper-quality", "Full-quality JPEG wallpapers",
            "Windows recompresses JPEG wallpapers to 85% quality, which adds visible artefacts on large or sharp images. This keeps them at 100%. Applies the next time you set a wallpaper.",
            CU(@"Control Panel\Desktop", "JPEGImportQuality", 100)) with { Group = "Interface", Source = SrcSophia };
        yield return T(G, "gen.numlock-on", "Num Lock on after sign-in",
            "Turns Num Lock on when you sign in, for your account. (The sign-in screen itself uses the system default.)",
            CUs(@"Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0")) with { Group = "Input", Source = SrcWinUtil };
        yield return T(G, "gen.default-printer", "Don't let Windows change the default printer",
            "Windows normally makes the last printer you used the default. With this, the default printer stays the one you chose.",
            CU(@"Software\Microsoft\Windows NT\CurrentVersion\Windows", "LegacyDefaultPrinterMode", 1, 0)) with { Group = "Interface", Source = SrcSophia };
        yield return T(G, "gen.logon-blur", "Sharp sign-in background (no blur)",
            "Shows the sign-in screen's background picture without the acrylic blur effect.",
            LM(Pol + @"\System", "DisableAcrylicBackgroundOnLogon", 1)) with { Group = "Interface", Source = SrcWinUtil };
        yield return T(G, "gen.first-logon-animation", "Skip the \"Hi / We're getting things ready\" animation",
            "Skips the full-screen animation shown on the first sign-in after a feature update or when a new account signs in, so the desktop appears sooner.",
            LM(SystemPolicies, "EnableFirstLogonAnimation", 0)) with { Group = "Startup", Source = SrcSophia };
        yield return T(G, "gen.no-lock-screen", "Skip the lock screen",
            "Goes straight to the sign-in prompt instead of the lock screen picture first. Signing in is still required.",
            LM(Pol + @"\Personalization", "NoLockScreen", 1)) with { Editions = ProEditions, Group = "Startup", Source = SrcWinUtil };
        yield return T(G, "gen.modern-standby-network", "Disconnect the network during Modern Standby",
            "On laptops and tablets with Modern Standby (S0 sleep), stops the PC staying online while it's asleep, which saves battery and avoids updates or downloads in your bag. No effect on PCs with classic sleep.",
            LM(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", "ACSettingIndex", 0),
            LM(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", "DCSettingIndex", 0)) with { Group = "Power", Source = SrcDebloat };
        yield return T(G, "gen.gamebar-controller", "Xbox controller button doesn't open Game Bar",
            "Stops the Xbox (Guide) button on a controller from opening Game Bar in the middle of a game. Win+G still opens it.",
            CU(@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, 1)) with { Group = "Gaming", Source = SrcDebloat };

        // ---- Debloating: privacy ------------------------------------------------------------------------------------------
        yield return T(D, "dbl.diag-logs", "Limit diagnostic logs, dumps and remote settings",
            "Stops Windows from sending extra diagnostic logs and memory dumps with its diagnostic data, and from downloading OneSettings (remote configuration that can switch data collection on). Uses Microsoft's DataCollection policies.",
            LM(Pol + @"\DataCollection", "LimitDiagnosticLogCollection", 1),
            LM(Pol + @"\DataCollection", "LimitDumpCollection", 1),
            LM(Pol + @"\DataCollection", "DisableOneSettingsDownloads", 1)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.language-list", "Don't share your language list with websites",
            "Websites can read the languages installed on Windows to choose local content. This keeps that list private (Settings → Privacy → General).",
            CU(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)) with { Group = Priv, Source = SrcSophia };
        yield return T(D, "dbl.jumplist-recent", "Don't track recently opened files in Start and Jump Lists",
            "Stops Windows recording which documents you open for Jump Lists, Start and File Explorer's recent list. Not fully reversible: Windows clears the existing recent-files list when tracking is turned off, and Undo only turns tracking back on.",
            CU(Adv, "Start_TrackDocs", 0, 1)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.cloud-search", "Don't search cloud content from Windows Search",
            "Keeps Windows Search to this PC: it no longer includes OneDrive, Outlook or work-account cloud results.",
            LM(Pol + @"\Windows Search", "AllowCloudSearch", 0)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.speech-models", "Don't download speech recognition model updates",
            "Stops Windows downloading updated speech recognition and synthesis models in the background. Built-in voices keep working.",
            LM(@"SOFTWARE\Policies\Microsoft\Speech", "AllowSpeechModelUpdate", 0)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.message-sync", "Don't back up text messages to the cloud",
            "Stops Windows syncing SMS/MMS messages from a connected phone or cellular modem to Microsoft's cloud.",
            LM(Pol + @"\Messaging", "AllowMessageSync", 0)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.map-downloads", "Stop automatic map data downloads",
            "Stops the Maps component downloading and updating offline map data on its own, and the network traffic its settings page makes.",
            LM(Pol + @"\Maps", "AutoDownloadAndUpdateMapData", 0),
            LM(Pol + @"\Maps", "AllowUntriggeredNetworkTrafficOnSettingsPage", 0)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.onedrive-presignin", "No OneDrive network traffic before sign-in",
            "Stops OneDrive contacting Microsoft while Windows is on the sign-in screen.",
            LM(@"SOFTWARE\Microsoft\OneDrive", "PreventNetworkTrafficPreUserSignIn", 1)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.wmdrm-online", "Don't let Windows Media DRM go online",
            "Stops the legacy Windows Media DRM component contacting license servers. Only old protected WMA/WMV files are affected.",
            LM(@"SOFTWARE\Policies\Microsoft\WMDRM", "DisableOnline", 1)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.signin-info", "Don't use your sign-in info to finish updates",
            "After an update restart, Windows can sign you in automatically to finish setting up and reopen apps. This turns that off, so nothing signs in for you.",
            LM(SystemPolicies, "DisableAutomaticRestartSignOn", 1)) with { Group = Priv, Source = SrcSophia };
        yield return T(D, "dbl.app-permissions", "Block apps from personal data they rarely need",
            "Denies Store apps access to your account info, contacts, calendar, call history, email, messages, phone calls, tasks and other apps' diagnostic info, using Windows' app-privacy policies. Desktop programs and games aren't affected.",
            LM(Pol + @"\AppPrivacy", "LetAppsAccessAccountInfo", 2), LM(Pol + @"\AppPrivacy", "LetAppsAccessContacts", 2),
            LM(Pol + @"\AppPrivacy", "LetAppsAccessCalendar", 2), LM(Pol + @"\AppPrivacy", "LetAppsAccessCallHistory", 2),
            LM(Pol + @"\AppPrivacy", "LetAppsAccessEmail", 2), LM(Pol + @"\AppPrivacy", "LetAppsAccessMessaging", 2),
            LM(Pol + @"\AppPrivacy", "LetAppsAccessPhone", 2), LM(Pol + @"\AppPrivacy", "LetAppsAccessTasks", 2),
            LM(Pol + @"\AppPrivacy", "LetAppsGetDiagnosticInfo", 2)) with
        {
            Risk = Risk.Caution, Group = Priv, Source = SrcOo,
            Warning = "Store apps that need these (Outlook/Mail, Calendar, People, Phone Link, To Do) stop working with your data until you undo this.",
        };
        yield return T(D, "dbl.voice-activation", "Don't let apps listen for voice keywords",
            "Stops Store apps from being started by voice keywords, including from the lock screen.",
            LM(Pol + @"\AppPrivacy", "LetAppsActivateWithVoice", 2),
            LM(Pol + @"\AppPrivacy", "LetAppsActivateWithVoiceAboveLock", 2)) with { Group = Priv, Source = SrcOo };
        yield return T(D, "dbl.nvidia-telemetry", "Opt out of NVIDIA driver telemetry",
            "Sets NVIDIA's own opt-out preference for usage data collected by the driver's control panel components. Shown as not available without an NVIDIA driver.",
            new DynamicRegOp("NVIDIA OptInOrOutPreference = 0", true, () =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NVIDIA Corporation");
                return k is null ? [] : [new RegValueOp(Hive.HKLM, @"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", "OptInOrOutPreference", RegistryValueKind.DWord, 0)];
            })) with { Group = Priv, Source = SrcOptimizer };

        // ---- Debloating: ads, AI and suggestions --------------------------------------------------------------------------
        yield return T(D, "dbl.settings-365-ads", "Hide Microsoft 365 and account promotions in Settings",
            "Removes the Microsoft 365, Game Pass and account-backup promotion cards from Settings' account and home pages.",
            LM(Pol + @"\CloudContent", "DisableConsumerAccountStateContent", 1)) with { Group = Ads, Source = SrcDebloat };
        yield return T(D, "dbl.settings-home", "Hide the Settings Home page",
            "Settings opens on System instead of the Home page with its recommended cards and promotions.",
            LMs(ExplorerPolicies, "SettingsPageVisibility", "hide:home")) with { MinBuild = 22631, Group = Ads, Source = SrcDebloat };
        yield return T(D, "dbl.start-phone-link", "Hide the Phone Link panel in Start",
            "Removes the mobile-device companion panel next to the Start menu.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", "IsEnabled", 0)) with { MinBuild = 26100, Group = Ads, Source = SrcDebloat };
        yield return T(D, "dbl.start-recently-added", "Hide recently added apps in Start",
            "Stops Start highlighting apps you just installed.",
            LM(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecentlyAddedApps", 1)) with { Group = Ads, Source = SrcSophia };
        yield return T(D, "dbl.click-to-do", "Disable Click to Do",
            "Turns off Click to Do on Copilot+ PCs, which analyses what's on screen when you press Win+Click. No effect on other PCs.",
            CU(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
            LM(Pol + @"\WindowsAI", "DisableClickToDo", 1)) with { MinBuild = 26100, Group = Ai, Source = SrcDebloat };
        yield return T(D, "dbl.notepad-ai", "Disable AI features in Notepad",
            "Turns off Notepad's Copilot features (rewrite, summarize) through Notepad's own policy.",
            LM(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)) with { MinBuild = 22000, Group = Ai, Source = SrcDebloat };
        yield return T(D, "dbl.paint-ai", "Disable AI features in Paint",
            "Turns off Paint's Cocreator, Image Creator, generative fill and erase, and background removal through Paint's policies.",
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeErase", 1),
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableRemoveBackground", 1)) with { MinBuild = 22000, Group = Ai, Source = SrcDebloat };

        // ---- Debloating: Windows Update behaviour ------------------------------------------------------------------------
        yield return T(D, "dbl.update-asap", "Don't get the latest updates as soon as they're available",
            "Turns off Windows Update's \"Get the latest updates as soon as they're available\", so optional feature rollouts arrive with the normal monthly updates. Security updates are not delayed.",
            LM(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "IsContinuousInnovationOptedIn", 0)) with { MinBuild = 22621, Group = Wu, Source = SrcDebloat };
        yield return T(D, "dbl.no-auto-reboot", "No automatic restart while you're signed in",
            "Windows Update waits for you to restart instead of restarting on its own while someone is signed in (for example during a long game or download). Updates still install; remember to restart.",
            LM(Pol + @"\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1)) with { Editions = ProEditions, Group = Wu, Source = SrcDebloat };

        // ---- Debloating: browsers ----------------------------------------------------------------------------------------
        yield return T(D, "dbl.edge-ai", "Edge: turn off Copilot and AI features",
            "Edge policies that stop Copilot reading the page you're on, hide the Microsoft 365 Copilot icon, and turn off inline Compose, AI history search, Bing Chat on the new tab page and visual search.",
            LM(EdgePol, "CopilotPageContext", 0), LM(EdgePol, "CopilotCDPPageContext", 0), LM(EdgePol, "Microsoft365CopilotChatIconEnabled", 0),
            LM(EdgePol, "ComposeInlineEnabled", 0), LM(EdgePol, "EdgeHistoryAISearchEnabled", 0), LM(EdgePol, "NewTabPageBingChatEnabled", 0),
            LM(EdgePol, "VisualSearchEnabled", 0)) with { Group = Edge, Source = SrcDebloat };
        yield return T(D, "dbl.edge-promos", "Edge: no news feed, promotions or first-run pages",
            "Edge policies that remove the news feed and default sponsored tiles from the new tab page, Microsoft Rewards, the Acrobat button, recommendations, default-browser campaigns and the first-run setup pages.",
            LM(EdgePol, "NewTabPageContentEnabled", 0), LM(EdgePol, "NewTabPageHideDefaultTopSites", 1), LM(EdgePol, "ShowMicrosoftRewards", 0),
            LM(EdgePol, "ShowAcrobatSubscriptionButton", 0), LM(EdgePol, "ShowRecommendationsEnabled", 0), LM(EdgePol, "SpotlightExperiencesAndRecommendationsEnabled", 0),
            LM(EdgePol, "DefaultBrowserSettingsCampaignEnabled", 0), LM(EdgePol, "HideFirstRunExperience", 1)) with { Group = Edge, Source = SrcOo };
        yield return T(D, "dbl.edge-lookups", "Edge: fewer background lookups while browsing",
            "Edge policies that stop it sending failed addresses to Microsoft for suggestions, asking sites about saved payment methods, using cloud tab services, and sending in-browser feedback. Safe Browsing (SmartScreen) is left on.",
            LM(EdgePol, "AlternateErrorPagesEnabled", 0), LM(EdgePol, "ResolveNavigationErrorsUseWebService", 0), LM(EdgePol, "PaymentMethodQueryEnabled", 0),
            LM(EdgePol, "TabServicesEnabled", 0), LM(EdgePol, "UserFeedbackAllowed", 0)) with { Group = Edge, Source = SrcOo };
        yield return T(D, "dbl.edge-desktop-shortcut", "Edge: no new desktop shortcut after updates",
            "Stops Edge's updater putting its shortcut back on the desktop after each update.",
            LM(@"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault", 0)) with { Group = Edge, Source = SrcSophia };
        yield return T(D, "dbl.chrome-ai", "Chrome: turn off Gemini and AI features",
            "Google Chrome policies that turn off Gemini in Chrome, Help me write, AI tab compare, AI history search, AI themes, AI autofill predictions, DevTools AI and the on-device AI model. No effect if Chrome isn't installed.",
            LM(@"SOFTWARE\Policies\Google\Chrome", "GeminiSettings", 1), LM(@"SOFTWARE\Policies\Google\Chrome", "HelpMeWriteSettings", 2),
            LM(@"SOFTWARE\Policies\Google\Chrome", "TabCompareSettings", 2), LM(@"SOFTWARE\Policies\Google\Chrome", "HistorySearchSettings", 2),
            LM(@"SOFTWARE\Policies\Google\Chrome", "CreateThemesSettings", 2), LM(@"SOFTWARE\Policies\Google\Chrome", "AutofillPredictionSettings", 2),
            LM(@"SOFTWARE\Policies\Google\Chrome", "DevToolsGenAiSettings", 2), LM(@"SOFTWARE\Policies\Google\Chrome", "GenAILocalFoundationalModelSettings", 1)) with { Group = Browsers, Source = SrcDebloat };
        yield return T(D, "dbl.chrome-telemetry", "Chrome: don't send usage statistics",
            "Google Chrome policies that turn off usage and crash statistics, Chrome Cleanup reporting and the feedback tool. No effect if Chrome isn't installed.",
            LM(@"SOFTWARE\Policies\Google\Chrome", "MetricsReportingEnabled", 0), LM(@"SOFTWARE\Policies\Google\Chrome", "ChromeCleanupReportingEnabled", 0),
            LM(@"SOFTWARE\Policies\Google\Chrome", "UserFeedbackAllowed", 0)) with { Group = Browsers, Source = SrcOptimizer };
        yield return T(D, "dbl.firefox-telemetry", "Firefox: no telemetry, studies or default-browser agent",
            "Mozilla Firefox policies that turn off telemetry, Shield studies and the background agent that reports your default browser. No effect if Firefox isn't installed.",
            LM(@"SOFTWARE\Policies\Mozilla\Firefox", "DisableTelemetry", 1), LM(@"SOFTWARE\Policies\Mozilla\Firefox", "DisableFirefoxStudies", 1),
            LM(@"SOFTWARE\Policies\Mozilla\Firefox", "DisableDefaultBrowserAgent", 1)) with { Group = Browsers, Source = SrcOptimizer };
        yield return T(D, "dbl.brave-extras", "Brave: hide Rewards, Wallet, VPN and Leo AI",
            "Brave policies that turn off Brave Rewards, the crypto Wallet, the VPN upsell and the Leo AI assistant. No effect if Brave isn't installed.",
            LM(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveRewardsDisabled", 1), LM(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveWalletDisabled", 1),
            LM(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveVPNDisabled", 1), LM(@"SOFTWARE\Policies\BraveSoftware\Brave", "BraveAIChatEnabled", 0)) with { Group = Browsers, Source = SrcWinUtil };

        // ---- Networking ---------------------------------------------------------------------------------------------------
        yield return T(N, "net.smb1-server-off", "Make sure the SMBv1 file-sharing server is off",
            "Turns off the server side of the obsolete SMBv1 protocol (used by WannaCry). Windows 10/11 normally ship without it; this makes sure it stays off even if something enabled it. Modern SMBv2/3 sharing is unaffected.",
            LM(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1", 0)) with { RequiresReboot = true, Group = "Security", Source = SrcOptimizer };
        yield return T(N, "net.netbios-off", "Disable NetBIOS over TCP/IP",
            "Turns off the legacy NetBIOS name service on every network interface, which answers name broadcasts on the local network. Modern sharing uses DNS and SMB over TCP 445.",
            new DynamicRegOp("NetbiosOptions=2 on all interfaces", true, () => NetBtInterfaces().Select(p => new RegValueOp(Hive.HKLM, p, "NetbiosOptions", RegistryValueKind.DWord, 2) { DefaultValue = 0 }))) with
        {
            Risk = Risk.Caution, RequiresReboot = true, Group = "Security", Source = SrcOptimizer,
            Warning = "Very old NAS boxes, printers or Windows XP-era shares that are found only by NetBIOS name stop showing up; connect by IP address or undo.",
        };

        // ---- Advanced -----------------------------------------------------------------------------------------------------
        yield return T(A, "adv.long-paths", "Allow file paths longer than 260 characters",
            "Lets programs that support it (most modern tools, Git, Node, game mod managers) use paths beyond Windows' classic 260-character limit, avoiding \"path too long\" errors in deep folders.",
            LM(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)) with { Group = "File system", Source = SrcWinUtil };
        yield return T(A, "adv.thumbnail-cache", "Keep the thumbnail cache",
            "Stops Windows' automatic maintenance from deleting the thumbnail cache, so folders full of pictures and videos don't rebuild previews (and stutter) after cleanups. The cache can still be cleared by hand in Disk Cleanup.",
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache", "Autorun", 0, 1)) with { Group = "File system", Source = SrcSophia };
        yield return T(A, "adv.reserved-storage", "Disable Reserved Storage",
            "Windows keeps about 7 GB set aside for updates. This releases it after the next update. If the disk is nearly full, an update can then fail until you free space.",
            Cmd("DISM.exe /Online /Set-ReservedStorageState /State:Disabled", "DISM.exe /Online /Set-ReservedStorageState /State:Enabled",
                "(Get-WindowsReservedStorageState).ReservedStorageState", @"^Disabled", label: "DISM reserved storage: disabled", checkShell: Shell.PowerShell)) with
        {
            Risk = Risk.Caution, MinBuild = 18362, Group = "File system", Source = SrcWinUtil,
            Warning = "Windows refuses while an update is pending or in progress (then nothing changes). Re-enabling takes effect the same way.",
        };
        yield return T(A, "adv.restore-point-frequency", "Allow restore points more than once a day",
            "Windows silently skips a new restore point if one was made in the last 24 hours. This lets Money Tweaks (and you) create one before every set of changes.",
            LM(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 0, 1440)) with { Group = "Recovery", Source = SrcWinUtil };
        yield return T(A, "adv.bsod-details", "Show details on blue screens",
            "Blue screens show the stop code's parameters and the driver involved instead of the sad face, which makes crashes much easier to look up (and Crash Detective's results easier to confirm).",
            LM(@"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, 0),
            LM(@"SYSTEM\CurrentControlSet\Control\CrashControl", "DisableEmoticon", 1, 0)) with { RequiresReboot = true, Group = "Recovery", Source = SrcWinUtil };
        yield return T(A, "adv.device-metadata", "Don't download device apps and icons automatically",
            "Stops Windows downloading manufacturers' companion apps, icons and info for newly connected devices (for example peripheral software that installs itself). Drivers still install.",
            LM(Pol + @"\Device Metadata", "PreventDeviceMetadataFromNetwork", 1)) with { Group = "Drivers", Source = SrcWinUtil };
        yield return T(A, "adv.co-installers", "Block device co-installer programs",
            "Stops drivers running their bundled \"co-installer\" setup programs when a device is plugged in, which is how some mouse and keyboard software installs itself without asking. The driver itself still installs.",
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", "DisableCoInstallers", 1, 0)) with
        {
            Risk = Risk.Caution, Group = "Drivers", Source = SrcWinUtil,
            Warning = "A few devices need their co-installer to finish setting up; install that device's software yourself if one doesn't work.",
        };
        yield return T(A, "adv.wpbt-off", "Ignore programs injected by the firmware (WPBT)",
            "Stops Windows running programs the PC's firmware asks it to install at every boot (the Windows Platform Binary Table), a channel some OEMs use for bloatware and anti-theft tools.",
            LM(@"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1)) with
        {
            Risk = Risk.Caution, RequiresReboot = true, Group = "Drivers", Source = SrcWinUtil,
            Warning = "If your laptop relies on firmware-installed anti-theft software (e.g. Absolute/Computrace), it stops being reinstalled.",
        };
        yield return T(A, "adv.mpo-off", "Disable multiplane overlay (MPO)",
            "A known fix for flickering, black flashes or stutter in some games and browsers on certain GPU and driver combinations. Leave it off unless you have those symptoms: MPO normally lowers latency in windowed games.",
            LM(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5)) with
        {
            Risk = Risk.Caution, RequiresReboot = true, Group = "GPU", Source = SrcWinUtil,
            Warning = "Only for flicker or stutter problems. Without them it can slightly raise latency or power use in windowed games.",
        };
        yield return T(A, "adv.password-reveal", "Hide the password reveal button",
            "Removes the eye button that shows a typed password in sign-in and credential boxes, so a password can't be revealed over your shoulder.",
            LM(Pol + @"\CredUI", "DisablePasswordReveal", 1)) with { Group = "Security", Source = SrcOo };
        yield return T(A, "adv.remote-assistance-off", "Turn off Remote Assistance invitations",
            "Stops this PC accepting Remote Assistance help sessions (a common scam route). Remote Desktop is a separate setting and isn't changed.",
            LM(@"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0, 1)) with { Group = "Security", Source = SrcOo };

        // ---- Services -----------------------------------------------------------------------------------------------------
        yield return new Tweak
        {
            Category = TweakCategory.Services, Id = "svc.wsaifabricsvc", Name = "Set to Manual: Windows AI Fabric Service",
            Description = "Hosts Windows' on-device AI features (Copilot+ PC experiences). On Manual it no longer starts with Windows and is started when an AI feature needs it. [WSAIFabricSvc → Manual]",
            Group = "AI", Ops = [new ServiceStartOp("WSAIFabricSvc", 3)], MinBuild = 26100, Source = SrcDebloat,
        };
    }
}
