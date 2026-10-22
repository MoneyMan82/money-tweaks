using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Discovery;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Views;

/// <summary>
/// The whole tweak library by purpose: lanes (what it's for) holding category › group tiles, sized by how many tweaks they
/// hold, coloured by category, linked to each other by conflicts and overlaps. Clicking a tile or lane lists its tweaks;
/// every tweak and category opens its real page. Below it, every conflict, duplicate and overlap in the library with both
/// settings linked. Aggregated and paged, so it stays fast and readable with thousands of entries.
/// </summary>
public sealed class TweakMapView : UserControl, IPageLifecycle
{
    /// <summary>Scale checks render the map over a synthetic library.</summary>
    public static Func<TweakKnowledge>? KnowledgeOverride { get; set; }

    private sealed record Tile(string Key, string Purpose, TweakCategory Category, string Group, List<TweakProfile> Items);

    private TweakKnowledge? _k;
    private HashSet<string> _applied = [];
    private string _lens = "all", _kinds = "all";
    private bool _onlyActive;
    private string? _selected;
    private readonly StackPanel _summary = new(), _map = new(), _detail = new(), _conflicts = new();
    private readonly Dictionary<string, Border> _tileViews = [];
    private Dictionary<string, HashSet<string>> _links = [];
    private List<Tile> _tiles = [];

    // One distinct colour per category (red is kept for conflicts, so no category uses it).
    private static readonly Dictionary<TweakCategory, Color> CategoryColors = new()
    {
        [TweakCategory.General] = Color.FromRgb(0x4F, 0x8C, 0xFF), [TweakCategory.Debloating] = Color.FromRgb(0x9B, 0x7B, 0xFF), [TweakCategory.Networking] = Color.FromRgb(0xE3, 0xB3, 0x41),
        [TweakCategory.Advanced] = Color.FromRgb(0x3F, 0xB9, 0x7A), [TweakCategory.Services] = Color.FromRgb(0x22, 0xB8, 0xCF), [TweakCategory.AppOptimizer] = Color.FromRgb(0xF0, 0x8C, 0x4A),
        [TweakCategory.AppRemoval] = Color.FromRgb(0xD9, 0x7A, 0xB0), [TweakCategory.GameProfiles] = Color.FromRgb(0x8B, 0xC3, 0x4A), [TweakCategory.Bios] = Color.FromRgb(0xA0, 0xA8, 0xB8),
    };
    private static Brush CatBrush(TweakCategory c) => new SolidColorBrush(CategoryColors.GetValueOrDefault(c, Colors.Gray));
    private static Brush CatSoft(TweakCategory c) { var k = CategoryColors.GetValueOrDefault(c, Colors.Gray); return new SolidColorBrush(Color.FromArgb(0x26, k.R, k.G, k.B)); }

    public TweakMapView()
    {
        var (root, body) = Ui.Page("Tweak Map",
            "The whole tweak library by what it's for, how settings connect, and every conflict or overlap. Click a tile to see its tweaks; every tweak and category opens its real page.",
            icon: "\uE81E");
        body.Children.Add(_summary);
        body.Children.Add(Ui.Card(_map));
        body.Children.Add(_detail);
        body.Children.Add(Ui.Card(_conflicts));
        Content = root;
        _map.Children.Add(Ui.Loading("Mapping the library…"));
    }

    public async void OnShown()
    {
        if (_k is not null) return;
        var (k, applied) = await Task.Run(() =>
        {
            var know = KnowledgeOverride?.Invoke() ?? TweakKnowledge.Current;
            return (know, know.Profiles.Where(p => BackupStore.Exists(p.Tweak.Id)).Select(p => p.Tweak.Id).ToHashSet());
        });
        _k = k; _applied = applied;
        BuildTiles();
        RenderSummary(); RenderMap(); RenderConflicts();
    }

    public void OnHidden() { }

    // ---- model ----------------------------------------------------------------------------------------------------------

    private bool Visible(TweakProfile p) => _lens switch
    {
        "recommended" => p.Tweak.Recommended,
        "applied" => _applied.Contains(p.Tweak.Id),
        "related" => _k!.FindingsFor(p.Tweak.Id).Count > 0,
        _ => true,
    };

    private void BuildTiles()
    {
        _tiles = _k!.Profiles.GroupBy(p => (p.Home, p.Tweak.Category, Group: p.Tweak.Group ?? CategoryInfo.DisplayName(p.Tweak.Category)))
            .Select(g => new Tile($"{g.Key.Home}|{g.Key.Category}|{g.Key.Group}", g.Key.Home, g.Key.Category, g.Key.Group, g.ToList())).ToList();
        var tileOf = _tiles.SelectMany(t => t.Items.Select(i => (i.Tweak.Id, t.Key))).ToDictionary(x => x.Id, x => x.Key);
        _links = _tiles.ToDictionary(t => t.Key, _ => new HashSet<string>());
        foreach (var f in _k.Findings)
        {
            var a = tileOf[f.A.Id]; var b = tileOf[f.B.Id];
            if (a == b) continue;
            _links[a].Add(b); _links[b].Add(a);
        }
    }

    // ---- summary ---------------------------------------------------------------------------------------------------------

    private void RenderSummary()
    {
        _summary.Children.Clear();
        var k = _k!;
        var grid = Ui.Grid(4);
        void Stat(string big, string small, string tone)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = big, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Ui.Brush(tone) });
            sp.Children.Add(Ui.Muted(small, 12));
            grid.Children.Add(Ui.Card(sp, new Thickness(0, 0, 10, 10)));
        }
        Stat(k.Profiles.Count.ToString(), $"tweaks in {k.Profiles.Select(p => p.Tweak.Category).Distinct().Count()} categories, {_tiles.Count} groups", "Text");
        Stat(_applied.Count.ToString(), "applied by Money Tweaks", "Accent");
        int conflicts = k.Findings.Count(f => f.Kind == RelationKind.Conflict);
        Stat(conflicts.ToString(), "pairs that can't both be active", conflicts > 0 ? "Danger" : "Success");
        Stat(k.Findings.Count(f => f.Kind != RelationKind.Conflict).ToString(), "overlaps, duplicates and order notes", "Gold");
        _summary.Children.Add(grid);
        int activeBad = k.Findings.Count(f => f.Kind is RelationKind.Conflict or RelationKind.Duplicate && _applied.Contains(f.A.Id) && _applied.Contains(f.B.Id));
        if (activeBad > 0)
            _summary.Children.Add(new Border
            {
                Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Danger"), Margin = new Thickness(0, 0, 0, 14),
                Child = Ui.Muted($"{activeBad} conflicting or duplicate pair(s) are both applied right now. They're at the top of Conflicts & overlaps below.", 12.5),
            });
    }

    // ---- map ---------------------------------------------------------------------------------------------------------------

    private void RenderMap()
    {
        _map.Children.Clear();
        _tileViews.Clear();
        _map.Children.Add(Ui.Section("Library map", "Lanes are purposes; tiles are categories › groups, sized by how many tweaks they hold. Hover a tile to light up the groups it connects to (conflicts, overlaps, same areas)."));
        var lens = new SegmentedPicker([("Everything", "all", null), ("Recommended", "recommended", null), ("Applied by Money Tweaks", "applied", null), ("Has conflicts or overlaps", "related", null)], _lens);
        lens.Changed += v => { _lens = v; RenderMap(); };
        _map.Children.Add(lens);

        // Legend: category colours, each a link to its page.
        var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        foreach (var c in _k!.Profiles.Select(p => p.Tweak.Category).Distinct().OrderBy(c => c))
        {
            var chip = new Border { CornerRadius = new CornerRadius(0), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6), Background = CatSoft(c), Cursor = Cursors.Hand, ToolTip = $"Open {CategoryInfo.DisplayName(c)}" };
            chip.Child = new TextBlock { Text = $"{CategoryInfo.DisplayName(c)} · {_k.Profiles.Count(p => p.Tweak.Category == c)}", FontSize = 11.5, Foreground = CatBrush(c), FontWeight = FontWeights.SemiBold };
            var cat = c;
            chip.MouseLeftButtonUp += (_, _) => MainWindow.Current?.Navigate("cat:" + cat);
            legend.Children.Add(chip);
        }
        _map.Children.Add(legend);

        // Two independent columns (masonry): each lane goes under the shorter column, so tall lanes don't leave gaps.
        var lanes = new Grid();
        lanes.ColumnDefinitions.Add(new ColumnDefinition());
        lanes.ColumnDefinitions.Add(new ColumnDefinition());
        var cols = new[] { new StackPanel(), new StackPanel() };
        Grid.SetColumn(cols[1], 1);
        lanes.Children.Add(cols[0]); lanes.Children.Add(cols[1]);
        var heights = new double[2];
        foreach (var purpose in TweakKnowledge.Purposes)
        {
            var tiles = _tiles.Where(t => t.Purpose == purpose.Id).Select(t => (t, n: t.Items.Count(Visible))).Where(x => x.n > 0).OrderByDescending(x => x.n).ToList();
            var all = _k.InPurpose(purpose.Id).Where(id => Visible(_k.Profile(id)!)).ToList();
            if (tiles.Count == 0 && all.Count == 0) continue;
            var lane = new StackPanel();
            var head = new DockPanel { Cursor = Cursors.Hand, ToolTip = "List every tweak that serves this purpose" };
            var icon = new TextBlock { Text = purpose.Icon, Style = Ui.Res<Style>("Icon"), FontSize = 16, Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); head.Children.Add(icon);
            var count = Ui.Muted($"{tiles.Sum(x => x.n)} here · {all.Count} serve it", 11.5); DockPanel.SetDock(count, Dock.Right); count.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(count);
            head.Children.Add(Ui.Text(purpose.Label, 14, FontWeights.SemiBold));
            var pid = purpose.Id;
            head.MouseLeftButtonUp += (_, _) => ShowPurpose(pid);
            lane.Children.Add(head);
            lane.Children.Add(Ui.Muted(purpose.Blurb, 11.5));
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (t, n) in tiles) wrap.Children.Add(TileView(t, n));
            lane.Children.Add(wrap);
            int col = heights[0] <= heights[1] ? 0 : 1;
            cols[col].Children.Add(Ui.Card(lane, new Thickness(0, 0, 10, 10)));
            heights[col] += 90 + Math.Ceiling(tiles.Count / 3.0) * 52;
        }
        _map.Children.Add(lanes);
        if (_selected is not null) Highlight(_selected, sticky: true);
    }

    private Border TileView(Tile t, int visible)
    {
        int applied = t.Items.Count(i => _applied.Contains(i.Tweak.Id));
        int findings = t.Items.Sum(i => _k!.FindingsFor(i.Tweak.Id).Count);
        int conflicts = t.Items.Sum(i => _k!.FindingsFor(i.Tweak.Id).Count(f => f.Kind == RelationKind.Conflict));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = t.Group, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new WrapPanel();
        meta.Children.Add(new TextBlock { Text = $"{visible}", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = CatBrush(t.Category), Margin = new Thickness(0, 0, 6, 0) });
        if (t.Group != CategoryInfo.DisplayName(t.Category)) meta.Children.Add(new TextBlock { Text = CategoryInfo.DisplayName(t.Category), FontSize = 10.5, Foreground = Ui.Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        if (applied > 0) meta.Children.Add(new TextBlock { Text = $"● {applied}", FontSize = 10.5, Foreground = Ui.Brush("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = $"{applied} applied by Money Tweaks" });
        if (conflicts > 0) meta.Children.Add(new TextBlock { Text = $"⛔ {conflicts}", FontSize = 10.5, Foreground = Ui.Brush("Danger"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "conflicts" });
        else if (findings > 0) meta.Children.Add(new TextBlock { Text = $"⧉ {findings}", FontSize = 10.5, Foreground = Ui.Brush("Gold"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "overlaps / notes" });
        sp.Children.Add(meta);
        double width = 118 + Math.Min(visible, 60) * 2.6;
        var b = new Border
        {
            Width = width, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(0), Cursor = Cursors.Hand,
            Background = CatSoft(t.Category), BorderBrush = CatBrush(t.Category), BorderThickness = new Thickness(3, 0, 0, 0), Child = sp,
            ToolTip = $"{CategoryInfo.DisplayName(t.Category)} › {t.Group}: {t.Items.Count} tweak(s)" + (_links[t.Key].Count > 0 ? $"\nConnected to {_links[t.Key].Count} other group(s)" : ""),
        };
        b.MouseEnter += (_, _) => Highlight(t.Key, sticky: false);
        b.MouseLeave += (_, _) => { if (_selected is { } s) Highlight(s, sticky: true); else ClearHighlight(); };
        b.MouseLeftButtonUp += (_, _) => { _selected = t.Key; Highlight(t.Key, sticky: true); ShowTile(t); };
        _tileViews[t.Key] = b;
        return b;
    }

    private void Highlight(string key, bool sticky)
    {
        var linked = _links.GetValueOrDefault(key) ?? [];
        foreach (var (k, v) in _tileViews)
        {
            bool me = k == key, link = linked.Contains(k);
            v.Opacity = me || link ? 1 : 0.35;
            v.BorderThickness = me ? new Thickness(3, 2, 2, 2) : link ? new Thickness(3, 1, 1, 1) : new Thickness(3, 0, 0, 0);
            if (me) v.BorderBrush = Ui.Brush("Text");
            else if (link) v.BorderBrush = Ui.Brush("Gold");
            else v.BorderBrush = CatBrush(_tiles.First(t => t.Key == k).Category);
        }
    }

    private void ClearHighlight()
    {
        foreach (var (k, v) in _tileViews)
        {
            v.Opacity = 1; v.BorderThickness = new Thickness(3, 0, 0, 0);
            v.BorderBrush = CatBrush(_tiles.First(t => t.Key == k).Category);
        }
    }

    // ---- detail ------------------------------------------------------------------------------------------------------------

    private void ShowTile(Tile t)
    {
        var items = t.Items.Where(Visible).ToList();
        var connected = t.Items.SelectMany(i => _k!.FindingsFor(i.Tweak.Id)).Distinct().ToList();
        ShowList($"{CategoryInfo.DisplayName(t.Category)} › {t.Group}", $"{TweakKnowledge.PurposeOf(t.Purpose).Label} · {items.Count} tweak(s)" + (connected.Count > 0 ? $" · {connected.Count} connection(s) to other groups" : ""),
            items, t.Category, connected);
    }

    private void ShowPurpose(string purpose)
    {
        var items = _k!.InPurpose(purpose).Select(id => _k.Profile(id)!).Where(Visible).OrderBy(p => p.Home != purpose).ThenBy(p => p.Tweak.Category).ThenBy(p => p.Tweak.Name).ToList();
        _selected = null; ClearHighlight();
        var p = TweakKnowledge.PurposeOf(purpose);
        ShowList(p.Label, $"{items.Count} tweak(s) across {items.Select(i => i.Tweak.Category).Distinct().Count()} categories serve this purpose. {p.Blurb}", items, null, []);
    }

    private void ShowList(string title, string sub, List<TweakProfile> items, TweakCategory? category, List<PairFinding> connected)
    {
        _detail.Children.Clear();
        var sp = new StackPanel();
        var head = new DockPanel();
        var close = Ui.Btn("Close", (_, _) => { _detail.Children.Clear(); _selected = null; ClearHighlight(); }, "GhostButton", new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        if (category is { } c)
        {
            var open = Ui.Btn($"Open {CategoryInfo.DisplayName(c)} page", (_, _) => MainWindow.Current?.Navigate("cat:" + c), "BaseButton", new Thickness(8, 0, 0, 0));
            DockPanel.SetDock(open, Dock.Right); head.Children.Add(open);
        }
        head.Children.Add(Ui.Text(title, 15, FontWeights.SemiBold));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted(sub, 12));
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(list);
        int shown = 0;
        Button? more = null;
        void Page()
        {
            foreach (var p in items.Skip(shown).Take(60)) list.Children.Add(TweakRow(p));
            shown = Math.Min(items.Count, shown + 60);
            if (more is not null) { more.Visibility = shown < items.Count ? Visibility.Visible : Visibility.Collapsed; more.Content = $"Show more ({items.Count - shown} left)"; }
        }
        more = Ui.Btn("", (_, _) => Page(), "GhostButton", new Thickness(0, 6, 0, 0));
        Page();
        sp.Children.Add(more);
        if (connected.Count > 0)
        {
            sp.Children.Add(Ui.Text("Connections", 13, FontWeights.SemiBold));
            foreach (var f in connected.Take(12)) sp.Children.Add(FindingRow(f));
            if (connected.Count > 12) sp.Children.Add(Ui.Muted($"…and {connected.Count - 12} more in Conflicts & overlaps below.", 11.5));
        }
        _detail.Children.Add(Ui.Card(sp));
        Ui.ScrollTo(_detail);
    }

    private UIElement TweakRow(TweakProfile p)
    {
        var t = p.Tweak;
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = t.Description };
        var tags = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        if (_applied.Contains(t.Id)) tags.Children.Add(Ui.Tag("APPLIED", "Accent"));
        if (t.Recommended) tags.Children.Add(Ui.Tag("RECOMMENDED", "Violet"));
        if (Compatibility.TierOf(t) == TweakTier.Experimental) tags.Children.Add(Ui.Tag("EXPERIMENTAL", "Gold"));
        if (TweakAudit.DisabledReason(t) is not null) tags.Children.Add(Ui.Tag("DISABLED", "Danger"));
        if (p.Effects.Contains(SideEffect.MorePowerAndHeat)) tags.Children.Add(Ui.Tag("MORE HEAT", "Gold"));
        int f = _k!.FindingsFor(t.Id).Count;
        if (f > 0) tags.Children.Add(Ui.Tag($"{f} RELATION{(f == 1 ? "" : "S")}", "Gold"));
        DockPanel.SetDock(tags, Dock.Right);
        row.Children.Add(tags);
        var name = new StackPanel();
        name.Children.Add(new TextBlock { Text = t.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        name.Children.Add(new TextBlock { Text = $"{CategoryInfo.DisplayName(t.Category)}{(t.Group is { } g ? " › " + g : "")} · {string.Join(", ", p.Purposes.Take(3).Select(x => TweakKnowledge.PurposeOf(x).Label.ToLowerInvariant()))}", FontSize = 11, Foreground = Ui.Brush("Subtle") });
        row.Children.Add(name);
        row.MouseLeftButtonUp += (_, _) => MainWindow.Current?.OpenTweak(t.Id);
        row.MouseEnter += (_, _) => row.Background = Ui.Brush("CardHover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return new Border { Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(0), Child = row };
    }

    // ---- conflicts & overlaps ----------------------------------------------------------------------------------------------

    private void RenderConflicts()
    {
        _conflicts.Children.Clear();
        _conflicts.Children.Add(Ui.Section("Conflicts & overlaps", "Settings that contradict each other, duplicate each other, act on the same feature, or must be applied in a certain order. Click either name to open it."));
        var picker = new SegmentedPicker([("All", "all", null), ("Can't coexist", "conflict", null), ("Order matters", "tradeoff", null), ("Duplicates", "duplicate", null), ("Same feature", "overlap", null), ("Same area", "area", null)], _kinds);
        picker.Changed += v => { _kinds = v; RenderConflicts(); };
        _conflicts.Children.Add(picker);
        var only = new CheckBox { Content = "Only pairs where both are applied by Money Tweaks", IsChecked = _onlyActive, Margin = new Thickness(0, 8, 0, 6) };
        only.Click += (_, _) => { _onlyActive = only.IsChecked == true; RenderConflicts(); };
        _conflicts.Children.Add(only);

        bool Active(PairFinding f) => _applied.Contains(f.A.Id) && _applied.Contains(f.B.Id);
        var list = _k!.Findings.Where(f => _kinds switch
        {
            "conflict" => f.Kind == RelationKind.Conflict, "tradeoff" => f.Kind == RelationKind.TradeOff, "duplicate" => f.Kind == RelationKind.Duplicate,
            "overlap" => f.Kind == RelationKind.OverlappingEffect, "area" => f.Kind == RelationKind.SameArea, _ => true,
        }).Where(f => !_onlyActive || Active(f))
          .OrderByDescending(Active).ThenBy(f => f.Kind).ThenBy(f => f.A.Name).ToList();
        if (list.Count == 0) { _conflicts.Children.Add(Ui.Muted(_onlyActive ? "None: no applied pair conflicts or overlaps." : "None of this kind.", 12.5)); return; }
        var host = new StackPanel();
        _conflicts.Children.Add(host);
        int shown = 0;
        Button? more = null;
        void Page()
        {
            foreach (var f in list.Skip(shown).Take(50)) host.Children.Add(FindingRow(f));
            shown = Math.Min(list.Count, shown + 50);
            more!.Visibility = shown < list.Count ? Visibility.Visible : Visibility.Collapsed;
            more.Content = $"Show more ({list.Count - shown} left)";
        }
        more = Ui.Btn("", (_, _) => Page(), "GhostButton", new Thickness(0, 6, 0, 0));
        _conflicts.Children.Add(more);
        Page();
    }

    private UIElement FindingRow(PairFinding f)
    {
        bool active = _applied.Contains(f.A.Id) && _applied.Contains(f.B.Id);
        var (label, tone) = f.Kind switch
        {
            RelationKind.Conflict => ("CAN'T COEXIST", "Danger"), RelationKind.Duplicate => ("DUPLICATE", "Gold"), RelationKind.TradeOff => ("ORDER MATTERS", "Gold"),
            RelationKind.OverlappingEffect => ("SAME FEATURE", "Gold"), RelationKind.SameArea => ("SAME AREA", "Violet"), _ => ("RELATED", "Accent"),
        };
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var head = new WrapPanel();
        if (active) head.Children.Add(Ui.Tag("BOTH APPLIED", "Danger"));
        head.Children.Add(Ui.Tag(label, tone));
        head.Children.Add(Link(f.A));
        head.Children.Add(new TextBlock { Text = "  ⇄  ", Foreground = Ui.Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(Link(f.B));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted(f.Text, 11.5));
        return sp;
    }

    private static TextBlock Link(Tweak t)
    {
        var tb = new TextBlock
        {
            Text = t.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Accent"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{CategoryInfo.DisplayName(t.Category)}{(t.Group is { } g ? " › " + g : "")}: open it",
        };
        tb.MouseEnter += (_, _) => tb.TextDecorations = TextDecorations.Underline;
        tb.MouseLeave += (_, _) => tb.TextDecorations = null;
        tb.MouseLeftButtonUp += (_, _) => MainWindow.Current?.OpenTweak(t.Id);
        return tb;
    }
}
