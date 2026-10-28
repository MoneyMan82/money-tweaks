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

public partial class TweakListView : UserControl, IPageLifecycle, IAnchorTarget
{
    private bool _radarChecking;

    /// <summary>
    /// "tweaks#radar" runs the conflict check; "tweaks#xray" opens the first tweak's Inspect changes. Navigation can call this
    /// twice for one jump (now and once the page has loaded), so both are guarded.
    /// </summary>
    public void ShowSection(string anchor)
    {
        if (anchor == "nexus" && _nexusPicker is not null) _nexusPicker.Select("nexus");
        else if (anchor == "radar") _ = CheckConflictsAsync();
        else if (anchor == "xray" && _view.Cast<TweakItem>().FirstOrDefault(i => i.HasXRay) is { XRayOpen: false } first)
        {
            first.XRayOpen = true;
            MainWindow.Current?.ShowToast("Tweak X-Ray: every tweak card has “Inspect changes ›”. The first one is open below.", "Accent");
        }
    }

    private async void CheckConflicts_Click(object sender, RoutedEventArgs e) => await CheckConflictsAsync();

    /// <summary>Conflict Radar over what's applied now, in the order it was applied. Read-only: fixes are offered when applying.</summary>
    private async Task CheckConflictsAsync()
    {
        if (_radarChecking) return;
        _radarChecking = true;
        try { await CheckConflictsCoreAsync(); }
        finally { _radarChecking = false; }
    }

    private async Task CheckConflictsCoreAsync()
    {
        var applied = await Task.Run(() => Core.Catalog.TweakCatalog.All
            .Where(t => t.Kind == Core.Model.TweakKind.Toggle && Core.Engine.BackupStore.Load(t.Id) is not null)
            .OrderBy(t => Core.Engine.BackupStore.Load(t.Id)!.AppliedAt).ToList());
        var conflicts = await Task.Run(() => Core.Engine.ConflictRadar.Scan(applied, []));
        Core.Sys.Log.Info($"Conflict Radar check: {conflicts.Count} finding(s) among {applied.Count} applied tweak(s)");
        if (conflicts.Count == 0)
        {
            Dialog.Show("No conflicts found", $"Conflict Radar compared what your {applied.Count} applied tweak(s) write and found no duplicates, contradictions or order problems.\n\nIt also checks automatically every time you apply tweaks, before anything changes.");
            return;
        }
        ConflictRadarDialog.Show(conflicts, "", readOnly: true);
    }

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
        }
        else
        {
            EyebrowText.Text = "TWEAK LIBRARY";
            TitleText.Text = "All Tweaks";
            CatIcon.Text = "\uE8FD";
            BlurbText.Text = "Every Money Tweaks tweak in one place. Search, filter by category, status and risk, sort, read known limitations and conflicts, and apply or undo each one. Every change is backed up and read back.";
            CategoryChips.Visibility = Visibility.Visible;
            CategoryChips.Children.Add(CategoryChip("All categories", null, TweakCatalog.All.Count));
            foreach (var cat in Enum.GetValues<TweakCategory>())
                CategoryChips.Children.Add(CategoryChip(CategoryInfo.DisplayName(cat), cat, TweakCatalog.InCategory(cat).Count()));
        }
        var sort = new Controls.SegmentedPicker(
        [
            ("Default", "default", "The catalogue's own order, grouped"),
            ("Name A–Z", "name", null),
            ("Caution first", "risk", "Caution tweaks first, then by name"),
            ("Applied first", "status", null),
            ("Not applied first", "status-desc", null),
        ], "default");
        sort.Changed += Sort_Changed;
        SortRow.Children.Add(sort);
        _items = new((category is { } only ? TweakCatalog.InCategory(only) : TweakCatalog.All).Select(t => new TweakItem(t)));
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(category is null ? nameof(TweakItem.CategoryName) : nameof(TweakItem.Group)));
        _view.Filter = Matches;
        List.ItemsSource = _view;
        RecommendedButton.Visibility = _items.Any(i => i.IsRecommended) ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
        if (category == TweakCategory.Services) AddNexusMode();
    }

    private Controls.SegmentedPicker? _nexusPicker;
    private ServiceNexusPanel? _nexus;

    /// <summary>
    /// Services page: "Tweaks | Service Nexus". Nexus replaces the list in place (same page, same tweak items, so applying
    /// there updates the cards here); it reads the Service Control Manager the first time it's opened.
    /// </summary>
    private void AddNexusMode()
    {
        _nexusPicker = new Controls.SegmentedPicker(
        [
            ("Tweaks", "list", "The service tweaks as cards"),
            ("◈  Service Nexus", "nexus", "Dependency graph from the Service Control Manager, with an impact preview before you change a service"),
        ], "list");
        _nexusPicker.Changed += async mode =>
        {
            bool nexus = mode == "nexus";
            List.Visibility = nexus ? Visibility.Collapsed : Visibility.Visible;
            FilterDock.Visibility = SortRow.Visibility = CountDock.Visibility = nexus ? Visibility.Collapsed : Visibility.Visible;
            NexusHost.Visibility = nexus ? Visibility.Visible : Visibility.Collapsed;
            if (!nexus) return;
            if (_nexus is null)
            {
                _nexus = new ServiceNexusPanel(id => _items.FirstOrDefault(i => i.Tweak.Id == id));
                NexusHost.Content = _nexus;
            }
            if (!_nexus.HasData) await _nexus.LoadAsync();
        };
        ModeRow.Children.Add(_nexusPicker);
        ModeRow.Visibility = Visibility.Visible;
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
            "tier-adv" => i.IsAdvancedTier,
            "tier-exp" => i.IsExperimental,
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
        if (_view is null) return;
        // Searching opens every group; clearing the search folds the remembered ones again.
        foreach (var gi in Controls.Ui.Descendants<GroupItem>(List))
            if (gi.DataContext is CollectionViewGroup g) ApplyGroup(gi, IsCollapsed(g), animate: false);
    }

    private RadioButton CategoryChip(string label, TweakCategory? cat, int count)
    {
        var chip = new RadioButton
        {
            Style = (Style)FindResource("Chip"), GroupName = "cat", IsChecked = cat is null, Margin = new Thickness(0, 0, 6, 6),
            Content = new TextBlock { Inlines = { new System.Windows.Documents.Run(label), new System.Windows.Documents.Run($"  {count}") { Foreground = (System.Windows.Media.Brush)FindResource("Subtle") } } },
        };
        chip.Checked += (_, _) =>
        {
            _categoryFilter = cat;
            _view?.Refresh();
            if (_view is not null) UpdateCount();
        };
        return chip;
    }

    private void Sort_Changed(string sortKey)
    {
        if (_view is null) return;
        _view.SortDescriptions.Clear();
        switch (sortKey)
        {
            case "name": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
            case "risk": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.IsCaution), ListSortDirection.Descending)); _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
            case "status": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.StatusRank), ListSortDirection.Ascending)); _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
            case "status-desc": _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.StatusRank), ListSortDirection.Descending)); _view.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending)); break;
        }
    }

    // ---- collapsible groups ----------------------------------------------------------------------------------------

    private static Core.Features.AppSettings S => App.Settings;
    private string GroupKey(object? name) => $"{_category?.ToString() ?? "all"}|{name}";
    /// <summary>While searching, every group shows its matches so nothing is hidden behind a folded heading.</summary>
    private bool Searching => !string.IsNullOrWhiteSpace(SearchBox?.Text);
    private bool IsCollapsed(CollectionViewGroup g) => !Searching && S.CollapsedTweakGroups.Contains(GroupKey(g.Name));

    private static (FrameworkElement? body, UIElement? chev, UIElement? hint) Parts(GroupItem gi) =>
        (gi.Template?.FindName("body", gi) as FrameworkElement,
         (gi.Template?.FindName("PART_Header", gi) as Button)?.Content is DockPanel dp ? dp.Children[0] : null,
         (gi.Template?.FindName("PART_Header", gi) as Button)?.Content is DockPanel dp2 ? dp2.Children[^1] : null);

    /// <summary>Open, or opening; a body that is still animating closed counts as closed.</summary>
    private static bool IsOpen(FrameworkElement body) => body.Visibility == Visibility.Visible && body.Tag as string != "closing";

    /// <summary>Shows a group folded or open; animated when the user clicks, instant when a container (re)appears.</summary>
    private void ApplyGroup(GroupItem gi, bool collapsed, bool animate)
    {
        gi.ApplyTemplate();
        var (body, chev, hint) = Parts(gi);
        if (body is null) return;
        if (chev is not null) Controls.Motion.Rotate(chev, collapsed ? -90 : 0, animate ? 200 : 0);
        if (hint is not null) hint.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (collapsed == !IsOpen(body)) return;
        if (!animate)
        {
            body.BeginAnimation(HeightProperty, null); body.Height = double.NaN; body.BeginAnimation(OpacityProperty, null); body.Opacity = 1;
            body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            body.Tag = collapsed ? "closing" : null;
            return;
        }
        // Long groups only animate the part that is on screen, so the motion stays quick and smooth.
        double viewport = Math.Max(200, List.ActualHeight);
        if (collapsed)
        {
            body.Tag = "closing";
            if (body.ActualHeight > viewport) body.Height = viewport;
            Controls.Motion.Collapse(body, 230);
        }
        else
        {
            body.Tag = null;
            body.Visibility = Visibility.Visible;
            body.Measure(new Size(Math.Max(1, body.ActualWidth > 0 ? body.ActualWidth : List.ActualWidth), viewport));
            Controls.Motion.Expand(body, 260);
        }
    }

    private void Group_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not GroupItem gi) return;
        // The virtualizing list reuses group containers for other groups without a new Loaded, so follow the group too.
        gi.DataContextChanged -= Group_DataContextChanged;
        gi.DataContextChanged += Group_DataContextChanged;
        if (gi.DataContext is CollectionViewGroup g) ApplyGroup(gi, IsCollapsed(g), animate: false);
    }

    private void Group_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is GroupItem gi && e.NewValue is CollectionViewGroup g) ApplyGroup(gi, IsCollapsed(g), animate: false);
    }

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { TemplatedParent: GroupItem { DataContext: CollectionViewGroup g } gi }) return;
        var key = GroupKey(g.Name);
        bool collapse = Parts(gi).body is { } body && IsOpen(body); // what's on screen decides, so a quick second click reverses it
        S.CollapsedTweakGroups.Remove(key);
        if (collapse) S.CollapsedTweakGroups.Add(key);
        S.Save();
        ApplyGroup(gi, collapse, animate: true);
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllGroups(true);
    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllGroups(false);

    private void SetAllGroups(bool collapse)
    {
        foreach (var g in _view.Groups?.OfType<CollectionViewGroup>() ?? [])
        {
            var key = GroupKey(g.Name);
            S.CollapsedTweakGroups.Remove(key);
            if (collapse) S.CollapsedTweakGroups.Add(key);
        }
        S.Save();
        // Containers on screen animate (with a small cascade); the rest pick the state up when they're created.
        int i = 0;
        foreach (var gi in Controls.Ui.Descendants<GroupItem>(List))
        {
            if (gi.DataContext is not CollectionViewGroup) continue;
            var item = gi; int delay = Math.Min(i++, 8) * 25;
            if (delay == 0 || !Controls.Motion.Enabled) ApplyGroup(item, collapse, animate: true);
            else Dispatcher.InvokeAsync(async () => { await Task.Delay(delay); ApplyGroup(item, collapse, animate: true); });
        }
        if (collapse) (VisualTreeHelperFind<ScrollViewer>(List))?.ScrollToTop();
    }

    private static T? VisualTreeHelperFind<T>(DependencyObject root) where T : DependencyObject => Controls.Ui.Descendants<T>(root).FirstOrDefault();

    /// <summary>"What changes" opens with a short fade and rise instead of popping in.</summary>
    /// <summary>Related setting clicked in a tweak's details: open it where it lives.</summary>
    private void Related_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) MainWindow.Current?.OpenTweak(id);
        e.Handled = true;
    }

    private void Details_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement el && e.NewValue is true) Controls.Motion.FadeIn(el, 0, 6, 220);
    }

    /// <summary>Builds the evidence panel each time a tweak's details open, so it reflects the latest measurements.</summary>
    private void Evidence_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ContentControl { DataContext: TweakItem item } host) return;
        if (e.NewValue is not true || item.Tweak.Kind != Core.Model.TweakKind.Toggle) { host.Content = null; return; }
        try { host.Content = EvidencePanel.Build(item.Tweak); }
        catch (Exception ex) { Core.Sys.Log.Error("Evidence panel failed", ex); host.Content = null; }
    }

    /// <summary>Tweak X-Ray: reads the tweak's real locations and values each time the inspection opens.</summary>
    private void XRay_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ContentControl { DataContext: TweakItem item } host) return;
        if (e.NewValue is true) XRayPanel.Fill(host, item.Tweak);
        else host.Content = null;
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
        var rec = _items.Where(i => i.IsRecommended && i.CanToggle && i.State is TweakState.NotApplied or TweakState.Partial).ToList();
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
            if (ok + failed == 0) { CountText.Text = "Nothing to change."; return; }
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
