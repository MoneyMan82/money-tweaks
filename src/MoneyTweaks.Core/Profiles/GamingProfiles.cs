using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Profiles;

/// <summary>Power plan used while a profile is active (the previous plan is restored afterwards).</summary>
public enum PowerChoice { Keep, HighPerformance, Balanced }

public sealed class GamingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> TweakIds { get; set; } = [];
    public PowerChoice Power { get; set; } = PowerChoice.Keep;
    /// <summary>Requested timer resolution while active (0.5–1.0 ms), null = off. Same mechanism as the Input Delay Reducer.</summary>
    public double? TimerResolutionMs { get; set; }
    /// <summary>Raise the game's process to High priority when it is detected (runtime only, ends with the process).</summary>
    public bool RaiseGamePriority { get; set; }
    /// <summary>Executables (e.g. FortniteClient-Win64-Shipping.exe) that trigger automatic switching.</summary>
    public List<string> GameExes { get; set; } = [];
    public DateTime Modified { get; set; } = DateTime.Now;
    [JsonIgnore] public bool BuiltIn { get; init; }

    public GamingProfile Copy(string name) => new()
    {
        Name = name, Description = Description, TweakIds = [.. TweakIds], Power = Power, TimerResolutionMs = TimerResolutionMs,
        RaiseGamePriority = RaiseGamePriority, GameExes = [.. GameExes],
    };
}

/// <summary>What was changed by an active profile, saved to disk before any change so it can always be undone.</summary>
public sealed class ProfileSession
{
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public string? TriggeredBy { get; set; }
    /// <summary>Game Library key ("cat:fortnite") when a library game triggered the profile; checked by process identity.</summary>
    public string? TriggeredGameKey { get; set; }
    public Dictionary<string, bool> OriginalTweakStates { get; set; } = [];
    public string? PreviousPowerScheme { get; set; }
    public double? TimerMs { get; set; }
}

public sealed class ProfileFile
{
    public int Version { get; set; } = 1;
    public List<GamingProfile> Profiles { get; set; } = [];
    public HashSet<string> AutoSwitch { get; set; } = [];
    public ProfileSession? Active { get; set; }
}

public sealed record ProfilePreview(List<string> Changes, List<string> AlreadyActive, List<string> Skipped, List<string> Conflicts, bool NeedsAdmin);

public static class ProfileStore
{
    public const string ExportFormat = "money-tweaks-profiles";
    public const int MaxNameLength = 60;
    public static string FilePath => Path.Combine(AppPaths.Root, "profiles.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public const string FortniteExe = "FortniteClient-Win64-Shipping.exe";

    public static IReadOnlyList<GamingProfile> BuiltIns { get; } =
    [
        new()
        {
            Id = "profile.fortnite-competitive", BuiltIn = true, Name = "Fortnite Competitive",
            Description = "Consistency first: Game Mode, no background recording, Fortnite at High priority, launchers lowered, raw mouse input, no Sticky Keys pop-ups, no USB power saving, 0.5 ms timer and High performance power while Fortnite runs.",
            TweakIds = ["gen.game-mode", "gen.background-recording", "gen.gamebar-tips", "game.fortnite", "opt.launcher-priority", "gen.mouse-accel", "gen.sticky-keys", "gen.usb-suspend", "adv.system-responsiveness", "adv.games-task"],
            Power = PowerChoice.HighPerformance, TimerResolutionMs = 0.5, RaiseGamePriority = true, GameExes = [FortniteExe],
        },
        new()
        {
            Id = "profile.fortnite-performance", BuiltIn = true, Name = "Fortnite Performance Mode",
            Description = "Windows side of Fortnite's Performance Mode: lighter desktop effects, no background recording, Fortnite prioritised, High performance power. Fortnite's own rendering mode is set in-game (see Potato Graphics).",
            TweakIds = ["gen.game-mode", "gen.background-recording", "gen.visualfx-performance", "gen.transparency", "game.fortnite", "adv.system-responsiveness", "adv.games-task"],
            Power = PowerChoice.HighPerformance, RaiseGamePriority = true, GameExes = [FortniteExe],
        },
        new()
        {
            Id = "profile.geometry-dash", BuiltIn = true, Name = "Geometry Dash",
            Description = "Input-focused: Game Mode, no background recording, no Sticky Keys pop-ups, no USB power saving, 0.5 ms timer and High priority for GeometryDash.exe while it runs.",
            TweakIds = ["gen.game-mode", "gen.background-recording", "gen.gamebar-tips", "gen.sticky-keys", "gen.usb-suspend"],
            Power = PowerChoice.HighPerformance, TimerResolutionMs = 0.5, RaiseGamePriority = true, GameExes = ["GeometryDash.exe"],
        },
        new()
        {
            Id = "profile.general", BuiltIn = true, Name = "General Gaming",
            Description = "Safe everyday set for any game: Game Mode, no background recording or Game Bar tips, no USB power saving, multimedia scheduler favours games. Keeps your power plan.",
            TweakIds = ["gen.game-mode", "gen.background-recording", "gen.gamebar-tips", "gen.usb-suspend", "adv.system-responsiveness"],
        },
    ];

    public static ProfileFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex) { Log.Error("profiles.json unreadable (kept as .bad)", ex); try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new();
    }

    public static void Save(ProfileFile f)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
        File.Move(tmp, FilePath, true);
    }

    public static IEnumerable<GamingProfile> All(ProfileFile f) => BuiltIns.Concat(f.Profiles);

    public static List<string> Validate(GamingProfile p, IEnumerable<GamingProfile> others)
    {
        var e = new List<string>();
        var name = p.Name.Trim();
        if (name.Length == 0) e.Add("Give the profile a name.");
        if (name.Length > MaxNameLength) e.Add($"Name is longer than {MaxNameLength} characters.");
        if (others.Any(o => o.Id != p.Id && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) e.Add("Another profile already has this name.");
        foreach (var id in p.TweakIds)
            if (TweakCatalog.Find(id) is not { } t) e.Add($"Unknown tweak '{id}'.");
            else if (!Usable(t, out var why)) e.Add($"{t.Name} can't be used in a profile ({why}).");
        if (p.TimerResolutionMs is { } ms && ms is < 0.5 or > 1.0) e.Add("Timer resolution must be between 0.5 and 1.0 ms.");
        foreach (var exe in p.GameExes)
            if (!Regex.IsMatch(exe, @"^[\w .()\-+]{1,80}\.exe$", RegexOptions.IgnoreCase)) e.Add($"'{exe}' is not a program file name (like Game.exe).");
        if (p.RaiseGamePriority && p.GameExes.Count == 0) e.Add("Add the game's .exe to raise its priority.");
        return e;
    }

    /// <summary>Profiles switch tweaks on and off often, so only instant, reversible ones qualify.</summary>
    public static bool Usable(Tweak t, out string? why) => TweakStateGuard.Eligible(t, out why);

    public static void Export(IEnumerable<GamingProfile> profiles, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { format = ExportFormat, version = 1, profiles = profiles.Select(p => p.Copy(p.Name)) }, Json));

    public static (List<GamingProfile> profiles, List<string> problems) Import(string path, IEnumerable<GamingProfile> existing)
    {
        var problems = new List<string>();
        var result = new List<GamingProfile>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("format", out var fmt) || fmt.GetString() != ExportFormat)
            throw new InvalidDataException("This file is not a Money Tweaks profile export.");
        var list = doc.RootElement.GetProperty("profiles").Deserialize<List<GamingProfile>>(Json) ?? [];
        var all = existing.ToList();
        foreach (var p in list.Take(100))
        {
            var copy = p.Copy(p.Name.Trim()); // fresh id; ignore anything else in the file
            var baseName = copy.Name.Length == 0 ? "Imported profile" : copy.Name[..Math.Min(copy.Name.Length, MaxNameLength - 6)];
            copy.Name = baseName;
            for (int i = 2; all.Any(x => x.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase)); i++) copy.Name = $"{baseName} ({i})";
            var dropped = copy.TweakIds.Where(id => TweakCatalog.Find(id) is not { } t || !Usable(t, out _)).ToList();
            copy.TweakIds.RemoveAll(dropped.Contains);
            if (dropped.Count > 0) problems.Add($"{copy.Name}: skipped unknown or unsupported tweaks: {string.Join(", ", dropped)}");
            var errs = Validate(copy, all);
            if (errs.Count > 0) { problems.Add($"{copy.Name}: {string.Join(" ", errs)}"); continue; }
            all.Add(copy);
            result.Add(copy);
        }
        return (result, problems);
    }
}

/// <summary>Activates and deactivates profiles. Every change is reversible and read back.</summary>
public static class ProfileEngine
{
    private static string PowerCfg => Path.Combine(ProcessRunner.System32, "powercfg.exe");
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public static string? ActiveScheme()
    {
        var m = Regex.Match(ProcessRunner.Run(PowerCfg, "/getactivescheme").StdOut, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Conflicts inside a profile: tweaks that fight each other, or power settings that overlap.</summary>
    public static List<string> Conflicts(GamingProfile p)
    {
        var list = new List<string>();
        var ids = p.TweakIds.ToHashSet();
        foreach (var id in ids)
            if (TweakCatalog.Find(id) is { } t)
                foreach (var i in TweakAudit.IssuesFor(t).Where(i => i.Kind == AuditKind.Conflict && i.Other is not null && ids.Contains(i.Other) && string.CompareOrdinal(id, i.Other) < 0))
                    list.Add($"{t.Name} ↔ {TweakCatalog.Find(i.Other!)!.Name}: {i.Text}");
        if (p.Power != PowerChoice.Keep && ids.Contains("gen.ultimate-plan"))
            list.Add("The profile's power plan and 'Ultimate Performance power plan' both switch the plan; only the last one applied wins.");
        if (p.Power != PowerChoice.Keep && ids.Any(id => TweakCatalog.Find(id)?.Ops.OfType<Ops.PowerCfgOp>().Any() == true))
            list.Add("Info: power-plan tweaks change the plan that is active when they're applied. The profile switches plans first, so they apply to the profile's plan and leave your normal plan as it was.");
        if (p.RaiseGamePriority && ids.Any(id => id.StartsWith("game.", StringComparison.Ordinal)))
            list.Add("Info: 'Raise game priority' and a Game Profiles priority tweak do the same thing; harmless together.");
        return list;
    }

    public static ProfilePreview Preview(GamingProfile p)
    {
        var changes = new List<string>(); var active = new List<string>(); var skipped = new List<string>();
        bool admin = false;
        foreach (var id in p.TweakIds)
        {
            if (TweakCatalog.Find(id) is not { } t) { skipped.Add($"{id}: not found"); continue; }
            if (!ProfileStore.Usable(t, out var why)) { skipped.Add($"{t.Name} ({why})"); continue; }
            var st = TweakEngine.GetState(t);
            if (st == TweakState.Applied) { active.Add(t.Name); continue; }
            if (st == TweakState.NotApplicable) { skipped.Add($"{t.Name} (not available on this PC)"); continue; }
            admin |= t.RequiresAdmin;
            changes.Add($"{t.Name}{(t.Risk == Risk.Caution ? "  ⚠ Caution" : "")}\n" + string.Join("\n", t.Ops.Select(o => "     " + o.Describe())));
        }
        var scheme = ActiveScheme();
        if (p.Power == PowerChoice.HighPerformance && scheme != GameBooster.HighPerformance) changes.Add($"Power plan → High performance while active (restored to your current plan afterwards)");
        if (p.Power == PowerChoice.Balanced && scheme != Balanced) changes.Add("Power plan → Balanced while active (restored afterwards)");
        if (p.TimerResolutionMs is { } ms) changes.Add($"Timer resolution request {ms:0.0} ms while active (released afterwards; also ends if Money Tweaks closes)");
        if (p.RaiseGamePriority && p.GameExes.Count > 0) changes.Add($"{string.Join(", ", p.GameExes)} → High CPU priority when running (runtime only, ends with the game)");
        return new(changes, active, skipped, Conflicts(p), admin);
    }

    public static bool NeedsAdmin(GamingProfile p) =>
        p.TweakIds.Select(TweakCatalog.Find).Any(t => t is not null && t.RequiresAdmin && ProfileStore.Usable(t, out _) && TweakEngine.GetState(t) != TweakState.Applied);

    /// <summary>
    /// Applies a profile. <paramref name="persist"/> is called with the session BEFORE any change (so a crash can be
    /// recovered) and again at the end. Returns the session and problems; on a tweak failure everything is rolled back.
    /// </summary>
    public static (ProfileSession? session, List<string> problems) Activate(GamingProfile p, string? triggeredBy, Action<ProfileSession> persist, string? gameKey = null)
    {
        var usable = p.TweakIds.Select(TweakCatalog.Find).Where(t => t is not null && ProfileStore.Usable(t, out _)).Select(t => t!).ToList();
        var session = new ProfileSession { ProfileId = p.Id, ProfileName = p.Name, TriggeredBy = triggeredBy, TriggeredGameKey = gameKey };
        if (p.Power != PowerChoice.Keep) session.PreviousPowerScheme = ActiveScheme();
        persist(session); // saved before the first change
        var problems = new List<string>();
        try
        {
            // Power first, so power-plan tweaks land on the profile's plan rather than the user's normal one.
            if (p.Power != PowerChoice.Keep && session.PreviousPowerScheme is not null)
            {
                var target = p.Power == PowerChoice.HighPerformance ? GameBooster.HighPerformance : Balanced;
                if (!ProcessRunner.Run(PowerCfg, "/setactive " + target).Ok || ActiveScheme() != target)
                    problems.Add(p.Power == PowerChoice.HighPerformance ? "High performance plan isn't available on this PC (Modern Standby devices hide it); kept the current plan." : "Couldn't switch to Balanced.");
            }
            // Tweak states are captured after the plan switch: power-plan settings belong to a plan, and the restore
            // runs while that same plan is still active (the plan itself is restored last).
            session.OriginalTweakStates = TweakStateGuard.Capture(usable);
            persist(session);
            problems.AddRange(TweakStateGuard.SetAll(session.OriginalTweakStates.Keys, true));
            if (problems.Any(x => !x.StartsWith("High performance", StringComparison.Ordinal) && !x.StartsWith("Couldn't switch", StringComparison.Ordinal)))
            {
                var r = Deactivate(session);
                problems.Add("Rolled back. " + (r.Count == 0 ? "Everything is restored." : string.Join("; ", r)));
                return (null, problems);
            }
            if (p.TimerResolutionMs is { } ms) { TimerResolution.Set(ms); session.TimerMs = ms; }
            if (p.RaiseGamePriority) RaisePriority(p);
            persist(session);
            Log.Info($"Profile '{p.Name}' activated{(triggeredBy is null ? "" : $" by {triggeredBy}")}");
            return (session, problems);
        }
        catch (Exception ex)
        {
            Log.Error($"Activating profile '{p.Name}' failed", ex);
            var r = Deactivate(session);
            problems.Add($"{ex.Message}. Rolled back. " + (r.Count == 0 ? "Everything is restored." : string.Join("; ", r)));
            return (null, problems);
        }
    }

    /// <summary>Restores every tweak, the power plan and the timer to their pre-profile state. Empty list = verified restore.</summary>
    public static List<string> Deactivate(ProfileSession s)
    {
        var problems = TweakStateGuard.Restore(s.OriginalTweakStates);
        if (s.TimerMs is { } ms) { try { TimerResolution.Release(ms); } catch (Exception ex) { problems.Add("Timer: " + ex.Message); } }
        if (s.PreviousPowerScheme is { } prev && ActiveScheme() != prev)
        {
            ProcessRunner.Run(PowerCfg, "/setactive " + prev);
            if (ActiveScheme() != prev) problems.Add($"Power plan could not be restored to {prev}. Change it in Control Panel → Power Options.");
        }
        Log.Info(problems.Count == 0 ? $"Profile '{s.ProfileName}' deactivated; everything restored and read back." : $"Profile '{s.ProfileName}' deactivated with problems: {string.Join("; ", problems)}");
        return problems;
    }

    public static void RaisePriority(GamingProfile p)
    {
        foreach (var exe in p.GameExes)
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                using (proc)
                    try { if (proc.PriorityClass is ProcessPriorityClass.Normal) proc.PriorityClass = ProcessPriorityClass.High; }
                    catch (Exception ex) { Log.Debug($"priority {exe}: {ex.Message}"); }
    }

    /// <summary>
    /// The first configured game exe that is running, or null. Uses the Game Library's identity rules, so a generic exe
    /// only counts when it really is the game (javaw.exe only when it's Minecraft's Java).
    /// </summary>
    public static string? RunningGame(IEnumerable<string> exes) => Games.GameDetector.FirstRunning(exes)?.exe;
}
