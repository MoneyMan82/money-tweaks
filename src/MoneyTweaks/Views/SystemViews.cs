using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

public sealed class StartupView : UserControl, IPageLifecycle
{
    private readonly StackPanel _list = new();

    public StartupView()
    {
        var (root, body) = Ui.Page("Startup Apps", "Choose which apps launch when you sign in. This uses the same on/off flags as Task Manager's Startup tab, so nothing is deleted and you can switch apps back on any time.",
            Ui.Row(Ui.Btn("Refresh", (_, _) => Load())));
        body.Children.Add(Ui.Card(_list));
        Content = root;
    }

    private void Load()
    {
        _list.Children.Clear();
        var entries = StartupManager.List();
        if (entries.Count == 0) _list.Children.Add(Ui.Muted("No Run-key startup apps found."));
        foreach (var e in entries)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var sw = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = e.Enabled, VerticalAlignment = VerticalAlignment.Center };
            sw.Click += (_, _) =>
            {
                try { StartupManager.SetEnabled(e, sw.IsChecked == true); }
                catch (UnauthorizedAccessException ex) { sw.IsChecked = e.Enabled; Dialog.Show("Administrator rights needed", ex.Message, DialogKind.Warning); }
                catch (Exception ex) { sw.IsChecked = e.Enabled; Log.Error("Startup toggle", ex); Dialog.Show("Couldn't change startup app", ex.Message, DialogKind.Error); }
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
        var (root, body) = Ui.Page("Backup & Restore", "Every tweak stores the original values before it changes anything. Undo tweaks one by one with their switches, all at once here, or roll back the whole system with a Windows restore point.");

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
        if (ids.Count == 0) _applied.Children.Add(Ui.Muted("Nothing applied yet."));

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
        var items = BackupStore.AllIds().Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
        if (items.Count == 0) { MainWindow.Current?.ShowToast("Nothing to undo."); return; }
        if (!Dialog.Confirm($"Undo all {items.Count} tweaks?", "Every tweak goes back to the value that was backed up before Money Tweaks changed it. Removed Store apps are re-registered where Windows still has their files.", "Undo everything", DialogKind.Warning)) return;
        IsEnabled = false;
        try
        {
            await Task.WhenAll(items.Select(i => i.RefreshAsync()));
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, false);
            Dialog.Show("Undo complete", $"{ok} reverted{(failed > 0 ? $", {failed} could not be reverted automatically (see Activity Log)" : "")}.{(reboot ? "\n\nRestart Windows to finish." : "")}");
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
            Ui.Row(Ui.Btn("Refresh", (_, _) => Load()), Ui.Btn("Open log folder", (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Logs) { UseShellExecute = true }))));
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

public sealed class AboutView : UserControl, IPageLifecycle
{
    public AboutView()
    {
        var (root, body) = Ui.Page("Settings & About", $"Money Tweaks {App.Version}: a free, open-source Windows tweaker. No accounts, no ads, no telemetry, no paid tiers.");

        var st = Ui.Section("Safety settings");
        var rp = new CheckBox { Content = "Create a restore point before the first change in each session (admin)", IsChecked = App.Settings.CreateRestorePointBeforeBatch, Margin = new Thickness(0, 4, 0, 4) };
        rp.Click += (_, _) => { App.Settings.CreateRestorePointBeforeBatch = rp.IsChecked == true; App.Settings.Save(); };
        var cc = new CheckBox { Content = "Ask for confirmation before applying Caution tweaks", IsChecked = App.Settings.ConfirmCautionTweaks, Margin = new Thickness(0, 4, 0, 4) };
        cc.Click += (_, _) => { App.Settings.ConfirmCautionTweaks = cc.IsChecked == true; App.Settings.Save(); };
        st.Children.Add(rp);
        st.Children.Add(cc);
        body.Children.Add(Ui.Card(st));

        var up = Ui.Section("Updates", $"You have Money Tweaks {App.Version}. Updates come from the official GitHub releases and are checksum-verified before installing.");
        var status = Ui.Muted(Updater.Available is { } av ? $"Version {av.Version.ToString(3)} is available." : "", 12.5);
        Updater.StatusChanged += t => Dispatcher.Invoke(() => status.Text = t);
        var install = Ui.Btn("Update now", async (_, _) => await Updater.InstallAsync(), "PrimaryButton");
        install.Visibility = Updater.Available is null ? Visibility.Collapsed : Visibility.Visible;
        Updater.StatusChanged += _ => Dispatcher.Invoke(() => install.Visibility = Updater.Available is null ? Visibility.Collapsed : Visibility.Visible);
        up.Children.Add(Ui.Row(Ui.Btn("Check for updates", async (_, _) => await Updater.CheckAsync(manual: true)), install));
        up.Children.Add(status);
        var auto = new CheckBox { Content = "Check for updates automatically when Money Tweaks starts", IsChecked = App.Settings.AutoCheckUpdates, Margin = new Thickness(0, 8, 0, 0) };
        auto.Click += (_, _) => { App.Settings.AutoCheckUpdates = auto.IsChecked == true; App.Settings.Save(); };
        up.Children.Add(auto);
        body.Children.Add(Ui.Card(up));

        var counts = string.Join("  ·  ", Enum.GetValues<Core.Model.TweakCategory>().Select(c => $"{Core.Model.CategoryInfo.DisplayName(c)} {TweakCatalog.InCategory(c).Count()}"));
        var ab = Ui.Section("About");
        ab.Children.Add(Ui.Muted($"{TweakCatalog.All.Count} tweaks: {counts}", 12.5));
        ab.Children.Add(Ui.Muted($"Data folder: {AppPaths.Root}", 12.5));
        ab.Children.Add(Ui.Muted($"Running on {OsInfo.ProductName}, {(Admin.IsElevated ? "as administrator" : "as standard user")}.", 12.5));
        ab.Children.Add(Ui.Row(
            Ui.Btn("Website", (_, _) => Open("https://money-tweaks.pages.dev/"), margin: new Thickness(0, 10, 8, 0)),
            Ui.Btn("Source code", (_, _) => Open("https://github.com/MoneyMan82/money-tweaks"), margin: new Thickness(0, 10, 8, 0)),
            Ui.Btn("Report an issue", (_, _) => Open("https://github.com/MoneyMan82/money-tweaks/issues"), margin: new Thickness(0, 10, 8, 0))));
        body.Children.Add(Ui.Card(ab));

        var na = Ui.Section("Not included", "These would need things Money Tweaks deliberately avoids (kernel drivers, paid content), so they are not available:");
        na.Children.Add(Ui.Muted("• Controller response curves: need a virtual-controller driver. Use Steam Input instead.\n• Console (PlayStation/Xbox) guides: out of scope for a Windows app.\n• Full temperature/fan sensors: need a kernel driver. Use HWiNFO or LibreHardwareMonitor.\n• FPS counter: use your GPU driver overlay or Xbox Game Bar.", 12.5));
        body.Children.Add(Ui.Card(na));

        var dis = Ui.Section("Disclaimer");
        dis.Children.Add(Ui.Muted("Money Tweaks is an independent project. It is not affiliated with, endorsed by or derived from EMTweaks, Microsoft or any game publisher. Tweaks change Windows settings; while every change is reversible and backed up, you use this software at your own risk under the MIT License. Benefits vary by hardware; no specific FPS gain is promised.", 12.5));
        body.Children.Add(Ui.Card(dis));
        Content = root;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void OnShown() { }
    public void OnHidden() { }
}
