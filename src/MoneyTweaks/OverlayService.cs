using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Views;

namespace MoneyTweaks;

/// <summary>
/// Live performance overlay runtime. FPS, frame time and 1% lows come from <see cref="PresentMonitor"/> (ETW Present events);
/// CPU, GPU, RAM, VRAM and temperatures from the shared <see cref="SensorHub"/>, so nothing is measured twice; ping from
/// <see cref="OverlayPing"/>. The ETW session, sensor sampling and ping only run while the overlay is visible. The game
/// being measured picks its saved layout automatically; games in the overlay's list can show it while they run.
/// </summary>
public static class OverlayService
{
    private const int HotkeyId = 0x4F56, EditHotkeyId = 0x4F57;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);

    private static OverlaySettings S => App.Settings.Overlay;
    private static PerformanceOverlayWindow? _win;
    private static readonly DispatcherTimer Refresh = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly object HubToken = new();
    private static nint _hwnd;
    private static bool _hookAdded, _hubHeld;
    private static CancellationTokenSource? _watch;
    private static readonly Dictionary<int, string> Names = [];
    /// <summary>Test runs only: keep the overlay window off screen (it is still created, shown and updated for real).</summary>
    private static bool _offscreen;
    private static OverlayLayout? _applied;
    private static int _layoutVersion, _appliedVersion = -1;

    /// <summary>The game being monitored automatically (exe and process), or null.</summary>
    private static (string exe, int pid)? _autoGame;
    /// <summary>The user hid the overlay while this game ran; don't show it again until the game closes.</summary>
    private static string? _suppressed;
    private static int _missing;

    public static bool Visible => _win?.IsVisible == true;
    public static bool Editing => _win?.EditMode == true;
    public static string HotkeyStatus { get; private set; } = "Not registered yet.";
    public static string EditHotkeyStatus { get; private set; } = "Not registered yet.";
    public static bool HotkeyRegistered { get; private set; }
    public static OverlayFrame? LastFrame { get; private set; }
    /// <summary>Exe of the game whose layout is showing, or null for the default layout.</summary>
    public static string? ActiveProfile { get; private set; }
    public static event Action? Changed;

    public static void Init(Window main)
    {
        _hwnd = new WindowInteropHelper(main).Handle;
        if (!_hookAdded && HwndSource.FromHwnd(_hwnd) is { } src)
        {
            src.AddHook((nint _, int msg, nint wp, nint _, ref bool handled) =>
            {
                if (msg == 0x0312 && wp == HotkeyId) { Toggle(); handled = true; }
                if (msg == 0x0312 && wp == EditHotkeyId) { SetEditMode(!Editing); handled = true; }
                return 0;
            });
            _hookAdded = true;
        }
        S.EnsureDefaultGames();
        RegisterHotkey();
        RegisterEditHotkey();
        Refresh.Tick += (_, _) => Tick();
        if (S.AutoMonitor) StartWatcher();
    }

    private static (bool ok, string status) Register(int id, string text, string what)
    {
        if (_hwnd == 0) return (false, "Not registered yet.");
        UnregisterHotKey(_hwnd, id);
        if (string.IsNullOrWhiteSpace(text)) return (false, $"No hotkey to {what}.");
        if (!OverlayHotkey.TryParse(text, out var mods, out var vk)) return (false, $"\"{text}\" isn't a valid hotkey.");
        var ok = RegisterHotKey(_hwnd, id, mods | OverlayHotkey.ModNoRepeat, (uint)vk);
        if (!ok) Log.Warn($"Overlay hotkey {text} not registered (in use by another app)");
        return (ok, ok ? $"{text} {what} (works while a game has focus)." : $"{text} is already used by another app; choose another.");
    }

    /// <summary>(Re)registers the show/hide hotkey from settings. Returns false if it's invalid or taken by another app.</summary>
    public static bool RegisterHotkey()
    {
        var (ok, status) = Register(HotkeyId, S.Hotkey, "shows/hides the overlay");
        HotkeyRegistered = ok;
        HotkeyStatus = status;
        Changed?.Invoke();
        return ok;
    }

    /// <summary>(Re)registers the move/resize hotkey. An empty hotkey simply leaves it unregistered.</summary>
    public static bool RegisterEditHotkey()
    {
        var (ok, status) = Register(EditHotkeyId, S.EditHotkey, "toggles move & resize mode");
        EditHotkeyStatus = status;
        Changed?.Invoke();
        return ok || string.IsNullOrWhiteSpace(S.EditHotkey);
    }

    public static void Toggle()
    {
        if (Visible)
        {
            if (_autoGame is { } g) _suppressed = g.exe;
            Hide();
        }
        else Show();
    }

    public static void Show()
    {
        if (_win is null)
        {
            _win = new PerformanceOverlayWindow();
            _win.Closed += (_, _) => _win = null;
            _win.Moved += OnMoved;
            _win.Scaled += OnScaled;
            _win.EditFinished += reset =>
            {
                if (reset && _applied is { } l) { l.CustomPosition = false; l.Monitor = null; App.SaveSettingsSoon(); }
                SetEditMode(false);
            };
            _appliedVersion = -1;
            if (_offscreen) { _win.Left = -6000; _win.Top = -6000; }
        }
        _win.SetClickThrough(S.ClickThrough);
        var state = PresentMonitor.Start();
        if (!_hubHeld) { SensorHub.Shared.Acquire(HubToken, record: false); _hubHeld = true; }
        _win.Show();
        Refresh.Interval = TimeSpan.FromMilliseconds(Math.Clamp(S.RefreshMs, 100, 2000));
        Refresh.Start();
        Tick();
        Log.Info($"Performance overlay shown (frame timing: {state}{(_autoGame is { } a ? ", game " + a.exe : "")})");
        Changed?.Invoke();
    }

    public static void Hide()
    {
        Refresh.Stop();
        if (_win is { EditMode: true }) _win.SetEditMode(false);
        _win?.Hide();
        if (_hubHeld) { SensorHub.Shared.Release(HubToken); _hubHeld = false; }
        OverlayPing.Stop();
        PresentMonitor.Stop();
        PresentMonitor.Frames.Clear();
        Names.Clear();
        Log.Info("Performance overlay hidden; frame-timing session stopped");
        Changed?.Invoke();
    }

    /// <summary>Move &amp; resize mode: the overlay takes the mouse, shows a frame, a caption and a scale grip.</summary>
    public static void SetEditMode(bool on)
    {
        if (on && !Visible) Show();
        if (_win is null) return;
        _win.SetEditMode(on);
        if (!on) _win.SetClickThrough(S.ClickThrough);
        Tick();
        Changed?.Invoke();
    }

    public static void SetClickThrough(bool on)
    {
        S.ClickThrough = on;
        App.SaveSettingsSoon();
        _win?.SetClickThrough(on);
        Changed?.Invoke();
    }

    /// <summary>Re-applies layout after settings change (the visible overlay updates immediately).</summary>
    public static void LayoutChanged()
    {
        _layoutVersion++;
        Refresh.Interval = TimeSpan.FromMilliseconds(Math.Clamp(S.RefreshMs, 100, 2000));
        if (Visible) Tick();
    }

    private static void OnMoved()
    {
        if (_win is null || _applied is not { } l) return;
        var (device, fx, fy) = _win.CurrentSpot();
        l.CustomPosition = true;
        l.Monitor = device;
        l.X = Math.Round(fx, 4);
        l.Y = Math.Round(fy, 4);
        App.SaveSettingsSoon();
        Changed?.Invoke();
    }

    private static void OnScaled(double scale)
    {
        if (_applied is not { } l || _win is null) return;
        l.Scale = Math.Round(scale, 2);
        _win.Panel.SetScale(l.Scale);
        App.SaveSettingsSoon();
    }

    private static string NameOf(int pid)
    {
        if (Names.TryGetValue(pid, out var n)) return n;
        try { using var p = Process.GetProcessById(pid); n = p.ProcessName; } catch { n = "process " + pid; }
        if (Names.Count > 64) Names.Clear();
        return Names[pid] = n;
    }

    /// <summary>
    /// Which process to measure: the auto-monitored game if one is running, otherwise the foreground window's process
    /// (ignoring Money Tweaks itself), otherwise the busiest presenter (never the desktop compositor).
    /// </summary>
    public static (int pid, nint hwnd) PickTarget()
    {
        var (fgWnd, fgPid) = GameWindow.Foreground();
        if (_autoGame is { } g) return (g.pid, fgPid == g.pid ? fgWnd : 0);
        if (fgPid != 0 && fgPid != Environment.ProcessId) return (fgPid, fgWnd);
        var busiest = PresentMonitor.Frames.Active(TimeSpan.FromSeconds(2)).FirstOrDefault(a => a.pid != Environment.ProcessId && !NameOf(a.pid).Equals("dwm", StringComparison.OrdinalIgnoreCase));
        return (busiest.pid, 0);
    }

    /// <summary>The layout for the measured app: the auto-detected game's, else the foreground game's if it has one, else the default.</summary>
    private static OverlayLayout LayoutFor(string? targetExe, out string? profile)
    {
        var exe = _autoGame?.exe ?? targetExe;
        var p = S.ProfileFor(exe);
        profile = p is { CustomLayout: true } ? p.Exe : null;
        return S.LayoutFor(exe);
    }

    /// <summary>The GPU doing the most work right now (the game's GPU while playing), with its load, temperature and VRAM.</summary>
    public static (double? load, double? temp, double? vramMb, double? vramTotalMb) BusiestGpu(SensorFrame? f)
    {
        if (f is null) return default;
        int best = -1; double bestLoad = -1;
        for (int i = 0; i < 8; i++)
            if (f.Values.TryGetValue($"gpu{i}.load", out var l) && l is { } x && x > bestLoad) { best = i; bestLoad = x; }
        if (best < 0) return default;
        double? total = f.Flags.TryGetValue($"gpu{best}.vramtotal", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var tv) ? tv : null;
        return (bestLoad, f.Values.GetValueOrDefault($"gpu{best}.temp"), f.Values.GetValueOrDefault($"gpu{best}.vram"), total);
    }

    private static void Tick()
    {
        if (_win is null) return;
        try
        {
            var now = PresentMonitor.Now;
            var (pid, hwnd) = PickTarget();
            string? exe = pid == 0 ? null : NameOf(pid) + ".exe";
            var layout = LayoutFor(exe, out var profile);
            if (!ReferenceEquals(layout, _applied) || _appliedVersion != _layoutVersion)
            {
                _win.Panel.ApplyLayout(layout);
                _win.Opacity = Math.Clamp(layout.WindowOpacity, 0.3, 1);
                _applied = layout;
                _appliedVersion = _layoutVersion;
                ActiveProfile = profile;
                Changed?.Invoke();
            }
            var items = layout.EffectiveItems();
            if (items.Contains(OverlayMetric.Ping)) OverlayPing.Start(App.Settings.PingHost); else OverlayPing.Stop();

            var stats = pid == 0 ? null : PresentMonitor.Frames.Stats(pid, TimeSpan.FromSeconds(1), 120, now);
            var graph = pid == 0 || !items.Contains(OverlayMetric.Graph) ? null : PresentMonitor.Frames.Stats(pid, TimeSpan.FromMilliseconds(FrametimeGraph.WindowMs + 300), 2000, now)?.RecentMs;
            string? note = null;
            if (PresentMonitor.State == CaptureState.Running && stats is null && pid != 0)
                note = "No frames from this app (idle, minimized, or not drawing)";
            else if (PresentMonitor.State is CaptureState.NeedsAdmin) note = "FPS needs Money Tweaks to run as administrator";
            var mode = hwnd != 0 ? GameWindow.Detect(hwnd) : GameWindowMode.Unknown;
            var hub = SensorHub.Shared.Last;
            var snap = hub?.Snapshot;
            var (gLoad, gTemp, vram, vramTotal) = BusiestGpu(hub);
            var mon = App.Settings.Monitor;
            var frame = new OverlayFrame(stats, PresentMonitor.State, note,
                snap?.CpuPercent, gLoad ?? snap?.GpuPercent, snap?.RamUsedGb, snap?.RamTotalGb, snap?.CpuTempC, gTemp ?? snap?.GpuTempC,
                pid == 0 ? null : NameOf(pid), mode)
            {
                VramUsedMb = vram, VramTotalMb = vramTotal, PingMs = OverlayPing.LastMs, PingNote = OverlayPing.Note, GraphMs = graph,
                CpuWarnC = mon.CpuTempWarnC, GpuWarnC = mon.GpuTempWarnC,
            };
            LastFrame = frame;
            _win.Panel.Update(frame);
            _win.UpdateLayout();
            if (_offscreen) { _win.Left = -6000; _win.Top = -6000; }
            else if (!_win.EditMode)
            {
                var gameMon = GameWindow.MonitorOf(hwnd != 0 ? hwnd : GameWindow.Foreground().hwnd) ?? PrimaryMonitor();
                if (layout.CustomPosition)
                {
                    var d = GameWindow.Displays().FirstOrDefault(x => x.device == layout.Monitor);
                    var target = d.device is not null ? (d.x, d.y, d.w, d.h) : gameMon;   // that display was unplugged: use the game's
                    _win.PlaceAt(layout.X, layout.Y, target);
                }
                else _win.Place(layout.Position, layout.Margin, gameMon);
            }
            PresentMonitor.Frames.Prune(now);
        }
        catch (Exception ex) { Log.Debug("overlay tick: " + ex.Message); }
    }

    private static (int x, int y, int w, int h) PrimaryMonitor()
    {
        var d = GameWindow.Displays();
        if (d.Count > 0) return (d[0].x, d[0].y, d[0].w, d[0].h);
        var r = Monitors.All()[0];
        return (r.X, r.Y, r.Width, r.Height);
    }

    // ---- automatic monitoring for configured games -------------------------------------------------------------------

    public static void StartWatcher()
    {
        if (_watch is not null) return;
        _watch = new CancellationTokenSource();
        var ct = _watch.Token;
        var ui = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { Poll(ui); } catch (Exception ex) { Log.Debug("overlay watcher: " + ex.Message); }
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        });
        Log.Info("Overlay game watcher started");
    }

    public static void StopWatcher()
    {
        _watch?.Cancel();
        _watch = null;
        _autoGame = null;
        _suppressed = null;
    }

    private static void Poll(Dispatcher ui)
    {
        var games = S.Games.Where(g => g.AutoShow && g.Exe.Length > 0).Select(g => OverlaySettings.Normalize(g.Exe)).ToList();
        if (_autoGame is { } cur)
        {
            bool alive;
            try { using var p = Process.GetProcessById(cur.pid); alive = !p.HasExited; } catch { alive = false; }
            _missing = alive ? 0 : _missing + 1;
            if (_missing >= 2)
            {
                // Game closed: hide, stop the ETW session and forget it.
                _missing = 0;
                ui.Invoke(() =>
                {
                    Log.Info($"{cur.exe} closed; overlay monitoring ended");
                    _autoGame = null;
                    if (_suppressed == cur.exe) _suppressed = null;
                    if (Visible) Hide();
                });
            }
            return;
        }
        var running = ProfileEngine.RunningGame(games);
        if (running is null) { _suppressed = null; return; }
        if (running == _suppressed) return;
        var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(running));
        int pid;
        try { if (ps.Length == 0) return; pid = ps[0].Id; } finally { foreach (var p in ps) p.Dispose(); }
        ui.Invoke(() =>
        {
            _autoGame = (running, pid);
            Log.Info($"{running} started; showing the performance overlay with its profile");
            Show();
        });
    }

    /// <summary>Stops everything the overlay started: watcher, timers, hotkeys, ping, ETW session, sensor sampling, window.</summary>
    public static void Shutdown()
    {
        StopWatcher();
        Refresh.Stop();
        if (_hwnd != 0) { UnregisterHotKey(_hwnd, HotkeyId); UnregisterHotKey(_hwnd, EditHotkeyId); }
        if (_hubHeld) { SensorHub.Shared.Release(HubToken); _hubHeld = false; }
        OverlayPing.Stop();
        PresentMonitor.Stop();
        _win?.Close();
    }
}

/// <summary>
/// Overlay ping: an ICMP echo to the ping host from settings every two seconds, only while the overlay shows ping. This is
/// your connection's latency to that host, not the game server's ping (games don't expose which server they use, and their
/// traffic isn't readable without hooking them), and the overlay labels it plainly as ping to that host.
/// </summary>
public static class OverlayPing
{
    private static CancellationTokenSource? _cts;
    private static string _host = "";
    public static double? LastMs { get; private set; }
    public static string? Note { get; private set; }
    public static string Host => _host;
    public static bool Running => _cts is not null;

    public static void Start(string host)
    {
        host = string.IsNullOrWhiteSpace(host) ? "1.1.1.1" : host.Trim();
        if (_cts is not null && host == _host) return;
        Stop();
        _host = host;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var ping = new Ping();
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var r = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(1500), null, null, cts.Token);
                    if (r.Status == IPStatus.Success) { LastMs = r.RoundtripTime; Note = null; }
                    else { LastMs = null; Note = r.Status == IPStatus.TimedOut ? "timeout" : "no reply"; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { LastMs = null; Note = ex is PingException ? "no network" : "n/a"; }
                try { await Task.Delay(2000, cts.Token); } catch { break; }
            }
        });
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        LastMs = null;
        Note = null;
    }
}
