using System.Runtime.InteropServices;
using MoneyTweaks.Core.Macros;

namespace MoneyTweaks.Core.Features;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Minimal: one line of numbers. Compact: labelled values in a row. Detailed: a labelled list with bars and the graph.</summary>
public enum OverlayStyle { Minimal, Compact, Detailed }

/// <summary>The readings the overlay can show, in their default order.</summary>
public static class OverlayMetric
{
    public const string Fps = "fps", FrameTime = "frametime", Low1 = "low1", Graph = "graph", Cpu = "cpu", CpuTemp = "cputemp",
        Gpu = "gpu", GpuTemp = "gputemp", Ram = "ram", Vram = "vram", Ping = "ping", Target = "target";

    public static readonly (string Id, string Label, string Hint)[] All =
    [
        (Fps, "FPS", "Frames per second of the measured game"),
        (FrameTime, "Frame time", "Average time between frames, ms"),
        (Low1, "1% low", "FPS of the slowest 1% of frames in the last second"),
        (Graph, "Frame-time graph", "The last 120 frame times"),
        (Cpu, "CPU usage", "Whole-CPU load"),
        (CpuTemp, "CPU temperature", "Only where this PC provides it"),
        (Gpu, "GPU usage", "Load of the GPU doing the most work"),
        (GpuTemp, "GPU temperature", "Of the same GPU"),
        (Ram, "RAM", "Memory in use"),
        (Vram, "VRAM", "Video memory in use on the same GPU"),
        (Ping, "Ping", "Round trip to your ping host (internet latency, not the game server)"),
        (Target, "Game name", "Which app is being measured, and its graphics API"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>Colour themes. Custom uses <see cref="OverlayLayout.TextColor"/> and <see cref="OverlayLayout.AccentColor"/>.</summary>
public static class OverlayTheme
{
    public sealed record Palette(string Name, string Text, string Accent, string Accent2, string Plate, string Warn);

    public static readonly Palette[] All =
    [
        new("Vector", "#FFF2F4F8", "#FF9479FF", "#FF2FD8EE", "#FF07080C", "#FFFFB020"),
        new("Mint", "#FFF0FFF6", "#FF2BE38B", "#FF7FD7FF", "#FF050A08", "#FFFFB020"),
        new("Ice", "#FFF2FAFF", "#FF5CC8FF", "#FFB4E9FF", "#FF05090D", "#FFFF8A5C"),
        new("Amber", "#FFFFF8EC", "#FFFFB020", "#FFFFD27A", "#FF0B0805", "#FFFF5C7A"),
        new("Mono", "#FFFFFFFF", "#FFFFFFFF", "#FFB8BEC9", "#FF000000", "#FFFFB020"),
    ];

    public static Palette Resolve(OverlayLayout l)
    {
        if (l.EffectiveTheme == "Custom") return new Palette("Custom", l.TextColor, l.AccentColor, l.AccentColor, "#FF05070B", "#FFFFB020");
        return All.FirstOrDefault(p => p.Name == l.EffectiveTheme) ?? All[0];
    }
}

/// <summary>What the overlay shows and how it looks.</summary>
public sealed class OverlayLayout
{
    /// <summary>Readings in display order (ids from <see cref="OverlayMetric"/>). Null in settings saved before 1.58: built from the switches below.</summary>
    public List<string>? Items { get; set; }
    public OverlayStyle Style { get; set; } = OverlayStyle.Detailed;
    /// <summary>Graph style (frame-time graph and usage bars) or text only.</summary>
    public bool Graphs { get; set; } = true;
    /// <summary>Theme name from <see cref="OverlayTheme"/> or "Custom". Null in older settings (see <see cref="EffectiveTheme"/>).</summary>
    public string? Theme { get; set; }
    /// <summary>Overall size, 0.6–2.5.</summary>
    public double Scale { get; set; } = 1;
    /// <summary>Opacity of the whole overlay, 0.3–1 (the background panel has its own <see cref="Opacity"/>).</summary>
    public double WindowOpacity { get; set; } = 1;
    /// <summary>Dark outline around the text, so it stays readable over bright scenes.</summary>
    public bool Outline { get; set; } = true;
    /// <summary>Dragged to a spot: top-left at (<see cref="X"/>, <see cref="Y"/>) as fractions of <see cref="Monitor"/>'s area.</summary>
    public bool CustomPosition { get; set; }
    public double X { get; set; } = 0.02;
    public double Y { get; set; } = 0.03;
    /// <summary>Display device name (\\.\DISPLAY2) for a custom position; null = the game's monitor.</summary>
    public string? Monitor { get; set; }

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

    /// <summary>The theme in use: older settings with their own colours keep them (Custom), otherwise Vector.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveTheme => Theme ?? (TextColor != "#FFFFFFFF" || AccentColor != "#FF2BE38B" ? "Custom" : "Vector");

    /// <summary>The readings to show, in order.</summary>
    public List<string> EffectiveItems()
    {
        if (Items is not null) return Items;
        var l = new List<string>();
        if (Fps) l.Add(OverlayMetric.Fps);
        if (FrameTime) l.Add(OverlayMetric.FrameTime);
        if (OnePercentLow) l.Add(OverlayMetric.Low1);
        if (Graph) l.Add(OverlayMetric.Graph);
        if (Cpu) l.Add(OverlayMetric.Cpu);
        if (Temps) l.Add(OverlayMetric.CpuTemp);
        if (Gpu) l.Add(OverlayMetric.Gpu);
        if (Temps) l.Add(OverlayMetric.GpuTemp);
        if (Ram) l.Add(OverlayMetric.Ram);
        if (Target) l.Add(OverlayMetric.Target);
        return l;
    }

    /// <summary>Turns a reading on or off (appended at the end) and keeps the original switches in step.</summary>
    public void SetItem(string id, bool on)
    {
        var items = Items ??= EffectiveItems();
        items.Remove(id);
        if (on) items.Add(id);
        SyncSwitches();
    }

    /// <summary>Moves a reading one place up (-1) or down (+1).</summary>
    public void Move(string id, int delta)
    {
        var items = Items ??= EffectiveItems();
        int i = items.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= items.Count) return;
        (items[i], items[j]) = (items[j], items[i]);
    }

    private void SyncSwitches()
    {
        var i = Items!;
        Fps = i.Contains(OverlayMetric.Fps); FrameTime = i.Contains(OverlayMetric.FrameTime); OnePercentLow = i.Contains(OverlayMetric.Low1);
        Graph = i.Contains(OverlayMetric.Graph); Cpu = i.Contains(OverlayMetric.Cpu); Gpu = i.Contains(OverlayMetric.Gpu); Ram = i.Contains(OverlayMetric.Ram);
        Temps = i.Contains(OverlayMetric.CpuTemp) || i.Contains(OverlayMetric.GpuTemp); Target = i.Contains(OverlayMetric.Target);
    }

    public OverlayLayout Clone()
    {
        var c = (OverlayLayout)MemberwiseClone();
        c.Items = Items is null ? null : [.. Items];
        return c;
    }
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
    /// <summary>Global hotkey that toggles move/resize mode (the overlay takes the mouse until you press it again).</summary>
    public string EditHotkey { get; set; } = "Ctrl+Shift+F11";
    /// <summary>Clicks pass through the overlay to the game. Off: the overlay can be dragged any time (it still never takes focus).</summary>
    public bool ClickThrough { get; set; } = true;
    /// <summary>Overlay refresh interval in ms (250, 500 or 1000).</summary>
    public int RefreshMs { get; set; } = 250;
    /// <summary>Version of the built-in game profiles already added (so a removed one isn't added back).</summary>
    public int DefaultsVersion { get; set; }
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

    /// <summary>
    /// Adds the built-in game profiles once: Fortnite (competitive, top left), Geometry Dash (minimal FPS readout, top right)
    /// and Roblox (compact). An existing Fortnite entry keeps its auto-show choice and gets its own layout only if it had none.
    /// </summary>
    public void EnsureDefaultGames()
    {
        if (DefaultsVersion >= 1) return;
        DefaultsVersion = 1;
        var fn = ProfileFor("FortniteClient-Win64-Shipping.exe");
        if (fn is null) Games.Add(fn = new OverlayGameProfile { Exe = "FortniteClient-Win64-Shipping.exe", Name = "Fortnite", AutoShow = true });
        if (!fn.CustomLayout)
        {
            fn.CustomLayout = true;
            fn.Layout = new OverlayLayout
            {
                Style = OverlayStyle.Compact, Theme = "Vector", Position = OverlayCorner.TopLeft, Margin = 10, Opacity = 0.5, FontSize = 13,
                Items = [OverlayMetric.Fps, OverlayMetric.Low1, OverlayMetric.FrameTime, OverlayMetric.Graph, OverlayMetric.Cpu, OverlayMetric.Gpu, OverlayMetric.GpuTemp, OverlayMetric.Ping],
            };
        }
        if (ProfileFor("GeometryDash.exe") is null)
            Games.Add(new OverlayGameProfile
            {
                Exe = "GeometryDash.exe", Name = "Geometry Dash", AutoShow = true, CustomLayout = true,
                Layout = new OverlayLayout { Style = OverlayStyle.Minimal, Theme = "Mint", Position = OverlayCorner.TopRight, Margin = 10, Opacity = 0.35, FontSize = 14, Items = [OverlayMetric.Fps, OverlayMetric.FrameTime, OverlayMetric.Low1] },
            });
        if (ProfileFor("RobloxPlayerBeta.exe") is null)
            Games.Add(new OverlayGameProfile
            {
                Exe = "RobloxPlayerBeta.exe", Name = "Roblox", AutoShow = true, CustomLayout = true,
                Layout = new OverlayLayout { Style = OverlayStyle.Compact, Theme = "Ice", Position = OverlayCorner.TopRight, Margin = 12, Opacity = 0.45, FontSize = 13, Items = [OverlayMetric.Fps, OverlayMetric.FrameTime, OverlayMetric.Cpu, OverlayMetric.Gpu, OverlayMetric.Ram, OverlayMetric.Ping] },
            });
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MONITORINFOEX { public int Size; public RECT Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorEnum(nint m, nint dc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum cb, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfoEx(nint m, ref MONITORINFOEX mi);

    /// <summary>Every display: device name and bounds in physical pixels, primary first.</summary>
    public static List<(string device, int x, int y, int w, int h, bool primary)> Displays()
    {
        var list = new List<(string, int, int, int, int, bool)>();
        MonitorEnum cb = (m, _, _, _) =>
        {
            var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoEx(m, ref mi)) list.Add((mi.Device, mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, mi.Monitor.Bottom - mi.Monitor.Top, (mi.Flags & 1) != 0));
            return true;
        };
        EnumDisplayMonitors(0, 0, cb, 0);
        GC.KeepAlive(cb);
        return [.. list.OrderByDescending(d => d.Item6)];
    }

    /// <summary>Device name of the display a window is on, or null.</summary>
    public static string? DeviceOf(nint hwnd)
    {
        if (hwnd == 0) return null;
        var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoEx(MonitorFromWindow(hwnd, 2), ref mi) ? mi.Device : null;
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
