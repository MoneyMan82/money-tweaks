using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>
/// Crash Detective in the Health Center: investigates blue screens, unexpected restarts, GPU driver resets, hardware errors
/// and app/game crashes from Windows' own logs, Reliability Monitor and crash dump folders. Confirmed facts and suspected
/// causes are shown apart; fixes that change anything ask first.
/// </summary>
public sealed partial class MonitorView
{
    private readonly StackPanel _crash = new();
    private CrashReport? _crashReport;
    private bool _crashBusy;
    private readonly HashSet<DateTime> _crashOpen = [];

    private void RenderCrashDetective()
    {
        _crash.Children.Clear();
        var head = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var go = Ui.Btn(_crashBusy ? "Investigating…" : _crashReport is null ? "Investigate crashes" : "Investigate again", async (_, _) => await InvestigateAsync(), "PrimaryButton");
        go.IsEnabled = !_crashBusy;
        buttons.Children.Add(go);
        if (_crashReport is not null) buttons.Children.Add(Ui.Btn("Export report…", (_, _) => ExportCrashReport(), "GhostButton", new Thickness(8, 0, 0, 0)));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var titles = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        titles.Children.Add(new TextBlock { Text = "CRASH DETECTIVE", Style = Ui.Res<Style>("Eyebrow") });
        titles.Children.Add(Ui.Text("Why did it crash?", 17, FontWeights.SemiBold));
        titles.Children.Add(Ui.Muted($"Investigates blue screens, unexpected restarts, GPU driver resets, hardware (WHEA) errors and game or app crashes from the last {CrashDetective.DefaultDays} days, using Windows' event logs, Reliability Monitor and crash dump folders. Everything stays on this PC. Events that happened together are grouped, and what Windows recorded is shown apart from what it might mean.", 12.5));
        head.Children.Add(titles);
        _crash.Children.Add(head);
        if (_crashBusy) { _crash.Children.Add(Ui.Loading("Reading event logs, Reliability Monitor and crash dumps…")); return; }
        if (_crashReport is not { } r) return;

        var sum = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        foreach (var g in r.Incidents.GroupBy(i => i.KindLabel))
            sum.Children.Add(Spaced(Ui.Tag($"{g.Count()} × {g.Key.ToUpperInvariant()}", g.First().Kind is CrashKind.BlueScreen or CrashKind.HardwareError ? "Danger" : "Gold")));
        if (r.StabilityNow is { } s) sum.Children.Add(Spaced(Ui.Tag($"STABILITY INDEX {s:0.0}/10 (LOWEST {r.StabilityLowest:0.0})", s >= 8 ? "Success" : s >= 5 ? "Gold" : "Danger")));
        _crash.Children.Add(sum);
        foreach (var p in r.Patterns) _crash.Children.Add(new TextBlock { Text = "Pattern: " + p, FontSize = 12.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        foreach (var n in r.Notes) _crash.Children.Add(Ui.Muted(n, 12));
        _crash.Children.Add(Ui.Muted("Read: " + string.Join(" · ", r.SourcesRead) + (r.Unavailable.Count > 0 ? "\nNot available: " + string.Join(" · ", r.Unavailable) : ""), 11));

        foreach (var i in r.Incidents.Take(40)) _crash.Children.Add(IncidentCard(i));
        if (r.Incidents.Count > 40) _crash.Children.Add(Ui.Muted($"{r.Incidents.Count - 40} older incidents are in the exported report.", 12));
    }

    private static Border Spaced(Border b) { b.Margin = new Thickness(0, 0, 6, 6); return b; }

    private UIElement IncidentCard(CrashIncident i)
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Spaced(Ui.Tag(i.KindLabel.ToUpperInvariant(), i.Kind is CrashKind.BlueScreen or CrashKind.HardwareError ? "Danger" : i.Kind is CrashKind.UnexpectedRestart or CrashKind.GpuDriverReset or CrashKind.LiveKernelEvent ? "Gold" : "Accent")));
        head.Children.Add(new TextBlock { Text = i.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), Margin = new Thickness(2, 0, 10, 6), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = i.Time.ToString("g"), FontSize = 12, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        var lead = i.Hypotheses[0];
        sp.Children.Add(new TextBlock { Text = $"{lead.ConfidenceLabel}: {lead.Cause}", FontSize = 12.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });

        var details = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = _crashOpen.Contains(i.Time) ? Visibility.Visible : Visibility.Collapsed };
        var toggle = Ui.Btn(_crashOpen.Contains(i.Time) ? "Hide evidence ‹" : "Evidence and fixes ›", (_, _) => { }, "GhostButton", new Thickness(0, 6, 0, 0));
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Click += (_, _) =>
        {
            bool open = details.Visibility != Visibility.Visible;
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = open ? "Hide evidence ‹" : "Evidence and fixes ›";
            if (open) _crashOpen.Add(i.Time); else _crashOpen.Remove(i.Time);
        };
        sp.Children.Add(toggle);

        details.Children.Add(new TextBlock { Text = "CONFIRMED · RECORDED BY WINDOWS", Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Success"), Margin = new Thickness(0, 0, 0, 4) });
        foreach (var f in i.Facts)
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 1, 0, 1) };
            row.Inlines.Add(new System.Windows.Documents.Run(f.Time.ToString("HH:mm:ss") + "  ") { Foreground = Ui.Brush("Subtle"), FontFamily = Ui.Res<FontFamily>("MonoFont") });
            row.Inlines.Add(new System.Windows.Documents.Run(f.Text) { Foreground = Ui.Brush("Text") });
            row.Inlines.Add(new System.Windows.Documents.Run("  · " + f.Source) { Foreground = Ui.Brush("Subtle"), FontSize = 11 });
            details.Children.Add(row);
        }
        details.Children.Add(new TextBlock { Text = "SUSPECTED CAUSES · HYPOTHESES, NOT CONFIRMED", Style = Ui.Res<Style>("Eyebrow"), Foreground = Ui.Brush("Gold"), Margin = new Thickness(0, 12, 0, 4) });
        foreach (var h in i.Hypotheses) details.Children.Add(HypothesisBlock(h));
        sp.Children.Add(details);
        return Ui.Card(sp, new Thickness(0, 10, 0, 0));
    }

    private UIElement HypothesisBlock(CrashHypothesis h)
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Spaced(Ui.Tag(h.ConfidenceLabel.ToUpperInvariant(), h.Confidence switch { Confidence.Strong => "Danger", Confidence.Moderate => "Gold", _ => "Muted" })));
        head.Children.Add(new TextBlock { Text = h.Cause, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, Margin = new Thickness(2, 0, 0, 6) });
        sp.Children.Add(head);
        foreach (var e in h.Evidence) sp.Children.Add(new TextBlock { Text = "• Evidence: " + e, FontSize = 12, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = "Caveat: " + h.Caveat, FontSize = 11.5, FontStyle = FontStyles.Italic, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        foreach (var fx in h.Fixes)
        {
            var f = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            f.Children.Add(new TextBlock { Text = "Fix: " + fx.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text") });
            int n = 1;
            foreach (var step in fx.Steps) f.Children.Add(new TextBlock { Text = $"{n++}. {step}", FontSize = 12, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 1, 0, 1) });
            if (fx.Reversibility is { } rv) f.Children.Add(new TextBlock { Text = rv, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 0, 0) });
            if (FixButton(fx) is { } b) f.Children.Add(b);
            sp.Children.Add(f);
        }
        return new Border { Child = sp, BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8) };
    }

    private Button? FixButton(CrashFix fx)
    {
        Button B(string text, Func<Task> run, string style = "GhostButton")
        {
            var b = Ui.Btn(text, async (_, _) => await run(), style, new Thickness(10, 6, 0, 0));
            b.HorizontalAlignment = HorizontalAlignment.Left;
            return b;
        }
        switch (fx.Action)
        {
            case { } a when a.StartsWith("undo-tweak:", StringComparison.Ordinal) && TweakCatalog.Find(a[11..]) is { } t:
                return B($"Undo “{t.Name}”…", () => UndoFromCrashAsync(t), "GoldButton");
            case { } a when a.StartsWith("repair:", StringComparison.Ordinal) && SafeRepairs.All.Any(r => r.Id == a[7..]):
                return B(SafeRepairs.Get(a[7..]).Title + "…", () => RunRepairAsync(a[7..]), "GoldButton");
            case { } a when a.StartsWith("link:", StringComparison.Ordinal):
                var target = a[5..];
                string label = target switch { "devmgmt.msc" => "Open Device Manager", "mdsched.exe" => "Open Windows Memory Diagnostic", "SystemPropertiesAdvanced.exe" => "Open System Properties", _ => "Open" };
                return B(label, () => { OpenLink(new HealthLink(label, target)); return Task.CompletedTask; });
            default: return null;
        }
    }

    /// <summary>Undo a Money Tweaks tweak suspected in a crash: confirmation, then the normal backup-based Undo with read-back.</summary>
    private async Task UndoFromCrashAsync(Tweak t)
    {
        if (TweakEngine.GetState(t) is not (TweakState.Applied or TweakState.Partial)) { Dialog.Show("Nothing to undo", $"“{t.Name}” isn't applied right now."); return; }
        if (!Dialog.Confirm($"Undo “{t.Name}”?",
                "Crash Detective lists this tweak because it was applied shortly before the problem started. That timing is only a lead, not proof.\n\n" +
                "Undo restores the original values Money Tweaks backed up when it was applied and reads them back. You can apply it again any time.",
                "Undo", DialogKind.Warning)) return;
        if (!TweakService.EnsureAdmin([t])) return;
        var r = await Task.Run(() => TweakEngine.Undo(t));
        Log.Info($"Crash Detective: undo {t.Id}: {(r.Success ? "ok" : r.Message)}");
        Dialog.Show(r.Success ? "Undone" : "Undo didn't finish", r.Message, r.Success ? DialogKind.Info : DialogKind.Warning);
    }

    private async Task InvestigateAsync()
    {
        if (_crashBusy) return;
        _crashBusy = true;
        RenderCrashDetective();
        try
        {
            var input = await Task.Run(() => CrashDetective.Collect());
            _crashReport = CrashDetective.Analyze(input, DateTime.Now);
            Log.Info($"Crash Detective: {_crashReport.Incidents.Count} incident(s), {input.Events.Count} events read; unavailable: {string.Join("; ", input.Unavailable)}");
        }
        catch (Exception ex)
        {
            Log.Error("Crash Detective failed", ex);
            Dialog.Show("Crash Detective couldn't finish", ex.Message, DialogKind.Error);
        }
        finally { _crashBusy = false; RenderCrashDetective(); }
    }

    private void ExportCrashReport()
    {
        if (_crashReport is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"MoneyTweaks-CrashReport-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "Text file|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, CrashDetective.Redact(CrashDetective.ToText(_crashReport)));
            Dialog.Show("Report saved", "Saved to " + dlg.FileName + "\n\nYour user name, computer name, profile path, account IDs, e-mail, IP and MAC addresses, report IDs and device serial numbers were removed. Check the file before sharing it; nothing was sent anywhere.");
        }
        catch (Exception ex) { Dialog.Show("Couldn't save the report", ex.Message, DialogKind.Error); }
    }
}
