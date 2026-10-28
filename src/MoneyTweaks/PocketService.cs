using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Pocket;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>
/// Pocket Panel in the app: runs <see cref="PocketServer"/> when turned on, feeds it the sensor hub's readings and the
/// overlay's frame timing, and runs the allowlisted overlay actions on the UI thread. Off by default.
/// </summary>
public static class PocketService
{
    private static PocketSettings S => App.Settings.Pocket;
    private static PocketAuth? _auth;
    private static PocketDeckStore? _deck;
    private static PocketServer? _server;
    private static readonly Host TheHost = new();
    public static event Action? Changed;

    public static PocketAuth Auth => _auth ??= CreateAuth();
    /// <summary>The dashboards shared by every paired device; edits from phones or the PC page reach all of them live.</summary>
    public static PocketDeckStore Deck => _deck ??= CreateDeck();

    private static PocketDeckStore CreateDeck()
    {
        var d = new PocketDeckStore(S);
        d.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() => { App.SaveSettingsSoon(); Changed?.Invoke(); });
        return d;
    }
    public static bool Running => _server?.Running == true;
    public static IReadOnlyList<string> Urls => _server?.Urls ?? [];
    public static int Clients => _server?.Clients ?? 0;
    public static string? LastError => _server?.LastError;

    private static PocketAuth CreateAuth()
    {
        var a = new PocketAuth(S);
        a.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() => { App.SaveSettingsSoon(); Changed?.Invoke(); });
        return a;
    }

    public static void Init()
    {
        if (S.Enabled && !App.ScreenshotMode) Start();
    }

    /// <param name="only">Diagnostics/tests: listen on these local addresses only (e.g. loopback) instead of every local network.</param>
    public static bool Start(IEnumerable<System.Net.IPAddress>? only = null)
    {
        _server ??= new PocketServer(Auth, TheHost, Deck);
        bool ok = _server.Start(Math.Clamp(S.Port, 1024, 65535), only);
        if (ok && _server.Urls.Count == 0) Log.Warn("Pocket Panel: no local network adapter found; only this PC can connect");
        Changed?.Invoke();
        return ok;
    }

    public static void Stop()
    {
        _server?.Stop();
        Auth.CancelPairing();
        Changed?.Invoke();
    }

    public static void SetEnabled(bool on)
    {
        S.Enabled = on;
        App.SaveSettingsSoon();
        if (on) Start(); else Stop();
    }

    public static void Shutdown() => _server?.Dispose();

    /// <summary>Links that pair a device: one per local address, the single-use code in the fragment (never sent in a request line).</summary>
    public static IReadOnlyList<string> PairingLinks(string code) => Urls.Select(u => u + "#pair=" + code).ToList();

    // ---- what the phone sees and may do -----------------------------------------------------------------------------

    private sealed class Host : IPocketHost
    {
        private static readonly object HubToken = new();
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public event Action? FrameReady;

        public Host() => SensorHub.Shared.Sampled += _ => FrameReady?.Invoke();

        private CancellationTokenSource? _gameWatch;
        private volatile PocketGame? _game;

        public void SetActive(bool active)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (active) SensorHub.Shared.Acquire(HubToken, record: false);
                else SensorHub.Shared.Release(HubToken);
                Changed?.Invoke();
            });
            if (active && _gameWatch is null) { _gameWatch = new CancellationTokenSource(); _ = WatchGames(_gameWatch.Token); }
            else if (!active) { _gameWatch?.Cancel(); _gameWatch = null; _game = null; }
        }

        /// <summary>
        /// Which Game Library game is running, every 5 s while a phone is connected: the same detection the library uses
        /// (the game's real executable from its install), so a launcher or a renamed exe doesn't count.
        /// </summary>
        private async Task WatchGames(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var running = GameRuntime.Statuses(GameLibrary.Items(GameLibrary.Current)).FirstOrDefault(x => x.status == GameStatus.Running);
                    var now = running.game is { } g ? new PocketGame(g.Key, g.Def.Name) : null;
                    if (now?.Key != _game?.Key) { _game = now; Log.Info($"Pocket Panel: game {(now is null ? "closed" : "detected: " + now.Name)}"); FrameReady?.Invoke(); }
                }
                catch (Exception ex) { Log.Debug("Pocket Panel game check: " + ex.Message); }
                try { await Task.Delay(5000, ct); } catch { return; }
            }
        }

        public IReadOnlyList<PocketGame> Games()
        {
            try { return GameLibrary.Items(GameLibrary.Current).Select(g => new PocketGame(g.Key, g.Def.Name)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
            catch { return []; }
        }

        public string FrameJson()
        {
            var hub = SensorHub.Shared;
            var f = hub.Last;
            double? V(string id) => f?.Values.GetValueOrDefault(id);
            string? Why(MetricGroup g, string name) => hub.Unsupported.FirstOrDefault(u => u.Group == g && u.Label == name)?.Reason;

            // The GPU doing the most work (the game's GPU while playing), as the overlay picks it.
            int gpu = -1; double best = -1;
            for (int i = 0; i < 8; i++) if (V($"gpu{i}.load") is { } l && l > best) { best = l; gpu = i; }
            var (gload, gtemp, vram, vramTotal) = OverlayService.BusiestGpu(f);

            bool shown = OverlayService.Shown;
            var lf = shown ? OverlayService.LastFrame : null;
            var fr = lf?.Frames;
            string? fpsNote = !shown ? "Turn on \"Overlay on screen\" in Controls to measure FPS in your game."
                : fr is null ? lf?.CaptureNote ?? "Waiting for a game to draw frames." : null;

            var o = App.Settings.Overlay.Layout;
            var frame = new
            {
                t = DateTime.UtcNow,
                cpu = new
                {
                    load = V("cpu.load") ?? f?.Snapshot.CpuPercent,
                    temp = V("cpu.temp"),
                    tempSource = hub.Find("cpu.temp")?.Source,
                    tempNote = V("cpu.temp") is null ? Why(MetricGroup.Cpu, "Temperature") ?? "Not available on this PC." : null,
                    name = hub.Find("cpu.load")?.Device,
                },
                gpu = new
                {
                    load = gload, temp = gtemp, vramUsedMb = vram, vramTotalMb = vramTotal,
                    name = gpu >= 0 ? hub.Find($"gpu{gpu}.load")?.Device : null,
                    tempNote = gtemp is null ? "This GPU doesn't report a temperature here." : null,
                    vramNote = vram is null ? "VRAM use isn't reported for this GPU." : null,
                },
                ram = new { usedGb = f?.Snapshot.RamUsedGb, totalGb = f?.Snapshot.RamTotalGb },
                fps = new { fps = fr?.Fps, low1 = fr?.Low1PercentFps, frameMs = fr?.AvgMs, target = fr is null ? null : lf?.Target, note = fpsNote },
                alerts = (f?.Alerts ?? []).Select(a => new { key = a.Key, level = a.Level.ToString(), title = a.Title, detail = a.Detail }),
                overlay = new { visible = shown, style = o.Style.ToString(), theme = o.EffectiveTheme, measuring = shown ? lf?.Target : null },
                // The overlay's own frame-time samples (ms, oldest first) while it measures a game.
                frametimes = shown ? lf?.GraphMs?.TakeLast(120).Select(v => Math.Round(v, 2)) : null,
                game = _game,
                profile = ProfileService.Active?.ProfileName,
                // Current state of every control, so switches on the phone follow changes made on the PC.
                controls = Actions(),
            };
            return JsonSerializer.Serialize(frame, Json);
        }

        private static readonly string[] Styles = Enum.GetNames<OverlayStyle>();

        /// <summary>
        /// The whole allowlist, all existing overlay and monitor settings that are easy to change back: the overlay on or off,
        /// auto-show for games, click-through, which readings it shows, its style and theme presets, and the compact monitor
        /// window. Called from the server thread, so it only reads settings and thread-safe flags.
        /// </summary>
        public IReadOnlyList<PocketAction> Actions()
        {
            var ov = App.Settings.Overlay;
            var o = ov.Layout;
            string[] items;
            try { items = [.. o.EffectiveItems()]; } catch (InvalidOperationException) { items = []; }   // changed mid-read: next frame has it
            var list = new List<PocketAction>
            {
                new("overlay.visible", "Overlay on screen", "Overlay", OverlayService.Shown, "switch", "FPS and readings over your game (measures FPS while on)"),
                new("overlay.auto", "Show automatically for games", "Overlay", ov.AutoMonitor, "switch", "Turns the overlay on when a game from your overlay list starts"),
                new("overlay.clickthrough", "Click-through", "Overlay", ov.ClickThrough, "switch", "Clicks pass through the overlay to the game"),
            };
            list.AddRange(Styles.Select(st => new PocketAction("overlay.style." + st, st, "Overlay style", o.Style.ToString() == st, "choice")));
            list.AddRange(OverlayTheme.All.Select(t => new PocketAction("overlay.theme." + t.Name, t.Name, "Overlay theme", o.EffectiveTheme == t.Name, "choice")));
            list.AddRange(OverlayMetric.All.Select(m => new PocketAction("overlay.item." + m.Id, m.Label, "Readings on the overlay", items.Contains(m.Id), "switch", m.Hint)));
            list.Add(new("monitor.compact", "Compact monitor window", "On the PC", CompactMonitorWindow.IsOpen, "switch", "Small always-on-top readings window on the PC's screen"));
            // Gaming profiles: the user's own, switched with the same backup-and-restore engine as automatic switching.
            var active = ProfileService.Active?.ProfileId;
            list.Add(new("profile.off", "Off", "Gaming profile", active is null, "choice", "Restores everything the active profile changed"));
            foreach (var p in ProfileService.File.Profiles.ToList())
                list.Add(new("profile.on." + p.Id, p.Name, "Gaming profile", active == p.Id, "choice"));
            list.Add(new("benchmark.open", "Open benchmark on PC", "On the PC", false, "button", "Brings Money Tweaks to the front on Test & Benchmark; you start and confirm it there"));
            return list;
        }

        public (bool ok, string message) Run(string id)
        {
            var d = Application.Current?.Dispatcher;
            if (d is null) return (false, "Money Tweaks is closing.");
            return d.Invoke(() => RunOnUi(id));
        }

        private static (bool ok, string message) RunOnUi(string id)
        {
            var ov = App.Settings.Overlay;
            var o = ov.Layout;
            switch (id)
            {
                case "overlay.visible":
                    if (OverlayService.Visible) OverlayService.Hide(); else OverlayService.Show();
                    Changed?.Invoke();
                    return (true, OverlayService.Visible ? "Overlay on." : "Overlay off.");
                case "overlay.auto":
                    ov.AutoMonitor = !ov.AutoMonitor;
                    if (ov.AutoMonitor) OverlayService.StartWatcher(); else OverlayService.StopWatcher();
                    App.SaveSettingsSoon();
                    Changed?.Invoke();
                    return (true, ov.AutoMonitor ? "The overlay now turns on when a listed game starts." : "Auto-show is off.");
                case "overlay.clickthrough":
                    OverlayService.SetClickThrough(!ov.ClickThrough);
                    return (true, ov.ClickThrough ? "Clicks pass through the overlay." : "The overlay can be dragged; it still never takes focus.");
                case "profile.off":
                    if (ProfileService.Active is null) return (true, "No profile is active.");
                    _ = ProfileService.DeactivateAsync();
                    return (true, "Switching the profile off; your settings are being restored.");
                case var pid when pid.StartsWith("profile.on.", StringComparison.Ordinal) && ProfileService.File.Profiles.FirstOrDefault(x => x.Id == pid[11..]) is { } prof:
                    if (ProfileService.Active?.ProfileId == prof.Id) return (true, $"“{prof.Name}” is already active.");
                    if (ProfileEngine.NeedsAdmin(prof) && !Core.Sys.Admin.IsElevated)
                        return (false, $"“{prof.Name}” changes system-wide settings, so it needs Money Tweaks running as administrator. Activate it on the PC.");
                    _ = ProfileService.ActivateAsync(prof, "Pocket Panel", confirm: false);
                    return (true, $"Activating “{prof.Name}”. Everything is backed up first and restored when you switch it off.");
                case "benchmark.open":
                    if (MainWindow.Current is not { } win) return (false, "The Money Tweaks window isn't open.");
                    if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                    win.Show();
                    win.Activate();
                    win.Navigate("benchmark");
                    return (true, "Test & Benchmark is open on the PC. Choose what to test and start it there.");
                case "monitor.compact":
                    CompactMonitorWindow.Toggle();
                    return (true, CompactMonitorWindow.IsOpen ? "Compact monitor window open on the PC." : "Compact monitor window closed.");
                case var m when m.StartsWith("overlay.item.", StringComparison.Ordinal) && OverlayMetric.All.Any(x => x.Id == m[13..]):
                    o.SetItem(m[13..], !o.EffectiveItems().Contains(m[13..]));
                    break;
                case var st when st.StartsWith("overlay.style.", StringComparison.Ordinal) && Enum.TryParse<OverlayStyle>(st[14..], out var style):
                    o.Style = style;
                    break;
                case var t when t.StartsWith("overlay.theme.", StringComparison.Ordinal) && OverlayTheme.All.Any(p => p.Name == t[14..]):
                    o.Theme = t[14..];
                    break;
                default: return (false, "That action isn't allowed.");
            }
            App.SaveSettingsSoon();
            OverlayService.LayoutChanged();
            Changed?.Invoke();
            return (true, "Saved for the default overlay layout (games with their own layout keep theirs).");
        }
    }
}
