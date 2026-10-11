using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;
using MoneyTweaks.Views;

namespace MoneyTweaks;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    public static MainWindow? Current { get; private set; }

    private static readonly (string tag, string icon, string label)[] Tools =
    [
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

    private static readonly Dictionary<TweakCategory, string> CategoryIcons = new()
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
        foreach (var c in Enum.GetValues<TweakCategory>())
            CategoryNav.Children.Add(NavItem("cat:" + c, CategoryIcons[c], CategoryInfo.DisplayName(c), TweakCatalog.InCategory(c).Count().ToString()));
        foreach (var (tag, icon, label) in Tools) ToolNav.Children.Add(NavItem(tag, icon, label, null));

        bool admin = Admin.IsElevated;
        AdminBanner.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        AdminText.Text = admin ? "Administrator" : "Standard user";
        AdminDot.Fill = (Brush)FindResource(admin ? "Accent" : "Gold");
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        TweakService.Changed += () => _ = RefreshAppliedSummaryAsync();
        Navigate("dashboard");
        Loaded += async (_, _) =>
        {
            await RefreshAppliedSummaryAsync();
            if (!App.ScreenshotMode) Updater.VerifyPendingUpdate();
            if (App.Settings.AutoCheckUpdates && !App.ScreenshotMode) await Updater.CheckAsync(manual: false);
        };
    }

    private RadioButton NavItem(string tag, string icon, string label, string? count)
    {
        var fg = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) };
        var panel = new DockPanel();
        if (count is not null)
        {
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = count, FontSize = 11, Foreground = (Brush)FindResource("Muted") } };
            DockPanel.SetDock(badge, Dock.Right);
            panel.Children.Add(badge);
        }
        var ic = new TextBlock { Text = icon, Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 12, 0) };
        ic.SetBinding(TextBlock.ForegroundProperty, fg);
        var tx = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        tx.SetBinding(TextBlock.ForegroundProperty, fg);
        panel.Children.Add(ic);
        panel.Children.Add(tx);
        var rb = new RadioButton { Style = (Style)FindResource("NavButton"), Tag = tag, Content = panel };
        rb.Checked += Nav_Checked;
        return rb;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && IsLoaded) Navigate(tag);
    }

    public void Navigate(string tag)
    {
        // Keep the sidebar selection in sync when navigation comes from code (e.g. dashboard tiles).
        foreach (var rb in FindNavButtons()) if ((string)rb.Tag == tag && rb.IsChecked != true) { rb.IsChecked = true; return; }

        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "dashboard" => new DashboardView(),
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
        CrumbText.Text = tag.StartsWith("cat:") ? "Tweaks  ›  " + CategoryInfo.DisplayName(Enum.Parse<TweakCategory>(tag[4..]))
            : Tools.FirstOrDefault(t => t.tag == tag).label ?? "Dashboard";
        Host.Content = page;
        page.Opacity = 0;
        page.RenderTransform = new TranslateTransform(0, 8);
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (page is IPageLifecycle lc) lc.OnShown();
        foreach (var other in _pages.Values.Where(p => p != page).OfType<IPageLifecycle>()) other.OnHidden();
    }

    private IEnumerable<RadioButton> FindNavButtons() =>
        Nav.Children.OfType<RadioButton>().Concat(CategoryNav.Children.OfType<RadioButton>()).Concat(ToolNav.Children.OfType<RadioButton>());

    public async Task RefreshAppliedSummaryAsync()
    {
        var count = await Task.Run(() => BackupStore.AllIds().Count());
        AppliedSummary.Text = count == 0 ? "No tweaks applied yet. Everything is reversible." : $"{count} tweak{(count == 1 ? "" : "s")} applied with backups.";
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2800))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3300))));
        Toast.BeginAnimation(OpacityProperty, a);
    }

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

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Admin.RestartElevated()) Application.Current.Shutdown();
        else ShowToast("Elevation was cancelled.");
    }

    private void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Borderless windows overhang the screen when maximized; pad to keep content visible.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    /// <summary>Renders every page to PNG (used to produce the real screenshots on the website).</summary>
    public async Task CaptureScreenshotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        string[] tags = ["dashboard", "cat:General", "cat:Debloating", "cat:Networking", "cat:Advanced", "cat:Services", "cat:AppOptimizer", "cat:AppRemoval", "cat:GameProfiles", "cat:Bios",
            "potato", "booster", "input", "ping", "crosshair", "startup", "monitor", "restore", "about"];
        foreach (var tag in tags)
        {
            Navigate(tag);
            await Task.Delay(tag is "dashboard" or "monitor" or "ping" ? 6000 : 3500);
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            await using var fs = File.Create(Path.Combine(dir, tag.Replace("cat:", "cat-").ToLowerInvariant() + ".png"));
            enc.Save(fs);
            if (tag == "potato" && Host.Content is UserControl { Content: ScrollViewer sv })
            {
                // Second shot of the controls below the preview.
                sv.ScrollToVerticalOffset(640);
                await Task.Delay(1200);
                var rtb2 = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                rtb2.Render(Root);
                var enc2 = new PngBitmapEncoder();
                enc2.Frames.Add(BitmapFrame.Create(rtb2));
                await using var fs2 = File.Create(Path.Combine(dir, "potato-controls.png"));
                enc2.Save(fs2);
                sv.ScrollToVerticalOffset(0);
                if (Host.Content is PotatoView pv)
                {
                    foreach (var preset in new[] { Core.Features.FnPreset.Competitive, Core.Features.FnPreset.Potato, Core.Features.FnPreset.UltraPotato })
                    {
                        pv.PreviewPresetOnly(preset);
                        await Task.Delay(1200);
                        var r = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        r.Render(Root);
                        var e = new PngBitmapEncoder();
                        e.Frames.Add(BitmapFrame.Create(r));
                        await using var f = File.Create(Path.Combine(dir, $"potato-preset-{preset.ToString().ToLowerInvariant()}.png"));
                        e.Save(f);
                    }
                    pv.PreviewPresetOnly(null);
                }
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
