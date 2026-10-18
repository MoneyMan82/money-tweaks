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
    public string Details => TweakInfo.For(Tweak).ToText() + Environment.NewLine + Environment.NewLine + (IsGuide
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
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

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

    public static async Task ToggleAsync(TweakItem item)
    {
        if (!item.CanToggle) return;
        bool apply = item.State != TweakState.Applied;
        var t = item.Tweak;
        if (apply && !item.IsGuide)
        {
            if (!EnsureAdmin([t])) return;
            if (t.Risk == Risk.Caution && App.Settings.ConfirmCautionTweaks &&
                !Dialog.Confirm($"Apply “{t.Name}”?", $"{t.Description}\n\n{(t.Warning is null ? "" : "⚠ " + t.Warning + "\n\n")}You can undo this at any time from the same switch.", "Apply", DialogKind.Warning))
                return;
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
        if (!EnsureAdmin(targets.Select(t => t.Tweak)))
        {
            // Not "0 failed": callers treat that as success (Presets dropped the run from its undo history).
            foreach (var it in targets) it.Message = "⚠ Needs administrator rights; nothing was changed.";
            return (0, targets.Count, false);
        }
        if (apply) await MaybeRestorePointAsync();
        int ok = 0, failed = 0; bool reboot = false;
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
        return (ok, failed, reboot);
    }
}
