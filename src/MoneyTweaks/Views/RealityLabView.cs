using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Reality Lab: a 3D virtual PC driven by this PC's live readings, plus safe tweak experiments. An experiment shows
/// exactly what would change and a labelled estimate, then measures with and without the tweaks (counterbalanced A/B,
/// the same sampler and 3D scene as Test &amp; Benchmark), puts every setting back, and only keeps the tweaks if asked.
/// The 3D scene is built after the page appears and only animates while the page is visible and nothing is being measured.
/// </summary>
public sealed partial class RealityLabView : UserControl, IPageLifecycle
{
    private readonly Border _sceneHost = new() { Height = 400, CornerRadius = new CornerRadius(0), ClipToBounds = true };
    private VirtualPcScene? _scene;
    private HardwareMonitor? _mon;
    private Task? _monInit;
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sampling, _shown;
    private PcReading? _live;
    private HardwareSnapshot? _liveSnap;
    private SystemSpecs? _specs;
    private readonly Task<(int Modules, int Slots)> _memLayout = Task.Run(HardwareMonitor.MemoryLayout);
    private string _view = "live"; // "live", "before", "after"
    private string _part = "cpu";
    private readonly StackPanel _readout = new();
    private readonly TextBlock _sceneNote = Ui.Muted("", 11.5);
    private readonly WrapPanel _viewChips = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _viewTag = new();
    private static readonly bool SoftwareRendering = (RenderCapability.Tier >> 16) == 0;

    public RealityLabView()
    {
        var (root, body) = Ui.Page("Reality Lab",
            "A 3D model of your PC that reacts to its real CPU, GPU and memory readings, and a lab for trying safe tweaks: see exactly what changes and what is expected, measure it with and without, then keep it or leave your PC as it was.",
            eyebrow: "Live 3D · Experiments · Measured results", icon: "\uE7B8");
        Content = root;
        body.Children.Add(BuildSceneCard());
        body.Children.Add(BuildGpuCard());
        body.Children.Add(BuildExperimentCard());
        body.Children.Add(_resultCard);
        body.Children.Add(_keptCard);
        _resultCard.Visibility = Visibility.Collapsed;
        RenderKept();

        _sampleTimer.Tick += async (_, _) => await SampleAsync();
        // Lazy: the 3D scene is built after the page has drawn once, so opening the page stays instant.
        Loaded += (_, _) => Dispatcher.BeginInvoke(EnsureScene, DispatcherPriority.Background);
        _ = Task.Run(HardwareMonitor.ReadSpecs).ContinueWith(t => Dispatcher.BeginInvoke(() => { _specs = t.Result; RenderReadout(); }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    // ---- virtual PC -------------------------------------------------------------------------------------------------

    private UIElement BuildSceneCard()
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        DockPanel.SetDock(_viewTag, Dock.Right);
        head.Children.Add(_viewTag);
        head.Children.Add(Ui.Text("Virtual PC", 15, FontWeights.SemiBold));
        sp.Children.Add(head);

        foreach (var (key, label) in new[] { ("live", "Live"), ("before", "Before (measured)"), ("after", "After (measured)") })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "labview", IsChecked = key == _view, Margin = new Thickness(0, 0, 6, 6), Tag = key };
            rb.Checked += (_, _) => { _view = key; ShowView(); };
            _viewChips.Children.Add(rb);
        }
        sp.Children.Add(_viewChips);

        _sceneHost.Background = new LinearGradientBrush(Color.FromRgb(0x10, 0x11, 0x18), Color.FromRgb(0x05, 0x06, 0x08), 90);
        _sceneHost.Child = Ui.Loading("Building the virtual PC…");
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.Children.Add(_sceneHost);
        var side = new Border { Padding = new Thickness(18, 4, 0, 0), Child = _readout };
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        sp.Children.Add(grid);
        sp.Children.Add(_sceneNote);
        _sceneNote.Margin = new Thickness(0, 10, 0, 0);
        _sceneNote.TextWrapping = TextWrapping.Wrap;
        SyncViewChips();
        return Ui.Hud(Ui.Card(sp));
    }

    private void EnsureScene()
    {
        if (_scene is not null) return;
        // The model shows this PC's real memory modules and slots and its graphics card, so wait for those readings (off the UI thread).
        if (!_memLayout.IsCompleted) { _memLayout.ContinueWith(_ => Dispatcher.BeginInvoke(EnsureScene, DispatcherPriority.Background)); return; }
        if (!_gpuDetect.IsCompleted) { _gpuDetect.ContinueWith(_ => Dispatcher.BeginInvoke(EnsureScene, DispatcherPriority.Background)); return; }
        using var perf = Perf.Measure("build Reality Lab scene", 20);
        var (modules, slots) = _memLayout.Result;
        _scene = new VirtualPcScene(modules, slots, detailed: !SoftwareRendering, gpu: _gpuDetect.IsCompletedSuccessfully ? _gpuDetect.Result.match : null);
        _scene.PartSelected += p => { _part = p; RenderReadout(); };
        _scene.Select(_part);
        _sceneHost.Child = _scene;
        if (CurrentReading() is { } r) _scene.Show(r);
        SyncRunning();
        Log.Info($"Reality Lab: virtual PC built ({_scene.Triangles} triangles{(SoftwareRendering ? ", software rendering: animation off" : "")})");
    }

    private void SyncRunning()
    {
        if (_scene is not null) _scene.Running = _shown && !_measuring && !SoftwareRendering;
        if (_showcase is not null) _showcase.Running = _shown && !_measuring && !SoftwareRendering;
        _sceneNote.Text =
            (_measuring ? "The 3D animation is paused while measuring, so the lab itself doesn't affect the results. " : "") +
            (SoftwareRendering ? "Animation is off because Windows draws this app without the graphics card here; colours and levels still update. " : "") +
            (App.ReduceMotion && !SoftwareRendering ? "Reduced motion is on: the model holds still and only its colours and levels change. " : "") +
            "Heat colour follows the temperature sensor (or load when there's none), GPU fans spin with GPU load, RAM sticks fill with memory in use, and data pulses speed up with CPU load. " +
            "The model is an illustration of your parts (your real number of memory modules and slots, and a graphics card shaped after yours), not a scan of their exact layout.";
    }

    public void OnShown()
    {
        _shown = true;
        _monInit ??= Task.Run(() => { _mon = new HardwareMonitor(); });
        if (_scene is null) Dispatcher.BeginInvoke(EnsureScene, DispatcherPriority.Background);
        _sampleTimer.Start();
        HoldHub(true);
        if (_showcase is null) Dispatcher.BeginInvoke(EnsureShowcase, DispatcherPriority.Background);
        SyncRunning();
        RenderKept();
        if (!_measuring) FillPresetRow();
    }

    public void OnHidden()
    {
        _shown = false;
        _sampleTimer.Stop();
        HoldHub(false);
        SyncRunning();
    }

    private async Task SampleAsync()
    {
        if (_sampling || _measuring || _monInit is null) return;
        _sampling = true;
        try
        {
            await _monInit;
            if (_mon is not { } mon) return;
            var s = await Task.Run(mon.Sample);
            _liveSnap = s;
            _live = new PcReading(s.CpuPercent, s.CpuTempC, s.GpuPercent, s.GpuTempC, s.RamUsedGb, s.RamTotalGb);
            if (_view == "live") { _scene?.Show(_live); RenderReadout(); }
            UpdateGpuTelemetry();
        }
        catch (Exception ex) { Log.Debug("Reality Lab sample: " + ex.Message); }
        finally { _sampling = false; }
    }

    /// <summary>What the model shows: live readings, or the measured averages of an experiment's before/after runs.</summary>
    private PcReading? CurrentReading() => _view switch
    {
        "before" => Averages(_lastRuns.Where(r => r.Phase == "Baseline").ToList()),
        "after" => Averages(_lastRuns.Where(r => r.Phase == "After").ToList()),
        _ => _live,
    };

    private PcReading? Averages(List<BenchmarkRun> runs)
    {
        if (runs.Count == 0) return null;
        double? Avg(Func<BenchmarkRun, double?> f) { var v = runs.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : v.Average(); }
        var total = _live?.RamTotalGb ?? MemoryTotalGb();
        return new PcReading(Avg(r => r.CpuAvg) ?? 0, Avg(r => r.CpuTempC), Avg(r => r.GpuAvg), Avg(r => r.GpuTempC), Avg(r => r.RamUsedGb) ?? 0, total);
    }

    private static double MemoryTotalGb() { var (total, _, _) = MemoryInfo.Status(); return total / 1073741824.0; }

    private void ShowView()
    {
        if (_view != "live" && _lastRuns.Count == 0) _view = "live";
        if (CurrentReading() is { } r) _scene?.Show(r);
        SyncViewChips();
        RenderReadout();
    }

    private void SyncViewChips()
    {
        foreach (var rb in _viewChips.Children.OfType<RadioButton>())
        {
            var key = (string)rb.Tag;
            rb.IsEnabled = key == "live" || _lastRuns.Any(r => r.Phase == (key == "before" ? "Baseline" : "After"));
            rb.ToolTip = rb.IsEnabled ? null : "Run an experiment first: this shows its measured averages.";
            if (key == _view && rb.IsChecked != true) rb.IsChecked = true;
        }
        var (text, tone) = _view switch
        {
            "before" => ($"MEASURED AVERAGE · WITHOUT TWEAKS ({_lastName})", "Accent"),
            "after" => ($"MEASURED AVERAGE · WITH TWEAKS ({_lastName})", "Violet"),
            _ => ("LIVE READINGS", "Success"),
        };
        _viewTag.Child = Ui.Tag(text, tone);
    }

    private void RenderReadout()
    {
        _readout.Children.Clear();
        var r = CurrentReading();
        void Row(string k, string v, string? tip = null)
        {
            var g = new DockPanel { Margin = new Thickness(0, 4, 0, 4), ToolTip = tip };
            var val = new TextBlock { Text = v, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 160, TextAlignment = TextAlignment.Right };
            DockPanel.SetDock(val, Dock.Right);
            g.Children.Add(val);
            g.Children.Add(new TextBlock { Text = k, Foreground = Ui.Brush("Muted") });
            _readout.Children.Add(g);
        }
        var name = _part switch { "cpu" => _specs?.Cpu, "gpu" => _specs?.Gpus, _ => _specs is { } s ? $"{s.RamGb:0.#} GB installed" : null };
        _readout.Children.Add(new TextBlock { Text = _part.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow") });
        _readout.Children.Add(new TextBlock { Text = name ?? "Reading this PC's parts…", FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        if (r is null) { _readout.Children.Add(Ui.Muted(_view == "live" ? "Waiting for the first reading…" : "No measurement yet.", 12)); return; }
        bool live = _view == "live";
        switch (_part)
        {
            case "cpu":
                Row(live ? "Load" : "Average load", $"{r.CpuPct:0} %");
                Row("Temperature", r.CpuTempC is { } ct ? $"{ct:0} °C" : "not available",
                    r.CpuTempC is null ? "Windows doesn't expose it here: " + HardwareMonitor.CpuTempHint : live ? "Source: " + HardwareMonitor.CpuTempSource(r.CpuTempC) : null);
                if (live && _liveSnap is { } ls) Row("Processes", ls.ProcessCount.ToString());
                if (_specs is { } sp) Row("Cores / threads", $"{sp.Cores} / {sp.Threads}");
                break;
            case "gpu":
                Row(live ? "Load (3D engine)" : "Average load", r.GpuPct is { } g ? $"{g:0} %" : "no reading", r.GpuPct is null ? "Windows' GPU Engine counters aren't available on this PC." : null);
                Row("Temperature", r.GpuTempC is { } gt ? $"{gt:0} °C" : "not available", r.GpuTempC is null ? "Read through NVIDIA's driver; other GPUs don't report it to Windows." : null);
                break;
            default:
                Row(live ? "In use" : "Average in use", $"{r.RamUsedGb:0.0} of {r.RamTotalGb:0.#} GB");
                Row("Share", r.RamTotalGb > 0 ? $"{r.RamUsedGb / r.RamTotalGb * 100:0} %" : "–");
                if (_memLayout.IsCompletedSuccessfully && _memLayout.Result is { Modules: > 0 } ml)
                    Row("Modules", ml.Slots > 0 ? $"{ml.Modules} in {ml.Slots} slots" : $"{ml.Modules}", "From the firmware's memory tables; the model shows the same.");
                if (_specs is { } sp2 && (sp2.RamConfiguredMts ?? sp2.RamRatedMts) is { } mts)
                    Row("Speed", $"{mts} MT/s" + (sp2.RamRatedMts is { } rated && mts < rated ? $" (rated {rated})" : ""), "Configured memory speed reported by the firmware.");
                break;
        }
        _readout.Children.Add(Ui.Muted(live ? "Updated every second from Windows performance counters. Click another part in the model to see it." : $"Averages of the {(_view == "before" ? "without" : "with")}-tweaks runs of “{_lastName}”, measured {_lastAt:g}.", 11.5));
        ((TextBlock)_readout.Children[^1]).TextWrapping = TextWrapping.Wrap;
        ((FrameworkElement)_readout.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
    }
}
