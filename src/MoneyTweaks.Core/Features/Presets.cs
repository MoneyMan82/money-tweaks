using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

/// <summary>A named, reusable set of catalog tweaks. Built-ins ship with the app; custom ones live in presets.json.</summary>
public sealed class TweakPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> TweakIds { get; set; } = [];
    public DateTime Modified { get; set; } = DateTime.Now;
    [JsonIgnore] public bool BuiltIn { get; init; }

    public TweakPreset Copy(string? name = null) => new()
    {
        Name = name ?? Name, Description = Description, TweakIds = [.. TweakIds], Modified = DateTime.Now,
    };
}

/// <summary>Records which tweaks one preset application actually changed, so Undo reverts exactly those.</summary>
public sealed class PresetRun
{
    public string PresetId { get; set; } = "";
    public string PresetName { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public List<string> AppliedIds { get; set; } = [];
}

public sealed class PresetFile
{
    public int Version { get; set; } = 1;
    public List<TweakPreset> Presets { get; set; } = [];
    public List<PresetRun> History { get; set; } = [];
}

public static class PresetStore
{
    public const string ExportFormat = "money-tweaks-presets";
    public const int MaxNameLength = 60;
    public const int MaxDescriptionLength = 400;
    private const long MaxImportBytes = 1_000_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.Root, "presets.json");

    /// <summary>Built-in presets. Every id must exist in the catalog (enforced by tests).</summary>
    public static IReadOnlyList<TweakPreset> BuiltIns { get; } =
    [
        new()
        {
            Id = "builtin.competitive-fortnite", BuiltIn = true, Name = "Competitive Fortnite",
            Description = "Game Mode on, Game DVR off, Fortnite at High CPU priority, launchers lowered, fullscreen optimizations off, raw mouse input and no network throttling.",
            TweakIds = ["gen.game-mode", "gen.background-recording", "gen.gamebar-tips", "game.fortnite", "opt.launcher-priority", "adv.fso",
                "gen.mouse-accel", "gen.sticky-keys", "gen.usb-suspend", "net.throttling-index", "adv.system-responsiveness", "adv.games-task"],
        },
        new()
        {
            Id = "builtin.low-latency", BuiltIn = true, Name = "Low Latency",
            Description = "Input and timer tweaks: no mouse acceleration, fastest key repeat, no USB/adapter power saving, timer and scheduler settings, Nagle off (TCP only).",
            TweakIds = ["gen.mouse-accel", "gen.keyboard-repeat", "gen.sticky-keys", "gen.usb-suspend", "adv.global-timer-res", "adv.timer-dynamic-tick",
                "adv.priority-separation", "net.nagle", "net.adapter-power"],
        },
        new()
        {
            Id = "builtin.stable-fps", BuiltIn = true, Name = "Stable FPS",
            Description = "Fewer background interruptions: high-performance power settings, no core parking or power throttling, SysMain and the compatibility appraiser off, multimedia scheduler favours games.",
            TweakIds = ["gen.ultimate-plan", "gen.core-parking", "adv.power-throttling", "adv.system-responsiveness", "adv.games-task",
                "svc.sysmain", "dbl.task.compat-appraiser", "gen.background-recording", "dbl.edge-background"],
        },
        new()
        {
            Id = "builtin.potato", BuiltIn = true, Name = "Potato Graphics (Windows side)",
            Description = "The Windows half of potato mode: best-performance visual effects, no transparency, no background recording, fullscreen optimizations off, games prioritised, Ultimate Performance plan. Fortnite's own settings are on the Potato Graphics page.",
            TweakIds = ["gen.visualfx-performance", "gen.transparency", "gen.background-recording", "adv.fso", "adv.system-responsiveness", "adv.games-task", "gen.ultimate-plan"],
        },
        new()
        {
            Id = "builtin.balanced", BuiltIn = true, Name = "Balanced Gaming",
            Description = "Safe everyday set: Game Mode, no background recording, no USB suspend, no mouse acceleration, less telemetry and no sponsored app installs. Keeps power plans and services untouched.",
            TweakIds = ["gen.game-mode", "gen.background-recording", "gen.usb-suspend", "gen.mouse-accel", "dbl.telemetry", "dbl.consumer-features",
                "dbl.start-suggestions", "net.throttling-index", "adv.system-responsiveness"],
        },
    ];

    public static PresetFile Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for the user instead of silently overwriting it.
            var bad = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(FilePath, bad, true); } catch { }
            Log.Error($"presets.json was unreadable; a copy was kept at {bad}", ex);
        }
        return new();
    }

    /// <summary>Atomic save: write to a temp file, then replace, so a crash never leaves a half-written file.</summary>
    public static void Save(PresetFile f)
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (f.History.Count > 50) f.History.RemoveRange(0, f.History.Count - 50);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Returns problems that block saving. Unknown tweak ids are errors (they'd silently do nothing).</summary>
    public static List<string> Validate(TweakPreset p, IEnumerable<TweakPreset> others)
    {
        var errors = new List<string>();
        var name = p.Name?.Trim() ?? "";
        if (name.Length == 0) errors.Add("Give the preset a name.");
        else if (name.Length > MaxNameLength) errors.Add($"The name is too long (max {MaxNameLength} characters).");
        if (others.Any(o => o.Id != p.Id && string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"A preset called \"{name}\" already exists.");
        if ((p.Description?.Length ?? 0) > MaxDescriptionLength) errors.Add($"The description is too long (max {MaxDescriptionLength} characters).");
        if (p.TweakIds.Count == 0) errors.Add("Select at least one tweak.");
        var unknown = p.TweakIds.Where(id => TweakCatalog.Find(id) is null).ToList();
        if (unknown.Count > 0) errors.Add($"Unknown tweak id(s): {string.Join(", ", unknown.Take(8))}{(unknown.Count > 8 ? "…" : "")}");
        return errors;
    }

    public static void Export(IEnumerable<TweakPreset> presets, string path)
    {
        var doc = new Dictionary<string, object>
        {
            ["format"] = ExportFormat,
            ["version"] = 1,
            ["exported"] = DateTime.Now,
            ["presets"] = presets.Select(p => new { p.Name, p.Description, p.TweakIds }).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Json));
    }

    /// <summary>
    /// Parses an export file. Returns valid presets (with fresh ids and de-duplicated names) and a list of readable
    /// problems. Unknown tweak ids are dropped with a warning; presets left empty are skipped.
    /// </summary>
    public static (List<TweakPreset> presets, List<string> problems) Import(string path, IEnumerable<TweakPreset> existing)
    {
        var problems = new List<string>();
        var result = new List<TweakPreset>();
        var fi = new FileInfo(path);
        if (!fi.Exists) { problems.Add("The file doesn't exist."); return (result, problems); }
        if (fi.Length > MaxImportBytes) { problems.Add("The file is too large to be a Money Tweaks preset file (over 1 MB)."); return (result, problems); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { problems.Add($"Not valid JSON (line {ex.LineNumber + 1}): {ex.Message.Split('.')[0]}."); return (result, problems); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var fmt) || fmt.GetString() != ExportFormat)
            { problems.Add("This isn't a Money Tweaks preset export (missing \"format\": \"money-tweaks-presets\")."); return (result, problems); }
            if (root.TryGetProperty("version", out var ver) && ver.TryGetInt32(out var v) && v > 1)
            { problems.Add($"This file was made by a newer Money Tweaks (format version {v}). Update the app to import it."); return (result, problems); }
            if (!root.TryGetProperty("presets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            { problems.Add("The file has no \"presets\" list."); return (result, problems); }

            var names = existing.Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (var item in arr.EnumerateArray())
            {
                index++;
                string? name = item.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null;
                string desc = item.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                var ids = item.TryGetProperty("TweakIds", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Distinct().ToList() : [];
                if (string.IsNullOrWhiteSpace(name)) { problems.Add($"Preset #{index}: has no name, skipped."); continue; }
                if (name.Length > MaxNameLength) name = name[..MaxNameLength];
                if (desc.Length > MaxDescriptionLength) desc = desc[..MaxDescriptionLength];
                var unknown = ids.Where(id => TweakCatalog.Find(id) is null).ToList();
                if (unknown.Count > 0) problems.Add($"\"{name}\": {unknown.Count} tweak(s) this version doesn't know were left out ({string.Join(", ", unknown.Take(5))}).");
                ids = ids.Except(unknown).ToList();
                if (ids.Count == 0) { problems.Add($"\"{name}\": no usable tweaks, skipped."); continue; }
                var unique = name;
                for (int i = 2; names.Contains(unique); i++) unique = $"{name} ({i})";
                names.Add(unique);
                result.Add(new TweakPreset { Name = unique, Description = desc, TweakIds = ids });
            }
            if (index == 0) problems.Add("The file contains no presets.");
        }
        return (result, problems);
    }
}
