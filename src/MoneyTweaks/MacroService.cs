using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MoneyTweaks.Core.Macros;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>App-wide macro runtime: one manager, an optional trigger hook (only while armed) and the emergency-stop hotkey.</summary>
public static class MacroService
{
    public static MacroManager Manager { get; } = new();
    private static GlobalKeyHook? _hook;
    public static bool Armed => _hook is not null;
    public const string EmergencyHotkeyText = "Ctrl+Alt+End";
    private const int HotkeyId = 0x4D4D;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);
    private static nint _hwnd;

    /// <summary>Registers Ctrl+Alt+End as a global emergency stop (works even while a game has focus).</summary>
    public static void Init(Window main)
    {
        _hwnd = new WindowInteropHelper(main).Handle;
        if (RegisterHotKey(_hwnd, HotkeyId, 0x0002 | 0x0001 | 0x4000, 0x23))
            HwndSource.FromHwnd(_hwnd)?.AddHook((nint _, int msg, nint wp, nint _, ref bool handled) =>
            {
                if (msg == 0x0312 && wp == HotkeyId) { Manager.EmergencyStop(); handled = true; }
                return 0;
            });
        else Log.Warn($"{EmergencyHotkeyText} is used by another app; use the on-screen Emergency stop button instead.");
    }

    /// <summary>Starts listening for trigger keys of the given macros. Throws if Windows refuses the hook.</summary>
    public static void Arm(IReadOnlyList<Macro> macros)
    {
        Manager.Armed = macros;
        if (_hook is null)
        {
            _hook = new GlobalKeyHook();
            _hook.Key += Manager.OnKey;
            Log.Info($"Macro triggers armed ({macros.Count} macros)");
        }
    }

    public static void Disarm()
    {
        if (_hook is null) return;
        _hook.Dispose();
        _hook = null;
        Manager.Armed = [];
        Log.Info("Macro triggers disarmed");
    }

    public static void Shutdown()
    {
        Disarm();
        Manager.Dispose(); // releases any held keys/buttons
        if (_hwnd != 0) UnregisterHotKey(_hwnd, HotkeyId);
    }
}
