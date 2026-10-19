using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Advisor;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

/// <summary>
/// Bottleneck &amp; Upgrade Advisor: "What are the three smartest ways to get closer to my FPS goal on my PC without wasting
/// money?" Setup → this PC → measure (while playing, or the built-in benchmark) → diagnosis → three ranked
/// recommendations → upgrade planner and goal simulator. Measured and projected numbers are always labelled.
/// </summary>
public sealed partial class AdvisorView : UserControl, IPageLifecycle
{
    private readonly AdvisorStore _store = AdvisorStore.Load();
    private readonly StackPanel _setup = new(), _pc = new(), _measure = new(), _results = new();
    private HardwareFacts? _hw;
    private Capture? _capture;
    private Diagnosis? _dx;
    private AdvisorResult? _res;
    private List<(LibraryGame g, GameInstall? i, int? pid)> _games = [];
    private CancellationTokenSource? _cts;
    private readonly string _country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
    private bool _loaded;

    public AdvisorView()
    {
        var (root, body) = Ui.Page("Upgrade Advisor",
            "What are the three smartest ways to get closer to your FPS goal on this PC without wasting money? Money Tweaks measures what's actually holding your game back, then ranks free fixes, cheap improvements and upgrades by benefit per euro.",
            icon: "\uE9D9");
        body.Children.Add(Ui.Card(_setup));
        body.Children.Add(Ui.Card(_pc));
        body.Children.Add(Ui.Card(_measure));
        body.Children.Add(_results);
        Content = root;
        _pc.Children.Add(Ui.Loading("Reading this PC's hardware…"));
    }

    public async void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshGamesAsync();
        RenderSetup();
        RenderMeasure();
        var inv = TuningDetector.Cached() ?? await TuningDetector.ScanAsync();
        _hw = await Task.Run(() => HardwareFactsDetector.Detect(inv, _store.Goal.GameInstallDir));
        RenderPc();
    }

    public void OnHidden() { }

    private async Task RefreshGamesAsync()
    {
        _games = await Task.Run(() =>
        {
            var items = GameLibrary.Items(GameLibrary.Current);
            return GameRuntime.Statuses(items).Where(x => x.install.Install is not null || x.status == GameStatus.Running)
                .Select(x => (x.game, x.install.Install, x.status == GameStatus.Running ? x.match.Process?.Pid : (int?)null)).ToList();
        });
    }

    // ---- setup -------------------------------------------------------------------------------------------------------------

    private void RenderSetup()
    {
        _setup.Children.Clear();
        var g = _store.Goal;
        _setup.Children.Add(Ui.Section("Your goal", "Pick the game and what you want from it. Budget is optional; hardware over it is listed separately."));
        var grid = new WrapPanel();
        StackPanel Field(string label, UIElement e, double width = 200) { var sp = new StackPanel { Width = width, Margin = new Thickness(0, 0, 16, 10) }; sp.Children.Add(Ui.Muted(label, 11.5)); sp.Children.Add(e); return sp; }

        var game = new ComboBox { IsEditable = true, Text = g.Game };
        foreach (var x in _games) game.Items.Add(x.g.Def.Name + (x.pid is not null ? "  (running)" : ""));
        game.LostKeyboardFocus += (_, _) => SetGame(game.Text.Replace("  (running)", ""));
        game.SelectionChanged += (_, _) => { if (game.SelectedItem is string s) SetGame(s.Replace("  (running)", "")); };
        grid.Children.Add(Field("Game", game, 240));

        var res = new ComboBox();
        var resolutions = new[] { (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3440, 1440), (3840, 2160) }.ToList();
        var screen = ((int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        foreach (var (w, h) in resolutions) res.Items.Add($"{w}×{h}");
        res.SelectedIndex = Math.Max(0, resolutions.FindIndex(r => r.Item1 == g.Width && r.Item2 == g.Height));
        res.SelectionChanged += (_, _) => { (g.Width, g.Height) = resolutions[res.SelectedIndex]; Save(); };
        grid.Children.Add(Field($"Resolution (your display reports {screen.Item1}×{screen.Item2} scaled)", res, 260));

        var settings = new SegmentedPicker([("Low", "Low", null), ("Medium", "Medium", null), ("High", "High", null), ("Epic/Ultra", "Epic", null), ("Custom", "Custom", null)], g.Settings);
        settings.Changed += v => { g.Settings = v; Save(); };
        grid.Children.Add(Field("Graphics settings", settings, 420));
        TextBox Num(double? v, Action<double?> set, string hint)
        {
            var tb = new TextBox { Text = v?.ToString("0", CultureInfo.CurrentCulture) ?? "", Tag = hint };
            tb.LostKeyboardFocus += (_, _) => { set(double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) && d > 0 ? d : null); Save(); };
            return tb;
        }
        grid.Children.Add(Field("Current FPS (if you know it)", Num(g.CurrentFps, v => g.CurrentFps = v, "measured if possible"), 180));
        grid.Children.Add(Field("Target FPS", Num(g.TargetFps, v => g.TargetFps = v ?? 144, "e.g. 240"), 140));
        grid.Children.Add(Field("Budget (€, optional)", Num(g.BudgetEur, v => g.BudgetEur = v, "no limit"), 160));
        _setup.Children.Add(grid);
        _setup.Children.Add(Ui.Muted($"Prices are never fetched or invented: hardware cards link to reputable stores for your region ({new RegionInfo(_country).EnglishName}) and you enter the price you find; it's kept with its source and date.", 11.5));
    }

    private void SetGame(string name)
    {
        var g = _store.Goal;
        if (g.Game == name) return;
        g.Game = name.Trim();
        var m = _games.FirstOrDefault(x => x.g.Def.Name.Equals(g.Game, StringComparison.OrdinalIgnoreCase));
        g.GameKey = m.g?.Key; g.GameExe = m.g?.Def.ExeNames.FirstOrDefault(); g.GameInstallDir = m.i?.InstallDir;
        Save();
        if (_hw is not null && TuningDetector.Cached() is { } inv) _ = Task.Run(() => HardwareFactsDetector.Detect(inv, g.GameInstallDir)).ContinueWith(t => Dispatcher.Invoke(() => { _hw = t.Result; RenderPc(); }));
    }

    private void Save() { _store.Save(); if (_dx is not null) Analyze(); }

    // ---- this PC -------------------------------------------------------------------------------------------------------------

    private void RenderPc()
    {
        _pc.Children.Clear();
        if (_hw is not { } h) return;
        _pc.Children.Add(Ui.Section("This PC", "From Windows: used to check upgrade compatibility. Unknowns are listed, never guessed."));
        var grid = Ui.Grid(2);
        void Row(string k, string v) { var d = new DockPanel { Margin = new Thickness(0, 2, 12, 2) }; var l = Ui.Muted(k, 12); l.Width = 110; DockPanel.SetDock(l, Dock.Left); d.Children.Add(l); d.Children.Add(new TextBlock { Text = v, FontSize = 12, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap }); grid.Children.Add(d); }
        Row("CPU", $"{h.Cpu} ({h.Cores}C/{h.Threads}T)");
        Row("Platform", h.Socket is { } s ? $"{s}: {h.Platform}" : "socket not identified");
        Row("GPU", $"{h.Gpu}{(h.VramGb is { } v ? $" · {v:0} GB VRAM" : "")}{(h.GpuPowerLimitW is { } pl ? $" · power limit {pl:0} W" : "")}");
        Row("Motherboard", $"{h.Motherboard} · BIOS {h.Bios}");
        Row("RAM", $"{h.RamGb:0} GB {h.RamType ?? "(type unknown)"} · {h.RamModules} stick(s){(h.RamSlots is { } sl ? $" of {sl} slots" : "")} · " +
                   (h.DualChannel switch { true => "dual channel", false => "single channel", _ => "channels unknown" }) +
                   (h.RamConfiguredMts is { } c ? $" · {c} MT/s" + (h.XmpOff ? $" (rated {h.RamRatedMts}: XMP/EXPO off)" : "") : ""));
        Row("Storage", string.Join(" · ", h.Storage.Select(d => $"{d.Name} ({d.Media} {d.Bus}, {d.SizeGb:0} GB{(d.DriveLetters.Count > 0 ? " " + string.Join(",", d.DriveLetters.Select(x => x + ":")) : "")})")));
        if (h.GameDisk is { } gd) Row("Game is on", $"{gd.Name} ({gd.Media})");
        if (h.Laptop == true) Row("Type", "Laptop: CPU/GPU upgrades usually aren't possible");
        _pc.Children.Add(grid);
        _pc.Children.Add(Ui.Muted("Not readable from Windows: " + string.Join("; ", h.Missing) + ".", 11.5));
    }
}
