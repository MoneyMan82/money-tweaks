using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

/// <summary>
/// Safe: well-understood, low risk on almost every PC. Advanced: a real trade-off (marked Caution). Experimental: changes
/// boot configuration, device interrupts or kernel memory management, where results vary by hardware and a bad case
/// needs Safe Mode or a restart to recover.
/// </summary>
public enum TweakTier { Safe, Advanced, Experimental }

/// <summary>Block = Money Tweaks refuses to apply; Warn = shown and must be confirmed; Info = shown for context.</summary>
public enum CompatSeverity { Info, Warn, Block }

public sealed record CompatFinding(CompatSeverity Severity, string Text, string? OtherTweakId = null);

/// <summary>Everything known about a tweak on this PC before it is applied.</summary>
public sealed record CompatibilityReport(Tweak Tweak, TweakTier Tier, TweakState State, IReadOnlyList<CompatFinding> Findings,
    IReadOnlyList<string> CurrentValues, TweakFacts Facts)
{
    public bool Blocked => Findings.Any(f => f.Severity == CompatSeverity.Block);
    public bool AlreadyApplied => State == TweakState.Applied;
    public IEnumerable<CompatFinding> Blockers => Findings.Where(f => f.Severity == CompatSeverity.Block);
    public IEnumerable<CompatFinding> Warnings => Findings.Where(f => f.Severity == CompatSeverity.Warn);

    /// <summary>The confirmation text: what changes, effect, risks, restart, reversibility and this PC's findings.</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Tier: {Compatibility.TierLabel(Tier)} · {Compatibility.TierBlurb(Tier)}");
        if (AlreadyApplied) sb.AppendLine("Already applied on this PC: nothing will be changed.");
        foreach (var f in Findings.OrderByDescending(f => f.Severity))
            sb.AppendLine($"{(f.Severity switch { CompatSeverity.Block => "⛔", CompatSeverity.Warn => "⚠", _ => "ℹ" })} {f.Text}");
        sb.AppendLine();
        sb.AppendLine("Changes:");
        foreach (var op in Tweak.Ops) sb.AppendLine("  • " + op.Describe());
        if (CurrentValues.Count > 0)
        {
            sb.AppendLine("Current values on this PC:");
            foreach (var v in CurrentValues.Take(8)) sb.AppendLine("  • " + v);
        }
        sb.AppendLine();
        sb.AppendLine($"Expected effect: {Facts.Benefit} (confidence: {Facts.Confidence}). No FPS gain is promised; measure it with Test & Benchmark.");
        sb.AppendLine($"Risk: {Facts.Risk}");
        sb.AppendLine(Tweak.RequiresReboot ? "Restart: needed for the change to take full effect." : "Restart: not needed.");
        sb.Append($"Reversible: {Facts.Undo}. Original values are backed up first and every value is read back after applying.");
        return sb.ToString();
    }
}

/// <summary>
/// Smart compatibility engine: tiering, real hardware/OS/driver checks, conflicts with what's already applied and a
/// pre-apply report. <see cref="TweakEngine.Apply"/> refuses anything with a Block finding, so presets, profiles, the
/// scanner and benchmarks are covered as well as the tweak pages.
/// </summary>
public static class Compatibility
{
    /// <summary>Kernel/boot/device-level changes whose outcome depends on hardware and drivers.</summary>
    private static readonly HashSet<string> ExperimentalIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "adv.timer-dynamic-tick", "adv.timer-hpet", "adv.gpu-msi", "adv.paging-executive", "adv.memory-compression",
        "adv.svchost-split", "adv.ntfs-memory", "adv.vbs-hvci", "adv.large-system-cache",
    };

    /// <summary>Tweaks the hardware rules below refer to (checked by tests so a renamed id can't silently disable a rule).</summary>
    public static readonly string[] RuleIds =
    [
        "gen.min-processor-state", "gen.core-parking", "gen.pcie-aspm-off", "gen.ultimate-plan", "gen.hibernate-off", "adv.paging-executive",
        "adv.svchost-split", "adv.memory-compression", "adv.prefetch", "svc.sysmain", "gen.disk-idle-off", "gpu.fortnite-high-performance",
        "adv.hags", "dbl.telemetry", "svc.spooler", "svc.wbiosrvc", "svc.bthserv", "svc.bthavctpsvc", "svc.btagservice", "svc.termservice", "net.eee-off",
    ];

    /// <summary>Tests: the set of tweaks conflicts are looked up in (default: the whole catalog).</summary>
    internal static Func<IEnumerable<Tweak>> Universe { get; set; } = () => TweakCatalog.All;

    public static TweakTier TierOf(Tweak t)
    {
        if (ExperimentalIds.Contains(t.Id)) return TweakTier.Experimental;
        if (t.Ops.OfType<CommandOp>().Any(c => c.ApplyCommand.Contains("bcdedit", StringComparison.OrdinalIgnoreCase))) return TweakTier.Experimental;
        if (t.Ops.OfType<DynamicRegOp>().Any(o => o.Describe().Contains("MSISupported", StringComparison.OrdinalIgnoreCase))) return TweakTier.Experimental;
        return t.Risk == Risk.Caution ? TweakTier.Advanced : TweakTier.Safe;
    }

    public static string TierLabel(TweakTier t) => t switch { TweakTier.Safe => "Safe", TweakTier.Advanced => "Advanced", _ => "Experimental" };
    public static string TierBlurb(TweakTier t) => t switch
    {
        TweakTier.Safe => "well understood, low risk on almost every PC",
        TweakTier.Advanced => "a real trade-off; read the warnings",
        _ => "changes boot, device or kernel-memory behaviour; results vary by hardware, and recovery may need a restart or Safe Mode",
    };

    // ---- conflicts ------------------------------------------------------------------------------------------------

    public enum Overlap { None, Duplicate, Conflict }

    /// <summary>
    /// How two tweaks overlap: same setting with the same value (Duplicate) or a different value (Conflict). Uses the same
    /// normalised effects as <see cref="ConflictRadar"/> (registry values and string entries, service start types, including a
    /// raw registry Start value, power settings, scheduled tasks, Store apps and command steps checking the same setting).
    /// Runtime-discovered targets aren't resolved here (this runs across the whole catalog); the radar resolves them.
    /// </summary>
    public static Overlap Between(Tweak a, Tweak b)
    {
        if (a.Id == b.Id) return Overlap.None;
        var shared = ConflictRadar.Shared(ConflictRadar.Effects(a, resolveDynamic: false), ConflictRadar.Effects(b, resolveDynamic: false));
        if (shared.Count == 0) return Overlap.None;
        return shared.Any(x => !string.Equals(x.a, x.b, StringComparison.OrdinalIgnoreCase)) ? Overlap.Conflict : Overlap.Duplicate;
    }

    /// <summary>Other tweaks that set the same thing differently and are currently applied by Money Tweaks.</summary>
    public static List<Tweak> ActiveConflicts(Tweak t, Func<Tweak, TweakState>? stateOf = null)
    {
        stateOf ??= TweakEngine.GetState;
        return Universe().Where(o => o.Kind == TweakKind.Toggle && Between(t, o) == Overlap.Conflict && BackupStore.Exists(o.Id)
            && stateOf(o) is TweakState.Applied or TweakState.Partial).ToList();
    }

    // ---- findings -------------------------------------------------------------------------------------------------

    /// <summary>Hardware, OS, driver and catalog findings for this tweak on this PC (no state checks, cheap).</summary>
    public static List<CompatFinding> StaticFindings(Tweak t, SystemProfile p)
    {
        var f = new List<CompatFinding>();
        void Add(CompatSeverity s, string text, string? other = null) => f.Add(new(s, text, other));

        if (TweakAudit.DisabledReason(t) is { } disabled) Add(CompatSeverity.Block, disabled);
        if (t.MinBuild > 0 && p.Build > 0 && p.Build < t.MinBuild) Add(CompatSeverity.Block, $"Needs Windows build {t.MinBuild} or newer; this PC has build {p.Build}.");
        if (t.Warning is { } w) Add(CompatSeverity.Warn, w);

        bool laptop = p.HasBattery == true;
        switch (t.Id)
        {
            case "gen.min-processor-state" when laptop:
                Add(CompatSeverity.Warn, "This PC has a battery: keeping the CPU at full clock drains it faster and adds heat, which can lower boost clocks under load.");
                break;
            case "gen.core-parking" or "gen.pcie-aspm-off" or "gen.ultimate-plan" when laptop:
                Add(CompatSeverity.Warn, "This PC has a battery: this uses more power, so battery life is shorter.");
                break;
            case "gen.hibernate-off" when laptop:
                Add(CompatSeverity.Warn, "This PC has a battery: without hibernation, a fully drained battery loses unsaved work instead of hibernating.");
                break;
            case "adv.paging-executive" or "adv.svchost-split" when p.RamGb is > 0 and < 16:
                Add(CompatSeverity.Warn, $"Meant for 16 GB RAM or more; this PC has {p.RamGb:0.#} GB.");
                break;
            case "adv.memory-compression" when p.RamGb is > 0 and < 16:
                Add(CompatSeverity.Warn, $"With {p.RamGb:0.#} GB RAM, memory compression is what keeps games out of the page file. Turning it off can cause stutter.");
                break;
            case "adv.prefetch" or "svc.sysmain" when p.SystemDiskIsSsd == false:
                Add(CompatSeverity.Warn, "Windows is on a hard disk here, where prefetching makes apps and games load noticeably faster. This is meant for SSDs.");
                break;
            case "gen.disk-idle-off" when p.SystemDiskIsSsd == true:
                Add(CompatSeverity.Info, "The Windows drive is an SSD; this only affects hard disks (other drives may still be HDDs).");
                break;
            case "gpu.fortnite-high-performance" when p.Gpus.Count == 1:
                Add(CompatSeverity.Block, $"Only one GPU was found ({p.Gpus[0]}), so there's nothing to choose between: this would change nothing.");
                break;
            case "adv.hags" when p.Build is > 0 and < 19041:
                Add(CompatSeverity.Block, "Hardware-accelerated GPU scheduling needs Windows 10 version 2004 (build 19041) or newer.");
                break;
            case "adv.hags":
                Add(CompatSeverity.Info, "Windows ignores this unless the GPU and driver support it (WDDM 2.7+, NVIDIA GTX 10 / AMD RX 5000 or newer). Support can't be read reliably before applying.");
                break;
            case "dbl.telemetry" when !p.IsEnterpriseLike && p.Edition.Length > 0:
                Add(CompatSeverity.Info, $"On {p.Edition} edition Windows treats this as 'Required' diagnostic data, not off.");
                break;
            case "svc.spooler" when p.PhysicalPrinters > 0:
                Add(CompatSeverity.Warn, $"{p.PhysicalPrinters} printer(s) are installed. With the Print Spooler disabled none of them can print.");
                break;
            case "svc.wbiosrvc" when p.HasBiometricDevice == true:
                Add(CompatSeverity.Warn, "A fingerprint or face sensor is installed. Windows Hello may stop working until the service starts on demand.");
                break;
            case "svc.bthserv" or "svc.bthavctpsvc" or "svc.btagservice" when p.HasBluetooth == true:
                Add(CompatSeverity.Warn, "This PC has Bluetooth. Bluetooth headsets, controllers or media buttons may need this service.");
                break;
            case "svc.termservice" when p.RemoteDesktopEnabled == true:
                Add(CompatSeverity.Warn, "Remote Desktop is turned on for this PC. Incoming connections still work on Manual, but check after a restart.");
                break;
            case "net.eee-off" when p.HasWiredAdapter == false:
                Add(CompatSeverity.Block, "No wired network adapter was found; Energy-Efficient Ethernet only exists on wired adapters.");
                break;
        }

        foreach (var issue in TweakAudit.IssuesFor(t))
            switch (issue.Kind)
            {
                case AuditKind.OneWay: Add(CompatSeverity.Warn, "Undo limit: " + issue.Text, issue.Other); break;
                case AuditKind.Ineffective or AuditKind.Obsolete: Add(CompatSeverity.Info, issue.Text, issue.Other); break;
                case AuditKind.Risky when TweakAudit.DisabledReason(t) is null: Add(CompatSeverity.Warn, issue.Text, issue.Other); break;
            }
        if (t.Ops.OfType<AppxRemoveOp>().Any())
            Add(CompatSeverity.Warn, "Undo can only re-register the app while Windows still has its files; otherwise reinstall it from the Microsoft Store.");
        return f;
    }

    /// <summary>Static findings plus conflicts with tweaks that are applied right now. Block findings stop <see cref="TweakEngine.Apply"/>.</summary>
    public static List<CompatFinding> Findings(Tweak t, SystemProfile p, Func<Tweak, TweakState>? stateOf = null)
    {
        var f = StaticFindings(t, p);
        foreach (var o in ActiveConflicts(t, stateOf))
            f.Add(new(CompatSeverity.Block, $"'{o.Name}' is applied and sets the same setting differently. Undo it first: applying this on top would overwrite its change, and its Undo would then restore the wrong value.", o.Id));
        foreach (var o in Universe().Where(o => o.Kind == TweakKind.Toggle && Between(t, o) == Overlap.Duplicate && BackupStore.Exists(o.Id)))
            f.Add(new(CompatSeverity.Info, $"'{o.Name}' already sets part of this; undoing either one undoes that part for both.", o.Id));
        return f;
    }

    /// <summary>Full pre-apply report, including the current values read from Windows. Runs state checks: call off the UI thread.</summary>
    public static CompatibilityReport Analyze(Tweak t, SystemProfile? profile = null)
    {
        var p = profile ?? SystemProfile.Current;
        TweakEngine.PrewarmChecks([t]);
        var state = TweakEngine.GetState(t);
        IReadOnlyList<string> current = [];
        try { current = TweakVerifier.Verify(t).Evidence; } catch (Exception ex) { current = [$"Couldn't read the current values: {ex.Message}"]; }
        return new(t, TierOf(t), state, Findings(t, p), current, TweakInfo.For(t));
    }
}
