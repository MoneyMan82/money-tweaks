using System.Text;
using System.Windows;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>App-wide gaming profile runtime: one active profile, automatic switching on game launch/exit, crash recovery.</summary>
public static class ProfileService
{
    public static ProfileFile File { get; private set; } = ProfileStore.Load();
    public static ProfileSession? Active => File.Active;
    public static event Action? Changed;
    private static CancellationTokenSource? _watch;
    private static string? _suppressedExe;   // game the user manually switched away from; don't re-trigger until it exits
    private static string? _adminNoticeExe;
    private static int _missingPolls;
    private static bool _switching;

    public static void Save()
    {
        try { ProfileStore.Save(File); }
        catch (Exception ex) { Log.Error("Saving profiles failed", ex); Dialog.Show("Couldn't save profiles", ex.Message, DialogKind.Error); }
    }

    public static string Describe(GamingProfile p)
    {
        var pv = ProfileEngine.Preview(p);
        var sb = new StringBuilder();
        sb.AppendLine(pv.Changes.Count == 0 ? "Nothing to change: everything in this profile is already active." : $"Will change ({pv.Changes.Count}):");
        foreach (var c in pv.Changes) sb.AppendLine("• " + c);
        if (pv.AlreadyActive.Count > 0) sb.AppendLine($"\nAlready active, left as is ({pv.AlreadyActive.Count}): {string.Join(", ", pv.AlreadyActive)}");
        if (pv.Skipped.Count > 0) sb.AppendLine($"\nSkipped ({pv.Skipped.Count}): {string.Join(", ", pv.Skipped)}");
        if (pv.Conflicts.Count > 0) sb.AppendLine("\nConflicts:\n" + string.Join("\n", pv.Conflicts.Select(c => "⚠ " + c)));
        sb.Append("\nEverything is backed up first and restored (and read back) when the profile is switched off. These settings aim for consistency; Money Tweaks doesn't promise FPS or ping gains. Measure with Test & Benchmark.");
        return sb.ToString();
    }

    /// <summary>Activates a profile (switching off the current one first). With <paramref name="confirm"/>, shows every change first.</summary>
    public static async Task<bool> ActivateAsync(GamingProfile p, string? trigger, bool confirm)
    {
        if (_switching) return false;
        if (confirm)
        {
            var text = await Task.Run(() => Describe(p));
            if (!Dialog.Confirm($"Activate “{p.Name}”?", text, "Activate", DialogKind.Confirm)) return false;
            if (await Task.Run(() => ProfileEngine.NeedsAdmin(p)) && !Admin.IsElevated)
            {
                if (Dialog.Confirm("Administrator rights needed", "This profile changes system-wide settings, so Money Tweaks needs to restart as administrator. Nothing has been changed yet.", "Restart as admin", DialogKind.Warning)
                    && Admin.RestartElevated()) Application.Current.Shutdown();
                return false;
            }
        }
        _switching = true;
        try
        {
            if (Active is not null && !await DeactivateCoreAsync()) return false;
            var (session, problems) = await Task.Run(() => ProfileEngine.Activate(p, trigger, s => { File.Active = s; Save(); }));
            if (session is null) { File.Active = null; Save(); }
            Changed?.Invoke();
            if (session is null) Dialog.Show($"“{p.Name}” was not activated", string.Join("\n", problems), DialogKind.Error);
            else MainWindow.Current?.ShowToast($"Profile “{p.Name}” active{(trigger is null ? "" : $" ({trigger} started)")}." + (problems.Count > 0 ? " " + string.Join(" ", problems) : ""), problems.Count > 0 ? "Gold" : "Success");
            return session is not null;
        }
        finally { _switching = false; }
    }

    public static async Task<bool> DeactivateAsync(bool userInitiated = true)
    {
        if (_switching || Active is null) return false;
        if (userInitiated && Active.TriggeredBy is { } exe && ProfileEngine.RunningGame([exe]) is not null) _suppressedExe = exe;
        _switching = true;
        try { return await DeactivateCoreAsync(); }
        finally { _switching = false; }
    }

    private static async Task<bool> DeactivateCoreAsync()
    {
        var s = Active!;
        var problems = await Task.Run(() => ProfileEngine.Deactivate(s));
        if (problems.Count == 0) { File.Active = null; Save(); }
        Changed?.Invoke();
        if (problems.Count == 0) MainWindow.Current?.ShowToast($"Profile “{s.ProfileName}” off. Your settings are restored and verified.", "Success");
        else Dialog.Show("Some settings were not restored", string.Join("\n", problems) + "\n\nThe profile stays marked as active so you can retry.", DialogKind.Error);
        return problems.Count == 0;
    }

    // ---- automatic switching -------------------------------------------------------------------------------------
    public static void StartWatcher()
    {
        if (_watch is not null || File.AutoSwitch.Count == 0) return;
        _watch = new CancellationTokenSource();
        var ct = _watch.Token;
        var ui = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct); } catch { break; }
                try { await Poll(ui); } catch (Exception ex) { Log.Debug("profile watcher: " + ex.Message); }
            }
        });
        Log.Info("Profile auto-switch watcher started");
    }

    public static void StopWatcher() { _watch?.Cancel(); _watch = null; }

    private static async Task Poll(System.Windows.Threading.Dispatcher ui)
    {
        if (_switching) return;
        if (_suppressedExe is not null && ProfileEngine.RunningGame([_suppressedExe]) is null) _suppressedExe = null;
        if (Active is { TriggeredBy: { } exe })
        {
            // Restore once the game that triggered the profile has been gone for two polls (~4 s).
            _missingPolls = ProfileEngine.RunningGame([exe]) is null ? _missingPolls + 1 : 0;
            if (_missingPolls >= 2) { _missingPolls = 0; await ui.InvokeAsync(() => DeactivateAsync(userInitiated: false)).Task.Unwrap(); }
            return;
        }
        if (Active is not null) return; // a manually activated profile is never replaced automatically
        foreach (var p in ProfileStore.All(File).Where(p => File.AutoSwitch.Contains(p.Id) && p.GameExes.Count > 0))
        {
            var running = ProfileEngine.RunningGame(p.GameExes);
            if (running is null || running == _suppressedExe) continue;
            if (!Admin.IsElevated && ProfileEngine.NeedsAdmin(p))
            {
                if (_adminNoticeExe != running)
                {
                    _adminNoticeExe = running;
                    await ui.InvokeAsync(() => MainWindow.Current?.ShowToast($"{running} started, but “{p.Name}” needs Money Tweaks running as admin to switch automatically.", "Gold"));
                }
                return;
            }
            await ui.InvokeAsync(() => ActivateAsync(p, running, confirm: false)).Task.Unwrap();
            return;
        }
    }

    /// <summary>At startup: a profile was still active when Money Tweaks last closed. Offer to restore.</summary>
    public static async Task CheckPendingAsync()
    {
        if (Active is not { } s) { StartWatcher(); return; }
        var stillRunning = s.TriggeredBy is { } exe && ProfileEngine.RunningGame([exe]) is not null;
        if (!stillRunning && Dialog.Confirm("Restore settings from a gaming profile?",
                $"“{s.ProfileName}” was still active when Money Tweaks closed (started {s.StartedAt:g}). Restore the settings it changed?", "Restore", DialogKind.Warning))
        {
            if (!Admin.IsElevated && s.OriginalTweakStates.Keys.Any(id => Core.Catalog.TweakCatalog.Find(id)?.RequiresAdmin == true))
            {
                if (Dialog.Confirm("Administrator rights needed", "Restoring these settings needs administrator rights.", "Restart as admin", DialogKind.Warning) && Admin.RestartElevated()) Application.Current.Shutdown();
                return;
            }
            await DeactivateAsync(userInitiated: false);
        }
        StartWatcher();
    }

    /// <summary>On exit: put everything back so Windows isn't left in the profile's state.</summary>
    public static void Shutdown()
    {
        StopWatcher();
        if (Active is not { } s) return;
        try
        {
            var problems = ProfileEngine.Deactivate(s);
            if (problems.Count == 0) { File.Active = null; ProfileStore.Save(File); }
        }
        catch (Exception ex) { Log.Error("Restoring profile on exit failed; it will be offered at next start", ex); }
    }
}
