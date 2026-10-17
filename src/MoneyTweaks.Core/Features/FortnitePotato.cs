using System.Globalization;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public enum FnPreset { Competitive, Potato, UltraPotato, Custom }

/// <summary>
/// Fortnite graphics choices for the Potato Graphics studio. Every property maps to a setting Fortnite itself
/// exposes in Settings › Video and stores in GameUserSettings.ini (0 = Low … 3 = Epic).
/// </summary>
public sealed class FortnitePotatoSettings
{
    public FnPreset Preset { get; set; } = FnPreset.Potato;
    /// <summary>Rendering Mode: Performance (DirectX 11, low-end renderer).</summary>
    public bool PerformanceMode { get; set; } = true;
    /// <summary>Performance-mode "Meshes: Low": the rounded, simplified "bubble-wrap" geometry.</summary>
    public bool LowMeshes { get; set; } = true;
    public int ViewDistance { get; set; }
    public int Textures { get; set; }
    public int Effects { get; set; }
    public int Foliage { get; set; }
    public bool ShowGrass { get; set; }
    public int Shadows { get; set; }
    public int PostProcessing { get; set; }
    /// <summary>Global illumination + reflections.</summary>
    public int Lighting { get; set; }
    /// <summary>Shading, landscape and anti-aliasing quality.</summary>
    public int SurfaceDetail { get; set; }
    /// <summary>3D resolution percent. 100 keeps the image sharp; the potato look comes from meshes, not blur.</summary>
    public int RenderScale { get; set; } = 100;
    public bool UncapFps { get; set; } = true;
    /// <summary>CosmeticStreamingEnabled off: loads skins up front to avoid mid-match texture pop-in/stutter.</summary>
    public bool PreloadCosmetics { get; set; }
    public bool Stretched { get; set; }
    public int StretchWidth { get; set; } = 1440;
    public int StretchHeight { get; set; } = 1080;

    public static FortnitePotatoSettings FromPreset(FnPreset p) => p switch
    {
        FnPreset.Competitive => new() { Preset = p, PerformanceMode = true, LowMeshes = false, ViewDistance = 2, Textures = 1, Effects = 0, Foliage = 0, ShowGrass = false, RenderScale = 100, UncapFps = true },
        FnPreset.UltraPotato => new() { Preset = p, PerformanceMode = true, LowMeshes = true, ViewDistance = 0, Textures = 0, Effects = 0, Foliage = 0, ShowGrass = false, RenderScale = 100, UncapFps = true, PreloadCosmetics = true },
        _ => new() { Preset = FnPreset.Potato, PerformanceMode = true, LowMeshes = true, ViewDistance = 1, Textures = 0, Effects = 0, Foliage = 0, ShowGrass = false, RenderScale = 100, UncapFps = true },
    };

    public FortnitePotatoSettings Clone() => (FortnitePotatoSettings)MemberwiseClone();
}

/// <summary>One planned edit: the label shown to the user, the INI key, the value now in the file (null = absent) and the new value.</summary>
public sealed record SettingChange(string Label, string Key, string? Current, string New)
{
    public bool Missing => Current is null;
    public bool Changes => Current is not null && !string.Equals(Current.Trim(), New, StringComparison.OrdinalIgnoreCase);
}

public static class FortnitePotato
{
    public static PotatoGame Game => PotatoGraphics.Games.First(g => g.Id == "fortnite");
    public static string[] LevelNames { get; } = ["Low", "Medium", "High", "Epic"];

    private static string Q(int v) => Math.Clamp(v, 0, 3).ToString(CultureInfo.InvariantCulture);

    /// <summary>The exact INI edits for these settings, in display order. "Section|Key" keys are section-scoped.</summary>
    public static List<(string label, string key, string value)> Values(FortnitePotatoSettings s)
    {
        var v = new List<(string, string, string)>();
        void Add(string label, string key, string value) => v.Add((label, key, value));
        if (s.PerformanceMode)
        {
            Add("Rendering mode: Performance (DirectX 11)", "PreferredRHI", "dx11");
            Add("Rendering mode: Performance feature level", "PreferredFeatureLevel", "es31");
            if (s.LowMeshes) Add("Meshes: Low (rounded low-poly)", "PerformanceMode|MeshQuality", "0");
        }
        Add($"View distance: {LevelNames[s.ViewDistance]}", "sg.ViewDistanceQuality", Q(s.ViewDistance));
        Add($"Textures: {LevelNames[s.Textures]}", "sg.TextureQuality", Q(s.Textures));
        Add($"Effects: {LevelNames[s.Effects]}", "sg.EffectsQuality", Q(s.Effects));
        Add($"Foliage: {LevelNames[s.Foliage]}", "sg.FoliageQuality", Q(s.Foliage));
        Add($"Show grass: {(s.ShowGrass ? "On" : "Off")}", "bShowGrass", s.ShowGrass ? "True" : "False");
        Add($"Shadows: {LevelNames[s.Shadows]}", "sg.ShadowQuality", Q(s.Shadows));
        Add($"Post-processing: {LevelNames[s.PostProcessing]}", "sg.PostProcessQuality", Q(s.PostProcessing));
        Add($"Global illumination: {LevelNames[s.Lighting]}", "sg.GlobalIlluminationQuality", Q(s.Lighting));
        Add($"Reflections: {LevelNames[s.Lighting]}", "sg.ReflectionQuality", Q(s.Lighting));
        Add($"Global illumination (saved choice): {LevelNames[s.Lighting]}", "DesiredGlobalIlluminationQuality", Q(s.Lighting));
        Add($"Reflections (saved choice): {LevelNames[s.Lighting]}", "DesiredReflectionQuality", Q(s.Lighting));
        Add($"Shading: {LevelNames[s.SurfaceDetail]}", "sg.ShadingQuality", Q(s.SurfaceDetail));
        Add($"Landscape: {LevelNames[s.SurfaceDetail]}", "sg.LandscapeQuality", Q(s.SurfaceDetail));
        Add($"Anti-aliasing: {LevelNames[s.SurfaceDetail]}", "sg.AntiAliasingQuality", Q(s.SurfaceDetail));
        Add("Nanite virtualized geometry: Off", "bUseNanite", "False");
        Add("Hardware ray tracing: Off", "bRayTracing", "False");
        Add("Motion blur: Off", "bMotionBlur", "False");
        Add("V-Sync: Off", "bUseVSync", "False");
        Add($"3D resolution: {Math.Clamp(s.RenderScale, 50, 100)}%", "sg.ResolutionQuality", Math.Clamp(s.RenderScale, 50, 100).ToString("0.000000", CultureInfo.InvariantCulture));
        Add("Dynamic 3D resolution: Off", "bUseDynamicResolution", "False");
        if (s.UncapFps) Add("Frame rate limit: Unlimited", "FrameRateLimit", "0.000000");
        if (s.PreloadCosmetics) Add("Pre-download stream cosmetics: Off (load up front)", "CosmeticStreamingEnabled", "CodeSet_Disabled");
        if (s.Stretched)
        {
            string w = s.StretchWidth.ToString(CultureInfo.InvariantCulture), h = s.StretchHeight.ToString(CultureInfo.InvariantCulture);
            foreach (var k in new[] { "ResolutionSizeX", "LastUserConfirmedResolutionSizeX", "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" }) Add($"Resolution width: {w}", k, w);
            foreach (var k in new[] { "ResolutionSizeY", "LastUserConfirmedResolutionSizeY", "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" }) Add($"Resolution height: {h}", k, h);
            Add("Window mode: Fullscreen", "FullscreenMode", "0");
            Add("Window mode: Fullscreen", "LastConfirmedFullscreenMode", "0");
            Add("Window mode: Fullscreen", "PreferredFullscreenMode", "0");
        }
        return v;
    }

    /// <summary>Reads the current file and returns what Apply would do. Read-only.</summary>
    public static List<SettingChange> Plan(FortnitePotatoSettings s)
    {
        var g = Game;
        var text = g.Installed ? File.ReadAllText(g.ConfigPath) : "";
        return Values(s).Select(x => new SettingChange(x.label, x.key, PotatoGraphics.ReadValue(text, x.key, true), x.value)).ToList();
    }

    public static bool IsLocked => Game.Installed && new FileInfo(Game.ConfigPath).IsReadOnly;

    /// <summary>Backs up GameUserSettings.ini once, then writes only keys already present in it.</summary>
    public static (int changed, List<SettingChange> missing) Apply(FortnitePotatoSettings s)
    {
        var g = Game;
        if (!g.Installed) throw new FileNotFoundException("Fortnite's settings file wasn't found. Launch Fortnite once, then try again.", g.ConfigPath);
        if (PotatoGraphics.IsRunning(g)) throw new InvalidOperationException("Close Fortnite first. It rewrites its settings file when it exits.");
        var plan = Plan(s);
        if (!g.HasBackup) File.Copy(g.ConfigPath, g.BackupPath);
        PotatoGraphics.ClearReadOnly(g.ConfigPath);
        var values = plan.Where(p => !p.Missing).GroupBy(p => p.Key).ToDictionary(x => x.Key, x => x.First().New);
        var (text, changed) = PotatoGraphics.ApplyValues(File.ReadAllText(g.ConfigPath), values, true);
        AtomicFile.WriteAllText(g.ConfigPath, text);
        // Fortnite resets a custom stretched resolution on launch unless the file is read-only.
        if (s.Stretched) File.SetAttributes(g.ConfigPath, File.GetAttributes(g.ConfigPath) | FileAttributes.ReadOnly);
        var missing = plan.Where(p => p.Missing).ToList();
        Log.Info($"Fortnite potato ({s.Preset}) applied: {changed} settings written, {missing.Count} not present in the file, read-only={s.Stretched}");
        return (changed, missing);
    }

    public static string Reset() => PotatoGraphics.Restore(Game);

    /// <summary>Removes the read-only lock without changing any setting (so in-game changes save again).</summary>
    public static void Unlock()
    {
        PotatoGraphics.ClearReadOnly(Game.ConfigPath);
        Log.Info("Fortnite settings file unlocked (read-only removed)");
    }
}
