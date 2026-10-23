using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

public sealed class StartupView : UserControl, IPageLifecycle
{
    private readonly StackPanel _list = new();

    public StartupView()
    {
        var (root, body) = Ui.Page("Startup Apps", "Choose which apps launch when you sign in. This uses the same on/off flags as Task Manager's Startup tab, so nothing is deleted and you can switch apps back on any time.",
            Ui.Row(Ui.Btn("Refresh", (_, _) => Load())), eyebrow: "System", icon: "\uE7E8");
        body.Children.Add(Ui.Card(_list));
        Content = root;
    }

    private void Load()
    {
        _list.Children.Clear();
        var entries = StartupManager.List();
        if (entries.Count == 0) _list.Children.Add(Ui.Empty("\uE73E", "Nothing starts with Windows", "No apps are registered to launch at sign-in. Nice and clean."));
        foreach (var e in entries)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var sw = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = e.Enabled, VerticalAlignment = VerticalAlignment.Center };
            sw.Click += (_, _) =>
            {
                try
                {
                    bool want = sw.IsChecked == true;
                    StartupManager.SetEnabled(e, want);
                    // Read it back instead of trusting the write.
                    var now = StartupManager.ReadEnabled(e);
                    sw.IsChecked = now;
                    if (now != want) Dialog.Show("Startup app not changed", $"Windows still reports '{e.Name}' as {(now ? "enabled" : "disabled")}.", DialogKind.Warning);
                }
                catch (UnauthorizedAccessException ex) { try { sw.IsChecked = StartupManager.ReadEnabled(e); } catch { sw.IsChecked = e.Enabled; }Dialog.Show("Administrator rights needed", ex.Message, DialogKind.Warning); }
                catch (Exception ex) { try { sw.IsChecked = StartupManager.ReadEnabled(e); } catch { sw.IsChecked = e.Enabled; }Log.Error("Startup toggle", ex); Dialog.Show("Couldn't change startup app", ex.Message, DialogKind.Error); }
            };
            DockPanel.SetDock(sw, Dock.Right);
            row.Children.Add(sw);
            var info = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var name = new WrapPanel();
            name.Children.Add(Ui.Text(e.Name, 13.5, FontWeights.SemiBold));
            name.Children.Add(new Border { Width = 8 });
            name.Children.Add(Ui.Pill(e.Location));
            info.Children.Add(name);
            var cmd = Ui.Muted(e.Command, 11.5);
            cmd.TextTrimming = TextTrimming.CharacterEllipsis;
            cmd.TextWrapping = TextWrapping.NoWrap;
            cmd.ToolTip = e.Command;
            info.Children.Add(cmd);
            row.Children.Add(info);
            _list.Children.Add(row);
        }
    }

    public void OnShown() => Load();
    public void OnHidden() { }
}

public sealed class RestoreView : UserControl, IPageLifecycle
{
    private readonly StackPanel _applied = new();
    private readonly StackPanel _points = new();

    public RestoreView()
    {
        var (root, body) = Ui.Page("Backup & Restore", "Every tweak stores the original values before it changes anything. Undo tweaks one by one with their switches, all at once here, or roll back the whole system with a Windows restore point.", eyebrow: "Safety", icon: "\uE777");

        var rp = Ui.Section("System restore points", "Money Tweaks creates one automatically before your first change each session (Settings). Windows allows one new restore point per 24 hours by default.");
        rp.Children.Add(Ui.Row(
            Ui.Btn("Create restore point now", CreatePoint, "PrimaryButton"),
            Ui.Btn("Open System Restore", (_, _) => Launch("rstrui.exe")),
            Ui.Btn("System Protection settings", (_, _) => Launch("SystemPropertiesProtection.exe"))));
        rp.Children.Add(new Border { Height = 8 });
        rp.Children.Add(_points);
        body.Children.Add(Ui.Card(rp));

        var ap = Ui.Section("Applied tweaks", "Tweaks that currently have a Money Tweaks backup.");
        ap.Children.Add(Ui.Row(Ui.Btn("Undo everything", UndoAll, "DangerButton"), Ui.Btn("Open backup folder", (_, _) => Launch("explorer.exe", AppPaths.Backups))));
        ap.Children.Add(new Border { Height = 8 });
        ap.Children.Add(_applied);
        body.Children.Add(Ui.Card(ap));
        Content = root;
    }

    private static void Launch(string exe, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Show("Couldn't open", ex.Message, DialogKind.Error); }
    }

    private async void Reload()
    {
        _applied.Children.Clear();
        var ids = BackupStore.AllIds().ToList();
        foreach (var id in ids)
        {
            var t = TweakCatalog.Find(id);
            var b = BackupStore.Load(id);
            _applied.Children.Add(Ui.Muted($"• {t?.Name ?? id} — applied {b?.AppliedAt:g}", 12.5));
        }
        if (ids.Count == 0) _applied.Children.Add(Ui.Empty("\uE8A5", "No tweaks applied yet", "When you apply a tweak, its backup shows up here so you can undo it any time."));

        _points.Children.Clear();
        var pts = await Task.Run(RestorePoints.List);
        if (pts.Count == 0) _points.Children.Add(Ui.Muted(Admin.IsElevated ? "No restore points found, or System Protection is off." : "Run as administrator to list restore points."));
        foreach (var (when, desc) in pts.Take(8)) _points.Children.Add(Ui.Muted($"• {when:g}  {desc}", 12.5));
    }

    private async void CreatePoint(object s, RoutedEventArgs e)
    {
        if (!Admin.IsElevated) { TweakService.EnsureAdmin(TweakCatalog.All.Where(t => t.RequiresAdmin).Take(1)); return; }
        IsEnabled = false;
        var (ok, msg) = await Task.Run(() => RestorePoints.Create("Money Tweaks manual restore point"));
        IsEnabled = true;
        Dialog.Show(ok ? "Restore point created" : "Restore point not created", msg, ok ? DialogKind.Info : DialogKind.Warning);
        Reload();
    }

    private async void UndoAll(object s, RoutedEventArgs e)
    {
        var ids = BackupStore.AllIds().ToList();
        var orphans = ids.Where(id => TweakCatalog.Find(id) is null).ToList();
        var items = ids.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
        if (items.Count == 0 && orphans.Count == 0) { MainWindow.Current?.ShowToast("Nothing to undo."); return; }
        if (!Dialog.Confirm($"Undo all {items.Count} tweaks?", "Every tweak goes back to the value that was backed up before Money Tweaks changed it. Removed Store apps are re-registered where Windows still has their files.", "Undo everything", DialogKind.Warning)) return;
        IsEnabled = false;
        try
        {
            await TweakService.RefreshStatesAsync(items);
            var guides = items.Where(i => i.IsGuide).ToList();
            var targets = items.Where(i => !i.IsGuide && i.CanToggle && i.State is TweakState.Applied or TweakState.Partial).ToList();
            var notOn = items.Except(guides).Except(targets).ToList();
            var (ok, failed, reboot) = await TweakService.BatchAsync(targets, false);
            if (targets.Count > 0 && ok == 0 && failed == targets.Count && !Admin.IsElevated && targets.Any(t => t.Tweak.RequiresAdmin))
            {
                Dialog.Show("Nothing was undone", "Undoing these tweaks needs administrator rights and the restart as administrator was declined. Nothing was changed.", DialogKind.Warning);
                return;
            }
            // Say what wasn't touched and why, instead of silently leaving those backups in the list.
            var notes = new List<string>();
            if (notOn.Count > 0) notes.Add($"{notOn.Count} already read as not applied (Windows or another tool changed them back), so they were left alone: {string.Join(", ", notOn.Take(5).Select(i => i.Name))}{(notOn.Count > 5 ? "…" : "")}. Undo them one by one if you want their backed-up values written back.");
            if (guides.Count > 0) notes.Add($"{guides.Count} BIOS guide(s) stay marked as done; firmware settings can only be changed in the BIOS.");
            if (orphans.Count > 0) notes.Add($"{orphans.Count} backup(s) belong to tweaks this version no longer has ({string.Join(", ", orphans.Take(5))}); they're kept in the backup folder untouched.");
            Dialog.Show(failed > 0 ? "Undo finished with problems" : "Undo complete",
                $"{ok} reverted{(failed > 0 ? $", {failed} could not be reverted automatically (see each tweak's message and the Activity Log)" : "")}." +
                (notes.Count > 0 ? "\n\n" + string.Join("\n\n", notes) : "") + (reboot ? "\n\nRestart Windows to finish." : ""),
                failed > 0 ? DialogKind.Warning : DialogKind.Info);
        }
        finally { IsEnabled = true; Reload(); }
    }

    public void OnShown() => Reload();
    public void OnHidden() { }
}

public sealed class LogView : UserControl, IPageLifecycle
{
    private readonly TextBox _box = new() { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 520, AcceptsReturn = true };

    public LogView()
    {
        var (root, body) = Ui.Page("Activity Log", "Everything Money Tweaks changes, every failure and every undo is written here and to a daily log file.",
            Ui.Row(Ui.Btn("Refresh", (_, _) => Load()), Ui.Btn("Open log folder", (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Logs) { UseShellExecute = true }))), eyebrow: "System", icon: "\uE81C");
        body.Children.Add(_box);
        Content = root;
        Log.Written += _ => Dispatcher.InvokeAsync(() => { if (IsVisible) Load(); });
    }

    private void Load()
    {
        _box.Text = string.Join(Environment.NewLine, Log.Snapshot().Where(e => e.Level != LogLevel.Debug).Select(e => e.ToString()));
        _box.ScrollToEnd();
    }

    public void OnShown() => Load();
    public void OnHidden() { }
}

public sealed class AboutView : UserControl, IPageLifecycle, IAnchorTarget
{
    private readonly Dictionary<string, FrameworkElement> _sections = [];

    /// <summary>"about#appearance", "about#legacy", "about#hotkeys", "about#updates".</summary>
    public void ShowSection(string anchor)
    {
        if (_sections.TryGetValue(anchor, out var el)) Ui.ScrollTo(el);
    }

    public AboutView()
    {
        var (root, body) = Ui.Page("Settings & About", $"Money Tweaks {App.Version}: a free, open-source Windows tweaker. No accounts, no ads, no telemetry, no paid tiers.", eyebrow: "Money Tweaks", icon: "\uE713");

        var st = Ui.Section("Safety settings");
        var rp = new CheckBox { Content = "Create a restore point before the first change in each session (admin)", IsChecked = App.Settings.CreateRestorePointBeforeBatch, Margin = new Thickness(0, 4, 0, 4) };
        rp.Click += (_, _) => { App.Settings.CreateRestorePointBeforeBatch = rp.IsChecked == true; App.Settings.Save(); };
        var cc = new CheckBox { Content = "Ask for confirmation before applying Caution tweaks", IsChecked = App.Settings.ConfirmCautionTweaks, Margin = new Thickness(0, 4, 0, 4) };
        cc.Click += (_, _) => { App.Settings.ConfirmCautionTweaks = cc.IsChecked == true; App.Settings.Save(); };
        st.Children.Add(rp);
        st.Children.Add(cc);
        body.Children.Add(Ui.Card(st));

        var ev = Ui.Section("Result sharing (Tweak Truth Index)",
            "Your measured tweak results always stay on this PC and appear in each tweak's details. You can choose to share them with the Community Hub so other players can see how tweaks behave on similar PCs.");
        var share = new CheckBox { Content = "Allow sharing my measured results with the Community Hub", IsChecked = App.Settings.ShareEvidence, Margin = new Thickness(0, 4, 0, 4) };
        share.Click += (_, _) =>
        {
            App.Settings.ShareEvidence = share.IsChecked == true;
            App.Settings.Save();
            MainWindow.Current?.ShowToast(App.Settings.ShareEvidence ? "Sharing allowed. Nothing is sent until you press Share on a tweak and confirm." : "Sharing is off. Your results stay on this PC.");
        };
        ev.Children.Add(share);
        ev.Children.Add(Ui.Muted("Off by default. Even when on, nothing is sent automatically: you press Share in a tweak's details and see exactly what will be sent first. " +
            "A shared result contains only: the tweak, how it was tested, the measured changes (percent and verdict per metric, number of runs), your CPU and graphics card model names, " +
            "RAM size rounded (e.g. 32 GB), the Windows build number and the Money Tweaks version. Never your account, PC name, user name, serial numbers, file paths, IP-derived data or times. " +
            "Sharing needs a Community Hub sign-in only to limit how much one account can send per day; the account isn't stored with the results.", 11.5));
        var evCard = Ui.Card(ev);
        _sections["evidence"] = evCard;
        body.Children.Add(evCard);

        var ap = Ui.Section("Appearance");
        ap.Children.Add(new TextBlock { Text = "Motion & effects", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        ap.Children.Add(Ui.Muted("Auto follows Windows (Settings › Accessibility › Visual effects › Animation effects) and your GPU. Reduced turns off page transitions, the ambient glow animation and the 3D preview's auto-rotate.", 12));
        var motion = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, value) in new (string, bool?)[] { ("Auto", null), ("Full", false), ("Reduced", true) })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "motion", IsChecked = App.Settings.ReduceMotion == value };
            rb.Checked += (_, _) =>
            {
                App.Settings.ReduceMotion = value;
                App.Settings.Save();
                MainWindow.Current?.StartAmbient();
                MainWindow.Current?.ShowToast(App.ReduceMotion ? "Reduced motion is on." : "Full motion is on.");
            };
            motion.Children.Add(rb);
        }
        ap.Children.Add(motion);
        ap.Children.Add(new TextBlock { Text = "Sidebar", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2) });
        ap.Children.Add(Ui.Muted("Compact shows icons only (names appear as tooltips). Groups can also be collapsed one by one by clicking their headings, and pages can be pinned with the ☆ next to the page name.", 12));
        var compact = new CheckBox { Content = "Compact sidebar (icons only)", IsChecked = App.Settings.SidebarCompact, Margin = new Thickness(0, 8, 0, 0) };
        compact.Click += (_, _) => { if (App.Settings.SidebarCompact != (compact.IsChecked == true)) MainWindow.Current?.ToggleSidebarFromSettings(); };
        ap.Children.Add(compact);
        ap.Children.Add(new TextBlock { Text = "Dashboard", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2) });
        ap.Children.Add(Ui.Muted("Choose which dashboard cards are shown with Customize on the dashboard.", 12));
        ap.Children.Add(Ui.Row(Ui.Btn("Customize dashboard ›", (_, _) => MainWindow.Current?.Navigate("dashboard"), "GhostButton", new Thickness(0, 6, 8, 0))));
        var legacyHead = new TextBlock { Text = "Legacy UI", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2) };
        ap.Children.Add(legacyHead);
        ap.Children.Add(Ui.Muted("The original Money Tweaks look from versions 1.0–1.3: green accents, flat cards and no ambient glow. Every page and feature stays the same; only the look changes. Takes effect after a restart.", 12));
        var legacy = new CheckBox { Content = "Use the legacy UI", IsChecked = App.Settings.LegacyUi, Margin = new Thickness(0, 8, 0, 0) };
        var legacyRestart = Ui.Btn("Restart now to apply", (_, _) => LegacyTheme.Restart(), "PrimaryButton", new Thickness(0, 8, 8, 0));
        void SyncLegacy() => legacyRestart.Visibility = (App.Settings.LegacyUi != LegacyTheme.Active) ? Visibility.Visible : Visibility.Collapsed;
        legacy.Click += (_, _) =>
        {
            App.Settings.LegacyUi = legacy.IsChecked == true;
            App.Settings.Save();
            SyncLegacy();
            MainWindow.Current?.ShowToast(App.Settings.LegacyUi == LegacyTheme.Active ? "That's the look you have now." : $"The {(App.Settings.LegacyUi ? "legacy" : "current")} look is used after a restart.");
        };
        SyncLegacy();
        ap.Children.Add(legacy);
        ap.Children.Add(Ui.Row(legacyRestart));
        _sections["legacy"] = legacyHead;
        var apCard = Ui.Card(ap);
        _sections["appearance"] = apCard;
        body.Children.Add(apCard);

        var hk = Ui.Section("Hotkeys", "Global hotkeys work while a game has focus; the search shortcut works inside Money Tweaks.");
        // One row per shortcut: keycaps in a shared-width column (so descriptions line up), description, optional link.
        Grid.SetIsSharedSizeScope(hk, true);
        void Key(string keys, string what, string? link = null, string? linkTag = null)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "keys" });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caps = Ui.KeyCaps(keys);
            caps.Margin = new Thickness(0, 0, 16, 0);
            row.Children.Add(caps);
            var text = new TextBlock { Text = what, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (link is not null)
            {
                var b = Ui.Btn(link + " ›", (_, _) => MainWindow.Current?.Navigate(linkTag!), "GhostButton", new Thickness(8, 0, 0, 0));
                b.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(b, 2);
                row.Children.Add(b);
            }
            hk.Children.Add(row);
        }
        Key("Ctrl+K", "Search pages, tweaks, games and settings");
        Key(MacroService.EmergencyHotkeyText, "Emergency stop: releases every macro key and stops the synthetic benchmark", "Gaming Macros", "macros");
        // The keys are already on the left; only add the status when the hotkey isn't working.
        Key(App.Settings.Overlay.Hotkey, "Show/hide the Performance Overlay (works while a game has focus)" + (OverlayService.HotkeyRegistered ? "" : ". " + OverlayService.HotkeyStatus), "Change", "overlay");
        var hkCard = Ui.Card(hk);
        _sections["hotkeys"] = hkCard;
        body.Children.Add(hkCard);

        var up = Ui.Section("Updates", $"You have Money Tweaks {App.Version}. Updates come from the official GitHub releases and are checksum-verified before installing.");
        var status = Ui.Muted(Updater.Available is { } av ? $"Version {AppVersioning.Display(av.Version)} is available." : "", 12.5);
        Updater.StatusChanged += t => Dispatcher.Invoke(() => status.Text = t);
        var install = Ui.Btn("Update now", async (_, _) => await Updater.InstallAsync(), "PrimaryButton");
        install.Visibility = Updater.Available is null ? Visibility.Collapsed : Visibility.Visible;
        Updater.StatusChanged += _ => Dispatcher.Invoke(() => install.Visibility = Updater.Available is null ? Visibility.Collapsed : Visibility.Visible);
        up.Children.Add(Ui.Row(Ui.Btn("Check for updates", async (_, _) => await Updater.CheckAsync(manual: true)), install));
        up.Children.Add(status);
        var auto = new CheckBox { Content = "Check for updates automatically when Money Tweaks starts", IsChecked = App.Settings.AutoCheckUpdates, Margin = new Thickness(0, 8, 0, 0) };
        auto.Click += (_, _) => { App.Settings.AutoCheckUpdates = auto.IsChecked == true; App.Settings.Save(); };
        up.Children.Add(auto);
        var upCard = Ui.Card(up);
        _sections["updates"] = upCard;
        body.Children.Add(upCard);

        var sw = Ui.Section("Start with Windows", "Starts Money Tweaks as a background process after you sign in: no window opens, just a tray icon. Gaming Profiles auto-switch, the Fortnite resolution switch and the macro emergency stop keep working. Nothing is built until you open it from the tray, and the update check waits 2 minutes, so sign-in isn't slowed down.");
        var startBox = new CheckBox { Content = "Start Money Tweaks with Windows (background, tray icon)", IsChecked = AutoStart.IsEnabled, Margin = new Thickness(0, 4, 0, 0) };
        startBox.Click += (_, _) =>
        {
            try
            {
                if (startBox.IsChecked == true && Environment.ProcessPath is { } exe) AutoStart.Enable(exe); else AutoStart.Disable();
                MainWindow.Current?.ShowToast(startBox.IsChecked == true ? "Money Tweaks will start in the background with Windows." : "Money Tweaks won't start with Windows.", "Success");
            }
            catch (Exception ex) { startBox.IsChecked = AutoStart.IsEnabled; Dialog.Show("Couldn't change the startup setting", ex.Message, DialogKind.Error); }
        };
        sw.Children.Add(startBox);
        sw.Children.Add(Ui.Row(Ui.Btn("Run in the background now", (_, _) => MainWindow.Current?.SendToBackground(), margin: new Thickness(0, 8, 8, 4))));
        sw.Children.Add(Ui.Muted("Uses your user account's Run entry (no administrator rights, no service). When started this way, closing the window hides it to the tray; use the tray icon's Exit to stop it. You can also turn startup off in Task Manager → Startup apps.", 11.5));
        body.Children.Add(Ui.Card(sw));

        var counts = string.Join("  ·  ", Enum.GetValues<Core.Model.TweakCategory>().Select(c => $"{Core.Model.CategoryInfo.DisplayName(c)} {TweakCatalog.InCategory(c).Count()}"));
        var ab = Ui.Section("About");
        ab.Children.Add(Ui.Muted($"{TweakCatalog.All.Count} tweaks: {counts}", 12.5));
        ab.Children.Add(Ui.Muted($"Data folder: {AppPaths.Root}", 12.5));
        ab.Children.Add(Ui.Muted($"Running on {OsInfo.ProductName}, {(Admin.IsElevated ? "as administrator" : "as standard user")}.", 12.5));
        ab.Children.Add(Ui.Row(
            Ui.Btn("Website", (_, _) => Open("https://money-tweaks.barkgdd.workers.dev/"), margin: new Thickness(0, 10, 8, 0)),
            Ui.Btn("Source code", (_, _) => Open("https://github.com/MoneyMan82/money-tweaks"), margin: new Thickness(0, 10, 8, 0)),
            Ui.Btn("Report an issue", (_, _) => Open("https://github.com/MoneyMan82/money-tweaks/issues"), margin: new Thickness(0, 10, 8, 0))));
        body.Children.Add(Ui.Card(ab));

        var lic = Ui.Section("Open-source licenses", "Money Tweaks is MIT-licensed. It includes LibreHardwareMonitorLib and its dependencies DiskInfoToolkit and RAMSPDToolkit (Mozilla Public License 2.0), HidSharp (Apache License 2.0) and .NET libraries (MIT), used unmodified. Their source code is available from their projects.");
        var notices = new TextBox { Text = ThirdPartyNotices(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 220, FontFamily = Ui.Res<System.Windows.Media.FontFamily>("MonoFont"), FontSize = 11.5, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        lic.Children.Add(Ui.Row(
            Ui.Btn("Show license notices", (s, _) => { notices.Visibility = notices.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; ((Button)s).Content = notices.Visibility == Visibility.Visible ? "Hide license notices" : "Show license notices"; }, margin: new Thickness(0, 10, 8, 0)),
            Ui.Btn("LibreHardwareMonitor source", (_, _) => Open("https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"), margin: new Thickness(0, 10, 8, 0))));
        lic.Children.Add(notices);
        body.Children.Add(Ui.Card(lic));

        var na = Ui.Section("Not included", "These would need things Money Tweaks deliberately avoids (kernel drivers, paid content), so they are not available:");
        na.Children.Add(Ui.Muted("• Controller response curves: need a virtual-controller driver. Use Steam Input instead.\n• Console (PlayStation/Xbox) guides: out of scope for a Windows app.\n• Installing sensor drivers: CPU and motherboard temperatures, power and fans are read through LibreHardwareMonitor only when its PawnIO driver is already installed and Money Tweaks runs as administrator; Money Tweaks never installs a driver itself.\n• Fan control: sensors are read-only.", 12.5));
        body.Children.Add(Ui.Card(na));

        var dis = Ui.Section("Disclaimer");
        dis.Children.Add(Ui.Muted("Money Tweaks is an independent project. It is not affiliated with, endorsed by or derived from Microsoft or any game publisher. Tweaks change Windows settings; while every change is reversible and backed up, you use this software at your own risk under the MIT License. Benefits vary by hardware; no specific FPS gain is promised.", 12.5));
        body.Children.Add(Ui.Card(dis));
        Content = root;
    }

    /// <summary>THIRD-PARTY-NOTICES.md, embedded in the executable.</summary>
    private static string ThirdPartyNotices()
    {
        try
        {
            using var s = typeof(App).Assembly.GetManifestResourceStream("MoneyTweaks.THIRD-PARTY-NOTICES.md");
            return s is null ? "Notices not found." : new System.IO.StreamReader(s).ReadToEnd();
        }
        catch (Exception ex) { return "Couldn't read the notices: " + ex.Message; }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Show("Couldn't open your browser", $"{ex.Message}\n\nThe address is: {url}", DialogKind.Error); }
    }

    public void OnShown() { }
    public void OnHidden() { }
}
