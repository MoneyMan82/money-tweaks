using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Model;

public enum Risk { Safe, Caution }

public enum TweakCategory
{
    General, Debloating, Networking, Advanced, Services, AppOptimizer, AppRemoval, GameProfiles, Bios
}

public enum TweakKind
{
    /// <summary>A reversible on/off change made by Money Tweaks.</summary>
    Toggle,
    /// <summary>A step-by-step guide for changes Windows cannot make (e.g. firmware/BIOS settings).</summary>
    Guide
}

public enum TweakState { NotApplied, Applied, Partial, NotApplicable, Unknown }

public sealed record Tweak
{
    public required string Id { get; init; }
    public required TweakCategory Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public Risk Risk { get; init; } = Risk.Safe;
    public TweakKind Kind { get; init; } = TweakKind.Toggle;
    public IReadOnlyList<TweakOp> Ops { get; init; } = [];
    public bool RequiresReboot { get; init; }
    /// <summary>Minimum Windows build (e.g. 22000 for Windows 11). 0 = any.</summary>
    public int MinBuild { get; init; }
    /// <summary>Part of the one-click "Recommended" preset.</summary>
    public bool Recommended { get; init; }
    /// <summary>Optional sub-group label shown in the UI (e.g. "Privacy", "Xbox").</summary>
    public string? Group { get; init; }
    /// <summary>Guide steps for <see cref="TweakKind.Guide"/> entries.</summary>
    public IReadOnlyList<string> GuideSteps { get; init; } = [];
    /// <summary>Extra warning shown in the confirmation dialog for Caution tweaks.</summary>
    public string? Warning { get; init; }

    public bool RequiresAdmin => Ops.Any(o => o.RequiresAdmin);

    public override string ToString() => $"{Id} ({Name})";
}

public static class CategoryInfo
{
    public static string DisplayName(TweakCategory c) => c switch
    {
        TweakCategory.AppOptimizer => "App Optimizer",
        TweakCategory.AppRemoval => "App Removal",
        TweakCategory.GameProfiles => "Game Profiles",
        TweakCategory.Bios => "BIOS",
        _ => c.ToString()
    };

    public static string Blurb(TweakCategory c) => c switch
    {
        TweakCategory.General => "Everyday Windows responsiveness, power and shell tweaks.",
        TweakCategory.Debloating => "Telemetry, ads, suggestions and background data collection.",
        TweakCategory.Networking => "TCP/IP, DNS and adapter settings for steadier latency.",
        TweakCategory.Advanced => "Kernel, memory, GPU and scheduler settings. Read each one carefully.",
        TweakCategory.Services => "Set rarely-used Windows services to Manual or Disabled. Your original start type is backed up.",
        TweakCategory.AppOptimizer => "Tame game launchers and background apps.",
        TweakCategory.AppRemoval => "Uninstall preinstalled Store apps for the current user.",
        TweakCategory.GameProfiles => "Per-game CPU priority profiles applied through Windows Image File Execution Options.",
        TweakCategory.Bios => "Firmware settings Windows cannot change. Follow the guide in your motherboard's BIOS/UEFI.",
        _ => ""
    };
}
