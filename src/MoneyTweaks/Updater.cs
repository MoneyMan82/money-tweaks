using System.Diagnostics;
using System.Windows;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>UI side of in-app updates: check, confirm, download with progress, install, verify after restart.</summary>
public static class Updater
{
    public static UpdateInfo? Available { get; private set; }
    public static event Action<string>? StatusChanged;
    private static bool _busy;

    public static Version Current => typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>
    /// Runs once at startup after an in-app update: confirms the running version really is the one we installed.
    /// If not, the update failed, so say so clearly and stop auto-offering that release (no endless loop).
    /// </summary>
    public static void VerifyPendingUpdate()
    {
        var s = App.Settings;
        if (string.IsNullOrEmpty(s.PendingUpdateVersion) || !Version.TryParse(s.PendingUpdateVersion, out var expected)) return;
        var tag = s.PendingUpdateTag ?? "v" + AppVersioning.Display(expected);
        s.PendingUpdateVersion = null;
        s.PendingUpdateTag = null;
        if (UpdateService.IsNewer(expected, Current))
        {
            s.SkippedUpdateTag = tag;
            s.Save();
            Log.Error($"Update to {tag} did not take effect: still running {AppVersioning.Display(Current)}");
            Dialog.Show("The update didn't install",
                $"Money Tweaks tried to update to {tag}, but this copy is still version {AppVersioning.Display(Current)}.\n\n" +
                $"Installer log: {UpdateService.InstallerLogPath}\n\n" +
                "Automatic update prompts for this release are paused. You can install it manually from the releases page, or use Settings & About → Check for updates to try again.",
                DialogKind.Warning);
        }
        else
        {
            if (s.SkippedUpdateTag == tag) s.SkippedUpdateTag = null;
            s.Save();
            Log.Info($"Update verified: now running {AppVersioning.Display(Current)} ({tag})");
            MainWindow.Current?.ShowToast($"Updated to Money Tweaks {AppVersioning.Display(Current)}.");
        }
    }

    public static async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        StatusChanged?.Invoke("Checking for updates…");
        try
        {
            Available = await UpdateService.CheckAsync(Current);
            if (Available is null)
            {
                StatusChanged?.Invoke($"You're up to date (v{AppVersioning.Display(Current)}).");
                if (manual) MainWindow.Current?.ShowToast($"You're on the latest version (v{AppVersioning.Display(Current)}).");
            }
            else if (!manual && Available.Tag == App.Settings.SkippedUpdateTag)
            {
                // A release that previously failed or was mislabeled: don't nag on every start.
                StatusChanged?.Invoke($"{Available.Tag} is available but couldn't be installed last time. Use Check for updates to retry.");
                Log.Info($"Auto-update skipped {Available.Tag} (previously failed or mislabeled)");
            }
            else
            {
                StatusChanged?.Invoke($"Version {AppVersioning.Display(Available.Version)} is available.");
                MainWindow.Current?.ShowUpdateBanner(Available);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            StatusChanged?.Invoke("Couldn't reach GitHub to check for updates.");
            if (manual) Dialog.Show("Update check failed", $"Couldn't reach GitHub: {ex.Message}\n\nCheck your internet connection, or download the latest version from:\n{UpdateService.ReleasesPage}", DialogKind.Warning);
        }
        finally { _busy = false; }
    }

    public static async Task InstallAsync()
    {
        if (_busy || Available is not { } u) return;
        var notes = string.IsNullOrWhiteSpace(u.Notes) ? "" : "\n\nWhat's new:\n" + (u.Notes.Length > 1200 ? u.Notes[..1200] + "…" : u.Notes);
        if (!Dialog.Confirm($"Update to Money Tweaks {AppVersioning.Display(u.Version)}?",
                $"The new version is downloaded from GitHub, its SHA-256 checksum and version are verified, and Money Tweaks restarts. Your applied tweaks and backups are kept.{notes}",
                "Update now")) return;
        _busy = true;
        try
        {
            var progress = new Progress<double>(p => StatusChanged?.Invoke($"Downloading update… {p:P0}"));
            var path = await UpdateService.DownloadAsync(u, Current, progress);
            StatusChanged?.Invoke("Installing update…");
            // Remember what we expect to be running after the restart, so VerifyPendingUpdate can confirm it.
            App.Settings.PendingUpdateTag = u.Tag;
            App.Settings.PendingUpdateVersion = AppVersioning.Display(u.Version);
            App.Settings.Save();
            UpdateService.Install(path);
            Application.Current.Shutdown();
        }
        catch (UpdateMismatchException ex)
        {
            Log.Error("Update refused", ex);
            App.Settings.SkippedUpdateTag = u.Tag;
            App.Settings.Save();
            Available = null;
            MainWindow.Current?.HideUpdateBanner();
            StatusChanged?.Invoke($"{u.Tag} was published with the wrong files and was not installed.");
            Dialog.Show("This update can't be installed", ex.Message + "\n\nYou'll get the next correctly published version automatically.", DialogKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            App.Settings.PendingUpdateTag = null;
            App.Settings.PendingUpdateVersion = null;
            App.Settings.Save();
            StatusChanged?.Invoke("Update failed. See the Activity Log.");
            if (Dialog.Confirm("Update failed", $"{ex.Message}\n\nNothing was changed. Open the download page to update manually?", "Open releases page", DialogKind.Error))
                Process.Start(new ProcessStartInfo(u.HtmlUrl) { UseShellExecute = true });
        }
        finally { _busy = false; }
    }
}
