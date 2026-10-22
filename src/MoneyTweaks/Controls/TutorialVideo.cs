using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Web.WebView2.Wpf;
using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Controls;

/// <summary>
/// A page's video tutorial, embedded in the page like a web page's video: a 16:9 player area that shows the title and a
/// play button, and becomes YouTube's privacy-enhanced player (youtube-nocookie.com) when clicked. Nothing is loaded from
/// the internet until then. The player is WebView2's composition control, so it scrolls and clips with the page like any
/// other content. It's released when the page closes; "Pop out" plays the same video in its own window (with fullscreen).
/// </summary>
public sealed class TutorialVideo : StackPanel
{
    private readonly Tutorial _tutorial;
    private readonly string _videoId;
    private readonly Border _screen = new() { CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = Brushes.Black };
    private WebView2CompositionControl? _web;

    public TutorialVideo(Tutorial t)
    {
        _tutorial = t;
        _videoId = Tutorials.VideoId(t.Url) ?? "";
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        var pop = Ui.Btn("Pop out ⧉", (_, _) => { Stop(); TutorialPlayer.Play(t); }, "GhostButton");
        pop.ToolTip = "Play in its own window (with fullscreen)";
        var yt = Ui.Btn("Open on YouTube ↗", (_, _) => { Stop(); TutorialPlayer.OpenInBrowser(t.Url); }, "GhostButton");
        yt.ToolTip = t.Url;
        links.Children.Add(pop);
        links.Children.Add(yt);
        DockPanel.SetDock(links, Dock.Right);
        head.Children.Add(links);
        head.Children.Add(new TextBlock { Text = "Video tutorial", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(head);
        // 16:9 at whatever width the card gets, capped so it doesn't dominate wide windows.
        var frame = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        SizeChanged += (_, e) =>
        {
            var w = Math.Min(860, e.NewSize.Width);
            if (Math.Abs(frame.Width - w) < 0.5) return;
            frame.Width = w;
            _screen.Height = Math.Round(w * 9 / 16);
        };
        frame.Children.Add(_screen);
        Children.Add(frame);
        Children.Add(new TextBlock
        {
            Text = "Plays from YouTube (privacy-enhanced youtube-nocookie.com player). Nothing is loaded until you press play.",
            Style = Ui.Res<Style>("MutedText"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        ShowPoster();
        Unloaded += (_, _) => Stop(); // leaving the page stops the video and frees the player
    }

    /// <summary>Starts the video (also used by the page's "Watch tutorial" button).</summary>
    public async void Play()
    {
        if (_web is not null) return;
        _web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Black };
        _screen.Child = _web;
        try
        {
            await _web.EnsureCoreWebView2Async(await TutorialPlayer.SharedEnvironment());
            if (_web?.CoreWebView2 is not { } core) return; // stopped while starting
            TutorialPlayer.Load(core, _videoId, _tutorial.Title, fullscreen: false);
            Core.Sys.Log.Info($"Tutorial playing on the page: {_tutorial.Title} ({_tutorial.Url})");
        }
        catch (Exception ex)
        {
            Stop();
            TutorialPlayer.Unavailable(ex, _tutorial);
        }
    }

    private void Stop()
    {
        if (_web is null) return;
        var w = _web;
        _web = null;
        ShowPoster();
        w.Dispose();
    }

    /// <summary>Before playing: dark screen with the title and a YouTube-style play button.</summary>
    private void ShowPoster()
    {
        var poster = new Grid { Cursor = Cursors.Hand, Background = new LinearGradientBrush(Color.FromRgb(0x14, 0x1A, 0x28), Color.FromRgb(0x06, 0x08, 0x0C), 90), ToolTip = "Play" };
        poster.Children.Add(new TextBlock
        {
            Text = _tutorial.Title + " tutorial", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, 14, 18, 0), VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var button = new Grid { Width = 68, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var bg = new Rectangle { RadiusX = 14, RadiusY = 14, Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x21, 0x2B)) };
        button.Children.Add(bg);
        button.Children.Add(new Polygon { Points = new PointCollection { new(27, 15), new(27, 33), new(43, 24) }, Fill = Brushes.White });
        poster.Children.Add(button);
        poster.MouseEnter += (_, _) => bg.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
        poster.MouseLeave += (_, _) => bg.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x21, 0x2B));
        poster.MouseLeftButtonUp += (_, _) => Play();
        _screen.Child = poster;
    }
}
