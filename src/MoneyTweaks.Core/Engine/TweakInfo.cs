using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Engine;

/// <summary>
/// Purpose, expected benefit, confidence, risk, requirements, verification and undo for a tweak. Benefit/confidence are
/// hand-written for performance-related tweaks and derived conservatively from the category for the rest.
/// "Confidence" = how sure we are the stated benefit happens on a typical PC.
/// </summary>
public sealed record TweakFacts(string Purpose, string Benefit, string Confidence, string Risk, string Requirements, string Verification, string Undo)
{
    public string ToText() =>
        $"Expected benefit: {Benefit}\nConfidence: {Confidence} · Risk: {Risk}\nRequires: {Requirements}\nVerified by: {Verification}\nUndo: {Undo}";
}

public static class TweakInfo
{
    private static readonly Dictionary<string, (string benefit, string confidence)> Known = new()
    {
        ["gen.visualfx-performance"] = ("Snappier windows and menus. Games are usually not affected.", "Medium"),
        ["gen.transparency"] = ("Slightly less GPU work for the desktop. Games are usually not affected.", "Low"),
        ["gen.menu-delay"] = ("Menus open instantly.", "High"),
        ["gen.startup-delay"] = ("Startup apps launch sooner after sign-in.", "High"),
        ["gen.ultimate-plan"] = ("CPU stays at high clocks, which can reduce ramp-up stutter. Often no FPS change on modern CPUs; more power use.", "Low"),
        ["gen.core-parking"] = ("No waiting for parked cores to wake. Often no measurable change on modern CPUs.", "Low"),
        ["gen.usb-suspend"] = ("Avoids brief mouse, keyboard or headset dropouts. No FPS effect.", "Medium"),
        ["gen.min-processor-state"] = ("Removes clock ramp-up delay; more heat can reduce boost on limited coolers.", "Low"),
        ["gen.hibernate-off"] = ("Frees disk space about the size of your RAM (hiberfil.sys).", "High"),
        ["gen.fast-startup-off"] = ("Every boot starts drivers fresh, which avoids some stale-driver issues. Boots take a little longer.", "Medium"),
        ["gen.mouse-accel"] = ("The same hand movement always moves the cursor/aim the same distance. No FPS effect.", "High"),
        ["gen.keyboard-repeat"] = ("Held keys repeat sooner and faster.", "High"),
        ["gen.sticky-keys"] = ("No accessibility pop-ups when you hold or tap Shift in games.", "High"),
        ["gen.game-mode"] = ("Fewer background interruptions (e.g. driver installs) while playing. Usually small or no FPS change.", "Medium"),
        ["gen.background-recording"] = ("Frees GPU encoder and memory used for background clips. More noticeable on weaker GPUs.", "Medium"),
        ["gen.gamebar-tips"] = ("No Game Bar pop-up when a game starts.", "High"),
        ["gen.pcie-aspm-off"] = ("GPU/NVMe links never wake from low power mid-game. Usually no measurable FPS change; more idle power.", "Low"),
        ["gen.disk-idle-off"] = ("No spin-up pauses for games on hard drives. No effect on SSDs.", "Medium"),
        ["gen.toasts-off"] = ("No notification banners over games.", "High"),
        ["adv.windowed-optimizations"] = ("Lower latency and VRR for DirectX 10/11 games in windowed/borderless mode.", "Medium"),
        ["gpu.fortnite-high-performance"] = ("Makes sure Fortnite runs on the dedicated GPU on two-GPU PCs. No effect on single-GPU PCs.", "High"),
        ["adv.hags"] = ("Mixed results by game and driver. Required for DLSS Frame Generation.", "Low"),
        ["adv.fso"] = ("Games that request exclusive fullscreen get it, which can lower latency in some older games. Alt-tab and overlays may behave differently.", "Low"),
        ["adv.gpu-msi"] = ("Can lower interrupt latency; most modern GPU drivers already use MSI.", "Low"),
        ["adv.system-responsiveness"] = ("Games registered with the multimedia scheduler get a larger CPU share. Small effect.", "Low"),
        ["adv.games-task"] = ("Higher multimedia-scheduler priority for registered games. Small effect.", "Low"),
        ["adv.priority-separation"] = ("Foreground window gets longer CPU time slices. Small, workload-dependent effect.", "Low"),
        ["adv.power-throttling"] = ("Background apps (e.g. recording/streaming) aren't slowed down.", "Medium"),
        ["adv.memory-compression"] = ("Less CPU spent compressing memory; uses more RAM. Only with plenty of RAM.", "Low"),
        ["adv.timer-dynamic-tick"] = ("Steadier timer interrupts; mixed results.", "Low"),
        ["adv.global-timer-res"] = ("Lets the Input Delay Reducer's timer request apply system-wide on Windows 11.", "Medium"),
        ["adv.paging-executive"] = ("Kernel code is never paged out. Rarely measurable with enough RAM.", "Low"),
        ["adv.prefetch"] = ("Less background disk activity. Slightly slower first app launches.", "Low"),
        ["adv.ntfs-lastaccess"] = ("Fewer small disk writes when files are read.", "Medium"),
        ["adv.ntfs-8dot3"] = ("Slightly faster file creation in very large folders.", "Low"),
        ["adv.background-drivers"] = ("Windows Update won't replace the GPU driver you chose.", "High"),
        ["net.autotuning"] = ("Restores full download speed if auto-tuning was disabled by another tool.", "High"),
        ["net.adapter-power"] = ("Fewer drop-outs on adapters that misbehave with power saving. Typical ping unchanged.", "Low"),
        ["net.eee-off"] = ("Avoids small latency spikes on some wired adapters/switches. Typical ping unchanged.", "Low"),
        ["net.lso-off"] = ("Can fix stalls with some drivers; slightly more CPU for uploads.", "Low"),
        ["net.dns-cloudflare"] = ("Website/name lookups may resolve faster. Does not change in-game ping.", "Medium"),
        ["net.dns-google"] = ("Website/name lookups may resolve faster. Does not change in-game ping.", "Medium"),
        ["net.dns-quad9"] = ("Blocks known malicious domains; lookups may be faster. Does not change in-game ping.", "Medium"),
    };

    public static TweakFacts For(Tweak t)
    {
        var (benefit, conf) = Known.TryGetValue(t.Id, out var k) ? k : Default(t);
        var ineffective = TweakAudit.IssuesFor(t).FirstOrDefault(i => i.Kind is AuditKind.Ineffective or AuditKind.Obsolete);
        if (ineffective is not null) (benefit, conf) = (ineffective.Text, "Low");
        if (TweakAudit.DisabledReason(t) is { } d) (benefit, conf) = (d, "n/a");

        var req = new List<string>();
        if (t.RequiresAdmin) req.Add("administrator rights");
        if (t.MinBuild >= 22000) req.Add($"Windows 11 (build {t.MinBuild}+)"); else if (t.MinBuild > 0) req.Add($"Windows build {t.MinBuild}+");
        if (t.RequiresReboot) req.Add("a restart to take effect");
        if (t.Id == "gpu.fortnite-high-performance") req.Add("two GPUs and Fortnite installed via Epic");
        if (t.Id == "net.eee-off") req.Add("a wired adapter whose driver supports EEE");
        if (t.Ops.OfType<PowerCfgOp>().Any()) req.Add("applies to the active power plan");

        string risk = t.Risk == Risk.Caution ? "Caution" + (t.Warning is { } w ? $" ({w})" : "") : "Low";
        return new(t.Description, benefit, conf, risk, req.Count == 0 ? "nothing special" : string.Join(", ", req), Verification(t), Undo(t));
    }

    private static (string, string) Default(Tweak t) => t.Category switch
    {
        TweakCategory.Debloating => ("Less data collection, fewer ads/suggestions and background tasks. Not expected to change FPS.", "High"),
        TweakCategory.Services => ("Fewer services running in the background; small memory saving. Rarely measurable in games.", "Medium"),
        TweakCategory.AppRemoval => ("Removes the app, its background activity and its disk use.", "High"),
        TweakCategory.GameProfiles => ("The game gets CPU time before normal background apps when the CPU is busy. Helps mainly when something else is using the CPU.", "Medium"),
        TweakCategory.AppOptimizer => ("Launchers and helpers compete less with your game, or don't start with Windows.", "Medium"),
        TweakCategory.Bios => ("See the guide; benefit depends on your hardware.", "Medium"),
        _ when t.Group is "Explorer" or "Taskbar" or "Interface" => ("Convenience or appearance change. No performance effect expected.", "High"),
        _ => ("Behaviour change described above. No measurable performance effect is claimed.", "Medium"),
    };

    private static string Verification(Tweak t)
    {
        if (t.Kind == TweakKind.Guide) return "you (firmware settings can't be read by Windows; XMP and Resizable BAR are checked where possible)";
        var kinds = t.Ops.Select(o => o switch
        {
            RegValueOp or DynamicRegOp or RegStringFlagOp => "reading the registry value back",
            ServiceStartOp => "reading the service start type",
            PowerCfgOp => "reading the power-plan value (AC and DC)",
            ScheduledTaskOp => "reading the task's enabled state",
            AppxRemoveOp => "checking whether the package is installed",
            CommandOp { HasCheck: true } => "running a read-only check command",
            _ => "nothing (no read-back exists)",
        }).Distinct();
        return string.Join(", ", kinds) + "; Apply is rolled back automatically if the read-back doesn't match";
    }

    private static string Undo(Tweak t)
    {
        if (t.Kind == TweakKind.Guide) return "change it back in the BIOS";
        var kinds = t.Ops.Select(o => o switch
        {
            RegValueOp or DynamicRegOp or RegStringFlagOp => "restores the backed-up original value",
            ServiceStartOp => "restores the original start type",
            PowerCfgOp => "restores the original AC/DC values",
            ScheduledTaskOp => "re-enables the task if it was enabled before",
            AppxRemoveOp => "re-registers the app if Windows still has its files (otherwise reinstall from the Microsoft Store)",
            CommandOp c when c.UndoCommand.Contains("default", StringComparison.OrdinalIgnoreCase) => "runs the reverse command (Windows default)",
            CommandOp => "runs the reverse command",
            _ => "restores Windows defaults",
        }).Distinct();
        return string.Join("; ", kinds) + "; then checks the result against the backup";
    }
}
