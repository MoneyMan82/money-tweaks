using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Games;

public sealed record SteamApp(int AppId, string Name, string InstallDir);
public sealed record EpicItem(string AppName, string DisplayName, string InstallLocation, string LaunchExecutable, string CatalogNamespace, string CatalogItemId, string MainGameAppName, bool Incomplete);

/// <summary>One source looked at for a game. Definitive sources (Steam, Epic, Riot, Roblox, Minecraft) can prove a game isn't installed.</summary>
public sealed record SourceCheck(string Source, bool Available, bool Found, bool Definitive, string Detail);

/// <summary>Where a game is installed and how to start it.</summary>
public sealed record GameInstall(string Source, string InstallDir, string? ExePath, string? LaunchUri, string? LaunchExe, string? LaunchArgs);

public sealed record InstallResult(GameInstall? Install, List<SourceCheck> Checks);

/// <summary>Everything read from launchers in one pass (all read-only). Roots can be overridden for tests.</summary>
public sealed class LauncherData
{
    public bool SteamAvailable { get; init; }
    public string SteamDetail { get; init; } = "";
    public List<SteamApp> SteamApps { get; init; } = [];
    public bool EpicAvailable { get; init; }
    public List<EpicItem> EpicItems { get; init; } = [];
    public bool RiotAvailable { get; init; }
    public Dictionary<string, string> RiotProducts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RiotClient { get; init; }
    public List<(string name, string location)> Uninstall { get; init; } = [];
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string RoamingAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public DateTime ScannedAt { get; init; } = DateTime.Now;

    public static LauncherData Scan()
    {
        // Steam: registry → libraryfolders.vdf → appmanifest_*.acf in every library.
        var steamApps = new List<SteamApp>();
        string? steam = null;
        string steamDetail;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steam = (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        }
        catch { }
        var vdf = steam is null ? null : Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        bool steamOk = vdf is not null && File.Exists(vdf);
        if (steamOk)
        {
            var libs = GameScanner.SteamLibraries(File.ReadAllText(vdf!));
            if (libs.Count == 0) libs.Add(steam!);
            foreach (var lib in libs)
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try { if (GameScanner.ParseAppManifest(File.ReadAllText(acf)) is { } a) steamApps.Add(a with { InstallDir = Path.Combine(apps, "common", a.InstallDir) }); }
                    catch (Exception ex) { Log.Debug($"{acf}: {ex.Message}"); }
                }
            }
            steamDetail = $"{libs.Count} Steam library folder(s), {steamApps.Count} installed app(s)";
        }
        else steamDetail = steam is null ? "Steam isn't installed (no SteamPath in the registry)." : $"Steam's library list wasn't found at {vdf}.";

        // Epic: one JSON .item manifest per installed app.
        var epicDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        var epic = new List<EpicItem>();
        if (Directory.Exists(epicDir))
            foreach (var f in Directory.EnumerateFiles(epicDir, "*.item"))
                try { if (GameScanner.ParseEpicItem(File.ReadAllText(f)) is { } e) epic.Add(e); } catch (Exception ex) { Log.Debug($"{f}: {ex.Message}"); }

        // Riot: product settings name each product's install folder.
        var riotRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");
        var riot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? riotClient = null;
        if (Directory.Exists(riotRoot))
        {
            var meta = Path.Combine(riotRoot, "Metadata");
            if (Directory.Exists(meta))
                foreach (var f in Directory.EnumerateFiles(meta, "*.product_settings.yaml", SearchOption.AllDirectories))
                    try
                    {
                        if (GameScanner.ParseRiotSettings(File.ReadAllText(f)) is { } path)
                            riot[Path.GetFileName(f).Split('.')[0]] = path;
                    }
                    catch { }
            try
            {
                var installs = Path.Combine(riotRoot, "RiotClientInstalls.json");
                if (File.Exists(installs) && JsonDocument.Parse(File.ReadAllText(installs)).RootElement.TryGetProperty("rc_default", out var rc)) riotClient = rc.GetString();
            }
            catch { }
        }

        return new LauncherData
        {
            SteamAvailable = steamOk, SteamDetail = steamDetail, SteamApps = steamApps,
            EpicAvailable = Directory.Exists(epicDir), EpicItems = epic,
            RiotAvailable = Directory.Exists(riotRoot), RiotProducts = riot, RiotClient = riotClient,
            Uninstall = GameScanner.UninstallEntries(),
        };
    }
}

/// <summary>Install detection from launcher data. Parsers are pure functions so they can be tested with fixtures.</summary>
public static class GameScanner
{
    // ---- parsers ---------------------------------------------------------------------------------------------------

    /// <summary>Minimal Valve KeyValues (VDF) reader: quoted keys/values and nested braces.</summary>
    public static Dictionary<string, object> ParseVdf(string text)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|([{}])").Select(m => m.Groups[1].Success ? (object)Regex.Unescape(m.Groups[1].Value) : m.Groups[2].Value[0]).ToList();
        int i = 0;
        Dictionary<string, object> Block()
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (i < tokens.Count)
            {
                if (tokens[i] is char c && c == '}') { i++; break; }
                if (tokens[i] is not string key) { i++; continue; }
                i++;
                if (i >= tokens.Count) break;
                if (tokens[i] is char open && open == '{') { i++; d[key] = Block(); }
                else if (tokens[i] is string val) { d[key] = val; i++; }
            }
            return d;
        }
        return Block();
    }

    public static List<string> SteamLibraries(string libraryFoldersVdf)
    {
        var root = ParseVdf(libraryFoldersVdf);
        var lf = root.Values.OfType<Dictionary<string, object>>().FirstOrDefault() ?? root;
        return lf.Values.OfType<Dictionary<string, object>>().Select(d => d.TryGetValue("path", out var p) ? p as string : null)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static SteamApp? ParseAppManifest(string acf)
    {
        var root = ParseVdf(acf);
        if (root.Values.OfType<Dictionary<string, object>>().FirstOrDefault() is not { } st) return null;
        if (!st.TryGetValue("appid", out var id) || !int.TryParse(id as string, out var appId)) return null;
        return new SteamApp(appId, st.TryGetValue("name", out var n) ? n as string ?? "" : "", st.TryGetValue("installdir", out var dir) ? dir as string ?? "" : "");
    }

    public static EpicItem? ParseEpicItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        if (S("AppName").Length == 0) return null;
        return new EpicItem(S("AppName"), S("DisplayName"), S("InstallLocation"), S("LaunchExecutable"), S("CatalogNamespace"), S("CatalogItemId"), S("MainGameAppName"), B("bIsIncompleteInstall"));
    }

    public static string? ParseRiotSettings(string yaml)
    {
        var m = Regex.Match(yaml, @"^\s*product_install_full_path:\s*""?([^""\r\n]+)""?", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim().Replace('/', '\\') : null;
    }

    public static List<(string name, string location)> UninstallEntries()
    {
        var list = new List<(string, string)>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (k is null) continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var e = k.OpenSubKey(sub);
                    if (e?.GetValue("DisplayName") is string n && e.GetValue("InstallLocation") is string loc && loc.Trim().Length > 0) list.Add((n.Trim(), loc.Trim().Trim('"')));
                }
            }
            catch { }
        }
        return list;
    }

    // ---- executable lookup -----------------------------------------------------------------------------------------

    private static readonly Regex Helper = new(@"(unins\d*|uninstall|setup|install|crash|report|updater|redist|vc_redist|dxsetup|directx|ue4prereq|easyanticheat|battleye|be_service|cleanup|helper|webhelper|benchmark)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The game's exe inside an install folder: known relative paths first, then a bounded search for its process names.</summary>
    public static string? FindExe(GameDef def, string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var hint in def.ExeHints)
        {
            var p = Path.Combine(dir, hint);
            if (File.Exists(p)) return p;
        }
        foreach (var rule in def.Processes.Where(r => r.PathAny is null))
        {
            var direct = Path.Combine(dir, rule.Exe);
            if (File.Exists(direct)) return direct;
        }
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var rule in def.Processes.Where(r => r.PathAny is null))
        {
            try { if (Directory.EnumerateFiles(dir, rule.Exe, opts).FirstOrDefault() is { } hit) return hit; } catch { }
        }
        return null;
    }

    /// <summary>Executables in a folder that could be the game, best first (helpers, installers and crash reporters excluded).</summary>
    public static List<(string path, long size)> CandidateExes(string dir, int max = 25)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        var list = new List<(string, long, int)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", opts))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (Helper.IsMatch(name)) continue;
                long size; try { size = new FileInfo(f).Length; } catch { continue; }
                int depth = Path.GetRelativePath(dir, f).Count(c => c == '\\');
                // Shipping builds and big binaries first; launchers after the game.
                int score = (name.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ? 1000 : 0) - (name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ? 500 : 0) - depth * 10;
                list.Add((f, size, score));
            }
        }
        catch { }
        return list.OrderByDescending(x => x.Item3).ThenByDescending(x => x.Item2).Take(max).Select(x => (x.Item1, x.Item2)).ToList();
    }

    // ---- resolution ------------------------------------------------------------------------------------------------

    private static GameInstall? Folder(string source, GameDef def, string dir, string? uri = null, string? launchExe = null, string? args = null) =>
        Directory.Exists(dir) ? new GameInstall(source, dir, FindExe(def, dir), uri, launchExe, args) : null;

    /// <summary>
    /// Finds where a catalogue game is installed. An install only counts if the game's own exe is in it (a "Grand Theft
    /// Auto V" Epic folder without GTA5_Enhanced.exe is not the Enhanced edition).
    /// </summary>
    public static InstallResult Resolve(GameDef def, LauncherData d)
    {
        var checks = new List<SourceCheck>();
        GameInstall? found = null;
        void Take(GameInstall? i, SourceCheck c)
        {
            checks.Add(c);
            if (found is null && c.Found) found = i;
        }

        if (def.SteamAppIds.Length > 0 || def.SteamName is not null)
        {
            if (!d.SteamAvailable) checks.Add(new("Steam", false, false, true, d.SteamDetail));
            else
            {
                var app = d.SteamApps.FirstOrDefault(a => def.SteamAppIds.Contains(a.AppId))
                          ?? (def.SteamName is null ? null : d.SteamApps.FirstOrDefault(a => a.Name.Equals(def.SteamName, StringComparison.OrdinalIgnoreCase)));
                if (app is null) checks.Add(new("Steam", true, false, true, $"Not in your Steam libraries ({d.SteamDetail})."));
                else
                {
                    var i = Folder("Steam", def, app.InstallDir, uri: $"steam://rungameid/{app.AppId}");
                    var ok = i is { ExePath: not null };
                    Take(i, new("Steam", true, ok, true, ok ? $"App {app.AppId} '{app.Name}' in {app.InstallDir}" : $"App {app.AppId} is listed, but {string.Join("/", def.ExeNames)} isn't in {app.InstallDir} (still downloading, or the exe changed)."));
                }
            }
        }
        if (def.EpicAppName is not null || def.EpicName is not null)
        {
            if (!d.EpicAvailable) checks.Add(new("Epic Games", false, false, true, "Epic Games Launcher isn't installed (no manifests folder)."));
            else
            {
                var item = d.EpicItems.Where(e => e.MainGameAppName.Length == 0 && !e.Incomplete).FirstOrDefault(e =>
                    (def.EpicAppName is not null && e.AppName.Equals(def.EpicAppName, StringComparison.OrdinalIgnoreCase)) ||
                    (def.EpicName is not null && e.DisplayName.Equals(def.EpicName, StringComparison.OrdinalIgnoreCase)));
                if (item is null) checks.Add(new("Epic Games", true, false, true, $"Not among {d.EpicItems.Count} Epic manifests."));
                else
                {
                    var uri = item.CatalogNamespace.Length > 0
                        ? $"com.epicgames.launcher://apps/{item.CatalogNamespace}%3A{item.CatalogItemId}%3A{item.AppName}?action=launch&silent=true"
                        : $"com.epicgames.launcher://apps/{item.AppName}?action=launch&silent=true";
                    var i = Folder("Epic Games", def, item.InstallLocation, uri: uri);
                    var ok = i is { ExePath: not null };
                    Take(i, new("Epic Games", true, ok, true, ok ? $"'{item.DisplayName}' ({item.AppName}) in {item.InstallLocation}" : $"'{item.DisplayName}' is in Epic, but {string.Join("/", def.ExeNames)} isn't in {item.InstallLocation}."));
                }
            }
        }
        if (def.RiotProduct is { } product)
        {
            if (!d.RiotAvailable) checks.Add(new("Riot Client", false, false, true, "Riot Client isn't installed."));
            else if (!d.RiotProducts.TryGetValue(product + ".live", out var dir) && !d.RiotProducts.TryGetValue(product, out dir)) checks.Add(new("Riot Client", true, false, true, $"No installed Riot product '{product}'."));
            else
            {
                var i = Folder("Riot Client", def, dir, launchExe: d.RiotClient, args: $"--launch-product={product} --launch-patchline=live");
                var ok = i is { ExePath: not null };
                Take(i, new("Riot Client", true, ok, true, ok ? $"{product} in {dir}" : $"Riot lists {product} at {dir}, but the game exe isn't there."));
            }
        }
        if (def.Special is { } sp) Take(Special(def, sp, d, out var c), c);
        foreach (var (key, value) in def.RegistryInstalls)
        {
            string? dir = null;
            try { using var k = Registry.LocalMachine.OpenSubKey(key); dir = (k?.GetValue(value) as string)?.Trim('"'); } catch { }
            if (dir is null) { checks.Add(new("Registry", true, false, false, $"No {key}\\{value}.")); continue; }
            var i = Folder("Registry", def, dir, launchExe: null);
            Take(i, new("Registry", true, i is { ExePath: not null }, false, $"{key} → {dir}"));
        }
        foreach (var n in def.UninstallNames)
        {
            var hit = d.Uninstall.FirstOrDefault(u => u.name.Equals(n, StringComparison.OrdinalIgnoreCase) || u.name.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase) || u.name.StartsWith(n + ":", StringComparison.OrdinalIgnoreCase));
            if (hit.name is null) { checks.Add(new("Apps & features", true, false, false, $"No '{n}' entry.")); continue; }
            var i = Folder("Apps & features", def, hit.location);
            Take(i, new("Apps & features", true, i is { ExePath: not null }, false, $"'{hit.name}' → {hit.location}"));
        }
        return new InstallResult(found, checks);
    }

    private static GameInstall? Special(GameDef def, string kind, LauncherData d, out SourceCheck check)
    {
        switch (kind)
        {
            case "roblox-player":
            case "roblox-studio":
            {
                var exe = kind == "roblox-player" ? "RobloxPlayerBeta.exe" : "RobloxStudioBeta.exe";
                var roots = new[]
                {
                    Path.Combine(d.LocalAppData, @"Roblox\Versions"), Path.Combine(d.LocalAppData, @"Bloxstrap\Versions"), Path.Combine(d.LocalAppData, @"Fishstrap\Versions"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Roblox\Versions"),
                };
                // Roblox keeps old version folders; the newest exe is the one Roblox runs.
                var newest = roots.Where(Directory.Exists)
                    .SelectMany(r => { try { return Directory.EnumerateFiles(r, exe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 }); } catch { return []; } })
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest is null)
                {
                    check = new("Roblox", true, false, true, $"No {exe} in Roblox's or Bloxstrap's Versions folders.");
                    return null;
                }
                check = new("Roblox", true, true, true, $"{exe} in {newest.DirectoryName} (newest of the version folders)");
                return new GameInstall("Roblox", newest.DirectoryName!, newest.FullName, null, newest.FullName, null);
            }
            case "minecraft-java":
            {
                var data = Path.Combine(d.RoamingAppData, ".minecraft");
                var launchers = new[]
                {
                    Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", @"XboxGames\Minecraft Launcher\Content\Minecraft.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Minecraft Launcher\MinecraftLauncher.exe"),
                };
                var launcher = launchers.FirstOrDefault(File.Exists);
                if (!Directory.Exists(data) && launcher is null)
                {
                    check = new("Minecraft Launcher", true, false, true, "No .minecraft folder and no Minecraft Launcher.");
                    return null;
                }
                check = new("Minecraft Launcher", true, true, true, $"Game data {(Directory.Exists(data) ? data : "not created yet")}; launcher {launcher ?? "not found (third-party launcher?)"}. Java runs as javaw.exe from Minecraft's runtime.");
                return new GameInstall("Minecraft Launcher", data, null, null, launcher, null);
            }
            case "minecraft-bedrock":
            {
                var gdk = DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady)
                    .Select(x => Path.Combine(x.RootDirectory.FullName, @"XboxGames\Minecraft for Windows\Content\Minecraft.Windows.exe")).FirstOrDefault(File.Exists);
                if (gdk is not null)
                {
                    check = new("Microsoft Store / Xbox", true, true, true, gdk);
                    return new GameInstall("Microsoft Store / Xbox", Path.GetDirectoryName(gdk)!, gdk, "minecraft:", null, null);
                }
                bool uwp = false;
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                    uwp = k?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.MinecraftUWP_", StringComparison.OrdinalIgnoreCase)) == true;
                }
                catch { }
                check = new("Microsoft Store / Xbox", true, uwp, true, uwp ? "Microsoft.MinecraftUWP package is installed." : "No Minecraft for Windows (Bedrock) package or XboxGames folder.");
                return uwp ? new GameInstall("Microsoft Store / Xbox", "Microsoft Store package", null, "minecraft:", null, null) : null;
            }
        }
        check = new(kind, false, false, false, "Unknown launcher.");
        return null;
    }
}
