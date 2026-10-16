using System.Globalization;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Display;

/// <summary>Fortnite's window modes as stored in GameUserSettings.ini (FullscreenMode).</summary>
public enum FortniteWindowMode { Fullscreen = 0, WindowedFullscreen = 1, Windowed = 2 }

/// <summary>
/// Sets Fortnite's in-game resolution and window mode in its own GameUserSettings.ini: only keys that already exist
/// are changed, the original file is backed up once (separately from Potato Graphics), nothing is deleted and no
/// game files are touched.
/// </summary>
public static class FortniteResolution
{
    public static PotatoGame Game => FortnitePotato.Game;
    public static string BackupPath => Path.Combine(AppPaths.FileBackups, "stretched-fortnite-GameUserSettings.ini");
    public static bool HasBackup => File.Exists(BackupPath);
    public static bool IsLocked => Game.Installed && new FileInfo(Game.ConfigPath).IsReadOnly;

    public static List<(string label, string key, string value)> Values(int w, int h, FortniteWindowMode mode)
    {
        string W = w.ToString(CultureInfo.InvariantCulture), H = h.ToString(CultureInfo.InvariantCulture), M = ((int)mode).ToString(CultureInfo.InvariantCulture);
        var v = new List<(string, string, string)>();
        foreach (var k in new[] { "ResolutionSizeX", "LastUserConfirmedResolutionSizeX", "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" }) v.Add(($"Width {W}", k, W));
        foreach (var k in new[] { "ResolutionSizeY", "LastUserConfirmedResolutionSizeY", "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" }) v.Add(($"Height {H}", k, H));
        foreach (var k in new[] { "FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode" }) v.Add(($"Window mode: {mode}", k, M));
        // Letterboxing would add black bars instead of stretching.
        foreach (var k in new[] { "bShouldLetterbox", "LastConfirmedShouldLetterbox" }) v.Add(("Letterbox: off", k, "False"));
        return v;
    }

    public static List<SettingChange> Plan(int w, int h, FortniteWindowMode mode)
    {
        var text = Game.Installed ? File.ReadAllText(Game.ConfigPath) : "";
        return Values(w, h, mode).Select(x => new SettingChange(x.label, x.key, PotatoGraphics.ReadValue(text, x.key, true), x.value)).ToList();
    }

    /// <summary>Current resolution stored in Fortnite's file, or null.</summary>
    public static (int w, int h, string mode)? Current()
    {
        if (!Game.Installed) return null;
        var text = File.ReadAllText(Game.ConfigPath);
        if (!int.TryParse(PotatoGraphics.ReadValue(text, "ResolutionSizeX", true), out var w) || !int.TryParse(PotatoGraphics.ReadValue(text, "ResolutionSizeY", true), out var h)) return null;
        var m = PotatoGraphics.ReadValue(text, "FullscreenMode", true) switch { "0" => "Fullscreen", "1" => "Windowed fullscreen", "2" => "Windowed", var x => x ?? "?" };
        return (w, h, m);
    }

    public static (int changed, List<SettingChange> missing) Apply(int w, int h, FortniteWindowMode mode, bool lockFile)
    {
        var g = Game;
        if (!g.Installed) throw new FileNotFoundException("Fortnite's settings file wasn't found. Launch Fortnite once, then try again.", g.ConfigPath);
        if (PotatoGraphics.IsRunning(g)) throw new InvalidOperationException("Close Fortnite first. It rewrites its settings file when it exits.");
        var errors = DisplayManager.Validate(w, h);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var plan = Plan(w, h, mode);
        if (!HasBackup) File.Copy(g.ConfigPath, BackupPath);
        PotatoGraphics.ClearReadOnly(g.ConfigPath);
        var values = plan.Where(p => !p.Missing).GroupBy(p => p.Key).ToDictionary(x => x.Key, x => x.First().New);
        var (text, changed) = PotatoGraphics.ApplyValues(File.ReadAllText(g.ConfigPath), values, true);
        File.WriteAllText(g.ConfigPath, text);
        // Fortnite resets resolutions it doesn't list on launch unless the file is read-only.
        if (lockFile) File.SetAttributes(g.ConfigPath, File.GetAttributes(g.ConfigPath) | FileAttributes.ReadOnly);
        // Read back what was written.
        var after = File.ReadAllText(g.ConfigPath);
        var notWritten = plan.Where(p => !p.Missing && !string.Equals(PotatoGraphics.ReadValue(after, p.Key, true), p.New, StringComparison.OrdinalIgnoreCase)).ToList();
        if (notWritten.Count > 0) throw new IOException("Fortnite's file didn't keep: " + string.Join(", ", notWritten.Select(n => n.Key)));
        var missing = plan.Where(p => p.Missing).ToList();
        Log.Info($"Fortnite resolution {w}×{h} {mode} written: {changed} values, {missing.Count} keys not present, locked={lockFile}");
        return (changed, missing);
    }

    public static string Restore()
    {
        if (!HasBackup) return "No backup from the Stretched Resolution tab exists, so there's nothing to restore.";
        if (PotatoGraphics.IsRunning(Game)) throw new InvalidOperationException("Close Fortnite first.");
        PotatoGraphics.ClearReadOnly(Game.ConfigPath);
        File.Copy(BackupPath, Game.ConfigPath, true);
        PotatoGraphics.ClearReadOnly(Game.ConfigPath);
        Log.Info("Fortnite settings file restored from the Stretched Resolution backup");
        return "Fortnite's settings file is back to how it was before the first change from this tab (backup kept).";
    }

    public static void Unlock() => PotatoGraphics.ClearReadOnly(Game.ConfigPath);
}
