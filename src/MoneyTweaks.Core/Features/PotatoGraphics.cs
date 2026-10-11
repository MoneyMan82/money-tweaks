using System.Globalization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public enum PotatoLevel { Low, Potato, UltraPotato }

public sealed class PotatoOptions
{
    public PotatoLevel Level { get; set; } = PotatoLevel.Potato;
    /// <summary>3D render scale in percent (Fortnite "3D Resolution", Rocket League "Render Quality").</summary>
    public int RenderScale { get; set; } = 50;
    /// <summary>Fortnite Performance rendering mode: the low-poly, flat-shaded look.</summary>
    public bool PerformanceMode { get; set; } = true;
    public bool Stretched { get; set; }
    public int StretchWidth { get; set; } = 1440;
    public int StretchHeight { get; set; } = 1080;
    public bool UncapFps { get; set; } = true;

    public static int DefaultScale(PotatoLevel l) => l switch { PotatoLevel.Low => 100, PotatoLevel.Potato => 50, _ => 25 };
}

public sealed record PotatoGame(
    string Id, string Name, string ConfigPath, string Notes, bool IsIni, string[] ProcessNames,
    bool SupportsRenderScale, bool SupportsPerformanceMode, bool SupportsStretched, bool SupportsFpsCap,
    Func<PotatoOptions, Dictionary<string, string>> Build)
{
    public bool Installed => File.Exists(ConfigPath);
    public string BackupPath => Path.Combine(AppPaths.FileBackups, $"potato-{Id}{Path.GetExtension(ConfigPath)}");
    public bool HasBackup => File.Exists(BackupPath);
}

/// <summary>
/// Potato Graphics: writes the lowest-quality values into a game's own settings file (after backing it up) for
/// maximum FPS and the flat, low-poly "potato" look. Only keys already present in the file are changed, so nothing
/// unknown is ever injected. Only games with a plain-text settings file are supported.
/// </summary>
public static class PotatoGraphics
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static Dictionary<string, string> Fortnite(PotatoOptions o)
    {
        var v = new Dictionary<string, string>
        {
            // Every scalability group to Low; 3D resolution drives the blurry/smooth potato look.
            ["sg.ResolutionQuality"] = Math.Clamp(o.RenderScale, 10, 100).ToString("0.000000", CultureInfo.InvariantCulture),
            ["sg.ViewDistanceQuality"] = "0", ["sg.AntiAliasingQuality"] = "0", ["sg.ShadowQuality"] = "0",
            ["sg.GlobalIlluminationQuality"] = "0", ["sg.ReflectionQuality"] = "0", ["sg.PostProcessQuality"] = "0",
            ["sg.TextureQuality"] = "0", ["sg.EffectsQuality"] = "0", ["sg.FoliageQuality"] = "0", ["sg.ShadingQuality"] = "0",
            ["bUseVSync"] = "False", ["bMotionBlur"] = "False", ["bShowGrass"] = "False",
            ["bRayTracing"] = "False", ["bUseNanite"] = "False",
        };
        if (o.Level != PotatoLevel.Low) v["bUseDynamicResolution"] = "False";
        if (o.PerformanceMode)
        {
            // Performance rendering mode = DirectX 11 with the ES3.1 (mobile) feature level: simplified meshes and flat shading.
            v["PreferredRHI"] = "dx11";
            v["PreferredFeatureLevel"] = "es31";
        }
        if (o.UncapFps) v["FrameRateLimit"] = "0.000000";
        if (o.Stretched)
        {
            string w = o.StretchWidth.ToString(CultureInfo.InvariantCulture), h = o.StretchHeight.ToString(CultureInfo.InvariantCulture);
            foreach (var k in new[] { "ResolutionSizeX", "LastUserConfirmedResolutionSizeX", "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" }) v[k] = w;
            foreach (var k in new[] { "ResolutionSizeY", "LastUserConfirmedResolutionSizeY", "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" }) v[k] = h;
            v["FullscreenMode"] = "0"; v["LastConfirmedFullscreenMode"] = "0"; v["PreferredFullscreenMode"] = "0";
        }
        return v;
    }

    private static Dictionary<string, string> Minecraft(PotatoOptions o) => new()
    {
        ["graphicsMode"] = "0", // Fast
        ["renderDistance"] = o.Level switch { PotatoLevel.Low => "8", PotatoLevel.Potato => "4", _ => "2" },
        ["simulationDistance"] = "5",
        ["particles"] = "2", ["ao"] = "false", ["renderClouds"] = "\"false\"", ["entityShadows"] = "false",
        ["enableVsync"] = "false", ["biomeBlendRadius"] = "0", ["mipmapLevels"] = "0",
        ["entityDistanceScaling"] = o.Level == PotatoLevel.Low ? "0.75" : "0.5",
        ["maxFps"] = o.UncapFps ? "260" : "120", // 260 = "Unlimited"
    };

    private static Dictionary<string, string> RocketLeague(PotatoOptions o) => new()
    {
        ["ScreenPercentage"] = Math.Clamp(o.RenderScale, 25, 100).ToString("0.000000", CultureInfo.InvariantCulture),
        ["DynamicShadows"] = "False", ["AmbientOcclusion"] = "False", ["DepthOfField"] = "False", ["Bloom"] = "False",
        ["LensFlares"] = "False", ["MotionBlur"] = "False", ["LightShafts"] = "False", ["Distortion"] = "False",
        ["DetailMode"] = "0", ["StaticDecals"] = "False", ["DynamicDecals"] = "False",
        ["bAllowLightShafts"] = "False", ["bAllowWholeSceneDominantShadows"] = "False",
    };

    public static IReadOnlyList<PotatoGame> Games { get; } =
    [
        new("fortnite", "Fortnite", Path.Combine(Local, @"FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini"),
            "Performance mode plus everything on Low gives the classic potato look: low-poly builds, flat textures, no grass or shadows. Lower 3D resolution makes it blurrier and smoother. In-game, also set Mesh Quality to Low under Performance mode.",
            true, ["FortniteClient-Win64-Shipping", "FortniteLauncher"], true, true, true, true, Fortnite),
        new("minecraft", "Minecraft: Java Edition", Path.Combine(Roaming, @".minecraft\options.txt"),
            "Fast graphics, tiny render distance, minimal particles, no clouds, shadows, smooth lighting or biome blending.",
            false, ["javaw"], false, false, false, true, Minecraft),
        new("rocketleague", "Rocket League", Path.Combine(Docs, @"My Games\Rocket League\TAGame\Config\TASystemSettings.ini"),
            "Turns off shadows, ambient occlusion, bloom, lens flares, light shafts and decals, and lowers render quality.",
            true, ["RocketLeague"], true, false, false, false, RocketLeague),
    ];

    /// <summary>
    /// Replaces "key=value" (ini) or "key:value" lines that already exist. A key written as "Section|Key" is only
    /// changed inside that [Section]. Returns the new text and the number of keys changed.
    /// </summary>
    internal static (string text, int changed) ApplyValues(string text, IReadOnlyDictionary<string, string> values, bool isIni)
    {
        var sep = isIni ? "=" : ":";
        int changed = 0;
        foreach (var (rawKey, value) in values)
        {
            var (start, end, key) = Scope(text, rawKey);
            if (start < 0) continue;
            // [^\r\n]* keeps the file's own line endings intact.
            var rx = new Regex($@"^{Regex.Escape(key)}{Regex.Escape(sep)}[^\r\n]*", RegexOptions.Multiline);
            var region = text[start..end];
            if (!rx.IsMatch(region)) continue;
            region = rx.Replace(region, _ => $"{key}{sep}{value}");
            text = text[..start] + region + text[end..];
            changed++;
        }
        return (text, changed);
    }

    /// <summary>Current value of a key ("Section|Key" supported), or null when the file doesn't contain it.</summary>
    internal static string? ReadValue(string text, string rawKey, bool isIni)
    {
        var (start, end, key) = Scope(text, rawKey);
        if (start < 0) return null;
        var m = Regex.Match(text[start..end], $@"^{Regex.Escape(key)}{Regex.Escape(isIni ? "=" : ":")}([^\r\n]*)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static (int start, int end, string key) Scope(string text, string rawKey)
    {
        var bar = rawKey.IndexOf('|');
        if (bar < 0) return (0, text.Length, rawKey);
        var section = rawKey[..bar];
        var head = Regex.Match(text, $@"^\[{Regex.Escape(section)}\][ \t]*\r?$", RegexOptions.Multiline);
        if (!head.Success) return (-1, -1, rawKey[(bar + 1)..]);
        var start = head.Index + head.Length;
        var next = Regex.Match(text[start..], @"^\[", RegexOptions.Multiline);
        return (start, next.Success ? start + next.Index : text.Length, rawKey[(bar + 1)..]);
    }

    public static bool IsRunning(PotatoGame g) =>
        g.ProcessNames.Any(n => System.Diagnostics.Process.GetProcessesByName(n).Length > 0);

    public static string Apply(PotatoGame g, PotatoOptions o)
    {
        if (!g.Installed) throw new FileNotFoundException($"{g.Name} settings file not found. Launch the game once first.", g.ConfigPath);
        if (!g.HasBackup) File.Copy(g.ConfigPath, g.BackupPath);
        ClearReadOnly(g.ConfigPath);
        var (text, changed) = ApplyValues(File.ReadAllText(g.ConfigPath), g.Build(o), g.IsIni);
        File.WriteAllText(g.ConfigPath, text);
        // Fortnite rewrites stretched resolutions on launch unless the file is read-only.
        if (o.Stretched && g.SupportsStretched) File.SetAttributes(g.ConfigPath, File.GetAttributes(g.ConfigPath) | FileAttributes.ReadOnly);
        Log.Info($"Potato Graphics ({o.Level}, {o.RenderScale}%) applied to {g.Name}: {changed} settings changed (backup: {g.BackupPath})");
        var msg = $"{changed} settings changed.";
        if (o.Stretched && g.SupportsStretched) msg += " Settings file locked (read-only) so the stretched resolution sticks. Set your GPU driver's scaling to 'Full-screen'.";
        return msg;
    }

    public static string Restore(PotatoGame g)
    {
        if (!g.HasBackup) return "No backup found. Nothing to restore.";
        ClearReadOnly(g.ConfigPath);
        File.Copy(g.BackupPath, g.ConfigPath, overwrite: true);
        ClearReadOnly(g.ConfigPath);
        File.Delete(g.BackupPath);
        Log.Info($"Potato Graphics restored original settings for {g.Name}");
        return "Original settings restored.";
    }

    internal static void ClearReadOnly(string path)
    {
        if (File.Exists(path) && new FileInfo(path).IsReadOnly)
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }
}
