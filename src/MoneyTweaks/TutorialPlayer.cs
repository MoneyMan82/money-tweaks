using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>
/// Plays a video tutorial inside Money Tweaks: YouTube's privacy-enhanced embedded player (youtube-nocookie.com) in a
/// WebView2 window. The player page is served from a local folder under a private host name so YouTube gets the referrer
/// its embeds require. Only the player can load; links that leave it open in the default browser. Without the WebView2
/// runtime (or if it fails), the video opens in the browser instead.
/// </summary>
public sealed class TutorialPlayer : Window
{
    private const string Host = "player.moneytweaks.local";
    private static TutorialPlayer? _open;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    private readonly Tutorial _tutorial;
    private readonly string _videoId;

    public static void Play(Tutorial t)
    {
        if (Tutorials.VideoId(t.Url) is not { } id) { OpenInBrowser(t.Url); return; }
        if (_open is { } w) { w.Close(); }
        _open = new TutorialPlayer(t, id);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Show("Couldn't open your browser", $"{ex.Message}\n\nThe video is at: {url}", DialogKind.Error); }
    }

    private TutorialPlayer(Tutorial t, string videoId)
    {
        _tutorial = t;
        _videoId = videoId;
        Title = $"Tutorial: {t.Title} · Money Tweaks";
        Width = 1040; Height = 660; MinWidth = 480; MinHeight = 320;
        Background = (Brush)FindResource("Bg");
        Foreground = (Brush)FindResource("Text");
        Owner = Application.Current.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        var open = new Button { Content = "Open on YouTube ↗", Style = (Style)FindResource("GhostButton"), ToolTip = t.Url };
        open.Click += (_, _) => OpenInBrowser(t.Url);
        DockPanel.SetDock(open, Dock.Right);
        bar.Children.Add(open);
        bar.Children.Add(new TextBlock { Text = $"{t.Title} tutorial", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Border { Background = Brushes.Black, Child = _web });
        Content = root;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _web.Dispose();
    }

    private async Task StartAsync()
    {
        try
        {
            // The default data folder is next to the exe, which isn't writable in Program Files.
            var data = Path.Combine(AppPaths.Root, "webview2");
            var page = Path.Combine(data, "player");
            Directory.CreateDirectory(page);
            File.WriteAllText(Path.Combine(page, "index.html"), PlayerHtml(_videoId, _tutorial.Title));
            // Pressing "Watch tutorial" is the user's go-ahead, so the video starts without a second click.
            var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, page, CoreWebView2HostResourceAccessKind.DenyCors);
            // The window only ever shows the player page; anything else goes to the default browser.
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                if (IsYouTube(e.Uri)) OpenInBrowser(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (IsYouTube(e.Uri)) OpenInBrowser(e.Uri); };
            // The player's fullscreen button fills the screen with this window.
            var (style, state) = (WindowStyle, WindowState);
            core.ContainsFullScreenElementChanged += (_, _) =>
            {
                if (core.ContainsFullScreenElement) { (style, state) = (WindowStyle, WindowState); WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; ((DockPanel)Content).Children[0].Visibility = Visibility.Collapsed; }
                else { WindowStyle = style; WindowState = state; ((DockPanel)Content).Children[0].Visibility = Visibility.Visible; }
            };
            core.Navigate($"https://{Host}/index.html");
            Log.Info($"Tutorial playing in the app: {_tutorial.Title} ({_tutorial.Url})");
        }
        catch (Exception ex)
        {
            Log.Warn($"In-app video player unavailable ({ex.GetType().Name}: {ex.Message}); opening the browser instead");
            Close();
            OpenInBrowser(_tutorial.Url);
            MainWindow.Current?.ShowToast("The in-app player isn't available on this PC (it needs Microsoft Edge WebView2), so the video opened in your browser.", "Gold");
        }
    }

    private static bool IsYouTube(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host is "youtu.be" or "youtube.com" or "www.youtube.com" or "m.youtube.com" or "www.youtube-nocookie.com");

    private static string PlayerHtml(string id, string title) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin">
        <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
        <style>html,body{margin:0;height:100%;background:#000;overflow:hidden}iframe{border:0;width:100%;height:100%}</style></head>
        <body><iframe src="https://www.youtube-nocookie.com/embed/{{id}}?autoplay=1&rel=0&modestbranding=1&playsinline=1"
          title="{{System.Net.WebUtility.HtmlEncode(title)}}" referrerpolicy="strict-origin-when-cross-origin"
          allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen></iframe></body></html>
        """;
}
