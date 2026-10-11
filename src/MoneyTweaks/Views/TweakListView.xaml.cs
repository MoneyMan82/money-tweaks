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
    private readonly TweakCategory _category;
    private readonly ObservableCollection<TweakItem> _items;
    private readonly ICollectionView _view;
    private string _filter = "all";
    private bool _loaded;

    public TweakListView(TweakCategory category)
    {
        InitializeComponent();
        _category = category;
        TitleText.Text = CategoryInfo.DisplayName(category);
        BlurbText.Text = CategoryInfo.Blurb(category);
        _items = new(TweakCatalog.InCategory(category).Select(t => new TweakItem(t)));
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakItem.Group)));
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

    private async Task RefreshAllAsync()
    {
        // Check states with limited parallelism: some checks spawn PowerShell/powercfg.
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(_items.Select(async i =>
        {
            await gate.WaitAsync();
            try { await i.RefreshAsync(); } finally { gate.Release(); }
        }));
        _view.Refresh();
        UpdateCount();
    }

    private bool Matches(object o)
    {
        var i = (TweakItem)o;
        var q = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(q) && !(i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            return false;
        return _filter switch
        {
            "applied" => i.State is TweakState.Applied or TweakState.Partial,
            "off" => i.State == TweakState.NotApplied,
            "safe" => !i.IsCaution,
            "caution" => i.IsCaution,
            _ => true
        };
    }

    private void UpdateCount()
    {
        var applied = _items.Count(i => i.State == TweakState.Applied);
        var na = _items.Count(i => i.State == TweakState.NotApplicable);
        CountText.Text = $"{applied} of {_items.Count} applied" + (na > 0 ? $" · {na} not applicable here" : "");
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _filter = tag;
        _view?.Refresh();
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
