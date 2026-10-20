using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoneyTweaks.Core.Crosshair;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayerKind { Lines, Dot, Circle, Image }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DotShape { Square, Round }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FireTrigger { LeftMouse, RightMouse, EitherMouse }

/// <summary>
/// One component of a crosshair. Units are screen pixels at scale 1. Which properties matter depends on
/// <see cref="Kind"/>: lines use arms/length/thickness/gap, dots use size/shape, circles use size (radius) and
/// thickness, images use size (width) and the stored aspect ratio.
/// </summary>
public sealed class CrosshairLayer
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public LayerKind Kind { get; set; } = LayerKind.Lines;
    public bool Visible { get; set; } = true;
    public string Color { get; set; } = "#FF2BE38B";
    public double Opacity { get; set; } = 1;
    public double Thickness { get; set; } = 2;
    public double Length { get; set; } = 10;
    public double Gap { get; set; } = 4;
    public bool Top { get; set; } = true;
    public bool Bottom { get; set; } = true;
    public bool Left { get; set; } = true;
    public bool Right { get; set; } = true;
    public bool RoundEnds { get; set; }
    /// <summary>Dot diameter, circle radius or image width.</summary>
    public double Size { get; set; } = 4;
    public DotShape DotShape { get; set; } = DotShape.Square;
    public bool Outline { get; set; } = true;
    public string OutlineColor { get; set; } = "#D9000000";
    public double OutlineThickness { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    /// <summary>Degrees, clockwise, around the layer's centre (the crosshair centre plus the offset).</summary>
    public double Rotation { get; set; }
    /// <summary>Follows the firing animation (gap/radius/size grows).</summary>
    public bool ReactsToFiring { get; set; } = true;
    /// <summary>Image layers: file name inside the crosshair image folder (no path).</summary>
    public string? ImageFile { get; set; }
    /// <summary>Image layers: height / width of the imported picture.</summary>
    public double ImageAspect { get; set; } = 1;

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    public static string DefaultName(LayerKind k) => k switch
    {
        LayerKind.Lines => "Lines",
        LayerKind.Dot => "Centre dot",
        LayerKind.Circle => "Circle",
        _ => "Image",
    };
}

/// <summary>What happens while you fire: the crosshair spreads (bloom), fades or flashes, then recovers.</summary>
public sealed class FiringAnimation
{
    public bool Enabled { get; set; }
    public FireTrigger Trigger { get; set; } = FireTrigger.LeftMouse;
    /// <summary>Pixels added to gaps and circle radii at full bloom.</summary>
    public double Spread { get; set; } = 6;
    /// <summary>Time to reach full bloom while the button is held.</summary>
    public double RiseMs { get; set; } = 120;
    /// <summary>Time to return to rest after release.</summary>
    public double RecoverMs { get; set; } = 220;
    /// <summary>Opacity multiplier at full bloom (1 = no fade).</summary>
    public double FiringOpacity { get; set; } = 1;
    public bool Flash { get; set; }
    public string FlashColor { get; set; } = "#FFFF3B3B";
    /// <summary>Hide the crosshair while the right mouse button is held (aiming down sights).</summary>
    public bool HideWhileAiming { get; set; }

    /// <summary>Whether the overlay needs to read mouse buttons at all.</summary>
    public bool NeedsInput => Enabled || HideWhileAiming;
}

public sealed class CrosshairProfile
{
    public string Id { get; set; } = CrosshairLayer.NewId();
    public string Name { get; set; } = "Crosshair";
    public List<CrosshairLayer> Layers { get; set; } = [];
    public FiringAnimation Firing { get; set; } = new();
    /// <summary>Snap unrotated shapes to whole pixels so thin lines stay sharp.</summary>
    public bool PixelPerfect { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    /// <summary>Serializer settings for crosshair data (NaN/Infinity are written, then clamped by the sanitizer on load).</summary>
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public CrosshairProfile Clone() => JsonSerializer.Deserialize<CrosshairProfile>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>A copy with fresh ids, for duplicating or importing.</summary>
    public CrosshairProfile Copy(string name)
    {
        var c = Clone();
        c.Id = CrosshairLayer.NewId();
        c.Name = name;
        c.Created = c.Modified = DateTime.Now;
        foreach (var l in c.Layers) l.Id = CrosshairLayer.NewId();
        return c;
    }
}

/// <summary>Where and how the desktop overlay is drawn, and its hotkeys.</summary>
public sealed class CrosshairOverlayOptions
{
    /// <summary>0 = primary monitor, then the others in Windows' order.</summary>
    public int MonitorIndex { get; set; }
    /// <summary>Move to the monitor of the focused game window (a listed game, or any borderless/fullscreen window).</summary>
    public bool FollowGameMonitor { get; set; }
    /// <summary>Offset of the crosshair centre from the monitor centre, pixels.</summary>
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+X";
    public string NextHotkey { get; set; } = "Ctrl+Alt+Right";
    public string PreviousHotkey { get; set; } = "Ctrl+Alt+Left";
    /// <summary>Opt-in: show the crosshair when Money Tweaks starts.</summary>
    public bool ShowOnStart { get; set; }
}

/// <summary>A game that switches to its own crosshair profile while it's in front.</summary>
public sealed class CrosshairGameRule
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProfileId { get; set; } = "";
    /// <summary>Show the crosshair when the game comes to the front (and hide it again when the game closes).</summary>
    public bool AutoShow { get; set; } = true;
}

public sealed class CrosshairData
{
    public int Version { get; set; } = 1;
    public List<CrosshairProfile> Profiles { get; set; } = [];
    public string ActiveId { get; set; } = "";
    public CrosshairOverlayOptions Overlay { get; set; } = new();
    public bool AutoSwitch { get; set; }
    /// <summary>Hide an automatically shown crosshair while its game isn't the focused window.</summary>
    public bool HideWhenGameUnfocused { get; set; } = true;
    public List<CrosshairGameRule> Games { get; set; } = [];
    /// <summary>Editor preview: background ("Dark", "Grey", "Sky", "Grass", "Image"), magnification and pixel grid.</summary>
    public string PreviewBackground { get; set; } = "Dark";
    public double PreviewZoom { get; set; } = 3;
    public bool PreviewGrid { get; set; }
    /// <summary>The user's own screenshot used as preview background (a path on this PC).</summary>
    public string? PreviewImage { get; set; }

    [JsonIgnore]
    public CrosshairProfile Active
    {
        get
        {
            if (Profiles.Count == 0) Profiles.Add(CrosshairTemplates.Create("Classic cross"));
            var p = Profiles.FirstOrDefault(p => p.Id == ActiveId);
            if (p is null) { p = Profiles[0]; ActiveId = p.Id; }
            return p;
        }
    }

    public CrosshairProfile? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);

    public CrosshairGameRule? RuleFor(string? exe) => exe is null ? null :
        Games.FirstOrDefault(g => string.Equals(NormalizeExe(g.Exe), NormalizeExe(exe), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeExe(string exe)
    {
        var n = Path.GetFileName(exe.Trim());
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }

    /// <summary>A unique name: "Name", "Name 2", "Name 3"…</summary>
    public string UniqueName(string name)
    {
        name = name.Trim().Length == 0 ? "Crosshair" : name.Trim();
        if (Profiles.All(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        var stem = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\d+$", "");
        for (int i = 2; ; i++)
        {
            var n = $"{stem} {i}";
            if (Profiles.All(p => !string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) return n;
        }
    }
}

/// <summary>Starting points for new profiles (the original Money Tweaks styles plus a few common ones).</summary>
public static class CrosshairTemplates
{
    public static readonly string[] Names = ["Classic cross", "Cross + dot", "T-shape", "Dot", "Circle + dot", "Small precise", "Empty"];

    public static CrosshairProfile Create(string name)
    {
        var p = new CrosshairProfile { Name = name };
        CrosshairLayer Lines(double len, double t, double gap) => new() { Name = "Lines", Kind = LayerKind.Lines, Length = len, Thickness = t, Gap = gap };
        CrosshairLayer Dot(double d) => new() { Name = "Centre dot", Kind = LayerKind.Dot, Size = d, ReactsToFiring = false };
        switch (name)
        {
            case "Classic cross": p.Layers.Add(Lines(10, 2, 4)); break;
            case "Cross + dot": p.Layers.Add(Lines(10, 2, 4)); p.Layers.Add(Dot(2)); break;
            case "T-shape": var t = Lines(10, 2, 4); t.Top = false; p.Layers.Add(t); break;
            case "Dot": p.Layers.Add(Dot(4)); break;
            case "Circle + dot":
                p.Layers.Add(new CrosshairLayer { Name = "Circle", Kind = LayerKind.Circle, Size = 14, Thickness = 2 });
                p.Layers.Add(Dot(2));
                break;
            case "Small precise":
                var s = Lines(5, 1, 3); s.Color = "#FF00FFFF"; p.Layers.Add(s);
                var d = Dot(1); d.Color = "#FF00FFFF"; p.Layers.Add(d);
                break;
        }
        return p;
    }

#if !MT_COMMUNITY_SERVER
    /// <summary>The single crosshair saved by Money Tweaks 1.40 and earlier, as a profile that looks the same.</summary>
    public static CrosshairProfile FromLegacy(Features.CrosshairSettings s)
    {
        var p = new CrosshairProfile { Name = "My crosshair" };
        CrosshairLayer Base(LayerKind kind, string name) => new()
        {
            Kind = kind, Name = name, Color = s.Color, Opacity = Math.Clamp(s.Opacity, 0.05, 1), Outline = s.Outline,
            OutlineColor = "#D9000000", OutlineThickness = 1, Thickness = s.Thickness, Length = s.Size, Gap = s.Gap,
        };
        CrosshairLayer Dot(double d) { var l = Base(LayerKind.Dot, "Centre dot"); l.Size = d; l.ReactsToFiring = false; return l; }
        switch (s.Style)
        {
            case "Dot": p.Layers.Add(Dot(Math.Max(2, s.Thickness * 2))); break;
            case "Circle":
                var c = Base(LayerKind.Circle, "Circle"); c.Size = s.Size + s.Gap; p.Layers.Add(c);
                p.Layers.Add(Dot(Math.Max(2, s.Thickness)));
                break;
            default:
                var l = Base(LayerKind.Lines, "Lines");
                if (s.Style == "T") l.Top = false;
                p.Layers.Add(l);
                if (s.Style == "CrossDot") p.Layers.Add(Dot(Math.Max(2, s.Thickness)));
                break;
        }
        return p;
    }
#endif
}
