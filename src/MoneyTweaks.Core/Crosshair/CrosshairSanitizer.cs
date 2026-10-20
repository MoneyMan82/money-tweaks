using System.Text.RegularExpressions;

namespace MoneyTweaks.Core.Crosshair;

/// <summary>Clamps every value to a sane range, so a hand-edited file or a pasted code can't produce a broken overlay.</summary>
public static class CrosshairSanitizer
{
    public const int MaxLayers = 64, MaxName = 60;

    public static void Clean(CrosshairData d)
    {
        d.Profiles ??= [];
        d.Profiles.RemoveAll(p => p is null);
        foreach (var p in d.Profiles) Clean(p);
        var seen = new HashSet<string>();
        foreach (var p in d.Profiles) if (!seen.Add(p.Id)) p.Id = CrosshairLayer.NewId();
        if (d.Profiles.Count == 0) d.Profiles.Add(CrosshairTemplates.Create("Classic cross"));
        if (d.Profiles.All(p => p.Id != d.ActiveId)) d.ActiveId = d.Profiles[0].Id;
        d.Overlay ??= new();
        var o = d.Overlay;
        o.MonitorIndex = Math.Clamp(o.MonitorIndex, 0, 16);
        o.PositionX = Clamp(o.PositionX, -4000, 4000, 0);
        o.PositionY = Clamp(o.PositionY, -4000, 4000, 0);
        o.Scale = Clamp(o.Scale, 0.25, 8, 1);
        o.Opacity = Clamp(o.Opacity, 0.05, 1, 1);
        o.ToggleHotkey ??= ""; o.NextHotkey ??= ""; o.PreviousHotkey ??= "";
        d.PreviewZoom = Clamp(d.PreviewZoom, 1, 8, 3);
        d.PreviewBackground ??= "Dark";
        d.Games ??= [];
        d.Games.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Exe));
        foreach (var g in d.Games) { g.Name ??= ""; g.ProfileId ??= ""; }
    }

    public static void Clean(CrosshairProfile p)
    {
        p.Id = string.IsNullOrWhiteSpace(p.Id) ? CrosshairLayer.NewId() : p.Id;
        p.Name = Name(p.Name, "Crosshair");
        p.Layers ??= [];
        p.Layers.RemoveAll(l => l is null);
        if (p.Layers.Count > MaxLayers) p.Layers.RemoveRange(MaxLayers, p.Layers.Count - MaxLayers);
        var ids = new HashSet<string>();
        foreach (var l in p.Layers)
        {
            if (string.IsNullOrWhiteSpace(l.Id) || !ids.Add(l.Id)) { l.Id = CrosshairLayer.NewId(); ids.Add(l.Id); }
            if (!Enum.IsDefined(l.Kind)) l.Kind = LayerKind.Lines;
            if (!Enum.IsDefined(l.DotShape)) l.DotShape = DotShape.Square;
            l.Name = Name(l.Name, CrosshairLayer.DefaultName(l.Kind));
            l.Color = Color(l.Color, "#FF2BE38B");
            l.OutlineColor = Color(l.OutlineColor, "#D9000000");
            l.Opacity = Clamp(l.Opacity, 0, 1, 1);
            l.Thickness = Clamp(l.Thickness, 0.5, 50, 2);
            l.Length = Clamp(l.Length, 0, 300, 10);
            l.Gap = Clamp(l.Gap, -50, 300, 4);
            l.Size = Clamp(l.Size, 0.5, 1000, 4);
            l.OutlineThickness = Clamp(l.OutlineThickness, 0, 10, 1);
            l.OffsetX = Clamp(l.OffsetX, -2000, 2000, 0);
            l.OffsetY = Clamp(l.OffsetY, -2000, 2000, 0);
            l.Rotation = Clamp(l.Rotation, -360, 360, 0);
            l.ImageAspect = Clamp(l.ImageAspect, 0.01, 100, 1);
            if (l.ImageFile is not null && !IsValidImageRef(l.ImageFile)) l.ImageFile = null;
        }
        p.Firing ??= new();
        var f = p.Firing;
        if (!Enum.IsDefined(f.Trigger)) f.Trigger = FireTrigger.LeftMouse;
        f.Spread = Clamp(f.Spread, 0, 100, 6);
        f.RiseMs = Clamp(f.RiseMs, 1, 5000, 120);
        f.RecoverMs = Clamp(f.RecoverMs, 1, 5000, 220);
        f.FiringOpacity = Clamp(f.FiringOpacity, 0, 1, 1);
        f.FlashColor = Color(f.FlashColor, "#FFFF3B3B");
    }

    private static readonly Regex LocalImage = new("^[0-9a-f]{40}\\.png$", RegexOptions.Compiled);
    private static readonly Regex CommunityImage = new("^img:[0-9a-f]{32}$", RegexOptions.Compiled);

    /// <summary>A stored PNG on this PC ("&lt;sha1&gt;.png") or an image attached to a Community Hub post ("img:&lt;id&gt;").</summary>
    public static bool IsValidImageRef(string name) => LocalImage.IsMatch(name) || CommunityImage.IsMatch(name);

    /// <summary>Only the local form ("&lt;sha1&gt;.png").</summary>
    public static bool IsLocalImageName(string name) => LocalImage.IsMatch(name);

    public static double Clamp(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public static string Name(string? s, string fallback)
    {
        s = Regex.Replace(s ?? "", @"[\p{C}]", "").Trim();
        if (s.Length > MaxName) s = s[..MaxName].Trim();
        return s.Length == 0 ? fallback : s;
    }

    public static string Color(string? s, string fallback) => Argb.TryParse(s, out var c) ? c.ToString() : fallback;
}
