using System.Runtime.InteropServices;
using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Display;

/// <summary>Windows scaling request sent with a mode change (DEVMODE.dmDisplayFixedOutput).</summary>
public enum ScalingMode
{
    /// <summary>DMDFO_DEFAULT: the GPU/monitor decide (usually keeps the aspect ratio).</summary>
    Default = 0,
    /// <summary>DMDFO_STRETCH: fill the whole screen (stretched).</summary>
    Stretch = 1,
    /// <summary>DMDFO_CENTER: no scaling, centred with black borders.</summary>
    Center = 2,
}

public sealed record DisplayMode(int Width, int Height, int Hz, int Bpp)
{
    public override string ToString() => $"{Width}×{Height} @ {Hz} Hz";
}

public sealed record DisplayInfo(string DeviceName, string AdapterName, string MonitorName, bool Primary, int X, int Y,
    DisplayMode Current, ScalingMode CurrentScaling, DisplayMode Registry, List<DisplayMode> Modes)
{
    /// <summary>Largest mode the driver reports. Windows doesn't expose EDID "native" here; for LCDs this is the native panel resolution.</summary>
    public DisplayMode? Native => Modes.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Hz).FirstOrDefault();
    public string Label => $"{(Primary ? "★ " : "")}{MonitorName} ({DeviceName.Replace(@"\\.\", "")})";
    public IEnumerable<int> RatesFor(int w, int h) => Modes.Where(m => m.Width == w && m.Height == h).Select(m => m.Hz).Distinct().OrderByDescending(x => x);
    public bool Reports(int w, int h) => Modes.Any(m => m.Width == w && m.Height == h);
}

public enum ChangeResult { Ok, NeedsRestart, Failed, BadMode, NotUpdated, BadFlags, BadParam, BadDualView, Unknown }

/// <summary>
/// Enumerates displays and changes the desktop resolution with documented user32 APIs (EnumDisplayDevices,
/// EnumDisplaySettingsEx, ChangeDisplaySettingsEx). No administrator rights are needed. Every change is first
/// validated with CDS_TEST; changes are applied dynamically (not saved) until the user confirms.
/// </summary>
public static class DisplayManager
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, int index, ref DISPLAY_DEVICE dd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DEVMODE dm, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, nint hwnd, int flags, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, nint dm, nint hwnd, int flags, nint param);

    private const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4, DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    private const int CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;
    private const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000, DM_DISPLAYFIXEDOUTPUT = 0x20000000;

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static List<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        for (int i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 || (dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            var monitorName = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString) ? mon.DeviceString : "Display";
            var cur = NewMode();
            if (!EnumDisplaySettingsEx(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur, 0)) continue;
            var reg = NewMode();
            var hasReg = EnumDisplaySettingsEx(dd.DeviceName, ENUM_REGISTRY_SETTINGS, ref reg, 0);
            var modes = new HashSet<DisplayMode>();
            for (int m = 0; ; m++)
            {
                var dm = NewMode();
                if (!EnumDisplaySettingsEx(dd.DeviceName, m, ref dm, 0)) break;
                if (dm.dmBitsPerPel >= 24 && dm.dmPelsWidth > 0) modes.Add(new(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, 32));
            }
            list.Add(new(dd.DeviceName, dd.DeviceString, monitorName, (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, cur.dmPositionX, cur.dmPositionY,
                new(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, cur.dmBitsPerPel), (ScalingMode)Math.Clamp(cur.dmDisplayFixedOutput, 0, 2),
                hasReg ? new(reg.dmPelsWidth, reg.dmPelsHeight, reg.dmDisplayFrequency, reg.dmBitsPerPel) : new(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, cur.dmBitsPerPel),
                modes.OrderByDescending(x => (long)x.Width * x.Height).ThenByDescending(x => x.Hz).ToList()));
        }
        return list.OrderByDescending(d => d.Primary).ThenBy(d => d.X).ToList();
    }

    public static DisplayInfo? Find(string device) => Displays().FirstOrDefault(d => d.DeviceName == device);

    private static ChangeResult Map(int code) => code switch
    {
        0 => ChangeResult.Ok, 1 => ChangeResult.NeedsRestart, -1 => ChangeResult.Failed, -2 => ChangeResult.BadMode, -3 => ChangeResult.NotUpdated,
        -4 => ChangeResult.BadFlags, -5 => ChangeResult.BadParam, -6 => ChangeResult.BadDualView, _ => ChangeResult.Unknown,
    };

    public static string Explain(ChangeResult r, int w, int h, int hz) => r switch
    {
        ChangeResult.Ok => "OK",
        ChangeResult.NeedsRestart => "Windows says this change needs a restart, so it wasn't applied.",
        ChangeResult.BadMode => $"Your display driver doesn't support {w}×{h}{(hz > 0 ? $" at {hz} Hz" : "")}. Create it first as a custom resolution: NVIDIA Control Panel → Display → Change resolution → Customize; AMD Software → Gaming → Display → Custom Resolutions; Intel Graphics Command Center → Display → Custom Resolution. Then click Refresh.",
        ChangeResult.Failed => "The display driver refused the change.",
        ChangeResult.NotUpdated => "Windows couldn't save the setting.",
        ChangeResult.BadParam or ChangeResult.BadFlags => "Windows rejected the request parameters.",
        ChangeResult.BadDualView => "This system uses DualView; Windows can't change this display on its own.",
        _ => "Windows returned an unknown error.",
    };

    private static DEVMODE Build(string device, int w, int h, int hz, ScalingMode? scaling)
    {
        var dm = NewMode();
        EnumDisplaySettingsEx(device, ENUM_CURRENT_SETTINGS, ref dm, 0); // keep position/orientation/bit depth
        dm.dmPelsWidth = w; dm.dmPelsHeight = h;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        if (hz > 0) { dm.dmDisplayFrequency = hz; dm.dmFields |= DM_DISPLAYFREQUENCY; }
        if (scaling is { } s) { dm.dmDisplayFixedOutput = (int)s; dm.dmFields |= DM_DISPLAYFIXEDOUTPUT; }
        return dm;
    }

    /// <summary>Asks Windows/the driver whether the mode would work, without changing anything.</summary>
    public static ChangeResult Test(string device, int w, int h, int hz, ScalingMode? scaling = null)
    {
        var dm = Build(device, w, h, hz, scaling);
        return Map(ChangeDisplaySettingsEx(device, ref dm, 0, CDS_TEST, 0));
    }

    /// <summary>Changes the mode for this session only (not saved). Reverting is <see cref="RevertToSaved"/>.</summary>
    public static ChangeResult ApplyTemporary(string device, int w, int h, int hz, ScalingMode? scaling = null)
    {
        var test = Test(device, w, h, hz, scaling);
        if (test != ChangeResult.Ok) return test;
        var dm = Build(device, w, h, hz, scaling);
        var r = Map(ChangeDisplaySettingsEx(device, ref dm, 0, 0, 0));
        Log.Info($"Display {device}: temporary {w}×{h}@{hz} scaling={scaling?.ToString() ?? "unchanged"} -> {r}");
        return r;
    }

    /// <summary>Saves the current (confirmed) mode as the display's setting so it survives sign-out/restart.</summary>
    public static ChangeResult Commit(string device)
    {
        var dm = NewMode();
        if (!EnumDisplaySettingsEx(device, ENUM_CURRENT_SETTINGS, ref dm, 0)) return ChangeResult.Failed;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL | DM_DISPLAYFIXEDOUTPUT;
        var r = Map(ChangeDisplaySettingsEx(device, ref dm, 0, CDS_UPDATEREGISTRY, 0));
        Log.Info($"Display {device}: committed {dm.dmPelsWidth}×{dm.dmPelsHeight}@{dm.dmDisplayFrequency} -> {r}");
        return r;
    }

    /// <summary>Drops any temporary change and returns to the saved (registry) mode.</summary>
    public static ChangeResult RevertToSaved(string device)
    {
        var r = Map(ChangeDisplaySettingsEx(device, 0, 0, 0, 0));
        Log.Info($"Display {device}: reverted to saved mode -> {r}");
        return r;
    }

    /// <summary>Applies and saves a specific mode (used to restore the original mode).</summary>
    public static ChangeResult ApplyAndSave(string device, DisplayMode m, ScalingMode scaling)
    {
        var dm = Build(device, m.Width, m.Height, m.Hz, scaling);
        if (m.Bpp > 0) { dm.dmBitsPerPel = m.Bpp; dm.dmFields |= DM_BITSPERPEL; }
        var test = Map(ChangeDisplaySettingsEx(device, ref dm, 0, CDS_TEST, 0));
        if (test != ChangeResult.Ok) return test;
        var r = Map(ChangeDisplaySettingsEx(device, ref dm, 0, CDS_UPDATEREGISTRY, 0));
        Log.Info($"Display {device}: applied and saved {m} scaling={scaling} -> {r}");
        return r;
    }

    // ---- aspect ratio --------------------------------------------------------------------------------------------
    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    // Sorted by ratio: "between X and Y" relies on the order.
    private static readonly (string name, double ratio)[] Common =
        [("5:4", 1.25), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:10", 1.6), ("16:9", 16.0 / 9), ("21:9", 21.0 / 9), ("32:9", 32.0 / 9)];

    /// <summary>"1440×1080 → 4:3", "1720×1080 → 43:27 (≈1.59, close to 16:10)".</summary>
    public static string AspectLabel(int w, int h)
    {
        if (w <= 0 || h <= 0) return "–";
        int g = Gcd(w, h);
        var exact = $"{w / g}:{h / g}";
        double r = (double)w / h;
        var near = Common.OrderBy(c => Math.Abs(c.ratio - r)).First();
        if (Math.Abs(near.ratio - r) < 0.005) return near.name;                        // e.g. 8:5 is shown as 16:10
        if (Math.Abs(near.ratio - r) < 0.04) return $"≈{near.name} ({exact})";         // e.g. 2560×1080 = 64:27, sold as 21:9
        return $"{exact} (≈{r:0.00}:1, between {Common.Where(c => c.ratio < r).Select(c => c.name).LastOrDefault() ?? "–"} and {Common.FirstOrDefault(c => c.ratio > r).name ?? "–"})";
    }

    public static List<string> Validate(int w, int h)
    {
        var e = new List<string>();
        if (w is < 640 or > 7680) e.Add("Width must be between 640 and 7680.");
        if (h is < 480 or > 4320) e.Add("Height must be between 480 and 4320.");
        return e;
    }
}

public sealed class ResolutionPreset
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool BuiltIn { get; init; }
}

/// <summary>Original modes (saved before the first change, never overwritten until restored), a pending temporary
/// change (for crash recovery) and user presets. display-state.json.</summary>
public sealed class DisplayState
{
    public Dictionary<string, OriginalMode> Originals { get; set; } = [];
    public string? PendingTemporaryDevice { get; set; }
    public List<ResolutionPreset> Presets { get; set; } = [];

    public sealed class OriginalMode
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Hz { get; set; }
        public int Bpp { get; set; }
        public ScalingMode Scaling { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public DisplayMode Mode => new(Width, Height, Hz, Bpp);
    }

    public static IReadOnlyList<ResolutionPreset> BuiltIns { get; } =
    [
        new() { Name = "1920×1080 (16:9)", Width = 1920, Height = 1080, BuiltIn = true },
        new() { Name = "1728×1080 (16:10 stretched)", Width = 1728, Height = 1080, BuiltIn = true },
        new() { Name = "1720×1080 (stretched)", Width = 1720, Height = 1080, BuiltIn = true },
        new() { Name = "1680×1050 (16:10)", Width = 1680, Height = 1050, BuiltIn = true },
        new() { Name = "1600×1080 (stretched)", Width = 1600, Height = 1080, BuiltIn = true },
        new() { Name = "2288×1440 (16:10 stretched, 1440p)", Width = 2288, Height = 1440, BuiltIn = true },
        new() { Name = "1440×1080 (4:3 stretched)", Width = 1440, Height = 1080, BuiltIn = true },
        new() { Name = "1280×1024 (5:4)", Width = 1280, Height = 1024, BuiltIn = true },
        new() { Name = "1280×960 (4:3)", Width = 1280, Height = 960, BuiltIn = true },
        new() { Name = "1024×768 (4:3)", Width = 1024, Height = 768, BuiltIn = true },
        new() { Name = "2560×1080 (21:9)", Width = 2560, Height = 1080, BuiltIn = true },
    ];

    private static string PathFor => Path.Combine(AppPaths.Root, "display-state.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static DisplayState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<DisplayState>(File.ReadAllText(PathFor), Json) ?? new(); }
        catch (Exception ex) { Log.Warn("display-state.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = PathFor + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, PathFor, true);
    }

    /// <summary>Remembers a display's mode before Money Tweaks first changes it.</summary>
    public void RememberOriginal(DisplayInfo d)
    {
        if (Originals.ContainsKey(d.DeviceName)) return;
        Originals[d.DeviceName] = new() { Width = d.Registry.Width, Height = d.Registry.Height, Hz = d.Registry.Hz, Bpp = d.Registry.Bpp, Scaling = d.CurrentScaling };
    }
}
