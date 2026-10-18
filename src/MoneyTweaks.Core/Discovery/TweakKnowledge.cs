using System.Text.RegularExpressions;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Discovery;

/// <summary>What a tweak is for. One tweak can serve several; the first is its home on the map.</summary>
public sealed record Purpose(string Id, string Label, string Blurb, string Icon);

/// <summary>A side effect that matters when combining tweaks or choosing for a goal.</summary>
public enum SideEffect { MorePowerAndHeat, WeakerSecurity, LimitedUndo, NeedsRestart, ShorterBattery }

/// <summary>Everything Discovery knows about one tweak, derived from the catalog itself (no hand-tagging, so it scales).</summary>
public sealed record TweakProfile(Tweak Tweak, IReadOnlyList<string> Purposes, IReadOnlySet<SideEffect> Effects, IReadOnlySet<string> Games,
    IReadOnlySet<string> Targets, IReadOnlyDictionary<string, int> Terms)
{
    public string Home => Purposes.Count > 0 ? Purposes[0] : "other";
}

public enum RelationKind { Conflict, Duplicate, TradeOff, OverlappingEffect, SameArea, Complements }

/// <summary>How another tweak relates to this one, in words, with a warning tone when it matters.</summary>
public sealed record Relation(Tweak Other, RelationKind Kind, string Text, double Score)
{
    public bool IsWarning => Kind is RelationKind.Conflict or RelationKind.Duplicate or RelationKind.TradeOff or RelationKind.OverlappingEffect;
}

/// <summary>A catalog-wide finding between two tweaks.</summary>
public sealed record PairFinding(Tweak A, Tweak B, RelationKind Kind, string Text, string Source);

/// <summary>
/// The discovery index over the tweak library: purposes and side effects per tweak (rules over name, description, group,
/// category and the actual operations), the subsystems each touches (registry key, service, power setting, task),
/// inverted indexes for related-tweak and conflict lookups (never all-pairs), and term statistics for search. Exact
/// conflicts and duplicates come from <see cref="Compatibility.Between"/> and <see cref="TweakAudit"/>; nothing here
/// invents tweaks, it only connects existing ones.
/// </summary>
public sealed class TweakKnowledge
{
    public static readonly IReadOnlyList<Purpose> Purposes =
    [
        new("smooth", "Smoother gameplay", "Frame pacing, GPU and CPU scheduling, power behaviour while gaming.", "\uE7FC"),
        new("latency", "Lower input latency", "Input devices, timers and presentation paths between your hand and the screen.", "\uE962"),
        new("network", "Network & ping", "Adapter, TCP/IP and DNS behaviour.", "\uE968"),
        new("background", "Less background load", "Services, scheduled tasks, launchers and startup apps competing with games.", "\uE9F5"),
        new("privacy", "Privacy", "Telemetry, tracking and data collection.", "\uE72E"),
        new("distraction", "Fewer ads & interruptions", "Ads, suggestions, tips and pop-ups.", "\uE7ED"),
        new("storage", "Storage & boot", "Disk, file system, hibernation and startup speed.", "\uEDA2"),
        new("interface", "Interface & convenience", "Explorer, taskbar, menus and visual effects.", "\uE771"),
        new("apps", "Built-in apps", "Removing preinstalled apps.", "\uE71D"),
        new("firmware", "Firmware (BIOS)", "Settings only the BIOS can change.", "\uE950"),
    ];

    public static Purpose PurposeOf(string id) => Purposes.FirstOrDefault(p => p.Id == id) ?? new(id, "Other", "", "");

    // Keyword rules: purpose ← words in the tweak's own text. Order inside a rule doesn't matter; the purpose order above
    // decides the "home" when several match, except where the category is decisive (see Derive).
    private static readonly (string purpose, string[] words)[] PurposeWords =
    [
        ("latency", ["input", "mouse", "keyboard", "latency", "delay", "timer", "interrupt", "msi mode", "dynamic tick", "hpet", "pointer", "acceleration", "usb selective", "fullscreen optimization", "flip model"]),
        ("smooth", ["game", "gaming", "fps", "frame", "stutter", "gpu", "graphics", "scheduling", "hags", "multimedia", "mmcss", "priority", "core parking", "processor", "ultimate performance", "power plan", "power throttling", "game dvr", "background recording", "memory compression", "paging executive", "fortnite", "windowed games", "direct3d", "directx", "vrr"]),
        ("network", ["network", "tcp", "dns", "adapter", "nagle", "ethernet", "lso", "teredo", "llmnr", "ping", "wi-fi", "wifi", "qos", "ttl", "port", "netbios", "ipv6", "bandwidth", "delivery optimization"]),
        ("privacy", ["telemetry", "tracking", "diagnostic", "advertising id", "activity history", "location", "feedback", "ceip", "data collection", "customer experience", "recall", "ai data", "inking", "typing", "speech", "camera", "microphone", "clipboard history", "cloud"]),
        ("distraction", ["ads", "advert", "suggest", "tips", "promot", "recommend", "spotlight", "sponsored", "notification", "toast", "pop-up", "popup", "widgets", "news", "copilot", "bing", "web search", "consumer features"]),
        ("background", ["service", "scheduled task", "background", "startup app", "launcher", "indexer", "search index", "sysmain", "superfetch", "update", "maintenance", "auto-start", "autostart", "discord", "steam", "epic"]),
        ("storage", ["disk", "ntfs", "hibernat", "fast startup", "prefetch", "storage", "8.3", "8dot3", "last access", "page file", "pagefile", "boot", "ssd", "hdd", "reserved storage", "large system cache"]),
        ("interface", ["explorer", "taskbar", "menu", "context menu", "dark mode", "file extension", "hidden files", "animation", "transparency", "visual effect", "snap", "aero", "start menu", "this pc", "end task", "search box", "task view"]),
    ];

    private static readonly string[] HeatWords = ["minimum processor state", "core parking", "ultimate performance", "aspm", "never turn off", "disk idle", "power throttling", "high performance plan", "always on"];
    private static readonly string[] SecurityWords = ["defender", "smartscreen", "uac", "user account control", "mitigation", "spectre", "meltdown", "memory integrity", "hvci", "vbs", "firewall", "exploit protection", "core isolation"];

    /// <summary>Words too common in tweak names to mean "the same feature".</summary>
    private static readonly HashSet<string> Generic = ["disable", "enable", "manual", "service", "priority", "windows", "remove", "microsoft", "settings", "background", "notification", "experience", "suggestion", "network", "power", "update", "search", "start", "store",
        "throttl", "express", "player", "google", "never", "quick", "large", "group", "platform", "video", "mobile", "media", "program", "automatic", "optimization", "recommend", "offline", "shutdown", "clock", "extension", "filter", "sticky", "timestamp", "resolution", "keyboard", "track", "collector"];

    private static readonly Regex Word = new(@"[a-z0-9][a-z0-9\-\.]*", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = ["the", "a", "an", "and", "or", "of", "to", "for", "in", "on", "with", "is", "it", "its", "be", "by", "this", "that", "your", "you", "can", "when", "from", "as", "at", "are", "not", "no", "only", "windows", "set", "sets", "turn", "turns", "off", "makes", "make"];

    private readonly Dictionary<string, TweakProfile> _byId;
    private readonly Dictionary<string, List<string>> _byTarget = [];
    private readonly Dictionary<string, List<string>> _byPurpose = [];
    private readonly Dictionary<string, List<PairFinding>> _pairs = [];
    public IReadOnlyList<TweakProfile> Profiles { get; }
    /// <summary>Document frequency of each term (for search ranking and "rare shared facet" weighting).</summary>
    public IReadOnlyDictionary<string, int> DocFreq { get; }
    public IReadOnlyList<PairFinding> Findings { get; }

    private static readonly Lazy<TweakKnowledge> _current = new(() => new TweakKnowledge(TweakCatalog.All));
    public static TweakKnowledge Current => _current.Value;

    public TweakKnowledge(IEnumerable<Tweak> universe)
    {
        Profiles = universe.Select(Derive).ToList();
        _byId = Profiles.ToDictionary(p => p.Tweak.Id);
        var df = new Dictionary<string, int>();
        foreach (var p in Profiles)
        {
            foreach (var term in p.Terms.Keys) df[term] = df.GetValueOrDefault(term) + 1;
            foreach (var t in p.Targets) (_byTarget.TryGetValue(t, out var l) ? l : _byTarget[t] = []).Add(p.Tweak.Id);
            foreach (var pu in p.Purposes) (_byPurpose.TryGetValue(pu, out var l2) ? l2 : _byPurpose[pu] = []).Add(p.Tweak.Id);
        }
        DocFreq = df;
        Findings = ComputeFindings();
        foreach (var f in Findings)
        {
            (_pairs.TryGetValue(f.A.Id, out var a) ? a : _pairs[f.A.Id] = []).Add(f);
            (_pairs.TryGetValue(f.B.Id, out var b) ? b : _pairs[f.B.Id] = []).Add(f);
        }
    }

    public TweakProfile? Profile(string id) => _byId.GetValueOrDefault(id);
    public IReadOnlyList<string> InPurpose(string purpose) => _byPurpose.GetValueOrDefault(purpose) ?? [];
    public IReadOnlyList<PairFinding> FindingsFor(string id) => _pairs.GetValueOrDefault(id) ?? [];

    // ---- derivation ---------------------------------------------------------------------------------------------------

    public static TweakProfile Derive(Tweak t)
    {
        var text = $"{t.Id.Replace('.', ' ').Replace('-', ' ')} {t.Name} {t.Description} {t.Group} {CategoryInfo.DisplayName(t.Category)} {t.Warning}".ToLowerInvariant();
        var purposes = new List<string>();
        // Category decides where the catalog itself is unambiguous.
        string? decisive = t.Category switch
        {
            TweakCategory.AppRemoval => "apps", TweakCategory.Bios => "firmware", TweakCategory.Networking => "network",
            TweakCategory.GameProfiles => "smooth", TweakCategory.Services => "background", TweakCategory.AppOptimizer => "background", _ => null,
        };
        if (decisive is not null) purposes.Add(decisive);
        if (t.Category == TweakCategory.Debloating) purposes.Add(Contains(text, PurposeWords.First(p => p.purpose == "privacy").words) ? "privacy" : Contains(text, PurposeWords.First(p => p.purpose == "distraction").words) ? "distraction" : "background");
        // Group names in the catalog are strong signals.
        switch (t.Group)
        {
            case "Input" or "Timers": purposes.Add("latency"); break;
            case "GPU" or "GPU preference" or "Gaming" or "Scheduler" or "Power": purposes.Add("smooth"); break;
            case "Explorer" or "Taskbar" or "Interface": purposes.Add("interface"); break;
            case "File system" or "Startup": purposes.Add("storage"); break;
        }
        foreach (var (purpose, words) in PurposeWords)
            if (Contains(text, words)) purposes.Add(purpose);
        if (purposes.Count == 0) purposes.Add("interface");
        purposes = purposes.Distinct().ToList();
        // Interface/convenience tweaks don't also count as "smoother gameplay" because their text says "game" somewhere.
        if (purposes[0] == "interface") purposes.Remove("smooth");

        var effects = new HashSet<SideEffect>();
        if (Contains(text, HeatWords) || (t.Group == "Power" && !text.Contains("hibernat") && !text.Contains("fast startup") && !text.Contains("usb")))
        { effects.Add(SideEffect.MorePowerAndHeat); effects.Add(SideEffect.ShorterBattery); }
        if (t.Group == "Security trade-off" || Contains(text, SecurityWords) || TweakAudit.IssuesFor(t).Any(i => i.Kind == AuditKind.Security)) effects.Add(SideEffect.WeakerSecurity);
        if (t.Ops.OfType<AppxRemoveOp>().Any() || TweakAudit.IssuesFor(t).Any(i => i.Kind == AuditKind.OneWay)) effects.Add(SideEffect.LimitedUndo);
        if (t.RequiresReboot) effects.Add(SideEffect.NeedsRestart);

        // Game-specific only when the game is in the tweak's name or id (descriptions mention games as examples).
        var nameText = $"{t.Id.Replace('.', ' ').Replace('-', ' ')} {t.Name}".ToLowerInvariant();
        var games = new HashSet<string>(Discovery.Games.Names.Where(g => StartsWord(nameText, g.Key)).Select(g => g.Value));

        var targets = new HashSet<string>();
        foreach (var op in t.Ops)
            switch (op)
            {
                case RegValueOp r: targets.Add($"reg:{r.Hive}\\{r.Path}".ToLowerInvariant()); break;
                case RegStringFlagOp f: targets.Add($"reg:{f.Hive}\\{f.Path}".ToLowerInvariant()); break;
                case ServiceStartOp s: targets.Add("svc:" + s.Service.ToLowerInvariant()); break;
                case PowerCfgOp p: targets.Add("power:" + p.SettingKey.ToLowerInvariant()); targets.Add("power:plan"); break;
                case ScheduledTaskOp st: targets.Add("task:" + st.TaskPath.ToLowerInvariant()); break;
                case CommandOp c when c.CheckCommand is not null: targets.Add("cmd:" + c.CheckCommand.ToLowerInvariant()); break;
            }

        var terms = new Dictionary<string, int>();
        void AddTerms(string s, int weight)
        {
            foreach (Match m in Word.Matches(s.ToLowerInvariant()))
            {
                var w = Stem(m.Value.Trim('.', '-'));
                if (w.Length < 2 || Stop.Contains(w)) continue;
                terms[w] = Math.Max(terms.GetValueOrDefault(w), weight);
            }
        }
        AddTerms(t.Description, 1);
        AddTerms(t.Group ?? "", 2);
        AddTerms(t.Name, 3);
        return new(t, purposes, effects, games, targets, terms);
    }

    /// <summary>A word or phrase that starts at a word boundary ("port" doesn't match "support"; "hibernat" matches "hibernation").</summary>
    private static bool Contains(string text, string[] words) => words.Any(w => StartsWord(text, w));

    public static bool StartsWord(string text, string w)
    {
        for (int i = text.IndexOf(w, StringComparison.Ordinal); i >= 0; i = text.IndexOf(w, i + 1, StringComparison.Ordinal))
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) return true;
        return false;
    }

    /// <summary>Tiny English stemmer: enough for "stuttering"/"stutter", "temperatures"/"temperature", "games"/"game".</summary>
    public static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) w = w[..^3] + "y";
        else if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal) && !w.EndsWith("ses", StringComparison.Ordinal)) w = w[..^1];
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
        if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) w = w[..^2];
        return w;
    }

    // ---- related ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Genuinely related tweaks, strongest first: conflicts and duplicates (exact setting), tweaks changing the same
    /// subsystem, and tweaks serving the same purpose in other categories, each with the reason in words. Plain
    /// "complements" need a shared purpose that is rare enough to mean something.
    /// </summary>
    public List<Relation> Related(string id, int max = 6)
    {
        if (Profile(id) is not { } me) return [];
        var scored = new Dictionary<string, (double score, RelationKind kind, string text)>();
        void Offer(Tweak other, double score, RelationKind kind, string text)
        {
            if (other.Id == id) return;
            if (!scored.TryGetValue(other.Id, out var cur) || score > cur.score) scored[other.Id] = (score, kind, text);
        }
        foreach (var f in FindingsFor(id))
        {
            var other = f.A.Id == id ? f.B : f.A;
            Offer(other, f.Kind switch { RelationKind.Conflict => 100, RelationKind.Duplicate => 90, RelationKind.TradeOff => 70, RelationKind.OverlappingEffect => 60, _ => 45 }, f.Kind, f.Text);
        }
        foreach (var target in me.Targets.Where(t => t != "power:plan"))
            foreach (var oid in _byTarget.GetValueOrDefault(target) ?? [])
                if (_byId[oid] is var o && !scored.ContainsKey(oid))
                    Offer(o.Tweak, 40 + (o.Tweak.Category != me.Tweak.Category ? 10 : 0), RelationKind.SameArea, $"Changes the same area ({Describe(target)}), so the two affect each other.");
        // Same purpose, other categories: weight by how rare the shared purpose is and by shared rare terms.
        int n = Profiles.Count;
        foreach (var pu in me.Purposes.Take(1))
        {
            var members = InPurpose(pu);
            double rarity = Math.Log(1.0 + n / Math.Max(1.0, members.Count));
            foreach (var oid in members)
            {
                if (oid == id || scored.ContainsKey(oid)) continue;
                var o = _byId[oid];
                if (o.Tweak.Category == me.Tweak.Category && o.Tweak.Group == me.Tweak.Group) continue; // same group is already next to it
                if (TweakAudit.DisabledReason(o.Tweak) is not null) continue;                           // never suggest a disabled tweak
                if (o.Home != pu) continue;                                                             // must share the main purpose
                double shared = me.Terms.Keys.Intersect(o.Terms.Keys).Sum(t => Math.Log(1.0 + n / (double)DocFreq.GetValueOrDefault(t, n)));
                if (shared < 3.5) continue; // a shared purpose alone isn't a relationship
                double score = 10 * rarity + shared + (o.Tweak.Category != me.Tweak.Category ? 8 : 0) + (o.Tweak.Recommended ? 2 : 0);
                string text = $"Also helps {PurposeOf(pu).Label.ToLowerInvariant()}, from {CategoryInfo.DisplayName(o.Tweak.Category)}" +
                              (o.Tweak.Group is { } g ? $" › {g}" : "") + ".";
                if (o.Effects.Contains(SideEffect.MorePowerAndHeat) && !me.Effects.Contains(SideEffect.MorePowerAndHeat)) text += " It raises power use and heat.";
                if (o.Effects.Contains(SideEffect.WeakerSecurity)) text += " It weakens a security protection.";
                Offer(o.Tweak, score, RelationKind.Complements, text);
            }
        }
        return scored.OrderByDescending(kv => kv.Value.score).Take(max)
            .Select(kv => new Relation(_byId[kv.Key].Tweak, kv.Value.kind, kv.Value.text, kv.Value.score)).ToList();
    }

    public static string Describe(string target)
    {
        var (kind, rest) = (target.Split(':', 2)[0], target.Split(':', 2)[1]);
        return kind switch
        {
            "reg" => "registry key " + string.Join('\\', rest.Split('\\').TakeLast(2)),
            "svc" => "the " + rest + " service",
            "power" => rest == "plan" ? "the active power plan" : "the same power-plan setting",
            "task" => "scheduled task " + rest.Split('\\').Last(),
            "cmd" => "the same system setting",
            _ => rest,
        };
    }

    // ---- catalog-wide conflicts and overlaps --------------------------------------------------------------------------

    private List<PairFinding> ComputeFindings()
    {
        var list = new List<PairFinding>();
        var seen = new HashSet<(string, string)>();
        bool Add(Tweak a, Tweak b, RelationKind k, string text, string source)
        {
            var key = string.CompareOrdinal(a.Id, b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
            if (!seen.Add(key)) return false;
            list.Add(new(a, b, k, text, source));
            return true;
        }
        // 1. Exact: the same setting (registry value, service, power setting, command) — only among tweaks sharing a target.
        foreach (var (target, ids) in _byTarget.Where(kv => kv.Value.Count > 1 && kv.Key != "power:plan"))
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var a = _byId[ids[i]].Tweak; var b = _byId[ids[j]].Tweak;
                    switch (Compatibility.Between(a, b))
                    {
                        case Compatibility.Overlap.Conflict:
                            Add(a, b, RelationKind.Conflict, $"Both change {Describe(target)} to different values. Only one can be active: applying one replaces the other, and Undo of the first would then restore the wrong value.", "same setting");
                            break;
                        case Compatibility.Overlap.Duplicate:
                            Add(a, b, RelationKind.Duplicate, $"Both write the same value to {Describe(target)}. Applying both does nothing extra, and undoing one undoes that part for the other.", "same setting");
                            break;
                    }
                }
        // 2. Hand-written relations from the catalog review (TweakAudit) that name another tweak.
        foreach (var p in Profiles)
            foreach (var issue in TweakAudit.IssuesFor(p.Tweak).Where(i => i.Other is not null))
                if (_byId.GetValueOrDefault(issue.Other!) is { } o && o.Tweak.Id != p.Tweak.Id)
                    Add(p.Tweak, o.Tweak, issue.Kind switch { AuditKind.Duplicate => RelationKind.Duplicate, AuditKind.Conflict when issue.Text.Contains("overlap", StringComparison.OrdinalIgnoreCase) => RelationKind.OverlappingEffect, AuditKind.Conflict => RelationKind.TradeOff, _ => RelationKind.SameArea }, issue.Text, "catalog review");
        // 3. Same subsystem, different settings (e.g. two values under one key): related, not contradictory.
        foreach (var (target, ids) in _byTarget.Where(kv => kv.Value.Count is > 1 and <= 12 && kv.Key.StartsWith("reg:", StringComparison.Ordinal)))
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    var a = _byId[ids[i]].Tweak; var b = _byId[ids[j]].Tweak;
                    if (a.Category == b.Category && a.Group == b.Group) continue; // neighbours in the list already
                    Add(a, b, RelationKind.SameArea, $"Both change {Describe(target)}. They don't overwrite each other, but they act on the same feature.", "same area");
                }
        // 4. Trade-offs: a tweak that raises power/heat next to a power-plan tweak (power-plan values are per plan).
        var planSwitchers = Profiles.Where(p => p.Tweak.Name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase) || p.Tweak.Name.Contains("power plan", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in Profiles.Where(p => p.Targets.Contains("power:plan")))
            foreach (var b in planSwitchers.Where(b => b != a))
                Add(a.Tweak, b.Tweak, RelationKind.TradeOff, "Power-plan setting: it changes only the plan that is active when you apply it. Switching plans afterwards doesn't carry it over; apply the plan first.", "power plan");
        // 5. Overlapping effects across categories: different settings that act on the same feature, found through a rare,
        //    distinctive word in both names (e.g. a Superfetch service and a Superfetch registry tweak).
        int total = Profiles.Count;
        var nameTerms = Profiles.ToDictionary(p => p.Tweak.Id, p => p.Terms.Where(kv => kv.Value == 3).Select(kv => kv.Key).ToHashSet());
        foreach (var term in DocFreq.Where(kv => kv.Value is >= 2 and <= 6 && kv.Key.Length >= 5 && !Generic.Contains(kv.Key)).Select(kv => kv.Key))
        {
            var holders = Profiles.Where(p => nameTerms[p.Tweak.Id].Contains(term)).ToList();
            if (holders.Count is < 2 or > 6) continue;
            for (int i = 0; i < holders.Count; i++)
                for (int j = i + 1; j < holders.Count; j++)
                {
                    var a = holders[i].Tweak; var b = holders[j].Tweak;
                    if (a.Category == b.Category) continue;
                    if (!holders[i].Purposes.Intersect(holders[j].Purposes).Any()) continue; // same word, different worlds (PCIe Express vs Photoshop Express)
                    var word = Regex.Match(a.Name.ToLowerInvariant(), $@"\b{Regex.Escape(term)}[a-z\-]*").Value is { Length: > 0 } wd ? wd : term;
                    Add(a, b, RelationKind.OverlappingEffect, $"Different settings for the same feature ({word}) in {CategoryInfo.DisplayName(a.Category)} and {CategoryInfo.DisplayName(b.Category)}. Applying both is fine but redundant in part; undoing one leaves the other in effect.", "same feature");
                }
        }
        return list;
    }
}

/// <summary>Game names recognised in tweak text and search queries (catalog games plus common short names).</summary>
public static class Games
{
    public static readonly IReadOnlyDictionary<string, string> Names = BuildNames();

    private static Dictionary<string, string> BuildNames()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in MoneyTweaks.Core.Games.GameCatalog.All)
        {
            var name = g.Name.ToLowerInvariant();
            if (name.Length >= 3) d[name] = g.Name;
        }
        foreach (var (k, v) in new[] { ("fortnite", "Fortnite"), ("valorant", "VALORANT"), ("cs2", "Counter-Strike 2"), ("counter-strike", "Counter-Strike 2"), ("apex", "Apex Legends"),
                     ("rocket league", "Rocket League"), ("gta", "GTA V"), ("roblox", "Roblox"), ("marvel rivals", "Marvel Rivals"), ("minecraft", "Minecraft"), ("geometry dash", "Geometry Dash") })
            d.TryAdd(k, v);
        return d;
    }
}
