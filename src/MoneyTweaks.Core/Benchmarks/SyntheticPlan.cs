using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Core.Benchmarks;

/// <summary>Run order and scope rules for the synthetic A/B benchmark.</summary>
public static class SyntheticPlan
{
    /// <summary>
    /// Counterbalanced order: off,on, on,off, off,on… Warm-up and thermal drift (the first run on a cold GPU is usually
    /// fastest) then hit both states equally instead of always favouring "off".
    /// </summary>
    public static bool[] Abba(int pairs) =>
        Enumerable.Range(0, Math.Max(1, pairs)).SelectMany(i => i % 2 == 0 ? new[] { false, true } : [true, false]).ToArray();

    private static readonly HashSet<string> InputIds = ["gen.mouse-accel", "gen.keyboard-repeat", "gen.sticky-keys", "gen.usb-suspend"];
    private static readonly HashSet<string> DesktopIds = ["gen.file-extensions", "gen.hidden-files", "gen.explorer-thispc", "gen.classic-context", "gen.taskbar-left",
        "gen.taskview-button", "gen.search-box", "gen.end-task", "gen.dark-mode", "gen.aero-shake", "gen.autoplay", "gen.snap-flyout", "gen.verbose-status", "gen.startup-delay"];

    /// <summary>
    /// Why a tweak can't change the synthetic scene's result (it targets something the scene doesn't use), or null if it
    /// can. Such tweaks may still matter in real games; they just can't be judged with this test.
    /// </summary>
    public static string? NotMeasurable(Tweak t) => t switch
    {
        { Category: TweakCategory.GameProfiles } => "only changes that game's process priority",
        { Category: TweakCategory.AppOptimizer } => "only affects launchers/indexer processes",
        { Category: TweakCategory.Networking } => "network setting; the scene uses no network",
        { Category: TweakCategory.Debloating or TweakCategory.Services } => "background/privacy setting whose effect is occasional, not continuous",
        { Id: "adv.fso" } => "only affects games using exclusive fullscreen",
        { Id: "gen.game-mode" } => "Windows applies Game Mode to apps it detects as games; this scene isn't one",
        { Id: "gen.background-recording" or "gen.gamebar-tips" } => "Game Bar/DVR only acts on games",
        { Id: "adv.system-responsiveness" or "adv.games-task" } => "multimedia scheduler 'Games' task: only threads registered as games",
        _ when InputIds.Contains(t.Id) => "input-device setting",
        _ when DesktopIds.Contains(t.Id) => "desktop/Explorer setting",
        _ => null,
    };
}
