using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Pocket;
using QRCoder;

namespace MoneyTweaks.Views;

/// <summary>
/// Pocket Panel: turn a phone or tablet browser on the same network into a live second screen. On/off, QR pairing with a
/// single-use expiring code, paired devices with revoke, and exactly what a phone can and can't do.
/// </summary>
public sealed class PocketPanelView : UserControl, IPageLifecycle
{
    private readonly StackPanel _status = new(), _pair = new(), _devices = new(), _decks = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _code;
    private DateTime _codeExpires;

    public PocketPanelView()
    {
        var (root, body) = Ui.Page("Pocket Panel",
            "Turn a phone or tablet browser into a control deck for this PC: your own dashboard pages with live CPU and GPU temperatures and usage, FPS and frame-time graphs, RAM, VRAM and warnings, plus touch buttons for the overlay, gaming profiles and more. Pages can open by themselves when a game starts. It runs on your home network only: no app to install, no account, no cloud.",
            eyebrow: "Tools", icon: "\uE8EA");
        body.Children.Add(Ui.Card(_status));
        body.Children.Add(Ui.Card(_pair));
        body.Children.Add(Ui.Card(_devices));
        body.Children.Add(Ui.Card(_decks));
        body.Children.Add(Ui.Card(SafetyCard()));
        Content = root;
        _tick.Tick += (_, _) => { RenderPairCountdown(); RenderStatus(); };
        Render();
    }

    public void OnShown() { PocketService.Changed += OnChanged; _tick.Start(); Render(); }
    public void OnHidden() { PocketService.Changed -= OnChanged; _tick.Stop(); }
    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void Render() { RenderStatus(); RenderPair(); RenderDevices(); RenderDecks(); }

    /// <summary>
    /// The dashboard pages every paired device shares. Built and arranged on the phone (Edit); here they can be renamed,
    /// reordered, removed or reset, and every connected phone updates within a second.
    /// </summary>
    private void RenderDecks()
    {
        _decks.Children.Clear();
        var head = new DockPanel();
        var reset = Ui.Btn("Reset to default", (_, _) =>
        {
            if (Dialog.Confirm("Reset the dashboards?", "Every page goes back to the default Gaming and System pages on all paired devices. Your own pages and widgets are removed.", "Reset", DialogKind.Warning))
                PocketService.Deck.Reset();
        }, "GhostButton");
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(new TextBlock { Text = "DASHBOARDS", Style = Ui.Res<Style>("Eyebrow"), VerticalAlignment = VerticalAlignment.Center });
        _decks.Children.Add(head);
        _decks.Children.Add(Ui.Muted("Shared by every paired phone and tablet. Build them on the device (Deck › Edit): drag widgets by their grip, resize them, add touch buttons, and set a page to open by itself while a game runs.", 12));
        var deck = PocketService.Deck.Get();
        for (int i = 0; i < deck.Pages.Count; i++)
        {
            var page = deck.Pages[i];
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var up = Ui.Btn("↑", (_, _) => EditDeck(d => { var x = d.Pages[index]; d.Pages.RemoveAt(index); d.Pages.Insert(index - 1, x); }), "GhostButton");
            up.IsEnabled = i > 0;
            var down = Ui.Btn("↓", (_, _) => EditDeck(d => { var x = d.Pages[index]; d.Pages.RemoveAt(index); d.Pages.Insert(index + 1, x); }), "GhostButton");
            down.IsEnabled = i < deck.Pages.Count - 1;
            var del = Ui.Btn("Delete", (_, _) =>
            {
                if (Dialog.Confirm($"Delete “{page.Name}”?", "The page and its widgets are removed from every paired device.", "Delete", DialogKind.Warning))
                    EditDeck(d => d.Pages.RemoveAt(index));
            }, "GhostButton");
            del.IsEnabled = deck.Pages.Count > 1;
            actions.Children.Add(up); actions.Children.Add(down); actions.Children.Add(del);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var name = new TextBox { Text = page.Name, MaxLength = 32, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Rename (press Enter)" };
            name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Rename(index, name.Text); };
            name.LostFocus += (_, _) => Rename(index, name.Text);
            var sp = new StackPanel();
            sp.Children.Add(name);
            sp.Children.Add(Ui.Muted($"{page.Widgets.Count} widget(s){(page.GameName is { } g ? $" · opens while {g} runs" : "")}", 11.5));
            row.Children.Add(sp);
            _decks.Children.Add(row);
        }
    }

    private void Rename(int index, string text)
    {
        var deck = PocketService.Deck.Get();
        if (index >= deck.Pages.Count || string.IsNullOrWhiteSpace(text) || deck.Pages[index].Name == text.Trim()) return;
        EditDeck(d => d.Pages[index].Name = text.Trim());
    }

    /// <summary>Changes a copy of the current dashboards and saves it through the same validated, versioned store the phones use.</summary>
    private static void EditDeck(Action<PocketDeck> change)
    {
        var current = PocketService.Deck.Get();
        var copy = System.Text.Json.JsonSerializer.Deserialize<PocketDeck>(System.Text.Json.JsonSerializer.Serialize(current))!;
        change(copy);
        var (ok, _, error) = PocketService.Deck.TrySave(current.Version, copy);
        if (!ok) MainWindow.Current?.ShowToast(error ?? "The dashboards weren't saved.", "Gold");
    }

    private void RenderStatus()
    {
        _status.Children.Clear();
        var sw = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = PocketService.Running, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => { PocketService.SetEnabled(sw.IsChecked == true); if (sw.IsChecked != true) _code = null; Render(); };
        var head = new DockPanel();
        DockPanel.SetDock(sw, Dock.Right);
        head.Children.Add(sw);
        var t = new StackPanel();
        t.Children.Add(Ui.Text("Pocket Panel", 16, FontWeights.SemiBold));
        string state = !PocketService.Running ? "Off. Nothing is listening for connections."
            : PocketService.Urls.Count == 0 ? "On, but no local network was found, so only this PC can open it."
            : $"On at {string.Join(" · ", PocketService.Urls)}{(PocketService.Clients > 0 ? $" · {PocketService.Clients} device(s) connected now" : "")}";
        t.Children.Add(Ui.Muted(state, 12.5));
        if (PocketService.LastError is { } err) t.Children.Add(new TextBlock { Text = err, Foreground = Ui.Brush("Gold"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(t);
        _status.Children.Add(head);
        if (PocketService.Running)
            _status.Children.Add(Ui.Muted("The first time, Windows may ask whether Money Tweaks may use the network. Allow it on Private networks only (your home Wi-Fi), not public ones.", 11.5));
    }

    private void RenderPair()
    {
        _pair.Children.Clear();
        _pair.Children.Add(new TextBlock { Text = "PAIR A PHONE OR TABLET", Style = Ui.Res<Style>("Eyebrow") });
        if (!PocketService.Running) { _pair.Children.Add(Ui.Muted("Turn Pocket Panel on above to pair a device.", 12.5)); return; }
        if (PocketService.Urls.Count == 0) { _pair.Children.Add(Ui.Muted("Connect this PC to your home network (Wi-Fi or cable) to pair a phone.", 12.5)); return; }
        if (_code is null || PocketService.Auth.PairingExpires is null)
        {
            _code = null;
            _pair.Children.Add(Ui.Muted("Shows a QR code for your phone's camera. The link inside works once and expires after 5 minutes; the phone must be on the same Wi-Fi as this PC.", 12.5));
            var b = Ui.Btn("Show pairing QR code", (_, _) =>
            {
                (_code, _codeExpires) = PocketService.Auth.StartPairing();
                RenderPair();
            }, "PrimaryButton", new Thickness(0, 10, 0, 0));
            b.HorizontalAlignment = HorizontalAlignment.Left;
            _pair.Children.Add(b);
            return;
        }
        var links = PocketService.PairingLinks(_code);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new Border { Child = new Image { Source = Qr(links[0]), Width = 220, Height = 220 }, Background = Brushes.White, Padding = new Thickness(8), Margin = new Thickness(0, 0, 18, 8) });
        var info = new StackPanel { MaxWidth = 520 };
        info.Children.Add(Ui.Text("Scan with your phone's camera", 14, FontWeights.SemiBold));
        info.Children.Add(Ui.Muted("Open the link it shows. The dashboard opens in the browser and stays paired on that device; add it to the home screen for one-tap access.", 12.5));
        info.Children.Add(new TextBlock { Name = "Countdown", FontSize = 12.5, Foreground = Ui.Brush("Gold"), Margin = new Thickness(0, 8, 0, 4) });
        info.Children.Add(Ui.Muted("Or type this address on the device (the code after # pairs it):", 11.5));
        foreach (var l in links)
            info.Children.Add(new TextBox { Text = l, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Ui.Brush("Text"), FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        var cancel = Ui.Btn("Cancel pairing", (_, _) => { PocketService.Auth.CancelPairing(); _code = null; RenderPair(); }, "GhostButton", new Thickness(0, 10, 0, 0));
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        info.Children.Add(cancel);
        row.Children.Add(info);
        _pair.Children.Add(row);
        RenderPairCountdown();
    }

    private void RenderPairCountdown()
    {
        if (_code is null) return;
        var left = _codeExpires - DateTime.UtcNow;
        if (left <= TimeSpan.Zero || PocketService.Auth.PairingExpires is null)
        {
            bool paired = PocketService.Auth.PairingExpires is null && left > TimeSpan.Zero;
            _code = null;
            RenderPair();
            if (paired) MainWindow.Current?.ShowToast("Device paired with Pocket Panel.", "Success");
            return;
        }
        foreach (var tb in Ui.Descendants<TextBlock>(_pair).Where(t => t.Name == "Countdown"))
            tb.Text = $"Code expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    /// <summary>
    /// The pairing QR code: black square modules on white with the Money Tweaks logo in the middle. Error correction is at
    /// the highest level (H, about 30% of the code can be missing) so the logo, about 6% of the area, never stops a scan.
    /// </summary>
    private static BitmapSource Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.H);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var code = new BitmapImage();
        code.BeginInit();
        code.StreamSource = new MemoryStream(png);
        code.CacheOption = BitmapCacheOption.OnLoad;
        code.EndInit();
        code.Freeze();

        var logo = new BitmapImage(new Uri("pack://application:,,,/MoneyTweaks;component/Assets/logo.png"));
        int w = code.PixelWidth, h = code.PixelHeight;
        double size = Math.Round(w * 0.25);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(code, new Rect(0, 0, w, h));
            dc.DrawImage(logo, new Rect((w - size) / 2, (h - size) / 2, size, size));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private void RenderDevices()
    {
        _devices.Children.Clear();
        var head = new DockPanel();
        var devices = PocketService.Auth.Devices.Where(d => d.ExpiresUtc > DateTime.UtcNow).OrderByDescending(d => d.LastSeenUtc).ToList();
        if (devices.Count > 0)
        {
            var all = Ui.Btn("Unpair all", (_, _) =>
            {
                if (Dialog.Confirm("Unpair all devices?", "Every phone and tablet loses access right away and has to scan a new QR code to connect again.", "Unpair all", DialogKind.Warning))
                    PocketService.Auth.RevokeAll();
            }, "GhostButton");
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(all);
        }
        head.Children.Add(new TextBlock { Text = "PAIRED DEVICES", Style = Ui.Res<Style>("Eyebrow"), VerticalAlignment = VerticalAlignment.Center });
        _devices.Children.Add(head);
        if (devices.Count == 0) { _devices.Children.Add(Ui.Muted("None yet.", 12.5)); return; }
        foreach (var d in devices)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var revoke = Ui.Btn("Unpair", (_, _) => PocketService.Auth.Revoke(d.Id), "GhostButton");
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(d.Name, 13.5, FontWeights.SemiBold));
            sp.Children.Add(Ui.Muted($"Last seen {d.LastSeenUtc.ToLocalTime():g} from {d.LastAddress} · paired {d.PairedUtc.ToLocalTime():d} · access ends {d.ExpiresUtc.ToLocalTime():d} unless you unpair it sooner", 11.5));
            row.Children.Add(sp);
            _devices.Children.Add(row);
        }
    }

    private static UIElement SafetyCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "WHAT A PAIRED DEVICE CAN DO", Style = Ui.Res<Style>("Eyebrow") });
        foreach (var line in new[]
        {
            "See live readings: CPU and GPU usage and temperatures, FPS while the overlay measures a game, RAM, VRAM and Money Tweaks' warnings.",
            "Control the performance overlay: on or off, auto-show for games, click-through, which readings it shows, and its existing styles and themes. Open or close the compact monitor window.",
            "Switch between your gaming profiles or turn the active one off (same backup and restore as automatic switching; profiles that need administrator rights are refused unless Money Tweaks runs as administrator).",
            "Open Test & Benchmark on the PC; benchmarks are started and confirmed on the PC, never from the phone.",
            "Arrange the shared dashboard pages. Nothing else: it can't run commands, open files, apply tweaks or change any other setting. Requests for anything not on this list are refused and logged.",
        }) sp.Children.Add(new TextBlock { Text = "• " + line, FontSize = 12.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        sp.Children.Add(new TextBlock { Text = "HOW IT STAYS LOCAL AND PRIVATE", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 12, 0, 0) });
        foreach (var line in new[]
        {
            "Only listens on this PC's home-network addresses and refuses connections from anywhere else; no ports are opened on your router and nothing goes through the internet.",
            "Every request needs the device's own session key. Money Tweaks stores only a fingerprint (hash) of it; access ends after 30 days or when you unpair.",
            "The page uses plain HTTP on your network (there's no certificate for a home PC), so use it on your own Wi-Fi, not on public networks.",
        }) sp.Children.Add(new TextBlock { Text = "• " + line, FontSize = 12.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        return sp;
    }
}
