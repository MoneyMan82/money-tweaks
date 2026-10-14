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
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;
using MoneyTweaks.Views;

namespace MoneyTweaks;

public sealed record PaletteItem(string Title, string Subtitle, string Icon, string Kind, Action Open);

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    public static MainWindow? Current { get; private set; }
    private Storyboard? _ambient;
    private List<PaletteItem>? _paletteAll;

    internal static readonly (string tag, string icon, string label)[] Tools =
    [
        ("presets", "\uE8F1", "Presets"),
        ("potato", "\uE790", "Potato Graphics"),
        ("booster", "\uE945", "Game Booster"),
        ("input", "\uE962", "Input Delay Reducer"),
        ("ping", "\uE701", "Ping Stabilizer"),
        ("crosshair", "\uE710", "Custom Crosshair"),
        ("startup", "\uE7E8", "Startup Apps"),
        ("monitor", "\uE9D9", "Hardware Monitor"),
        ("restore", "\uE777", "Backup & Restore"),
        ("logs", "\uE81C", "Activity Log"),
        ("about", "\uE713", "Settings & About"),
    ];

    internal static readonly Dictionary<TweakCategory, string> CategoryIcons = new()
    {
        [TweakCategory.General] = "\uE771", [TweakCategory.Debloating] = "\uE74D", [TweakCategory.Networking] = "\uE968",
        [TweakCategory.Advanced] = "\uE9E9", [TweakCategory.Services] = "\uE912", [TweakCategory.AppOptimizer] = "\uEC4A",
        [TweakCategory.AppRemoval] = "\uE71D", [TweakCategory.GameProfiles] = "\uE7FC", [TweakCategory.Bios] = "\uE950",
    };

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        VersionText.Text = $"v{App.Version} · free & open source";
        OverviewNav.Children.Add(NavItem("dashboard", "\uE80F", "Dashboard", null));
        foreach (var c in Enum.GetValues<TweakCategory>())
            CategoryNav.Children.Add(NavItem("cat:" + c, CategoryIcons[c], CategoryInfo.DisplayName(c), TweakCatalog.InCategory(c).Count().ToString()));
        foreach (var (tag, icon, label) in Tools) ToolNav.Children.Add(NavItem(tag, icon, label, null));

        bool admin = Admin.IsElevated;
        AdminBanner.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        AdminText.Text = admin ? "Administrator" : "Standard user";
        AdminDot.Fill = AdminHalo.Fill = (Brush)FindResource(admin ? "Success" : "Gold");
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        TweakService.Changed += () => _ = RefreshAppliedSummaryAsync();
        Navigate("dashboard");
        Loaded += async (_, _) =>
        {
            Log.Info($"[perf] startup to first frame: {App.StartupClock.ElapsedMilliseconds} ms");
            StartStallDetector();
            StartAmbient();
            Activated += (_, _) => _ambient?.Resume(Ambient);
            Deactivated += (_, _) => _ambient?.Pause(Ambient);
            await RefreshAppliedSummaryAsync();
            if (!App.ScreenshotMode) Updater.VerifyPendingUpdate();
            if (App.Settings.AutoCheckUpdates && !App.ScreenshotMode) await Updater.CheckAsync(manual: false);
        };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
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

    private RadioButton NavItem(string tag, string icon, string label, string? count)
    {
        var fg = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
        var panel = new DockPanel();
        if (count is not null)
        {
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = count, FontSize = 10.5, Foreground = (Brush)FindResource("Muted") } };
            DockPanel.SetDock(badge, Dock.Right);
            panel.Children.Add(badge);
        }
        var ic = new TextBlock { Text = icon, Style = (Style)FindResource("Icon"), FontSize = 15, Margin = new Thickness(0, 0, 12, 0) };
        ic.SetBinding(TextBlock.ForegroundProperty, fg);
        var tx = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        tx.SetBinding(TextBlock.ForegroundProperty, fg);
        panel.Children.Add(ic);
        panel.Children.Add(tx);
        var rb = new RadioButton { Style = (Style)FindResource("NavButton"), Tag = tag, Content = panel };
        rb.Checked += Nav_Checked;
        rb.Checked += (_, _) => ic.Foreground = (Brush)FindResource("Accent");
        rb.Unchecked += (_, _) => ic.SetBinding(TextBlock.ForegroundProperty, fg);
        return rb;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && IsLoaded) Navigate(tag);
        Dispatcher.BeginInvoke(MoveIndicator, DispatcherPriority.Loaded);
    }

    /// <summary>Glides the selection pill to the checked nav item.</summary>
    private void MoveIndicator()
    {
        var rb = FindNavButtons().FirstOrDefault(r => r.IsChecked == true);
        if (rb is null || !rb.IsVisible) return;
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

    public void Navigate(string tag)
    {
        // Keep the sidebar selection in sync when navigation comes from code (e.g. dashboard tiles).
        foreach (var rb in FindNavButtons()) if ((string)rb.Tag == tag && rb.IsChecked != true) { rb.IsChecked = true; return; }

        if (!_pages.TryGetValue(tag, out var page))
        {
            using var perf = Perf.Measure($"create page {tag}", 20);
            page = tag switch
            {
                "dashboard" => new DashboardView(),
                "presets" => new PresetsView(),
                "potato" => new PotatoView(),
                "booster" => new BoosterView(),
                "input" => new InputDelayView(),
                "ping" => new PingView(),
                "crosshair" => new CrosshairView(),
                "startup" => new StartupView(),
                "monitor" => new MonitorView(),
                "restore" => new RestoreView(),
                "logs" => new LogView(),
                "about" => new AboutView(),
                _ when tag.StartsWith("cat:") => new TweakListView(Enum.Parse<TweakCategory>(tag[4..])),
                _ => new DashboardView(),
            };
            _pages[tag] = page;
        }
        if (tag.StartsWith("cat:")) { CrumbRoot.Text = "Tweaks"; CrumbText.Text = CategoryInfo.DisplayName(Enum.Parse<TweakCategory>(tag[4..])); }
        else if (tag == "dashboard") { CrumbRoot.Text = "Overview"; CrumbText.Text = "Dashboard"; }
        else { CrumbRoot.Text = "Tools"; CrumbText.Text = Tools.FirstOrDefault(t => t.tag == tag).label ?? tag; }

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

    private IEnumerable<RadioButton> FindNavButtons() =>
        OverviewNav.Children.OfType<RadioButton>().Concat(CategoryNav.Children.OfType<RadioButton>()).Concat(ToolNav.Children.OfType<RadioButton>());

    public async Task RefreshAppliedSummaryAsync()
    {
        var count = await Task.Run(() => BackupStore.AllIds().Count());
        AppliedSummary.Text = count == 0 ? "No tweaks applied yet. Everything is reversible." : $"{count} tweak{(count == 1 ? "" : "s")} applied · all backed up";
    }

    // ═════ Toasts ═════
    /// <summary>Stacked notification. <paramref name="tone"/>: Accent, Success, Gold, Danger.</summary>
    public void ShowToast(string text, string tone = "Accent")
    {
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
        _paletteAll ??= BuildPaletteItems();
        Palette.Visibility = Visibility.Visible;
        PaletteBox.Text = "";
        PaletteFilter(null!, null!);
        Ui.Animate(Palette, OpacityProperty, 1, 140);
        PaletteShift.Y = App.ReduceMotion ? 0 : -12;
        Ui.Animate(PaletteShift, TranslateTransform.YProperty, 0, 220);
        PaletteBox.Focus();
    }

    private void ClosePalette(object? sender, RoutedEventArgs? e) => Palette.Visibility = Visibility.Collapsed;

    private List<PaletteItem> BuildPaletteItems()
    {
        var list = new List<PaletteItem> { new("Dashboard", "Overview, live metrics and quick actions", "\uE80F", "Page", () => Navigate("dashboard")) };
        foreach (var c in Enum.GetValues<TweakCategory>())
        {
            var cat = c;
            list.Add(new(CategoryInfo.DisplayName(c), CategoryInfo.Blurb(c), CategoryIcons[c], "Category", () => Navigate("cat:" + cat)));
        }
        foreach (var (tag, icon, label) in Tools)
        {
            var t = tag;
            list.Add(new(label, "Tool", icon, "Tool", () => Navigate(t)));
        }
        foreach (var t in TweakCatalog.All)
        {
            var tw = t;
            list.Add(new(t.Name, $"{CategoryInfo.DisplayName(t.Category)} · {(t.Risk == Risk.Caution ? "Caution" : "Safe")}", CategoryIcons[t.Category], "Tweak", () =>
            {
                Navigate("cat:" + tw.Category);
                if (Host.Content is TweakListView v) v.FocusTweak(tw.Name);
            }));
        }
        return list;
    }

    private void PaletteFilter(object sender, TextChangedEventArgs e)
    {
        if (_paletteAll is null) return;
        var q = PaletteBox.Text.Trim();
        IEnumerable<PaletteItem> hits = q.Length == 0
            ? _paletteAll.Where(i => i.Kind is "Page" or "Tool" or "Category")
            : _paletteAll.Select(i => (i, score: Score(i, q))).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.i);
        var shown = hits.Take(80).ToList();
        PaletteList.ItemsSource = shown;
        PaletteList.SelectedIndex = shown.Count > 0 ? 0 : -1;
        PaletteCount.Text = q.Length == 0 ? $"{_paletteAll.Count} items" : $"{shown.Count} result{(shown.Count == 1 ? "" : "s")}";
    }

    private static int Score(PaletteItem i, string q)
    {
        int s = 0;
        if (i.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) s += 100;
        else if (i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) s += 60;
        else if (i.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)) s += 20;
        else if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase))) s += 40;
        if (s > 0 && i.Kind != "Tweak") s += 15; // pages and tools first on ties
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
    private void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

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
            "presets", "potato", "booster", "input", "ping", "crosshair", "startup", "monitor", "restore", "about"];
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
