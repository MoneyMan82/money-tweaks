using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Macros;

public enum StepKind { KeyPress, KeyDown, KeyUp, Combo, MouseClick, MouseDown, MouseUp, Delay, WaitKeyDown, WaitKeyUp }
public enum TriggerKind { Manual, PressKey, HoldKey, ToggleKey, Color }
public enum MouseBtn { Left, Right, Middle }

/// <summary>One timeline step. Pure data: no scripts, no mouse movement.</summary>
public sealed class MacroStep
{
    public StepKind Kind { get; set; }
    /// <summary>Key name for key steps and waits; for Combo, '+'-joined names (e.g. "LCtrl+C").</summary>
    public string Key { get; set; } = "";
    public MouseBtn Button { get; set; } = MouseBtn.Left;
    /// <summary>Delay length, hold time for KeyPress/Combo/MouseClick, or timeout for waits (0 = no timeout).</summary>
    public int Ms { get; set; } = 30;

    public MacroStep Clone() => (MacroStep)MemberwiseClone();

    public string Describe() => Kind switch
    {
        StepKind.KeyPress => $"Press {Key}" + (Ms > 0 ? $" (hold {Ms} ms)" : ""),
        StepKind.KeyDown => $"Hold down {Key}",
        StepKind.KeyUp => $"Release {Key}",
        StepKind.Combo => $"Press {Key} together" + (Ms > 0 ? $" (hold {Ms} ms)" : ""),
        StepKind.MouseClick => $"Click {Button} mouse button",
        StepKind.MouseDown => $"Hold {Button} mouse button",
        StepKind.MouseUp => $"Release {Button} mouse button",
        StepKind.Delay => $"Wait {Ms} ms",
        StepKind.WaitKeyDown => $"Wait until {Key} is pressed" + (Ms > 0 ? $" (max {Ms} ms)" : ""),
        StepKind.WaitKeyUp => $"Wait until {Key} is released" + (Ms > 0 ? $" (max {Ms} ms)" : ""),
        _ => Kind.ToString(),
    };
}

public sealed class Macro
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Game { get; set; } = "General";
    public string Category { get; set; } = "Utility";
    public TriggerKind Trigger { get; set; } = TriggerKind.Manual;
    public string TriggerKey { get; set; } = "";
    /// <summary>How many times to run the timeline. 0 = keep looping until stopped (needs a stop trigger).</summary>
    public int Repeat { get; set; } = 1;
    /// <summary>Hard stop after this many seconds, whatever happens.</summary>
    public int SafetyTimeoutSec { get; set; } = 600;
    public List<MacroStep> Steps { get; set; } = [];
    /// <summary>Screen-colour conditions. With Trigger = Color they start the macro; with a key trigger they must also be true (AND).</summary>
    public List<ColorTrigger> ColorTriggers { get; set; } = [];
    /// <summary>All = every colour condition must hold (AND); Any = at least one (OR).</summary>
    public TriggerLogic ColorLogic { get; set; } = TriggerLogic.All;
    public int ColorPollMs { get; set; } = 150;
    public DateTime Modified { get; set; } = DateTime.Now;
    [JsonIgnore] public bool BuiltIn { get; init; }

    public Macro Copy(string? name = null) => new()
    {
        Name = name ?? Name, Description = Description, Game = Game, Category = Category, Trigger = Trigger, TriggerKey = TriggerKey,
        Repeat = Repeat, SafetyTimeoutSec = SafetyTimeoutSec, Steps = Steps.Select(s => s.Clone()).ToList(), Modified = DateTime.Now,
        ColorTriggers = ColorTriggers.Select(c => c.Clone()).ToList(), ColorLogic = ColorLogic, ColorPollMs = ColorPollMs,
    };

    public string TriggerText => Trigger switch
    {
        TriggerKind.PressKey => $"Press {TriggerKey}",
        TriggerKind.HoldKey => $"Hold {TriggerKey} (stops on release)",
        TriggerKind.ToggleKey => $"Toggle with {TriggerKey}",
        TriggerKind.Color => $"Screen colour ({ColorTriggers.Count} condition{(ColorTriggers.Count == 1 ? "" : "s")}, {(ColorLogic == TriggerLogic.All ? "AND" : "OR")})",
        _ => "Run button only",
    } + (Trigger is not TriggerKind.Color and not TriggerKind.Manual && ColorTriggers.Count > 0 ? " + colour" : "");
}

public sealed class MacroFile
{
    public int Version { get; set; } = 1;
    public List<Macro> Macros { get; set; } = [];
}

/// <summary>Games whose rules or anti-cheat forbid or flag automated input. Macros refuse to run while they are focused.</summary>
public static class MacroPolicy
{
    public static readonly (string process, string game)[] Blocked =
    [
        ("FortniteClient-Win64-Shipping", "Fortnite"), ("VALORANT-Win64-Shipping", "VALORANT"), ("cs2", "Counter-Strike 2"),
        ("r5apex", "Apex Legends"), ("r5apex_dx12", "Apex Legends"), ("cod", "Call of Duty"), ("RainbowSix", "Rainbow Six Siege"),
        ("RainbowSix_Vulkan", "Rainbow Six Siege"), ("Overwatch", "Overwatch 2"), ("League of Legends", "League of Legends"),
        ("TslGame", "PUBG"), ("RocketLeague", "Rocket League"), ("Marvel-Win64-Shipping", "Marvel Rivals"), ("destiny2", "Destiny 2"),
        ("EscapeFromTarkov", "Escape from Tarkov"), ("RustClient", "Rust"), ("FiveM", "FiveM / GTA Online"), ("GTA5", "GTA V / GTA Online"),
        ("deadlock", "Deadlock"), ("dota2", "Dota 2"), ("FallGuys_client_game", "Fall Guys"), ("Warframe.x64", "Warframe"),
    ];

    public static string? BlockedGame(string? processName) =>
        processName is null ? null : Blocked.FirstOrDefault(b => b.process.Equals(processName, StringComparison.OrdinalIgnoreCase)).game;

    public const int MaxSteps = 500, MaxRepeat = 10_000, MaxStepMs = 60_000, MaxTimeoutSec = 3600, MinColorPollMs = 50, MaxColorTriggers = 8;

    public static List<string> Validate(Macro m, IEnumerable<Macro> others)
    {
        var e = new List<string>();
        var name = m.Name?.Trim() ?? "";
        if (name.Length == 0) e.Add("Give the macro a name.");
        else if (name.Length > 60) e.Add("The name is too long (max 60 characters).");
        if (others.Any(o => o.Id != m.Id && o.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))) e.Add($"A macro called \"{name}\" already exists.");
        if ((m.Description?.Length ?? 0) > 400) e.Add("The description is too long (max 400 characters).");
        if (m.Steps.Count == 0) e.Add("Add at least one step.");
        if (m.Steps.Count > MaxSteps) e.Add($"Too many steps (max {MaxSteps}).");
        if (m.Repeat < 0 || m.Repeat > MaxRepeat) e.Add($"Repeat must be 0 (loop) to {MaxRepeat}.");
        if (m.SafetyTimeoutSec < 1 || m.SafetyTimeoutSec > MaxTimeoutSec) e.Add($"Safety timeout must be 1–{MaxTimeoutSec} seconds.");
        if (m.Trigger is not (TriggerKind.Manual or TriggerKind.Color) && !VirtualKeys.TryParse(m.TriggerKey, out _)) e.Add("Choose a trigger key.");
        if (m.Trigger == TriggerKind.Color && m.ColorTriggers.Count == 0) e.Add("Add at least one screen-colour condition.");
        if (m.ColorTriggers.Count > MaxColorTriggers) e.Add($"Too many colour conditions (max {MaxColorTriggers}).");
        if (m.ColorTriggers.Count > 0 && (m.ColorPollMs < MinColorPollMs || m.ColorPollMs > 5000)) e.Add($"Colour check interval must be {MinColorPollMs}–5000 ms.");
        for (int i = 0; i < m.ColorTriggers.Count; i++)
        {
            var c = m.ColorTriggers[i];
            if (c.Mode != ColorMode.Changes && !ColorTrigger.TryParseHex(c.Color, out _)) e.Add($"Colour condition {i + 1}: enter a colour like #FF8800.");
            if (c.Tolerance is < 0 or > 255) e.Add($"Colour condition {i + 1}: tolerance must be 0–255.");
            if (c.Width is < 1 or > ColorTrigger.MaxSide || c.Height is < 1 or > ColorTrigger.MaxSide) e.Add($"Colour condition {i + 1}: region must be 1–{ColorTrigger.MaxSide} pixels per side.");
        }
        if (m.Repeat == 0 && m.Trigger == TriggerKind.PressKey) e.Add("A looping macro needs a way to stop: use Hold, Toggle or Run button.");
        for (int i = 0; i < m.Steps.Count; i++)
        {
            var s = m.Steps[i];
            var n = i + 1;
            if (s.Ms < 0 || s.Ms > MaxStepMs) e.Add($"Step {n}: time must be 0–{MaxStepMs} ms.");
            switch (s.Kind)
            {
                case StepKind.KeyPress or StepKind.KeyDown or StepKind.KeyUp or StepKind.WaitKeyDown or StepKind.WaitKeyUp:
                    if (!VirtualKeys.TryParse(s.Key, out _)) e.Add($"Step {n}: choose a key.");
                    break;
                case StepKind.Combo:
                    var parts = s.Key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 2 || parts.Length > 5 || parts.Any(p => !VirtualKeys.TryParse(p, out _))) e.Add($"Step {n}: a combination needs 2–5 valid keys.");
                    break;
            }
        }
        if (m.Steps.Count > 0 && m.Steps.All(s => s.Kind is StepKind.WaitKeyDown or StepKind.WaitKeyUp or StepKind.Delay))
            e.Add("The macro only waits; add at least one key or mouse action.");
        return e;
    }
}

public static class MacroStore
{
    public const string ExportFormat = "money-tweaks-macros";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static string FilePath => Path.Combine(AppPaths.Root, "macros.json");

    public static MacroFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex)
        {
            var bad = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(FilePath, bad, true); } catch { }
            Log.Error($"macros.json was unreadable; a copy was kept at {bad}", ex);
        }
        return new();
    }

    public static void Save(MacroFile f)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void Export(IEnumerable<Macro> macros, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { format = ExportFormat, version = 1, macros = macros.ToList() }, Json));

    /// <summary>Imports macros from an export file; every macro is validated, invalid ones are skipped with a reason.</summary>
    public static (List<Macro> macros, List<string> problems) Import(string path, IEnumerable<Macro> existing)
    {
        var ok = new List<Macro>();
        var problems = new List<string>();
        var fi = new FileInfo(path);
        if (!fi.Exists) { problems.Add("The file doesn't exist."); return (ok, problems); }
        if (fi.Length > 2_000_000) { problems.Add("The file is too large to be a Money Tweaks macro file (over 2 MB)."); return (ok, problems); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { problems.Add($"Not valid JSON (line {ex.LineNumber + 1})."); return (ok, problems); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var f) || f.GetString() != ExportFormat)
            { problems.Add("This isn't a Money Tweaks macro export (missing \"format\": \"money-tweaks-macros\")."); return (ok, problems); }
            if (root.TryGetProperty("version", out var v) && v.TryGetInt32(out var ver) && ver > 1)
            { problems.Add($"This file was made by a newer Money Tweaks (format version {ver})."); return (ok, problems); }
            if (!root.TryGetProperty("macros", out var arr) || arr.ValueKind != JsonValueKind.Array)
            { problems.Add("The file has no \"macros\" list."); return (ok, problems); }
            var all = existing.ToList();
            int i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                i++;
                Macro? m;
                try { m = el.Deserialize<Macro>(Json); }
                catch (JsonException) { problems.Add($"Macro #{i}: unreadable, skipped."); continue; }
                if (m is null) continue;
                m.Id = Guid.NewGuid().ToString("N");
                m.Name = (m.Name ?? "").Trim();
                var baseName = m.Name;
                for (int k = 2; all.Any(x => x.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)); k++) m.Name = $"{baseName} ({k})";
                var errs = MacroPolicy.Validate(m, all);
                if (errs.Count > 0) { problems.Add($"\"{baseName}\": {string.Join(" ", errs)} Skipped."); continue; }
                all.Add(m);
                ok.Add(m);
            }
            if (i == 0) problems.Add("The file contains no macros.");
        }
        return (ok, problems);
    }
}
