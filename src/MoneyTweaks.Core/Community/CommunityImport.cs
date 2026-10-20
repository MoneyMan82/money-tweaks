using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoneyTweaks.Community;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Profiles;

namespace MoneyTweaks.Core.Community;

/// <summary>One tweak in an import preview: what it is on this PC, or why it will be skipped.</summary>
public sealed record TweakPreviewLine(string Id, string Name, bool Caution, bool WillImport, string? Note);

/// <summary>
/// Converts Community Hub posts into Money Tweaks items and back. Importing only adds a profile, preset or crosshair to
/// your library: nothing is applied or activated. Everything is validated against this PC's catalog and the app's
/// own rules first; unknown or unsupported tweaks are listed and skipped, never guessed.
/// </summary>
public static class CommunityImport
{
    private static readonly JsonSerializerOptions Json = new(Api.Json) { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static List<TweakPreviewLine> Preview(TweakBundle b, bool forProfile)
    {
        var lines = new List<TweakPreviewLine>();
        foreach (var id in b.TweakIds.Distinct())
        {
            if (TweakCatalog.Find(id) is not { } t) { lines.Add(new(id, id, false, false, "Not in this version of Money Tweaks: skipped")); continue; }
            if (t.Kind == TweakKind.Guide) { lines.Add(new(id, t.Name, false, false, "A guide, not a setting: skipped")); continue; }
            if (forProfile && !ProfileStore.Usable(t, out var why)) { lines.Add(new(id, t.Name, t.Risk == Risk.Caution, false, $"Can't be used in a profile ({why}): skipped")); continue; }
            lines.Add(new(id, t.Name, t.Risk == Risk.Caution, true, t.Risk == Risk.Caution ? "Caution: asks for confirmation when you apply it" : null));
        }
        return lines;
    }

    private static string UniqueName(string name, IEnumerable<string> taken, int max)
    {
        name = PostRules.Clean(name, max - 5);
        if (name.Length == 0) name = "Imported";
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var n = name;
        for (int i = 2; set.Contains(n); i++) n = $"{name} ({i})";
        return n;
    }

    public static (GamingProfile? profile, List<string> notes) ToGamingProfile(TweakBundle b, IEnumerable<GamingProfile> existing)
    {
        var notes = new List<string>();
        var all = existing.ToList();
        var keep = Preview(b, forProfile: true).Where(l => l.WillImport).Select(l => l.Id).ToList();
        if (keep.Count < b.TweakIds.Count) notes.Add($"{b.TweakIds.Count - keep.Count} tweak(s) skipped (not available or not usable in profiles here).");
        var p = new GamingProfile
        {
            Name = UniqueName(b.Name, all.Select(x => x.Name), ProfileStore.MaxNameLength),
            Description = PostRules.Clean(b.Description, 300),
            TweakIds = keep,
            Power = Enum.TryParse<PowerChoice>(b.Power, out var pw) ? pw : PowerChoice.Keep,
            RaiseGamePriority = b.RaiseGamePriority,
            GameExes = [.. b.GameExes],
        };
        if (b.TimerResolutionMs is { } ms)
        {
            if (ms is >= 0.5 and <= 1.0) p.TimerResolutionMs = ms;
            else notes.Add($"Timer resolution {ms.ToString(CultureInfo.InvariantCulture)} ms is outside what profiles support (0.5–1.0 ms): left off.");
        }
        if (p.RaiseGamePriority && p.GameExes.Count == 0) p.RaiseGamePriority = false;
        var errors = ProfileStore.Validate(p, all);
        if (errors.Count > 0) { notes.AddRange(errors); return (null, notes); }
        return (p, notes);
    }

    public static (TweakPreset? preset, List<string> notes) ToPreset(TweakBundle b, IEnumerable<TweakPreset> existing)
    {
        var notes = new List<string>();
        var all = existing.ToList();
        var keep = Preview(b, forProfile: false).Where(l => l.WillImport).Select(l => l.Id).ToList();
        if (keep.Count < b.TweakIds.Count) notes.Add($"{b.TweakIds.Count - keep.Count} tweak(s) skipped (not available in this version).");
        var p = new TweakPreset
        {
            Name = UniqueName(b.Name, all.Select(x => x.Name), PresetStore.MaxNameLength),
            Description = PostRules.Clean(b.Description, PresetStore.MaxDescriptionLength),
            TweakIds = keep,
        };
        var errors = PresetStore.Validate(p, all);
        if (errors.Count > 0) { notes.AddRange(errors); return (null, notes); }
        return (p, notes);
    }

    /// <summary>
    /// A crosshair from a post. Image layers point at the post's images ("img:&lt;id&gt;"): <paramref name="download"/> fetches
    /// the PNG, which is checked and stored like a PNG you import yourself. Values are clamped by the app's own rules.
    /// </summary>
    public static async Task<(CrosshairProfile? profile, List<string> notes)> ToCrosshairAsync(JsonElement json, IEnumerable<CrosshairProfile> existing, Func<string, Task<byte[]?>> download)
    {
        var notes = new List<string>();
        CrosshairProfile? p;
        try { p = json.Deserialize<CrosshairProfile>(Json); }
        catch (JsonException) { p = null; }
        if (p is null) { notes.Add("The crosshair in this post couldn't be read."); return (null, notes); }
        CrosshairSanitizer.Clean(p);
        foreach (var l in p.Layers.Where(l => l.Kind == LayerKind.Image))
        {
            var reference = l.ImageFile;
            l.ImageFile = null;
            if (reference is null || !reference.StartsWith("img:", StringComparison.Ordinal) || !PostRules.IdPattern().IsMatch(reference[4..]))
            { notes.Add($"Layer \"{l.Name}\" has no image."); continue; }
            var bytes = await download(reference[4..]);
            if (bytes is null) { notes.Add($"The image for \"{l.Name}\" isn't available (it may be awaiting moderation)."); continue; }
            try
            {
                l.ImageFile = CrosshairImages.Store(bytes, out var info);
                l.ImageAspect = (double)info.Height / info.Width;
            }
            catch (InvalidDataException ex) { notes.Add($"The image for \"{l.Name}\" wasn't imported: {ex.Message}"); }
        }
        var copy = p.Copy(UniqueName(p.Name, existing.Select(x => x.Name), CrosshairSanitizer.MaxName));
        return (copy, notes);
    }

    // ---- publishing -------------------------------------------------------------------------------------------------

    public static TweakBundle FromGamingProfile(GamingProfile p) => new()
    {
        Name = p.Name, Description = p.Description, TweakIds = [.. p.TweakIds], Power = p.Power.ToString(),
        TimerResolutionMs = p.TimerResolutionMs, RaiseGamePriority = p.RaiseGamePriority, GameExes = [.. p.GameExes],
    };

    public static TweakBundle FromPreset(TweakPreset p) => new() { Name = p.Name, Description = p.Description, TweakIds = [.. p.TweakIds] };

    /// <summary>The crosshair as it goes into a post. Local image layers become "img:&lt;id&gt;" through <paramref name="mapImage"/> or are dropped.</summary>
    public static JsonElement CrosshairForPost(CrosshairProfile p, Func<string, string?> mapImage)
    {
        var c = p.Clone();
        foreach (var l in c.Layers.Where(l => l.Kind == LayerKind.Image))
            l.ImageFile = l.ImageFile is { } f && mapImage(f) is { } id ? "img:" + id : null;
        return JsonSerializer.SerializeToElement(c, Json);
    }

    /// <summary>Plain-text summary of game settings, for copying into a game or a note.</summary>
    public static string SettingsText(GameSettings s)
    {
        var sb = new StringBuilder();
        void Line(string k, object? v) { if (v is not null) sb.AppendLine($"{k}: {v}"); }
        if (s.ResolutionWidth is { } w && s.ResolutionHeight is { } h) Line("Resolution", $"{w}×{h}{(s.Stretched == true ? " (stretched)" : "")}");
        Line("Refresh rate", s.RefreshHz is { } hz ? $"{hz} Hz" : null);
        Line("FPS cap", s.FpsCap is { } f ? (f == 0 ? "Unlimited" : f.ToString(CultureInfo.CurrentCulture)) : null);
        Line("Sensitivity", s.Sensitivity?.ToString(CultureInfo.CurrentCulture));
        Line("ADS sensitivity", s.AdsSensitivity?.ToString(CultureInfo.CurrentCulture));
        Line("Mouse DPI", s.MouseDpi);
        foreach (var g in s.Graphics) Line(g.Name, g.Value);
        if (s.Keybinds.Count > 0) { sb.AppendLine("Keybinds:"); foreach (var k in s.Keybinds) sb.AppendLine($"  {k.Action} = {k.Key}"); }
        if (s.Notes is { Length: > 0 } n) { sb.AppendLine(); sb.AppendLine(n); }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Parses "Name = Value" lines (keybinds, graphics) typed in the publish form.</summary>
    public static List<(string a, string b)> ParsePairs(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => { var i = l.IndexOfAny(['=', ':']); return i > 0 ? (l[..i].Trim(), l[(i + 1)..].Trim()) : (l, ""); })
            .Where(p => p.Item1.Length > 0).ToList();
}
