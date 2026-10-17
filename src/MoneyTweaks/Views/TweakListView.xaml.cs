using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

public partial class TweakListView : UserControl, IPageLifecycle
{
    /// <summary>null = the all-tweaks library (every category in one list).</summary>
    private readonly TweakCategory? _category;
    private TweakCategory? _categoryFilter;
    private readonly ObservableCollection<TweakItem> _items;
    private readonly ICollectionView _view;
    private string _filter = "all";
    private bool _loaded;

    /// <summary>The whole tweak library: every category, with category filter and sorting.</summary>
    public TweakListView() : this(null) { }

    public TweakListView(TweakCategory category) : this((TweakCategory?)category) { }

    private TweakListView(TweakCategory? category)
    {
        InitializeComponent();
        _category = category;
        if (category is { } c)
        {
            TitleText.Text = CategoryInfo.DisplayName(c);
            CatIcon.Text = MainWindow.CategoryIcons[c];
            BlurbText.Text = CategoryInfo.Blurb(c);
            CategoryLabel.Visibility = CategoryBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            EyebrowText.Text = "TWEAK LIBRARY";
            TitleText.Text = "All Tweaks";
            CatIcon.Text = "\uE8FD";
            BlurbText.Text = "Every Money Tweaks tweak in one place. Search, filter by category, status and risk, sort, read known limitations and conflicts, and apply or undo each one. Every change is backed up and read back.";
            CategoryBox.Items.Add(new ComboBoxItem { Content = "All categories", Tag = null, IsSelected = true });
            foreach (var cat in Enum.GetValues<TweakCategory>())
                CategoryBox.Items.Add(new ComboBoxItem { Content = $"{CategoryInfo.DisplayName(cat)} ({TweakCatalog.InCategory(cat).Count()})", Tag = cat });
        }
        _items = new((category is { } only ? TweakCatalog.InCategory(only) : TweakCatalog.All).Select(t => new TweakItem(t)));
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(category is null ? nameof(TweakItem.CategoryName) : nameof(TweakItem.Group)));
        _view.Filter = Matches;
        List.ItemsSource = _view;
        RecommendedButton.Visibility = _items.Any(i => i.IsRecommended) ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    public async void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAllAsync();
    }

    public void OnHidden() { }

    private bool Scanning
    {
        set
        {
            ScanBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ScanBar.Value = 0;
            if (value) CountText.Text = $"Checking {_items.Count} tweaks on this PC…";
        }
    }

    private void ScanProgress(int done, int total)
    {
        ScanBar.Maximum = Math.Max(1, total);
        ScanBar.Value = done;
        CountText.Text = $"Checking {done}/{total} tweaks on this PC…";
    }

    /// <summary>Used by the command palette: filter the list down to one tweak by name.</summary>
    public void FocusTweak(string name)
    {
        SearchBox.Text = name;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async Task RefreshAllAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Scanning = true;
        await TweakService.RefreshStatesAsync(_items, new Progress<int>(n => ScanProgress(n, _items.Count)));
        Scanning = false;
        _view.Refresh();
        UpdateCount();
        Core.Sys.Log.Info($"[perf] state scan {(_category?.ToString() ?? "all")} ({_items.Count} tweaks): {sw.ElapsedMilliseconds} ms");
    }

    private bool Matches(object o)
    {
        var i = (TweakItem)o;
        var q = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(q) && !(i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Tweak.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Group.Contains(q, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_categoryFilter is { } cf && i.Tweak.Category != cf) return false;
        return _filter switch
        {
            "applied" => i.State is TweakState.Applied or TweakState.Partial,
            "off" => i.State == TweakState.NotApplied,
            "safe" => !i.IsCaution,
            "caution" => i.IsCaution,
            "rec" => i.IsRecommended,
            "notes" => i.HasNotes,
            _ => true
        };
    }

    private void UpdateCount()
    {
        var applied = _items.Count(i => i.State == TweakState.Applied);
        var na = _items.Count(i => i.State == TweakState.NotApplicable);
        var relevant = _items.Count - na;
        CountText.Text = $"{_view.Cast<object>().Count()} shown" + (na > 0 ? $" · {na} not applicable on this PC" : "");
        AppliedBig.Text = $"{applied} / {relevant}";
        AppliedBar.Maximum = Math.Max(1, relevant);
        Controls.Ui.Animate(AppliedBar, System.Windows.Controls.Primitives.RangeBase.ValueProperty, applied, 500);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _filter = tag;
        _view?.Refresh();
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        _categoryFilter = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as TweakCategory?;
        _view?.Refresh();
        if (_view is not null) UpdateCount();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_view is null) return;
        _view.SortDescriptions.Clear();
        switch ((SortBox.SelectedItem as ComboBoxItem)?.Tag as string)
        {
            case "name": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
            case "risk": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.IsCaution), ListSortDirection.Descending)); _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
            case "status": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.StatusRank), ListSortDirection.Ascending)); _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
            case "status-desc": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.StatusRank), ListSortDirection.Descending)); _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
        }
    }

    /// <summary>Rollback for one tweak: restores its backed-up values through the same verified path as Undo all.</summary>
    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TweakItem item } || !item.CanUndo) return;
        if (!Dialog.Confirm($"Undo “{item.Name}”?", "Restores the original values that were backed up when it was applied (or the Windows default if there is no backup), then reads them back.", "Undo", DialogKind.Warning)) return;
        await RunBatch([item], false);
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: TweakItem item } tb) return;
        // Let the engine decide the real state; snap the switch back until it reports.
        tb.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        await TweakService.ToggleAsync(item);
        tb.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        UpdateCount();
        if (item.NeedsReboot && item.State == TweakState.Applied) MainWindow.Current?.ShowToast("Applied. Restart Windows for it to take full effect.");
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TweakItem item } || item.Busy) return;
        var r = await TweakService.VerifyAsync(item);
        item.Expanded = true;
        UpdateCount();
        bool canLive = Core.Engine.TweakVerifier.CanRoundTrip(item.Tweak, out _) && item.State == TweakState.NotApplied;
        var kind = r.Status switch { Core.Engine.VerifyStatus.Fail => DialogKind.Error, Core.Engine.VerifyStatus.Warning => DialogKind.Warning, _ => DialogKind.Info };
        var text = r.ToText() + (canLive ? "\n\nThis read-only test changed nothing. A live test applies the tweak, reads it back, undoes it and checks every original value is restored." : "");
        if (Dialog.Show($"Test: {r.StatusLabel}", text, kind, canLive ? "Run live test…" : "OK", canLive ? "Close" : null) && canLive)
        {
            var live = await TweakService.RoundTripAsync(item);
            if (live is not null) Dialog.Show($"Live test: {live.StatusLabel}", live.ToText(), live.Status == Core.Engine.VerifyStatus.Fail ? DialogKind.Error : DialogKind.Info);
            UpdateCount();
        }
    }

    private void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TweakItem item }) BenchmarkView.Open("tweak", item.Tweak.Id);
    }

    private async void ApplyRecommended_Click(object sender, RoutedEventArgs e)
    {
        var rec = _items.Where(i => i.IsRecommended && i.State == TweakState.NotApplied).ToList();
        if (rec.Count == 0) { MainWindow.Current?.ShowToast("All recommended tweaks in this category are already applied."); return; }
        var names = string.Join("\n", rec.Select(r => "• " + r.Name));
        if (!Dialog.Confirm($"Apply {rec.Count} recommended tweaks?", names + "\n\nEach one is backed up and can be undone individually.", "Apply")) return;
        await RunBatch(rec, true);
    }

    private async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        var on = _items.Where(i => i.State is TweakState.Applied or TweakState.Partial && !i.IsGuide).ToList();
        if (on.Count == 0) { MainWindow.Current?.ShowToast("Nothing to undo in this category."); return; }
        if (!Dialog.Confirm($"Undo {on.Count} tweaks?", "This restores the original values that were backed up when each tweak was applied (or the Windows default if no backup exists).", "Undo all", DialogKind.Warning)) return;
        await RunBatch(on, false);
    }

    private async Task RunBatch(List<TweakItem> items, bool apply)
    {
        IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => CountText.Text = s);
            var (ok, failed, reboot) = await TweakService.BatchAsync(items, apply, progress);
            var msg = $"{ok} {(apply ? "applied" : "reverted")}" + (failed > 0 ? $", {failed} failed (see the message on each card)" : "") + (reboot ? ". Restart Windows to finish." : ".");
            MainWindow.Current?.ShowToast(msg);
        }
        finally
        {
            IsEnabled = true;
            _view.Refresh();
            UpdateCount();
        }
    }
}
