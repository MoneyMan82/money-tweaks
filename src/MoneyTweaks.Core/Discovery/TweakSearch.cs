using System.Text.RegularExpressions;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Core.Discovery;

/// <summary>What a query asks for: purposes, side effects to avoid, games and the remaining words.</summary>
public sealed record QueryIntent(IReadOnlyList<string> Purposes, IReadOnlySet<SideEffect> Avoid, IReadOnlyList<string> Games, IReadOnlyList<string> Terms, bool IsNatural)
{
    /// <summary>Human summary of how the query was understood (shown with the results).</summary>
    public string Understood()
    {
        var parts = new List<string>();
        parts.AddRange(Purposes.Select(p => TweakKnowledge.PurposeOf(p).Label.ToLowerInvariant()));
        parts.AddRange(Games);
        parts.AddRange(Avoid.Select(a => a switch
        {
            SideEffect.MorePowerAndHeat => "no extra heat/power",
            SideEffect.ShorterBattery => "battery life kept",
            SideEffect.WeakerSecurity => "security kept",
            SideEffect.NeedsRestart => "no restart",
            _ => "full undo",
        }));
        return string.Join(", ", parts.Distinct());
    }
}

public sealed record DiscoveryHit(Tweak Tweak, double Score, IReadOnlyList<string> Reasons, CompatSeverity? Compat);

/// <summary>Context that personalises ranking: this PC's compatibility findings and your goal (from Performance Genome).</summary>
public sealed record SearchContext(Func<Tweak, CompatSeverity?>? Compat = null, string? GoalPreset = null);

public sealed record SearchResult(QueryIntent Intent, IReadOnlyList<DiscoveryHit> Hits, int HiddenBySideEffect);

/// <summary>
/// Natural-language discovery over the existing tweak library ("make Fortnite smoother without increasing temperatures").
/// Ranks by text relevance (IDF-weighted, so rare words count more), purpose, game, compatibility with this PC and your
/// goal; drops tweaks with a side effect the query rules out and says how many. Only existing tweaks are returned.
/// </summary>
public static class TweakSearch
{
    private static readonly (string purpose, string[] words)[] Intents =
    [
        ("smooth", ["smooth", "smoother", "stutter", "stuttering", "stutters", "fps", "frame", "frames", "framerate", "frametime", "pacing", "hitch", "hitching", "microstutter", "performance", "faster game", "gaming", "boost", "drops", "dropping"]),
        ("latency", ["latency", "input lag", "input delay", "delay", "responsive", "responsiveness", "mouse", "aim", "keyboard", "click", "reaction"]),
        ("network", ["ping", "internet", "network", "wifi", "wi-fi", "connection", "packet", "download", "upload", "dns", "online", "lag spike", "rubber"]),
        ("privacy", ["privacy", "private", "tracking", "track", "telemetry", "spying", "spy", "data collection", "collect"]),
        ("distraction", ["ads", "advert", "ad ", "popup", "pop-up", "notification", "tips", "suggestion", "annoying", "nag", "widgets", "news"]),
        ("background", ["background", "services", "service", "startup apps", "ram usage", "memory usage", "resources", "bloat", "processes", "lighter"]),
        ("storage", ["disk", "space", "storage", "ssd", "boot", "boots", "startup time", "start faster"]),
        ("interface", ["explorer", "taskbar", "context menu", "menu", "appearance", "looks", "dark mode", "file extensions"]),
        ("apps", ["remove apps", "uninstall", "bloatware", "preinstalled", "candy crush", "xbox app"]),
        ("firmware", ["bios", "xmp", "expo", "resizable bar", "rebar", "c-states"]),
    ];

    // "without increasing temperatures", "but not hotter", "keep temps low", "no restart", "don't hurt battery"…
    private static readonly Regex AvoidClause = new(@"\b(without|but not|not|no|don'?t|dont|avoid|keep|less|never|minus)\b\s+((?:increas\w*|rais\w*|more|higher|hurt\w*|los\w*|chang\w*|affect\w*|reduc\w*|break\w*|any|my|the|extra|getting|making|it)\s+)*(\w+)(\s+\w+)?", RegexOptions.Compiled);
    private static readonly (string[] words, SideEffect[] effects)[] AvoidNouns =
    [
        (["temp", "temps", "temperature", "temperatures", "heat", "hot", "hotter", "thermal", "thermals", "fan", "fans", "noise", "noisy", "loud", "louder", "power", "watts", "electricity", "cool", "cooler", "quiet"], [SideEffect.MorePowerAndHeat]),
        (["battery", "batteries", "laptop"], [SideEffect.ShorterBattery, SideEffect.MorePowerAndHeat]),
        (["security", "safety", "safe", "protection", "defender", "secure"], [SideEffect.WeakerSecurity]),
        (["restart", "reboot", "rebooting", "restarting"], [SideEffect.NeedsRestart]),
        (["undo", "permanent", "irreversible"], [SideEffect.LimitedUndo]),
    ];
    // Goals stated positively ("cooler", "quieter", "longer battery") also rule out heat.
    private static readonly string[] PositiveCool = ["cooler", "quieter", "less heat", "lower temp", "battery life", "longer battery", "save power", "efficient", "efficiency", "on battery", "laptop battery", "my battery"];

    // Words that carry intent or grammar, not a topic: they must not rank a tweak just because its name contains "lower".
    private static readonly HashSet<string> Filler = ["make", "my", "me", "i", "want", "the", "a", "an", "to", "for", "in", "on", "and", "or", "of", "with", "how", "can", "do", "is", "it",
        "pc", "computer", "windows", "better", "best", "improve", "more", "less", "get", "help", "please", "some", "any", "that", "this", "while", "when", "also", "but", "so", "should", "game", "games",
        "lower", "reduce", "fix", "stop", "increase", "faster", "quicker", "higher", "decrease", "minimize", "maximize", "speed", "disable", "enable", "remove", "turn", "off", "fewer", "smoother", "smooth", "without"];

    public static QueryIntent Parse(string query)
    {
        var q = " " + query.ToLowerInvariant().Trim() + " ";
        var avoid = new HashSet<SideEffect>();
        var consumed = new HashSet<string>();
        foreach (Match m in AvoidClause.Matches(q))
        {
            var noun = m.Groups[3].Value;
            var noun2 = m.Groups[4].Value.Trim();
            foreach (var (words, effects) in AvoidNouns)
                if (words.Contains(noun) || (noun2.Length > 0 && words.Contains(noun2)))
                {
                    foreach (var e in effects) avoid.Add(e);
                    foreach (var w in m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) consumed.Add(w);
                }
        }
        if (PositiveCool.Any(p => q.Contains(p, StringComparison.Ordinal))) avoid.Add(SideEffect.MorePowerAndHeat);

        var purposes = new List<string>();
        foreach (var (purpose, words) in Intents)
            if (words.Any(w => Regex.IsMatch(q, $@"(?<![a-z]){Regex.Escape(w.Trim())}(?![a-z])"))) purposes.Add(purpose);
        // "lag" alone is ambiguous: frame lag or network lag.
        if (Regex.IsMatch(q, @"\blag\b|\blaggy\b") && !purposes.Contains("network")) { purposes.Add("smooth"); purposes.Add("network"); }

        var games = Discovery.Games.Names.Where(g => Regex.IsMatch(q, $@"(?<![a-z]){Regex.Escape(g.Key)}(?![a-z])")).Select(g => g.Value).Distinct().ToList();
        if (games.Count > 0 && !purposes.Contains("smooth")) purposes.Insert(0, "smooth");

        var terms = Regex.Matches(q, @"[a-z0-9][a-z0-9\-\.]*").Select(m => m.Value.Trim('.', '-'))
            .Where(w => w.Length >= 2 && !Filler.Contains(w) && !consumed.Contains(w))
            .Select(TweakKnowledge.Stem).Distinct().ToList();
        bool natural = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 || avoid.Count > 0 || purposes.Count > 0 || games.Count > 0;
        return new(purposes.Distinct().ToList(), avoid, games, terms, natural);
    }

    public static SearchResult Search(TweakKnowledge k, string query, SearchContext? ctx = null, int max = 40)
    {
        ctx ??= new();
        var intent = Parse(query);
        int n = Math.Max(1, k.Profiles.Count);
        double Idf(string term) => Math.Log(1.0 + n / (double)Math.Max(1, k.DocFreq.GetValueOrDefault(term)));
        var hits = new List<DiscoveryHit>();
        int hidden = 0;
        bool goalCool = ctx.GoalPreset is "cool" or "quiet";
        foreach (var p in k.Profiles)
        {
            var t = p.Tweak;
            if (TweakAudit.DisabledReason(t) is not null) continue;
            var reasons = new List<string>();
            // Text relevance: exact term, then prefix (≥4 letters), weighted by where it appears and how rare it is.
            double text = 0;
            var matched = new List<string>();
            foreach (var term in intent.Terms)
            {
                if (p.Terms.TryGetValue(term, out var w)) { text += w * Idf(term); matched.Add(term); continue; }
                if (term.Length >= 4 && p.Terms.Keys.FirstOrDefault(x => x.StartsWith(term, StringComparison.Ordinal) || (x.Length >= 4 && term.StartsWith(x, StringComparison.Ordinal))) is { } near)
                { text += 0.6 * p.Terms[near] * Idf(near); matched.Add(near); }
            }
            double purpose = 0;
            foreach (var pu in intent.Purposes)
                if (p.Purposes.Contains(pu)) { purpose += p.Home == pu ? 8 : 5; reasons.Add("for " + TweakKnowledge.PurposeOf(pu).Label.ToLowerInvariant()); }
            double game = 0;
            foreach (var g in intent.Games)
            {
                if (p.Games.Contains(g)) { game += 14; reasons.Add(g + "-specific"); }
                else if (p.Games.Count > 0) game -= 6;                       // a tweak for a different game
                else if (p.Purposes.Contains("smooth") || p.Purposes.Contains("latency")) game += 1.5; // general gaming tweak
            }
            if (intent.Games.Count == 0 && p.Games.Count > 0 && !intent.Terms.Any(p.Terms.ContainsKey)) game -= 3; // game-specific, but no game asked for
            // Something must actually match: words, or a purpose (purpose-only matches need a natural query).
            if (text <= 0 && purpose <= 0 && game <= 0) continue;
            if (text <= 0 && purpose + game <= 0) continue;
            if (text <= 0 && !intent.IsNatural) continue;
            if (p.Effects.Overlaps(intent.Avoid)) { hidden++; continue; }

            double score = text * 1.6 + purpose + game;
            if (matched.Count > 0) reasons.Insert(0, "matches " + string.Join(", ", matched.Distinct().Take(3).Select(m => $"\"{m}\"")));
            if (intent.Avoid.Contains(SideEffect.MorePowerAndHeat) && (p.Purposes.Contains("smooth") || p.Purposes.Contains("latency"))) reasons.Add("doesn't raise temperatures");
            if (t.Recommended) { score += 1.5; reasons.Add("recommended"); }
            // Honest about known limits from the catalog review (e.g. Nagle doesn't touch UDP game traffic).
            if (TweakAudit.IssuesFor(t).FirstOrDefault(i => i.Kind is AuditKind.Ineffective or AuditKind.Obsolete) is { } weak)
            { score -= 4; reasons.Add("limited effect: " + weak.Text.Split('.')[0].ToLowerInvariant()); }
            var tier = Compatibility.TierOf(t);
            if (tier == TweakTier.Experimental) { score -= 3; reasons.Add("experimental"); }
            if (goalCool && p.Effects.Contains(SideEffect.MorePowerAndHeat)) { score -= 4; reasons.Add("adds heat (your goal: cooler)"); }
            else if (ctx.GoalPreset is "maxfps" or "competitive" or "latency" && (p.Purposes.Contains("smooth") || p.Purposes.Contains("latency"))) score += 1;
            CompatSeverity? compat = ctx.Compat?.Invoke(t);
            if (compat == CompatSeverity.Block) { score *= 0.3; reasons.Add("not applicable on this PC"); }
            else if (compat == CompatSeverity.Warn) { score -= 2; reasons.Add("has a warning for this PC"); }
            else if (ctx.Compat is not null) reasons.Add("fits this PC");
            if (p.Effects.Contains(SideEffect.WeakerSecurity)) { score -= 5; reasons.Add("weakens security"); }
            hits.Add(new(t, score, reasons.Distinct().ToList(), compat));
        }
        var ranked = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Tweak.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        // Drop the long tail of weak purpose-only matches when strong matches exist.
        if (ranked.Count > 8 && ranked[0].Score > 0) ranked = ranked.Where((h, i) => i < 8 || h.Score >= ranked[0].Score * 0.25).ToList();
        return new(intent, ranked, hidden);
    }
}
