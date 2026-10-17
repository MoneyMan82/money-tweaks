using System.IO;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Dashboard cards added by the reorganization: shortcuts, active optimizations, game status, PC health, recent activity
/// and backups, all from real app data. Every card (including the original ones) can be hidden with Customize.
/// </summary>
public sealed partial class DashboardView
{
    private readonly Dictionary<string, FrameworkElement> _cards = [];
    private readonly StackPanel _shortcuts = new(), _active = new(), _games = new(), _health = new(), _activity = new(), _backups = new();
    private readonly WrapPanel _customize = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };

    private static readonly (string id, string label)[] CardIds =
    [
        ("shortcuts", "Shortcuts"), ("live", "Live performance"), ("active", "Active now"), ("games", "Games"), ("health", "PC health"),
        ("kit", "Gaming toolkit"), ("activity", "Recent activity"), ("backups", "Backups"), ("pc", "Your PC & applied tweaks"),
    ];

    /// <summary>Regroups the original sections into named cards and adds the new ones, without changing what they do.</summary>
    private void BuildExtras(StackPanel body)
    {
        // Original order after the hero: live card, live tiles, toolkit header, toolkit grid, PC + categories grid.
        var originals = body.Children.Cast<UIElement>().Skip(1).ToList();
        foreach (var o in originals) body.Children.Remove(o);
        StackPanel Wrap(params UIElement[] parts) { var sp = new StackPanel(); foreach (var p in parts) sp.Children.Add(p); return sp; }
        _cards["live"] = Wrap(originals[0], originals[1]);
        _cards["kit"] = Wrap(originals[2], originals[3]);
        _cards["pc"] = Wrap(originals[4]);

        Section(_shortcuts, "shortcuts");
        Section(_active, "active");
        Section(_games, "games");
        Section(_health, "health");
        Section(_activity, "activity");
        Section(_backups, "backups");

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var custom = Ui.Btn("Customize", (_, _) => _customize.Visibility = _customize.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, "GhostButton", new Thickness(0));
        DockPanel.SetDock(custom, Dock.Right);
        bar.Children.Add(custom);
        bar.Children.Add(Ui.Muted("Your dashboard: every number below is read from this PC or from Money Tweaks' own records.", 12));
        body.Children.Add(bar);
        foreach (var (id, label) in CardIds)
        {
            var key = id;
            var cb = new CheckBox { Content = label, IsChecked = !App.Settings.DashboardHidden.Contains(id), Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 0, 16, 6) };
            cb.Click += (_, _) =>
            {
                App.Settings.DashboardHidden.Remove(key);
                if (cb.IsChecked != true) App.Settings.DashboardHidden.Add(key);
                App.SaveSettingsSoon();
                ApplyCardVisibility();
            };
            _customize.Children.Add(cb);
        }
        body.Children.Add(_customize);

        body.Children.Add(_cards["shortcuts"]);
        body.Children.Add(_cards["live"]);
        var pair1 = (ResponsiveGrid)Ui.Grid(2, _cards["active"], _cards["games"]);
        pair1.MinItemWidth = 360;
        body.Children.Add(pair1);
        var pair2 = (ResponsiveGrid)Ui.Grid(2, _cards["health"], _cards["backups"]);
        pair2.MinItemWidth = 360;
        body.Children.Add(pair2);
        body.Children.Add(_cards["kit"]);
        body.Children.Add(_cards["activity"]);
        body.Children.Add(_cards["pc"]);
        ApplyCardVisibility();
        RenderShortcuts();
    }

    private void Section(StackPanel content, string id) => _cards[id] = Ui.Card(content, new Thickness(0, 0, 14, 14));

    private void ApplyCardVisibility()
    {
        foreach (var (id, el) in _cards) el.Visibility = App.Settings.DashboardHidden.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static StackPanel Head(StackPanel host, string title, string? linkText = null, string? linkTag = null)
    {
        host.Children.Clear();
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (linkText is not null)
        {
            var b = Ui.Btn(linkText + " ›", (_, _) => MainWindow.Current?.Navigate(linkTag!), "GhostButton", new Thickness(0));
            DockPanel.SetDock(b, Dock.Right);
            d.Children.Add(b);
        }
        d.Children.Add(new TextBlock { Text = title, Style = Ui.Res<Style>("H2"), VerticalAlignment = VerticalAlignment.Center });
        host.Children.Add(d);
        return host;
    }

    private static UIElement Line(string label, string value, string tone = "Text")
    {
        var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var v = new TextBlock { Text = value, Foreground = Ui.Brush(tone), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, MaxWidth = 260 };
        DockPanel.SetDock(v, Dock.Right);
        d.Children.Add(v);
        d.Children.Add(Ui.Muted(label, 12.5));
        return d;
    }

    private void RenderShortcuts()
    {
        Head(_shortcuts, "Shortcuts");
        var search = Ui.Btn("Search everything (Ctrl+K) ›", (_, _) => System.Windows.Input.ApplicationCommands.Find.Execute(null, MainWindow.Current), "GhostButton", new Thickness(0));
        DockPanel.SetDock(search, Dock.Right);
        ((DockPanel)_shortcuts.Children[0]).Children.Insert(0, search);
        var favs = App.Settings.FavouritePages.Select(t => AppNavigation.Pages.FirstOrDefault(p => p.Tag == t)).OfType<NavPage>().ToList();
        bool suggested = favs.Count == 0;
        if (suggested) favs = new[] { "scanner", "library", "monitor#health", "restore" }.Select(t => AppNavigation.Pages.First(p => p.Tag == t)).ToList();
        var wrap = new WrapPanel();
        foreach (var p in favs)
        {
            var page = p;
            var b = new Button { Style = Ui.Res<Style>("BaseButton"), Margin = new Thickness(0, 0, 8, 8), ToolTip = p.Description };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = p.Icon, Style = Ui.Res<Style>("Icon"), Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            b.Click += (_, _) => MainWindow.Current?.Navigate(page.Tag);
            wrap.Children.Add(b);
        }
        _shortcuts.Children.Add(wrap);
        _shortcuts.Children.Add(Ui.Muted(suggested ? "Suggestions. Pin your own with the ☆ next to any page's name; they appear here and at the top of the sidebar." : "Your favourites. Pin or unpin with the ☆ next to a page's name.", 11.5));
    }

    /// <summary>Refreshes the new cards (off the UI thread for anything that touches the disk, WMI or processes).</summary>
    private async Task RefreshExtrasAsync()
    {
        RenderShortcuts();
        RenderActive();
        RenderActivity();
        RenderHealth();
        await Task.WhenAll(RenderGamesAsync(), RenderBackupsAsync());
    }

    private void RenderActive()
    {
        Head(_active, "Active now");
        var a = ProfileService.Active;
        _active.Children.Add(Line("Gaming profile", a is null ? "None" : a.ProfileName + (a.TriggeredBy is null ? "" : $" (auto: {a.TriggeredBy})"), a is null ? "Muted" : "Success"));
        _active.Children.Add(Line("Tweaks applied (backed up)", BackupStore.AllIds().Count().ToString(), "Accent"));
        _active.Children.Add(Line("Performance overlay", OverlayService.Visible ? "On" : "Off", OverlayService.Visible ? "Success" : "Muted"));
        _active.Children.Add(Line("Fortnite auto resolution", DisplayService.State.AutoFortnite.Enabled ? $"On ({DisplayService.State.AutoFortnite.Width}×{DisplayService.State.AutoFortnite.Height})" : "Off", DisplayService.State.AutoFortnite.Enabled ? "Success" : "Muted"));
        _active.Children.Add(Line("Start with Windows", AutoStart.IsEnabled ? "On (background)" : "Off", AutoStart.IsEnabled ? "Success" : "Muted"));
        var (_, _, cur) = TimerResolution.Query();
        _active.Children.Add(Line("Kernel timer resolution", $"{cur:0.###} ms"));
        _active.Children.Add(Ui.Row(Ui.Btn("Gaming Profiles ›", (_, _) => MainWindow.Current?.Navigate("profiles"), "GhostButton"), Ui.Btn("All Tweaks ›", (_, _) => MainWindow.Current?.Navigate("tweaks"), "GhostButton")));
    }

    private async Task RenderGamesAsync()
    {
        Head(_games, "Games", "Game Library", "library");
        _games.Children.Add(Ui.Muted("Checking Steam, Epic, Riot, Roblox and Minecraft…", 12));
        try
        {
            var rows = await Task.Run(() => GameRuntime.Statuses(GameLibrary.Items(GameLibrary.Current)));
            Head(_games, "Games", "Game Library", "library");
            var running = rows.Where(r => r.status == GameStatus.Running).ToList();
            var installed = rows.Where(r => r.status == GameStatus.Installed).ToList();
            _games.Children.Add(Line("Running now", running.Count == 0 ? "No game" : string.Join(", ", running.Select(r => r.game.Def.Name)), running.Count == 0 ? "Muted" : "Success"));
            _games.Children.Add(Line("Installed", installed.Count.ToString(), "Accent"));
            _games.Children.Add(Line("Not detected", rows.Count(r => r.status == GameStatus.NotDetected).ToString(), "Muted"));
            var fav = rows.Where(r => r.game.Favourite).Select(r => r.game.Def.Name).ToList();
            if (fav.Count > 0) _games.Children.Add(Line("Favourites", string.Join(", ", fav.Take(4))));
            if (installed.Count > 0) _games.Children.Add(Ui.Muted(string.Join(" · ", installed.Take(8).Select(r => r.game.Def.Name)), 11.5));
        }
        catch (Exception ex) { _games.Children.Add(Ui.Muted("Couldn't check games: " + ex.Message, 12)); }
    }

    private void RenderHealth()
    {
        Head(_health, "PC health", "Health Center", "monitor#health");
        var st = HealthState.Load();
        if (st.LastScan is null || st.Results.Count == 0)
        {
            _health.Children.Add(Ui.Muted("Not scanned yet. The Driver & Windows Health Center checks drivers, Windows Update, crashes, throttling, memory and disks (read-only).", 12));
            _health.Children.Add(Ui.Row(Ui.Btn("Run a health scan ›", (_, _) => MainWindow.Current?.Navigate("monitor#health"), "PrimaryButton")));
            return;
        }
        var overall = HealthState.Overall(st.Results);
        _health.Children.Add(Line("Last scan", st.LastScan.Value.ToString("g"), "Muted"));
        _health.Children.Add(Line("Overall", HealthResult.Label(overall), overall switch { HealthStatus.Problem => "Danger", HealthStatus.Warning => "Gold", HealthStatus.Passed => "Success", _ => "Muted" }));
        _health.Children.Add(Line("Problems / warnings", $"{st.Results.Count(r => r.Status == HealthStatus.Problem)} / {st.Results.Count(r => r.Status == HealthStatus.Warning)}"));
        foreach (var r in st.Results.Where(r => r.Status is HealthStatus.Problem or HealthStatus.Warning).OrderBy(r => r.Status == HealthStatus.Problem ? 0 : 1).Take(3))
            _health.Children.Add(new TextBlock { Text = (r.Status == HealthStatus.Problem ? "● " : "○ ") + r.Title, Foreground = Ui.Brush(r.Status == HealthStatus.Problem ? "Danger" : "Gold"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
    }

    private void RenderActivity()
    {
        Head(_activity, "Recent activity", "Activity Log", "logs");
        var entries = Log.Snapshot().Where(e => e.Level >= LogLevel.Info && !e.Message.StartsWith("[perf]", StringComparison.Ordinal)).TakeLast(6).Reverse().ToList();
        if (entries.Count == 0) { _activity.Children.Add(Ui.Muted("Nothing yet this session.", 12)); return; }
        foreach (var e in entries)
        {
            var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var t = Ui.Muted(e.Time.ToString("HH:mm"), 11.5);
            t.Width = 44;
            DockPanel.SetDock(t, Dock.Left);
            d.Children.Add(t);
            d.Children.Add(new TextBlock { Text = e.Message, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Ui.Brush(e.Level == LogLevel.Error ? "Danger" : e.Level == LogLevel.Warn ? "Gold" : "Text"), ToolTip = e.Message });
            _activity.Children.Add(d);
        }
    }

    private async Task RenderBackupsAsync()
    {
        Head(_backups, "Backups", "Backup & Restore", "restore");
        var tweakBackups = BackupStore.AllIds().Count();
        _backups.Children.Add(Line("Tweak backups", tweakBackups.ToString(), "Accent"));
        var nv = Path.Combine(AppPaths.Root, "nvidia-backups");
        int nip = Directory.Exists(nv) ? Directory.GetFiles(nv, "fortnite-profile-*.nip").Length : 0;
        _backups.Children.Add(Line("NVIDIA profile backups", nip.ToString()));
        _backups.Children.Add(Line("Auto restore point before changes", App.Settings.CreateRestorePointBeforeBatch ? "On" : "Off", App.Settings.CreateRestorePointBeforeBatch ? "Success" : "Muted"));
        _backups.Children.Add(Line("Latest restore point", "reading…", "Muted"));
        try
        {
            var points = await Task.Run(RestorePoints.List);
            _backups.Children.RemoveAt(_backups.Children.Count - 1);
            _backups.Children.Add(points.Count == 0 ? Line("Latest restore point", Admin.IsElevated ? "None" : "Needs admin to read", "Muted")
                : Line("Latest restore point", $"{points.Max(p => p.when):g}"));
        }
        catch { _backups.Children.RemoveAt(_backups.Children.Count - 1); _backups.Children.Add(Line("Latest restore point", "Couldn't read", "Muted")); }
    }
}
