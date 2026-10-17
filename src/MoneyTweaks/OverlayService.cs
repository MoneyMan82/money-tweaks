using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Views;

namespace MoneyTweaks;

/// <summary>
/// Live performance overlay runtime. FPS and frame time come from <see cref="PresentMonitor"/> (ETW Present events);
/// CPU, GPU, RAM and temperatures from the shared <see cref="LiveStats"/> sampler, so nothing is measured twice. The ETW
/// session and hardware sampling only run while the overlay is visible. Games in the overlay's list can show it
/// automatically while they run; when they close it hides and the session stops.
/// </summary>
public static class OverlayService
{
    private const int HotkeyId = 0x4F56;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);

    private static OverlaySettings S => App.Settings.Overlay;
    private static PerformanceOverlayWindow? _win;
    private static readonly DispatcherTimer Refresh = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
    private static nint _hwnd;
    private static bool _hookAdded, _subscribed;
    private static HardwareSnapshot? _hw;
    private static CancellationTokenSource? _watch;
    private static readonly Dictionary<int, string> Names = [];

    /// <summary>The game being monitored automatically (exe and process), or null.</summary>
    private static (string exe, int pid)? _autoGame;
    /// <summary>The user hid the overlay while this game ran; don't show it again until the game closes.</summary>
    private static string? _suppressed;
    private static int _missing;

    public static bool Visible => _win?.IsVisible == true;
    public static string HotkeyStatus { get; private set; } = "Not registered yet.";
    public static OverlayFrame? LastFrame { get; private set; }
    public static event Action? Changed;

    public static void Init(Window main)
    {
        _hwnd = new WindowInteropHelper(main).Handle;
        if (!_hookAdded && HwndSource.FromHwnd(_hwnd) is { } src)
        {
            src.AddHook((nint _, int msg, nint wp, nint _, ref bool handled) =>
            {
                if (msg == 0x0312 && wp == HotkeyId) { Toggle(); handled = true; }
                return 0;
            });
            _hookAdded = true;
        }
        RegisterHotkey();
        Refresh.Tick += (_, _) => Tick();
        if (S.AutoMonitor) StartWatcher();
    }

    /// <summary>(Re)registers the show/hide hotkey from settings. Returns false if it's invalid or taken by another app.</summary>
    public static bool RegisterHotkey()
    {
        if (_hwnd == 0) return false;
        UnregisterHotKey(_hwnd, HotkeyId);
        if (!OverlayHotkey.TryParse(S.Hotkey, out var mods, out var vk)) { HotkeyStatus = $"\"{S.Hotkey}\" isn't a valid hotkey."; Changed?.Invoke(); return false; }
        var ok = RegisterHotKey(_hwnd, HotkeyId, mods | OverlayHotkey.ModNoRepeat, (uint)vk);
        HotkeyStatus = ok ? $"{S.Hotkey} shows/hides the overlay (works while a game has focus)." : $"{S.Hotkey} is already used by another app; choose another.";
        if (!ok) Log.Warn("Overlay hotkey not registered: " + HotkeyStatus);
        Changed?.Invoke();
        return ok;
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
        }
        _win.Panel.ApplyLayout(S.LayoutFor(_autoGame?.exe));
        var state = PresentMonitor.Start();
        if (!_subscribed) { LiveStats.Subscribe(OnSample); _subscribed = true; }
        _win.Show();
        Refresh.Start();
        Tick();
        Log.Info($"Performance overlay shown (frame timing: {state}{(_autoGame is { } a ? ", game " + a.exe : "")})");
        Changed?.Invoke();
    }

    public static void Hide()
    {
        Refresh.Stop();
        _win?.Hide();
        if (_subscribed) { LiveStats.Unsubscribe(OnSample); _subscribed = false; }
        PresentMonitor.Stop();
        PresentMonitor.Frames.Clear();
        Names.Clear();
        Log.Info("Performance overlay hidden; frame-timing session stopped");
        Changed?.Invoke();
    }

    /// <summary>Re-applies layout after settings change (the visible overlay updates immediately).</summary>
    public static void LayoutChanged()
    {
        _win?.Panel.ApplyLayout(S.LayoutFor(_autoGame?.exe));
        if (Visible) Tick();
    }

    private static void OnSample(HardwareSnapshot s) => _hw = s;

    private static string NameOf(int pid)
    {
        if (Names.TryGetValue(pid, out var n)) return n;
        try { using var p = Process.GetProcessById(pid); n = p.ProcessName; } catch { n = "process " + pid; }
        if (Names.Count > 64) Names.Clear();
        return Names[pid] = n;
    }

    /// <summary>
    /// Which process to measure: the auto-monitored game if one is running, otherwise the foreground window's process
    /// (ignoring Money Tweaks itself), otherwise the busiest presenter.
    /// </summary>
    public static (int pid, nint hwnd) PickTarget()
    {
        var (fgWnd, fgPid) = GameWindow.Foreground();
        if (_autoGame is { } g) return (g.pid, fgPid == g.pid ? fgWnd : 0);
        if (fgPid != 0 && fgPid != Environment.ProcessId) return (fgPid, fgWnd);
        var busiest = PresentMonitor.Frames.Active(TimeSpan.FromSeconds(2)).FirstOrDefault(a => a.pid != Environment.ProcessId);
        return (busiest.pid, 0);
    }

    private static void Tick()
    {
        if (_win is null) return;
        try
        {
            var now = PresentMonitor.Now;
            var (pid, hwnd) = PickTarget();
            var stats = pid == 0 ? null : PresentMonitor.Frames.Stats(pid, TimeSpan.FromSeconds(1), 120, now);
            string? note = null;
            if (PresentMonitor.State == CaptureState.Running && stats is null && pid != 0)
                note = "No frames from this app (idle, Vulkan/OpenGL, or not a game)";
            else if (PresentMonitor.State is CaptureState.NeedsAdmin) note = "Run as admin for FPS";
            var mode = hwnd != 0 ? GameWindow.Detect(hwnd) : GameWindowMode.Unknown;
            var hw = _hw;
            var frame = new OverlayFrame(stats, PresentMonitor.State, note,
                hw?.CpuPercent, hw?.GpuPercent, hw?.RamUsedGb, hw?.RamTotalGb, hw?.CpuTempC, hw?.GpuTempC,
                pid == 0 ? null : NameOf(pid), mode);
            LastFrame = frame;
            _win.Panel.Update(frame);
            var layout = S.LayoutFor(_autoGame?.exe);
            var monitor = GameWindow.MonitorOf(hwnd != 0 ? hwnd : GameWindow.Foreground().hwnd) ?? PrimaryMonitor();
            _win.UpdateLayout();
            _win.Place(layout.Position, layout.Margin, monitor);
            PresentMonitor.Frames.Prune(now);
        }
        catch (Exception ex) { Log.Debug("overlay tick: " + ex.Message); }
    }

    private static (int x, int y, int w, int h) PrimaryMonitor()
    {
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
            Log.Info($"{running} started; showing the performance overlay");
            Show();
        });
    }

    public static void Shutdown()
    {
        StopWatcher();
        Refresh.Stop();
        if (_hwnd != 0) UnregisterHotKey(_hwnd, HotkeyId);
        if (_subscribed) { LiveStats.Unsubscribe(OnSample); _subscribed = false; }
        PresentMonitor.Stop();
        _win?.Close();
    }
}
