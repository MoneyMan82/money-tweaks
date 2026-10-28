using System.Text.RegularExpressions;

namespace MoneyTweaks.Core.Pocket;

/// <summary>One widget on a dashboard page. Type and size come from fixed lists; a button names an allowlisted action.</summary>
public sealed class PocketWidget
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>"s" one cell, "m" a full-width row, "l" full width and double height.</summary>
    public string Size { get; set; } = "s";
    /// <summary>Button widgets: the action id (still checked against the allowlist when pressed).</summary>
    public string? Action { get; set; }
    /// <summary>Button widgets: optional own caption.</summary>
    public string? Label { get; set; }
    /// <summary>Reading cards: warn at or above this value (°C, %, GB or FPS, as the card shows).</summary>
    public double? Warn { get; set; }
}

/// <summary>A dashboard page. With <see cref="Game"/> set it opens automatically while that game runs.</summary>
public sealed class PocketPage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Game Library key of the game this page is for, or null.</summary>
    public string? Game { get; set; }
    public string? GameName { get; set; }
    public List<PocketWidget> Widgets { get; set; } = [];
}

/// <summary>All dashboard pages, shared by every paired device. Version goes up on every save (conflict detection).</summary>
public sealed class PocketDeck
{
    public int Version { get; set; }
    public List<PocketPage> Pages { get; set; } = [];
}

/// <summary>
/// Validation, defaults and versioned storage of the dashboards. Everything a phone sends is checked against fixed lists
/// and limits and rebuilt from scratch, so nothing unexpected is ever stored.
/// </summary>
public sealed class PocketDeckStore(PocketSettings settings)
{
    public const int MaxPages = 12, MaxWidgets = 40;
    public static readonly string[] Types = ["fps", "frametime", "cpu", "gpu", "cputemp", "gputemp", "ram", "vram", "alerts", "game", "button"];
    public static readonly string[] Sizes = ["s", "m", "l"];
    private static readonly Regex IdRx = new("^[A-Za-z0-9_-]{1,24}$"), ActionRx = new("^[a-z0-9._-]{1,80}$"), GameRx = new("^[A-Za-z0-9:._ -]{1,120}$");
    private readonly object _gate = new();

    /// <summary>Raised after a save (any thread).</summary>
    public event Action? Changed;

    public PocketDeck Get()
    {
        lock (_gate)
        {
            if (settings.Deck is not { Pages.Count: > 0 }) settings.Deck = Default();
            return settings.Deck;
        }
    }

    /// <summary>Saves when <paramref name="baseVersion"/> is the current version; otherwise returns the current deck as a conflict.</summary>
    public (bool ok, PocketDeck deck, string? error) TrySave(int baseVersion, PocketDeck incoming)
    {
        lock (_gate)
        {
            var current = Get();
            if (baseVersion != current.Version) return (false, current, "Changed on another device; here is the current layout.");
            var clean = Sanitize(incoming, out var error);
            if (clean is null) return (false, current, error);
            clean.Version = current.Version + 1;
            settings.Deck = clean;
        }
        Changed?.Invoke();
        return (true, settings.Deck!, null);
    }

    public void Reset()
    {
        lock (_gate) { var v = Get().Version; settings.Deck = Default(); settings.Deck.Version = v + 1; }
        Changed?.Invoke();
    }

    /// <summary>A clean copy that only contains known types, sizes and safe text, or null with the reason.</summary>
    public static PocketDeck? Sanitize(PocketDeck d, out string? error)
    {
        error = null;
        if (d.Pages is not { Count: > 0 and <= MaxPages }) { error = $"A layout needs 1 to {MaxPages} pages."; return null; }
        var deck = new PocketDeck();
        var pageIds = new HashSet<string>();
        foreach (var p in d.Pages)
        {
            if (p is null || !IdRx.IsMatch(p.Id ?? "") || !pageIds.Add(p.Id!)) { error = "A page has a missing or repeated id."; return null; }
            var name = Text(p.Name, 32);
            if (name.Length == 0) { error = "Every page needs a name."; return null; }
            if ((p.Widgets?.Count ?? 0) > MaxWidgets) { error = $"A page can have up to {MaxWidgets} widgets."; return null; }
            var page = new PocketPage { Id = p.Id!, Name = name, Game = p.Game is { } g && GameRx.IsMatch(g) ? g : null };
            page.GameName = page.Game is null ? null : Text(p.GameName, 60);
            var widgetIds = new HashSet<string>();
            foreach (var w in p.Widgets ?? [])
            {
                if (w is null || !IdRx.IsMatch(w.Id ?? "") || !widgetIds.Add(w.Id!) || !Types.Contains(w.Type) || !Sizes.Contains(w.Size))
                { error = "A widget has an unknown type, size or id."; return null; }
                if (w.Type == "button" && (w.Action is null || !ActionRx.IsMatch(w.Action))) { error = "A button has no valid action."; return null; }
                page.Widgets.Add(new PocketWidget
                {
                    Id = w.Id!, Type = w.Type, Size = w.Size,
                    Action = w.Type == "button" ? w.Action : null,
                    Label = w.Type == "button" && Text(w.Label, 24) is { Length: > 0 } l ? l : null,
                    Warn = w.Type is "cpu" or "gpu" or "cputemp" or "gputemp" or "ram" or "vram" or "fps" && w.Warn is { } v && double.IsFinite(v) && v is >= 0 and <= 1000 ? Math.Round(v, 1) : null,
                });
            }
            deck.Pages.Add(page);
        }
        return deck;
    }

    private static string Text(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>The starting layout: a gaming page and a system page, every widget backed by real readings or actions.</summary>
    public static PocketDeck Default() => new()
    {
        Version = 1,
        Pages =
        [
            new PocketPage
            {
                Id = "gaming", Name = "Gaming",
                Widgets =
                [
                    new() { Id = "w1", Type = "fps", Size = "m" },
                    new() { Id = "w2", Type = "frametime", Size = "m" },
                    new() { Id = "w3", Type = "gputemp", Size = "s", Warn = 85 },
                    new() { Id = "w4", Type = "cputemp", Size = "s", Warn = 90 },
                    new() { Id = "w5", Type = "gpu", Size = "s" },
                    new() { Id = "w6", Type = "cpu", Size = "s" },
                    new() { Id = "w7", Type = "button", Size = "s", Action = "overlay.visible" },
                    new() { Id = "w8", Type = "game", Size = "s" },
                    new() { Id = "w9", Type = "alerts", Size = "m" },
                ],
            },
            new PocketPage
            {
                Id = "system", Name = "System",
                Widgets =
                [
                    new() { Id = "w1", Type = "cpu", Size = "m" },
                    new() { Id = "w2", Type = "gpu", Size = "m" },
                    new() { Id = "w3", Type = "cputemp", Size = "s", Warn = 90 },
                    new() { Id = "w4", Type = "gputemp", Size = "s", Warn = 85 },
                    new() { Id = "w5", Type = "ram", Size = "s" },
                    new() { Id = "w6", Type = "vram", Size = "s" },
                    new() { Id = "w7", Type = "alerts", Size = "m" },
                ],
            },
        ],
    };
}
