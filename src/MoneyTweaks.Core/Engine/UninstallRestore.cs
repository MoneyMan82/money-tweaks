using System.Text.Json;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Display;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Core.Engine;

/// <summary>One thing the uninstall restore did (or couldn't do).</summary>
public sealed record RestoreItem(string Area, string Name, bool Ok, string Message, bool NeedsAdmin = false);

public sealed class RestoreReport
{
    public List<RestoreItem> Items { get; set; } = [];
    public bool RestartRecommended { get; set; }
    /// <summary>Changes that are still applied because they need administrator rights this pass didn't have.</summary>
    public List<RestoreItem> Remaining => [.. Items.Where(i => !i.Ok)];
    public int Restored => Items.Count(i => i.Ok);
    public void Merge(RestoreReport other) { Items.RemoveAll(i => i.NeedsAdmin && other.Items.Any(o => o.Area == i.Area && o.Name == i.Name)); Items.AddRange(other.Items); RestartRecommended |= other.RestartRecommended; }
    public string ToJson() => JsonSerializer.Serialize(this);
    public static RestoreReport? FromJson(string json) { try { return JsonSerializer.Deserialize<RestoreReport>(json); } catch { return null; } }
}

/// <summary>
/// Puts Windows back the way it was before Money Tweaks, for uninstall: every applied tweak (from its backup, newest
/// first, verified by read-back), an active gaming profile, CPU/GPU tuning, NVIDIA driver profile changes, game config
/// files (Potato Graphics, Fortnite stretched resolution), display modes changed for stretched resolutions, and Money
/// Tweaks' own startup entry. Everything goes through the same restore code the app's own Undo buttons use.
///
/// Runs in two passes so the app never has to run elevated just to be removed: the user pass does everything a standard
/// user can (per-user tweaks, files, displays, startup entry); <see cref="NeedsAdminPass"/> then says whether anything is
/// left that needs administrator rights, which the admin pass (an elevated copy, same data folder) finishes. Backups of
/// anything that couldn't be restored are kept, so reinstalling Money Tweaks can still undo it.
/// </summary>
public static class UninstallRestore
{
    public static RestoreReport Run(bool adminPass, IProgress<string>? progress = null)
    {
        var r = new RestoreReport();
        bool elevated = Admin.IsElevated;
        void Step(string s) { progress?.Report(s); Log.Info("Uninstall restore: " + s); }
        void Add(RestoreItem i) { r.Items.Add(i); Log.Info($"Uninstall restore: {i.Area} / {i.Name}: {(i.Ok ? "ok" : "not restored")} - {i.Message}"); }

        // 1. An active gaming profile first: it put back its own earlier state of tweaks, power plan and timer.
        Step("Turning off the active gaming profile");
        var profiles = ProfileStore.Load();
        if (profiles.Active is { } session)
        {
            try
            {
                var problems = ProfileEngine.Deactivate(session);
                if (problems.Count == 0) { profiles.Active = null; ProfileStore.Save(profiles); Add(new("Gaming profile", session.ProfileName, true, "Turned off; its settings were restored and verified.")); }
                else Add(new("Gaming profile", session.ProfileName, false, string.Join(" ", problems), NeedsAdmin: !elevated));
            }
            catch (Exception ex) { Add(new("Gaming profile", session.ProfileName, false, ex.Message, NeedsAdmin: !elevated)); }
        }

        // 2. Tweaks, newest first (so two tweaks touching the same setting unwind in the right order).
        var backups = BackupStore.AllIds()
            .Select(id => (id, tweak: TweakCatalog.Find(id), at: BackupStore.Load(id)?.AppliedAt ?? DateTime.MinValue))
            .OrderByDescending(x => x.at).ToList();
        int n = 0;
        foreach (var (id, t, _) in backups)
        {
            n++;
            if (t is null) { Add(new("Tweak", id, false, "This version no longer knows this tweak; its backup is kept in the backups folder.")); continue; }
            if (t.RequiresAdmin && !elevated) { Add(new("Tweak", t.Name, false, "Needs administrator rights.", NeedsAdmin: true)); continue; }
            Step($"Undoing tweaks ({n} of {backups.Count}): {t.Name}");
            try
            {
                var res = TweakEngine.Undo(t);
                Add(new("Tweak", t.Name, res.Success, res.Message));
                if (res.Success && t.RequiresReboot) r.RestartRecommended = true;
            }
            catch (Exception ex) { Add(new("Tweak", t.Name, false, ex.Message)); }
        }

        // 3. CPU and GPU tuning (power limits, clock locks, CPU power settings, Afterburner offsets).
        var tuning = TuningState.Load();
        if (tuning.HasChanges)
        {
            if (!elevated) Add(new("CPU/GPU tuning", "Tuning", false, "Needs administrator rights.", NeedsAdmin: true));
            else
            {
                Step("Reverting CPU and GPU tuning");
                try { var (ok, log) = TuningEngine.RevertAll(tuning); Add(new("CPU/GPU tuning", "Tuning", ok, ok ? "Reverted." : string.Join(" ", log))); }
                catch (Exception ex) { Add(new("CPU/GPU tuning", "Tuning", false, ex.Message)); }
            }
        }

        // 4. NVIDIA driver profiles (driver settings writes need administrator rights).
        var nvFn = NvFortniteState.Load();
        if (nvFn.Backup is not null)
        {
            if (!elevated) Add(new("NVIDIA", "Fortnite texture settings", false, "Needs administrator rights.", NeedsAdmin: true));
            else
            {
                Step("Restoring Fortnite's NVIDIA profile");
                try { var (ok, msg, _) = NvidiaFortnite.Restore(nvFn); Add(new("NVIDIA", "Fortnite texture settings", ok, msg)); }
                catch (Exception ex) { Add(new("NVIDIA", "Fortnite texture settings", false, ex.Message)); }
            }
        }
        var nvProfile = NvProfileState.Load();
        if (nvProfile.LastBackupJson is { } snapPath && NvidiaProfilePresets.LoadSnapshot(snapPath) is { } snap)
        {
            if (!elevated) Add(new("NVIDIA", "Fortnite profile preset", false, "Needs administrator rights.", NeedsAdmin: true));
            else
            {
                Step("Restoring the NVIDIA profile from before the preset");
                try
                {
                    var log = NvidiaProfilePresets.RestoreSnapshot(snap);
                    nvProfile.LastBackupJson = null; nvProfile.LastPreset = null; nvProfile.Save();
                    Add(new("NVIDIA", "Fortnite profile preset", true, log.LastOrDefault() ?? "Restored."));
                }
                catch (Exception ex) { Add(new("NVIDIA", "Fortnite profile preset", false, ex.Message)); }
            }
        }

        if (!adminPass)
        {
            // 5. Game config files (Potato Graphics, Fortnite stretched resolution): the copies taken before the first change.
            foreach (var g in PotatoGraphics.Games.Where(g => g.HasBackup))
            {
                Step($"Restoring {g.Name}'s settings file");
                if (PotatoGraphics.IsRunning(g)) { Add(new("Game settings", g.Name, false, $"{g.Name} is running; close it and run Undo in Money Tweaks, or restore the file from the backups folder.")); continue; }
                try { var msg = PotatoGraphics.Restore(g); Add(new("Game settings", g.Name, !g.HasBackup, msg)); }
                catch (Exception ex) { Add(new("Game settings", g.Name, false, ex.Message)); }
            }
            if (FortniteResolution.HasBackup)
            {
                Step("Restoring Fortnite's resolution settings");
                try { var msg = FortniteResolution.Restore(); Add(new("Game settings", "Fortnite stretched resolution", !FortniteResolution.HasBackup, msg)); }
                catch (Exception ex) { Add(new("Game settings", "Fortnite stretched resolution", false, ex.Message)); }
            }

            // 6. Display modes Money Tweaks changed (stretched desktop resolution), back to the saved originals.
            var ds = DisplayState.Load();
            foreach (var (device, o) in ds.Originals.ToList())
            {
                Step($"Restoring the display mode of {device}");
                try
                {
                    var res = DisplayManager.ApplyAndSave(device, o.Mode, o.Scaling);
                    if (res == ChangeResult.Ok) { ds.Originals.Remove(device); Add(new("Display", device, true, $"Back to {o.Width}×{o.Height} @ {o.Hz} Hz.")); }
                    else Add(new("Display", device, false, DisplayManager.Explain(res, o.Width, o.Height, o.Hz)));
                }
                catch (Exception ex) { Add(new("Display", device, false, ex.Message)); }
            }
            ds.AutoFortnite.Enabled = false;
            ds.Save();

            // 7. Money Tweaks' own "start with Windows" entry, only when it starts this copy (the one being uninstalled),
            //    or a copy that no longer exists. Never from a test host or another tool loading this code.
            if (AutoStart.Current() is { } cmd && Environment.ProcessPath is { } self
                && Path.GetFileNameWithoutExtension(self).StartsWith("MoneyTweaks", StringComparison.OrdinalIgnoreCase)
                && (cmd.Contains(self, StringComparison.OrdinalIgnoreCase) || !File.Exists(cmd.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")))
            {
                try { AutoStart.Disable(); Add(new("Startup", "Start Money Tweaks with Windows", !AutoStart.IsEnabled, "Removed.")); }
                catch (Exception ex) { Add(new("Startup", "Start Money Tweaks with Windows", false, ex.Message)); }
            }
        }

        // A frame-timing session left behind by a crashed copy (harmless, but it shouldn't outlive the app). Only when no
        // other Money Tweaks is running: a running copy's session is its own.
        try
        {
            var others = System.Diagnostics.Process.GetProcessesByName("MoneyTweaks");
            bool alone = others.All(p => p.Id == Environment.ProcessId);
            foreach (var p in others) p.Dispose();
            if (alone) PresentMonitor.StopLeftoverSession();
        }
        catch { }
        Step(r.Remaining.Count == 0 ? "Done" : "Done, with items left");
        return r;
    }

    /// <summary>True when something is still applied that only an elevated pass can restore.</summary>
    public static bool NeedsAdminPass(RestoreReport userPass) => userPass.Items.Any(i => i.NeedsAdmin);
}
