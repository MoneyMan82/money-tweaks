using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>
/// Real-time code monitor: files changed by agents (confirmed by the agent's Edit/Write event and/or seen on disk by
/// the file monitor - never guessed), with agent, branch, line counts, tests after the change and a git diff viewer.
/// </summary>
public sealed class CodeView : Border
{
    private readonly Workspace _ws;
    private readonly ListBox _files = new();
    private readonly DiffViewer _diff = new();
    private readonly TextBlock _diffTitle = Ui.T("Select a file to see its diff.", 13, Ui.Dim, FontWeights.SemiBold, Ui.Display);
    private readonly CheckBox _agentOnly = new() { Content = "Only files linked to an agent", Foreground = Ui.Dim, IsChecked = true, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private string _sig = "";
    private FileActivity? _selected;

    public CodeView(Workspace ws)
    {
        _ws = ws;
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 120 });
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 0, 8) };
        head.Children.Add(Ui.Head("Code monitor"));
        head.Children.Add(_agentOnly);
        g.Children.Add(head);
        var list = new Border { Background = Ui.Panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _files };
        Grid.SetRow(list, 1); g.Children.Add(list);
        var split = new GridSplitter { Height = 2, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(split, 2); g.Children.Add(split);
        var diffDock = new DockPanel();
        _diffTitle.Margin = new Thickness(12, 8, 12, 6);
        DockPanel.SetDock(_diffTitle, Dock.Top);
        diffDock.Children.Add(_diffTitle);
        diffDock.Children.Add(_diff);
        var diffBox = new Border { Background = Ui.Panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = diffDock };
        Grid.SetRow(diffBox, 3); g.Children.Add(diffBox);
        Child = g;
        _files.SelectionChanged += (_, _) => { if (_files.SelectedItem is FrameworkElement { Tag: FileActivity fa }) Select(fa); };
        _agentOnly.Checked += (_, _) => _sig = ""; _agentOnly.Unchecked += (_, _) => _sig = "";
    }

    public void Update(TeamState s, DateTimeOffset now)
    {
        var files = s.FileMap.Values.Where(f => _agentOnly.IsChecked != true || f.AgentId is not null).OrderByDescending(f => f.LastChange).Take(400).ToList();
        var sig = string.Join("|", files.Select(f => $"{f.Path}:{f.Changes}:{f.AgentId}:{f.TestsAfter?.Passed}"));
        if (sig == _sig) return;
        _sig = sig;
        var keep = _selected?.Path;
        _files.Items.Clear();
        foreach (var f in files) _files.Items.Add(Row(f, s, now));
        if (keep is not null) foreach (FrameworkElement it in _files.Items) if (it.Tag is FileActivity fa && fa.Path == keep) { _files.SelectedItem = it; break; }
        if (_selected is not null && files.FirstOrDefault(f => f.Path == _selected.Path) is { } cur && cur.Changes != _selectedChanges) Select(cur);
    }

    private int _selectedChanges;

    private FrameworkElement Row(FileActivity f, TeamState s, DateTimeOffset now)
    {
        var g = new Grid { Margin = new Thickness(12, 6, 12, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Ui.T(Path.GetFileName(f.Path), 13, Ui.Fg, FontWeights.SemiBold, Ui.Display));
        left.Children.Add(Ui.T(Path.GetDirectoryName(f.Relative), 11, Ui.Faint, font: Ui.Mono));
        var who = f.AgentType is null ? "not linked to an agent" : f.AgentType;
        var conf = string.Join(" + ", f.Confirmed.Select(c => c == "agent" ? "agent edit event" : "seen on disk"));
        left.Children.Add(Ui.T($"{who} · {f.Branch ?? "branch unknown"} · {conf} · {Ui.Ago(f.LastChange, now)}" + (f.TestsAfter is { } t ? $" · tests after: {t.Passed} passed, {t.Failed} failed" : ""), 11, f.TestsAfter?.Failed > 0 ? Ui.Red : Ui.Dim));
        g.Children.Add(left);
        var lines = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(Ui.T(f.Added is { } a ? "+" + a : "+?", 12, Ui.Green, FontWeights.SemiBold, Ui.Mono));
        lines.Children.Add(Ui.T(f.Removed is { } r ? "  −" + r : "  −?", 12, Ui.Red, FontWeights.SemiBold, Ui.Mono));
        Grid.SetColumn(lines, 1); g.Children.Add(lines);
        return new Border { Child = g, Tag = f, ToolTip = f.Path };
    }

    private async void Select(FileActivity f)
    {
        _selected = f; _selectedChanges = f.Changes;
        _diffTitle.Text = f.Relative + "  ·  loading diff…";
        var path = f.Path;
        var diff = await Task.Run(() => _ws.Diff(path));
        if (_selected?.Path != path) return;
        _diffTitle.Text = f.Relative + (f.Branch is null ? "" : "  ·  " + f.Branch);
        _diff.Show(diff);
    }
}

/// <summary>Coloured unified diff (virtualized, monospace): + green, − red, @@ hunks cyan.</summary>
public sealed class DiffViewer : ListBox
{
    public DiffViewer()
    {
        FontFamily = Ui.Mono; FontSize = 12;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(this, true);
    }

    public void Show(string diff)
    {
        Items.Clear();
        foreach (var raw in diff.Split('\n').Take(20_000))
        {
            var l = raw.TrimEnd('\r');
            var (fg, bg) = l.StartsWith("+++") || l.StartsWith("---") ? (Ui.Dim, Brushes.Transparent)
                : l.StartsWith('+') ? (Ui.Green, Ui.B("#0E2219")) : l.StartsWith('-') ? (Ui.Red, Ui.B("#2A1116"))
                : l.StartsWith("@@") ? (Ui.Accent, Brushes.Transparent) : l.StartsWith("diff ") || l.StartsWith("index ") ? (Ui.Faint, Brushes.Transparent) : (Ui.Fg, Brushes.Transparent);
            Items.Add(new Border { Background = bg, Padding = new Thickness(10, 0, 10, 0), Child = new TextBlock { Text = l.Length == 0 ? " " : l, Foreground = fg, FontFamily = Ui.Mono } });
        }
    }
}
