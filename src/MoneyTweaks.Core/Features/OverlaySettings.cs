using System.Runtime.InteropServices;
using MoneyTweaks.Core.Macros;

namespace MoneyTweaks.Core.Features;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>What the overlay shows and how it looks.</summary>
public sealed class OverlayLayout
{
    public bool Fps { get; set; } = true;
    public bool FrameTime { get; set; } = true;
    public bool OnePercentLow { get; set; } = true;
    public bool Graph { get; set; } = true;
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Temps { get; set; } = true;
    public bool Target { get; set; } = true;
    public double FontSize { get; set; } = 13;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public string AccentColor { get; set; } = "#FF2BE38B";
    /// <summary>Background panel opacity, 0 (none) to 1.</summary>
    public double Opacity { get; set; } = 0.55;
    public OverlayCorner Position { get; set; } = OverlayCorner.TopLeft;
    public double Margin { get; set; } = 12;

    public OverlayLayout Clone() => (OverlayLayout)MemberwiseClone();
}

/// <summary>A game the overlay knows about: shown automatically when it starts, optionally with its own layout.</summary>
public sealed class OverlayGameProfile
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public bool AutoShow { get; set; } = true;
    public bool CustomLayout { get; set; }
    public OverlayLayout Layout { get; set; } = new();
}

public sealed class OverlaySettings
{
    public OverlayLayout Layout { get; set; } = new();
    public string Hotkey { get; set; } = "Ctrl+Shift+F10";
    /// <summary>Watch for the games below and show the overlay while they run.</summary>
    public bool AutoMonitor { get; set; }
    public List<OverlayGameProfile> Games { get; set; } = [];

    public OverlayGameProfile? ProfileFor(string? exe) =>
        exe is null ? null : Games.FirstOrDefault(g => string.Equals(Normalize(g.Exe), Normalize(exe), StringComparison.OrdinalIgnoreCase));

    /// <summary>The game's own layout if it has one, otherwise the default layout.</summary>
    public OverlayLayout LayoutFor(string? exe) => ProfileFor(exe) is { CustomLayout: true } p ? p.Layout : Layout;

    public static string Normalize(string exe)
    {
        var n = Path.GetFileName(exe.Trim());
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }

    public static OverlaySettings WithDefaults()
    {
        var s = new OverlaySettings();
        s.Games.Add(new OverlayGameProfile { Exe = "FortniteClient-Win64-Shipping.exe", Name = "Fortnite", AutoShow = true });
        return s;
    }
}

/// <summary>"Ctrl+Shift+F10" ↔ RegisterHotKey modifiers and virtual key.</summary>
public static class OverlayHotkey
{
    public const uint ModAlt = 0x1, ModCtrl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    public static bool TryParse(string? text, out uint mods, out int vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModCtrl; break;
                case "shift": mods |= ModShift; break;
                case "alt": mods |= ModAlt; break;
                case "win": mods |= ModWin; break;
                default: return false;
            }
        }
        // A global hotkey needs at least one modifier unless it's a function key, so it can't swallow normal typing.
        if (!VirtualKeys.TryParse(parts[^1], out vk) || vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5) return false;
        return mods != 0 || vk is >= 0x70 and <= 0x87;
    }
}

public enum GameWindowMode { Unknown, Windowed, Borderless, ExclusiveFullscreen }

/// <summary>Best-effort window-mode detection for the game window (no access to the game process needed).</summary>
public static class GameWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint m, ref MONITORINFO mi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    public static (nint hwnd, int pid) Foreground()
    {
        var h = GetForegroundWindow();
        if (h == 0) return (0, 0);
        GetWindowThreadProcessId(h, out var pid);
        return (h, (int)pid);
    }

    /// <summary>Monitor rectangle (physical pixels) of a window, or null.</summary>
    public static (int x, int y, int w, int h)? MonitorOf(nint hwnd)
    {
        if (hwnd == 0) return null;
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi)) return null;
        return (mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, mi.Monitor.Bottom - mi.Monitor.Top);
    }

    public static GameWindowMode Detect(nint hwnd)
    {
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r)) return GameWindowMode.Unknown;
        var mon = MonitorOf(hwnd);
        if (mon is null) return GameWindowMode.Unknown;
        var style = (long)GetWindowLongPtr(hwnd, -16);
        int quns = SHQueryUserNotificationState(out var q) == 0 ? q : -1;
        return Classify(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, mon.Value, (style & 0x00C00000) != 0, quns);
    }

    /// <summary>
    /// QUNS_RUNNING_D3D_FULL_SCREEN (3) = a Direct3D exclusive-fullscreen app has the display. Otherwise a captionless
    /// window covering its monitor is borderless fullscreen, anything else is windowed.
    /// </summary>
    public static GameWindowMode Classify(int x, int y, int w, int h, (int x, int y, int w, int h) monitor, bool hasCaption, int quns)
    {
        if (quns == 3) return GameWindowMode.ExclusiveFullscreen;
        bool covers = x <= monitor.x && y <= monitor.y && x + w >= monitor.x + monitor.w && y + h >= monitor.y + monitor.h;
        return covers && !hasCaption ? GameWindowMode.Borderless : GameWindowMode.Windowed;
    }

    public static string Explain(GameWindowMode m) => m switch
    {
        GameWindowMode.ExclusiveFullscreen => "Exclusive fullscreen: frame timing still works, but Windows doesn't draw other windows over it, so the overlay may be hidden (or alt-tab the game). Use borderless/windowed fullscreen to see it.",
        GameWindowMode.Borderless => "Borderless fullscreen: the overlay is drawn on top.",
        GameWindowMode.Windowed => "Windowed: the overlay is drawn on top.",
        _ => "Window mode unknown.",
    };
}
