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
    private readonly StackPanel _status = new(), _pair = new(), _devices = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _code;
    private DateTime _codeExpires;

    public PocketPanelView()
    {
        var (root, body) = Ui.Page("Pocket Panel",
            "Turn a phone or tablet browser into a live second screen for this PC: CPU and GPU temperatures and usage, FPS, RAM and VRAM with animated graphs and alerts, plus controls for the performance overlay. It runs on your home network only: no app to install, no account, no cloud.",
            eyebrow: "Tools", icon: "\uE8EA");
        body.Children.Add(Ui.Card(_status));
        body.Children.Add(Ui.Card(_pair));
        body.Children.Add(Ui.Card(_devices));
        body.Children.Add(Ui.Card(SafetyCard()));
        Content = root;
        _tick.Tick += (_, _) => { RenderPairCountdown(); RenderStatus(); };
        Render();
    }

    public void OnShown() { PocketService.Changed += OnChanged; _tick.Start(); Render(); }
    public void OnHidden() { PocketService.Changed -= OnChanged; _tick.Stop(); }
    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void Render() { RenderStatus(); RenderPair(); RenderDevices(); }

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

    private static BitmapImage Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
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
            "Turn the performance overlay on or off, and switch its default layout between the existing styles (Minimal, Compact, Detailed) and themes (Vector, Mint, Ice, Amber, Mono).",
            "Nothing else: it can't run commands, open files, apply tweaks or change any other setting. Requests for anything not on this list are refused and logged.",
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
