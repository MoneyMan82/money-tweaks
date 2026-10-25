using System.Windows;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.ViewModels;

public sealed class TweakItem(Tweak tweak) : Observable
{
    public Tweak Tweak { get; } = tweak;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Group => Tweak.Group ?? CategoryInfo.DisplayName(Tweak.Category);
    public bool IsCaution => Tweak.Risk == Risk.Caution;
    public string RiskLabel => IsCaution ? "Caution" : "Safe";
    public bool IsGuide => Tweak.Kind == TweakKind.Guide;
    public bool NeedsAdmin => Tweak.RequiresAdmin;
    public bool NeedsReboot => Tweak.RequiresReboot;
    public bool IsRecommended => Tweak.Recommended;
    /// <summary>Safe / Advanced / Experimental, from the compatibility engine.</summary>
    public TweakTier Tier { get; } = Compatibility.TierOf(tweak);
    public string TierLabel => Compatibility.TierLabel(Tier);
    public bool IsExperimental => Tier == TweakTier.Experimental;
    public bool IsAdvancedTier => Tier == TweakTier.Advanced;
    // Advanced = the Caution label already on the card; only Experimental needs its own badge.
    public bool ShowTier => Tier == TweakTier.Experimental;
    public string TierTip => $"{TierLabel}: {Compatibility.TierBlurb(Tier)}";

    /// <summary>This PC's compatibility findings (hardware, OS, drivers), once the system profile has been read.</summary>
    private string CompatText()
    {
        if (!SystemProfile.IsDetected || IsGuide) return "";
        var f = Compatibility.StaticFindings(Tweak, SystemProfile.Current);
        return $"\n\nON THIS PC ({TierLabel}): " + (f.Count == 0 ? "no compatibility issues found."
            : string.Concat(f.Select(x => $"\n{(x.Severity switch { CompatSeverity.Block => "⛔", CompatSeverity.Warn => "⚠", _ => "ℹ" })} {x.Text}")));
    }

    public string Details => TweakInfo.For(Tweak).ToText() + CompatText() + Environment.NewLine + Environment.NewLine + (IsGuide
        ? string.Join("\n", Tweak.GuideSteps.Select((s, i) => $"{i + 1}. {s}"))
        : string.Join("\n", Tweak.Ops.Select(o => "• " + o.Describe())))
        + (_verify is null ? "" : $"\n\nTEST {_verify.At:T}: {_verify.StatusLabel}. {_verify.Summary}" + string.Concat(_verify.Evidence.Select(e => "\n  – " + e)))
        + string.Concat(TweakAudit.IssuesFor(Tweak).Select(i => $"\n! {i.Label}: {i.Text}"));

    public string CategoryName => CategoryInfo.DisplayName(Tweak.Category);
    /// <summary>Known limitations, overlaps and conflicts (with the related tweak's name), from the catalogue audit.</summary>
    public IReadOnlyList<AuditIssue> Issues { get; } = TweakAudit.IssuesFor(tweak);
    public bool HasNotes => Issues.Count > 0;
    public string NotesBadge => Issues.Count == 1 ? "1 note" : $"{Issues.Count} notes";
    public string NotesText => string.Join("\n", Issues.Select(i => $"{i.Label}: {i.Text}" +
        (i.Other is { } o && Core.Catalog.TweakCatalog.Find(o) is { } other ? $" (related: {other.Name})" : "")));
    /// <summary>Sort order for "status": applied first, unavailable last.</summary>
    public int StatusRank => State switch { TweakState.Applied => 0, TweakState.Partial => 1, TweakState.NotApplied => 2, TweakState.Unknown => 3, _ => 4 };
    /// <summary>Undo is offered for applied (or partly applied) tweaks; it restores the backed-up values.</summary>
    public bool CanUndo => !Busy && !IsGuide && State is TweakState.Applied or TweakState.Partial;

    private TweakState _state = TweakState.Unknown;
    public TweakState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsOn)); Raise(nameof(StatusText)); Raise(nameof(CanToggle)); Raise(nameof(CanUndo)); Raise(nameof(StatusRank));
        }
    }

    /// <summary>null = partial (switch shows the indeterminate gold state).</summary>
    public bool? IsOn
    {
        get => State switch { TweakState.Applied => true, TweakState.Partial => null, _ => false };
        set { /* driven by TweakService; the click is intercepted */ }
    }

    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(CanToggle)); Raise(nameof(CanUndo)); } } }
    public bool CanToggle => !Busy && State is not TweakState.NotApplicable and not TweakState.Unknown
        && (DisabledReason is null || State is TweakState.Applied or TweakState.Partial);
    public string? DisabledReason { get; } = TweakAudit.DisabledReason(tweak);
    public bool IsDisabled => DisabledReason is not null;

    private VerifyReport? _verify;
    /// <summary>Last Test/Verify result for this tweak (null until tested).</summary>
    public VerifyReport? Verify { get => _verify; set { if (Set(ref _verify, value)) { Raise(nameof(VerifyLabel)); Raise(nameof(HasVerify)); Raise(nameof(Details)); } } }
    public string VerifyLabel => _verify?.StatusLabel ?? "";
    public bool HasVerify => _verify is not null;

    private string? _message;
    public string? Message { get => _message; set => Set(ref _message, value); }

    private bool _expanded;
    // Re-read Details on open: the "on this PC" findings appear once the system profile has been detected.
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value) && value) { Raise(nameof(Details)); _related = null; Raise(nameof(Related)); Raise(nameof(HasRelated)); } } }

    /// <summary>Tweak X-Ray applies: the tweak makes system changes that can be listed (not BIOS guides).</summary>
    public bool HasXRay => TweakXRay.Applies(Tweak);
    private bool _xrayOpen;
    public bool XRayOpen { get => _xrayOpen; set => Set(ref _xrayOpen, value); }

    private IReadOnlyList<RelatedTweakRow>? _related;

    /// <summary>
    /// Related settings from other places in the library (Discovery): conflicts and duplicates first, then the same
    /// area, then tweaks serving the same purpose, each with the reason. Computed when the details are opened.
    /// </summary>
    public IReadOnlyList<RelatedTweakRow> Related => _related ??= !_expanded ? [] : Core.Discovery.TweakKnowledge.Current.Related(Tweak.Id, 5)
        .Select(r => new RelatedTweakRow(r.Other.Id, r.Other.Name, $"{CategoryInfo.DisplayName(r.Other.Category)}{(r.Other.Group is { } g ? " › " + g : "")}",
            r.Kind switch
            {
                Core.Discovery.RelationKind.Conflict => "CONFLICT", Core.Discovery.RelationKind.Duplicate => "DUPLICATE", Core.Discovery.RelationKind.TradeOff => "ORDER MATTERS",
                Core.Discovery.RelationKind.OverlappingEffect => "OVERLAPS", Core.Discovery.RelationKind.SameArea => "SAME AREA", _ => "WORKS WITH",
            },
            r.Text + (Core.Engine.BackupStore.Exists(r.Other.Id) ? " It's applied right now." : ""),
            r.Kind switch { Core.Discovery.RelationKind.Conflict => "Danger", Core.Discovery.RelationKind.Duplicate or Core.Discovery.RelationKind.TradeOff or Core.Discovery.RelationKind.OverlappingEffect => "Gold", Core.Discovery.RelationKind.SameArea => "Violet", _ => "Accent" }))
        .ToList();

    public bool HasRelated => Related.Count > 0;

    public string StatusText => State switch
    {
        TweakState.Applied => IsGuide ? "Done" : "Applied",
        TweakState.Partial => "Partially applied",
        TweakState.NotApplicable => Tweak.Category == TweakCategory.AppRemoval ? "Not installed"
            : Tweak.MinBuild > OsInfo.Build ? (Tweak.MinBuild >= 22000 ? "Windows 11 only" : $"Needs build {Tweak.MinBuild}+") : "Not available on this PC",
        TweakState.Unknown => "Checking…",
        _ => ""
    };

    public async Task RefreshAsync()
    {
        var s = await Task.Run(() => TweakEngine.GetState(Tweak));
        State = s;
        if (IsDisabled && Message is null) Message = "⚠ " + DisabledReason;
    }
}

/// <summary>Apply/undo workflow shared by every page: admin check, caution confirmation, restore point, logging.</summary>
public static class TweakService
{
    private static bool _restorePointThisSession;
    // One app-wide limit on concurrent state checks (some read the registry, some start processes).
    private static readonly SemaphoreSlim StateGate = new(4);

    /// <summary>Refreshes many items off the UI thread: batched PowerShell checks first, then bounded parallel checks.</summary>
    public static async Task RefreshStatesAsync(IReadOnlyCollection<TweakItem> items, IProgress<int>? done = null)
    {
        static bool Slow(TweakItem i) => i.Tweak.Ops.OfType<Core.Ops.CommandOp>().Any(o => o.HasSlowCheck);
        int count = 0;
        async Task RefreshAll(IEnumerable<TweakItem> set) => await Task.WhenAll(set.Select(async i =>
        {
            await StateGate.WaitAsync();
            try { await i.RefreshAsync(); }
            finally { StateGate.Release(); done?.Report(Interlocked.Increment(ref count)); }
        }));
        // Start the slow PowerShell checks in the background and show every registry-backed tweak right away.
        var slow = items.Where(Slow).ToList();
        var prewarm = Task.Run(() => TweakEngine.PrewarmChecks(slow.Select(i => i.Tweak)));
        await RefreshAll(items.Where(i => !Slow(i)));
        await prewarm;
        await RefreshAll(slow);
    }
    public static event Action? Changed;

    /// <summary>Read-only test: reads every value back from Windows. Changes nothing.</summary>
    public static async Task<VerifyReport> VerifyAsync(TweakItem item)
    {
        item.Busy = true;
        try
        {
            var r = await Task.Run(() => { TweakEngine.PrewarmChecks([item.Tweak]); return TweakVerifier.Verify(item.Tweak); });
            item.Verify = r;
            item.State = r.State;
            item.Message = $"Test: {r.StatusLabel}. {r.Summary}";
            Log.Info($"Verify {item.Tweak.Id}: {r.StatusLabel} – {r.Summary}");
            return r;
        }
        finally { item.Busy = false; }
    }

    /// <summary>Live test after confirmation: apply, read back, undo, check every original value is back.</summary>
    public static async Task<VerifyReport?> RoundTripAsync(TweakItem item)
    {
        if (!TweakVerifier.CanRoundTrip(item.Tweak, out var why)) { Dialog.Show("Live test not available", why); return null; }
        if (item.State != TweakState.NotApplied) { Dialog.Show("Live test not available", "The live test only runs on tweaks that are currently off, so your current settings are never touched. Use the read-only Test instead."); return null; }
        if (!Dialog.Confirm($"Live test “{item.Name}”?",
                "Money Tweaks will apply this tweak, read every value back from Windows, undo it, and check that every original value is restored exactly. " +
                "This takes a few seconds and briefly changes the setting." + (item.NeedsReboot ? "\n\nThe effect itself needs a restart, so only the stored settings can be checked." : ""),
                "Run live test", item.IsCaution ? DialogKind.Warning : DialogKind.Confirm)) return null;
        if (!EnsureAdmin([item.Tweak])) return null;
        item.Busy = true;
        item.Message = "Live test running…";
        try
        {
            var r = await Task.Run(() => TweakVerifier.RoundTrip(item.Tweak));
            item.Verify = r;
            item.State = await Task.Run(() => TweakEngine.GetState(item.Tweak));
            item.Message = $"Live test: {r.StatusLabel}. {r.Summary}";
            Log.Info($"Round-trip {item.Tweak.Id}: {r.StatusLabel} – {r.Summary}");
            return r;
        }
        finally { item.Busy = false; Changed?.Invoke(); }
    }

    public static bool EnsureAdmin(IEnumerable<Tweak> tweaks)
    {
        if (Admin.IsElevated || !tweaks.Any(t => t.RequiresAdmin)) return true;
        if (Dialog.Confirm("Administrator rights needed",
                "This change edits system-wide settings, so Money Tweaks needs to restart as administrator. Nothing has been changed yet.",
                "Restart as admin", DialogKind.Warning))
        {
            if (Admin.RestartElevated()) Application.Current.Shutdown();
        }
        return false;
    }

    private static async Task MaybeRestorePointAsync()
    {
        if (_restorePointThisSession || !App.Settings.CreateRestorePointBeforeBatch || !Admin.IsElevated) return;
        _restorePointThisSession = true;
        var (ok, msg) = await Task.Run(() => RestorePoints.Create($"Money Tweaks {App.Version} – before changes"));
        if (!ok) Log.Warn("Continuing without a new restore point: " + msg);
    }

    /// <summary>
    /// Tweak Conflict Radar before applying: compares what the selected tweaks write with each other and with what's applied,
    /// shows each conflict with evidence, and applies the chosen fixes. Skipping and reordering only change this run; undoing an
    /// applied tweak needs a second confirmation and goes through its backup. Returns the items to apply, or null to stop.
    /// </summary>
    internal static async Task<List<TweakItem>?> RadarAsync(List<TweakItem> targets, IProgress<string>? progress = null)
    {
        progress?.Report("Checking for conflicts…");
        List<RadarConflict> conflicts;
        try { conflicts = await Task.Run(() => ConflictRadar.ScanForApply(targets.Select(i => i.Tweak).ToList())); }
        catch (Exception ex) { Log.Error("Conflict Radar failed", ex); return targets; }   // the engine still blocks applied conflicts
        if (conflicts.Count == 0) return targets;
        Log.Info($"Conflict Radar: {conflicts.Count} finding(s): " + string.Join(" | ", conflicts.Select(c => $"{c.KindLabel} {c.Selected.Id} ↔ {c.Other.Id}{(c.OtherApplied ? " (applied)" : "")}: {string.Join("; ", c.Evidence)}")));
        var choices = ConflictRadarDialog.Show(conflicts, targets.Count == 1 ? "Continue" : "Continue with these choices");
        if (choices is null) { Log.Info("Conflict Radar: cancelled, nothing applied"); return null; }

        var result = targets.ToList();
        var undo = choices.Where(kv => kv.Value.Kind == RadarFixKind.UndoAppliedFirst).ToList();
        if (undo.Count > 0)
        {
            if (!Dialog.Confirm("Undo applied tweaks first?",
                    string.Join("\n", undo.Select(kv => $"• Undo “{kv.Key.Other.Name}”, then apply “{kv.Key.Selected.Name}”")) +
                    "\n\nUndo restores each one's backed-up values, or runs its reverse command for command steps, and checks the result. You can apply them again later.",
                    "Undo and continue", DialogKind.Warning)) { Log.Info("Conflict Radar: undo declined, nothing applied"); return null; }
            if (!EnsureAdmin(undo.Select(kv => kv.Key.Other))) return null;
            foreach (var (c, _) in undo.DistinctBy(kv => kv.Key.Other.Id))
            {
                progress?.Report($"Undoing {c.Other.Name}…");
                var r = await Task.Run(() => TweakEngine.Undo(c.Other));
                Log.Info($"Conflict Radar: undo {c.Other.Id} before {c.Selected.Id}: {(r.Success ? "ok" : r.Message)}");
                if (!r.Success)
                    foreach (var it in result.Where(i => i.Tweak.Id == c.Selected.Id).ToList())
                    {
                        it.Message = $"⚠ Not applied: undoing “{c.Other.Name}” failed ({r.Message})";
                        result.Remove(it);
                    }
            }
            Changed?.Invoke();
        }
        foreach (var (c, fix) in choices)
            switch (fix.Kind)
            {
                case RadarFixKind.SkipSelected:
                    foreach (var it in result.Where(i => i.Tweak.Id == fix.TweakId).ToList())
                    {
                        it.Message = $"Skipped (Conflict Radar: {c.KindLabel.ToLowerInvariant()} with “{(c.Selected.Id == fix.TweakId ? c.Other.Name : c.Selected.Name)}”).";
                        result.Remove(it);
                    }
                    break;
                case RadarFixKind.MoveFirst when result.FirstOrDefault(i => i.Tweak.Id == fix.TweakId) is { } first:
                    result.Remove(first);
                    result.Insert(0, first);
                    break;
            }
        Log.Info($"Conflict Radar: applying {result.Count} of {targets.Count} after the chosen fixes");
        return result;
    }

    private static void ShowBlocked(TweakItem item, CompatibilityReport report)
    {
        item.Message = "⛔ " + string.Join(" ", report.Blockers.Select(b => b.Text));
        Dialog.Show($"“{item.Tweak.Name}” can't be applied here", string.Join("\n\n", report.Blockers.Select(b => "⛔ " + b.Text)) + "\n\nNothing was changed.", DialogKind.Warning);
    }

    public static async Task ToggleAsync(TweakItem item)
    {
        if (!item.CanToggle) return;
        bool apply = item.State != TweakState.Applied;
        var t = item.Tweak;
        if (apply && !item.IsGuide)
        {
            // Compatibility check on this PC first: blocked → explain and stop; risky → show the full report and confirm.
            item.Busy = true;
            item.Message = "Checking compatibility…";
            CompatibilityReport report;
            try { report = await Task.Run(() => Compatibility.Analyze(t)); }
            finally { item.Busy = false; item.Message = null; }
            item.State = report.State;
            if (report.AlreadyApplied) { item.Message = "Already applied on this PC; nothing to change."; return; }
            // Blocked by this PC (hardware, Windows build, disabled tweak): Conflict Radar can't help, explain and stop.
            if (report.Blockers.Any(b => b.OtherTweakId is null))
            {
                ShowBlocked(item, report);
                return;
            }
            // Conflict Radar: duplicates, contradictions and documented interactions with what's applied, with fixes to choose from.
            var cleared = await RadarAsync([item]);
            if (cleared is null || cleared.Count == 0) { item.State = await Task.Run(() => TweakEngine.GetState(t)); return; }
            if (report.Blocked)
            {
                report = await Task.Run(() => Compatibility.Analyze(t));   // the radar may have undone the conflicting tweak
                item.State = report.State;
                if (report.Blocked) { ShowBlocked(item, report); return; }
            }
            bool confirm = report.Tier == TweakTier.Experimental || report.Warnings.Any() || (t.Risk == Risk.Caution && App.Settings.ConfirmCautionTweaks);
            if (confirm)
            {
                // Tweak X-Ray preview: the real locations and current → new values, read right before confirming.
                item.Busy = true;
                item.Message = "Reading current values…";
                string preview;
                try { preview = await Task.Run(() => TweakXRay.Inspect(t).Compact()); }
                catch (Exception ex) { preview = "WHAT CHANGES ON THIS PC\nCouldn't read the current values: " + ex.Message; }
                finally { item.Busy = false; item.Message = null; }
                if (!Dialog.Confirm($"Apply “{t.Name}”?", report.ToText() + "\n\n" + preview, "Apply", report.Tier == TweakTier.Safe ? DialogKind.Confirm : DialogKind.Warning))
                    return;
            }
            if (!EnsureAdmin([t])) return;
            await MaybeRestorePointAsync();
        }
        else if (!apply && !item.IsGuide && !EnsureAdmin([t])) return;

        item.Busy = true;
        item.Message = apply ? "Applying…" : "Reverting…";
        try
        {
            var r = await Task.Run(() => apply ? TweakEngine.Apply(t) : TweakEngine.Undo(t));
            item.State = r.NewState;
            item.Message = r.Success ? r.Message : "⚠ " + r.Message;
            if (!r.Success) Log.Warn($"{t.Name}: {r.Message}");
        }
        catch (Exception ex)
        {
            item.Message = "⚠ " + ex.Message;
            Log.Error($"{t.Name} failed", ex);
        }
        finally
        {
            item.Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Applies or reverts many tweaks with one confirmation. Returns (succeeded, failed).</summary>
    public static async Task<(int ok, int failed, bool reboot)> BatchAsync(IReadOnlyList<TweakItem> items, bool apply, IProgress<string>? progress = null)
    {
        var targets = items.Where(i => i.CanToggle && !i.IsGuide && (apply ? i.State != TweakState.Applied : i.State is TweakState.Applied or TweakState.Partial)).ToList();
        if (targets.Count == 0) return (0, 0, false);
        int skipped = 0;
        if (apply)
        {
            // Conflict Radar over the whole selection before anything is changed (duplicates, contradictions, order).
            var cleared = await RadarAsync(targets, progress);
            if (cleared is null)
            {
                foreach (var it in targets) it.Message = "Not applied: cancelled in Conflict Radar.";
                return (0, targets.Count, false);   // not "0 failed": callers would treat that as success
            }
            skipped = targets.Count - cleared.Count;   // skipped by choice: reported as not applied
            targets = cleared;
            if (targets.Count == 0) return (0, skipped, false);
        }
        if (!EnsureAdmin(targets.Select(t => t.Tweak)))
        {
            // Not "0 failed": callers treat that as success (Presets dropped the run from its undo history).
            foreach (var it in targets) it.Message = "⚠ Needs administrator rights; nothing was changed.";
            return (0, targets.Count, false);
        }
        if (apply) await MaybeRestorePointAsync();
        int ok = 0, failed = 0; bool reboot = false;
        if (apply)
        {
            // The engine blocks unsupported tweaks and conflicts with anything applied (including an earlier item in this
            // batch) as it goes. Experimental ones get their own confirmation on top of the caller's list.
            progress?.Report("Checking compatibility…");
            await Task.Run(() => _ = SystemProfile.Current);
            var experimental = targets.Where(i => i.IsExperimental).ToList();
            if (experimental.Count > 0 && !Dialog.Show($"Include {experimental.Count} experimental tweak(s)?",
                    string.Join("\n", experimental.Select(i => "• " + i.Name)) + $"\n\nExperimental: {Compatibility.TierBlurb(TweakTier.Experimental)}.\n\nChoose Skip to apply only the others.",
                    DialogKind.Warning, "Include", "Skip"))
            {
                foreach (var it in experimental) it.Message = "Skipped (experimental, not confirmed).";
                targets = targets.Except(experimental).ToList();
                if (targets.Count == 0) return (0, experimental.Count, false);
            }
        }
        foreach (var it in targets)
        {
            progress?.Report($"{(apply ? "Applying" : "Reverting")} {ok + failed + 1}/{targets.Count}: {it.Name}");
            it.Busy = true;
            var r = await Task.Run(() => apply ? TweakEngine.Apply(it.Tweak) : TweakEngine.Undo(it.Tweak));
            it.State = r.NewState;
            it.Message = r.Success ? r.Message : "⚠ " + r.Message;
            it.Busy = false;
            if (r.Success) { ok++; reboot |= it.NeedsReboot; } else failed++;
        }
        Changed?.Invoke();
        return (ok, failed + skipped, reboot);
    }
}

/// <summary>One related setting shown in a tweak's details; Tone is a theme brush key.</summary>
public sealed record RelatedTweakRow(string Id, string Title, string Where, string Badge, string Text, string Tone)
{
    public System.Windows.Media.Brush ToneBrush => (System.Windows.Media.Brush)Application.Current.Resources[Tone];
    public System.Windows.Media.Brush SoftBrush => (System.Windows.Media.Brush)Application.Current.Resources[Tone == "Danger" ? "DangerSoft" : Tone == "Gold" ? "GoldSoft" : Tone == "Violet" ? "VioletSoft" : "AccentSoft"];
}
