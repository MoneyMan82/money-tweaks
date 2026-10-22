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
        Controls.Motion.SquareCorners(this);
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
        Loaded += async (_, _) => { if (!App.BackgroundStart) FadeInWindow(); await StartupTasksAsync(); };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
        // Narrow windows: the search button keeps its icon and shortcut, so the breadcrumb next to it never gets covered.
        SizeChanged += (_, e) => SearchHint.Text = e.NewSize.Width < 1240 ? "Search" : "Search pages, tweaks, games & settings";
    }

    private bool _startupDone, _opened;

    /// <summary>Runs once: on Loaded for a normal start, or right away (window hidden) for a background start.</summary>
    public async Task StartupTasksAsync()
    {
        if (_startupDone) return;
        _startupDone = true;
        // Compatibility engine: read this PC's profile (WMI, ~1 s) in the background so checks are instant later.
        if (!App.BackgroundStart) _ = Task.Run(() => Core.Engine.SystemProfile.Current);
        // Discovery index (purposes, relations, conflicts) for search, related tweaks and the map: built once, off the UI thread.
        if (!App.BackgroundStart) _ = Task.Run(() => Core.Discovery.TweakKnowledge.Current);
        // Tuning applied in an earlier session but never confirmed (the app closed or crashed): offer to undo, never re-apply.
        try
        {
            // A fan curve left "running" by a crash: put the fan back on automatic first (safety, never a new setting).
            if (await Task.Run(FanCurveService.RecoverAfterCrash) is { } fanMsg) ShowToast(fanMsg, "Gold");
            var ts = Core.Tuning.TuningState.Load();
            if (ts.PendingConfirmation && ts.HasChanges)
                ShowToast($"CPU/GPU tuning \"{ts.ProfileName}\" from {ts.AppliedAt:g} wasn't confirmed stable. Open CPU & GPU Tuning to undo or keep it.", "Gold");
        }
        catch (Exception ex) { Log.Warn("tuning state: " + ex.Message); }
        {
            Log.Info($"[perf] startup to {(App.BackgroundStart ? "background ready" : "first frame")}: {App.StartupClock.ElapsedMilliseconds} ms");
            StartStallDetector();
            MacroService.Init(this);
            OverlayService.Init(this); // overlay hotkey + optional game watcher (also in background mode)
            if (!App.ScreenshotMode) CrosshairService.Init(this); // crosshair hotkeys + per-game switching, without opening the page
            if (!App.BackgroundStart) StartAmbient();
            Activated += (_, _) => _ambient?.Resume(Ambient);
            Deactivated += (_, _) => _ambient?.Pause(Ambient);
            await RefreshAppliedSummaryAsync();
            if (!App.ScreenshotMode) Updater.VerifyPendingUpdate();
            if (!App.ScreenshotMode) await BenchmarkView.CheckPendingSyntheticRestoreAsync();
            if (!App.ScreenshotMode) await RealityLabView.CheckPendingAsync();
            if (!App.ScreenshotMode) await GenomeView.CheckPendingAsync();
            if (!App.ScreenshotMode && !App.BackgroundStart) _ = GenomeView.CheckForUpdatesAsync();
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
        if (LegacyTheme.Active)
        {
            // The original look had no ambient glow; the sidebar highlight uses the legacy accent.
            Ambient.Visibility = Visibility.Collapsed;
            NavIndicator.Background = (Brush)FindResource("AccentSoft");
            NavIndicator.BorderBrush = (Brush)FindResource("AccentSoft");
            return;
        }
        if (App.ReduceMotion) return;
        var a = new DoubleAnimation(1, 0.6, TimeSpan.FromSeconds(7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        Storyboard.SetTargetProperty(a, new PropertyPath(OpacityProperty));
        // Slow drift of the whole cached light layer (moves the cached bitmap; nothing is re-rendered).
        if (Ambient.RenderTransform is not TranslateTransform) Ambient.RenderTransform = new TranslateTransform();
        var dx = new DoubleAnimation(-28, 28, TimeSpan.FromSeconds(23)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var dy = new DoubleAnimation(18, -18, TimeSpan.FromSeconds(31)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTargetProperty(dx, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
        Storyboard.SetTargetProperty(dy, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        _ambient = new Storyboard();
        _ambient.Children.Add(a);
        _ambient.Children.Add(dx);
        _ambient.Children.Add(dy);
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

    /// <summary>Builds the sidebar from AppNavigation: Dashboard, favourites, then the collapsible groups.</summary>
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
        var chevron = new TextBlock { Text = "\uE70D", Style = (Style)FindResource("Icon"), FontSize = 9, Foreground = (Brush)FindResource("Subtle"), VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(collapsed ? -90 : 0) };
        var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(12, 14, 10, 6), Cursor = Cursors.Hand, ToolTip = collapsed ? $"Show {g.Label}" : $"Hide {g.Label}" };
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        var label = new TextBlock { Text = g.Label.ToUpperInvariant(), Foreground = (Brush)FindResource("Subtle"), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont") };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        row.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Hairline"), Margin = new Thickness(10, 1, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.MouseEnter += (_, _) => label.Foreground = (Brush)FindResource("Muted");
        row.MouseLeave += (_, _) => label.Foreground = (Brush)FindResource("Subtle");
        row.MouseLeftButtonUp += (_, _) =>
        {
            bool nowCollapsed = !S.CollapsedNavGroups.Contains(g.Id); // state, not visibility: a second click mid-animation reverses it
            if (nowCollapsed) Motion.Collapse(items); else Motion.Expand(items);
            Motion.Rotate(chevron, nowCollapsed ? -90 : 0);
            row.ToolTip = nowCollapsed ? $"Show {g.Label}" : $"Hide {g.Label}";
            S.CollapsedNavGroups.Remove(g.Id);
            if (nowCollapsed) S.CollapsedNavGroups.Add(g.Id);
            App.SaveSettingsSoon();
            Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
            // Follow the moving items while the group animates, then settle.
            Motion.Tween(row, 0, 1, 240, _ => SnapIndicator(), () => Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded));
        };
        return row;
    }

    /// <summary>
    /// Favourites (pinned) at the top of the sidebar. Dashboard is the fixed home item, so it never appears twice.
    /// There's no "Recent" list in the sidebar any more: it reshuffled on every page change. Recent pages are still
    /// offered by the search (Ctrl+K) when it's empty.
    /// </summary>
    private void RefreshPins()
    {
        bool compact = S.SidebarCompact;
        FillPins(_favPanel, "FAVOURITES", AppNavigation.SidebarFavourites(S.FavouritePages), compact);
        _recentPanel.Children.Clear();
    }

    private void FillPins(StackPanel panel, string title, IEnumerable<string> tags, bool compact)
    {
        panel.Children.Clear();
        var pages = tags.Select(t => AppNavigation.Pages.FirstOrDefault(p => p.Tag == t)).OfType<NavPage>().ToList();
        if (pages.Count == 0) return;
        panel.Children.Add(compact
            ? new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(12, 6, 12, 8), ToolTip = title }
            : new TextBlock { Text = title, Foreground = (Brush)FindResource("Subtle"), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont"), Margin = new Thickness(12, 8, 0, 6) });
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
        double from = SideCol.ActualWidth;
        S.SidebarCompact = !S.SidebarCompact;
        App.SaveSettingsSoon();
        BuildNav();
        double to = SideCol.Width.Value;
        SideCol.Width = new GridLength(from);
        Motion.Tween(SideCol, from, to, 280, v => { SideCol.Width = new GridLength(v); SnapIndicator(); }, () => Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded));
    }

    /// <summary>Content fades in when the window opens (skipped with reduced motion).</summary>
    private void FadeInWindow()
    {
        _closingAnim = false;
        if (!Motion.Enabled) { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 1; return; }
        Root.Opacity = 0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var s = RootScale();
        var grow = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private bool _closingAnim, _fadedOut;

    private ScaleTransform RootScale()
    {
        if (Root.RenderTransform is not ScaleTransform s || s.IsFrozen) { s = new ScaleTransform(1, 1); Root.RenderTransform = s; Root.RenderTransformOrigin = new Point(0.5, 0.5); }
        return s;
    }

    /// <summary>Closing animation: content fades and settles back slightly, then <paramref name="then"/> closes or hides the window.</summary>
    private void FadeOutWindow(Action then)
    {
        if (_closingAnim) return; // a second click while it's closing does nothing extra
        // Already faded out (Close → OnClosing → hide to tray): finish straight away.
        if (_fadedOut || !Motion.Enabled || !IsVisible || WindowState == WindowState.Minimized) { then(); return; }
        _closingAnim = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            _closingAnim = false;
            _fadedOut = true;
            try { then(); } finally { _fadedOut = false; }
            // Ready for the next time the window is shown (BringToFront fades it back in).
            Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 1;
            var rs = RootScale(); rs.BeginAnimation(ScaleTransform.ScaleXProperty, null); rs.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        var s = RootScale();
        var shrink = new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private RadioButton NavItem(string tag, string icon, string label, string? count, bool compact, bool pin = false)
    {
        var fg = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
        var panel = new DockPanel();
        if (count is not null && !compact)
        {
            var badge = new Border { CornerRadius = new CornerRadius(0), Padding = new Thickness(6, 0, 6, 1), Background = (Brush)FindResource("Hairline"), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = count, FontSize = 10.5, FontFamily = (FontFamily)FindResource("DisplayFont"), Foreground = (Brush)FindResource("Subtle") } };
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
            rb.Checked += (_, _) => ic.Foreground = (Brush)FindResource("AccentGradient");
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
        // Dashboard is always at the top of the sidebar; starring it would only add a duplicate.
        FavButton.Visibility = AppNavigation.IsHome(_current) ? Visibility.Collapsed : Visibility.Visible;
        bool fav = S.FavouritePages.Contains(_current);
        FavIcon.Text = fav ? "★" : "☆";
        FavIcon.Foreground = (Brush)FindResource(fav ? "Gold" : "Muted");
        FavButton.ToolTip = fav ? "Remove this page from favourites" : "Add this page to favourites";
    }

    private bool _snapIndicator;

    /// <summary>Puts the selection pill on its item without its own glide (used while the sidebar itself is animating).</summary>
    private void SnapIndicator()
    {
        _snapIndicator = true;
        try { MoveIndicator(); } finally { _snapIndicator = false; }
    }

    /// <summary>Glides the selection pill to the checked nav item.</summary>
    private void MoveIndicator()
    {
        var rb = FindNavButtons().FirstOrDefault(r => r.IsChecked == true);
        if (rb is null || !rb.IsVisible) { NavIndicator.Opacity = 0; return; } // selected page sits in a collapsed group
        var y = rb.TranslatePoint(new Point(0, 0), NavGrid).Y;
        NavIndicator.Width = Math.Max(0, rb.ActualWidth);
        if (NavIndicator.RenderTransform is not TranslateTransform move || move.IsFrozen) NavIndicator.RenderTransform = move = new TranslateTransform();
        if (NavIndicator.Opacity < 1 || App.ReduceMotion || _snapIndicator)
        {
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.Y = y;
            NavIndicator.Height = rb.ActualHeight;
            NavIndicator.Opacity = 1;
            return;
        }
        if (Math.Abs(move.Y - y) < 0.5) return;
        // Glide (render transform only), then the edge bar stretches and brightens as the selection lands.
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        NavIndicator.Height = rb.ActualHeight;
        NavEdgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
        Controls.Motion.Pulse(NavEdge, 0.2, 520);
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
    public static FrameworkElement CreatePage(string pageTag)
    {
        var page = CreatePageCore(pageTag);
        AlignEyebrow(page, pageTag);
        return page;
    }

    /// <summary>Old section names some pages still carried above their title; they now show the sidebar group instead.</summary>
    private static readonly HashSet<string> GenericEyebrows = new(StringComparer.OrdinalIgnoreCase)
        { "GAMING TOOLS", "GAMING", "TOOLS", "SYSTEM", "SAFETY", "MONEY TWEAKS", "TWEAK CATEGORY" };

    /// <summary>Keeps the small label above each page title in step with the sidebar group (descriptive taglines stay).</summary>
    private static void AlignEyebrow(FrameworkElement page, string pageTag)
    {
        if (AppNavigation.Find(pageTag) is not { } nav) return;
        var eyebrowStyle = Application.Current.TryFindResource("Eyebrow");
        static IEnumerable<TextBlock> Texts(object node, int depth)
        {
            if (depth > 8 || node is not DependencyObject d) yield break;
            if (node is TextBlock tb) { yield return tb; yield break; }
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                foreach (var t in Texts(child, depth + 1)) yield return t;
        }
        var eyebrow = Texts(page, 0).FirstOrDefault(t => ReferenceEquals(t.Style, eyebrowStyle));
        if (eyebrow is not null && GenericEyebrows.Contains(eyebrow.Text.Trim()))
            eyebrow.Text = AppNavigation.GroupOf(nav).Label.ToUpperInvariant();
    }

    private static FrameworkElement CreatePageCore(string pageTag) => pageTag switch
    {
        "dashboard" => new DashboardView(),
        "presets" => new PresetsView(),
        "macros" => new GamingMacrosView(),
        "benchmark" => new BenchmarkView(),
        "lab" => new RealityLabView(),
        "scanner" => new ScannerView(),
        "profiles" => new ProfilesView(),
        "library" => new GameLibraryView(),
        "tweaks" => new TweakListView(),
        "tuning" => new TuningView(),
        "genome" => new GenomeView(),
        "map" => new TweakMapView(),
        "cleaner" => new CleanerView(),
        "advisor" => new AdvisorView(),
        "potato" => new PotatoView(),
        "stretched" => new StretchedView(),
        "fnstretch" => new FortniteStretchedView(),
        "booster" => new BoosterView(),
        "input" => new InputDelayView(),
        "ping" => new PingView(),
        "crosshair" => new CrosshairView(),
        "community" => new CommunityHubView(),
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
                // Cinematic but quick: fade, rise and settle from 98.5% scale (render transforms only, no relayout).
                page.Opacity = 0;
                var shift = new TranslateTransform(0, 18);
                var scale = new ScaleTransform(0.985, 0.985);
                page.RenderTransformOrigin = new Point(0.5, 0);
                page.RenderTransform = new TransformGroup { Children = { scale, shift } };
                var outQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };
                page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(460)) { EasingFunction = outQuint });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(460)) { EasingFunction = outQuint });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(460)) { EasingFunction = outQuint });
            }
            else { page.BeginAnimation(OpacityProperty, null); page.Opacity = 1; page.RenderTransform = null; }
            if (page is UserControl { Content: ScrollViewer { Content: Panel body } }) Motion.Stagger(body);
            else if (page is UserControl { Content: Panel panel }) Motion.Stagger(panel);
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
            Width = 30, Height = 30, CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 12, 0), Background = Ui.Brush(tone + "Soft"), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), FontSize = 13, Foreground = Ui.Brush(tone), HorizontalAlignment = HorizontalAlignment.Center }
        });
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        // Tone edge on the left, like the sidebar selection bar.
        var body = new Grid();
        body.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(0), Background = Ui.Brush(tone), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-14, 2, 0, 2) });
        body.Children.Add(row);
        // Countdown line along the bottom edge: shows how long the notice stays.
        var countdown = new ScaleTransform(1, 1);
        body.Children.Add(new Border { Height = 1, Background = Ui.Brush(tone), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-14, 0, -16, -12), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = countdown });
        if (!App.ReduceMotion) countdown.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(3.6)));
        var toast = new Border
        {
            Style = Ui.Res<Style>("CardBorder"), Padding = new Thickness(14, 12, 16, 12), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x13, 0x16, 0x1C)), Child = body, RenderTransform = new TranslateTransform(30, 0), Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.45 },
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
        _searchCtx = null;                // this PC's profile may have been detected since
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
            list.Add(new(t.Name, $"{CategoryInfo.DisplayName(t.Category)} · {(t.Risk == Risk.Caution ? "Caution" : "Safe")}", CategoryIcons[t.Category], "Tweak", () => OpenTweak(tw.Id), tw.Id));
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

    /// <summary>Opens a tweak where it lives: its category page, filtered to it (used by search, related tweaks and the map).</summary>
    public void OpenTweak(string id)
    {
        if (TweakCatalog.Find(id) is not { } t) return;
        Navigate("cat:" + t.Category);
        if (Host.Content is TweakListView v) v.FocusTweak(t.Name);
    }

    private Core.Discovery.SearchContext? _searchCtx;

    /// <summary>Personalises palette ranking: this PC's compatibility findings (once detected) and the Performance Genome goal.</summary>
    private static Core.Discovery.SearchContext BuildSearchContext()
    {
        Func<Tweak, Core.Engine.CompatSeverity?>? compat = null;
        if (Core.Engine.SystemProfile.IsDetected)
        {
            var profile = Core.Engine.SystemProfile.Current;
            var cache = new Dictionary<string, Core.Engine.CompatSeverity?>();
            compat = t =>
            {
                if (cache.TryGetValue(t.Id, out var v)) return v;
                var worst = Core.Engine.Compatibility.StaticFindings(t, profile).Select(f => f.Severity).Where(s => s >= Core.Engine.CompatSeverity.Warn).DefaultIfEmpty(Core.Engine.CompatSeverity.Info).Max();
                return cache[t.Id] = worst == Core.Engine.CompatSeverity.Info ? null : worst;
            };
        }
        string? goal = null;
        try { if (File.Exists(Core.Genome.GenomeStore.PathFor)) goal = Core.Genome.GenomeStore.Load().Goal.Preset; } catch { }
        return new(compat, goal);
    }

    private void PaletteFilter(object sender, TextChangedEventArgs e)
    {
        if (_paletteAll is null) return;
        var q = PaletteBox.Text.Trim();
        IEnumerable<PaletteItem> hits = [];
        if (q.Length == 0)
        {
            // Favourites and recent pages first, then every page.
            var byTag = _paletteAll.Where(i => i.Tag is not null && i.Kind is "Page" or "Section").GroupBy(i => i.Tag!).ToDictionary(g => g.Key, g => g.First());
            var fav = S.FavouritePages.Where(byTag.ContainsKey).Select(t => byTag[t] with { Kind = "★ Favourite" });
            var recent = S.RecentPages.Where(t => byTag.ContainsKey(t) && !S.FavouritePages.Contains(t)).Take(5).Select(t => byTag[t] with { Kind = "Recent" });
            var pages = _paletteAll.Where(i => i.Kind == "Page");
            hits = fav.Concat(recent).Concat(pages).DistinctBy(i => i.Tag ?? i.Title);
        }
        Core.Discovery.SearchResult? found = null;
        if (q.Length > 0)
        {
            // Natural-language discovery over the tweak library ranks tweaks by meaning, this PC and your goal; the plain
            // title match below still finds pages, games and settings (and exact tweak names).
            _searchCtx ??= BuildSearchContext();
            found = Core.Discovery.TweakSearch.Search(Core.Discovery.TweakKnowledge.Current, q, _searchCtx, 60);
            var byId = found.Hits.ToDictionary(h => h.Tweak.Id);
            var intent = found.Intent;
            hits = _paletteAll.Select(i =>
            {
                int s = Score(i, q);
                if (i.Kind == "Tweak" && byId.TryGetValue(i.Keywords, out var h))
                {
                    s = Math.Max(s, (int)(h.Score * 4));
                    i = i with { Subtitle = $"{CategoryInfo.DisplayName(h.Tweak.Category)} · {string.Join(" · ", h.Reasons.Take(4))}" };
                }
                else if (i.Kind == "Tweak" && intent.IsNatural && s < 60) s = 0; // natural queries: tweaks only by meaning
                else if (intent.IsNatural && s == 0 && i.Kind is "Page" or "Section" or "Setting")
                {
                    var text = (i.Title + " " + i.Keywords + " " + i.Subtitle).ToLowerInvariant();
                    int terms = intent.Terms.Count(t => text.Contains(t, StringComparison.Ordinal)) + intent.Games.Count(g => text.Contains(g.ToLowerInvariant(), StringComparison.Ordinal));
                    s = terms * 25;
                }
                return (i, score: s);
            }).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.i);
        }
        var shown = hits.Take(80).ToList();
        PaletteList.ItemsSource = shown;
        PaletteList.SelectedIndex = shown.Count > 0 ? 0 : -1;
        PaletteCount.Text = q.Length == 0 ? $"{_paletteAll.Count} items: pages, tweaks, games and settings"
            : $"{shown.Count} result{(shown.Count == 1 ? "" : "s")}" +
              (found is { Intent.IsNatural: true } f && f.Intent.Understood() is { Length: > 0 } u ? $" · understood: {u}" : "") +
              (found is { HiddenBySideEffect: > 0 } f2 ? $" · {f2.HiddenBySideEffect} hidden (side effects you ruled out)" : "");
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
        UpdateText.Text = $"Money Tweaks {AppVersioning.Display(u.Version)} is available (you have {App.Version}).";
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
        FadeOutWindow(Hide);
        TrayIcon.Notify("Money Tweaks is running in the background. Double-click the tray icon to open it; right-click › Exit to stop it.", "Accent");
    }
    private void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => FadeOutWindow(Close);

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
            FadeOutWindow(Hide);
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
            FadeInWindow();
            if (Host.Content is IPageLifecycle lc) lc.OnShown();
            _ambient?.Resume(Ambient);
        }
        Activate();
        Topmost = true; Topmost = false; // reliably raise above the launching app
        Focus();
    }

    private bool _wasMinimized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Borderless windows overhang the screen when maximized; pad to keep content visible.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (WindowState == WindowState.Minimized) _ambient?.Pause(Ambient); else if (IsActive) _ambient?.Resume(Ambient);
        // Coming back from the taskbar: the content eases in like on first open.
        if (_wasMinimized && WindowState != WindowState.Minimized) FadeInWindow();
        _wasMinimized = WindowState == WindowState.Minimized;
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
