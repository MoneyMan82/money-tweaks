using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Advisor;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>
/// Fix My Game, on the Dashboard: pick a problem → measure it (while the game runs, or the network for ping) → the
/// likely causes with their evidence and 1–3 actions → apply what you approve (tweaks are previewed, confirmed, backed
/// up) → re-test the same way and see improved / the same / worse, and whether the problem is still there, with Undo for
/// whatever was changed here. Uses the Upgrade Advisor's capture and diagnosis, the network diagnostic and Smart
/// Scanner; it has no measurements or tweaks of its own.
/// </summary>
public sealed class FixMyGamePanel : UserControl
{
    private readonly StackPanel _body = new();
    private GameProblem _problem;
    private FixMeasurement? _before, _after;
    private FixPlan? _plan;
    private (int pid, string name)? _game;
    private readonly List<TweakItem> _applied = [];
    private CancellationTokenSource? _cts;

    public FixMyGamePanel()
    {
        var root = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = "FIX MY GAME", Style = Ui.Res<Style>("Eyebrow") });
        root.Children.Add(head);
        root.Children.Add(new TextBlock { Text = "What's wrong in your game?", Style = Ui.Res<Style>("H2") });
        root.Children.Add(new TextBlock { Text = "Pick the problem. Money Tweaks measures it on this PC, shows what's most likely causing it, suggests up to three fixes and checks afterwards whether it helped. Nothing changes without your OK.", Style = Ui.Res<Style>("MutedText"), Margin = new Thickness(0, 4, 0, 14), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left });
        root.Children.Add(_body);
        Content = Ui.Hud(new Border { Style = Ui.Res<Style>("CardBorder"), Padding = new Thickness(24, 20, 24, 22), Child = root, Margin = new Thickness(0, 0, 14, 18) });
        ShowPick();
    }

    private void Swap(params UIElement[] parts)
    {
        _body.Children.Clear();
        foreach (var p in parts) _body.Children.Add(p);
        if (!App.ReduceMotion)
        {
            _body.Opacity = 0;
            var tt = new TranslateTransform(0, 8);
            _body.RenderTransform = tt;
            Ui.Animate(_body, OpacityProperty, 1, 220);
            Ui.Animate(tt, TranslateTransform.YProperty, 0, 260);
        }
    }

    private static readonly (GameProblem p, string icon)[] Problems =
    [
        (GameProblem.LowFps, "\uEC4A"), (GameProblem.Stutter, "\uE9D9"), (GameProblem.InputDelay, "\uE962"), (GameProblem.HighPing, "\uE701"), (GameProblem.Overheating, "\uE9CA"),
    ];

    // ---- step 1: pick ---------------------------------------------------------------------------------------------------
    private void ShowPick()
    {
        var grid = (ResponsiveGrid)Ui.Grid(5);
        grid.MinItemWidth = 160;
        foreach (var (p, icon) in Problems)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = icon, Style = Ui.Res<Style>("Icon"), FontSize = 20, Foreground = Ui.Brush("AccentGradient"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(new TextBlock { Text = FixMyGame.Label(p), FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
            sp.Children.Add(new TextBlock { Text = FixMyGame.Blurb(p), Style = Ui.Res<Style>("MutedText"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) });
            var card = Ui.ClickCard(sp, () => Pick(p), new Thickness(0, 0, 10, 0));
            card.Padding = new Thickness(14, 14, 14, 14);
            grid.Children.Add(card);
        }
        Swap(grid);
    }

    private async void Pick(GameProblem p)
    {
        _problem = p;
        _before = _after = null;
        _plan = null;
        _applied.Clear();
        await ShowMeasureAsync(retest: false);
    }

    // ---- step 2: measure ------------------------------------------------------------------------------------------------
    private int Seconds => _problem == GameProblem.Stutter ? 30 : 20;

    private async Task ShowMeasureAsync(bool retest)
    {
        var box = new StackPanel();
        box.Children.Add(StepTitle(retest ? "Re-test" : "Measure", FixMyGame.Label(_problem)));
        if (!FixMyGame.NeedsGame(_problem))
        {
            box.Children.Add(Ui.Muted($"Pings your router and two internet servers for {Seconds} s at the same time, to tell home-network problems from provider problems. You don't need to be in a game; pausing big downloads gives a fairer result.", 12.5));
            box.Children.Add(Ui.Row(Ui.Btn(retest ? "Re-test now" : "Start the test", async (_, _) => await MeasureAsync(retest), "PrimaryButton"), Ui.Btn("Back", (_, _) => Back(retest), "GhostButton")));
            Swap(box);
            return;
        }
        box.Children.Add(Ui.Muted("Looking for your game…", 12));
        Swap(box);
        var running = await Task.Run(RunningGames);
        box.Children.RemoveAt(box.Children.Count - 1);
        if (!Admin.IsElevated)
        {
            var warn = Ui.Muted("FPS, stutter and frame times need administrator rights (Windows only shares frame timing with administrators). Without them the check uses CPU, GPU, memory and temperature readings only.", 12);
            warn.Foreground = Ui.Brush("Gold");
            box.Children.Add(warn);
            box.Children.Add(Ui.Row(Ui.Btn("Restart as admin", (_, _) => { if (Admin.RestartElevated()) Application.Current.Shutdown(); }, "GhostButton")));
        }
        var pick = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 6, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
        const string front = "The game in front (pick after a 5-second countdown)";
        if (retest && _game is { } g && Alive(g.pid)) pick.Items.Add($"{g.name} (same as before)");
        foreach (var r in running) if (!(retest && _game?.pid == r.pid)) pick.Items.Add(r.name);
        pick.Items.Add(front);
        pick.SelectedIndex = 0;
        box.Children.Add(Ui.Muted(running.Count == 0 && !(retest && _game is not null)
            ? "No game from your Game Library is running. Start the game and get into a normal match or scene, then come back and press Start. Or use the countdown and switch to the game."
            : $"Get into a normal match or scene. Money Tweaks measures for {Seconds} s while you play; nothing is injected into the game.", 12.5));
        box.Children.Add(pick);
        box.Children.Add(Ui.Row(
            Ui.Btn(retest ? "Re-test now" : "Start measuring", async (_, _) =>
            {
                var choice = pick.SelectedItem as string ?? front;
                (int pid, string name)? target = choice == front ? null
                    : choice.EndsWith("(same as before)", StringComparison.Ordinal) ? _game
                    : running.FirstOrDefault(r => r.name == choice) is { name: not null } r ? (r.pid, r.name) : null;
                if (target is null)
                {
                    target = await CountdownAsync(box);
                    if (target is null) return;
                }
                _game = target;
                await MeasureAsync(retest);
            }, "PrimaryButton"),
            Ui.Btn("Back", (_, _) => Back(retest), "GhostButton")));
    }

    private void Back(bool retest) { if (retest && _plan is not null) ShowPlan(); else ShowPick(); }

    private static List<(int pid, string name)> RunningGames()
    {
        try
        {
            return GameRuntime.Statuses(GameLibrary.Items(GameLibrary.Current))
                .Where(s => s.status == GameStatus.Running && s.match.Process is not null)
                .Select(s => (s.match.Process!.Pid, s.game.Def.Name)).ToList();
        }
        catch (Exception ex) { Log.Debug("fix my game: running games: " + ex.Message); return []; }
    }

    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; } }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>Five seconds to switch to the game, then whatever is in front (never Money Tweaks or the desktop).</summary>
    private async Task<(int pid, string name)?> CountdownAsync(StackPanel box)
    {
        var t = new TextBlock { FontSize = 13, Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 4, 0, 0) };
        box.Children.Add(t);
        for (int i = 5; i > 0; i--) { t.Text = $"Switch to your game now… {i}"; await Task.Delay(1000); }
        GetWindowThreadProcessId(GetForegroundWindow(), out var fpid);
        try
        {
            using var p = Process.GetProcessById((int)fpid);
            if (p.Id == Environment.ProcessId || p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                t.Text = "No game was in front. Switch to the game during the countdown, or start it from your Game Library.";
                t.Foreground = Ui.Brush("Gold");
                return null;
            }
            return (p.Id, p.ProcessName);
        }
        catch { t.Text = "Couldn't tell which program was in front."; t.Foreground = Ui.Brush("Gold"); return null; }
    }

    private async Task MeasureAsync(bool retest)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var box = new StackPanel();
        box.Children.Add(StepTitle(retest ? "Re-testing" : "Measuring", FixMyGame.Label(_problem)));
        var bar = new ProgressBar { Maximum = Seconds, Height = 4, Margin = new Thickness(0, 6, 0, 6), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 520 };
        var live = Ui.Muted(FixMyGame.NeedsGame(_problem) ? $"Keep playing {_game?.name}…" : "Pinging…", 12.5);
        box.Children.Add(bar);
        box.Children.Add(live);
        box.Children.Add(Ui.Row(Ui.Btn("Cancel", (_, _) => _cts?.Cancel(), "GhostButton")));
        Swap(box);
        try
        {
            var scan = Task.Run(() => OptimizationScanner.Analyze(OptimizationScanner.Collect()), ct);
            FixMeasurement m;
            if (!FixMyGame.NeedsGame(_problem))
            {
                var r = await NetworkDiagnostics.RunAsync(Seconds, new Progress<int>(pct => bar.Value = pct * Seconds / 100.0), ct);
                m = FixMyGame.FromNetwork(r);
            }
            else
            {
                var (pid, name) = _game!.Value;
                double hz = StressScene.RefreshRate() is > 1 and var h ? h : 60;
                var cap = await GameCapture.RunAsync(pid, name, Seconds, hz, new Progress<(int second, TelemetrySample s)>(x =>
                {
                    bar.Value = x.second;
                    live.Text = $"Keep playing {name}… {x.second}/{Seconds} s · " +
                        (x.s.Frames > 0 ? $"{x.s.Frames} FPS · " : "") + $"GPU {x.s.GpuUtil ?? 0:0}% · CPU {x.s.CpuBusiestCore:0}% (busiest core)" +
                        (x.s.GpuTempC is { } gt ? $" · GPU {gt:0} °C" : "") + (x.s.CpuTempC is { } c ? $" · CPU {c:0} °C" : "");
                }), ct);
                var hw = await Task.Run(() => TuningDetector.Cached() is { } inv ? HardwareFactsDetector.Detect(inv, null)
                    : new HardwareFacts { Threads = Environment.ProcessorCount, RamGb = Math.Round(MemoryInfo.Status().total / 1073741824.0) }, ct);
                var d = BottleneckEngine.Diagnose(cap, hw, new AdvisorGoal { Game = name, TargetFps = hz });
                m = FixMyGame.FromCapture(_problem, cap, d, hz);
            }
            var findings = await scan;
            if (!retest)
            {
                _before = m;
                _plan = FixMyGame.Plan(m, findings);
                Log.Info($"Fix My Game: {FixMyGame.Label(_problem)} on {m.What}: {_plan.Causes.Count} cause(s), {_plan.Actions.Count} action(s); top: {_plan.Causes.FirstOrDefault()?.Title}");
                ShowPlan();
            }
            else
            {
                _after = m;
                ShowResult();
            }
        }
        catch (OperationCanceledException) { if (retest) ShowPlan(); else ShowPick(); }
        catch (Exception ex)
        {
            Log.Error("Fix My Game measurement failed", ex);
            Swap(StepTitle("Couldn't measure", FixMyGame.Label(_problem)), Ui.Muted(ex.Message, 12.5), Ui.Row(Ui.Btn("Back", (_, _) => Back(retest), "GhostButton")));
        }
    }

    // ---- step 3: causes and actions -------------------------------------------------------------------------------------
    private void ShowPlan()
    {
        if (_plan is null || _before is null) { ShowPick(); return; }
        var box = new StackPanel();
        box.Children.Add(StepTitle("What we found", $"{FixMyGame.Label(_problem)} · {_before.What} · {_before.At:t}"));
        foreach (var c in _plan.Causes.Take(3)) box.Children.Add(CauseRow(c));
        if (_plan.Causes.Count > 3) box.Children.Add(Ui.Muted($"{_plan.Causes.Count - 3} smaller signal(s) not shown: they're less likely to matter.", 11.5));
        box.Children.Add(new TextBlock { Text = _plan.Actions.Count == 0 ? "NO RECOMMENDED ACTIONS" : $"WHAT TO TRY ({_plan.Actions.Count})", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 16, 0, 6) });
        if (_plan.Actions.Count == 0)
            box.Children.Add(Ui.Muted("Nothing measured supports a specific fix, so Money Tweaks isn't suggesting one. " +
                (_problem == GameProblem.HighPing ? "Run the test again while the lag is happening." : "Measuring a heavier or more typical scene, while the problem happens, can show more."), 12.5));
        var actions = (ResponsiveGrid)Ui.Grid(3);
        actions.MinItemWidth = 260;
        int i = 1;
        foreach (var a in _plan.Actions) actions.Children.Add(ActionCard(a, i++));
        box.Children.Add(actions);
        foreach (var n in _plan.Notes) box.Children.Add(new TextBlock { Text = "ⓘ " + n, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        box.Children.Add(Ui.Row(
            Ui.Btn(_plan.Actions.Count == 0 ? "Re-test" : "Re-test after trying a fix", async (_, _) => await ShowMeasureAsync(retest: true), "PrimaryButton"),
            Ui.Btn("Measure again from scratch", async (_, _) => await ShowMeasureAsync(retest: false), "GhostButton"),
            Ui.Btn("Pick another problem", (_, _) => ShowPick(), "GhostButton")));
        ((WrapPanel)box.Children[^1]).Margin = new Thickness(0, 16, 0, 0);
        Swap(box);
    }

    private static UIElement StepTitle(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
        sp.Children.Add(Ui.Muted(sub, 11.5));
        return sp;
    }

    private static UIElement CauseRow(FixCause c)
    {
        var tone = c.Score >= 0.75 ? "Danger" : c.Score >= 0.45 ? "Gold" : "Muted";
        var row = new Border { BorderBrush = Ui.Brush(tone == "Muted" ? "BorderBrush" : tone), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 0, 8) };
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = c.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(Ui.Tag($"{c.Confidence} confidence", tone));
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = c.Plain, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        if (c.Evidence.Count > 0)
        {
            var ev = new Expander { Header = new TextBlock { Text = "Show the evidence", FontSize = 11.5, Foreground = Ui.Brush("Subtle") }, Margin = new Thickness(0, 4, 0, 0), Foreground = Ui.Brush("Muted") };
            var list = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
            foreach (var e in c.Evidence) list.Children.Add(new TextBlock { Text = "• " + e, FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
            ev.Content = list;
            sp.Children.Add(ev);
        }
        row.Child = sp;
        return row;
    }

    private UIElement ActionCard(FixAction a, int n)
    {
        var sp = new StackPanel();
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = n.ToString(), FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 18, Foreground = Ui.Brush("AccentGradient"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(a.Kind switch
        {
            FixKind.Tweak => Ui.Tag("Changes a Windows setting · undoable", "Gold"),
            FixKind.Tool => Ui.Tag("Opens a Money Tweaks tool", "Violet"),
            _ => Ui.Tag("You do this", "Muted"),
        });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock { Text = a.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = a.Plain, Style = Ui.Res<Style>("MutedText"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        sp.Children.Add(new TextBlock { Text = "What to expect: " + a.Expect, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var status = new TextBlock { FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        if (a.Kind == FixKind.Tool && a.PageTag is { } tag)
            buttons.Children.Add(Ui.Btn("Open " + (AppNavigation.Find(tag)?.Label ?? tag), (_, _) => MainWindow.Current?.Navigate(tag), "GhostButton"));
        if (a.Kind == FixKind.Tweak && a.TweakId is { } id && TweakCatalog.Find(id) is { } t)
        {
            var item = _applied.FirstOrDefault(x => x.Tweak.Id == id) ?? new TweakItem(t);
            var preview = new TextBlock { FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            Button? previewBtn = null;
            previewBtn = Ui.Btn("Preview change", async (_, _) =>
            {
                if (preview.Visibility == Visibility.Visible) { preview.Visibility = Visibility.Collapsed; previewBtn!.Content = "Preview change"; return; }
                preview.Text = "Reading current values…";
                preview.Visibility = Visibility.Visible;
                previewBtn!.Content = "Hide preview";
                preview.Text = await PreviewAsync(t);
            }, "GhostButton");
            buttons.Children.Add(previewBtn);
            if (_applied.Contains(item))
            {
                status.Text = "Applied here. Undo restores the backed-up values.";
                status.Foreground = Ui.Brush("Success");
                buttons.Children.Add(Ui.Btn("Undo", async (_, _) => { await UndoAsync(item); ShowPlan(); }, "GhostButton"));
            }
            else
                buttons.Children.Add(Ui.Btn("Apply…", async (_, _) =>
                {
                    var text = await PreviewAsync(t);
                    if (!Dialog.Confirm($"Apply “{t.Name}”?", $"{a.Plain}\n\nWhat to expect: {a.Expect}\n\n{text}\n\nThe current values are backed up first; Undo puts them back.", "Apply", t.Risk == Risk.Caution ? DialogKind.Warning : DialogKind.Confirm)) return;
                    await item.RefreshAsync();
                    if (item.State == TweakState.Applied) { status.Text = "Already on for this PC; nothing changed."; return; }
                    await TweakService.ToggleAsync(item);
                    if (item.State == TweakState.Applied) { _applied.Add(item); ShowPlan(); }
                    else status.Text = item.Message ?? "Not applied.";
                }, "PrimaryButton"));
            sp.Children.Add(buttons);
            sp.Children.Add(status);
            sp.Children.Add(preview);
        }
        else if (buttons.Children.Count > 0) sp.Children.Add(buttons);
        var card = Ui.Card(sp, new Thickness(0, 0, 10, 10));
        card.Padding = new Thickness(16, 14, 16, 14);
        return card;
    }

    private static async Task<string> PreviewAsync(Tweak t)
    {
        try { return await Task.Run(() => TweakXRay.Inspect(t).Compact()); }
        catch (Exception ex) { return "Couldn't read the current values: " + ex.Message; }
    }

    private async Task UndoAsync(TweakItem item)
    {
        await item.RefreshAsync();
        if (item.State is TweakState.Applied or TweakState.Partial) await TweakService.ToggleAsync(item);
        if (item.State is not (TweakState.Applied or TweakState.Partial)) _applied.Remove(item);
        else MainWindow.Current?.ShowToast($"“{item.Name}” wasn't undone: {item.Message}", "Gold");
    }

    // ---- step 4: re-test result -----------------------------------------------------------------------------------------
    private void ShowResult()
    {
        if (_before is null || _after is null) { ShowPick(); return; }
        var cmp = FixMyGame.Compare(_before, _after);
        Log.Info($"Fix My Game re-test: {FixMyGame.Label(_problem)}: {cmp.Verdict}, still a problem: {cmp.StillAProblem}. {cmp.Summary}");
        var box = new StackPanel();
        box.Children.Add(StepTitle("Re-test result", $"{FixMyGame.Label(_problem)} · before {_before.At:t}, after {_after.At:t}"));
        var (word, tone) = cmp.Verdict switch
        {
            FixVerdict.Improved => ("Improved", "Success"),
            FixVerdict.Worse => ("Got worse", "Danger"),
            FixVerdict.Same => ("No clear change", "Muted"),
            _ => ("Can't compare", "Muted"),
        };
        var verdict = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        verdict.Children.Add(new TextBlock { Text = word, FontFamily = Ui.Res<FontFamily>("DisplayFont"), FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(tone == "Muted" ? "Text" : tone), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        verdict.Children.Add(Ui.Tag(cmp.StillAProblem ? "Still a problem" : cmp.HadProblem ? "Looks resolved" : "No problem measured", cmp.StillAProblem ? "Gold" : "Success"));
        box.Children.Add(verdict);
        box.Children.Add(Ui.Muted(cmp.Summary, 12.5));

        var table = new Grid { Margin = new Thickness(0, 10, 0, 0), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Cell(string text, int r, int c, string brush, bool bold = false)
        {
            while (table.RowDefinitions.Count <= r) table.RowDefinitions.Add(new RowDefinition());
            var t = new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Brush(brush), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 3, 12, 3) };
            Grid.SetRow(t, r); Grid.SetColumn(t, c); table.Children.Add(t);
        }
        Cell("", 0, 0, "Subtle"); Cell("BEFORE", 0, 1, "Subtle", true); Cell("AFTER", 0, 2, "Subtle", true);
        int row = 1;
        foreach (var (metric, b, a, unit, higher) in cmp.Rows)
        {
            Cell(metric.Replace(" (median %)", "").Replace(" (max °C)", "").Replace(" (%)", "").Replace(" (ms)", ""), row, 0, "Muted");
            Cell(b is { } bv ? bv.ToString("0.#") + unit : "n/a", row, 1, "Text");
            // Coloured only beyond normal run-to-run variation (5 % and a small absolute floor).
            string brush = b is { } x && a is { } y && Math.Abs(y - x) > Math.Max(0.05 * Math.Abs(x), unit is " ms" ? 1 : unit is " °C" ? 3 : unit is "%" ? 0.5 : 2) ? ((y > x) == higher ? "Success" : "Danger") : "Text";
            Cell(a is { } av ? av.ToString("0.#") + unit : "n/a", row, 2, brush);
            row++;
        }
        box.Children.Add(table);
        foreach (var n in cmp.Notes) box.Children.Add(new TextBlock { Text = "ⓘ " + n, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var buttons = Ui.Row();
        buttons.Margin = new Thickness(0, 16, 0, 0);
        if (_applied.Count > 0)
        {
            var names = string.Join(", ", _applied.Select(x => x.Name));
            box.Children.Add(Ui.Muted($"Changed here: {names}.", 12));
            buttons.Children.Add(Ui.Btn(cmp.Verdict == FixVerdict.Improved ? "Undo the changes anyway" : "Undo the changes made here", async (_, _) =>
            {
                if (!Dialog.Confirm("Undo these changes?", $"{names}\n\nEach one is restored from the backup taken before it was applied, then read back.", "Undo")) return;
                foreach (var item in _applied.ToList()) await UndoAsync(item);
                ShowResult();
            }, cmp.Verdict == FixVerdict.Improved ? "GhostButton" : "GoldButton"));
        }
        buttons.Children.Add(Ui.Btn("Back to the suggestions", (_, _) => ShowPlan(), "GhostButton"));
        buttons.Children.Add(Ui.Btn("Re-test again", async (_, _) => await ShowMeasureAsync(retest: true), "GhostButton"));
        buttons.Children.Add(Ui.Btn("Done", (_, _) => ShowPick(), "GhostButton"));
        box.Children.Add(buttons);
        Swap(box);
    }
}
