using System.Windows;
using WinForms = System.Windows.Forms;

namespace MoneyTweaks;

/// <summary>
/// Notification-area icon for background mode: Open / Exit, and notifications while no window is shown.
/// Created only when Money Tweaks runs in the background, so a normal start doesn't load Windows Forms.
/// </summary>
public static class TrayIcon
{
    private static WinForms.NotifyIcon? _icon;
    public static bool Exists => _icon is not null;

    public static void Create()
    {
        if (_icon is not null) return;
        System.Drawing.Icon? ico = null;
        try
        {
            using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MoneyTweaks.ico"))?.Stream;
            if (s is not null) ico = new System.Drawing.Icon(s, new System.Drawing.Size(16, 16));
        }
        catch { /* fall back to the default icon */ }
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Money Tweaks", null, (_, _) => Open());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit (stops background features)", null, (_, _) => Exit());
        _icon = new WinForms.NotifyIcon
        {
            Icon = ico ?? System.Drawing.SystemIcons.Application,
            Text = "Money Tweaks: running in the background",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Open();
        _icon.BalloonTipClicked += (_, _) => Open();
    }

    private static void Open() => Application.Current?.Dispatcher.BeginInvoke(() => (Application.Current.MainWindow as MainWindow)?.BringToFront());

    private static void Exit() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        App.ExitRequested = true;
        Application.Current.Shutdown();
    });

    /// <summary>Windows notification from the tray icon (used while the window is hidden).</summary>
    public static void Notify(string text, string tone)
    {
        if (_icon is null) return;
        var kind = tone switch { "Danger" => WinForms.ToolTipIcon.Error, "Gold" => WinForms.ToolTipIcon.Warning, _ => WinForms.ToolTipIcon.Info };
        _icon.ShowBalloonTip(5000, "Money Tweaks", text.Length > 240 ? text[..240] + "…" : text, kind);
    }

    public static void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
