using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed record PotatoGame(string Id, string Name, string ConfigPath, string Notes, IReadOnlyDictionary<string, string> Values, bool IsIni)
{
    public bool Installed => File.Exists(ConfigPath);
    public string BackupPath => Path.Combine(AppPaths.FileBackups, $"potato-{Id}{Path.GetExtension(ConfigPath)}");
    public bool HasBackup => File.Exists(BackupPath);
}

/// <summary>
/// Potato Graphics: writes the lowest-quality values into a game's own settings file (after backing it up),
/// for maximum FPS on weak hardware. Only games with a documented, plain-text settings file are supported.
/// </summary>
public static class PotatoGraphics
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static IReadOnlyList<PotatoGame> Games { get; } =
    [
        new("fortnite", "Fortnite", Path.Combine(Local, @"FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini"),
            "Sets every Unreal scalability group to Low, turns off V-Sync and motion blur, and uses the DirectX 11 / Performance-friendly settings. Rendering mode itself (Performance mode) must still be chosen in-game.",
            new Dictionary<string, string>
            {
                ["sg.ResolutionQuality"] = "100", ["sg.ViewDistanceQuality"] = "0", ["sg.AntiAliasingQuality"] = "0",
                ["sg.ShadowQuality"] = "0", ["sg.GlobalIlluminationQuality"] = "0", ["sg.ReflectionQuality"] = "0",
                ["sg.PostProcessQuality"] = "0", ["sg.TextureQuality"] = "0", ["sg.EffectsQuality"] = "0",
                ["sg.FoliageQuality"] = "0", ["sg.ShadingQuality"] = "0", ["bUseVSync"] = "False", ["bMotionBlur"] = "False",
                ["bShowGrass"] = "False",
            }, true),
        new("minecraft", "Minecraft: Java Edition", Path.Combine(Roaming, @".minecraft\options.txt"),
            "Fast graphics, 6-chunk render and simulation distance, minimal particles, no clouds, entity shadows or smooth lighting.",
            new Dictionary<string, string>
            {
                ["graphicsMode"] = "0", ["renderDistance"] = "6", ["simulationDistance"] = "6", ["particles"] = "2",
                ["ao"] = "false", ["renderClouds"] = "\"false\"", ["entityShadows"] = "false", ["enableVsync"] = "false",
                ["biomeBlendRadius"] = "0", ["mipmapLevels"] = "0", ["entityDistanceScaling"] = "0.5",
            }, false),
        new("rocketleague", "Rocket League", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"My Games\Rocket League\TAGame\Config\TASystemSettings.ini"),
            "Turns off the most expensive Unreal Engine 3 effects: dynamic shadows, ambient occlusion, depth of field, bloom, lens flares and motion blur.",
            new Dictionary<string, string>
            {
                ["DynamicShadows"] = "False", ["AmbientOcclusion"] = "False", ["DepthOfField"] = "False", ["Bloom"] = "False",
                ["LensFlares"] = "False", ["MotionBlur"] = "False", ["LightShafts"] = "False", ["Distortion"] = "False",
                ["DetailMode"] = "0", ["StaticDecals"] = "False", ["DynamicDecals"] = "False",
            }, true),
    ];

    public static string Apply(PotatoGame g)
    {
        if (!g.Installed) throw new FileNotFoundException($"{g.Name} settings file not found. Launch the game once first.", g.ConfigPath);
        if (!g.HasBackup) File.Copy(g.ConfigPath, g.BackupPath);
        var text = File.ReadAllText(g.ConfigPath);
        int changed = 0;
        foreach (var (key, value) in g.Values)
        {
            var sep = g.IsIni ? "=" : ":";
            // [^\r\n]* keeps the file's own line endings intact.
            var rx = new Regex($@"^{Regex.Escape(key)}{Regex.Escape(sep)}[^\r\n]*", RegexOptions.Multiline);
            if (rx.IsMatch(text))
            {
                text = rx.Replace(text, _ => $"{key}{sep}{value}");
                changed++;
            }
        }
        var wasReadOnly = new FileInfo(g.ConfigPath).IsReadOnly;
        if (wasReadOnly) File.SetAttributes(g.ConfigPath, File.GetAttributes(g.ConfigPath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(g.ConfigPath, text);
        Log.Info($"Potato Graphics applied to {g.Name}: {changed} settings changed (backup: {g.BackupPath})");
        return $"{changed} settings lowered. Close the game before applying, or it may overwrite the file on exit.";
    }

    public static string Restore(PotatoGame g)
    {
        if (!g.HasBackup) return "No backup found. Nothing to restore.";
        File.Copy(g.BackupPath, g.ConfigPath, overwrite: true);
        File.Delete(g.BackupPath);
        Log.Info($"Potato Graphics restored original settings for {g.Name}");
        return "Original settings restored.";
    }
}
