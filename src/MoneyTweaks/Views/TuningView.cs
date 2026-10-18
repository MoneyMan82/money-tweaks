using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

/// <summary>
/// CPU & GPU tuning center. Direct controls only where a vendor documents an interface (nvidia-smi, Windows processor
/// power settings); everything else is a guided handoff to the official tool or the BIOS. Every change: preview,
/// confirmation, backup, read-back, rollback. Nothing is applied at startup.
/// </summary>
public sealed class TuningView : UserControl, IPageLifecycle
{
    private readonly StackPanel _hw = new(), _status = new(), _auto = new(), _manual = new(), _test = new(), _caps = new(), _banner = new(), _ab = new(), _scan = new();
    private readonly TextBlock _live = Ui.Muted("", 12.5);
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TuningInventory? _inv;
    private List<NvidiaGpuLimits> _nv = [];
    private Dictionary<string, CpuPowerSetting> _cpu = [];
    private TuningState _state = TuningState.Load();
    private bool _busy, _liveBusy;
    private CancellationTokenSource? _testCts;
    private readonly Button _rescan;

    private NvidiaGpuLimits? Gpu => _nv.FirstOrDefault(g => g.Index == _state.GpuIndex) ?? _nv.FirstOrDefault();

    public TuningView()
    {
        _rescan = Ui.Btn("Rescan hardware", async (_, _) => await LoadAsync(force: true), "BaseButton", new Thickness(0));
        var (root, body) = Ui.Page("CPU & GPU Tuning",
            "Power limits, clock caps and efficiency profiles where your hardware's vendor supports them, guided steps for everything else (PBO, Curve Optimizer, Intel XTU, GPU curves, XMP). Every change is previewed, backed up, read back and can be undone in one click.",
            _rescan, icon: "\uE950");
        body.Children.Add(_banner);
        body.Children.Add(Ui.Card(Section("This PC", _hw)));
        body.Children.Add(Ui.Card(Section("Current tuning", _status, "What Money Tweaks changed, and whether each value was read back.", _live)));
        body.Children.Add(Ui.Card(Section("Auto tuning", new StackPanel { Children = { _scan, new Border { Height = 1, Background = Ui.Brush("BorderBrush"), Margin = new Thickness(0, 14, 0, 12) }, _auto } },
            "Find your best GPU overclock automatically, or pick a ready-made profile from a quick look at your hardware.")));
        body.Children.Add(Ui.Card(Section("Manual tuning", _manual, "Every control here changes a real setting through a documented interface, within the range the driver or Windows reports.")));
        body.Children.Add(Ui.Card(Section("GPU offsets, undervolt and fan (MSI Afterburner)", _ab, "Through Afterburner's documented control interface (its SDK). Needs Afterburner running.")));
        body.Children.Add(Ui.Card(Section("Stability tests", _test, "Quick scan (1 minute) catches gross problems only. Extended tests run longer. Neither proves stability.")));
        body.Children.Add(Ui.Card(Section("Everything else: guided", _caps, "What can't be changed safely from here, and exactly where to do it instead.")));
        body.Children.Add(Ui.Card(Section("Read this first", new StackPanel { Children = { Ui.Muted(
            "• Overclocking and undervolting can crash games, freeze Windows and corrupt files that are being written when it happens. Save your work and test before relying on any setting.\n" +
            "• Money Tweaks never writes undocumented CPU registers (MSRs), SMU/BIOS values or arbitrary voltages, and never installs a kernel driver.\n" +
            "• GPU changes made with nvidia-smi reset when Windows restarts. CPU power settings belong to the active power plan and stay until reverted.\n" +
            "• Nothing is applied when Money Tweaks starts. If it closes before you confirm a change, the next start offers to undo it.", 12) } })));
        Content = root;
        _liveTimer.Tick += async (_, _) => await UpdateLiveAsync();
    }

    private static StackPanel Section(string title, UIElement content, string? sub = null, UIElement? extra = null)
    {
        var s = Ui.Section(title, sub);
        if (extra is not null) s.Children.Add(extra);
        s.Children.Add(content);
        return s;
    }

    public async void OnShown()
    {
        _liveTimer.Start();
        if (_inv is null)
        {
            // Cached inventory shows instantly; the scan (once per run) refreshes it in the background.
            _inv = TuningDetector.Cached();
            if (_inv is not null) RenderHardware();
            else _hw.Children.Add(Ui.Loading("Detecting CPU, GPU, motherboard, BIOS, drivers and memory…"));
            await LoadAsync(force: false);
        }
    }

    public void OnHidden() => _liveTimer.Stop();

    private async Task LoadAsync(bool force)
    {
        _rescan.IsEnabled = false;
        try
        {
            var invTask = TuningDetector.ScanAsync(force);
            var nvTask = Task.Run(NvidiaSmi.Limits);
            var cpuTask = Task.Run(() => new[] { CpuPowerSettings.BoostMode, CpuPowerSettings.MaxState, CpuPowerSettings.Epp }.Select(CpuPowerSettings.Read).ToDictionary(s => s.Alias));
            _inv = await invTask;
            _nv = await nvTask;
            _cpu = await cpuTask;
            if (Gpu is { } g && _state.GpuIndex != g.Index) _state.GpuIndex = g.Index;
            RenderAll();
        }
        catch (Exception ex) { Log.Error("tuning load", ex); _hw.Children.Clear(); _hw.Children.Add(Ui.Muted("Detection failed: " + ex.Message)); }
        finally { _rescan.IsEnabled = true; }
    }

    private void RenderAll()
    {
        RenderHardware(); RenderBanner(); RenderStatus(); RenderAuto(); RenderManual(); RenderTests(); RenderCaps();
        if (!TuningViewAutoScan.Running) TuningViewAutoScan.Render(_scan, _state, _inv, Gpu, ReloadValuesAsync);
        _ = TuningViewAfterburner.RenderAsync(_ab, _state, _inv, Gpu?.PowerLimitSupported == true, ReloadValuesAsync);
    }

    // ---- this PC ------------------------------------------------------------------------------------------------------

    private void RenderHardware()
    {
        _hw.Children.Clear();
        if (_inv is not { } i) return;
        void Row(string k, string v) { var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; var l = Ui.Muted(k, 12.5); l.Width = 150; DockPanel.SetDock(l, Dock.Left); d.Children.Add(l); d.Children.Add(Ui.Text(v, 12.5)); _hw.Children.Add(d); }
        Row("CPU", $"{i.Cpu} ({i.Cores} cores / {i.Threads} threads)");
        Row("Motherboard", i.Motherboard);
        Row("BIOS", i.Bios);
        foreach (var g in i.Gpus) Row("GPU", $"{g.Name} · driver {g.DriverVersion}{(g.DriverDate.Length > 0 ? $" ({g.DriverDate})" : "")}");
        foreach (var m in i.Ram) Row("Memory " + m.Slot, $"{m.Gb:0.#} GB {m.Manufacturer} {m.PartNumber}".Trim() + (m.ConfiguredMts is { } c ? $" · running {c} MT/s" : "") + (m.RatedMts is { } r ? $" (module reports {r} MT/s)" : ""));
        var tools = i.Tools.Where(t => t.Path is not null).Select(t => t.Name).ToList();
        Row("Tuning tools found", tools.Count == 0 ? "none" : string.Join(", ", tools));
        _hw.Children.Add(Ui.Muted($"Detected {i.DetectedAt:g}. Cached, so this page opens instantly; use Rescan after changing hardware or drivers.", 11.5));
    }

    // ---- pending banner -----------------------------------------------------------------------------------------------

    private void RenderBanner()
    {
        _banner.Children.Clear();
        if (!_state.PendingConfirmation || !_state.HasChanges) return;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text($"Tuning \"{_state.ProfileName}\" applied {_state.AppliedAt:g} hasn't been confirmed", 14, FontWeights.SemiBold));
        sp.Children.Add(Ui.Muted("Run a stability test, keep it if everything has been fine, or undo it now. If Money Tweaks or Windows crashed after applying, undo it.", 12.5));
        sp.Children.Add(Ui.Row(
            Ui.Btn("Undo tuning now", async (_, _) => await RevertAsync(), "GoldButton", new Thickness(0, 8, 8, 0)),
            Ui.Btn("Keep it", (_, _) => { _state.PendingConfirmation = false; _state.Save(); RenderBanner(); }, "GhostButton", new Thickness(0, 8, 8, 0))));
        _banner.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Gold"), Margin = new Thickness(0, 0, 0, 14), Child = sp });
    }

    // ---- status + live monitor ----------------------------------------------------------------------------------------

    private void RenderStatus()
    {
        _status.Children.Clear();
        if (!_state.HasChanges) { _status.Children.Add(Ui.Muted("Nothing changed by Money Tweaks: everything is at its original value.", 12.5)); return; }
        _status.Children.Add(Ui.Text($"Profile: {_state.ProfileName ?? "Custom"} · applied {_state.AppliedAt:g}", 13, FontWeights.SemiBold));
        foreach (var r in _state.LastApply)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(Ui.Tag(r.Status switch { ItemStatus.Verified => "Verified", ItemStatus.Applied => "Applied", ItemStatus.Failed => "Failed", ItemStatus.RolledBack => "Rolled back", _ => "Configured" },
                r.Status switch { ItemStatus.Verified => "Success", ItemStatus.Applied => "Accent", ItemStatus.Failed => "Danger", _ => "Muted" }));
            row.Children.Add(Ui.Text($"{r.Item}: {r.Value}", 12.5));
            row.ToolTip = r.Detail;
            _status.Children.Add(row);
        }
        if (_state.LastTestSummary is { } t) _status.Children.Add(Ui.Muted("Last test: " + t, 12));
        _status.Children.Add(Ui.Row(Ui.Btn("Undo all tuning (restore originals)", async (_, _) => await RevertAsync(), "DangerButton", new Thickness(0, 10, 8, 0))));
    }

    private async Task UpdateLiveAsync()
    {
        if (_liveBusy) return;
        _liveBusy = true;
        try
        {
            var gpu = Gpu;
            var nv = gpu is null ? null : await Task.Run(() => NvidiaSmi.Read(gpu.Index));
            var hw = LiveStats.Last;
            var parts = new List<string>();
            if (hw is not null) parts.Add($"CPU {hw.CpuPercent:0}% load" + (hw.CpuTempC is { } ct ? $", {ct:0} °C ({HardwareMonitor.CpuTempSource(ct)})" : $", temperature not available ({HardwareMonitor.CpuTempHint})"));
            if (nv is not null)
                parts.Add($"GPU {nv.LoadPercent}% · {nv.TempC:0} °C · {nv.PowerW:0}/{nv.PowerLimitW:0} W · {nv.CoreMhz} MHz core, {nv.MemMhz} MHz memory" +
                          (nv.HardwareThrottling ? " · ⚠ thermal/power-brake throttling" : nv.PowerCapped ? " · at power limit" : ""));
            _live.Text = parts.Count == 0 ? "" : "Live: " + string.Join("   |   ", parts);
        }
        catch (Exception ex) { Log.Debug("tuning live: " + ex.Message); }
        finally { _liveBusy = false; }
    }

    // ---- auto ------------------------------------------------------------------------------------------------------------

    private void RenderAuto()
    {
        _auto.Children.Clear();
        var result = Ui.Muted("", 12.5);
        _auto.Children.Add(Ui.Row(Ui.Btn("Quick assessment (10 s, read-only)", async (_, _) => await AssessAsync(result), "PrimaryButton")));
        _auto.Children.Add(result);
        var profiles = TuningEngine.Profiles(Gpu, _cpu).Where(p => !p.Settings.IsEmpty).ToList();
        if (profiles.Count == 0) { _auto.Children.Add(Ui.Muted("No supported profile on this hardware: neither an adjustable NVIDIA GPU nor readable Windows processor settings were found.", 12.5)); return; }
        var grid = Ui.Grid(3);
        foreach (var p in profiles)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(p.Name, 14, FontWeights.SemiBold));
            sp.Children.Add(Ui.Muted(p.Summary, 12));
            foreach (var l in TuningEngine.Preview(p.Settings, Gpu, _cpu, _state)) sp.Children.Add(Ui.Muted("• " + l, 11.5));
            sp.Children.Add(new TextBlock { Text = p.Caveat, FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            sp.Children.Add(Ui.Row(Ui.Btn("Preview & apply…", async (_, _) => await ApplyAsync(p.Settings, p.Name, p.Caveat), "BaseButton", new Thickness(0, 8, 0, 0))));
            grid.Children.Add(Ui.Card(sp, new Thickness(0, 0, 10, 10)));
        }
        _auto.Children.Add(grid);
        _auto.Children.Add(Ui.Muted("These profiles only change power and clock limits, never clock offsets. For an overclock, use the automatic scan above (NVIDIA) or the vendor's own automatic tuning: NVIDIA App Automatic Tuning, Ryzen Master PBO/Auto OC, Intel XTU Speed Optimizer, Adrenalin Auto Overclock/Undervolt (see Everything else below).", 11.5));
    }

    private async Task AssessAsync(TextBlock result)
    {
        result.Text = "Sampling for 10 seconds…";
        var gpu = Gpu;
        var samples = new List<(double? gt, double? gp, double? ct)>();
        for (int i = 0; i < 5; i++)
        {
            var nv = gpu is null ? null : await Task.Run(() => NvidiaSmi.Read(gpu.Index));
            samples.Add((nv?.TempC, nv?.PowerW, LiveStats.Last?.CpuTempC));
            await Task.Delay(2000);
        }
        var laptop = Core.Engine.SystemProfile.IsDetected ? Core.Engine.SystemProfile.Current.HasBattery : null;
        double? gt = samples.Max(s => s.gt), ct = samples.Max(s => s.ct);
        var lines = new List<string>();
        if (gt is { } g) lines.Add($"GPU at {g:0} °C now{(g >= 70 ? " (warm for light load: check airflow before raising limits)" : "")}.");
        if (ct is { } c) lines.Add($"CPU at {c:0} °C now{(c >= 80 ? " (hot for light load)" : "")}."); else lines.Add("CPU temperature isn't exposed by this firmware, so CPU thermals can't be judged here.");
        string rec = laptop == true ? "Efficiency (this PC has a battery: lower power means longer battery life and less heat)."
            : gt >= 70 || ct >= 80 ? "Quiet or Efficiency (temperatures are already high at light load)."
            : "Efficiency for cooler, quieter gaming with a small performance cost, or Maximum headroom if you want every bit of performance and your cooling is good.";
        lines.Add("Recommended: " + rec);
        lines.Add("This assessment doesn't load the system; it can't predict stability or gains. Measure with Test & Benchmark.");
        result.Text = string.Join("\n", lines);
    }

    // ---- manual ----------------------------------------------------------------------------------------------------------

    private void RenderManual()
    {
        _manual.Children.Clear();
        var pending = new Dictionary<string, Func<TuningSettings, TuningSettings>>();
        var resetters = new List<Action>();

        StackPanel SliderRow(string label, string tip, double min, double max, double step, double current, double? def, Func<double, string> fmt, Action<double> changed, string? rangeText = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6), ToolTip = tip };
            var head = new DockPanel();
            var val = Ui.Text(fmt(current), 12.5, FontWeights.SemiBold); DockPanel.SetDock(val, Dock.Right); head.Children.Add(val);
            head.Children.Add(Ui.Text(label, 12.5));
            sp.Children.Add(head);
            var s = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, Value = current };
            s.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue) + (Math.Abs(e.NewValue - current) > step / 2 ? $"  (now {fmt(current)})" : ""); changed(e.NewValue); };
            sp.Children.Add(s);
            sp.Children.Add(Ui.Muted(rangeText ?? $"Range {fmt(min)}–{fmt(max)}, step {fmt(step).Replace(" W", "").Replace(" MHz", "").Replace("%", "")}{(def is { } d ? $", default {fmt(d)}" : "")}.", 11));
            resetters.Add(() => s.Value = current);
            return sp;
        }

        if (Gpu is { } g)
        {
            _manual.Children.Add(Ui.Text(g.Name, 13, FontWeights.SemiBold));
            if (g.PowerLimitSupported)
                _manual.Children.Add(SliderRow("Power limit", "Maximum board power the GPU may draw. Lower = cooler, quieter, slower under heavy load. Changed with nvidia-smi -pl.",
                    g.MinPowerLimitW!.Value, g.MaxPowerLimitW!.Value, 1, g.PowerLimitW ?? g.DefaultPowerLimitW!.Value, g.DefaultPowerLimitW, v => $"{v:0} W",
                    v => pending["pl"] = s => s with { GpuPowerLimitW = Math.Abs(v - (g.PowerLimitW ?? 0)) < 0.5 ? null : v }));
            if (g.ClockLockSupported)
            {
                var clocks = g.GraphicsClocks;
                int cur = _state.LockedMaxGpuMhz ?? clocks.Max();
                _manual.Children.Add(SliderRow("GPU clock cap", "Highest core clock the GPU may use (nvidia-smi -lgc). Capping it cuts power and heat; the maximum means no cap. This is not an undervolt: the voltage curve is unchanged.",
                    0, clocks.Count - 1, 1, clocks.ToList().IndexOf(cur) is var ix and >= 0 ? ix : clocks.Count - 1, null, v => $"{clocks[(int)Math.Round(v)]} MHz",
                    v => { var mhz = clocks[(int)Math.Round(v)]; pending["clk"] = s => s with { GpuClockLock = mhz == clocks.Max() ? (_state.GpuClocksLocked ? (0, 0) : null) : (clocks.Min(), mhz) }; },
                    $"Supported clocks {clocks.Min()}–{clocks.Max()} MHz in {g.GraphicsStep} MHz steps (the driver's list). Maximum = no cap."));
            }
            if (g.MemoryLockSupported)
                _manual.Children.Add(Ui.Muted($"Memory clock locking is supported ({g.MemoryClocks.Min()}–{g.MemoryClocks.Max()} MHz) but gives no benefit for gaming, so it's only offered as a reset: " + (_state.MemClocksLocked ? "currently locked." : "not locked."), 11.5));
        }
        else _manual.Children.Add(Ui.Muted(_inv?.HasNvidia == true ? "nvidia-smi didn't report adjustable limits for this GPU." : "No NVIDIA GPU: GPU controls are in your GPU vendor's tool (see Everything else).", 12));

        _manual.Children.Add(Ui.Text("CPU (Windows power settings, active plan)", 13, FontWeights.SemiBold));
        if (_cpu.TryGetValue(CpuPowerSettings.BoostMode, out var bm) && bm.Readable && bm.Choices is { } choices)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6), ToolTip = bm.Tooltip };
            sp.Children.Add(Ui.Text("Boost mode", 12.5));
            var cb = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var c in choices) cb.Items.Add(c);
            cb.SelectedIndex = bm.Ac ?? 0;
            cb.SelectionChanged += (_, _) => pending["boost"] = s => s with { CpuBoostMode = cb.SelectedIndex == bm.Ac ? null : cb.SelectedIndex };
            resetters.Add(() => cb.SelectedIndex = bm.Ac ?? 0);
            sp.Children.Add(cb);
            sp.Children.Add(Ui.Muted($"Now: {bm.Describe(bm.Ac ?? 0)} (plugged in), {bm.Describe(bm.Dc ?? 0)} (battery).", 11));
            _manual.Children.Add(sp);
        }
        if (_cpu.TryGetValue(CpuPowerSettings.MaxState, out var ms) && ms.Readable)
            _manual.Children.Add(SliderRow(ms.Label, ms.Tooltip, ms.Min, ms.Max, Math.Max(1, ms.Step), ms.Ac!.Value, 100, v => $"{v:0}%",
                v => pending["max"] = s => s with { CpuMaxState = (int)v == ms.Ac ? null : (int)v }));
        if (_cpu.TryGetValue(CpuPowerSettings.Epp, out var ep) && ep.Readable)
            _manual.Children.Add(SliderRow(ep.Label, ep.Tooltip, ep.Min, ep.Max, Math.Max(1, ep.Step), ep.Ac!.Value, null, v => $"{v:0}%",
                v => pending["epp"] = s => s with { CpuEpp = (int)v == ep.Ac ? null : (int)v }));
        if (_cpu.Values.All(c => !c.Readable)) _manual.Children.Add(Ui.Muted("Windows didn't report these settings for the active power plan.", 12));

        _manual.Children.Add(Ui.Row(
            Ui.Btn("Apply…", async (_, _) =>
            {
                var s = pending.Values.Aggregate(new TuningSettings(), (acc, f) => f(acc));
                if (s.IsEmpty) { MainWindow.Current?.ShowToast("Nothing changed: every control is at its current value."); return; }
                await ApplyAsync(s, "Custom", "Change one thing at a time and test it before changing the next.");
            }, "PrimaryButton", new Thickness(0, 10, 8, 0)),
            Ui.Btn("Cancel", (_, _) => { foreach (var r in resetters) r(); pending.Clear(); }, "BaseButton", new Thickness(0, 10, 8, 0)),
            Ui.Btn("Reset to original", async (_, _) => await RevertAsync(), "GhostButton", new Thickness(0, 10, 8, 0))));
    }

    // ---- apply / revert ------------------------------------------------------------------------------------------------

    private async Task ApplyAsync(TuningSettings s, string name, string caveat)
    {
        if (_busy) return;
        var errors = TuningEngine.Validate(s, Gpu, _cpu);
        if (errors.Count > 0) { Dialog.Show("Can't apply these values", string.Join("\n", errors), DialogKind.Warning); return; }
        var preview = TuningEngine.Preview(s, Gpu, _cpu, _state);
        bool gpuChange = s.GpuPowerLimitW is not null || s.GpuClockLock is not null || s.MemClockLock is not null;
        if (!Dialog.Confirm($"Apply {name}?",
                string.Join("\n", preview.Select(p => "• " + p)) + $"\n\n{caveat}\n\nOriginal values are backed up first. Each value is read back; if any step fails, everything from this apply is undone. " +
                "Unstable settings can crash games or Windows and corrupt files being written. Save your work first." +
                (gpuChange ? "\n\nGPU changes reset when Windows restarts." : ""), "Apply", DialogKind.Warning)) return;
        if (gpuChange && !Admin.IsElevated)
        {
            if (Dialog.Confirm("Administrator rights needed", "nvidia-smi only changes GPU limits for administrators. Restart Money Tweaks as administrator? Nothing has been changed.", "Restart as admin", DialogKind.Warning)
                && Admin.RestartElevated()) Application.Current.Shutdown();
            return;
        }
        _busy = true;
        try
        {
            var (ok, results) = await Task.Run(() => TuningEngine.Apply(s, _state, name));
            Dialog.Show(ok ? "Applied" : "Not applied (rolled back)",
                string.Join("\n", results.Select(r => $"{r.Status}: {r.Item} {r.Value} – {r.Detail}")) +
                (ok ? "\n\nNext: run the quick scan, then an extended test before relying on it." : ""), ok ? DialogKind.Info : DialogKind.Warning);
        }
        finally { _busy = false; await ReloadValuesAsync(); }
    }

    private async Task RevertAsync()
    {
        if (_busy) return;
        if (!_state.HasChanges && !_state.BackupTaken) { MainWindow.Current?.ShowToast("Nothing to undo."); return; }
        if (!Admin.IsElevated && (_state.GpuClocksLocked || _state.MemClocksLocked || _state.LastApply.Any(r => r.Item.StartsWith("GPU", StringComparison.Ordinal))))
        {
            if (Dialog.Confirm("Administrator rights needed", "Undoing GPU changes needs administrator rights. Restart as administrator? (Restarting Windows also resets them.)", "Restart as admin", DialogKind.Warning)
                && Admin.RestartElevated()) Application.Current.Shutdown();
            return;
        }
        _busy = true;
        try
        {
            var (ok, log) = await Task.Run(() => TuningEngine.RevertAll(_state));
            Dialog.Show(ok ? "Original values restored" : "Restore incomplete", (log.Count == 0 ? "Everything was already at its original value." : string.Join("\n", log)), ok ? DialogKind.Info : DialogKind.Warning);
        }
        finally { _busy = false; await ReloadValuesAsync(); }
    }

    private async Task ReloadValuesAsync()
    {
        _nv = await Task.Run(NvidiaSmi.Limits);
        _cpu = await Task.Run(() => new[] { CpuPowerSettings.BoostMode, CpuPowerSettings.MaxState, CpuPowerSettings.Epp }.Select(CpuPowerSettings.Read).ToDictionary(x => x.Alias));
        RenderAll();
    }

    // ---- stability tests -----------------------------------------------------------------------------------------------

    private void RenderTests()
    {
        _test.Children.Clear();
        var gpuLoad = new CheckBox { Content = "Also load the GPU (opens the 3D stress scene)", IsChecked = Gpu is not null, Margin = new Thickness(0, 0, 0, 6) };
        var revertOnFail = new CheckBox { Content = "Undo tuning automatically if the test fails", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var progress = new ProgressBar { Maximum = 1, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };
        var live = Ui.Muted("", 12);
        var result = Ui.Text("", 12.5);
        var stop = Ui.Btn("Stop", (_, _) => _testCts?.Cancel(), "DangerButton", new Thickness(0, 6, 8, 0));
        stop.IsEnabled = false;
        var buttons = new List<Button>();
        Button Run(string label, TimeSpan d) { var b = Ui.Btn(label, async (_, _) => await TestAsync(d, gpuLoad.IsChecked == true, revertOnFail.IsChecked == true, progress, live, result, stop, buttons), "BaseButton", new Thickness(0, 6, 8, 0)); buttons.Add(b); return b; }
        _test.Children.Add(gpuLoad);
        _test.Children.Add(revertOnFail);
        _test.Children.Add(Ui.Row(Run("Quick scan (1 min)", TimeSpan.FromSeconds(StabilityTest.QuickSeconds)), Run("Extended (10 min)", TimeSpan.FromMinutes(10)),
            Run("Extended (30 min)", TimeSpan.FromMinutes(30)), Run("Extended (60 min)", TimeSpan.FromMinutes(60)), stop));
        _test.Children.Add(progress);
        _test.Children.Add(live);
        _test.Children.Add(result);
        _test.Children.Add(Ui.Muted($"Fails on: wrong CPU results, new WHEA hardware errors or GPU driver resets in the event log, GPU ≥ 87 °C or CPU ≥ 95 °C (when readable). The CPU load uses all {Environment.ProcessorCount} threads. Close other apps and save your work first.", 11.5));
    }

    private async Task TestAsync(TimeSpan duration, bool gpuLoad, bool revertOnFail, ProgressBar bar, TextBlock live, TextBlock result, Button stop, List<Button> buttons)
    {
        if (_busy) return;
        if (!Dialog.Confirm("Start stability test?", $"Full CPU load{(gpuLoad ? " and a GPU stress scene" : "")} for {(duration.TotalMinutes >= 1 ? $"{duration.TotalMinutes:0} minutes" : $"{duration.TotalSeconds:0} seconds")}. " +
                "The PC will get hot and loud. If it freezes or restarts, that is a failed test: undo the tuning afterwards. Save your work first.", "Start", DialogKind.Warning)) return;
        _busy = true;
        _testCts = new CancellationTokenSource();
        foreach (var b in buttons) b.IsEnabled = false;
        stop.IsEnabled = true;
        bar.Visibility = Visibility.Visible; bar.Value = 0;
        result.Text = "";
        var gpu = Gpu;
        Task<SceneResult>? scene = null;
        try
        {
            if (gpuLoad) scene = StressScene.RunAsync(new SceneOptions(4, (int)duration.TotalSeconds + 5, 1280, 720, "Money Tweaks stability test"), _testCts.Token);
            StabilitySample Sample()
            {
                var nv = gpu is null ? null : NvidiaSmi.Read(gpu.Index);
                var hw = LiveStats.Last;
                return new(DateTime.Now, hw?.CpuPercent, hw?.CpuTempC, nv?.TempC ?? hw?.GpuTempC, nv?.PowerW, nv?.CoreMhz, nv?.HardwareThrottling == true, nv?.PowerCapped == true);
            }
            var prog = new Progress<(double f, StabilitySample s)>(p =>
            {
                bar.Value = p.f;
                live.Text = $"CPU {p.s.CpuLoad:0}%{(p.s.CpuTempC is { } c ? $" {c:0} °C" : "")} · GPU {(p.s.GpuTempC is { } g ? $"{g:0} °C" : "n/a")} {(p.s.GpuPowerW is { } w ? $"{w:0} W" : "")} {(p.s.GpuCoreMhz is { } m ? $"{m} MHz" : "")}{(p.s.GpuHwThrottle ? " · ⚠ throttling" : "")}";
            });
            var r = await Task.Run(() => StabilityTest.RunAsync(duration, Sample, prog, _testCts.Token));
            _testCts.Cancel(); // stop the GPU scene too
            if (scene is not null) { try { await scene; } catch { } }
            var extra = new List<string>();
            if (r.MaxGpuMhz is { } mhz && _state.LockedMaxGpuMhz is { } cap && gpu is not null)
            {
                bool ok = mhz <= cap + gpu.GraphicsStep;
                extra.Add(ok ? $"GPU clock cap verified under load: highest clock {mhz} MHz ≤ {cap} MHz." : $"GPU reached {mhz} MHz although capped at {cap} MHz: the cap isn't in effect.");
                if (ok) MarkClockCapVerified();
            }
            if (r.ThrottleSamples > 0) extra.Add($"The GPU throttled (thermal or power brake) in {r.ThrottleSamples} sample(s).");
            result.Text = r.Summary + (extra.Count > 0 ? "\n" + string.Join("\n", extra) : "");
            result.Foreground = Ui.Brush(r.Failed ? "Danger" : r.Verdict == StabilityVerdict.Stopped ? "Gold" : "Success");
            _state.LastTestSummary = $"{DateTime.Now:g}: {r.Summary}";
            if (r.Failed && revertOnFail && _state.HasChanges)
            {
                var (ok, log) = await Task.Run(() => TuningEngine.RevertAll(_state));
                result.Text += "\n\n" + (ok ? "Tuning was undone automatically: original values restored and verified." : "Automatic undo had problems: " + string.Join("; ", log));
            }
            else if (r.Verdict == StabilityVerdict.NoErrorsExtended && _state.PendingConfirmation)
                _state.PendingConfirmation = false; // a clean extended run counts as confirmation; a quick scan does not
            _state.Save();
        }
        catch (Exception ex) { result.Text = "The test couldn't run: " + ex.Message; Log.Error("stability test", ex); }
        finally
        {
            _busy = false;
            foreach (var b in buttons) b.IsEnabled = true;
            stop.IsEnabled = false;
            bar.Visibility = Visibility.Collapsed;
            RenderBanner(); RenderStatus();
        }
    }

    private void MarkClockCapVerified()
    {
        _state.LastApply = _state.LastApply.Select(r => r.Item == "GPU clock lock" && r.Status == ItemStatus.Applied
            ? r with { Status = ItemStatus.Verified, Detail = "Verified under load during a stability test." } : r).ToList();
    }

    // ---- guided handoffs ------------------------------------------------------------------------------------------------

    private void RenderCaps()
    {
        _caps.Children.Clear();
        if (_inv is null) return;
        foreach (var area in TuningCapabilities.For(_inv, _nv, Afterburner.IsRunning).GroupBy(c => c.Area))
        {
            _caps.Children.Add(Ui.Text(area.Key, 13, FontWeights.SemiBold));
            foreach (var c in area)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                var head = new WrapPanel();
                head.Children.Add(Ui.Tag(c.Support switch { Support.Direct => "Here", Support.Handoff => "Guided", _ => "Not available" },
                    c.Support switch { Support.Direct => "Success", Support.Handoff => "Accent", _ => "Muted" }));
                head.Children.Add(Ui.Text(c.Name, 12.5, FontWeights.SemiBold));
                sp.Children.Add(head);
                sp.Children.Add(Ui.Muted(c.Why, 12));
                if (c.Steps is { Count: > 0 } steps) sp.Children.Add(Ui.Muted(string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}")), 11.5));
                if (c.Tool is { } toolName && _inv.Tool(toolName) is { } tool)
                    sp.Children.Add(Ui.Row(tool.Path is { } path
                        ? Ui.Btn($"Open {tool.Name}", (_, _) => Launch(path), "GhostButton", new Thickness(0, 4, 8, 0))
                        : Ui.Btn($"Get {tool.Name} (official site) ↗", (_, _) => Launch(tool.Url), "GhostButton", new Thickness(0, 4, 8, 0))));
                _caps.Children.Add(sp);
            }
        }
        _caps.Children.Add(Ui.Muted("Per-game tuning: choose a tuning profile in Gaming Profiles › Edit; it's applied while the game runs and put back afterwards.", 11.5));
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Show("Couldn't open it", $"{ex.Message}\n\n{target}", DialogKind.Error); }
    }
}
