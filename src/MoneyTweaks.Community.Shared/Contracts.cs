using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoneyTweaks.Community;

/// <summary>Fixed lists offered when publishing. Keeping them fixed makes filtering reliable and blocks junk values.</summary>
public static class Taxonomy
{
    public static readonly string[] Games =
    [
        "Fortnite", "Geometry Dash", "VALORANT", "Counter-Strike 2", "Apex Legends", "Rocket League", "Roblox", "Minecraft",
        "Marvel Rivals", "Call of Duty", "Overwatch 2", "League of Legends", "Rainbow Six Siege", "PUBG", "GTA V", "Other",
    ];

    public static readonly string[] Categories =
        ["Competitive", "Performance", "Low-end PC", "Visual quality", "Stretched resolution", "Crosshair", "Streaming", "Other"];

    public static readonly string[] ReportReasons =
        ["Spam or advertising", "Offensive or hateful", "Sexual or graphic content", "Harassment", "Dangerous or malicious settings", "Misleading", "Copyright", "Other"];

    public static readonly string[] Sorts = ["new", "top", "helpful", "imports"];
}

public static class CommunityGuidelines
{
    public const int Version = 1;
    public const string Text =
        "• Share settings you actually use. Don't copy other people's posts as your own.\n" +
        "• Keep it clean: no hate, harassment, sexual or graphic content, and no slurs, in text or images.\n" +
        "• Screenshots should show gameplay or settings. No personal information (yours or anyone else's).\n" +
        "• No cheats, exploits, anti-cheat bypasses, injectors or links to them. Don't post settings meant to harm a PC.\n" +
        "• No spam, advertising, referral links or repeated posts.\n" +
        "• FPS and benchmark numbers are your own measurements: they're shown as user-reported.\n" +
        "• Report anything that breaks these rules. Moderators can hide posts and images and suspend accounts.";
}

// ---- post content ----------------------------------------------------------------------------------------------------

public sealed class Keybind
{
    public string Action { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class SettingPair
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>In-game settings. Display only: Money Tweaks never changes them automatically.</summary>
public sealed class GameSettings
{
    public int? ResolutionWidth { get; set; }
    public int? ResolutionHeight { get; set; }
    public bool? Stretched { get; set; }
    public int? RefreshHz { get; set; }
    public int? FpsCap { get; set; }
    public double? Sensitivity { get; set; }
    public double? AdsSensitivity { get; set; }
    public int? MouseDpi { get; set; }
    public List<Keybind> Keybinds { get; set; } = [];
    public List<SettingPair> Graphics { get; set; } = [];
    public string? Notes { get; set; }
}

/// <summary>A Money Tweaks gaming profile or preset: references to catalog tweaks by id, never commands.</summary>
public sealed class TweakBundle
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> TweakIds { get; set; } = [];
    /// <summary>Gaming profiles only: "Keep", "HighPerformance" or "Balanced" (Money Tweaks' PowerChoice).</summary>
    public string? Power { get; set; }
    public double? TimerResolutionMs { get; set; }
    public bool RaiseGamePriority { get; set; }
    public List<string> GameExes { get; set; } = [];
}

/// <summary>Optional, entered (or confirmed) by the author. Never collected automatically.</summary>
public sealed class HardwareInfo
{
    public string? Cpu { get; set; }
    public string? Gpu { get; set; }
    public int? RamGb { get; set; }
    public string? Monitor { get; set; }
}

/// <summary>Always shown as user-reported: the server can't verify it.</summary>
public sealed class BenchmarkReport
{
    public double? AverageFps { get; set; }
    public double? OnePercentLowFps { get; set; }
    public string? Scenario { get; set; }
}

public sealed class PostContent
{
    public GameSettings? Settings { get; set; }
    /// <summary>A Money Tweaks crosshair profile (validated and clamped by the server with the app's own rules).</summary>
    public JsonElement? Crosshair { get; set; }
    public TweakBundle? GamingProfile { get; set; }
    public TweakBundle? Preset { get; set; }
    public HardwareInfo? Hardware { get; set; }
    public BenchmarkReport? Benchmark { get; set; }

    public List<string> Sections()
    {
        var s = new List<string>();
        if (Settings is not null) s.Add("Game settings");
        if (Crosshair is { ValueKind: JsonValueKind.Object }) s.Add("Crosshair");
        if (GamingProfile is not null) s.Add("Gaming profile");
        if (Preset is not null) s.Add("Preset");
        if (Benchmark is not null) s.Add("Benchmark (user-reported)");
        return s;
    }
}

public sealed class PostInput
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Game { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public PostContent Content { get; set; } = new();
    /// <summary>Images uploaded by the author for this post (not generated previews).</summary>
    public List<string> ImageIds { get; set; } = [];
}

// ---- responses -----------------------------------------------------------------------------------------------------

public sealed class AuthorInfo
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public DateTime JoinedUtc { get; set; }
    public int PostCount { get; set; }
    public int LikesReceived { get; set; }
}

public sealed class ImageInfo
{
    public string Id { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>"upload" (counts toward the daily limit) or "crosshair-preview" (generated by the server, free).</summary>
    public string Kind { get; set; } = "upload";
    /// <summary>"approved", "pending" (only the author and moderators can see it) or "rejected".</summary>
    public string Status { get; set; } = "pending";
    public bool HasAlpha { get; set; }
    public string Url { get; set; } = "";
    public string ThumbUrl { get; set; } = "";
}

public class PostSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Game { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public AuthorInfo Author { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int Likes { get; set; }
    public int HelpfulYes { get; set; }
    public int HelpfulNo { get; set; }
    public int Imports { get; set; }
    public bool Liked { get; set; }
    public bool Bookmarked { get; set; }
    public string? ThumbUrl { get; set; }
    public List<string> Sections { get; set; } = [];
    /// <summary>"visible", "hidden" (auto-hidden after reports, awaiting review), "removed".</summary>
    public string Status { get; set; } = "visible";
}

public sealed class PostDetail : PostSummary
{
    public string Description { get; set; } = "";
    public PostContent Content { get; set; } = new();
    public List<ImageInfo> Images { get; set; } = [];
    public bool? MyHelpful { get; set; }
    public bool CanEdit { get; set; }
    public bool Reported { get; set; }
}

public sealed class PostList
{
    public List<PostSummary> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

public sealed class QuotaInfo
{
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
    public DateTime ResetsAtUtc { get; set; }
}

public sealed class MeInfo
{
    public AuthorInfo User { get; set; } = new();
    public bool IsAdmin { get; set; }
    public bool Banned { get; set; }
    public QuotaInfo Quota { get; set; } = new();
}

public sealed class SessionInfo
{
    public string Token { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public MeInfo Me { get; set; } = new();
}

public sealed class ServerConfig
{
    public string? GitHubClientId { get; set; }
    public int DailyUploadLimit { get; set; }
    public long MaxUploadBytes { get; set; }
    public int MaxImageDimension { get; set; }
    public int MinImageDimension { get; set; }
    public List<string> AcceptedTypes { get; set; } = [];
    public string[] Games { get; set; } = Taxonomy.Games;
    public string[] Categories { get; set; } = Taxonomy.Categories;
    public string[] ReportReasons { get; set; } = Taxonomy.ReportReasons;
    public string Guidelines { get; set; } = CommunityGuidelines.Text;
    public bool DevSignIn { get; set; }
    /// <summary>Server parts that aren't set up yet (e.g. "GITHUB_CLIENT_SECRET secret"). Empty when everything works.</summary>
    public List<string> NotConfigured { get; set; } = [];
}

public sealed class UploadResult
{
    public ImageInfo Image { get; set; } = new();
    public QuotaInfo Quota { get; set; } = new();
}

public sealed class ReportInput
{
    public string Reason { get; set; } = "";
    public string Details { get; set; } = "";
}

public sealed class AdminReport
{
    public string Id { get; set; } = "";
    public string PostId { get; set; } = "";
    public string PostTitle { get; set; } = "";
    public string PostStatus { get; set; } = "";
    public string Reporter { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int OpenReportsOnPost { get; set; }
}

public sealed class AdminImage
{
    public ImageInfo Image { get; set; } = new();
    public string Owner { get; set; } = "";
    public string? PostId { get; set; }
    public string? PostTitle { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class AdminQueue
{
    public List<AdminReport> Reports { get; set; } = [];
    public List<AdminImage> PendingImages { get; set; } = [];
    public List<PostSummary> HiddenPosts { get; set; } = [];
}

public sealed class ApiError
{
    public string Error { get; set; } = "";
    public string Message { get; set; } = "";
}

public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Validation shared by the app (instant feedback) and the server (authoritative).</summary>
public static partial class PostRules
{
    public const int TitleMin = 6, TitleMax = 80, DescriptionMax = 4000, MaxTags = 8, TagMax = 24, MaxImages = 4,
        MaxTweakIds = 400, MaxKeybinds = 60, MaxGraphics = 60, MaxLinks = 2, ShortText = 80, NotesMax = 1000;

    [GeneratedRegex("^[a-z0-9][a-z0-9 +\\-]{0,23}$")] private static partial Regex TagPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9._\\-]{1,80}$")] private static partial Regex TweakIdPattern();
    [GeneratedRegex("^[A-Za-z0-9 ._()\\-]{1,80}\\.exe$", RegexOptions.IgnoreCase)] private static partial Regex ExePattern();
    [GeneratedRegex("^[0-9a-f]{32}$")] public static partial Regex IdPattern();
    [GeneratedRegex("https?://|www\\.", RegexOptions.IgnoreCase)] private static partial Regex LinkPattern();
    [GeneratedRegex("[\\p{Cc}\\p{Cf}\\p{Co}\\p{Cn}-[\\n\\t]]")] private static partial Regex ControlChars();

    /// <summary>Removes control/format characters (except newlines and tabs) and trims. Prevents invisible-text tricks.</summary>
    public static string Clean(string? s, int max, bool multiline = false)
    {
        s = ControlChars().Replace(s ?? "", "");
        if (!multiline) s = s.Replace('\n', ' ').Replace('\t', ' ');
        s = s.Trim();
        return s.Length > max ? s[..max].Trim() : s;
    }

    /// <summary>Normalizes the input in place and returns the problems (empty = valid). Crosshair structure is checked separately.</summary>
    public static List<string> Validate(PostInput p)
    {
        var e = new List<string>();
        p.Title = Clean(p.Title, TitleMax + 1);
        p.Description = Clean(p.Description, DescriptionMax + 1, multiline: true);
        if (p.Title.Length < TitleMin || p.Title.Length > TitleMax) e.Add($"Title must be {TitleMin}–{TitleMax} characters.");
        if (p.Description.Length > DescriptionMax) e.Add($"Description can be at most {DescriptionMax} characters.");
        if (LinkPattern().Matches(p.Title).Count > 0) e.Add("Links aren't allowed in the title.");
        if (LinkPattern().Matches(p.Description).Count > MaxLinks) e.Add($"At most {MaxLinks} links in the description.");
        if (!Taxonomy.Games.Contains(p.Game)) e.Add("Choose a game from the list.");
        if (!Taxonomy.Categories.Contains(p.Category)) e.Add("Choose a category from the list.");
        p.Tags = (p.Tags ?? []).Select(t => Clean(t, TagMax + 1).ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        if (p.Tags.Count > MaxTags) e.Add($"At most {MaxTags} tags.");
        if (p.Tags.Any(t => !TagPattern().IsMatch(t))) e.Add("Tags can use letters, numbers, spaces, + and - (up to 24 characters).");
        p.ImageIds = (p.ImageIds ?? []).Distinct().ToList();
        if (p.ImageIds.Count > MaxImages) e.Add($"At most {MaxImages} images per post.");
        if (p.ImageIds.Any(i => !IdPattern().IsMatch(i))) e.Add("Unknown image.");

        var c = p.Content ??= new PostContent();
        if (c.Settings is null && c.Crosshair is not { ValueKind: JsonValueKind.Object } && c.GamingProfile is null && c.Preset is null)
            e.Add("Add at least one thing to share: game settings, a crosshair, a gaming profile or a preset.");
        if (c.Crosshair is { } x && x.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)) e.Add("The crosshair isn't valid.");
        if (c.Settings is { } s) ValidateSettings(s, e);
        if (c.GamingProfile is { } gp) ValidateBundle(gp, "Gaming profile", e, profile: true);
        if (c.Preset is { } pr) ValidateBundle(pr, "Preset", e, profile: false);
        if (c.Hardware is { } h)
        {
            h.Cpu = Opt(h.Cpu, ShortText); h.Gpu = Opt(h.Gpu, ShortText); h.Monitor = Opt(h.Monitor, ShortText);
            if (h.RamGb is < 1 or > 2048) e.Add("RAM must be 1–2048 GB.");
            if (h.Cpu is null && h.Gpu is null && h.RamGb is null && h.Monitor is null) c.Hardware = null;
        }
        if (c.Benchmark is { } b)
        {
            b.Scenario = Opt(b.Scenario, ShortText);
            if (b.AverageFps is < 0 or > 5000 || b.OnePercentLowFps is < 0 or > 5000) e.Add("FPS values must be 0–5000.");
            if (b.AverageFps is null && b.OnePercentLowFps is null) c.Benchmark = null;
        }
        return e;
    }

    private static string? Opt(string? s, int max) { s = Clean(s, max); return s.Length == 0 ? null : s; }

    private static void ValidateSettings(GameSettings s, List<string> e)
    {
        if (s.ResolutionWidth is < 320 or > 15360 || s.ResolutionHeight is < 200 or > 8640) e.Add("Resolution must be between 320×200 and 15360×8640.");
        if (s.ResolutionWidth is null != s.ResolutionHeight is null) e.Add("Enter both resolution width and height.");
        if (s.RefreshHz is < 24 or > 1000) e.Add("Refresh rate must be 24–1000 Hz.");
        if (s.FpsCap is < 0 or > 5000) e.Add("FPS cap must be 0 (unlimited) to 5000.");
        if (s.Sensitivity is < 0 or > 100000 || s.AdsSensitivity is < 0 or > 100000) e.Add("Sensitivity must be 0–100000.");
        if (s.MouseDpi is < 50 or > 100000) e.Add("Mouse DPI must be 50–100000.");
        s.Keybinds = (s.Keybinds ?? []).Select(k => new Keybind { Action = Clean(k?.Action, 40), Key = Clean(k?.Key, 24) }).Where(k => k.Action.Length > 0 && k.Key.Length > 0).ToList();
        s.Graphics = (s.Graphics ?? []).Select(g => new SettingPair { Name = Clean(g?.Name, 40), Value = Clean(g?.Value, 40) }).Where(g => g.Name.Length > 0).ToList();
        if (s.Keybinds.Count > MaxKeybinds) e.Add($"At most {MaxKeybinds} keybinds.");
        if (s.Graphics.Count > MaxGraphics) e.Add($"At most {MaxGraphics} graphics settings.");
        s.Notes = Opt(s.Notes, NotesMax);
    }

    private static void ValidateBundle(TweakBundle b, string what, List<string> e, bool profile)
    {
        b.Name = Clean(b.Name, 60);
        b.Description = Clean(b.Description, 500, multiline: true);
        if (b.Name.Length == 0) e.Add($"{what} needs a name.");
        b.TweakIds = (b.TweakIds ?? []).Select(t => (t ?? "").Trim()).Distinct().ToList();
        if (b.TweakIds.Count > MaxTweakIds) e.Add($"{what}: too many tweaks.");
        if (b.TweakIds.Any(t => !TweakIdPattern().IsMatch(t))) e.Add($"{what}: contains an invalid tweak id.");
        b.GameExes = (b.GameExes ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList();
        if (b.GameExes.Count > 20 || b.GameExes.Any(x => !ExePattern().IsMatch(x))) e.Add($"{what}: game executables must be plain .exe names.");
        if (!profile) { b.Power = null; b.TimerResolutionMs = null; b.RaiseGamePriority = false; b.GameExes = []; }
        else
        {
            if (b.Power is not null && b.Power is not ("Keep" or "HighPerformance" or "Balanced")) e.Add($"{what}: unknown power plan.");
            if (b.TimerResolutionMs is < 0.5 or > 15.625) e.Add($"{what}: timer resolution must be 0.5–15.625 ms.");
        }
        if (b.TweakIds.Count == 0 && !(profile && (b.Power is not null and not "Keep" || b.TimerResolutionMs is not null || b.RaiseGamePriority)))
            e.Add($"{what} doesn't change anything.");
    }
}
