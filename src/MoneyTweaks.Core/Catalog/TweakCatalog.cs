using Microsoft.Win32;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

/// <summary>
/// The complete Money Tweaks library. Every entry maps to a real, documented Windows setting and is reversible
/// (App Removal undo re-registers the package when Windows still has its files).
/// </summary>
public static partial class TweakCatalog
{
    private static readonly Lazy<IReadOnlyList<Tweak>> _all = new(Build);
    public static IReadOnlyList<Tweak> All => _all.Value;

    public static IEnumerable<Tweak> InCategory(TweakCategory c) => All.Where(t => t.Category == c);
    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static IReadOnlyList<Tweak> Build()
    {
        var list = new List<Tweak>();
        list.AddRange(General());
        list.AddRange(Debloating());
        list.AddRange(Networking());
        list.AddRange(Advanced());
        list.AddRange(Services());
        list.AddRange(AppOptimizer());
        list.AddRange(AppRemoval());
        list.AddRange(GameProfiles());
        list.AddRange(Bios());
        return list;
    }

    // ---- builder helpers -------------------------------------------------------------------------------------
    internal const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    internal const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    internal const string Pol = @"SOFTWARE\Policies\Microsoft\Windows";

    internal static RegValueOp CU(string path, string name, int v, object? def = null) =>
        new(Hive.HKCU, path, name, RegistryValueKind.DWord, v) { DefaultValue = def };
    internal static RegValueOp LM(string path, string name, int v, object? def = null) =>
        new(Hive.HKLM, path, name, RegistryValueKind.DWord, v) { DefaultValue = def };
    internal static RegValueOp LMu(string path, string name, uint v, object? def = null) =>
        new(Hive.HKLM, path, name, RegistryValueKind.DWord, v) { DefaultValue = def };
    internal static RegValueOp CUs(string path, string name, string v, object? def = null) =>
        new(Hive.HKCU, path, name, RegistryValueKind.String, v) { DefaultValue = def };
    internal static RegValueOp LMs(string path, string name, string v, object? def = null) =>
        new(Hive.HKLM, path, name, RegistryValueKind.String, v) { DefaultValue = def };

    internal static CommandOp Cmd(string apply, string undo, string? check = null, string? pattern = null, bool admin = true, string? label = null, Shell? checkShell = null) =>
        new() { ApplyCommand = apply, UndoCommand = undo, CheckCommand = check, AppliedPattern = pattern, Admin = admin, Label = label, CheckShell = checkShell };
    internal static CommandOp Ps(string apply, string undo, string? check = null, string? pattern = null, bool admin = true, string? label = null) =>
        new() { ApplyCommand = apply, UndoCommand = undo, CheckCommand = check, AppliedPattern = pattern, Admin = admin, Label = label, Shell = Shell.PowerShell };

    internal static Tweak T(TweakCategory cat, string id, string name, string desc, params TweakOp[] ops) =>
        new() { Category = cat, Id = id, Name = name, Description = desc, Ops = ops };

    // ---- GENERAL ---------------------------------------------------------------------------------------------
    private static IEnumerable<Tweak> General()
    {
        const TweakCategory G = TweakCategory.General;
        const string Desk = @"Control Panel\Desktop";
        const string Mouse = @"Control Panel\Mouse";

        yield return T(G, "gen.visualfx-performance", "Visual effects: best performance",
            "Switches Windows to the 'Adjust for best performance' visual-effects preset and turns off minimize/maximize animations, so windows open and close instantly.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, 0),
            CUs(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1"),
            CU(Adv, "TaskbarAnimations", 0, 1)) with { Recommended = true, Group = "Interface" };
        yield return T(G, "gen.menu-delay", "Instant menus",
            "Lowers the hover delay before cascading menus open from 400 ms to 0 ms.",
            CUs(Desk, "MenuShowDelay", "0", "400")) with { Recommended = true, Group = "Interface" };
        yield return T(G, "gen.transparency", "Turn off transparency effects",
            "Disables the Acrylic/Mica see-through effect on the taskbar, Start and Settings, which costs a little GPU work.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)) with { Group = "Interface" };
        yield return T(G, "gen.startup-delay", "Remove startup app delay",
            "Windows waits a few seconds after sign-in before launching startup apps. This removes that artificial wait.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0)) with { Recommended = true, Group = "Startup" };
        yield return T(G, "gen.ultimate-plan", "Ultimate Performance power plan",
            "Adds Microsoft's hidden Ultimate Performance plan and makes it active. Undo switches back to Balanced. Uses more power, so it's best on desktops.",
            Cmd("powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 99999999-9999-9999-9999-999999999999 & powercfg /setactive 99999999-9999-9999-9999-999999999999",
                "powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e & powercfg /delete 99999999-9999-9999-9999-999999999999",
                "powercfg /getactivescheme", "99999999-9999-9999-9999-999999999999", admin: false, label: "Create and activate the Ultimate Performance plan")) with { Group = "Power" };
        yield return T(G, "gen.core-parking", "Disable CPU core parking",
            "Sets the active power plan's minimum unparked cores to 100% so no core sleeps and needs waking mid-game.",
            new PowerCfgOp("sub_processor", "CPMINCORES", 100, "Processor minimum unparked cores")) with { Group = "Power" };
        yield return T(G, "gen.usb-suspend", "Disable USB selective suspend",
            "Stops Windows powering down idle USB ports, which can cause mouse, keyboard or headset dropouts.",
            new PowerCfgOp("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, "USB selective suspend")) with { Recommended = true, Group = "Power" };
        yield return T(G, "gen.min-processor-state", "Minimum processor state 100%",
            "Keeps the CPU at full clock instead of ramping up from idle. Raises temperatures and power draw. Desktops only.",
            new PowerCfgOp("sub_processor", "PROCTHROTTLEMIN", 100, "Minimum processor state")) with { Risk = Risk.Caution, Group = "Power" };
        yield return T(G, "gen.hibernate-off", "Disable hibernation",
            "Turns off hibernation and deletes hiberfil.sys, freeing several GB on the system drive. This also disables Fast Startup.",
            Cmd("powercfg /hibernate off", "powercfg /hibernate on", label: "powercfg /hibernate off")) with { Group = "Power", Risk = Risk.Caution };
        yield return T(G, "gen.fast-startup-off", "Disable Fast Startup",
            "Fast Startup half-hibernates the kernel at shutdown, which can leave drivers in a stale state. Disabling it makes every boot a clean boot.",
            LM(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)) with { Group = "Power" };
        yield return T(G, "gen.mouse-accel", "Disable mouse acceleration",
            "Turns off 'Enhance pointer precision' so cursor movement maps 1:1 to how far you move the mouse.",
            CUs(Mouse, "MouseSpeed", "0", "1"), CUs(Mouse, "MouseThreshold1", "0", "6"), CUs(Mouse, "MouseThreshold2", "0", "10")) with { Recommended = true, Group = "Input" };
        yield return T(G, "gen.keyboard-repeat", "Fastest keyboard repeat",
            "Sets the shortest repeat delay and the fastest repeat rate for held keys.",
            CUs(@"Control Panel\Keyboard", "KeyboardDelay", "0", "1"), CUs(@"Control Panel\Keyboard", "KeyboardSpeed", "31", "31")) with { Group = "Input" };
        yield return T(G, "gen.sticky-keys", "Disable Sticky/Filter/Toggle Keys shortcuts",
            "Stops pressing Shift five times (or holding it) from popping up accessibility prompts mid-game. The features themselves stay available.",
            CUs(@"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510"),
            CUs(@"Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126"),
            CUs(@"Control Panel\Accessibility\ToggleKeys", "Flags", "58", "62")) with { Recommended = true, Group = "Input" };
        yield return T(G, "gen.file-extensions", "Show file extensions",
            "Shows extensions like .exe and .pdf in File Explorer, which also helps you spot disguised files.",
            CU(Adv, "HideFileExt", 0, 1)) with { Group = "Explorer" };
        yield return T(G, "gen.hidden-files", "Show hidden files",
            "Shows hidden files and folders in File Explorer.",
            CU(Adv, "Hidden", 1, 2)) with { Group = "Explorer" };
        yield return T(G, "gen.explorer-thispc", "Open File Explorer to This PC",
            "File Explorer opens on This PC (your drives) instead of Home/Quick Access.",
            CU(Adv, "LaunchTo", 1, 2)) with { Group = "Explorer" };
        yield return T(G, "gen.classic-context", "Classic right-click menu",
            "Restores the full Windows 10-style context menu on Windows 11, so you skip 'Show more options'. Takes effect after Explorer restarts or you sign out.",
            new RegValueOp(Hive.HKCU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", RegistryValueKind.String, "") { RemoveKeyIfCreated = true }) with { MinBuild = 22000, Group = "Explorer" };
        yield return T(G, "gen.taskbar-left", "Align taskbar to the left",
            "Moves Windows 11 taskbar icons from the center back to the left.",
            CU(Adv, "TaskbarAl", 0, 1)) with { MinBuild = 22000, Group = "Taskbar" };
        yield return T(G, "gen.taskview-button", "Hide Task View button",
            "Removes the Task View button from the taskbar. Win+Tab still works.",
            CU(Adv, "ShowTaskViewButton", 0, 1)) with { Group = "Taskbar" };
        yield return T(G, "gen.search-box", "Hide taskbar search box",
            "Hides the search box/icon on the taskbar. Press Win and start typing to search.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, 1)) with { Group = "Taskbar" };
        yield return T(G, "gen.end-task", "Add 'End task' to taskbar right-click",
            "Enables Windows 11's hidden 'End task' option on taskbar app menus so you can kill frozen apps quickly.",
            CU(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0)) with { MinBuild = 22631, Group = "Taskbar" };
        yield return T(G, "gen.dark-mode", "Dark mode for apps and Windows",
            "Switches both apps and the Windows shell to the dark theme.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0, 1),
            CU(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0, 1)) with { Group = "Interface" };
        yield return T(G, "gen.aero-shake", "Disable Aero Shake",
            "Stops shaking a window from minimizing every other window.",
            CU(Adv, "DisallowShaking", 1, 0)) with { Group = "Interface" };
        yield return T(G, "gen.autoplay", "Disable AutoPlay",
            "Stops Windows automatically opening or running content when you plug in USB drives or discs. Also a security win.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", 1, 0)) with { Group = "Explorer" };
        yield return T(G, "gen.snap-flyout", "Disable Snap Layouts hover flyout",
            "Stops the layout picker popping up when you hover over a window's maximize button. Win+Z still works.",
            CU(Adv, "EnableSnapAssistFlyout", 0, 1)) with { MinBuild = 22000, Group = "Interface" };
        yield return T(G, "gen.verbose-status", "Verbose sign-in/shutdown messages",
            "Shows exactly what Windows is doing during sign-in and shutdown instead of a spinner, which helps diagnose slow boots.",
            LM(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1)) with { Group = "Startup" };
        yield return T(G, "gen.game-mode", "Enable Game Mode",
            "Makes sure Windows Game Mode is on. It prioritizes the game you're playing and pauses Windows Update driver installs during play.",
            CU(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1), CU(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1)) with { Recommended = true, Group = "Gaming" };
        yield return T(G, "gen.background-recording", "Disable Game DVR background recording",
            "Turns off the Xbox Game Bar's always-on capture buffer and its game recording, which costs FPS on weaker GPUs.",
            CU(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
            CU(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            LM(Pol + @"\GameDVR", "AllowGameDVR", 0)) with { Recommended = true, Group = "Gaming" };
        yield return T(G, "gen.gamebar-tips", "Disable Game Bar startup tips",
            "Stops the 'Press Win+G to open Game Bar' popup when games start.",
            CU(@"Software\Microsoft\GameBar", "ShowStartupPanel", 0, 1)) with { Group = "Gaming" };
    }
}
