using System.Diagnostics;
using System.Runtime.InteropServices;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Macros;

/// <summary>Where macro input goes. The real sink uses SendInput; tests and "Test" mode use a recording sink.</summary>
public interface IInputSink
{
    void Key(int vk, bool down);
    void Mouse(MouseBtn button, bool down);
    bool IsKeyDown(int vk);
}

/// <summary>Standard Windows input injection (same API as on-screen keyboards). No drivers, no hooks into games.</summary>
public sealed class SendInputSink : IInputSink
{
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    public void Key(int vk, bool down)
    {
        uint flags = 0x0008; // KEYEVENTF_SCANCODE: many games read scan codes, not virtual keys
        if (!down) flags |= 0x0002;
        if (VirtualKeys.IsExtended(vk)) flags |= 0x0001;
        var input = new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKey((uint)vk, 0), dwFlags = flags } } };
        Send(input);
    }

    public void Mouse(MouseBtn button, bool down)
    {
        uint flag = button switch
        {
            MouseBtn.Right => down ? 0x0008u : 0x0010u,
            MouseBtn.Middle => down ? 0x0020u : 0x0040u,
            _ => down ? 0x0002u : 0x0004u,
        };
        Send(new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flag } } });
    }

    public bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 0)
            throw new InvalidOperationException("Windows blocked the input. If the target app runs as administrator, run Money Tweaks as administrator too.");
    }
}

public enum StopReason { Finished, Stopped, TriggerReleased, Emergency, Timeout, BlockedGame, Error }

/// <summary>Runs a macro timeline once per repeat. Tracks everything it holds and always releases it.</summary>
public sealed class MacroPlayer(IInputSink sink, Func<string?>? foregroundProcess = null)
{
    private readonly HashSet<int> _keys = [];
    private readonly ManualResetEventSlim _running = new(true);
    private List<int> _pausedKeys = [];
    private List<MouseBtn> _pausedButtons = [];
    public bool Paused => !_running.IsSet;

    /// <summary>Pauses before the next step and releases held inputs (they are pressed again on resume).</summary>
    public void Pause()
    {
        if (!_running.IsSet) return;
        _running.Reset();
        _pausedKeys = [.. _keys];
        _pausedButtons = [.. _buttons];
        ReleaseAll();
    }

    public void Resume()
    {
        if (_running.IsSet) return;
        foreach (var k in _pausedKeys) Down(k);
        foreach (var b in _pausedButtons) MDown(b);
        _running.Set();
    }
    private readonly HashSet<MouseBtn> _buttons = [];
    public event Action<int>? StepStarted;
    public string? BlockedGame { get; private set; }

    public async Task<StopReason> RunAsync(Macro m, CancellationToken ct)
    {
        try
        {
            for (int pass = 0; m.Repeat == 0 || pass < m.Repeat; pass++)
            {
                for (int i = 0; i < m.Steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_running.IsSet) await Task.Run(() => _running.Wait(ct), ct);
                    if (foregroundProcess is not null && MacroPolicy.BlockedGame(foregroundProcess()) is { } game)
                    {
                        BlockedGame = game;
                        return StopReason.BlockedGame;
                    }
                    StepStarted?.Invoke(i);
                    await ExecuteAsync(m.Steps[i], ct);
                }
                if (m.Repeat == 0 && m.Steps.All(s => s.Kind is not StepKind.Delay)) await Task.Delay(1, ct); // never spin a tight loop
            }
            return StopReason.Finished;
        }
        catch (OperationCanceledException) { return StopReason.Stopped; }
        catch (BlockedGameException b) { BlockedGame = b.Game; return StopReason.BlockedGame; }
        finally { ReleaseAll(); }
    }

    private async Task ExecuteAsync(MacroStep s, CancellationToken ct)
    {
        switch (s.Kind)
        {
            case StepKind.KeyPress:
                Down(Vk(s.Key));
                await Wait(s.Ms, ct);
                Up(Vk(s.Key));
                break;
            case StepKind.KeyDown: Down(Vk(s.Key)); break;
            case StepKind.KeyUp: Up(Vk(s.Key)); break;
            case StepKind.Combo:
                var keys = s.Key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Vk).ToList();
                foreach (var k in keys) Down(k);
                await Wait(s.Ms, ct);
                foreach (var k in Enumerable.Reverse(keys)) Up(k);
                break;
            case StepKind.MouseClick:
                MDown(s.Button);
                await Wait(Math.Max(s.Ms, 1), ct);
                MUp(s.Button);
                break;
            case StepKind.MouseDown: MDown(s.Button); break;
            case StepKind.MouseUp: MUp(s.Button); break;
            case StepKind.Delay: await Wait(s.Ms, ct); break;
            case StepKind.WaitKeyDown:
            case StepKind.WaitKeyUp:
                var vk = Vk(s.Key);
                bool wantDown = s.Kind == StepKind.WaitKeyDown;
                var sw = Stopwatch.StartNew();
                while (sink.IsKeyDown(vk) != wantDown)
                {
                    if (s.Ms > 0 && sw.ElapsedMilliseconds >= s.Ms) break;
                    await Task.Delay(5, ct);
                }
                break;
        }
    }

    private static int Vk(string name) => VirtualKeys.TryParse(name, out var vk) ? vk : throw new InvalidOperationException($"Unknown key \"{name}\".");
    /// <summary>Thrown when a blocked competitive game takes focus while a step is holding or waiting.</summary>
    private sealed class BlockedGameException(string game) : Exception(game) { public string Game { get; } = game; }

    /// <summary>
    /// Waits in short slices and checks the focused app between them. Without this, a held key ("hold W, wait 60 s")
    /// kept going into a blocked game that took focus mid-step; the check used to run only between steps.
    /// </summary>
    private async Task Wait(int ms, CancellationToken ct)
    {
        if (ms <= 0) return;
        if (foregroundProcess is null) { await Task.Delay(ms, ct); return; }
        var end = Environment.TickCount64 + ms;
        while (true)
        {
            if (MacroPolicy.BlockedGame(foregroundProcess()) is { } game) throw new BlockedGameException(game);
            var left = end - Environment.TickCount64;
            if (left <= 0) return;
            await Task.Delay((int)Math.Min(left, BlockedPollMs), ct);
        }
    }

    /// <summary>How often a running step re-checks the focused app for blocked games.</summary>
    public const int BlockedPollMs = 100;
    private void Down(int vk) { sink.Key(vk, true); _keys.Add(vk); }
    private void Up(int vk) { sink.Key(vk, false); _keys.Remove(vk); }
    private void MDown(MouseBtn b) { sink.Mouse(b, true); _buttons.Add(b); }
    private void MUp(MouseBtn b) { sink.Mouse(b, false); _buttons.Remove(b); }

    /// <summary>Releases every key and button this player pressed. Safe to call repeatedly.</summary>
    public void ReleaseAll()
    {
        foreach (var k in _keys.ToList()) { try { sink.Key(k, false); } catch { } }
        foreach (var b in _buttons.ToList()) { try { sink.Mouse(b, false); } catch { } }
        _keys.Clear();
        _buttons.Clear();
    }
}

/// <summary>Global keyboard hook used only for macro triggers. Ignores injected input so macros can't trigger themselves.</summary>
public sealed class GlobalKeyHook : IDisposable
{
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint extra; }

    private readonly HookProc _proc;
    private nint _hook;
    public event Action<int, bool>? Key; // (vk, isDown)

    public GlobalKeyHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(null), 0);
        if (_hook == 0) throw new InvalidOperationException("Windows refused the keyboard hook needed for macro triggers.");
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & 0x10) != 0;
            int msg = (int)wParam;
            if (!injected && msg is 0x0100 or 0x0104 or 0x0101 or 0x0105) // WM_(SYS)KEYDOWN / WM_(SYS)KEYUP
            {
                try { Key?.Invoke((int)k.vkCode, msg is 0x0100 or 0x0104); } catch { /* never break the user's keyboard */ }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }
}

/// <summary>Runs one macro at a time, handles triggers, safety timeout, blocked games and emergency stop.</summary>
public sealed class MacroManager : IDisposable
{
    private readonly Func<IInputSink> _sinkFactory;
    private readonly Func<string?> _foreground;
    private CancellationTokenSource? _cts;
    private MacroPlayer? _player;
    private Task? _running;
    private readonly HashSet<int> _down = [];
    public Macro? Running { get; private set; }
    public bool DryRun { get; private set; }
    public IReadOnlyList<Macro> Armed { get; set; } = [];
    public event Action<Macro, int>? Step;
    public event Action<Macro, StopReason, string?>? Stopped;
    public event Action<Macro>? Started;

    public MacroManager(Func<IInputSink>? sinkFactory = null, Func<string?>? foreground = null)
    {
        _sinkFactory = sinkFactory ?? (() => new SendInputSink());
        _foreground = foreground ?? ForegroundProcessName;
    }

    public static string? ForegroundProcessName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    /// <summary>Starts a macro. dryRun = timeline only, no real input (the "Test" button).</summary>
    public bool Start(Macro m, bool dryRun = false, IInputSink? testSink = null)
    {
        if (Running is not null) return false;
        if (!dryRun && MacroPolicy.BlockedGame(_foreground()) is { } game)
        {
            Stopped?.Invoke(m, StopReason.BlockedGame, game);
            return false;
        }
        Running = m;
        DryRun = dryRun;
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(m.SafetyTimeoutSec, 1, MacroPolicy.MaxTimeoutSec)));
        var player = _player = new MacroPlayer(testSink ?? (dryRun ? new NullSink() : _sinkFactory()), dryRun ? null : _foreground);
        player.StepStarted += i => Step?.Invoke(m, i);
        var cts = _cts;
        Started?.Invoke(m);
        Log.Info($"Macro '{m.Name}' started{(dryRun ? " (test, no input)" : "")}");
        _running = Task.Run(async () =>
        {
            StopReason reason;
            string? detail = null;
            try
            {
                reason = await player.RunAsync(m, cts.Token);
                if (reason == StopReason.Stopped && cts.IsCancellationRequested && _pendingReason is { } pr) reason = pr;
                else if (reason == StopReason.Stopped && cts.IsCancellationRequested && _pendingReason is null) reason = StopReason.Timeout;
                if (reason == StopReason.BlockedGame) detail = player.BlockedGame;
            }
            catch (Exception ex) { reason = StopReason.Error; detail = ex.Message; player.ReleaseAll(); }
            finally { _pendingReason = null; }
            Running = null;
            _player = null;
            Log.Info($"Macro '{m.Name}' stopped: {reason}{(detail is null ? "" : " – " + detail)}");
            Stopped?.Invoke(m, reason, detail);
        });
        return true;
    }

    private StopReason? _pendingReason;

    public void Stop(StopReason reason = StopReason.Stopped)
    {
        _pendingReason = reason;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Cancels the macro and immediately releases anything held, even before the run loop unwinds.</summary>
    /// <summary>AND-gate for key triggers that also have colour conditions (supplied by the colour watcher).</summary>
    public Func<Macro, bool>? ColorGate { get; set; }
    public bool Paused => _player?.Paused ?? false;
    public void Pause() => _player?.Pause();
    public void Resume() => _player?.Resume();

    /// <summary>Colour watcher rising edge: start a colour-triggered macro if nothing else runs.</summary>
    public void OnColorRising(Macro m)
    {
        if (m.Trigger == TriggerKind.Color && Running is null && Armed.Any(a => a.Id == m.Id)) Start(m);
    }

    /// <summary>Colour watcher falling edge: looping colour macros stop when their condition ends.</summary>
    public void OnColorFalling(Macro m)
    {
        if (Running is { } r && r.Id == m.Id && !DryRun && (r.Trigger == TriggerKind.Color || r.ColorTriggers.Count > 0) && r.Repeat == 0) Stop(StopReason.TriggerReleased);
    }

    public void EmergencyStop()
    {
        Stop(StopReason.Emergency);
        _player?.ReleaseAll();
    }

    public Task WaitAsync() => _running ?? Task.CompletedTask;

    /// <summary>Feed from <see cref="GlobalKeyHook"/>. Only real (non-injected) key events arrive here.</summary>
    public void OnKey(int vk, bool down)
    {
        bool repeat = down && !_down.Add(vk);
        if (!down) _down.Remove(vk);
        if (repeat) return; // ignore auto-repeat while a key is held
        if (Running is { } r && !DryRun && VirtualKeys.TryParse(r.TriggerKey, out var rvk) && rvk == vk)
        {
            if (r.Trigger == TriggerKind.HoldKey && !down) { Stop(StopReason.TriggerReleased); return; }
            if (r.Trigger == TriggerKind.ToggleKey && down) { Stop(StopReason.Stopped); return; }
        }
        if (!down || Running is not null) return;
        var m = Armed.FirstOrDefault(a => a.Trigger is not (TriggerKind.Manual or TriggerKind.Color) && VirtualKeys.TryParse(a.TriggerKey, out var t) && t == vk
            && (a.ColorTriggers.Count == 0 || (ColorGate?.Invoke(a) ?? false)));
        if (m is not null) Start(m);
    }

    public void Dispose()
    {
        EmergencyStop();
        try { _running?.Wait(2000); } catch { }
    }

    private sealed class NullSink : IInputSink
    {
        public void Key(int vk, bool down) { }
        public void Mouse(MouseBtn button, bool down) { }
        public bool IsKeyDown(int vk) => false;
    }
}

/// <summary>Ready-made macros for uses where automation is normally fine (single-player, accessibility, desktop, streaming).</summary>
public static class MacroLibrary
{
    private static MacroStep K(StepKind k, string key, int ms = 30) => new() { Kind = k, Key = key, Ms = ms };
    private static MacroStep D(int ms) => new() { Kind = StepKind.Delay, Ms = ms };
    private static MacroStep M(StepKind k, MouseBtn b, int ms = 30) => new() { Kind = k, Button = b, Ms = ms };

    public static IReadOnlyList<Macro> BuiltIns { get; } =
    [
        new() { Id = "builtin.autowalk", BuiltIn = true, Name = "Auto-walk (toggle)", Game = "General", Category = "Accessibility",
            Description = "Holds W until you press F8 again. Handy for long walks in single-player games without holding the key.",
            Trigger = TriggerKind.ToggleKey, TriggerKey = "F8", Repeat = 0, SafetyTimeoutSec = 1800,
            Steps = [K(StepKind.KeyDown, "W"), D(60000)] },
        new() { Id = "builtin.hold-left", BuiltIn = true, Name = "Hold left mouse (toggle)", Game = "General", Category = "Accessibility",
            Description = "Press F9 to hold the left mouse button, press F9 again to release. For players who can't hold a button for long (mining, dragging).",
            Trigger = TriggerKind.ToggleKey, TriggerKey = "F9", Repeat = 0, SafetyTimeoutSec = 1800,
            Steps = [M(StepKind.MouseDown, MouseBtn.Left), D(60000)] },
        new() { Id = "builtin.mc-sprint", BuiltIn = true, Name = "Minecraft: sprint forward (toggle)", Game = "Minecraft", Category = "Movement",
            Description = "Holds Left Ctrl + W to sprint forward until you press F7. Use in single-player; check a server's rules before using macros there.",
            Trigger = TriggerKind.ToggleKey, TriggerKey = "F7", Repeat = 0, SafetyTimeoutSec = 1800,
            Steps = [K(StepKind.KeyDown, "LCtrl"), K(StepKind.KeyDown, "W"), D(60000)] },
        new() { Id = "builtin.mc-fishing", BuiltIn = true, Name = "Minecraft: AFK fishing", Game = "Minecraft", Category = "Farming",
            Description = "Right-clicks every 0.5 s while toggled with F6 (for an AFK fishing farm). Single-player/Realms; many servers forbid AFK macros.",
            Trigger = TriggerKind.ToggleKey, TriggerKey = "F6", Repeat = 0, SafetyTimeoutSec = 3600,
            Steps = [M(StepKind.MouseClick, MouseBtn.Right, 40), D(500)] },
        new() { Id = "builtin.hold-sequence", BuiltIn = true, Name = "Hold Shift: number sequence", Game = "General", Category = "Example",
            Description = "Example of a hold trigger: while Right Shift is held, presses 1, 2, 3 with 500 ms between them and repeats; stops when you let go. Edit the keys and delays to suit your game.",
            Trigger = TriggerKind.HoldKey, TriggerKey = "RShift", Repeat = 0, SafetyTimeoutSec = 300,
            Steps = [K(StepKind.KeyPress, "1"), D(500), K(StepKind.KeyPress, "2"), D(500), K(StepKind.KeyPress, "3"), D(500)] },
        new() { Id = "builtin.clip", BuiltIn = true, Name = "Save last 30 seconds (Game Bar)", Game = "General", Category = "Streaming",
            Description = "Presses Win+Alt+G, the Xbox Game Bar shortcut that saves a clip of the last moments (needs background recording on).",
            Trigger = TriggerKind.Manual, Repeat = 1,
            Steps = [new() { Kind = StepKind.Combo, Key = "LWin+LAlt+G", Ms = 60 }] },
        new() { Id = "builtin.snip", BuiltIn = true, Name = "Screenshot a region", Game = "Desktop", Category = "Productivity",
            Description = "Opens the Snipping Tool region capture (Win+Shift+S).",
            Trigger = TriggerKind.Manual, Repeat = 1,
            Steps = [new() { Kind = StepKind.Combo, Key = "LWin+LShift+S", Ms = 60 }] },
        new() { Id = "builtin.color-example", BuiltIn = true, Name = "Example: colour turns green → press Space", Game = "General", Category = "Example",
            Description = "Shows how screen-colour triggers work: when the watched pixel turns green, Space is pressed once. Duplicate it, use Pick from screen to choose your pixel and colour, then arm triggers.",
            Trigger = TriggerKind.Color, Repeat = 1, ColorPollMs = 150,
            ColorTriggers = [new() { X = 0, Y = 0, Color = "#00FF00", Tolerance = 30, Mode = ColorMode.Appears }],
            Steps = [K(StepKind.KeyPress, "Space")] },
        new() { Id = "builtin.paste-enter", BuiltIn = true, Name = "Paste and send", Game = "Desktop", Category = "Productivity",
            Description = "Pastes the clipboard and presses Enter after 150 ms, e.g. for chat messages you send often.",
            Trigger = TriggerKind.PressKey, TriggerKey = "F10", Repeat = 1,
            Steps = [new() { Kind = StepKind.Combo, Key = "LCtrl+V", Ms = 40 }, D(150), K(StepKind.KeyPress, "Enter")] },
    ];
}
