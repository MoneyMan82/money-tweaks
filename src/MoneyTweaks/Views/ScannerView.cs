using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>Smart Optimization Scanner: reads this PC, lists evidence-based findings, Fix (via the tweak engine) / Ignore / Undo.</summary>
public sealed class ScannerView : UserControl, IPageLifecycle
{
    private readonly ScannerState _state = ScannerState.Load();
    private readonly StackPanel _summary = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _fixed = new();
    private readonly TextBlock _status = Ui.Muted("Nothing scanned yet. Scanning only reads settings and changes nothing.", 12.5);
    private readonly Button _scanBtn;
    private ScanFacts? _facts;
    private List<Finding> _findings = [];
    private bool _busy, _showIgnored;

    public ScannerView()
    {
        _scanBtn = Ui.Btn("Scan", async (_, _) => await ScanAsync(), "PrimaryButton");
        var (root, body) = Ui.Page("Smart Optimization Scanner",
            "Reads your hardware, power plan, memory, startup apps, background processes and network, then lists what is worth changing on this PC. Each finding shows the real data it's based on, how sure we are, and what to expect. Fixes use the normal tweak engine: backed up, verified and undoable.",
            _scanBtn, "Scan · Fix · Ignore", "\uE721");
        Content = root;
        body.Children.Add(_status);
        body.Children.Add(_summary);
        var ignoredToggle = new CheckBox { Content = "Show ignored findings", Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 6, 0, 10) };
        ignoredToggle.Checked += (_, _) => { _showIgnored = true; Render(); };
        ignoredToggle.Unchecked += (_, _) => { _showIgnored = false; Render(); };
        body.Children.Add(ignoredToggle);
        body.Children.Add(_list);
        body.Children.Add(_fixed);
        body.Children.Add(Ui.Muted("The scanner never changes services or security settings, never closes apps, and never promises FPS or ping gains. Measure real effects on the Test & Benchmark page.", 11.5));
        RenderFixed();
    }

    public async void OnShown() { if (_facts is null && !_busy) await ScanAsync(); }
    public void OnHidden() { }
    internal bool IsScanning => _busy;

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        _scanBtn.IsEnabled = false;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<string>(s => _status.Text = s);
            _facts = await Task.Run(() => OptimizationScanner.Collect(progress));
            _findings = OptimizationScanner.Analyze(_facts);
            _state.LastScan = DateTime.Now;
            SaveState();
            _status.Text = $"Scanned {DateTime.Now:T} in {sw.Elapsed.TotalSeconds:0.0} s. Nothing was changed.";
            Log.Info($"Scanner: {_findings.Count} findings ({string.Join(", ", _findings.Select(f => f.Id))})");
            Render();
        }
        catch (Exception ex) { Log.Error("Scan failed", ex); _status.Text = "Scan failed: " + ex.Message; }
        finally { _busy = false; _scanBtn.IsEnabled = true; }
    }

    private void SaveState()
    {
        try { _state.Save(); } catch (Exception ex) { Log.Error("Saving scanner state failed", ex); }
    }

    private static string Tone(Severity s) => s switch { Severity.High => "Danger", Severity.Medium => "Gold", Severity.Low => "Accent", _ => "Muted" };

    private void Render()
    {
        _summary.Children.Clear();
        _list.Children.Clear();
        if (_facts is not { } f) return;
        // Real system facts, no score.
        var facts = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        void Fact(string label, string value)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 26, 8), MaxWidth = 300 };
            sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow") });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            facts.Children.Add(sp);
        }
        Fact("CPU", $"{f.Cpu} ({f.Cores}C/{f.Threads}T)");
        Fact("GPU", f.Gpus);
        Fact("Memory", $"{f.RamGb:0.#} GB · {f.RamUsedPercent:0}% used" + (f.RamConfiguredMts is { } c ? $" · {c} MT/s" : ""));
        Fact("Power", $"{(f.PowerPlanName.Length > 0 ? f.PowerPlanName : "unknown")} · {(f.HasBattery ? (f.OnAc == false ? "on battery" : "laptop, plugged in") : "desktop")}");
        Fact("Network", f.Adapters.Count == 0 ? "no active adapter" : string.Join(", ", f.Adapters.Select(a => $"{a.Type} {a.SpeedBps / 1_000_000} Mbps")));
        Fact("Startup apps", $"{f.StartupEnabled.Count} enabled");
        _summary.Children.Add(Ui.Card(facts));

        var visible = _findings.Where(x => _showIgnored || !_state.Ignored.Contains(x.Id)).ToList();
        var counts = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var s in new[] { Severity.High, Severity.Medium, Severity.Low, Severity.Info })
            counts.Children.Add(Ui.Tag($"{_findings.Count(x => x.Severity == s && !_state.Ignored.Contains(x.Id))} {s}", Tone(s)));
        var ign = _findings.Count(x => _state.Ignored.Contains(x.Id));
        if (ign > 0) counts.Children.Add(Ui.Tag($"{ign} ignored", "Muted"));
        _summary.Children.Add(counts);

        if (visible.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Empty("\uE73E", "Nothing to fix", "No issues found that are worth changing on this PC right now.")));
            return;
        }
        foreach (var x in visible) _list.Children.Add(FindingCard(x));
    }

    private UIElement FindingCard(Finding x)
    {
        bool ignored = _state.Ignored.Contains(x.Id);
        var sp = new StackPanel { Opacity = ignored ? 0.55 : 1 };
        var head = new WrapPanel();
        head.Children.Add(Ui.Tag(x.Severity.ToString().ToUpperInvariant(), Tone(x.Severity)));
        head.Children.Add(Ui.Text(x.Title, 14.5, FontWeights.SemiBold));
        head.Children.Add(new TextBlock { Text = "   " + x.Area, Foreground = Ui.Brush("Subtle"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = x.Why, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var ev = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0),
            Child = new TextBlock { Text = string.Join("\n", x.Evidence.Select(e => "• " + e)), FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap }
        };
        sp.Children.Add(ev);
        var meta = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        meta.Children.Add(Ui.Tag($"Confidence: {x.Confidence}", x.Confidence == Confidence.High ? "Success" : x.Confidence == Confidence.Medium ? "Accent" : "Muted"));
        meta.Children.Add(new TextBlock { Text = "Expected benefit: " + x.Benefit, FontSize = 12, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, MaxWidth = 700, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(meta);
        if (x.Manual is { } man) sp.Children.Add(new TextBlock { Text = "What to do: " + man, FontSize = 12, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var row = Ui.Row();
        row.Margin = new Thickness(0, 10, 0, 0);
        if (x.FixTweakIds.Count > 0) row.Children.Add(Ui.Btn("Fix…", async (_, _) => await FixAsync(x), "PrimaryButton"));
        if (_state.Fixed.ContainsKey(x.Id)) row.Children.Add(Ui.Btn("Undo fix", async (_, _) => await UndoAsync(x.Id, x.Title), "GoldButton"));
        row.Children.Add(Ui.Btn(ignored ? "Un-ignore" : "Ignore", (_, _) =>
        {
            if (!_state.Ignored.Remove(x.Id)) _state.Ignored.Add(x.Id);
            SaveState();
            Render();
        }, "GhostButton"));
        sp.Children.Add(row);
        return Ui.Card(sp);
    }

    private async Task FixAsync(Finding x)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var items = x.FixTweakIds.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
            await TweakService.RefreshStatesAsync(items);
            var todo = items.Where(i => i.CanToggle && i.State is TweakState.NotApplied or TweakState.Partial).ToList();
            if (todo.Count == 0) { Dialog.Show("Already done", "These settings are already applied. Scan again to refresh."); return; }
            var sb = new StringBuilder($"{x.Title}\nExpected benefit: {x.Benefit}\nConfidence: {x.Confidence}\n\nThese changes will be made:\n");
            foreach (var i in todo)
            {
                sb.Append($"\n• {i.Name}{(i.IsCaution ? "  ⚠ Caution" : "")}{(i.NeedsReboot ? "  (needs restart)" : "")}\n  {i.Description}\n");
                foreach (var op in i.Tweak.Ops) sb.Append($"     {op.Describe()}\n");
                if (i.Tweak.Warning is { } w) sb.Append($"  ⚠ {w}\n");
            }
            sb.Append("\nEach value is backed up first, read back after applying (rolled back automatically if Windows doesn't keep it), and can be undone here or from its tweak page.");
            if (!Dialog.Confirm($"Fix: {x.Title}?", sb.ToString(), "Apply", todo.Any(i => i.IsCaution) ? DialogKind.Warning : DialogKind.Confirm)) return;
            var (ok, failed, reboot) = await TweakService.BatchAsync(todo, true);
            var applied = todo.Where(i => i.State == TweakState.Applied).Select(i => i.Tweak.Id).ToList();
            if (applied.Count > 0)
            {
                _state.Fixed[x.Id] = (_state.Fixed.GetValueOrDefault(x.Id) ?? []).Union(applied).ToList();
                SaveState();
            }
            var fails = string.Join("\n", todo.Where(i => i.State != TweakState.Applied).Select(i => $"• {i.Name}: {i.Message}"));
            Dialog.Show(failed == 0 ? "Fixed and verified" : ok == 0 ? "Nothing was fixed" : "Fixed with problems",
                $"{ok} change(s) applied and read back from Windows{(failed > 0 ? $", {failed} failed:\n{fails}" : ".")}{(reboot ? "\n\nRestart Windows to finish." : "")}",
                failed == 0 ? DialogKind.Info : DialogKind.Warning);
        }
        finally { _busy = false; }
        RenderFixed();
        await ScanAsync();
    }

    private async Task UndoAsync(string findingId, string title)
    {
        if (_busy || !_state.Fixed.TryGetValue(findingId, out var ids)) return;
        if (!Dialog.Confirm("Undo fix?", $"{title}\n\nRestores the backed-up original values of:\n{string.Join("\n", ids.Select(id => "• " + (TweakCatalog.Find(id)?.Name ?? id)))}", "Undo", DialogKind.Warning)) return;
        _busy = true;
        try
        {
            var items = ids.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
            await TweakService.RefreshStatesAsync(items);
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, false);
            await TweakService.RefreshStatesAsync(items);
            var still = items.Where(i => i.State is TweakState.Applied or TweakState.Partial).Select(i => i.Tweak.Id).ToList();
            if (still.Count == 0) _state.Fixed.Remove(findingId); else _state.Fixed[findingId] = still;
            SaveState();
            Dialog.Show(still.Count == 0 ? "Undone and verified" : "Undo incomplete",
                $"{ok} restored, {failed} failed.\n" + string.Join("\n", items.Select(i => $"• {i.Name}: {i.Message}")) + (reboot ? "\n\nRestart Windows to finish." : ""),
                still.Count == 0 ? DialogKind.Info : DialogKind.Warning);
        }
        finally { _busy = false; }
        RenderFixed();
        await ScanAsync();
    }

    private void RenderFixed()
    {
        _fixed.Children.Clear();
        if (_state.Fixed.Count == 0) return;
        _fixed.Children.Add(Ui.Section("Fixed by the scanner", "Undo restores the original values backed up before each fix."));
        foreach (var (id, ids) in _state.Fixed.ToList())
        {
            var sp = new DockPanel();
            var undo = Ui.Btn("Undo fix", async (_, _) => await UndoAsync(id, id), "GoldButton");
            DockPanel.SetDock(undo, Dock.Right);
            sp.Children.Add(undo);
            sp.Children.Add(new TextBlock { Text = $"{id}: {string.Join(", ", ids.Select(t => TweakCatalog.Find(t)?.Name ?? t))}", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            _fixed.Children.Add(Ui.Card(sp, new Thickness(0, 0, 0, 8)));
        }
    }
}
