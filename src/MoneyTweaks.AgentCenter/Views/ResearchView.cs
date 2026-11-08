using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>
/// Research monitor: pages read and searches run (from WebFetch/WebSearch), findings and sources the agents reported,
/// competitors mentioned, and the team's ideas backlog (team/IDEAS.md) with rejected ideas and their reasons.
/// </summary>
public sealed class ResearchView : Border
{
    private readonly StackPanel _sources = new(), _findings = new(), _ideas = new(), _competitors = new();
    private string _sig = "";

    private static readonly string[] KnownTools = ["WinUtil", "Win11Debloat", "Sophia", "O&O ShutUp10", "Optimizer", "Process Lasso", "Afterburner", "RTSS", "NVIDIA App", "Razer Cortex", "PresentMon", "CapFrameX", "LatencyMon", "Autoruns", "ExitLag", "WinAero"];

    public ResearchView()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Ui.Head("Research monitor"));
        left.Children.Add(Section("Findings and conclusions (reported by agents)", _findings));
        left.Children.Add(Section("Sources, pages read and searches", _sources));
        var right = new StackPanel();
        right.Children.Add(Section("Competitors investigated", _competitors));
        right.Children.Add(Section("Ideas backlog (team/IDEAS.md)", _ideas));
        g.Children.Add(Ui.Scroll(left));
        var r = Ui.Scroll(right); Grid.SetColumn(r, 2); g.Children.Add(r);
        Child = g;
    }

    private static UIElement Section(string title, StackPanel body)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Label(title));
        sp.Children.Add(body);
        return new Border { Margin = new Thickness(0, 10, 0, 0), Child = Ui.Card(sp, null, 10) };
    }

    public void Update(TeamState s, List<IdeaRow> ideas, DateTimeOffset now)
    {
        var sig = s.Research.Count + "|" + ideas.Count + "|" + string.Join(",", ideas.Select(i => i.Status));
        if (sig == _sig) return;
        _sig = sig;

        _findings.Children.Clear();
        var findings = s.Research.Where(r => r.Kind == "finding").OrderByDescending(r => r.Time).Take(150).ToList();
        if (findings.Count == 0) _findings.Children.Add(Ui.T("No findings reported yet. Agents report them with `command_center.py publish --finding`.", 12, Ui.Faint, wrap: true));
        foreach (var f in findings) _findings.Children.Add(Item(f.Time, f.AgentType, f.Text, null));
        var reports = s.Agents.Values.Where(a => a.Report is not null && (a.Type.Contains("research") || a.Type.Contains("scout") || a.Type.Contains("competitor") || a.Type.Contains("critic") || a.Type.Contains("specialist") || a.Type.Contains("analyst")))
            .OrderByDescending(a => a.LastSeen).Take(10);
        foreach (var a in reports) _findings.Children.Add(Item(a.LastSeen, a.Type + " · conclusion", a.Report!.Length > 700 ? a.Report[..700] + "…" : a.Report, null));

        _sources.Children.Clear();
        var src = s.Research.Where(r => r.Kind is "source" or "search").GroupBy(r => r.Text).Select(g => g.OrderByDescending(x => x.Time).First()).OrderByDescending(r => r.Time).Take(200).ToList();
        if (src.Count == 0) _sources.Children.Add(Ui.T("No web research yet.", 12, Ui.Faint));
        foreach (var r in src) _sources.Children.Add(Item(r.Time, r.AgentType + (r.Kind == "search" ? " · search" : ""), r.Text, r.Link));

        _competitors.Children.Clear();
        var text = string.Join("\n", s.Research.Select(r => r.Text)) + "\n" + string.Join("\n", s.Agents.Values.Select(a => a.Report ?? ""));
        var found = KnownTools.Select(t => (t, n: CountOf(text, t))).Where(x => x.n > 0).OrderByDescending(x => x.n).ToList();
        if (found.Count == 0) _competitors.Children.Add(Ui.T("None mentioned in research so far.", 12, Ui.Faint));
        foreach (var (t, n) in found) _competitors.Children.Add(Ui.T($"{t} — mentioned {n} time(s)", 12, Ui.Fg));

        _ideas.Children.Clear();
        foreach (var grp in ideas.GroupBy(i => TeamFiles.IsRejected(i) ? "Rejected" : TeamFiles.IsDone(i) ? "Became features" : "Open").OrderBy(g => g.Key switch { "Open" => 0, "Became features" => 1, _ => 2 }))
        {
            _ideas.Children.Add(new TextBlock { Text = grp.Key.ToUpperInvariant() + $" ({grp.Count()})", FontFamily = Ui.Display, FontSize = 10.5, Foreground = grp.Key == "Rejected" ? Ui.Red : grp.Key == "Open" ? Ui.Accent : Ui.Green, Margin = new Thickness(0, 6, 0, 2) });
            foreach (var i in grp)
                _ideas.Children.Add(Item(null, $"{i.Id} · {i.Status}", i.Title + (grp.Key == "Rejected" && i.Notes.Length > 0 ? " — " + i.Notes : ""), null));
        }
    }

    private static int CountOf(string text, string word)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += word.Length; }
        return n;
    }

    private static UIElement Item(DateTimeOffset? t, string who, string text, string? link)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(Ui.T((t is { } tt ? Ui.Clock(tt) + "  " : "") + who, 10.5, Ui.Faint, font: Ui.Display));
        if (link is not null && Sanitizer.IsWebLink(link))
        {
            var h = new Hyperlink(new Run(text)) { Foreground = Ui.Accent, ToolTip = "Open in your browser: " + link };
            h.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { } };
            sp.Children.Add(new TextBlock(h) { TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        else sp.Children.Add(Ui.T(text, 12, Ui.Fg, wrap: true));
        return sp;
    }
}
