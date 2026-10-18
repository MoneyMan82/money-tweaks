using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

/// <summary>"Find my best overclock": the automatic GPU overclock scan, run through MSI Afterburner under the 3D stress
/// scene. CPUs have no vendor-supported interface for other apps, so the CPU part points to the vendor's own auto-OC.</summary>
public static class TuningViewAutoScan
{
    private static CancellationTokenSource? _cts;
    public static bool Running => _cts is not null;

    public static void Render(StackPanel host, TuningState st, TuningInventory? inv, NvidiaGpuLimits? gpu, Func<Task> refresh)
    {
        host.Children.Clear();
        host.Children.Add(Ui.Text("Find my best GPU overclock (automatic scan)", 14, FontWeights.SemiBold));
        host.Children.Add(Ui.Muted("Raises the GPU core clock step by step under a 3D load until it fails, then the memory clock, takes a safety margin off and confirms the result with a longer run. " +
            "About 15–25 minutes. Failures are detected from GPU driver resets, the load crashing and the GPU no longer responding.", 12));

        if (inv?.HasNvidia != true)
        {
            host.Children.Add(Ui.Muted(inv?.HasAmdGpu == true
                ? "AMD GPU: use Adrenalin › Performance › Tuning › Automatic (Overclock GPU / Undervolt GPU); it's AMD's own scan and Money Tweaks has no supported way to do it."
                : "No NVIDIA GPU found, so this scan isn't available.", 12));
            AddCpuNote(host, inv);
            return;
        }

        var log = Ui.Muted("", 12);
        var bar = new ProgressBar { Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };
        var stop = Ui.Btn("Stop scan", (_, _) => _cts?.Cancel(), "DangerButton", new Thickness(0, 8, 8, 0));
        stop.IsEnabled = Running;
        var start = Ui.Btn(Running ? "Scan running…" : "Start automatic scan…", async (s, _) => await StartAsync((Button)s, stop, st, gpu, bar, log, refresh), "PrimaryButton", new Thickness(0, 8, 8, 0));
        start.IsEnabled = !Running;
        host.Children.Add(Ui.Row(start, stop));
        host.Children.Add(bar);
        host.Children.Add(log);
        host.Children.Add(Ui.Muted("Needs MSI Afterburner (Money Tweaks starts it) and administrator rights. Only the core and memory clock offsets are changed; power limit, voltage and fans stay as they are. " +
            "Stopping or a failed scan restores the settings from before the scan.", 11.5));
        AddCpuNote(host, inv);
    }

    /// <summary>Same identity-checked detection as the Game Library and the synthetic benchmark.</summary>
    internal static List<string> RunningGames()
    {
        var games = Core.Games.GameLibrary.Items(Core.Games.GameLibrary.Current);
        return Core.Games.GameRuntime.Statuses(games).Where(s => s.status == Core.Games.GameStatus.Running).Select(s => s.game.Def.Name).ToList();
    }

    private static void AddCpuNote(StackPanel host, TuningInventory? inv)
    {
        string cpu = inv?.IsRyzen == true ? "AMD Ryzen Master (Precision Boost Overdrive, Curve Optimizer test or Auto Overclocking)"
            : inv?.IsIntelCpu == true ? (inv.IntelUnlocked ? "Intel XTU (Speed Optimizer)" : "nothing: this Intel CPU's multiplier is locked")
            : "the CPU vendor's tool";
        host.Children.Add(Ui.Muted($"CPU: see CPU auto tuning below (boost scan and the per-core undervolt assistant). AMD and Intel don't let other apps overclock the CPU directly (only undocumented registers would, which Money Tweaks never touches); the CPU's own automatic overclock is in {cpu}.", 11.5));
    }

    private static async Task StartAsync(Button start, Button stop, TuningState st, NvidiaGpuLimits? gpu, ProgressBar bar, TextBlock log, Func<Task> refresh)
    {
        if (Running) return;
        if (!Admin.IsElevated)
        {
            if (Dialog.Confirm("Administrator rights needed", "The scan changes clock offsets through MSI Afterburner, which runs as administrator. Restart Money Tweaks as administrator? Nothing has been changed.", "Restart as admin", DialogKind.Warning)
                && Admin.RestartElevated()) Application.Current.Shutdown();
            return;
        }
        if (!await TuningViewAfterburner.EnsureConnectedAsync()) return;
        var g = await Task.Run(Afterburner.Read);
        if (g?.CoreOffset is not { Adjustable: true } core) { Dialog.Show("Can't scan this GPU", "MSI Afterburner reports no adjustable core clock offset for this GPU.", DialogKind.Warning); return; }
        var games = await Task.Run(RunningGames);
        if (games.Count > 0) { Dialog.Show("Close your games first", "A game is running (" + string.Join(", ", games) + "). The scan needs the GPU to itself, and a failing step can crash whatever is running.", DialogKind.Warning); return; }
        var o = ScanOptions.For(g);
        if (!Dialog.Confirm("Start the automatic overclock scan?",
                $"• Core clock offset: tested from +0 up to +{o.CoreMax} MHz in {o.CoreStep} MHz steps\n" +
                (o.ScanMemory ? $"• Memory clock offset: tested up to +{o.MemMax} MHz in {o.MemStep} MHz steps\n" : "") +
                $"• Each step runs the 3D stress scene for {o.Step.TotalSeconds:0} s; the result is confirmed with a {o.Confirm.TotalMinutes:0}-minute run\n\n" +
                "Finding the limit means going past it: expect the screen to flicker or go black for a few seconds once or twice while the driver recovers. In rare cases Windows can freeze; then restart, and Money Tweaks offers to undo everything.\n\n" +
                "Save your work and close games and video apps. Don't use the PC during the scan.", "Start scan", DialogKind.Warning)) return;

        var before = await Task.Run(() => Afterburner.Control.Read());
        if (before is null) { Dialog.Show("MSI Afterburner", "Couldn't read Afterburner's current settings, so the scan didn't start.", DialogKind.Warning); return; }
        _cts = new CancellationTokenSource();
        start.IsEnabled = false; start.Content = "Scan running…"; stop.IsEnabled = true;
        bar.Visibility = Visibility.Visible; bar.Maximum = o.MaxSteps; bar.Value = 0;
        var lines = new List<string>();
        var progress = new Progress<(int step, string text)>(p =>
        {
            bar.Value = Math.Min(bar.Maximum, p.step);
            lines.Add(p.text);
            log.Text = string.Join("\n", lines.TakeLast(8));
        });
        await using var backend = new LiveScanBackend(st, gpu?.Index ?? 0);
        ScanResult r;
        try { r = await AutoOcScanner.RunAsync(backend, o, progress, _cts.Token); }
        catch (Exception ex) { Log.Error("auto oc scan", ex); r = new(ScanOutcome.Aborted, 0, 0, null, null, "The scan failed: " + ex.Message, lines); }
        finally { _cts.Dispose(); _cts = null; }

        if (!r.KeepsOffsets)
        {
            var (ok, msg) = await Task.Run(() => Afterburner.Restore(before));
            if (ok && r.Outcome is ScanOutcome.Cancelled or ScanOutcome.Aborted) { st.PendingConfirmation = false; st.ProfileName = "Custom"; }
            r = r with { Summary = r.Summary + (ok ? " Settings from before the scan restored and verified." : " Restoring the previous settings failed: " + msg + " Use Undo all tuning.") };
        }
        else
        {
            st.ProfileName = $"Auto scan: core {r.CoreMhz:+0;-0;0}, memory {r.MemMhz:+0;-0;0} MHz";
            st.PendingConfirmation = true; // a scan never counts as confirmation; an extended test does
        }
        st.LastTestSummary = $"{DateTime.Now:g}: automatic scan: {r.Summary}";
        st.Save();
        Log.Info("auto oc scan: " + r.Outcome + " core " + r.CoreMhz + " mem " + r.MemMhz);
        bar.Visibility = Visibility.Collapsed; stop.IsEnabled = false;
        log.Text = string.Join("\n", lines.TakeLast(8));
        Dialog.Show(r.Outcome switch { ScanOutcome.Found => "Overclock found", ScanOutcome.Cancelled => "Scan stopped", _ => "No overclock applied" },
            r.Summary + (r.KeepsOffsets ? "\n\nNext: run Stability tests › Extended (30 min) with the GPU load. If it fails, the tuning is undone automatically." : ""),
            r.KeepsOffsets ? DialogKind.Info : DialogKind.Warning);
        await refresh();
    }

    /// <summary>Real hardware: offsets through Afterburner (verified read-back), one stress scene running for the whole
    /// scan, nvidia-smi samples and GPU driver resets (Display 4101) from the event log.</summary>
    private sealed class LiveScanBackend(TuningState st, int gpuIndex) : IOcScanBackend, IAsyncDisposable
    {
        private CancellationTokenSource? _sceneCts;
        private Task<SceneResult>? _scene;

        private void EnsureScene()
        {
            if (_scene is { IsCompleted: false }) return;
            _sceneCts?.Dispose();
            _sceneCts = new CancellationTokenSource();
            _scene = StressScene.RunAsync(new SceneOptions(4, 4 * 3600, 1280, 720, "Money Tweaks overclock scan"), _sceneCts.Token);
        }

        public async Task<bool> SetOffsetsAsync(int core, int mem)
        {
            var g = await Task.Run(Afterburner.Read);
            if (g is null) return false;
            var c = new Afterburner.Change { CoreOffsetMhz = core, MemOffsetMhz = g.MemOffset?.Adjustable == true ? mem : null };
            var (ok, _) = await Task.Run(() => TuningEngine.ApplyAfterburner(c, st, $"Auto scan in progress (core {core:+0;-0;0}, memory {mem:+0;-0;0} MHz)"));
            return ok;
        }

        public async Task<ScanStepResult> LoadAsync(TimeSpan duration, CancellationToken ct)
        {
            EnsureScene();
            var since = DateTime.Now;
            var end = since + duration;
            double? maxTemp = null; var clocks = new List<int>(); int missed = 0;
            while (DateTime.Now < end)
            {
                await Task.Delay(2000, ct);
                if (_scene!.IsCompleted)
                {
                    var sr = await _scene;
                    if (sr.StopReason is "Stopped by user" or "Window closed") throw new OperationCanceledException();
                    return new(false, "the 3D load stopped (" + (sr.StopReason ?? "ended") + ")", MaxTempC: maxTemp, AvgCoreMhz: Avg(clocks));
                }
                var nv = await Task.Run(() => NvidiaSmi.Read(gpuIndex), ct);
                if (nv?.CoreMhz is null) { if (++missed >= 3) return new(false, "the GPU stopped responding to the driver", MaxTempC: maxTemp, AvgCoreMhz: Avg(clocks)); continue; }
                missed = 0;
                clocks.Add(nv.CoreMhz.Value);
                if (nv.TempC is { } t) maxTemp = Math.Max(maxTemp ?? 0, t);
                if (maxTemp >= 87) return new(false, $"GPU reached {maxTemp:0} °C (limit 87 °C). Improve cooling before overclocking.", Fatal: true, MaxTempC: maxTemp, AvgCoreMhz: Avg(clocks));
                var (_, resets) = await Task.Run(() => StabilityTest.CountEventsSince(since), ct);
                if (resets > 0) return new(false, "GPU driver reset (the driver crashed and recovered)", MaxTempC: maxTemp, AvgCoreMhz: Avg(clocks));
            }
            return new(true, MaxTempC: maxTemp, AvgCoreMhz: Avg(clocks));
        }

        public async Task RecoverAsync()
        {
            // Drop back to no overclock while the driver settles, and restart the load if the failure killed it.
            await SetOffsetsAsync(0, 0);
            await Task.Delay(8000);
        }

        private static int? Avg(List<int> l) => l.Count == 0 ? null : (int)l.Average();

        public async ValueTask DisposeAsync()
        {
            _sceneCts?.Cancel();
            if (_scene is not null) { try { await _scene; } catch { } }
            _sceneCts?.Dispose();
        }
    }
}
