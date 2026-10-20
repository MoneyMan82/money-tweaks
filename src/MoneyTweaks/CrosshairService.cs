using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Views;

namespace MoneyTweaks;

/// <summary>
/// Runs the desktop crosshair: profiles (crosshair.json), the overlay window, global hotkeys, firing detection and
/// per-game switching. Started with the main window, so hotkeys work without opening the Crosshair page.
/// Firing is detected by reading the mouse button state (GetAsyncKeyState) only while the overlay is visible and the
/// profile uses it: no hooks, no injection, nothing touches the game.
/// </summary>
public static class CrosshairService
{
    private const int ToggleId = 0x4D54, NextId = 0x4D55, PrevId = 0x4D56;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

    private static CrosshairData? _data;
    private static CrosshairOverlayWindow? _win;
    private static nint _hwnd;
    private static bool _hookAdded;
    private static readonly BloomState Bloom = new();
    private static DispatcherTimer? _input, _save;
    private static readonly Stopwatch InputClock = new();
    private static bool _aiming;
    private static CancellationTokenSource? _watch;

    // Per-game switching state.
    private static string? _gameExe, _previousProfile, _suppressed;
    private static int _gamePid;
    private static bool _autoShown, _hiddenForFocus;
    private static (int x, int y, int w, int h)? _followMonitor;

    public static string? LoadNotice { get; private set; }
    public static string SaveError { get; private set; } = "";
    public static Dictionary<string, string> HotkeyStatus { get; } = [];
    public static string GameStatus { get; private set; } = "";
    public static event Action? Changed;

    public static CrosshairData Data
    {
        get
        {
            if (_data is null)
            {
                _data = CrosshairStore.Load(App.Settings.Crosshair, out var notice);
                LoadNotice = notice;
                if (!File.Exists(CrosshairStore.FilePath)) SaveNow();
            }
            return _data;
        }
    }

    public static CrosshairProfile Active => Data.Active;
    public static bool Visible => _win?.IsVisible == true;
    public static CrosshairOverlayWindow? Window => _win;
    public static double CurrentBloom => Bloom.Value;
    /// <summary>The game whose profile is in use right now, if any.</summary>
    public static string? ActiveGame => _gameExe;

    public static void Init(Window main)
    {
        _ = Data;
        _hwnd = new WindowInteropHelper(main).Handle;
        if (!_hookAdded && HwndSource.FromHwnd(_hwnd) is { } src)
        {
            src.AddHook((nint _, int msg, nint wp, nint _, ref bool handled) =>
            {
                if (msg != 0x0312) return 0;
                switch ((int)wp)
                {
                    case ToggleId: Toggle(); handled = true; break;
                    case NextId: Cycle(1); handled = true; break;
                    case PrevId: Cycle(-1); handled = true; break;
                }
                return 0;
            });
            _hookAdded = true;
        }
        RegisterHotkeys();
        UpdateWatcher();
        if (Data.Overlay.ShowOnStart) Show();
    }

    // ---- hotkeys --------------------------------------------------------------------------------------------------

    public static readonly (string key, string label)[] Hotkeys = [("toggle", "Show / hide"), ("next", "Next profile"), ("previous", "Previous profile")];

    public static string HotkeyText(string key) => key switch
    {
        "toggle" => Data.Overlay.ToggleHotkey,
        "next" => Data.Overlay.NextHotkey,
        _ => Data.Overlay.PreviousHotkey,
    };

    /// <summary>Changes one hotkey; keeps the old one if the new one is invalid or taken. Empty turns it off.</summary>
    public static bool SetHotkey(string key, string text)
    {
        text = text.Trim();
        if (text.Length > 0 && !OverlayHotkey.TryParse(text, out _, out _)) { HotkeyStatus[key] = $"\"{text}\" isn't a valid hotkey (example: Ctrl+Alt+X)."; Changed?.Invoke(); return false; }
        var o = Data.Overlay;
        var old = HotkeyText(key);
        Assign(key, text);
        var ok = RegisterHotkeys();
        if (!ok && text.Length > 0 && HotkeyStatus.GetValueOrDefault(key, "").Contains("already used"))
        {
            var msg = HotkeyStatus[key];
            Assign(key, old);
            RegisterHotkeys();
            HotkeyStatus[key] = msg + " Kept " + (old.Length > 0 ? old : "no hotkey") + ".";
            Changed?.Invoke();
            return false;
        }
        SaveSoon();
        return true;

        void Assign(string k, string v) { if (k == "toggle") o.ToggleHotkey = v; else if (k == "next") o.NextHotkey = v; else o.PreviousHotkey = v; }
    }

    /// <summary>(Re)registers all three hotkeys. Returns false if any configured one couldn't be registered.</summary>
    public static bool RegisterHotkeys()
    {
        if (_hwnd == 0) return false;
        bool all = true;
        foreach (var (key, label) in Hotkeys)
        {
            int id = key == "toggle" ? ToggleId : key == "next" ? NextId : PrevId;
            UnregisterHotKey(_hwnd, id);
            var text = HotkeyText(key);
            if (text.Length == 0) { HotkeyStatus[key] = "Off."; continue; }
            if (!OverlayHotkey.TryParse(text, out var mods, out var vk)) { HotkeyStatus[key] = $"\"{text}\" isn't a valid hotkey."; all = false; continue; }
            if (RegisterHotKey(_hwnd, id, mods | OverlayHotkey.ModNoRepeat, (uint)vk)) HotkeyStatus[key] = $"{text}: works anywhere, also while a game has focus.";
            else
            {
                HotkeyStatus[key] = $"{text} is already used by another app or another Money Tweaks hotkey.";
                Log.Warn($"Crosshair hotkey ({label}) not registered: {text}");
                all = false;
            }
        }
        Changed?.Invoke();
        return all;
    }

    // ---- show / hide / profiles ---------------------------------------------------------------------------------

    public static void Toggle()
    {
        if (Visible)
        {
            if (_gameExe is not null) _suppressed = _gameExe;
            _hiddenForFocus = false;
            Hide();
        }
        else
        {
            _suppressed = null;
            Show();
        }
    }

    public static void Show()
    {
        if (_win is null)
        {
            _win = new CrosshairOverlayWindow();
            _win.Closed += (_, _) => _win = null;
        }
        Apply();
        _win.Show();
        _win.ApplyPendingPlacement();
        Apply();
        UpdateInput();
        UpdateWatcher();
        Log.Info($"Crosshair shown: {Active.Name}");
        Changed?.Invoke();
    }

    public static void Hide()
    {
        _win?.Hide();
        UpdateInput();
        UpdateWatcher();
        Log.Info("Crosshair hidden");
        Changed?.Invoke();
    }

    public static void Activate(string profileId)
    {
        if (Data.Find(profileId) is null || Data.ActiveId == profileId) return;
        Data.ActiveId = profileId;
        Bloom.Reset();
        Apply();
        UpdateInput();
        SaveSoon();
        Changed?.Invoke();
    }

    public static void Cycle(int dir)
    {
        var list = Data.Profiles;
        if (list.Count < 2) return;
        int i = list.FindIndex(p => p.Id == Data.ActiveId);
        Activate(list[((i + dir) % list.Count + list.Count) % list.Count].Id);
        Log.Info("Crosshair profile: " + Active.Name);
    }

    /// <summary>Call after any edit to the active profile or overlay options: the overlay redraws immediately.</summary>
    public static void Edited()
    {
        Apply();
        UpdateInput();
        UpdateWatcher();
        SaveSoon();
    }

    /// <summary>Redraws and positions the overlay from the current profile and options.</summary>
    private static void Apply()
    {
        if (_win is null) return;
        var o = Data.Overlay;
        var v = _win.Visual;
        v.Profile = Active;
        v.Bloom = Bloom.Value;
        v.CrosshairScale = o.Scale;
        v.GlobalOpacity = _aiming ? 0 : o.Opacity;
        var mon = TargetMonitor();
        int size = (int)Math.Ceiling(CrosshairGeometry.Extent(Active, o.Scale)) * 2 + 8;
        size = Math.Min(size, Math.Max(mon.w, mon.h) * 2);
        _win.PlaceAt(mon.x + mon.w / 2 + (int)Math.Round(o.PositionX), mon.y + mon.h / 2 + (int)Math.Round(o.PositionY), size);
        v.Redraw();
    }

    /// <summary>The monitor the crosshair is drawn on (physical pixels).</summary>
    public static (int x, int y, int w, int h) TargetMonitor()
    {
        if (Data.Overlay.FollowGameMonitor && _followMonitor is { } f) return f;
        var all = Monitors.All();
        var r = all[Math.Clamp(Data.Overlay.MonitorIndex, 0, all.Count - 1)];
        return (r.X, r.Y, r.Width, r.Height);
    }

    // ---- firing detection ---------------------------------------------------------------------------------------

    /// <summary>Polls mouse buttons only while the overlay is visible and the profile animates or hides on aim.</summary>
    private static void UpdateInput()
    {
        bool need = Visible && Active.Firing.NeedsInput;
        if (need && _input is null)
        {
            _input = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
            _input.Tick += (_, _) => PollInput();
            InputClock.Restart();
            _input.Start();
        }
        else if (!need && _input is not null)
        {
            _input.Stop();
            _input = null;
            Bloom.Reset();
            if (_aiming) { _aiming = false; Apply(); }
        }
    }

    /// <summary>Physical left/right buttons, corrected for "switch primary and secondary buttons".</summary>
    public static (bool left, bool right) MouseButtons()
    {
        bool l = (GetAsyncKeyState(0x01) & 0x8000) != 0, r = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        return GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? (r, l) : (l, r);
    }

    private static void PollInput()
    {
        if (_win is null) return;
        var dt = InputClock.Elapsed.TotalMilliseconds;
        InputClock.Restart();
        var (left, right) = MouseButtons();
        var f = Active.Firing;
        bool changed = Bloom.Update(BloomState.IsFiring(f.Trigger, left, right), dt, f);
        bool aiming = f.HideWhileAiming && right;
        if (aiming != _aiming) { _aiming = aiming; changed = true; }
        if (!changed) return;
        _win.Visual.Bloom = Bloom.Value;
        _win.Visual.GlobalOpacity = _aiming ? 0 : Data.Overlay.Opacity;
        _win.Visual.Redraw();
    }

    // ---- per-game profiles and following the game's monitor ---------------------------------------------------

    public static void UpdateWatcher()
    {
        bool need = Data.AutoSwitch && Data.Games.Count > 0 || Data.Overlay.FollowGameMonitor && Visible;
        if (need && _watch is null)
        {
            _watch = new CancellationTokenSource();
            var ct = _watch.Token;
            var ui = Application.Current.Dispatcher;
            _ = Task.Run(async () =>
            {
                var names = new Dictionary<int, string>();
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (fg, pid) = GameWindow.Foreground();
                        string? exe = null;
                        if (pid != 0 && !names.TryGetValue(pid, out exe))
                        {
                            try { using var p = Process.GetProcessById(pid); exe = p.ProcessName + ".exe"; } catch { exe = null; }
                            if (names.Count > 128) names.Clear();
                            if (exe is not null) names[pid] = exe;
                        }
                        var mode = fg != 0 && pid != Environment.ProcessId ? GameWindow.Detect(fg) : GameWindowMode.Unknown;
                        var mon = fg != 0 ? GameWindow.MonitorOf(fg) : null;
                        bool gameAlive = _gamePid != 0 && Alive(_gamePid);
                        ui.Invoke(() => OnForeground(pid, exe, mode, mon, gameAlive));
                    }
                    catch (Exception ex) { Log.Debug("crosshair watcher: " + ex.Message); }
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            });
        }
        else if (!need && _watch is not null)
        {
            _watch.Cancel();
            _watch = null;
            if (_gameExe is not null) EndGame();
            _followMonitor = null;
            GameStatus = "";
        }
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    private static void OnForeground(int pid, string? exe, GameWindowMode mode, (int x, int y, int w, int h)? mon, bool gameAlive)
    {
        var rule = Data.AutoSwitch ? Data.RuleFor(exe) : null;
        bool own = pid == Environment.ProcessId;
        string status = "";

        if (rule is not null)
        {
            var name = rule.Name.Length > 0 ? rule.Name : rule.Exe;
            if (!string.Equals(_gameExe, CrosshairData.NormalizeExe(rule.Exe), StringComparison.OrdinalIgnoreCase))
            {
                if (_gameExe is not null) EndGame();
                _gameExe = CrosshairData.NormalizeExe(rule.Exe);
                _gamePid = pid;
                _previousProfile = Data.ActiveId;
                if (Data.Find(rule.ProfileId) is not null) Activate(rule.ProfileId);
                Log.Info($"Crosshair: {name} in front, profile {Active.Name}");
                if (rule.AutoShow && !Visible && _suppressed != _gameExe) { Show(); _autoShown = true; }
            }
            else if (_hiddenForFocus) { _hiddenForFocus = false; Show(); }
            status = $"{name} is in front: using \"{Active.Name}\".";
            if (mode == GameWindowMode.ExclusiveFullscreen)
                status += " It's in exclusive fullscreen, where Windows doesn't draw other windows on top; switch the game to borderless/windowed fullscreen to see the crosshair.";
        }
        else if (_gameExe is not null)
        {
            if (!gameAlive) EndGame();
            else
            {
                if (Data.HideWhenGameUnfocused && _autoShown && Visible && !own) { _hiddenForFocus = true; Hide(); }
                status = $"{_gameExe} is running in the background.";
            }
        }

        // Follow the focused game's monitor (a listed game, or any borderless/fullscreen window that isn't us).
        if (Data.Overlay.FollowGameMonitor && !own && mon is { } m && (rule is not null || mode is GameWindowMode.Borderless or GameWindowMode.ExclusiveFullscreen))
        {
            if (_followMonitor != m) { _followMonitor = m; Apply(); }
        }
        if (status != GameStatus) { GameStatus = status; Changed?.Invoke(); }
    }

    private static void EndGame()
    {
        Log.Info($"Crosshair: {_gameExe} closed");
        if (_previousProfile is not null && Data.Find(_previousProfile) is not null) Activate(_previousProfile);
        if (_autoShown && Visible) Hide();
        _gameExe = null; _gamePid = 0; _previousProfile = null; _suppressed = null; _autoShown = false; _hiddenForFocus = false;
        GameStatus = "";
        Changed?.Invoke();
    }

    // ---- persistence ----------------------------------------------------------------------------------------------

    public static void SaveSoon()
    {
        if (_save is null)
        {
            _save = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
            _save.Tick += (_, _) => { _save!.Stop(); SaveNow(); };
        }
        _save.Stop();
        _save.Start();
    }

    public static bool SaveNow()
    {
        _save?.Stop();
        if (_data is null) return true;
        try
        {
            CrosshairStore.Save(_data);
            if (SaveError.Length > 0) { SaveError = ""; Changed?.Invoke(); }
            return true;
        }
        catch (Exception ex)
        {
            SaveError = "Couldn't save your crosshairs: " + ex.Message;
            Log.Error("Could not save crosshair.json", ex);
            Changed?.Invoke();
            return false;
        }
    }

    public static void Shutdown()
    {
        _watch?.Cancel();
        _watch = null;
        _input?.Stop();
        if (_hwnd != 0) { UnregisterHotKey(_hwnd, ToggleId); UnregisterHotKey(_hwnd, NextId); UnregisterHotKey(_hwnd, PrevId); }
        if (_save?.IsEnabled == true || _data is not null) SaveNow();
        _win?.Close();
    }
}
