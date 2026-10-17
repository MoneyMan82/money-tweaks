using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;
using MoneyTweaks.Views;

namespace MoneyTweaks;

/// <summary>A search result. Keywords widen matching; Tag links pages to favourites/recents.</summary>
public sealed record PaletteItem(string Title, string Subtitle, string Icon, string Kind, Action Open, string Keywords = "", string? Tag = null);

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    public static MainWindow? Current { get; private set; }
    private Storyboard? _ambient;
    private List<PaletteItem>? _paletteAll;

    /// <summary>Category icons, from the app's navigation model (one source for sidebar, search and pages).</summary>
    internal static readonly Dictionary<TweakCategory, string> CategoryIcons = Enum.GetValues<TweakCategory>().ToDictionary(c => c, AppNavigation.CategoryIcon);

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        VersionText.Text = $"v{App.Version} · free & open source";
        BuildNav();

        bool admin = Admin.IsElevated;
        AdminBanner.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        AdminText.Text = admin ? "Administrator" : "Standard user";
        AdminDot.Fill = AdminHalo.Fill = (Brush)FindResource(admin ? "Success" : "Gold");
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        TweakService.Changed += () => _ = RefreshAppliedSummaryAsync();
        // Started with Windows: no page is built until the window is first opened (see EnsureOpened).
        if (!App.BackgroundStart) Navigate("dashboard");
        Loaded += async (_, _) => await StartupTasksAsync();
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
    }

    private bool _startupDone, _opened;

    /// <summary>Runs once: on Loaded for a normal start, or right away (window hidden) for a background start.</summary>
    public async Task StartupTasksAsync()
    {
        if (_startupDone) return;
        _startupDone = true;
        {
            Log.Info($"[perf] startup to {(App.BackgroundStart ? "background ready" : "first frame")}: {App.StartupClock.ElapsedMilliseconds} ms");
            StartStallDetector();
            MacroService.Init(this);
            OverlayService.Init(this); // overlay hotkey + optional game watcher (also in background mode)
            if (!App.BackgroundStart) StartAmbient();
            Activated += (_, _) => _ambient?.Resume(Ambient);
            Deactivated += (_, _) => _ambient?.Pause(Ambient);
            await RefreshAppliedSummaryAsync();
            if (!App.ScreenshotMode) Updater.VerifyPendingUpdate();
            if (!App.ScreenshotMode) await BenchmarkView.CheckPendingSyntheticRestoreAsync();
            if (!App.ScreenshotMode) await ProfileService.CheckPendingAsync();
            if (!App.ScreenshotMode) DisplayService.CheckPending();
            if (!App.ScreenshotMode) DisplayService.StartAutoSwitch();
            if (App.Settings.AutoCheckUpdates && !App.ScreenshotMode)
            {
                if (App.BackgroundStart) await Task.Delay(TimeSpan.FromMinutes(2)); // don't compete with sign-in
                await Updater.CheckAsync(manual: false);
            }
        }
    }

    /// <summary>Slow "breathing" of the cached ambient layer (opacity only). Off with reduced motion.</summary>
    public void StartAmbient()
    {
        _ambient?.Stop(Ambient);
        _ambient = null;
        Ambient.Opacity = 1;
        if (App.ReduceMotion) return;
        var a = new DoubleAnimation(1, 0.6, TimeSpan.FromSeconds(7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        Storyboard.SetTargetProperty(a, new PropertyPath(OpacityProperty));
        _ambient = new Storyboard();
        _ambient.Children.Add(a);
        Timeline.SetDesiredFrameRate(_ambient, 20); // a slow fade doesn't need 60 fps
        _ambient.Begin(Ambient, true);
    }

    /// <summary>Logs whenever the UI thread is blocked long enough to be felt (heartbeat every 100 ms).</summary>
    private void StartStallDetector()
    {
        var last = System.Diagnostics.Stopwatch.GetTimestamp();
        var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        t.Tick += (_, _) =>
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var gap = System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            last = now;
            if (gap > 350 && IsActive && !_capturing) Log.Info($"[perf] UI thread stalled {gap - 200:0} ms (page: {CrumbText.Text})");
            _capturing = false;
        };
        t.Start();
    }

    // ═════ Sidebar ═════
    private static AppSettings S => App.Settings;
    private bool _syncingNav;
    private string _current = "dashboard";
    private readonly StackPanel _favPanel = new(), _recentPanel = new();

    /// <summary>Builds the sidebar from AppNavigation: favourites, recent pages, then the collapsible groups.</summary>
    private void BuildNav()
    {
        bool compact = S.SidebarCompact;
        Nav.Children.Clear();
        var home = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var p in AppNavigation.Pages.Where(p => p.Group == "dashboard" && p.InSidebar)) home.Children.Add(NavItem(p.Tag, p.Icon, p.Label, null, compact));
        Nav.Children.Add(home);
        Nav.Children.Add(_favPanel);
        Nav.Children.Add(_recentPanel);
        RefreshPins();
        foreach (var g in AppNavigation.Groups.Where(g => g.Id != "dashboard"))
        {
            var pages = AppNavigation.Pages.Where(p => p.Group == g.Id && p.InSidebar).ToList();
            if (pages.Count == 0) continue;
            bool collapsed = !compact && S.CollapsedNavGroups.Contains(g.Id);
            var items = new StackPanel { Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
            foreach (var p in pages) items.Children.Add(NavItem(p.Tag, p.Icon, p.Label, CountFor(p), compact));
            Nav.Children.Add(GroupHeader(g, items, collapsed, compact));
            Nav.Children.Add(items);
        }
        ApplyCompactChrome(compact);
        SyncNav(_current);
    }

    private static string? CountFor(NavPage p) =>
        p.Tag.StartsWith("cat:", StringComparison.Ordinal) ? TweakCatalog.InCategory(Enum.Parse<TweakCategory>(p.Tag[4..])).Count().ToString()
        : p.Tag == "tweaks" ? TweakCatalog.All.Count.ToString() : null;

    private UIElement GroupHeader(NavGroup g, StackPanel items, bool collapsed, bool compact)
    {
        if (compact) return new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(12, 10, 12, 8), ToolTip = g.Label };
        var chevron = new TextBlock { Text = collapsed ? "\uE76C" : "\uE70D", Style = (Style)FindResource("Icon"), FontSize = 9, Foreground = (Brush)FindResource("Subtle"), VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(12, 14, 10, 6), Cursor = Cursors.Hand, ToolTip = collapsed ? $"Show {g.Label}" : $"Hide {g.Label}" };
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        var label = new TextBlock { Text = g.Label.ToUpperInvariant(), Foreground = (Brush)FindResource("Subtle"), FontSize = 10.5, FontWeight = FontWeights.Bold };
        row.Children.Add(label);
        row.MouseEnter += (_, _) => label.Foreground = (Brush)FindResource("Muted");
        row.MouseLeave += (_, _) => label.Foreground = (Brush)FindResource("Subtle");
        row.MouseLeftButtonUp += (_, _) =>
        {
            bool nowCollapsed = items.Visibility == Visibility.Visible;
            items.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = nowCollapsed ? "\uE76C" : "\uE70D";
            row.ToolTip = nowCollapsed ? $"Show {g.Label}" : $"Hide {g.Label}";
            S.CollapsedNavGroups.Remove(g.Id);
            if (nowCollapsed) S.CollapsedNavGroups.Add(g.Id);
            App.SaveSettingsSoon();
            Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
        };
        return row;
    }

    /// <summary>Favourites (pinned) and recently opened pages at the top of the sidebar.</summary>
    private void RefreshPins()
    {
        bool compact = S.SidebarCompact;
        FillPins(_favPanel, "FAVOURITES", S.FavouritePages, compact);
        FillPins(_recentPanel, "RECENT", compact ? [] : S.RecentPages.Where(t => t != _current && !S.FavouritePages.Contains(t)).Take(4), compact);
    }

    private void FillPins(StackPanel panel, string title, IEnumerable<string> tags, bool compact)
    {
        panel.Children.Clear();
        var pages = tags.Select(t => AppNavigation.Pages.FirstOrDefault(p => p.Tag == t)).OfType<NavPage>().ToList();
        if (pages.Count == 0) return;
        panel.Children.Add(compact
            ? new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(12, 6, 12, 8), ToolTip = title }
            : new TextBlock { Text = title, Foreground = (Brush)FindResource("Subtle"), FontSize = 10.5, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 8, 0, 6) });
        foreach (var p in pages) panel.Children.Add(NavItem(p.Tag, p.Icon, p.Label, null, compact, pin: true));
    }

    private void ApplyCompactChrome(bool compact)
    {
        SideCol.Width = new GridLength(compact ? 72 : 256);
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StatusCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DockPanel.SetDock(SidebarToggle, compact ? Dock.Bottom : Dock.Right);
        SidebarToggle.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        BrandBar.Margin = compact ? new Thickness(17, 16, 17, 12) : new Thickness(15, 16, 10, 12);
        SidebarToggleIcon.Text = compact ? "\uE76C" : "\uE76B";
        SidebarToggle.ToolTip = compact ? "Expand the sidebar" : "Collapse the sidebar to icons";
    }

    public void ToggleSidebarFromSettings() => ToggleSidebar(this, new RoutedEventArgs());

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        S.SidebarCompact = !S.SidebarCompact;
        App.SaveSettingsSoon();
        BuildNav();
    }

    private RadioButton NavItem(string tag, string icon, string label, string? count, bool compact, bool pin = false)
    {
        var fg = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
        var panel = new DockPanel();
        if (count is not null && !compact)
        {
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = count, FontSize = 10.5, Foreground = (Brush)FindResource("Muted") } };
            DockPanel.SetDock(badge, Dock.Right);
            panel.Children.Add(badge);
        }
        var ic = new TextBlock { Text = icon, Style = (Style)FindResource("Icon"), FontSize = 15, Margin = new Thickness(compact ? 4 : 0, 0, compact ? 0 : 12, 0) };
        ic.SetBinding(TextBlock.ForegroundProperty, fg);
        panel.Children.Add(ic);
        if (!compact)
        {
            var tx = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            tx.SetBinding(TextBlock.ForegroundProperty, fg);
            panel.Children.Add(tx);
        }
        var rb = new RadioButton { Style = (Style)FindResource("NavButton"), Tag = tag, Content = panel, GroupName = pin ? "pin" + tag + label : "nav", ToolTip = compact ? label : null };
        if (pin)
        {
            // Shortcut entries open the page; the selection highlight stays on the page's own entry in its group.
            rb.Checked += (_, _) => { Dispatcher.BeginInvoke(() => rb.IsChecked = false); if (!_syncingNav) Navigate(tag); };
        }
        else
        {
            rb.Checked += Nav_Checked;
            rb.Checked += (_, _) => ic.Foreground = (Brush)FindResource("Accent");
            rb.Unchecked += (_, _) => ic.SetBinding(TextBlock.ForegroundProperty, fg);
        }
        return rb;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        if (sender is RadioButton { Tag: string tag }) Navigate(tag);
    }

    /// <summary>Checks the sidebar entry for a page (exact section entry first) without navigating again.</summary>
    private void SyncNav(string tag)
    {
        _syncingNav = true;
        try
        {
            var buttons = FindNavButtons().ToList();
            var rb = buttons.FirstOrDefault(r => (string)r.Tag == tag) ?? buttons.FirstOrDefault(r => (string)r.Tag == tag.Split('#')[0]);
            if (rb is not null) rb.IsChecked = true;
            else foreach (var b in buttons) b.IsChecked = false;
        }
        finally { _syncingNav = false; }
        Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
    }

    private void ToggleFavourite(object sender, RoutedEventArgs e)
    {
        var tag = _current;
        bool added = !S.FavouritePages.Remove(tag);
        if (added) S.FavouritePages.Add(tag);
        App.SaveSettingsSoon();
        RefreshPins();
        UpdateFavIcon();
        Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
        ShowToast(added ? $"{CrumbText.Text} added to favourites (sidebar and dashboard)." : $"{CrumbText.Text} removed from favourites.", "Success");
    }

    private void UpdateFavIcon()
    {
        bool fav = S.FavouritePages.Contains(_current);
        FavIcon.Text = fav ? "★" : "☆";
        FavIcon.Foreground = (Brush)FindResource(fav ? "Gold" : "Muted");
        FavButton.ToolTip = fav ? "Remove this page from favourites" : "Add this page to favourites";
    }

    /// <summary>Glides the selection pill to the checked nav item.</summary>
    private void MoveIndicator()
    {
        var rb = FindNavButtons().FirstOrDefault(r => r.IsChecked == true);
        if (rb is null || !rb.IsVisible) { NavIndicator.Opacity = 0; return; } // selected page sits in a collapsed group
        var y = rb.TranslatePoint(new Point(0, 0), NavGrid).Y;
        NavIndicator.Width = Math.Max(0, rb.ActualWidth);
        if (NavIndicator.Opacity < 1 || App.ReduceMotion)
        {
            NavIndicator.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(NavIndicator, y);
            NavIndicator.Height = rb.ActualHeight;
            NavIndicator.Opacity = 1;
            return;
        }
        NavIndicator.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        NavIndicator.Height = rb.ActualHeight;
    }

    /// <summary>
    /// Opens a page by tag ("monitor", "cat:Networking", or "monitor#health" to open a page at a section). Pages are created
    /// once and kept, so their state survives switching.
    /// </summary>
    public void Navigate(string tag)
    {
        _current = tag;
        SyncNav(tag);
        ShowPage(tag);
        S.RecentPages = AppNavigation.PushRecent(S.RecentPages, tag);
        if (!App.ScreenshotMode) App.SaveSettingsSoon();
        RefreshPins();
        UpdateFavIcon();
    }

    /// <summary>Creates a page for a tag (every sidebar and search entry resolves through here).</summary>
    public static FrameworkElement CreatePage(string pageTag) => pageTag switch
    {
        "dashboard" => new DashboardView(),
        "presets" => new PresetsView(),
        "macros" => new GamingMacrosView(),
        "benchmark" => new BenchmarkView(),
        "scanner" => new ScannerView(),
        "profiles" => new ProfilesView(),
        "library" => new GameLibraryView(),
        "tweaks" => new TweakListView(),
        "potato" => new PotatoView(),
        "stretched" => new StretchedView(),
        "fnstretch" => new FortniteStretchedView(),
        "booster" => new BoosterView(),
        "input" => new InputDelayView(),
        "ping" => new PingView(),
        "crosshair" => new CrosshairView(),
        "startup" => new StartupView(),
        "monitor" => new MonitorView(),
        "overlay" => new OverlayView(),
        "restore" => new RestoreView(),
        "logs" => new LogView(),
        "about" => new AboutView(),
        _ when pageTag.StartsWith("cat:", StringComparison.Ordinal) => new TweakListView(Enum.Parse<TweakCategory>(pageTag[4..])),
        _ => new DashboardView(),
    };

    private void ShowPage(string tag)
    {
        var pageTag = tag.Split('#')[0];
        if (!_pages.TryGetValue(pageTag, out var page))
        {
            using var perf = Perf.Measure($"create page {pageTag}", 20);
            page = CreatePage(pageTag);
            _pages[pageTag] = page;
        }
        var nav = AppNavigation.Find(tag);
        CrumbRoot.Text = nav is null ? "Money Tweaks" : AppNavigation.GroupOf(nav).Label;
        CrumbText.Text = nav?.Label ?? pageTag;

        bool same = ReferenceEquals(Host.Content, page);
        if (!same)
        {
            foreach (var other in _pages.Values.Where(p => p != page).OfType<IPageLifecycle>()) other.OnHidden();
            Host.Content = page;
            if (!App.ReduceMotion)
            {
                page.Opacity = 0;
                var shift = new TranslateTransform(0, 14);
                page.RenderTransform = shift;
                page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
            }
            else { page.BeginAnimation(OpacityProperty, null); page.Opacity = 1; page.RenderTransform = null; }
            if (page is IPageLifecycle lc) lc.OnShown();
        }
        if (nav?.Anchor is { } anchor && page is IAnchorTarget target)
        {
            void Go() => Dispatcher.BeginInvoke(() => target.ShowSection(anchor), DispatcherPriority.Loaded);
            if (!page.IsLoaded)
            {
                RoutedEventHandler? once = null;
                once = (_, _) => { page.Loaded -= once; Go(); };
                page.Loaded += once;
            }
            Go();
        }
        else if (same && page is UserControl { Content: ScrollViewer sv }) sv.ScrollToTop();
    }

    private IEnumerable<RadioButton> FindNavButtons() =>
        Nav.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<RadioButton>()).Where(r => r.GroupName == "nav");

    public async Task RefreshAppliedSummaryAsync()
    {
        var count = await Task.Run(() => BackupStore.AllIds().Count());
        AppliedSummary.Text = count == 0 ? "No tweaks applied yet. Everything is reversible." : $"{count} tweak{(count == 1 ? "" : "s")} applied · all backed up";
    }

    // ═════ Toasts ═════
    /// <summary>Stacked notification. <paramref name="tone"/>: Accent, Success, Gold, Danger.</summary>
    public void ShowToast(string text, string tone = "Accent")
    {
        if (!IsVisible && TrayIcon.Exists) { TrayIcon.Notify(text, tone); return; } // background mode
        var icon = tone switch { "Success" => "\uE73E", "Gold" => "\uE7BA", "Danger" => "\uE783", _ => "\uE946" };
        var row = new DockPanel();
        row.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 12, 0), Background = Ui.Brush(tone + "Soft"), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), FontSize = 13, Foreground = Ui.Brush(tone), HorizontalAlignment = HorizontalAlignment.Center }
        });
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var toast = new Border
        {
            Style = Ui.Res<Style>("CardBorder"), Padding = new Thickness(14, 12, 16, 12), Margin = new Thickness(0, 10, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x12, 0x17, 0x22)), Child = row, RenderTransform = new TranslateTransform(30, 0), Opacity = 0
        };
        while (ToastHost.Children.Count >= 3) ToastHost.Children.RemoveAt(0);
        ToastHost.Children.Add(toast);
        Ui.Animate(toast, OpacityProperty, 1, 180);
        Ui.Animate((TranslateTransform)toast.RenderTransform, TranslateTransform.XProperty, 0, 260);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(App.ReduceMotion ? 1 : 260));
            fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    // ═════ Command palette ═════
    private void OpenPalette(object sender, RoutedEventArgs e)
    {
        _paletteAll = BuildPaletteItems(); // rebuilt each time: games and favourites change
        Palette.Visibility = Visibility.Visible;
        PaletteBox.Text = "";
        PaletteFilter(null!, null!);
        Ui.Animate(Palette, OpacityProperty, 1, 140);
        PaletteShift.Y = App.ReduceMotion ? 0 : -12;
        Ui.Animate(PaletteShift, TranslateTransform.YProperty, 0, 220);
        PaletteBox.Focus();
    }

    private void ClosePalette(object? sender, RoutedEventArgs? e) => Palette.Visibility = Visibility.Collapsed;

    /// <summary>Settings that live inside pages, so search can find them by name.</summary>
    internal static readonly (string title, string where, string tag, string keywords)[] SettingsIndex =
    [
        ("Create a restore point before changes", "Safety", "about", "restore point system protection"),
        ("Confirm before Caution tweaks", "Safety", "about", "confirm caution warning"),
        ("Motion & effects", "Appearance", "about#appearance", "animation reduce motion effects"),
        ("Compact sidebar", "Appearance", "about#appearance", "sidebar icons collapse"),
        ("Check for updates automatically", "Updates", "about#updates", "update version github"),
        ("Start with Windows (background)", "Startup", "about", "autostart tray background sign in"),
        ("Run in the background now", "Title bar button / Settings", "about", "background tray hide minimize keep running"),
        ("Search hotkey (Ctrl+K)", "Hotkeys", "about#hotkeys", "shortcut search"),
        ("Emergency stop hotkey (Ctrl+Alt+End)", "Hotkeys", "about#hotkeys", "macro stop panic"),
        ("Overlay show/hide hotkey", "Performance Overlay", "overlay", "hotkey fps overlay"),
        ("Overlay automatic monitoring", "Performance Overlay", "overlay", "auto game watcher"),
        ("Customize dashboard cards", "Dashboard", "dashboard", "dashboard cards hide show customize"),
        ("Fortnite auto resolution", "Fortnite Stretched", "fnstretch", "2288 auto switch resolution"),
        ("NVIDIA texture filtering presets", "Potato Graphics", "potato", "lod bias nvidia profile inspector legacy"),
    ];

    private List<PaletteItem> BuildPaletteItems()
    {
        var list = new List<PaletteItem>();
        foreach (var p in AppNavigation.Pages)
        {
            var page = p;
            list.Add(new(p.Label, $"{AppNavigation.GroupOf(p).Label} · {p.Description}", p.Icon, p.Anchor is null ? "Page" : "Section", () => Navigate(page.Tag), p.Keywords, p.Tag));
        }
        foreach (var t in TweakCatalog.All)
        {
            var tw = t;
            list.Add(new(t.Name, $"{CategoryInfo.DisplayName(t.Category)} · {(t.Risk == Risk.Caution ? "Caution" : "Safe")}", CategoryIcons[t.Category], "Tweak", () =>
            {
                Navigate("cat:" + tw.Category);
                if (Host.Content is TweakListView v) v.FocusTweak(tw.Name);
            }, tw.Id));
        }
        foreach (var g in GameLibrary.Items(GameLibrary.Current))
        {
            var name = g.Def.Name;
            list.Add(new(name, $"Game · {GameCatalog.Label(g.Def.Category)} · {string.Join(", ", g.Def.ExeNames)}", "\uE7FC", "Game", () =>
            {
                Navigate("library");
                if (Host.Content is GameLibraryView v) v.FocusGame(name);
            }, string.Join(" ", g.Def.ExeNames)));
        }
        foreach (var (title, where, tag, keywords) in SettingsIndex)
        {
            var t = tag;
            list.Add(new(title, "Setting · " + where, "\uE713", "Setting", () => Navigate(t), keywords));
        }
        return list;
    }

    private void PaletteFilter(object sender, TextChangedEventArgs e)
    {
        if (_paletteAll is null) return;
        var q = PaletteBox.Text.Trim();
        IEnumerable<PaletteItem> hits;
        if (q.Length == 0)
        {
            // Favourites and recent pages first, then every page.
            var byTag = _paletteAll.Where(i => i.Tag is not null && i.Kind is "Page" or "Section").GroupBy(i => i.Tag!).ToDictionary(g => g.Key, g => g.First());
            var fav = S.FavouritePages.Where(byTag.ContainsKey).Select(t => byTag[t] with { Kind = "★ Favourite" });
            var recent = S.RecentPages.Where(t => byTag.ContainsKey(t) && !S.FavouritePages.Contains(t)).Take(5).Select(t => byTag[t] with { Kind = "Recent" });
            var pages = _paletteAll.Where(i => i.Kind == "Page");
            hits = fav.Concat(recent).Concat(pages).DistinctBy(i => i.Tag ?? i.Title);
        }
        else hits = _paletteAll.Select(i => (i, score: Score(i, q))).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.i);
        var shown = hits.Take(80).ToList();
        PaletteList.ItemsSource = shown;
        PaletteList.SelectedIndex = shown.Count > 0 ? 0 : -1;
        PaletteCount.Text = q.Length == 0 ? $"{_paletteAll.Count} items: pages, tweaks, games and settings" : $"{shown.Count} result{(shown.Count == 1 ? "" : "s")}";
    }

    internal static int Score(PaletteItem i, string q)
    {
        int s = 0;
        if (i.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) s += 100;
        else if (i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) s += 60;
        else if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase))) s += 40;
        else if (i.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)) s += 30;
        else if (i.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)) s += 20;
        if (s > 0 && i.Kind is "Page" or "Section") s += 15;      // pages first on ties
        else if (s > 0 && i.Kind is "Game" or "Setting") s += 8;
        return s;
    }

    private void PaletteKeys(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: ClosePalette(null, null); e.Handled = true; break;
            case Key.Down: PaletteList.SelectedIndex = Math.Min(PaletteList.Items.Count - 1, PaletteList.SelectedIndex + 1); PaletteList.ScrollIntoView(PaletteList.SelectedItem); e.Handled = true; break;
            case Key.Up: PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1); PaletteList.ScrollIntoView(PaletteList.SelectedItem); e.Handled = true; break;
            case Key.Enter: RunPaletteSelection(); e.Handled = true; break;
        }
    }

    private void PaletteClick(object sender, MouseButtonEventArgs e) => RunPaletteSelection();

    private void RunPaletteSelection()
    {
        if (PaletteList.SelectedItem is not PaletteItem item) return;
        ClosePalette(null, null);
        item.Open();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Palette.Visibility == Visibility.Visible) { ClosePalette(null, null); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ═════ Banners & chrome ═════
    public void HideUpdateBanner() => UpdateBanner.Visibility = Visibility.Collapsed;

    public void ShowUpdateBanner(Core.Features.UpdateInfo u)
    {
        UpdateText.Text = $"Money Tweaks {u.Version.ToString(3)} is available (you have {App.Version}).";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        void OnStatus(string s) => Dispatcher.Invoke(() => UpdateText.Text = s);
        Updater.StatusChanged += OnStatus;
        try { await Updater.InstallAsync(); }
        finally { Updater.StatusChanged -= OnStatus; UpdateButton.IsEnabled = true; }
    }

    private void DismissAdminBanner(object sender, RoutedEventArgs e) => AdminBanner.Visibility = Visibility.Collapsed;

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Admin.RestartElevated()) Application.Current.Shutdown();
        else ShowToast("Elevation was cancelled.", "Gold");
    }

    private void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Background_Click(object s, RoutedEventArgs e) => SendToBackground();

    private bool _hiddenToTray;

    /// <summary>Hides the window to the tray; the app keeps running. Open it again from the tray icon or by starting Money Tweaks.</summary>
    public void SendToBackground()
    {
        App.EnterBackground();
        _hiddenToTray = true;
        // Pages stop their live sampling while nobody can see them; background services keep running.
        foreach (var p in _pages.Values.OfType<IPageLifecycle>()) p.OnHidden();
        _ambient?.Pause(Ambient);
        Hide();
        TrayIcon.Notify("Money Tweaks is running in the background. Double-click the tray icon to open it; right-click › Exit to stop it.", "Accent");
    }
    private void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    /// <summary>First time the window is shown after a background start: build the dashboard and animations now.</summary>
    private void EnsureOpened()
    {
        if (_opened || !App.BackgroundStart) return;
        _opened = true;
        if (Host.Content is null) Navigate("dashboard");
        StartAmbient();
        Log.Info("Opened from background start; dashboard built now");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Started in the background: closing hides the window so the background features keep running.
        if ((App.BackgroundStart || App.RunningInBackground) && !App.ExitRequested)
        {
            e.Cancel = true;
            Hide();
            TrayIcon.Notify("Still running in the background. Right-click the tray icon to exit.", "Accent");
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Called when a second launch asks this copy to show itself.</summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        EnsureOpened();
        if (_hiddenToTray)
        {
            _hiddenToTray = false;
            if (Host.Content is IPageLifecycle lc) lc.OnShown();
            _ambient?.Resume(Ambient);
        }
        Activate();
        Topmost = true; Topmost = false; // reliably raise above the launching app
        Focus();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Borderless windows overhang the screen when maximized; pad to keep content visible.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (WindowState == WindowState.Minimized) _ambient?.Pause(Ambient); else if (IsActive) _ambient?.Resume(Ambient);
    }

    // ═════ Screenshot mode (real screenshots for the website) ═════
    private bool _capturing; // screenshot captures block the UI thread by design; don't count them as stalls

    private async Task Snap(string dir, string name)
    {
        _capturing = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using var fs = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(fs);
    }

    public async Task CaptureScreenshotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        string[] tags = ["dashboard", "cat:General", "cat:Debloating", "cat:Networking", "cat:Advanced", "cat:Services", "cat:AppOptimizer", "cat:AppRemoval", "cat:GameProfiles", "cat:Bios",
            "profiles", "scanner", "presets", "benchmark", "macros", "potato", "stretched", "fnstretch", "booster", "input", "ping", "crosshair", "startup", "monitor", "restore", "about"];
        foreach (var tag in tags)
        {
            Navigate(tag);
            await Task.Delay(tag is "dashboard" or "monitor" or "ping" or "cat:Networking" ? 6000 : 3500);
            await Snap(dir, tag.Replace("cat:", "cat-").ToLowerInvariant());
            if (tag == "dashboard")
            {
                OpenPalette(this, new RoutedEventArgs());
                PaletteBox.Text = "nagle";
                await Task.Delay(600);
                await Snap(dir, "palette");
                ClosePalette(null, null);
            }
            if (tag == "macros" && Host.Content is GamingMacrosView mv)
            {
                mv.ShowEditorForScreenshot();
                await Task.Delay(1200);
                await Snap(dir, "macros-editor");
                mv.CloseEditorForScreenshot();
            }
            if (tag == "stretched" && Host.Content is StretchedView stv && stv.Content is ScrollViewer ssv)
            {
                foreach (var (off, name) in new[] { (640.0, "stretched-desktop"), (1500.0, "stretched-fortnite") })
                {
                    ssv.ScrollToVerticalOffset(off);
                    await Task.Delay(700);
                    await Snap(dir, name);
                }
                ssv.ScrollToVerticalOffset(0);
            }
            if (tag == "fnstretch" && Host.Content is FortniteStretchedView fsv && fsv.Content is ScrollViewer fsc)
            {
                fsc.ScrollToVerticalOffset(1250);
                await Task.Delay(700);
                await Snap(dir, "fnstretch-gpu");
                fsc.ScrollToVerticalOffset(0);
            }
            if (tag == "scanner" && Host.Content is ScannerView scv)
            {
                for (int i = 0; i < 150 && scv.IsScanning; i++) await Task.Delay(200);
                await Task.Delay(600);
                await Snap(dir, "scanner-results");
            }
            if (tag == "benchmark" && Host.Content is BenchmarkView bv)
            {
                await bv.AuditForScreenshotAsync(); // read-only verification
                await Task.Delay(800);
                await Snap(dir, "benchmark-audit");
            }
            if (tag == "presets" && Host.Content is PresetsView prv)
            {
                prv.ShowEditorForScreenshot();
                await Task.Delay(1200);
                await Snap(dir, "presets-editor");
                prv.CloseEditorForScreenshot();
            }
            if (tag == "potato" && Host.Content is PotatoView pv && pv.Content is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(640);
                await Task.Delay(1200);
                await Snap(dir, "potato-controls");
                sv.ScrollToVerticalOffset(0);
                foreach (var preset in new[] { Core.Features.FnPreset.Competitive, Core.Features.FnPreset.Potato, Core.Features.FnPreset.UltraPotato })
                {
                    pv.PreviewPresetOnly(preset);
                    await Task.Delay(1200);
                    await Snap(dir, $"potato-preset-{preset.ToString().ToLowerInvariant()}");
                }
                pv.PreviewPresetOnly(null);
            }
        }
        Log.Info($"Screenshots saved to {dir}");
        Application.Current.Shutdown();
    }
}

public interface IPageLifecycle
{
    void OnShown();
    void OnHidden();
}

/// <summary>A page with sections that navigation can open directly ("monitor#health").</summary>
public interface IAnchorTarget
{
    void ShowSection(string anchor);
}
