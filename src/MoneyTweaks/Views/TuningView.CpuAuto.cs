using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

/// <summary>
/// CPU auto tuning. (1) Boost scan: measures Windows' boost/EPP combinations and recommends one (applied only through the
/// normal preview → confirm → backup → read-back path). (2) Curve Optimizer / undervolt assistant: Money Tweaks tests
/// every core on its own and decides the next value per core; the user sets it in Ryzen Master or XTU, because AMD and
/// Intel offer no interface for other apps to change it.
/// </summary>
public static class TuningViewCpuAuto
{
    private static CancellationTokenSource? _cts;
    public static bool Running => _cts is not null;

    public static void Render(StackPanel host, TuningInventory? inv, Func<TuningSettings, string, string, Task> apply)
    {
        host.Children.Clear();
        RenderBoostScan(host, apply);
        host.Children.Add(new Border { Height = 1, Background = Ui.Brush("BorderBrush"), Margin = new Thickness(0, 14, 0, 12) });
        RenderCurveAssistant(host, inv);
    }

    // ---- 1. boost scan -------------------------------------------------------------------------------------------------

    private static void RenderBoostScan(StackPanel host, Func<TuningSettings, string, string, Task> apply)
    {
        host.Children.Add(Ui.Text("Find the best CPU boost settings (automatic)", 14, FontWeights.SemiBold));
        host.Children.Add(Ui.Muted($"Measures {CpuAutoTune.Candidates.Count} Windows boost/power combinations under real load (one thread and all threads), with temperature and power when MSI Afterburner or HWiNFO runs, " +
            "and recommends the coolest one that is just as fast. About 3 minutes. Your settings are put back afterwards; you choose what to apply.", 12));
        var status = Ui.Muted("", 12);
        var table = new StackPanel();
        var bar = new ProgressBar { Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };
        var stop = Ui.Btn("Stop", (_, _) => _cts?.Cancel(), "DangerButton", new Thickness(0, 8, 8, 0));
        stop.IsEnabled = false;
        Button? start = null;
        start = Ui.Btn("Start CPU boost scan…", async (_, _) =>
        {
            if (Running) return;
            var games = await Task.Run(TuningViewAutoScan.RunningGames);
            if (games.Count > 0) { Dialog.Show("Close your games first", "A game is running (" + string.Join(", ", games) + "); it would distort the measurements.", DialogKind.Warning); return; }
            if (!Dialog.Confirm("Start the CPU boost scan?", "Money Tweaks loads the CPU fully for about 3 minutes and briefly switches Windows' processor boost settings for each measurement. " +
                    "Your settings are put back at the end (or when you press Stop). Close other apps for accurate numbers.", "Start", DialogKind.Info)) return;
            _cts = new CancellationTokenSource();
            start!.IsEnabled = false; stop.IsEnabled = true; bar.Visibility = Visibility.Visible; table.Children.Clear();
            try
            {
                var backend = await Task.Run(() => new LiveCpuTuneBackend());
                if (!backend.Supported) { status.Text = "Windows doesn't report the boost settings for the active power plan, so they can't be tuned."; return; }
                var progress = new Progress<(int step, int total, string text)>(p => { bar.Maximum = p.total; bar.Value = p.step; status.Text = p.text; });
                var r = await CpuAutoTune.RunAsync(backend, progress, _cts.Token);
                status.Text = r.Summary;
                if (r.Completed) ShowResults(table, r, apply);
            }
            catch (Exception ex) { status.Text = "The scan failed: " + ex.Message; Log.Error("cpu boost scan", ex); }
            finally { _cts?.Dispose(); _cts = null; start!.IsEnabled = true; stop.IsEnabled = false; bar.Visibility = Visibility.Collapsed; }
        }, "PrimaryButton", new Thickness(0, 8, 8, 0));
        host.Children.Add(Ui.Row(start, stop));
        host.Children.Add(bar);
        host.Children.Add(status);
        host.Children.Add(table);
    }

    private static void ShowResults(StackPanel table, CpuTuneResult r, Func<TuningSettings, string, string, Task> apply)
    {
        foreach (var x in r.Results.OrderByDescending(x => x.ScorePct))
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (x == r.Recommended) row.Children.Add(Ui.Tag("Recommended", "Success"));
            else if (x == r.Fastest) row.Children.Add(Ui.Tag("Fastest", "Accent"));
            row.Children.Add(Ui.Text($"{x.Candidate.Name}: one thread {x.SinglePct - 100:+0.#;-0.#;0}%, all threads {x.MultiPct - 100:+0.#;-0.#;0}%" +
                (x.Measure.MaxTempC is { } t ? $", {t:0} °C" : "") + (x.Measure.AvgPowerW is { } w ? $", {w:0} W" : ""), 12.5));
            row.ToolTip = $"{x.Candidate.Why} Boost mode {CpuPowerSettings.BoostChoices[x.Candidate.BoostMode]}, energy preference {x.Candidate.Epp}%. Percentages are vs. your current settings; this run's noise was ±{r.NoisePct:0.#}%.";
            table.Children.Add(row);
        }
        Button ApplyBtn(CpuCandidateResult c, string label) => Ui.Btn(label, async (_, _) =>
            await apply(new TuningSettings { CpuBoostMode = c.Candidate.BoostMode, CpuEpp = c.Candidate.Epp }, c.Candidate.Name + " (CPU boost scan)",
                $"Measured: one thread {c.SinglePct - 100:+0.#;-0.#;0}%, all threads {c.MultiPct - 100:+0.#;-0.#;0}% vs. your current settings (noise ±{r.NoisePct:0.#}%)."),
            "BaseButton", new Thickness(0, 8, 8, 0));
        var buttons = new List<UIElement>();
        if (r.Recommended is { } rec) buttons.Add(ApplyBtn(rec, $"Apply {rec.Candidate.Name}…"));
        if (r.Fastest is { } f && f != r.Recommended) buttons.Add(ApplyBtn(f, $"Apply {f.Candidate.Name} (fastest)…"));
        table.Children.Add(Ui.Row(buttons.ToArray()));
    }

    // ---- 2. Curve Optimizer / undervolt assistant ------------------------------------------------------------------------

    private static void RenderCurveAssistant(StackPanel host, TuningInventory? inv)
    {
        bool amd = inv?.IsRyzen == true, intel = inv?.IsIntelCpu == true;
        host.Children.Add(Ui.Text(amd ? "Curve Optimizer assistant (per-core undervolt)" : "Undervolt assistant", 14, FontWeights.SemiBold));
        if (!amd && !(intel && inv!.IntelUnlocked))
        {
            host.Children.Add(Ui.Muted(intel ? "This Intel CPU has a locked multiplier; XTU's voltage controls are usually locked on it too." : "Available on AMD Ryzen (Curve Optimizer) and unlocked Intel CPUs (XTU).", 12));
            return;
        }
        var rules = amd ? CurveRules.Amd : CurveRules.Intel;
        var cores = CurveAssistant.PhysicalCores();
        var st = CurveState.Load();
        int slots = amd ? cores.Count : 1;
        if (st.Values.Count != slots || st.PerCore != amd) { st.Reset(slots, amd); st.Save(); }

        host.Children.Add(Ui.Muted((amd
            ? "A negative Curve Optimizer value lowers each core's voltage, so it runs cooler and often boosts higher. Every core tolerates a different amount, and the only way to know is to test each core on its own. "
            : "A negative core voltage offset makes the CPU run cooler and can keep boost clocks up longer. ") +
            $"Money Tweaks tests and decides the next value; you set it in {rules.Tool}, because {(amd ? "AMD" : "Intel")} lets no other app change it.", 12));

        var tool = inv!.Tool(amd ? "AMD Ryzen Master" : "Intel XTU");
        var body = new StackPanel();
        host.Children.Add(body);
        void Redraw() { body.Children.Clear(); Draw(body, st, rules, cores, amd, tool, Redraw); }
        Redraw();
    }

    private static void Draw(StackPanel body, CurveState st, CurveRules rules, List<int> cores, bool amd, TuningTool? tool, Action redraw)
    {
        // The PC crashed or restarted during a core's test: that core failed.
        if (st.InProgressCore is { } crashed && !Running)
        {
            st.InProgressCore = null;
            var advice = CurveAssistant.Advise(st, [new(crashed, crashed < cores.Count ? cores[crashed] : crashed, false, "the PC crashed or restarted during its test", 0)], rules);
            if (advice.Changed) st.Pending = advice.Next;
            st.Save();
            body.Children.Add(Banner($"The last test didn't finish: the PC crashed or restarted while testing {(amd ? $"core {crashed}" : "the CPU")}. That counts as a failure.\n" + string.Join("\n", advice.Lines)));
        }

        // Step 1: values to set.
        if (st.Pending is { } pending)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text($"Set these in {rules.Tool}, apply, then come back:", 13, FontWeights.SemiBold));
            sp.Children.Add(Ui.Muted(string.Join("   ", pending.Select((v, i) => $"{(amd ? $"Core {i}" : "Offset")}: {CurveAssistant.Fmt(v, rules)}" + (v != st.Values[i] ? " ←" : ""))), 12.5));
            sp.Children.Add(Ui.Row(
                Ui.Btn("I've set these values", (_, _) => { st.Values = pending.ToList(); st.Pending = null; st.Save(); redraw(); }, "PrimaryButton", new Thickness(0, 8, 8, 0)),
                OpenTool(tool)));
            body.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Accent"), Margin = new Thickness(0, 8, 0, 8), Child = sp });
        }

        // Current values (editable, in case they were changed elsewhere) and status per slot.
        body.Children.Add(Ui.Text("Values set now" + (st.Pending is null ? "" : " (before the change above)"), 12.5, FontWeights.SemiBold));
        var boxes = new List<TextBox>();
        var grid = new WrapPanel();
        for (int i = 0; i < st.Values.Count; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 14, 4) };
            cell.Children.Add(Ui.Muted(amd ? $"Core {i} " : "Core voltage offset ", 12));
            var tb = new TextBox { Text = st.Values[i].ToString(), Width = 52, IsEnabled = st.Pending is null && !Running };
            boxes.Add(tb);
            cell.Children.Add(tb);
            if (!string.IsNullOrEmpty(rules.Unit)) cell.Children.Add(Ui.Muted(rules.Unit, 12));
            var last = st.LastResults.FirstOrDefault(r => !amd || r.Core == i);
            string mark = st.Done[i] ? " ✓ done" : last is null ? "" : (amd ? last.Passed : st.LastResults.All(r => r.Passed)) ? " passed" : " failed";
            if (mark.Length > 0) cell.Children.Add(Ui.Muted(mark, 12));
            grid.Children.Add(cell);
        }
        body.Children.Add(grid);

        // Test round.
        var progress = Ui.Muted("", 12);
        var bar = new ProgressBar { Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };
        var stop = Ui.Btn("Stop", (_, _) => _cts?.Cancel(), "DangerButton", new Thickness(0, 8, 8, 0));
        stop.IsEnabled = Running;
        var buttons = new List<Button>();
        Button Round(string label, TimeSpan perCore)
        {
            var b = Ui.Btn(label, async (_, _) => await RoundAsync(st, rules, cores, amd, perCore, boxes, buttons, stop, bar, progress, redraw), "BaseButton", new Thickness(0, 8, 8, 0));
            b.IsEnabled = st.Pending is null && !Running;
            buttons.Add(b);
            return b;
        }
        int n = cores.Count;
        body.Children.Add(Ui.Row(Round($"Test every core ({n} × 1 min)", TimeSpan.FromMinutes(1)), Round($"Long test ({n} × 5 min)", TimeSpan.FromMinutes(5)), stop,
            Ui.Btn("Start over", (_, _) =>
            {
                if (Running || !Dialog.Confirm("Start over?", "Forgets the values, results and failures recorded so far (nothing on the PC changes). Set everything back to 0 in the tool first.", "Start over", DialogKind.Warning)) return;
                st.Reset(st.Values.Count, st.PerCore); st.Save(); redraw();
            }, "GhostButton", new Thickness(0, 8, 8, 0)), OpenTool(tool)));
        body.Children.Add(bar);
        body.Children.Add(progress);
        if (st.Done.All(d => d) && st.Done.Count > 0)
            body.Children.Add(Ui.Text($"All {(amd ? "cores are" : "values are")} done. Run a Long test once more, then put the same values in the BIOS ({(amd ? "AMD Overclocking › Precision Boost Overdrive › Curve Optimizer" : "CPU core voltage offset")}) to make them permanent. Play for a few days and step back by {rules.Step} if anything crashes or idles badly.", 12.5));
        body.Children.Add(Ui.Muted("How it works: start at 0, test, set what Money Tweaks suggests, test again. A core that passes goes one step further; a core that fails goes back one step and never past that again. " +
            "Each core is tested alone because one busy core boosts highest, which is where too much undervolt shows. Fails on wrong results, WHEA hardware errors, 95 °C, or a crash/restart during the test (it remembers which core was running). " +
            (amd ? "Too much undervolt can also show up only at idle or light load (random restarts while browsing): if that happens, step that core back. Ryzen Master's settings may not survive a restart; check them after one." : ""), 11.5));
    }

    private static async Task RoundAsync(CurveState st, CurveRules rules, List<int> cores, bool amd, TimeSpan perCore, List<TextBox> boxes, List<Button> buttons, Button stop,
        ProgressBar bar, TextBlock progress, Action redraw)
    {
        if (Running) return;
        var values = new List<int>();
        foreach (var tb in boxes)
        {
            if (!int.TryParse(tb.Text.Trim(), out var v) || v > 30 || v < rules.Floor - 20) { Dialog.Show("Check the values", "Every value must be a whole number, as set in the tool.", DialogKind.Warning); return; }
            values.Add(v);
        }
        if (!values.SequenceEqual(st.Values))
        {
            // Edited by hand: changed slots start fresh (old results were for other values).
            for (int i = 0; i < values.Count; i++) if (values[i] != st.Values[i]) st.Done[i] = false;
            st.Values = values;
        }
        var games = await Task.Run(TuningViewAutoScan.RunningGames);
        if (games.Count > 0) { Dialog.Show("Close your games first", "A game is running (" + string.Join(", ", games) + ").", DialogKind.Warning); return; }
        var total = TimeSpan.FromTicks(perCore.Ticks * cores.Count);
        if (!Dialog.Confirm("Test every core?", $"Testing {cores.Count} cores one at a time, {perCore.TotalMinutes:0} min each ({total.TotalMinutes:0} min), with these values set in the tool:\n" +
                string.Join(", ", st.Values.Select((v, i) => (amd ? $"core {i} " : "") + CurveAssistant.Fmt(v, rules))) +
                "\n\nAn unstable value can freeze or restart the PC: save your work first. If that happens, open this page again and Money Tweaks counts it as that core's failure.", "Start", DialogKind.Warning)) return;

        _cts = new CancellationTokenSource();
        foreach (var b in buttons) b.IsEnabled = false;
        stop.IsEnabled = true;
        bar.Visibility = Visibility.Visible; bar.Maximum = cores.Count; bar.Value = 0;
        var results = new List<CoreTestResult>();
        try
        {
            for (int i = 0; i < cores.Count; i++)
            {
                progress.Text = $"Core {i} (CPU {cores[i]})… {perCore.TotalMinutes:0} min";
                st.InProgressCore = amd ? i : 0; st.Save();   // survives a crash: the next visit knows which core was running
                var r = await CurveAssistant.TestCoreAsync(i, cores[i], perCore, _cts.Token, cpuTemp: () => Afterburner.CpuTemperature() ?? HwInfo.CpuTemperature());
                st.InProgressCore = null; st.Save();
                results.Add(r);
                bar.Value = i + 1;
                progress.Text = string.Join("   ", results.Select(x => $"Core {x.Core}: {(x.Passed ? "passed" : "failed")}"));
            }
            st.LastResults = results;
            st.LastRound = DateTime.Now;
            var advice = CurveAssistant.Advise(st, results, rules);
            if (advice.Changed) st.Pending = advice.Next;
            st.Save();
            Log.Info("curve assistant round: " + string.Join(", ", results.Select(r => $"{r.Core}:{(r.Passed ? "ok" : r.Failure)}")));
            Dialog.Show(advice.AllDone ? "All done" : advice.Changed ? "Next values" : "Round finished", string.Join("\n", advice.Lines) +
                (advice.Changed ? $"\n\nSet the new values in {rules.Tool}, then press \"I've set these values\" and test again." : ""), results.All(r => r.Passed) ? DialogKind.Info : DialogKind.Warning);
        }
        catch (OperationCanceledException) { st.InProgressCore = null; st.Save(); progress.Text = "Stopped. Nothing was recorded for this round."; }
        catch (Exception ex) { st.InProgressCore = null; st.Save(); progress.Text = "The test failed to run: " + ex.Message; Log.Error("curve assistant", ex); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            bar.Visibility = Visibility.Collapsed;
            redraw();
        }
    }

    private static Border Banner(string text) => new()
    {
        Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Gold"), Margin = new Thickness(0, 8, 0, 8),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Ui.Brush("Text") },
    };

    private static UIElement OpenTool(TuningTool? tool)
    {
        if (tool is null) return new Border();
        return tool.Path is { } path
            ? Ui.Btn($"Open {tool.Name}", (_, _) => Launch(path), "GhostButton", new Thickness(0, 8, 8, 0))
            : Ui.Btn($"Get {tool.Name} (official site) ↗", (_, _) => Launch(tool.Url), "GhostButton", new Thickness(0, 8, 8, 0));
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Show("Couldn't open it", $"{ex.Message}\n\n{target}", DialogKind.Error); }
    }
}
