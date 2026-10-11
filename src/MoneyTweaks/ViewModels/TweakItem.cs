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
    public string Details => IsGuide
        ? string.Join("\n", Tweak.GuideSteps.Select((s, i) => $"{i + 1}. {s}"))
        : string.Join("\n", Tweak.Ops.Select(o => "• " + o.Describe()));

    private TweakState _state = TweakState.Unknown;
    public TweakState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsOn)); Raise(nameof(StatusText)); Raise(nameof(CanToggle));
        }
    }

    /// <summary>null = partial (switch shows the indeterminate gold state).</summary>
    public bool? IsOn
    {
        get => State switch { TweakState.Applied => true, TweakState.Partial => null, _ => false };
        set { /* driven by TweakService; the click is intercepted */ }
    }

    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(CanToggle)); } }
    public bool CanToggle => !Busy && State is not TweakState.NotApplicable and not TweakState.Unknown;

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
    }
}

/// <summary>Apply/undo workflow shared by every page: admin check, caution confirmation, restore point, logging.</summary>
public static class TweakService
{
    private static bool _restorePointThisSession;
    public static event Action? Changed;

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
        if (!EnsureAdmin(targets.Select(t => t.Tweak))) return (0, 0, false);
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
