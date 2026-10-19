using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Advisor;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

public sealed partial class AdvisorView
{
    private int _seconds = 45;
    private readonly TextBlock _live = Ui.Muted("", 12);
    private readonly ProgressBar _bar = new() { Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 4) };

    private void RenderMeasure()
    {
        _measure.Children.Clear();
        _measure.Children.Add(Ui.Section("Measure", "The diagnosis comes from what your PC does while the game runs: GPU and per-core CPU load, clocks, temperatures, power limits, memory, paging, disk response and every frame time."));
        var dur = new SegmentedPicker([("30 s", "30", null), ("45 s", "45", null), ("90 s", "90", null)], _seconds.ToString(CultureInfo.InvariantCulture));
        dur.Changed += v => _seconds = int.Parse(v, CultureInfo.InvariantCulture);
        _measure.Children.Add(dur);
        var play = Ui.Btn("Measure while playing", async (_, _) => await MeasureGameAsync(), "PrimaryButton", new Thickness(0, 10, 8, 0));
        var bench = Ui.Btn("Run the built-in benchmark instead", async (_, _) => await MeasureBenchmarkAsync(), "BaseButton", new Thickness(0, 10, 8, 0));
        var stop = Ui.Btn("Stop", (_, _) => _cts?.Cancel(), "DangerButton", new Thickness(0, 10, 8, 0));
        play.IsEnabled = bench.IsEnabled = _cts is null; stop.IsEnabled = _cts is not null;
        _measure.Children.Add(Ui.Row(play, bench, stop));
        _measure.Children.Add(_bar);
        _measure.Children.Add(_live);
        _measure.Children.Add(Ui.Muted((Admin.IsElevated ? "" : "Frame timing (FPS, 1% lows, stutter, frame caps) needs administrator rights; without it the diagnosis uses your FPS number and hardware signals only. ") +
            "Play normally in a typical scene during the capture (the same kind of moment you care about). Nothing is injected into the game; frame timing is read from Windows' Present events.", 11.5));
    }

    private async Task MeasureGameAsync()
    {
        if (_cts is not null) return;
        await RefreshGamesAsync();
        var g = _store.Goal;
        var running = _games.FirstOrDefault(x => x.pid is not null && (g.GameKey is null || x.g.Key == g.GameKey));
        if (running.pid is not { } pid) { Dialog.Show("Start the game first", g.Game.Length > 0 ? $"{g.Game} isn't running (or isn't in your Game Library). Start it, get into a typical scene, then measure." : "Pick your game above, start it, then measure."); return; }
        if (g.GameKey is null) SetGame(running.g.Def.Name);
        if (!Dialog.Confirm("Ready?", $"Get into a typical scene in {running.g.Def.Name}. After you press Start you have 8 seconds to switch to the game; a sound marks the start and end of the {_seconds}-second capture.", "Start", DialogKind.Info)) return;
        _cts = new CancellationTokenSource();
        RenderMeasure();
        _bar.Visibility = Visibility.Visible; _bar.Maximum = _seconds; _bar.Value = 0;
        try
        {
            for (int i = 8; i > 0; i--) { _live.Text = $"Capture starts in {i} s: switch to the game…"; await Task.Delay(1000, _cts.Token); }
            SystemSounds.Asterisk.Play();
            var progress = new Progress<(int second, TelemetrySample s)>(p => { _bar.Value = p.second; _live.Text = LiveLine(p.second, p.s); });
            _capture = await GameCapture.RunAsync(pid, running.g.Def.Name, _seconds, StressScene.RefreshRate(), progress, _cts.Token);
            SystemSounds.Exclamation.Play();
            Analyze();
        }
        catch (OperationCanceledException) { _live.Text = "Stopped. Nothing was analysed."; }
        catch (Exception ex) { _live.Text = "Measuring failed: " + ex.Message; Log.Error("advisor capture", ex); }
        finally { _cts?.Dispose(); _cts = null; _bar.Visibility = Visibility.Collapsed; RenderMeasure(); }
    }

    /// <summary>The supported benchmark: the built-in 3D scene, sampled with the same probe (not game-specific: the result says so).</summary>
    private async Task MeasureBenchmarkAsync()
    {
        if (_cts is not null) return;
        var games = await Task.Run(TuningViewAutoScan.RunningGames);
        if (games.Count > 0) { Dialog.Show("Close your games first", $"{string.Join(", ", games)} is running and would distort the benchmark."); return; }
        _cts = new CancellationTokenSource();
        RenderMeasure();
        _bar.Visibility = Visibility.Visible; _bar.Maximum = _seconds; _bar.Value = 0;
        try
        {
            var cap = new Capture { Source = "benchmark", What = "Built-in 3D scene", Seconds = _seconds, RefreshHz = StressScene.RefreshRate() };
            var before = GameCapture.ProcessTimes();
            using var probe = await Task.Run(() => new TelemetryProbe(Environment.ProcessId));
            var scene = StressScene.RunAsync(new SceneOptions(5, _seconds, 1920, 1080, "Upgrade Advisor · benchmark"), _cts.Token);
            await Task.Delay(StressScene.WarmupSec * 1000, _cts.Token);
            for (int i = 0; i < _seconds && !scene.IsCompleted; i++)
            {
                await Task.Delay(1000, _cts.Token);
                var s = await Task.Run(() => probe.Sample(0));
                cap.Samples.Add(s);
                _bar.Value = i + 1; _live.Text = LiveLine(i + 1, s);
            }
            var r = await scene;
            if (!r.Completed) throw new OperationCanceledException();
            cap.FrameMs.AddRange(r.FrameMs);
            for (int i = 0; i < cap.Samples.Count && i < r.FpsPerSecond.Count; i++) cap.Samples[i] = cap.Samples[i] with { Frames = (int)r.FpsPerSecond[i] };
            cap.TopBackground = GameCapture.TopBackground(before, GameCapture.ProcessTimes(), Environment.ProcessId, _seconds);
            _capture = cap;
            Analyze();
        }
        catch (OperationCanceledException) { _live.Text = "Stopped. Nothing was analysed."; }
        catch (Exception ex) { _live.Text = "The benchmark failed: " + ex.Message; Log.Error("advisor benchmark", ex); }
        finally { _cts?.Dispose(); _cts = null; _bar.Visibility = Visibility.Collapsed; RenderMeasure(); }
    }

    private static string LiveLine(int second, TelemetrySample s) =>
        $"{second} s · {(s.Frames > 0 ? $"{s.Frames} FPS · " : "")}GPU {(s.GpuUtil is { } g ? $"{g:0}%" : "n/a")}{(s.GpuTempC is { } gt ? $" {gt:0} °C" : "")} · busiest core {s.CpuBusiestCore:0}% · CPU {s.CpuTotal:0}%{(s.CpuTempC is { } ct ? $" {ct:0} °C" : "")} · RAM free {s.RamAvailableMb / 1024:0.0} GB" +
        (s.DiskLatencyMs is { } dl && dl > 5 ? $" · disk {dl:0} ms" : "");

    private void Analyze()
    {
        if (_capture is null || _hw is null) return;
        _dx = BottleneckEngine.Diagnose(_capture, _hw, _store.Goal);
        _res = RecommendationEngine.Recommend(_dx, _hw, _store.Goal, _store.Prices);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm} · {_capture.What} · {(_dx.MeasuredFps is { } f ? $"{f:0} FPS measured" : "FPS not measured")} · {_dx.Primary.Label} ({_dx.Primary.Confidence}) · target {_store.Goal.TargetFps:0}";
        if (_store.History.FirstOrDefault() != line) { _store.History.Insert(0, line); if (_store.History.Count > 20) _store.History.RemoveAt(20); _store.Save(); }
        RenderResults();
    }
}
