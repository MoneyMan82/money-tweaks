using System.Diagnostics;
using System.Windows;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>UI side of in-app updates: check, confirm, download with progress, install, exit.</summary>
public static class Updater
{
    public static UpdateInfo? Available { get; private set; }
    public static event Action<string>? StatusChanged;
    private static bool _busy;

    public static Version Current => typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);

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
                StatusChanged?.Invoke($"You're up to date (v{App.Version}).");
                if (manual) MainWindow.Current?.ShowToast($"You're on the latest version (v{App.Version}).");
            }
            else
            {
                StatusChanged?.Invoke($"Version {Available.Version.ToString(3)} is available.");
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
        if (!Dialog.Confirm($"Update to Money Tweaks {u.Version.ToString(3)}?",
                $"The new version is downloaded from GitHub, its SHA-256 checksum is verified, and Money Tweaks restarts. Your applied tweaks and backups are kept.{notes}",
                "Update now")) return;
        _busy = true;
        try
        {
            var progress = new Progress<double>(p => StatusChanged?.Invoke($"Downloading update… {p:P0}"));
            var path = await UpdateService.DownloadAsync(u, progress);
            StatusChanged?.Invoke("Installing update…");
            App.Settings.Save();
            UpdateService.Install(path);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            StatusChanged?.Invoke("Update failed. See the Activity Log.");
            if (Dialog.Confirm("Update failed", $"{ex.Message}\n\nNothing was changed. Open the download page to update manually?", "Open releases page", DialogKind.Error))
                Process.Start(new ProcessStartInfo(u.HtmlUrl) { UseShellExecute = true });
        }
        finally { _busy = false; }
    }
}
