using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Cleaner;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

public sealed partial class CleanerView
{
    private List<CategoryScan> _scan = [];
    private readonly HashSet<string> _selected = new(StorageCleaner.Categories().Where(c => c.DefaultSelected).Select(c => c.Id));
    private CancellationTokenSource? _storageCts;
    private readonly ProgressBar _storageBar = new() { Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 4), IsIndeterminate = true };
    private readonly TextBlock _storageStatus = Ui.Muted("", 12);
    private readonly StackPanel _categoryList = new(), _review = new(), _storageResult = new(), _quarantinePanel = new();

    private void RenderStorage()
    {
        _storage.Children.Clear();
        _storage.Children.Add(Ui.Section("Storage Cleanup", "Scan → review → clean selected. Only approved locations are scanned; personal folders, browser data, Installer/WinSxS, restore points and anything in use are never touched."));
        if (!Admin.IsElevated)
        {
            var warn = new StackPanel();
            warn.Children.Add(Ui.Muted("Running without administrator rights: Windows temp, servicing logs, system crash dumps, the Delivery Optimization cache and Windows Update cleanup can only be scanned and cleaned as administrator. Your own temp files, caches and the Recycle Bin work now.", 12));
            warn.Children.Add(Ui.Row(Ui.Btn("Restart as administrator", (_, _) => { if (Admin.RestartElevated()) Application.Current.Shutdown(); }, "GoldButton", new Thickness(0, 6, 0, 0))));
            _storage.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), BorderBrush = Ui.Brush("Gold"), Margin = new Thickness(0, 0, 0, 10), Child = warn });
        }
        var scanBtn = Ui.Btn(_scan.Count == 0 ? "Scan" : "Scan again", async (_, _) => await ScanStorageAsync(), "PrimaryButton", new Thickness(0, 0, 8, 0));
        var stop = Ui.Btn("Stop", (_, _) => _storageCts?.Cancel(), "DangerButton", new Thickness(0, 0, 8, 0));
        stop.IsEnabled = _storageCts is not null;
        scanBtn.IsEnabled = _storageCts is null;
        var analyze = Ui.Btn("Analyze Windows Update cleanup", async (_, _) => await AnalyzeComponentStoreAsync(), "GhostButton", new Thickness(0, 0, 8, 0));
        analyze.ToolTip = "Runs DISM /AnalyzeComponentStore (a few minutes, administrator) to see whether Windows recommends a component cleanup and how much it holds.";
        analyze.IsEnabled = Admin.IsElevated && _storageCts is null;
        _storage.Children.Add(Ui.Row(scanBtn, stop, analyze));
        _storage.Children.Add(_storageBar);
        _storage.Children.Add(_storageStatus);
        _storage.Children.Add(_categoryList);
        _storage.Children.Add(_review);
        _storage.Children.Add(_storageResult);
        _storage.Children.Add(_quarantinePanel);
        var notOffered = new Expander { Header = Ui.Text("Not offered, on purpose", 12.5, FontWeights.SemiBold), Margin = new Thickness(0, 12, 0, 0) };
        var no = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (what, why) in StorageCleaner.NotOffered) no.Children.Add(Ui.Muted($"• {what}: {why}", 11.5));
        notOffered.Content = no;
        _storage.Children.Add(notOffered);
        RenderExclusions(_storage, "Never clean (your folder exclusions)", _s.StorageExclusions, @"folder, e.g. %TEMP%\MyToolCache", () => { RenderStorage(); return Task.CompletedTask; });
        RenderCategories();
        RenderQuarantine();
    }

    private async Task ScanStorageAsync()
    {
        if (_storageCts is not null) return;
        _storageCts = new CancellationTokenSource();
        RenderStorage();
        _storageBar.Visibility = Visibility.Visible;
        _storageResult.Children.Clear(); _review.Children.Clear();
        var sw = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<(string category, string location, int files)>(p => _storageStatus.Text = $"Scanning {p.category}: {p.files:N0} file(s){(p.location.Length > 0 ? " · " + p.location : "")}");
            _scan = await StorageCleaner.ScanAsync(StorageCleaner.Categories(), _s.StorageExclusions, Admin.IsElevated, progress, _storageCts.Token);
            _storageStatus.Text = $"Scanned in {sw.Elapsed.TotalSeconds:0.0} s. Review each category before cleaning; nothing has been changed.";
        }
        catch (OperationCanceledException) { _storageStatus.Text = "Scan stopped. Nothing was changed."; }
        catch (Exception ex) { _storageStatus.Text = "Scan failed: " + ex.Message; Log.Error("storage scan", ex); }
        finally { _storageCts.Dispose(); _storageCts = null; _storageBar.Visibility = Visibility.Collapsed; }
        RenderStorage();
    }

    private async Task AnalyzeComponentStoreAsync()
    {
        _storageStatus.Text = "DISM is analyzing the component store (a few minutes)…";
        _storageBar.Visibility = Visibility.Visible;
        var (ok, summary) = await Task.Run(StorageCleaner.AnalyzeComponentStore);
        _storageBar.Visibility = Visibility.Collapsed;
        _storageStatus.Text = summary;
        if (_scan.FirstOrDefault(s => s.Category.Id == "component-store") is { } cs) cs.Note = summary;
        RenderCategories();
    }

    private void RenderCategories()
    {
        _categoryList.Children.Clear();
        if (_scan.Count == 0) { _categoryList.Children.Add(Ui.Muted("Not scanned yet. Scan only reads; nothing is deleted until you choose Clean selected and confirm.", 12)); _reclaim.Text = "Not scanned"; return; }
        foreach (var s in _scan) _categoryList.Children.Add(CategoryRow(s));
        long total = _scan.Where(s => _selected.Contains(s.Category.Id)).Sum(s => Math.Max(0, s.Bytes));
        _reclaim.Text = StorageCleaner.Size(total);
        var clean = Ui.Btn($"Clean selected ({StorageCleaner.Size(total)})", async (_, _) => await CleanStorageAsync(), "PrimaryButton", new Thickness(0, 10, 8, 0));
        clean.IsEnabled = _storageCts is null;
        var q = new CheckBox { Content = $"Quarantine files for {StorageCleaner.QuarantineDays} days so they can be restored (space is freed when the quarantine is emptied)", IsChecked = _s.Quarantine, Margin = new Thickness(0, 12, 0, 0) };
        q.Click += (_, _) => { _s.Quarantine = q.IsChecked == true; _s.Save(); };
        _categoryList.Children.Add(q);
        _categoryList.Children.Add(Ui.Row(clean));
        _categoryList.Children.Add(Ui.Muted("Sizes are what the scan found. What is actually removed can be less: files that are in use, changed or locked at clean time are skipped, and the report says so.", 11.5));
    }

    private UIElement CategoryRow(CategoryScan s)
    {
        var c = s.Category;
        bool needsAdmin = c.NeedsAdmin && !Admin.IsElevated;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cb = new CheckBox { IsChecked = _selected.Contains(c.Id), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0),
            IsEnabled = !(needsAdmin && c.Kind != StorageKind.Files) && (s.Count > 0 || s.Estimated || c.Kind != StorageKind.Files) };
        cb.Click += (_, _) => { if (cb.IsChecked == true) _selected.Add(c.Id); else _selected.Remove(c.Id); RenderCategories(); };
        grid.Children.Add(cb);
        var info = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Text(c.Name, 13.5, FontWeights.SemiBold));
        head.Children.Add(new Border { Width = 8 });
        if (c.Optional) head.Children.Add(Ui.Tag("OPTIONAL", "Gold"));
        if (needsAdmin) head.Children.Add(Ui.Tag("NEEDS ADMIN", "Gold"));
        if (s.Partial) head.Children.Add(Ui.Tag("PARTIAL", "Gold"));
        if (c.Kind != StorageKind.Files) head.Children.Add(Ui.Tag(c.Kind == StorageKind.RecycleBin ? "SHELL API" : "WINDOWS TOOL", "Violet"));
        info.Children.Add(head);
        info.Children.Add(Ui.Muted(c.Why, 12));
        if (c.Note.Length > 0) info.Children.Add(new TextBlock { Text = c.Note, FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap });
        if (s.Note is { } n) info.Children.Add(Ui.Muted(n, 11.5));
        if (s.Locations.Count > 0) info.Children.Add(new TextBlock { Text = string.Join("\n", s.Locations), FontSize = 11, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (s.Skipped.Count > 0) info.Children.Add(Ui.Muted("Left out: " + string.Join(", ", s.Skipped.OrderByDescending(k => k.Value).Select(k => $"{k.Value:N0} {k.Key}")), 11));
        Grid.SetColumn(info, 1); grid.Children.Add(info);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(new TextBlock { Text = s.Estimated && s.Bytes == 0 ? "size: Windows" : StorageCleaner.Size(s.Bytes), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ui.Brush(s.Bytes > 0 ? "Text" : "Subtle"), HorizontalAlignment = HorizontalAlignment.Right });
        right.Children.Add(Ui.Muted(c.Kind == StorageKind.Files ? $"{s.Count:N0} file(s)" : c.Kind == StorageKind.RecycleBin ? $"{s.Count:N0} item(s)" : "", 11.5));
        if (s.Files.Count > 0)
        {
            var rv = Ui.Btn("Review files", (_, _) => ShowReview(s), "GhostButton", new Thickness(0, 4, 0, 0));
            rv.Padding = new Thickness(8, 2, 8, 2); rv.FontSize = 11.5;
            right.Children.Add(rv);
        }
        Grid.SetColumn(right, 2); grid.Children.Add(right);
        return new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 0, 0), Background = Ui.Brush(_selected.Contains(c.Id) ? "AccentSoft" : "Surface"), Child = grid };
    }

    /// <summary>Inspection before deletion: the largest files of a category, with their location and age. Exclude a folder from here.</summary>
    private void ShowReview(CategoryScan s)
    {
        _review.Children.Clear();
        var sp = new StackPanel();
        var head = new DockPanel();
        var close = Ui.Btn("Close", (_, _) => _review.Children.Clear(), "GhostButton", new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(Ui.Text($"Review: {s.Category.Name} · {s.Count:N0} file(s) · {StorageCleaner.Size(s.Bytes)}", 14, FontWeights.SemiBold));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted("Largest first (up to 500 shown). Select a file and exclude its folder if you want to keep it; then scan again.", 11.5));
        var list = new ListBox { Height = 320, Margin = new Thickness(0, 6, 0, 6), Background = Ui.Brush("Surface"), Foreground = Ui.Brush("Text"), BorderThickness = new Thickness(0), FontSize = 11.5 };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        foreach (var f in s.Files.OrderByDescending(f => f.Bytes).Take(500))
            list.Items.Add(new ListBoxItem { Content = $"{StorageCleaner.Size(f.Bytes),10}   {(DateTime.UtcNow - f.LastWriteUtc).TotalDays,5:0} days   {f.Path}", Tag = f.Path });
        sp.Children.Add(list);
        sp.Children.Add(Ui.Row(Ui.Btn("Exclude this file's folder", (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string path }) return;
            var folder = Path.GetDirectoryName(path)!;
            if (!_s.StorageExclusions.Contains(folder, StringComparer.OrdinalIgnoreCase)) _s.StorageExclusions.Add(folder);
            _s.Save();
            MainWindow.Current?.ShowToast($"Excluded {folder}. Scan again to update the sizes.", "Accent");
            RenderStorage();
        }, "BaseButton", new Thickness(0, 0, 8, 0)),
            Ui.Btn("Open the folder", (_, _) =>
            {
                if (list.SelectedItem is ListBoxItem { Tag: string path })
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
            }, "GhostButton")));
        _review.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), Margin = new Thickness(0, 10, 0, 0), Child = sp });
    }

    private async Task CleanStorageAsync()
    {
        var chosen = _scan.Where(s => _selected.Contains(s.Category.Id) && !(s.Category.NeedsAdmin && !Admin.IsElevated && s.Category.Kind != StorageKind.Files)).ToList();
        chosen = chosen.Where(s => s.Category.Kind != StorageKind.Files || s.Files.Count > 0).ToList();
        if (chosen.Count == 0) { Dialog.Show("Nothing to clean", "Select at least one category that has something in it."); return; }
        var lines = chosen.Select(s => $"• {s.Category.Name}: {(s.Estimated && s.Bytes == 0 ? "size decided by Windows" : StorageCleaner.Size(s.Bytes))}" +
            (s.Category.Kind == StorageKind.Files ? $" ({s.Count:N0} files)" : "")).ToList();
        bool hasFiles = chosen.Any(s => s.Category.Kind == StorageKind.Files);
        if (!Dialog.Confirm("Clean the selected categories?", string.Join("\n", lines) +
                (hasFiles ? (_s.Quarantine ? $"\n\nFiles go to a quarantine on the same drive for {StorageCleaner.QuarantineDays} days (restorable; space is freed when it's emptied). Files on other drives are deleted permanently."
                                           : "\n\nFiles are deleted permanently (quarantine is off).") : "") +
                "\n\nEvery file is re-checked first: in use, changed since the scan, read-only, linked or protected files are skipped." +
                (chosen.Any(s => s.Category.Kind == StorageKind.ComponentStore) ? "\n\nWindows Update cleanup runs DISM and can take 10–30 minutes; it can't be stopped safely once started." : ""),
                "Clean", DialogKind.Warning)) return;
        if (chosen.FirstOrDefault(s => s.Category.Kind == StorageKind.RecycleBin) is { } rb &&
            !Dialog.Confirm("Empty the Recycle Bin permanently?", $"{rb.Count:N0} item(s), {StorageCleaner.Size(rb.Bytes)}. This can't be undone: Recycle Bin contents are not quarantined.", "Empty it", DialogKind.Warning))
            chosen.Remove(rb);
        if (chosen.Count == 0) return;

        _storageCts = new CancellationTokenSource();
        RenderStorage();
        _storageBar.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<(string category, int done, int total)>(p => _storageStatus.Text = $"Cleaning {p.category}: {p.done:N0} / {p.total:N0}");
            var report = await StorageCleaner.CleanAsync(chosen, _s.Quarantine, _s.StorageExclusions, progress, _storageCts.Token);
            _s.AddHistory($"Storage: freed {StorageCleaner.Size(report.FreedBytes)}, quarantined {StorageCleaner.Size(report.QuarantinedBytes)}" +
                          $" ({string.Join(", ", report.Results.Select(r => r.Category.Name))}){(report.Cancelled ? " — stopped part-way" : "")}");
            _s.Save();
            ShowStorageReport(report);
            _scan.Clear();
        }
        catch (Exception ex) { _storageStatus.Text = "Cleaning failed: " + ex.Message; Log.Error("storage clean", ex); }
        finally { _storageCts.Dispose(); _storageCts = null; _storageBar.Visibility = Visibility.Collapsed; }
        var result = _storageResult.Children.Cast<UIElement>().ToList();
        RenderStorage();
        foreach (var r in result) _storageResult.Children.Add(r);
        UpdateQuarantineTile();
        RenderHistory();
    }

    private void ShowStorageReport(StorageCleanReport r)
    {
        _storageResult.Children.Clear();
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(r.Cancelled ? "Stopped part-way: this is what was done" : "Cleanup result", 14, FontWeights.SemiBold));
        foreach (var x in r.Results)
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var head = new WrapPanel();
            head.Children.Add(Ui.Text(x.Category.Name, 12.5, FontWeights.SemiBold));
            if (x.Deleted > 0 || x.DeletedBytes > 0) head.Children.Add(Ui.Tag($"FREED {StorageCleaner.Size(x.DeletedBytes)}{(x.Deleted > 0 ? $" · {x.Deleted:N0}" : "")}", "Success"));
            if (x.Quarantined > 0) head.Children.Add(Ui.Tag($"QUARANTINED {StorageCleaner.Size(x.QuarantinedBytes)} · {x.Quarantined:N0}", "Accent"));
            if (x.Failed > 0) head.Children.Add(Ui.Tag($"FAILED {x.Failed:N0}", "Danger"));
            if (x.Deleted == 0 && x.DeletedBytes == 0 && x.Quarantined == 0 && x.Failed == 0) head.Children.Add(Ui.Tag("NOTHING REMOVED", "Muted"));
            row.Children.Add(head);
            if (x.Skipped.Count > 0) row.Children.Add(Ui.Muted("Skipped: " + string.Join(", ", x.Skipped.OrderByDescending(k => k.Value).Select(k => $"{k.Value:N0} {k.Key}")), 11.5));
            if (x.Note is { } n) row.Children.Add(Ui.Muted(n, 11.5));
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.Text("Free space (read from Windows)", 12.5, FontWeights.SemiBold));
        foreach (var (drive, (before, after)) in r.FreeSpace)
            sp.Children.Add(Ui.Muted($"{drive} {StorageCleaner.Size(before)} → {StorageCleaner.Size(after)} ({(after - before >= 0 ? "+" : "−")}{StorageCleaner.Size(Math.Abs(after - before))})", 12));
        if (r.QuarantinedBytes > 0) sp.Children.Add(Ui.Muted($"{StorageCleaner.Size(r.QuarantinedBytes)} is in quarantine and still uses space until the quarantine is emptied (below).", 11.5));
        sp.Children.Add(Ui.Muted("Free space also changes for other reasons (other apps, Windows), so the drive figure won't match the category totals exactly.", 11));
        _storageResult.Children.Add(new Border { Style = Ui.Res<Style>("CardBorder"), Margin = new Thickness(0, 10, 0, 0), Child = sp });
        _storageStatus.Text = $"Freed {StorageCleaner.Size(r.FreedBytes)}, quarantined {StorageCleaner.Size(r.QuarantinedBytes)}.";
    }

    private void RenderQuarantine()
    {
        _quarantinePanel.Children.Clear();
        var sessions = StorageCleaner.QuarantineSessions();
        if (sessions.Count == 0) return;
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(Ui.Text($"Quarantine ({StorageCleaner.Size(sessions.Sum(s => s.Bytes))})", 12.5, FontWeights.SemiBold));
        sp.Children.Add(Ui.Muted($"Emptied automatically after {StorageCleaner.QuarantineDays} days. Restore puts files back where they were (never over a newer file).", 11.5));
        foreach (var s in sessions)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(Ui.Text($"{s.At:g} · {s.Items.Count:N0} file(s) · {StorageCleaner.Size(s.Bytes)}", 12));
            var session = s;
            row.Children.Add(Ui.Btn("Restore", async (_, _) =>
            {
                var (n, problems) = await Task.Run(() => StorageCleaner.Restore(session));
                _s.AddHistory($"Quarantine restored: {n} file(s){(problems.Count > 0 ? $", {problems.Count} problem(s)" : "")}"); _s.Save();
                Dialog.Show(problems.Count == 0 ? "Restored" : "Partly restored", problems.Count == 0 ? $"{n} file(s) are back where they were." : $"{n} restored. Not restored:\n" + string.Join("\n", problems.Take(15)), problems.Count == 0 ? DialogKind.Info : DialogKind.Warning);
                RenderQuarantine(); UpdateQuarantineTile(); RenderHistory();
            }, "GhostButton", new Thickness(8, 0, 0, 0)));
            row.Children.Add(Ui.Btn("Delete now", async (_, _) =>
            {
                if (!Dialog.Confirm("Delete permanently?", $"{session.Items.Count:N0} file(s), {StorageCleaner.Size(session.Bytes)}. They can't be restored afterwards.", "Delete", DialogKind.Warning)) return;
                var (freed, failed) = await Task.Run(() => StorageCleaner.Purge(session));
                _s.AddHistory($"Quarantine emptied: {StorageCleaner.Size(freed)} freed{(failed > 0 ? $", {failed} couldn't be deleted" : "")}"); _s.Save();
                RenderQuarantine(); UpdateQuarantineTile(); RenderHistory();
            }, "GhostButton", new Thickness(4, 0, 0, 0)));
            sp.Children.Add(row);
        }
        _quarantinePanel.Children.Add(sp);
    }
}
