using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

public static partial class TweakCatalog
{
    private static IEnumerable<Tweak> Debloating()
    {
        const TweakCategory D = TweakCategory.Debloating;
        const string Priv = "Privacy", Ads = "Ads & suggestions", Shell = "Shell & AI", Tasks = "Telemetry tasks", Edge = "Microsoft Edge";

        yield return T(D, "dbl.telemetry", "Minimize Windows telemetry",
            "Sets diagnostic data to the lowest level your edition allows (Security on Enterprise/Education, Required elsewhere) and stops Windows asking for feedback about it.",
            LM(Pol + @"\DataCollection", "AllowTelemetry", 0),
            LM(Pol + @"\DataCollection", "DoNotShowFeedbackNotifications", 1)) with { Recommended = true, Group = Priv };
        yield return T(D, "dbl.advertising-id", "Disable advertising ID",
            "Turns off the per-user advertising ID that apps use to show personalized ads.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1),
            LM(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1)) with { Recommended = true, Group = Priv };
        yield return T(D, "dbl.activity-history", "Disable activity history",
            "Stops Windows collecting and uploading the list of apps, files and sites you open (the old Timeline feature).",
            LM(Pol + @"\System", "EnableActivityFeed", 0),
            LM(Pol + @"\System", "PublishUserActivities", 0),
            LM(Pol + @"\System", "UploadUserActivities", 0)) with { Recommended = true, Group = Priv };
        yield return T(D, "dbl.tailored", "Disable tailored experiences",
            "Prevents Microsoft from using your diagnostic data to personalize tips, ads and recommendations.",
            CU(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
            CU(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)) with { Recommended = true, Group = Priv };
        yield return T(D, "dbl.feedback", "Never ask for feedback",
            "Sets Windows feedback prompts to 'Never'.",
            CU(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
            CU(@"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds", 0)) with { Group = Priv };
        yield return T(D, "dbl.error-reporting", "Disable Windows Error Reporting",
            "Stops crash reports being sent to Microsoft. Crash dumps and Event Viewer logs still work locally.",
            LM(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, 0)) with { Group = Priv };
        yield return T(D, "dbl.ceip", "Disable Customer Experience Improvement Program",
            "Opts this PC out of the legacy CEIP usage-data program.",
            LM(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0)) with { Recommended = true, Group = Priv };
        yield return T(D, "dbl.app-launch-tracking", "Disable app launch tracking",
            "Stops Windows recording which apps you launch to rank Start and search results.",
            CU(Adv, "Start_TrackProgs", 0, 1)) with { Group = Priv };
        yield return T(D, "dbl.inking-typing", "Disable inking & typing personalization",
            "Stops Windows building a personal dictionary from what you type and handwrite.",
            CU(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
            CU(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),
            CU(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, 1),
            CU(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, 1)) with { Group = Priv };
        yield return T(D, "dbl.online-speech", "Disable online speech recognition",
            "Stops voice input being sent to Microsoft's cloud speech service.",
            CU(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, 1)) with { Group = Priv };
        yield return T(D, "dbl.location", "Disable location services",
            "Turns off Windows location for the whole PC. Weather, Maps and Find My Device lose location access.",
            LM(Pol + @"\LocationAndSensors", "DisableLocation", 1)) with { Risk = Risk.Caution, Group = Priv };
        yield return T(D, "dbl.find-my-device", "Disable Find My Device",
            "Stops the PC periodically reporting its location for Find My Device.",
            LM(@"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", 0)) with { Group = Priv };
        yield return T(D, "dbl.cloud-clipboard", "Disable cloud clipboard sync",
            "Keeps clipboard history on this PC instead of syncing it across devices through your Microsoft account.",
            LM(Pol + @"\System", "AllowCrossDeviceClipboard", 0)) with { Group = Priv };
        yield return T(D, "dbl.handwriting-sharing", "Disable handwriting data sharing",
            "Stops handwriting-recognition samples being sent to Microsoft.",
            LM(Pol + @"\TabletPC", "PreventHandwritingDataSharing", 1),
            LM(Pol + @"\HandwritingErrorReports", "PreventHandwritingErrorReports", 1)) with { Group = Priv };
        yield return T(D, "dbl.inventory", "Disable app inventory collector",
            "Stops the Inventory Collector sending details about installed apps and drivers to Microsoft.",
            LM(Pol + @"\AppCompat", "DisableInventory", 1)) with { Group = Priv };
        yield return T(D, "dbl.steps-recorder", "Disable Steps Recorder",
            "Turns off the Steps Recorder (problem-steps capture) feature.",
            LM(Pol + @"\AppCompat", "DisableUAR", 1)) with { Group = Priv };
        yield return T(D, "dbl.delivery-p2p", "Disable Delivery Optimization peer-to-peer",
            "Stops Windows Update uploading update files to other PCs on the internet. Updates still download normally from Microsoft.",
            LM(Pol + @"\DeliveryOptimization", "DODownloadMode", 0)) with { Recommended = true, Group = Priv };
        yield return T(D, "dbl.settings-sync", "Disable settings sync",
            "Stops theme, password and preference sync through your Microsoft account.",
            LM(Pol + @"\SettingSync", "DisableSettingSync", 2),
            LM(Pol + @"\SettingSync", "DisableSettingSyncUserOverride", 1)) with { Risk = Risk.Caution, Group = Priv };
        yield return T(D, "dbl.background-apps", "Block background Store apps",
            "Prevents Microsoft Store apps from running in the background. Apps that need background notifications (Mail, Teams) will only update while open.",
            LM(Pol + @"\AppPrivacy", "LetAppsRunInBackground", 2)) with { Risk = Risk.Caution, Group = Priv };
        yield return T(D, "dbl.powershell-telemetry", "Opt out of PowerShell & .NET CLI telemetry",
            "Sets the official POWERSHELL_TELEMETRY_OPTOUT and DOTNET_CLI_TELEMETRY_OPTOUT environment variables machine-wide.",
            LMs(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "POWERSHELL_TELEMETRY_OPTOUT", "1"),
            LMs(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "DOTNET_CLI_TELEMETRY_OPTOUT", "1")) with { Group = Priv };
        yield return T(D, "dbl.office-telemetry", "Disable Microsoft Office telemetry",
            "Turns off Office's client telemetry agent for the current user.",
            CU(@"Software\Policies\Microsoft\office\common\clienttelemetry", "SendTelemetry", 3),
            CU(@"Software\Policies\Microsoft\office\16.0\common\clienttelemetry", "SendTelemetry", 3),
            CU(@"Software\Microsoft\Office\Common\ClientTelemetry", "DisableTelemetry", 1)) with { Group = Priv };
        yield return T(D, "dbl.wifi-sense", "Disable Wi-Fi Sense hotspot sharing",
            "Stops automatic connection to suggested open hotspots and Wi-Fi sharing with contacts.",
            LM(@"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowAutoConnectToWiFiSenseHotspots", "value", 0, 1),
            LM(@"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting", "value", 0, 1)) with { Group = Priv };

        // Ads, suggestions and nags
        yield return T(D, "dbl.consumer-features", "Block automatic sponsored app installs",
            "Turns off Windows consumer features, which silently install promoted apps and games (Candy Crush and similar) on new accounts.",
            LM(Pol + @"\CloudContent", "DisableWindowsConsumerFeatures", 1),
            CU(Cdm, "SilentInstalledAppsEnabled", 0, 1),
            CU(Cdm, "PreInstalledAppsEnabled", 0, 1),
            CU(Cdm, "OemPreInstalledAppsEnabled", 0, 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.start-suggestions", "Disable Start menu suggestions",
            "Removes promoted apps and 'suggestions' from the Start menu.",
            CU(Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
            CU(Cdm, "SubscribedContent-338388Enabled", 0, 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.tips", "Disable tips and tricks notifications",
            "Stops 'Get tips and suggestions when using Windows' notifications.",
            CU(Cdm, "SubscribedContent-338389Enabled", 0, 1),
            CU(Cdm, "SoftLandingEnabled", 0, 1),
            LM(Pol + @"\CloudContent", "DisableSoftLanding", 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.settings-suggestions", "Disable suggested content in Settings",
            "Removes promotional suggestions shown inside the Settings app.",
            CU(Cdm, "SubscribedContent-338393Enabled", 0, 1),
            CU(Cdm, "SubscribedContent-353694Enabled", 0, 1),
            CU(Cdm, "SubscribedContent-353696Enabled", 0, 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.lockscreen-tips", "Disable lock screen fun facts & ads",
            "Removes tips, 'fun facts' and promotions shown over the lock screen picture.",
            CU(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
            CU(Cdm, "SubscribedContent-338387Enabled", 0, 1)) with { Group = Ads };
        yield return T(D, "dbl.welcome-experience", "Disable 'finish setting up your device' screens",
            "Stops the full-screen 'Let's finish setting up' and Windows welcome experience after updates.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1),
            CU(Cdm, "SubscribedContent-310093Enabled", 0, 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.spotlight", "Disable Windows Spotlight features",
            "Turns off Spotlight content on the lock screen and desktop, along with its promotional tips.",
            CU(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", 1)) with { Group = Ads };
        yield return T(D, "dbl.sync-provider-ads", "Disable OneDrive ads in File Explorer",
            "Stops sync-provider notifications (mostly OneDrive and Microsoft 365 promos) in File Explorer.",
            CU(Adv, "ShowSyncProviderNotifications", 0, 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.start-recommendations", "Disable Start 'Recommended' promotions",
            "Turns off tips, shortcuts and new-app promotions in the Windows 11 Start menu's Recommended area.",
            CU(Adv, "Start_IrisRecommendations", 0, 1)) with { MinBuild = 22000, Group = Ads };
        yield return T(D, "dbl.account-notifications", "Disable Start account notifications",
            "Hides Microsoft account nags (backup, sign-in reminders) in the Windows 11 Start menu.",
            CU(Adv, "Start_AccountNotifications", 0, 1)) with { MinBuild = 22000, Group = Ads };
        yield return T(D, "dbl.web-search", "Disable Bing web results in Start search",
            "Start search only looks at your PC instead of also sending what you type to Bing.",
            CU(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
            CU(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1)) with { Recommended = true, Group = Ads };
        yield return T(D, "dbl.search-highlights", "Disable search highlights",
            "Removes the daily illustrations and trending searches from the search box and search window.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, 1)) with { Group = Ads };
        yield return T(D, "dbl.recent-files", "Hide recent & frequent files in Quick Access",
            "Stops File Explorer Home showing recently opened files and frequent folders.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecent", 0, 1),
            CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowFrequent", 0, 1)) with { Group = Priv };

        // Shell features, AI and widgets
        yield return T(D, "dbl.cortana", "Disable Cortana",
            "Turns off Cortana through Group Policy, including its search integration.",
            LM(Pol + @"\Windows Search", "AllowCortana", 0)) with { Recommended = true, Group = Shell };
        yield return T(D, "dbl.copilot", "Disable Windows Copilot",
            "Turns off the Windows Copilot sidebar and hides its taskbar button.",
            CU(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
            LM(Pol + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1),
            CU(Adv, "ShowCopilotButton", 0, 1)) with { MinBuild = 22000, Recommended = true, Group = Shell };
        yield return T(D, "dbl.recall", "Disable Recall snapshots",
            "Stops Windows Recall saving screenshots of your activity on Copilot+ PCs.",
            CU(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
            LM(Pol + @"\WindowsAI", "DisableAIDataAnalysis", 1)) with { MinBuild = 22000, Recommended = true, Group = Shell };
        yield return T(D, "dbl.widgets", "Disable Widgets board",
            "Turns off Widgets and their news feed through policy, and hides the taskbar Widgets button.",
            LM(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)) with { MinBuild = 22000, Recommended = true, Group = Shell };
        yield return T(D, "dbl.news-interests", "Disable News and Interests (Windows 10)",
            "Removes the weather/news taskbar feed on Windows 10.",
            LM(Pol + @"\Windows Feeds", "EnableFeeds", 0)) with { Group = Shell };
        yield return T(D, "dbl.meet-now", "Hide Meet Now",
            "Removes the Skype 'Meet Now' icon from the Windows 10 system tray.",
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "HideSCAMeetNow", 1)) with { Group = Shell };
        yield return T(D, "dbl.chat-icon", "Hide Chat/Teams taskbar icon",
            "Removes the Chat (Teams consumer) button from the Windows 11 taskbar.",
            CU(Adv, "TaskbarMn", 0, 1)) with { MinBuild = 22000, Group = Shell };
        yield return T(D, "dbl.onedrive-sync", "Block OneDrive file sync",
            "Prevents OneDrive from syncing files on this PC through policy. Existing files stay where they are. Undo re-enables sync.",
            LM(Pol + @"\OneDrive", "DisableFileSyncNGSC", 1)) with { Risk = Risk.Caution, Group = Shell, Warning = "Files that only live in OneDrive (online-only) will not be reachable from File Explorer while this is applied." };
        yield return T(D, "dbl.ink-workspace", "Disable Windows Ink Workspace",
            "Turns off the pen Ink Workspace and its suggested apps.",
            LM(@"SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowWindowsInkWorkspace", 0)) with { Group = Shell };
        yield return T(D, "dbl.lock-screen-camera", "Disable lock screen camera",
            "Prevents the camera being opened from the lock screen.",
            LM(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreenCamera", 1)) with { Group = Shell };
        yield return T(D, "dbl.storage-sense", "Disable Storage Sense",
            "Stops Windows automatically deleting temp files, Recycle Bin and Downloads content on a schedule.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0, 1)) with { Group = Shell };

        // Edge background behaviour
        yield return T(D, "dbl.edge-background", "Stop Edge running in the background",
            "Disables Edge Startup Boost and background mode, so Edge doesn't sit in memory after you close it.",
            LM(@"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
            LM(@"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0)) with { Recommended = true, Group = Edge };
        yield return T(D, "dbl.edge-shopping", "Disable Edge shopping & sidebar",
            "Turns off Edge's shopping assistant, sidebar (Copilot/hub) and personalization reporting.",
            LM(@"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0),
            LM(@"SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0),
            LM(@"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0)) with { Group = Edge };
        yield return T(D, "dbl.edge-telemetry", "Limit Edge diagnostic data",
            "Sets Edge to send only required diagnostic data.",
            LM(@"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0)) with { Group = Edge };

        // Telemetry scheduled tasks
        (string id, string path, string name, string desc)[] tasks =
        [
            ("compat-appraiser", @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Compatibility Appraiser task", "Scans installed programs and sends compatibility telemetry. CPU-heavy when it runs."),
            ("program-data-updater", @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", "ProgramDataUpdater task", "Collects program telemetry for the Customer Experience Improvement Program."),
            ("ceip-consolidator", @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", "CEIP Consolidator task", "Collects and uploads CEIP usage data."),
            ("ceip-usb", @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", "USB CEIP task", "Collects USB bus statistics for Microsoft."),
            ("disk-diagnostic", @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Disk diagnostic data collector", "Sends general disk and system information to Microsoft."),
            ("feedback-dmclient", @"\Microsoft\Windows\Feedback\Siuf\DmClient", "Feedback DmClient task", "Downloads and runs feedback-survey scenarios."),
            ("feedback-dmclient-scenario", @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload", "Feedback scenario download task", "Companion to DmClient for feedback scenarios."),
            ("autochk-proxy", @"\Microsoft\Windows\Autochk\Proxy", "Autochk SQM proxy task", "Collects and uploads autochk data to CEIP."),
            ("maps-update", @"\Microsoft\Windows\Maps\MapsUpdateTask", "Offline maps update task", "Checks for offline map updates. Not needed unless you use offline maps."),
            ("maps-toast", @"\Microsoft\Windows\Maps\MapsToastTask", "Maps notification task", "Shows Maps app notifications."),
            ("error-reporting-queue", @"\Microsoft\Windows\Windows Error Reporting\QueueReporting", "Error report queue task", "Uploads queued Windows Error Reporting data."),
            ("cloud-experience-host", @"\Microsoft\Windows\CloudExperienceHost\CreateObjectTask", "CloudExperienceHost task", "Supports Microsoft account cloud-experience prompts."),
        ];
        foreach (var (id, path, name, desc) in tasks)
            yield return T(D, "dbl.task." + id, "Disable " + name, desc + " Undo re-enables the task.", new ScheduledTaskOp(path)) with { Group = Tasks, Recommended = id is "compat-appraiser" or "ceip-consolidator" };
    }
}
