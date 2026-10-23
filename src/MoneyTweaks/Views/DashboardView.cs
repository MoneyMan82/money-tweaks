using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>
/// Classic live snapshot (CPU, GPU 3D, RAM, disk, network, temperatures) for the dashboard, overlay and other views. Fed by
/// the shared sensor hub, so they and the Hardware Monitor read the sensors once, not each on their own timer.
/// </summary>
public static class LiveStats
{
    private static readonly object Token = new();
    private static int _subscribers;
    private static bool _queued;
    public static event Action<HardwareSnapshot>? Sampled;
    public static HardwareSnapshot? Last => SensorHub.Shared.Last?.Snapshot;

    public static void Subscribe(Action<HardwareSnapshot> h)
    {
        Sampled += h;
        if (Last is { } l) h(l);
        if (_subscribers++ == 0)
        {
            SensorHub.Shared.Sampled += OnFrame;
            SensorHub.Shared.Acquire(Token, record: false);
        }
    }

    public static void Unsubscribe(Action<HardwareSnapshot> h)
    {
        Sampled -= h;
        if (--_subscribers <= 0)
        {
            _subscribers = 0;
            SensorHub.Shared.Sampled -= OnFrame;
            SensorHub.Shared.Release(Token);
        }
    }

    private static void OnFrame(SensorFrame f)
    {
        if (_queued) return;   // a slow UI never builds a backlog of samples
        _queued = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _queued = false; Sampled?.Invoke(f.Snapshot); });
    }

    public static void Fill(HardwareSnapshot s, StatTile net, StatTile disk, StatTile temp)
    {
        net.Max = 1024;
        net.Push(s.NetDownKBps, Fmt.Rate(s.NetDownKBps), $"↑ {Fmt.Rate(s.NetUpKBps)}");
        disk.Push(s.DiskPercent, s.DiskPercent is null ? "N/A" : $"{s.DiskPercent:0}%", "active time, all disks");
        temp.Push(s.CpuTempC, s.CpuTempC is null ? "N/A" : $"{s.CpuTempC:0} °C",
            s.CpuTempC is null ? "Firmware doesn't expose it; " + HardwareMonitor.CpuTempHint : HardwareMonitor.CpuTempSource(s.CpuTempC));
    }

    public static void FillRings(HardwareSnapshot s, RingGauge cpu, RingGauge gpu, RingGauge ram)
    {
        cpu.Set(s.CpuPercent, $"{s.CpuPercent:0}", "% load");
        gpu.Set(s.GpuPercent, s.GpuPercent is null ? "N/A" : $"{s.GpuPercent:0}", s.GpuPercent is null ? "no counters" : "% 3D");
        ram.Set(s.RamPercent, $"{s.RamPercent:0}", $"% · {s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB");
    }
}

public sealed partial class DashboardView : UserControl, IPageLifecycle
{
    private readonly RingGauge _score = new("Recommended active", 168, 14);
    private readonly RingGauge _cpu = new("CPU"), _gpu = new("GPU"), _ram = new("Memory");
    private readonly StatTile _net = new("Network", "\uE968"), _disk = new("Disk", "\uEDA2"), _temp = new("CPU temp", "\uE706");
    private readonly TextBlock _greeting = new() { Style = Ui.Res<Style>("H1"), FontSize = 32 };
    private readonly TextBlock _machine = Ui.Muted("Reading your hardware…", 13.5);
    private readonly WrapPanel _tags = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _scoreNote = Ui.Muted("Measuring…", 12);
    private readonly Button _recommend;
    private readonly StackPanel _specs = new();
    private readonly TextBlock _xmp = Ui.Muted("");
    private readonly StackPanel _cats = new();
    private bool _scoreLoaded;

    public DashboardView()
    {
        var (root, body) = Ui.Page("Dashboard", "");
        body.Children.Clear(); // the hero replaces the standard header

        // ── Hero ───────────────────────────────────────────────────────────────────────────────────────────────
        _recommend = Ui.Btn("★  Apply recommended", ApplyRecommended, "PrimaryButton");
        var heroLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroLeft.Children.Add(new TextBlock { Text = "SYSTEM OVERVIEW", Style = Ui.Res<Style>("Eyebrow") });
        heroLeft.Children.Add(_greeting);
        _machine.Margin = new Thickness(0, 4, 0, 0);
        heroLeft.Children.Add(_machine);
        heroLeft.Children.Add(_tags);
        var actions = Ui.Row(_recommend, Ui.Btn("Create restore point", CreateRestorePoint), Ui.Btn("Open Game Booster", (_, _) => MainWindow.Current?.Navigate("booster"), "GhostButton"));
        actions.Margin = new Thickness(0, 18, 0, 0);
        heroLeft.Children.Add(actions);
        var heroRight = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 8, 0) };
        heroRight.Children.Add(_score);
        _scoreNote.TextAlignment = TextAlignment.Center; _scoreNote.MaxWidth = 200; _scoreNote.Margin = new Thickness(0, 4, 0, 0);
        heroRight.Children.Add(_scoreNote);
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(heroRight, 1);
        hero.Children.Add(heroLeft);
        hero.Children.Add(heroRight);
        body.Children.Add(Ui.Hud(new Border
        {
            Style = Ui.Res<Style>("CardBorder"), Background = Ui.Brush("HeroFill"), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(32, 28, 32, 28), Margin = new Thickness(0, 4, 14, 18), Child = hero // right edge lines up with the cards below
        }));

        // ── Live performance ───────────────────────────────────────────────────────────────────────────────────
        var liveHead = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var liveTag = new StackPanel { Orientation = Orientation.Horizontal };
        liveTag.Children.Add(new System.Windows.Shapes.Rectangle { Width = 8, Height = 8, Fill = Ui.Brush("Success"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        liveTag.Children.Add(Ui.Muted("Live · Windows performance counters", 11.5));
        DockPanel.SetDock(liveTag, Dock.Right);
        liveHead.Children.Add(liveTag);
        liveHead.Children.Add(new TextBlock { Text = "Live performance", Style = Ui.Res<Style>("H2") });
        var rings = (ResponsiveGrid)Ui.Grid(3, Center(_cpu), Center(_gpu), Center(_ram));
        rings.MinItemWidth = 170;
        var livePanel = new StackPanel();
        livePanel.Children.Add(liveHead);
        livePanel.Children.Add(rings);
        body.Children.Add(Ui.Card(livePanel, new Thickness(0, 0, 14, 14)));
        body.Children.Add(Ui.Grid(3, _net, _disk, _temp));

        // ── Quick actions ──────────────────────────────────────────────────────────────────────────────────────
        body.Children.Add(new TextBlock { Text = "Gaming toolkit", Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 8, 0, 12) });
        var kit = (ResponsiveGrid)Ui.Grid(4,
            Action("\uE945", "Game Booster", "One-click session boost", "booster"),
            Action("\uE790", "Potato Graphics", "Low-poly Fortnite studio", "potato"),
            Action("\uE701", "Ping Stabilizer", "Latency, jitter & loss", "ping"),
            Action("\uE962", "Input Delay", "Timer resolution & input", "input"));
        kit.MinItemWidth = 190;
        body.Children.Add(kit);

        // ── PC + categories ───────────────────────────────────────────────────────────────────────────────────
        var left = Ui.Section("Your PC");
        left.Children.Add(_specs);
        _xmp.Margin = new Thickness(0, 10, 0, 0);
        left.Children.Add(_xmp);
        var right = Ui.Section("Applied by Money Tweaks", "Tweaks with a backup, per category. Click a row to open it.");
        right.Children.Add(_cats);
        body.Children.Add(Ui.Grid(2, Ui.Card(left, new Thickness(0, 0, 14, 14)), Ui.Card(right, new Thickness(0, 0, 14, 14))));
        ((ResponsiveGrid)body.Children[^1]).MinItemWidth = 360;
        BuildExtras(body);
        Content = root;

        TweakService.Changed += () => Dispatcher.InvokeAsync(() => { _scoreLoaded = false; _ = LoadScoreAsync(); UpdateCategories(); UpdateTags(); });
        UpdateGreeting();
        UpdateTags();
        UpdateCategories();
        _ = LoadSpecsAsync();
    }

    private static UIElement Center(FrameworkElement e) { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    private static UIElement Action(string icon, string title, string sub, string tag)
    {
        var dp = new DockPanel();
        var arrow = new TextBlock { Text = "\uE72A", Style = Ui.Res<Style>("Icon"), FontSize = 12, Foreground = Ui.Brush("Subtle"), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(arrow, Dock.Right);
        dp.Children.Add(arrow);
        var sp = new StackPanel();
        var tile = Ui.IconTile(icon, 42, 17);
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        tile.Margin = new Thickness(0, 0, 0, 14);
        sp.Children.Add(tile);
        sp.Children.Add(Ui.Text(title, 14, FontWeights.SemiBold));
        sp.Children.Add(Ui.Muted(sub, 12));
        dp.Children.Add(sp);
        return Ui.ClickCard(dp, () => MainWindow.Current?.Navigate(tag), new Thickness(0, 0, 14, 14));
    }

    private void UpdateGreeting()
    {
        var h = DateTime.Now.Hour;
        _greeting.Text = h < 5 ? "Late-night session" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
    }

    private void UpdateTags()
    {
        _tags.Children.Clear();
        _tags.Children.Add(Admin.IsElevated ? Ui.Tag("Administrator", "Success") : Ui.Tag("Standard user", "Gold"));
        // Counts Money Tweaks' own backups; the ring counts recommended settings already in place, whoever set them.
        var backed = BackupStore.AllIds().Count();
        _tags.Children.Add(Ui.Tag(backed == 1 ? "1 change by Money Tweaks" : $"{backed} changes by Money Tweaks", "Accent"));
        _tags.Children.Add(App.Settings.CreateRestorePointBeforeBatch ? Ui.Tag("Auto restore point on", "Violet") : Ui.Tag("Auto restore point off", "Muted"));
        _tags.Children.Add(Ui.Tag($"{TweakCatalog.All.Count} tweaks available", "Muted"));
    }

    private void UpdateCategories()
    {
        _cats.Children.Clear();
        var applied = BackupStore.AllIds().ToHashSet();
        foreach (var c in Enum.GetValues<TweakCategory>())
        {
            var all = TweakCatalog.InCategory(c).ToList();
            var n = all.Count(t => applied.Contains(t.Id));
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
            var cat = c;
            row.MouseLeftButtonUp += (_, _) => MainWindow.Current?.Navigate("cat:" + cat);
            var count = Ui.Muted($"{n}/{all.Count}", 12);
            count.Width = 54; count.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Width = 140 };
            label.Children.Add(new TextBlock { Text = MainWindow.CategoryIcons[c], Style = Ui.Res<Style>("Icon"), FontSize = 13, Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 10, 0) });
            label.Children.Add(Ui.Text(CategoryInfo.DisplayName(c), 12.5, FontWeights.SemiBold));
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(new ProgressBar { Maximum = Math.Max(1, all.Count), Value = n, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            _cats.Children.Add(row);
        }
    }

    /// <summary>Score = share of Recommended tweaks currently active on this PC (real state checks, batched, off the UI thread).</summary>
    private async Task LoadScoreAsync()
    {
        if (_scoreLoaded) return;
        _scoreLoaded = true;
        var items = TweakCatalog.All.Where(t => t.Recommended).Select(t => new TweakItem(t)).ToList();
        await TweakService.RefreshStatesAsync(items);
        var relevant = items.Where(i => i.State != TweakState.NotApplicable).ToList();
        var on = relevant.Count(i => i.State == TweakState.Applied);
        var pct = relevant.Count == 0 ? 0 : 100.0 * on / relevant.Count;
        _score.Set(pct, $"{pct:0}", "%");
        _scoreNote.Text = $"{on} of {relevant.Count} recommended tweaks are active on this PC. A checklist, not a performance rating.";
        var left = relevant.Count - on;
        _recommend.Content = left == 0 ? "✓  All recommended applied" : $"★  Apply recommended ({left})";
        _recommend.IsEnabled = left > 0;
    }

    private async Task LoadSpecsAsync()
    {
        var s = await Task.Run(HardwareMonitor.ReadSpecs);
        var gpu = s.Gpus.Split(" + ").LastOrDefault(g => !g.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) ?? s.Gpus;
        _machine.Text = $"{s.Os}  ·  {Short(s.Cpu)}  ·  {Short(gpu)}  ·  {s.RamGb:0} GB";
        void Line(string k, string v)
        {
            var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var key = Ui.Muted(k, 12.5); key.Width = 110;
            d.Children.Add(key);
            d.Children.Add(Ui.Text(v, 12.5));
            _specs.Children.Add(d);
        }
        Line("Windows", s.Os);
        Line("Processor", $"{s.Cpu} ({s.Cores}C/{s.Threads}T)");
        Line("Graphics", s.Gpus);
        Line("Memory", $"{s.RamGb:0.#} GB" + (s.RamConfiguredMts is { } cfg ? $" @ {cfg} MT/s" : ""));
        Line("Motherboard", s.Motherboard);
        Line("BIOS", s.Bios);
        if (s.RamRatedMts is { } r && s.RamConfiguredMts is { } c && c < r)
            _xmp.Text = $"⚠ Your RAM runs at {c} MT/s but the modules report {r} MT/s. See BIOS › 'Enable XMP / EXPO memory profile'.";
        else if (s.RamConfiguredMts is not null)
            _xmp.Text = "✓ Memory is running at its reported speed. (XMP/EXPO overclocks above the JEDEC rating can't be detected from Windows.)";
    }

    private static string Short(string s) => s.Replace("(R)", "").Replace("(TM)", "").Replace(" Processor", "").Replace("  ", " ").Trim();

    private async void ApplyRecommended(object s, RoutedEventArgs e)
    {
        var items = TweakCatalog.All.Where(t => t.Recommended).Select(t => new TweakItem(t)).ToList();
        _recommend.IsEnabled = false;
        _recommend.Content = "Checking…";
        try
        {
            await TweakService.RefreshStatesAsync(items);
            // Same set the button counts: everything recommended that isn't fully on, partly applied ones included.
            var todo = items.Where(i => i.State is TweakState.NotApplied or TweakState.Partial && i.CanToggle).ToList();
            if (todo.Count == 0) { MainWindow.Current?.ShowToast("Every recommended tweak is already applied.", "Success"); return; }
            var list = string.Join("\n", todo.GroupBy(t => CategoryInfo.DisplayName(t.Tweak.Category)).Select(g => $"{g.Key}: {g.Count()}"));
            if (!Dialog.Confirm($"Apply {todo.Count} recommended tweaks?",
                    $"These are all marked Safe and are the common, well-understood optimizations:\n\n{list}\n\nA restore point is created first (if System Protection is on), and each tweak is backed up so you can undo it individually.", "Apply")) return;
            _recommend.Content = "Applying…";
            var (ok, failed, reboot) = await TweakService.BatchAsync(todo, true);
            MainWindow.Current?.ShowToast($"{ok} applied{(failed > 0 ? $", {failed} failed" : "")}.{(reboot ? " Restart Windows to finish." : "")}", failed > 0 ? "Gold" : "Success");
        }
        finally { _scoreLoaded = false; await LoadScoreAsync(); UpdateCategories(); UpdateTags(); }
    }

    private async void CreateRestorePoint(object s, RoutedEventArgs e)
    {
        if (!Admin.IsElevated) { TweakService.EnsureAdmin(TweakCatalog.All.Where(t => t.RequiresAdmin).Take(1)); return; }
        var b = (Button)s;
        b.IsEnabled = false; b.Content = "Creating…";
        var (ok, msg) = await Task.Run(() => RestorePoints.Create("Money Tweaks manual restore point"));
        b.IsEnabled = true; b.Content = "Create restore point";
        MainWindow.Current?.ShowToast(msg, ok ? "Success" : "Gold");
    }

    private void OnSample(HardwareSnapshot s)
    {
        LiveStats.FillRings(s, _cpu, _gpu, _ram);
        LiveStats.Fill(s, _net, _disk, _temp);
    }

    public void OnShown() { LiveStats.Subscribe(OnSample); UpdateGreeting(); UpdateTags(); UpdateCategories(); _ = LoadScoreAsync(); _ = RefreshExtrasAsync(); }
    public void OnHidden() => LiveStats.Unsubscribe(OnSample);
}
