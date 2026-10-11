using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>Shared live counters so Dashboard and Hardware Monitor don't each create their own.</summary>
public static class LiveStats
{
    private static HardwareMonitor? _mon;
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static int _subscribers;
    public static event Action<HardwareSnapshot>? Sampled;

    public static void Subscribe(Action<HardwareSnapshot> h)
    {
        Sampled += h;
        if (_subscribers++ == 0)
        {
            Timer.Tick -= Tick;
            Timer.Tick += Tick;
            Timer.Start();
        }
    }

    public static void Unsubscribe(Action<HardwareSnapshot> h)
    {
        Sampled -= h;
        if (--_subscribers <= 0) { _subscribers = 0; Timer.Stop(); }
    }

    private static bool _busy;
    private static async void Tick(object? s, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _mon ??= await Task.Run(() => new HardwareMonitor());
            var snap = await Task.Run(_mon.Sample);
            Sampled?.Invoke(snap);
        }
        catch (Exception ex) { Log.Debug("monitor: " + ex.Message); }
        finally { _busy = false; }
    }

    public static void Fill(HardwareSnapshot s, StatTile cpu, StatTile gpu, StatTile ram, StatTile disk, StatTile net, StatTile temp)
    {
        cpu.Push(s.CpuPercent, $"{s.CpuPercent:0}%", $"{s.ProcessCount} processes");
        gpu.Push(s.GpuPercent, s.GpuPercent is null ? "N/A" : $"{s.GpuPercent:0}%", s.GpuPercent is null ? "GPU counters unavailable" : "3D engine load");
        ram.Push(s.RamPercent, $"{s.RamPercent:0}%", $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB");
        disk.Push(s.DiskPercent, s.DiskPercent is null ? "N/A" : $"{s.DiskPercent:0}%", "active time, all disks");
        net.Max = 1024;
        net.Push(s.NetDownKBps, s.NetDownKBps > 1024 ? $"{s.NetDownKBps / 1024:0.0} MB/s" : $"{s.NetDownKBps:0} KB/s", $"↑ {(s.NetUpKBps > 1024 ? $"{s.NetUpKBps / 1024:0.0} MB/s" : $"{s.NetUpKBps:0} KB/s")}");
        temp.Push(s.CpuTempC, s.CpuTempC is null ? "N/A" : $"{s.CpuTempC:0} °C",
            s.CpuTempC is null ? "Firmware doesn't expose a thermal zone" : "ACPI thermal zone");
    }
}

public sealed class DashboardView : UserControl, IPageLifecycle
{
    private readonly StatTile _cpu = new("CPU", "\uE950"), _gpu = new("GPU", "\uE7F4"), _ram = new("Memory", "\uE964"),
        _disk = new("Disk", "\uEDA2"), _net = new("Network ↓", "\uE896"), _temp = new("CPU temp", "\uE706");
    private readonly StackPanel _specs = new();
    private readonly TextBlock _applied = Ui.Text("…", 28, FontWeights.SemiBold);
    private readonly TextBlock _xmp = Ui.Muted("");
    private readonly UniformGrid _cats = new() { Columns = 3 };

    public DashboardView()
    {
        var (root, body) = Ui.Page("Dashboard", $"{TweakCatalog.All.Count} reversible tweaks across 9 categories, plus gaming tools. Every change is backed up and can be undone from the same switch.",
            Ui.Row(Ui.Btn("★ Apply all recommended", ApplyRecommended, "PrimaryButton"), Ui.Btn("Create restore point", CreateRestorePoint)));

        body.Children.Add(Ui.Grid(3, _cpu, _gpu, _ram));
        body.Children.Add(Ui.Grid(3, _disk, _net, _temp));

        var left = new StackPanel();
        left.Children.Add(Ui.Section("Your PC"));
        left.Children.Add(_specs);
        left.Children.Add(_xmp);
        var right = new StackPanel();
        right.Children.Add(Ui.Section("Tweaks applied"));
        right.Children.Add(_applied);
        right.Children.Add(Ui.Muted("Backups are stored in %LOCALAPPDATA%\\MoneyTweaks\\backups. Undo any tweak with its switch, or everything at once from Backup & Restore."));
        var two = new Grid();
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var lc = Ui.Card(left, new Thickness(0, 0, 12, 12));
        var rc = Ui.Card(right, new Thickness(0, 0, 12, 12));
        Grid.SetColumn(rc, 1);
        two.Children.Add(lc); two.Children.Add(rc);
        body.Children.Add(two);

        body.Children.Add(new TextBlock { Text = "Categories", Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 8, 0, 10) });
        foreach (var c in Enum.GetValues<TweakCategory>())
        {
            var sp = new StackPanel();
            var head = new DockPanel();
            var count = Ui.Pill(TweakCatalog.InCategory(c).Count().ToString());
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);
            head.Children.Add(Ui.Text(CategoryInfo.DisplayName(c), 14, FontWeights.SemiBold));
            sp.Children.Add(head);
            sp.Children.Add(Ui.Muted(CategoryInfo.Blurb(c), 12));
            var card = Ui.Card(sp, new Thickness(0, 0, 12, 12));
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => MainWindow.Current?.Navigate("cat:" + c);
            card.MouseEnter += (_, _) => card.BorderBrush = Ui.Brush("Accent");
            card.MouseLeave += (_, _) => card.BorderBrush = Ui.Brush("BorderBrush");
            _cats.Children.Add(card);
        }
        body.Children.Add(_cats);
        Content = root;
        TweakService.Changed += () => Dispatcher.InvokeAsync(UpdateApplied);
        _ = LoadSpecsAsync();
        UpdateApplied();
    }

    private void UpdateApplied() => _applied.Text = $"{BackupStore.AllIds().Count()} / {TweakCatalog.All.Count}";

    private async Task LoadSpecsAsync()
    {
        var s = await Task.Run(HardwareMonitor.ReadSpecs);
        void Line(string k, string v)
        {
            var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
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
            _xmp.Text = $"Your RAM runs at {c} MT/s, but the modules report {r} MT/s. See BIOS › 'Enable XMP / EXPO memory profile'.";
        else if (s.RamConfiguredMts is not null)
            _xmp.Text = "Memory is running at its reported speed. XMP/EXPO overclocks above the JEDEC rating can't be detected from Windows.";
        _xmp.Margin = new Thickness(0, 8, 0, 0);
    }

    private async void ApplyRecommended(object s, RoutedEventArgs e)
    {
        var items = TweakCatalog.All.Where(t => t.Recommended).Select(t => new TweakItem(t)).ToList();
        IsEnabled = false;
        try
        {
            await Task.WhenAll(items.Select(i => i.RefreshAsync()));
            var todo = items.Where(i => i.State == TweakState.NotApplied).ToList();
            if (todo.Count == 0) { MainWindow.Current?.ShowToast("Every recommended tweak is already applied."); return; }
            var list = string.Join("\n", todo.GroupBy(t => CategoryInfo.DisplayName(t.Tweak.Category)).Select(g => $"{g.Key}: {g.Count()}"));
            if (!Dialog.Confirm($"Apply {todo.Count} recommended tweaks?",
                    $"These are all marked Safe and are the common, well-understood optimizations:\n\n{list}\n\nA restore point is created first (if System Protection is on), and each tweak is backed up so you can undo it individually.", "Apply")) return;
            var (ok, failed, reboot) = await TweakService.BatchAsync(todo, true);
            Dialog.Show("Recommended tweaks applied", $"{ok} applied{(failed > 0 ? $", {failed} failed. Open the category pages to see why" : "")}.{(reboot ? "\n\nRestart Windows to finish." : "")}");
        }
        finally { IsEnabled = true; UpdateApplied(); }
    }

    private async void CreateRestorePoint(object s, RoutedEventArgs e)
    {
        if (!Admin.IsElevated) { TweakService.EnsureAdmin(TweakCatalog.All.Where(t => t.RequiresAdmin).Take(1)); return; }
        IsEnabled = false;
        var (ok, msg) = await Task.Run(() => RestorePoints.Create("Money Tweaks manual restore point"));
        IsEnabled = true;
        Dialog.Show(ok ? "Restore point created" : "Restore point not created", msg, ok ? DialogKind.Info : DialogKind.Warning);
    }

    private void OnSample(HardwareSnapshot s) => LiveStats.Fill(s, _cpu, _gpu, _ram, _disk, _net, _temp);
    public void OnShown() { LiveStats.Subscribe(OnSample); UpdateApplied(); }
    public void OnHidden() => LiveStats.Unsubscribe(OnSample);
}

public sealed class MonitorView : UserControl, IPageLifecycle
{
    private readonly StatTile _cpu = new("CPU usage", "\uE950"), _gpu = new("GPU 3D usage", "\uE7F4"), _ram = new("Memory in use", "\uE964"),
        _disk = new("Disk active time", "\uEDA2"), _net = new("Download", "\uE896"), _temp = new("CPU temperature", "\uE706");
    private readonly TextBlock _uptime = Ui.Muted("");
    private readonly TextBlock _timer = Ui.Muted("");

    public MonitorView()
    {
        var (root, body) = Ui.Page("Hardware Monitor", "Live readings from Windows performance counters, updated every second. No drivers are installed. Temperatures only appear if your firmware exposes an ACPI thermal zone; for full sensor data use a dedicated tool such as HWiNFO.");
        body.Children.Add(Ui.Grid(2, _cpu, _gpu));
        body.Children.Add(Ui.Grid(2, _ram, _disk));
        body.Children.Add(Ui.Grid(2, _net, _temp));
        var info = new StackPanel();
        info.Children.Add(Ui.Section("System"));
        info.Children.Add(_uptime);
        info.Children.Add(_timer);
        body.Children.Add(Ui.Card(info));
        Content = root;
    }

    private void OnSample(HardwareSnapshot s)
    {
        LiveStats.Fill(s, _cpu, _gpu, _ram, _disk, _net, _temp);
        _uptime.Text = $"Uptime: {(int)s.Uptime.TotalDays}d {s.Uptime.Hours}h {s.Uptime.Minutes}m · {s.ProcessCount} processes running";
        var (min, max, cur) = TimerResolution.Query();
        _timer.Text = $"Kernel timer resolution: {cur:0.###} ms (range {max:0.###} – {min:0.###} ms)";
    }

    public void OnShown() => LiveStats.Subscribe(OnSample);
    public void OnHidden() => LiveStats.Unsubscribe(OnSample);
}
