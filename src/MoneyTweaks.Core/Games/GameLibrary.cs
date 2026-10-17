using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Games;

/// <summary>
/// Your settings for one library game. Catalogue games get an entry only once you change something (favourite, profile,
/// custom path); games you add yourself always have one.
/// </summary>
public sealed class LibraryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Catalogue game this entry belongs to; null for a game you added.</summary>
    public string? CatalogId { get; set; }
    public string Name { get; set; } = "";
    public GameCategory Category { get; set; } = GameCategory.Custom;
    /// <summary>A specific exe (games you add, or a catalogue game whose exe moved).</summary>
    public string? ExePath { get; set; }
    public List<string> ProcessNames { get; set; } = [];
    public bool Favourite { get; set; }
    /// <summary>Gaming profile to use with this game.</summary>
    public string? ProfileId { get; set; }
    /// <summary>Activate the profile automatically while the game runs (restored when it closes).</summary>
    public bool AutoActivate { get; set; }
    /// <summary>Raise the verified game process to High priority while it runs (runtime only).</summary>
    public bool RaisePriority { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
}

public sealed class LibraryFile
{
    public int Version { get; set; } = 1;
    public List<LibraryEntry> Entries { get; set; } = [];
    /// <summary>Catalogue games you removed from the library (restorable). The game itself is never touched.</summary>
    public HashSet<string> RemovedCatalog { get; set; } = [];
}

/// <summary>A game as shown in the library: its definition plus your entry, if any.</summary>
public sealed record LibraryGame(string Key, GameDef Def, LibraryEntry? Entry, bool Custom)
{
    public bool Favourite => Entry?.Favourite == true;
}

/// <summary>The editable game library. Removing a game only removes Money Tweaks' entry; nothing on disk is deleted.</summary>
public static class GameLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath => Path.Combine(AppPaths.Root, "library.json");

    private static LibraryFile? _current;
    /// <summary>The library in use (loaded on first access).</summary>
    public static LibraryFile Current { get => _current ??= Load(); set => _current = value; }

    public static LibraryFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex) { Log.Error("library.json unreadable (kept as .bad)", ex); try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new();
    }

    public static void Save(LibraryFile f)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
        File.Move(tmp, FilePath, true);
    }

    /// <summary>Definition for a game you added: its own exe names, no launcher sources.</summary>
    public static GameDef DefFor(LibraryEntry e) => new()
    {
        Id = "usr:" + e.Id, Name = e.Name, Category = e.Category,
        Processes = e.ProcessNames.Select(n => new ProcessRule(n)).ToArray(),
    };

    public static List<LibraryGame> Items(LibraryFile f)
    {
        var list = GameCatalog.All.Where(g => !f.RemovedCatalog.Contains(g.Id))
            .Select(g => new LibraryGame("cat:" + g.Id, g, f.Entries.FirstOrDefault(e => e.CatalogId == g.Id), false)).ToList();
        list.AddRange(f.Entries.Where(e => e.CatalogId is null).Select(e => new LibraryGame("usr:" + e.Id, DefFor(e), e, true)));
        return list;
    }

    public static LibraryGame? Find(LibraryFile f, string key) => Items(f).FirstOrDefault(g => g.Key == key);

    /// <summary>Your entry for a game, created for catalogue games on first change.</summary>
    public static LibraryEntry EntryFor(LibraryFile f, LibraryGame g)
    {
        if (g.Entry is not null) return g.Entry;
        var e = new LibraryEntry { CatalogId = g.Def.Id, Name = g.Def.Name, Category = g.Def.Category, ProcessNames = g.Def.ExeNames.ToList() };
        f.Entries.Add(e);
        return e;
    }

    /// <summary>Removes the game from the library only. Catalogue games can be restored later. Never touches the game's files.</summary>
    public static void Remove(LibraryFile f, LibraryGame g)
    {
        if (g.Custom) f.Entries.RemoveAll(e => e.Id == g.Entry!.Id);
        else
        {
            f.RemovedCatalog.Add(g.Def.Id);
            f.Entries.RemoveAll(e => e.CatalogId == g.Def.Id);
        }
        Log.Info($"Game Library: removed {g.Def.Name} from the library (no files changed)");
    }

    public static void Restore(LibraryFile f, string catalogId) => f.RemovedCatalog.Remove(catalogId);

    public static IEnumerable<GameDef> Removed(LibraryFile f) => GameCatalog.All.Where(g => f.RemovedCatalog.Contains(g.Id));

    /// <summary>
    /// Adds a game by exe. If the exe belongs to a catalogue game, that game is restored (if removed) and pointed at this
    /// exe instead of creating a duplicate.
    /// </summary>
    public static (LibraryGame game, string message) AddExe(LibraryFile f, string exePath, string? name = null, GameCategory category = GameCategory.Custom)
    {
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose the game's .exe file.");
        if (!File.Exists(exePath)) throw new FileNotFoundException("That file doesn't exist.", exePath);
        var full = Path.GetFullPath(exePath);
        var exe = Path.GetFileName(full);
        if (GameCatalog.ByExe(exe).FirstOrDefault(d => d.Processes.Any(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase) && !p.HasIdentityRule)) is { } def)
        {
            f.RemovedCatalog.Remove(def.Id);
            var g = Items(f).First(x => x.Def.Id == def.Id);
            EntryFor(f, g).ExePath = full;
            return (Items(f).First(x => x.Def.Id == def.Id), $"{exe} is {def.Name}, which is already in the catalogue; it now uses this exe.");
        }
        var existing = f.Entries.FirstOrDefault(e => e.CatalogId is null && string.Equals(e.ExePath, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return (Items(f).First(x => x.Entry == existing), $"{existing.Name} is already in your library.");
        if (name is null)
        {
            try { name = FileVersionInfo.GetVersionInfo(full).ProductName?.Trim(); } catch { }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60) name = Path.GetFileNameWithoutExtension(full);
        }
        var entry = new LibraryEntry { Name = name, Category = category, ExePath = full, ProcessNames = [exe] };
        f.Entries.Add(entry);
        Log.Info($"Game Library: added {name} ({full})");
        return (Items(f).First(x => x.Entry == entry), $"Added {name}.");
    }

    public static IEnumerable<LibraryGame> Filter(IEnumerable<LibraryGame> games, string? search, GameCategory? category, bool favouritesOnly) =>
        games.Where(g =>
            (!favouritesOnly || g.Favourite) &&
            (category is null || g.Def.Category == category) &&
            (string.IsNullOrWhiteSpace(search) || g.Def.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || g.Def.ExeNames.Any(e => e.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(g => g.Favourite).ThenBy(g => g.Def.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Your own (not built-in) gaming profiles tied to this game: chosen for it, or listing its exe.</summary>
    public static List<GamingProfile> LinkedCustomProfiles(LibraryGame g, IEnumerable<GamingProfile> customProfiles)
    {
        var exes = g.Def.ExeNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return customProfiles.Where(p => !p.BuiltIn && (p.Id == g.Entry?.ProfileId || p.GameExes.Any(exes.Contains))).ToList();
    }

    // ---- install resolution with your overrides ---------------------------------------------------------------------

    /// <summary>Install for a library game: your chosen exe first (also for catalogue games whose exe moved), then launchers.</summary>
    public static InstallResult Resolve(LibraryGame g, LauncherData d)
    {
        var checks = new List<SourceCheck>();
        if (g.Entry?.ExePath is { } path)
        {
            if (File.Exists(path))
            {
                checks.Add(new("Your exe", true, true, true, path));
                var dir = Path.GetDirectoryName(path)!;
                return new InstallResult(new GameInstall("Your exe", dir, path, null, path, null), checks);
            }
            checks.Add(new("Your exe", true, false, false, $"{path} doesn't exist any more (moved or uninstalled?). Choose the new exe."));
        }
        if (g.Custom) return new InstallResult(null, checks);
        var r = GameScanner.Resolve(g.Def, d);
        checks.AddRange(r.Checks);
        return new InstallResult(r.Install, checks);
    }
}
