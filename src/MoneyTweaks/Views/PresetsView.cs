using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>Reusable settings presets: built-in and custom sets of catalog tweaks, applied through the normal engine.</summary>
public sealed class PresetsView : UserControl, IPageLifecycle
{
    private PresetFile _file = PresetStore.Load();
    private readonly StackPanel _builtIn = new(), _custom = new();
    private readonly StackPanel _lists = new();
    private readonly Border _editorHost = new() { Visibility = Visibility.Collapsed };
    private bool _busy;

    public PresetsView()
    {
        var actions = Ui.Row(
            Ui.Btn("+  New preset", (_, _) => OpenEditor(null), "PrimaryButton"),
            Ui.Btn("Import…", (_, _) => Import()),
            Ui.Btn("Export my presets…", (_, _) => Export(_file.Presets), margin: new Thickness(0, 0, 0, 8)));
        var (root, body) = Ui.Page("Presets",
            "Apply a whole set of tweaks in one go, or build your own. Every preset uses the same safe engine as the switches: admin check, restore point, a backup for each tweak, and Undo restores exactly what the preset changed. Preview shows every registry value, service or command before anything happens.",
            actions, eyebrow: "Gaming tools", icon: "\uE8F1");
        body.Children.Add(_editorHost);
        _lists.Children.Add(new TextBlock { Text = "Built-in presets", Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 4, 0, 12) });
        _lists.Children.Add(_builtIn);
        _lists.Children.Add(new TextBlock { Text = "Your presets", Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 10, 0, 12) });
        _lists.Children.Add(_custom);
        _lists.Children.Add(Ui.Muted("Keyboard/mouse macros aren't included: automated input can break games' rules and anti-cheat, so Money Tweaks only offers settings presets. Presets don't promise specific FPS or ping gains; results depend on your PC.", 11.5));
        body.Children.Add(_lists);
        Content = root;
        Rebuild();
    }

    private IEnumerable<TweakPreset> All => PresetStore.BuiltIns.Concat(_file.Presets);

    private void Rebuild()
    {
        _builtIn.Children.Clear();
        _custom.Children.Clear();
        var g1 = (ResponsiveGrid)Ui.Grid(2, PresetStore.BuiltIns.Select(Card).ToArray());
        g1.MinItemWidth = 360;
        _builtIn.Children.Add(g1);
        if (_file.Presets.Count == 0)
            _custom.Children.Add(Ui.Card(Ui.Empty("\uE710", "No custom presets yet", "Create one with “New preset”, duplicate a built-in one, or import a file someone shared."), new Thickness(0, 0, 14, 14)));
        else
        {
            var g2 = (ResponsiveGrid)Ui.Grid(2, _file.Presets.OrderBy(p => p.Name).Select(Card).ToArray());
            g2.MinItemWidth = 360;
            _custom.Children.Add(g2);
        }
    }

    private UIElement Card(TweakPreset p)
    {
        var tweaks = p.TweakIds.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => t!).ToList();
        var sp = new StackPanel();
        var head = new DockPanel();
        var kind = Ui.Tag(p.BuiltIn ? "Built-in" : "Custom", p.BuiltIn ? "Violet" : "Accent");
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(kind);
        head.Children.Add(Ui.Text(p.Name, 15, FontWeights.SemiBold));
        sp.Children.Add(head);
        var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        tags.Children.Add(Ui.Tag($"{tweaks.Count} tweaks", "Muted"));
        var caution = tweaks.Count(t => t.Risk == Risk.Caution);
        if (caution > 0) tags.Children.Add(Ui.Tag($"{caution} Caution", "Gold"));
        if (tweaks.Any(t => t.RequiresAdmin)) tags.Children.Add(Ui.Tag("Admin", "Muted"));
        if (tweaks.Any(t => t.RequiresReboot)) tags.Children.Add(Ui.Tag("Restart", "Muted"));
        sp.Children.Add(tags);
        sp.Children.Add(Ui.Muted(string.IsNullOrWhiteSpace(p.Description) ? "No description." : p.Description, 12.5));
        var lastRun = _file.History.LastOrDefault(r => r.PresetId == p.Id);
        if (lastRun is not null) { var lr = Ui.Muted($"Last applied {lastRun.At:g}: {lastRun.AppliedIds.Count} tweak(s) changed.", 11.5); lr.Margin = new Thickness(0, 6, 0, 0); sp.Children.Add(lr); }

        var row = Ui.Row(Ui.Btn("Apply", async (_, _) => await ApplyAsync(p), "PrimaryButton"), Ui.Btn("Preview", async (_, _) => await PreviewAsync(p)));
        row.Margin = new Thickness(0, 14, 0, 0);
        if (lastRun is not null) row.Children.Add(Ui.Btn("Undo last apply", async (_, _) => await UndoAsync(p, lastRun)));
        row.Children.Add(Ui.Btn("Duplicate", (_, _) => Duplicate(p), "GhostButton"));
        if (!p.BuiltIn)
        {
            row.Children.Add(Ui.Btn("Edit / rename", (_, _) => OpenEditor(p), "GhostButton"));
            row.Children.Add(Ui.Btn("Export", (_, _) => Export([p]), "GhostButton"));
            row.Children.Add(Ui.Btn("Delete", (_, _) => Delete(p), "GhostButton"));
        }
        sp.Children.Add(row);
        return Ui.Card(sp, new Thickness(0, 0, 14, 14));
    }

    // ---- preview / apply / undo -------------------------------------------------------------------------------
    private async Task<List<TweakItem>> StatesAsync(TweakPreset p)
    {
        var items = p.TweakIds.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
        await TweakService.RefreshStatesAsync(items);
        return items;
    }

    private static string Describe(List<TweakItem> items, bool withOps)
    {
        var sb = new StringBuilder();
        var todo = items.Where(i => i.State is TweakState.NotApplied or TweakState.Partial).ToList();
        var done = items.Where(i => i.State == TweakState.Applied).ToList();
        var na = items.Where(i => i.State is TweakState.NotApplicable or TweakState.Unknown).ToList();
        sb.AppendLine(todo.Count == 0 ? "Nothing to change: everything in this preset is already active." : $"Will change ({todo.Count}):");
        foreach (var i in todo)
        {
            sb.AppendLine($"• {i.Name}{(i.IsCaution ? "  ⚠ Caution" : "")}{(i.NeedsReboot ? "  (restart)" : "")}");
            if (withOps)
                foreach (var op in i.Tweak.Ops) sb.AppendLine($"     {op.Describe()}");
            if (i.IsCaution && i.Tweak.Warning is { } w) sb.AppendLine($"     ⚠ {w}");
        }
        if (done.Count > 0) sb.AppendLine($"\nAlready active ({done.Count}): {string.Join(", ", done.Select(d => d.Name))}");
        if (na.Count > 0) sb.AppendLine($"\nNot available on this PC ({na.Count}): {string.Join(", ", na.Select(d => $"{d.Name} ({d.StatusText})"))}");
        return sb.ToString().TrimEnd();
    }

    private async Task PreviewAsync(TweakPreset p)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var items = await StatesAsync(p);
            Dialog.Show($"Preview: {p.Name}", Describe(items, withOps: true) + "\n\nNothing has been changed. Each tweak keeps its own backup when applied.");
        }
        finally { _busy = false; }
    }

    private async Task ApplyAsync(TweakPreset p)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var items = await StatesAsync(p);
            var todo = items.Where(i => i.State is TweakState.NotApplied or TweakState.Partial).ToList();
            if (todo.Count == 0) { MainWindow.Current?.ShowToast($"“{p.Name}” is already fully active.", "Success"); return; }
            var hasCaution = todo.Any(i => i.IsCaution);
            if (!Dialog.Confirm($"Apply “{p.Name}”?", Describe(items, withOps: false) +
                    "\n\nA restore point is created first (admin, System Protection on). Undo on this card reverts exactly the tweaks this apply changes.",
                    "Apply", hasCaution ? DialogKind.Warning : DialogKind.Confirm)) return;
            var before = todo.Select(i => i.Tweak.Id).ToHashSet();
            var (ok, failed, reboot) = await TweakService.BatchAsync(todo, true);
            var applied = todo.Where(i => i.State == TweakState.Applied).Select(i => i.Tweak.Id).Where(before.Contains).ToList();
            if (applied.Count > 0)
            {
                _file.History.Add(new PresetRun { PresetId = p.Id, PresetName = p.Name, AppliedIds = applied });
                SaveFile();
            }
            Log.Info($"Preset '{p.Name}' applied: {ok} ok, {failed} failed");
            MainWindow.Current?.ShowToast($"{p.Name}: {ok} applied{(failed > 0 ? $", {failed} failed (see the Activity Log)" : "")}.{(reboot ? " Restart Windows to finish." : "")}", failed > 0 ? "Gold" : "Success");
            Rebuild();
        }
        finally { _busy = false; }
    }

    private async Task UndoAsync(TweakPreset p, PresetRun run)
    {
        if (_busy) return;
        if (!Dialog.Confirm($"Undo “{p.Name}”?", $"Reverts the {run.AppliedIds.Count} tweak(s) this preset changed on {run.At:g}, restoring their backed-up original values. Tweaks that were already active before are left alone.", "Undo", DialogKind.Warning)) return;
        _busy = true;
        try
        {
            var items = run.AppliedIds.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
            await TweakService.RefreshStatesAsync(items);
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, false);
            if (failed == 0) { _file.History.Remove(run); SaveFile(); }
            MainWindow.Current?.ShowToast($"{p.Name}: {ok} reverted{(failed > 0 ? $", {failed} failed (kept in history so you can retry)" : "")}.{(reboot ? " Restart Windows to finish." : "")}", failed > 0 ? "Gold" : "Success");
            Rebuild();
        }
        finally { _busy = false; }
    }

    // ---- manage ---------------------------------------------------------------------------------------------
    private void SaveFile()
    {
        try { PresetStore.Save(_file); }
        catch (Exception ex) { Log.Error("Saving presets failed", ex); Dialog.Show("Couldn't save presets", ex.Message, DialogKind.Error); }
    }

    private void Duplicate(TweakPreset p)
    {
        var name = p.Name + " (copy)";
        for (int i = 2; All.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{p.Name} (copy {i})";
        var copy = p.Copy(name);
        _file.Presets.Add(copy);
        SaveFile();
        Rebuild();
        OpenEditor(copy);
    }

    private void Delete(TweakPreset p)
    {
        if (!Dialog.Confirm($"Delete “{p.Name}”?", "Only the preset is deleted. Tweaks it applied stay applied and can still be undone from their switches or Backup & Restore.", "Delete", DialogKind.Warning)) return;
        _file.Presets.RemoveAll(x => x.Id == p.Id);
        SaveFile();
        Rebuild();
    }

    private void Export(IEnumerable<TweakPreset> presets)
    {
        var list = presets.ToList();
        if (list.Count == 0) { MainWindow.Current?.ShowToast("There are no custom presets to export yet."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Money Tweaks presets|*.mtpresets.json|JSON|*.json", FileName = list.Count == 1 ? Safe(list[0].Name) + ".mtpresets.json" : "my-presets.mtpresets.json" };
        if (dlg.ShowDialog() != true) return;
        try { PresetStore.Export(list, dlg.FileName); MainWindow.Current?.ShowToast($"Exported {list.Count} preset(s).", "Success"); }
        catch (Exception ex) { Dialog.Show("Export failed", ex.Message, DialogKind.Error); }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Money Tweaks presets|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        var (presets, problems) = PresetStore.Import(dlg.FileName, All);
        if (presets.Count == 0)
        {
            Dialog.Show("Nothing imported", string.Join("\n", problems.DefaultIfEmpty("The file had no presets.")), DialogKind.Warning);
            return;
        }
        _file.Presets.AddRange(presets);
        SaveFile();
        Rebuild();
        var msg = $"Imported: {string.Join(", ", presets.Select(p => p.Name))}.";
        if (problems.Count > 0) Dialog.Show("Imported with notes", msg + "\n\n" + string.Join("\n", problems), DialogKind.Warning);
        else MainWindow.Current?.ShowToast(msg, "Success");
    }

    // ---- editor ---------------------------------------------------------------------------------------------
    public sealed class PickItem(Tweak t, bool selected) : Observable
    {
        public Tweak Tweak { get; } = t;
        public string Name => Tweak.Name;
        public string Group => CategoryInfo.DisplayName(Tweak.Category);
        public string Info => (Tweak.Risk == Risk.Caution ? "Caution · " : "") + Tweak.Description;
        private bool _selected = selected;
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
    }

    private static readonly DataTemplate PickTemplate = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <CheckBox IsChecked="{Binding Selected}" Margin="0,4">
            <StackPanel>
              <TextBlock Text="{Binding Name}" FontWeight="SemiBold" FontSize="13"/>
              <TextBlock Text="{Binding Info}" FontSize="11.5" Foreground="{DynamicResource Muted}" TextTrimming="CharacterEllipsis" MaxWidth="720"/>
            </StackPanel>
          </CheckBox>
        </DataTemplate>
        """);

    private void OpenEditor(TweakPreset? existing)
    {
        var editing = existing ?? new TweakPreset();
        var picks = new ObservableCollection<PickItem>(TweakCatalog.All.Where(t => t.Kind == TweakKind.Toggle).Select(t => new PickItem(t, editing.TweakIds.Contains(t.Id))));
        var view = CollectionViewSource.GetDefaultView(picks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickItem.Group)));

        var name = new TextBox { Text = editing.Name, MaxLength = PresetStore.MaxNameLength };
        var desc = new TextBox { Text = editing.Description, MaxLength = PresetStore.MaxDescriptionLength, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 60 };
        var search = new TextBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var onlySelected = new CheckBox { Content = "Show selected only", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var count = Ui.Muted("", 12);
        var errors = new TextBlock { Foreground = Ui.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        void UpdateCount() => count.Text = $"{picks.Count(p => p.Selected)} selected";
        foreach (var p in picks) p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PickItem.Selected)) UpdateCount(); };
        view.Filter = o =>
        {
            var i = (PickItem)o;
            if (onlySelected.IsChecked == true && !i.Selected) return false;
            var q = search.Text.Trim();
            return q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Group.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        search.TextChanged += (_, _) => view.Refresh();
        onlySelected.Click += (_, _) => view.Refresh();
        UpdateCount();

        var list = new ListBox
        {
            ItemsSource = view, ItemTemplate = PickTemplate, Height = 360, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 10, 0, 0)
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        header.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        header.SetValue(TextBlock.ForegroundProperty, Ui.Brush("Accent"));
        header.SetValue(MarginProperty, new Thickness(0, 10, 0, 4));
        list.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });

        var sp = new StackPanel();
        sp.Children.Add(Ui.Section(existing is null ? "New preset" : $"Edit “{existing.Name}”", "Name it, describe it, and tick the tweaks it should apply."));
        sp.Children.Add(Ui.Text("Name", 12.5, FontWeights.SemiBold)); sp.Children.Add(name);
        var dl = Ui.Text("Description (optional)", 12.5, FontWeights.SemiBold); dl.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(dl); sp.Children.Add(desc);
        var bar = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(count, Dock.Right);
        count.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(count);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        search.Padding = new Thickness(32, 8, 10, 8);
        var searchBox = new Grid();
        searchBox.Children.Add(search);
        var hint = new TextBlock { Text = "Search tweaks", Foreground = Ui.Brush("Subtle"), Margin = new Thickness(33, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchBox.Children.Add(hint);
        searchBox.Children.Add(new TextBlock { Text = "\uE721", Style = Ui.Res<Style>("Icon"), FontSize = 13, Foreground = Ui.Brush("Muted"), Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false });
        left.Children.Add(searchBox); left.Children.Add(onlySelected);
        bar.Children.Add(left);
        sp.Children.Add(bar);
        sp.Children.Add(list);
        sp.Children.Add(errors);
        sp.Children.Add(Ui.Row(
            Ui.Btn("Save preset", (_, _) =>
            {
                var candidate = new TweakPreset { Id = editing.Id, Name = name.Text.Trim(), Description = desc.Text.Trim(), TweakIds = picks.Where(p => p.Selected).Select(p => p.Tweak.Id).ToList() };
                var problems = PresetStore.Validate(candidate, All);
                if (problems.Count > 0) { errors.Text = string.Join("\n", problems); return; }
                candidate.Modified = DateTime.Now;
                var idx = _file.Presets.FindIndex(x => x.Id == candidate.Id);
                if (idx >= 0) _file.Presets[idx] = candidate; else _file.Presets.Add(candidate);
                SaveFile();
                CloseEditor();
                Rebuild();
                MainWindow.Current?.ShowToast($"Saved “{candidate.Name}”.", "Success");
            }, "PrimaryButton"),
            Ui.Btn("Cancel", (_, _) => CloseEditor())));
        _editorHost.Child = Ui.Card(sp, new Thickness(0, 0, 14, 18));
        _editorHost.Visibility = Visibility.Visible;
        _lists.Visibility = Visibility.Collapsed;
        name.Focus();
    }

    internal void ShowEditorForScreenshot() => OpenEditor(PresetStore.BuiltIns[1].Copy("Example: my latency preset"));
    internal void CloseEditorForScreenshot() => CloseEditor();

    private void CloseEditor()
    {
        _editorHost.Child = null;
        _editorHost.Visibility = Visibility.Collapsed;
        _lists.Visibility = Visibility.Visible;
    }

    public void OnShown() { _file = PresetStore.Load(); if (_editorHost.Visibility != Visibility.Visible) Rebuild(); }
    public void OnHidden() { }
}
