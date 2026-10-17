using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Legacy Potato (LOD bias 0x20) and comparison presets applied to Fortnite's NVIDIA profile by .nip import, with
/// Apply / Verify / Restore Previous Profile / Reset and a per-field report read back from the driver.
/// </summary>
public sealed partial class PotatoView
{
    private readonly NvProfileState _np = NvProfileState.Load();
    private readonly StackPanel _lgBody = new(), _lgReport = new();
    private NvPreset _lgPreset = NvidiaProfilePresets.Legacy;
    private string? _inspector;
    private IReadOnlyDictionary<uint, InspectorSetting>? _meta;
    private bool _useInspector, _lgBusy;

    private UIElement BuildLegacySection()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Driver-profile presets", Style = Ui.Res<Style>("H2"), FontSize = 15, Margin = new Thickness(0, 4, 0, 6) });
        sp.Children.Add(_lgBody);
        sp.Children.Add(_lgReport);
        _lgBody.Children.Add(Ui.Loading("Checking Fortnite's NVIDIA profile…"));
        _ = InitLegacyAsync();
        return sp;
    }

    private async Task InitLegacyAsync()
    {
        _inspector = await Task.Run(() => NvidiaProfilePresets.FindInspector(_np.InspectorPath));
        await LoadMetaAsync();
        _useInspector = _inspector is not null && _np.InspectorPath is not null;
        RenderLegacy();
        await VerifyLegacyAsync(quiet: true);
    }

    private void RenderLegacy()
    {
        _lgBody.Children.Clear();
        var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var p in NvidiaProfilePresets.All)
        {
            var preset = p;
            var rb = new RadioButton { Content = p.Name, Style = Ui.Res<Style>("Chip"), GroupName = "nvlegacy", IsChecked = p == _lgPreset, Margin = new Thickness(0, 0, 8, 6), ToolTip = p.Description };
            rb.Checked += async (_, _) => { if (_lgPreset == preset) return; _lgPreset = preset; RenderLegacy(); await VerifyLegacyAsync(quiet: true); };
            chips.Children.Add(rb);
        }
        _lgBody.Children.Add(chips);
        _lgBody.Children.Add(Ui.Muted(_lgPreset.Description, 12));

        // The exact fields, values and IDs (resolved from the driver's own setting names when applied/verified).
        var table = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var w in new[] { 2.4, 1.0, 1.6, 3.2 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Cell(int r, int c, string text, bool head = false, Brush? fg = null)
        {
            var t = new TextBlock { Text = text, FontSize = head ? 11 : 12, FontWeight = head ? FontWeights.Bold : FontWeights.Normal, Foreground = fg ?? Ui.Brush(head ? "Subtle" : "Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 10, 3) };
            if (c == 1) t.FontFamily = Ui.Res<FontFamily>("MonoFont");
            Grid.SetRow(t, r); Grid.SetColumn(t, c); table.Children.Add(t);
        }
        table.RowDefinitions.Add(new RowDefinition());
        Cell(0, 0, "FIELD", true); Cell(0, 1, "ID", true); Cell(0, 2, "VALUE", true); Cell(0, 3, "NOTE", true);
        int row = 1;
        foreach (var i in _lgPreset.Items)
        {
            table.RowDefinitions.Add(new RowDefinition());
            Cell(row, 0, i.Field + (i.Mandatory ? "  (mandatory)" : ""));
            Cell(row, 1, NvidiaProfilePresets.Hex(i.ExpectedId));
            Cell(row, 2, $"{NvidiaProfilePresets.Hex(i.Value)}  {(i.ValueLabel == NvidiaProfilePresets.Hex(i.Value) ? "" : i.ValueLabel)}", fg: i.Mandatory ? Ui.Brush("Gold") : null);
            Cell(row, 3, i.Note, fg: Ui.Brush("Muted"));
            row++;
        }
        _lgBody.Children.Add(new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // Import method: NVIDIA Profile Inspector only if the user has it; never downloaded.
        var insp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (_inspector is null)
            insp.Children.Add(Ui.Muted("NVIDIA Profile Inspector: not found on this PC (Money Tweaks never downloads it). The preset is imported as a .nip through NVAPI, the same driver API Inspector uses, and every backup is also saved as a .nip you can open in Inspector.", 11.5));
        else
        {
            var cb = new CheckBox { Content = $"Import with NVIDIA Profile Inspector ({_inspector})", IsChecked = _useInspector, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 0, 0, 2) };
            cb.Click += (_, _) => { _useInspector = cb.IsChecked == true; NvidiaProfilePresets.TrustInspector(_np, _useInspector ? _inspector : null); SaveNp(); };
            insp.Children.Add(cb);
            insp.Children.Add(Ui.Muted("Inspector asks for administrator rights and may show its own \"import done\" message. Money Tweaks reads the profile back afterwards either way.", 11.5));
        }
        var locate = Ui.Btn(_inspector is null ? "Locate NVIDIA Profile Inspector…" : "Choose another Inspector…", (_, _) => LocateInspector(), "GhostButton", new Thickness(0, 4, 8, 0));
        insp.Children.Add(Ui.Row(locate));
        _lgBody.Children.Add(insp);

        var actions = Ui.Row(
            Ui.Btn("Apply…", async (_, _) => await ApplyLegacyAsync(), "PrimaryButton"),
            Ui.Btn("Verify", async (_, _) => await VerifyLegacyAsync(quiet: false)),
            Ui.Btn("Restore Previous Profile…", async (_, _) => await RestoreLegacyAsync()),
            Ui.Btn("Reset…", async (_, _) => await ResetLegacyAsync(), "GhostButton"),
            Ui.Btn("Open backup folder", (_, _) => { Directory.CreateDirectory(NvidiaProfilePresets.BackupDir); Process.Start(new ProcessStartInfo("explorer.exe", NvidiaProfilePresets.BackupDir) { UseShellExecute = true }); }, "GhostButton"));
        actions.Margin = new Thickness(0, 12, 0, 0);
        foreach (var b in actions.Children.OfType<Button>()) b.IsEnabled = !_lgBusy;
        ((Button)actions.Children[2]).IsEnabled = !_lgBusy && NvidiaProfilePresets.LoadSnapshot(_np.LastBackupJson) is not null;
        _lgBody.Children.Add(actions);
        if (NvidiaProfilePresets.LoadSnapshot(_np.LastBackupJson) is { } snap)
            _lgBody.Children.Add(Ui.Muted($"Previous profile backup: '{snap.ProfileName}', {snap.Settings.Count} settings, {snap.At:g}.", 11.5));
    }

    private async Task LoadMetaAsync()
    {
        var exe = _inspector;
        _meta = exe is null ? null : await Task.Run(() =>
        {
            try { var m = InspectorMetadata.Load(exe); return m.Count > 0 ? m : null; }
            catch (Exception ex) { Log.Warn("Inspector metadata unreadable: " + ex.Message); return null; }
        });
    }

    private void SaveNp()
    {
        try { _np.Save(); } catch (Exception ex) { Log.Error("Saving nvidia-profile.json failed", ex); }
    }

    private void LocateInspector()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Locate nvidiaProfileInspector.exe", Filter = "NVIDIA Profile Inspector|nvidiaProfileInspector.exe|Programs|*.exe" };
        if (dlg.ShowDialog() != true) return;
        _inspector = dlg.FileName;
        _useInspector = true;
        NvidiaProfilePresets.TrustInspector(_np, dlg.FileName);
        SaveNp();
        _ = LoadMetaAsync().ContinueWith(_ => Dispatcher.Invoke(RenderLegacy));
    }

    private static string StatusTone(NvItemStatus s) => s switch
    {
        NvItemStatus.Applied or NvItemStatus.AlreadySet => "Success",
        NvItemStatus.Unsupported => "Muted",
        NvItemStatus.Rejected => "Danger",
        _ => "Gold",
    };

    private static string StatusText(NvItemStatus s) => s switch
    {
        NvItemStatus.Applied => "Applied",
        NvItemStatus.AlreadySet => "Set",
        NvItemStatus.Unsupported => "Unsupported",
        NvItemStatus.Rejected => "Rejected",
        _ => "Not applied",
    };

    private void ShowReport(NvReport r, string title)
    {
        _lgReport.Children.Clear();
        var box = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Tag(r.Ok ? "VERIFIED IN DRIVER" : "NOT VERIFIED", r.Ok ? "Success" : "Danger"));
        head.Children.Add(Ui.Text(title, 13.5, FontWeights.SemiBold));
        box.Children.Add(head);
        box.Children.Add(new TextBlock { Text = r.Summary, FontSize = 12.5, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        // The in-game effect is judged separately from the saved values.
        var render = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        render.Children.Add(Ui.Tag(r.Render switch { RenderSupport.Supported => "Renderer: override can apply", RenderSupport.LikelyIgnored => "Renderer: override likely ignored", _ => "Renderer: unknown" },
            r.Render switch { RenderSupport.Supported => "Accent", RenderSupport.LikelyIgnored => "Danger", _ => "Muted" }));
        render.Children.Add(new TextBlock { Text = r.RenderText, FontSize = 12, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, VerticalAlignment = VerticalAlignment.Center });
        box.Children.Add(render);
        box.Children.Add(Ui.Muted("Money Tweaks can confirm what the driver profile contains, not what Fortnite draws. Check in game after restarting Fortnite (take a before/after screenshot).", 11.5));

        foreach (var i in r.Items)
        {
            var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(Ui.Tag(StatusText(i.Status), StatusTone(i.Status)));
            line.Children.Add(new TextBlock
            {
                Text = $"{i.Item.Field} = {NvidiaProfilePresets.Hex(i.Item.Value)} · {i.Detail}" + (i.Before is { } b || i.After is not null ? $" · before {(i.Before is { } x ? NvidiaProfilePresets.Hex(x) : "not set")}, now {(i.After is { } y ? NvidiaProfilePresets.Hex(y) : "not set")}" : ""),
                FontSize = 12, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, MaxWidth = 900, VerticalAlignment = VerticalAlignment.Center,
            });
            box.Children.Add(line);
            if (i.Inspector is { } note) box.Children.Add(new TextBlock { Text = "    " + note, FontSize = 11, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap });
        }
        if (r.UnrelatedChanges.Count > 0)
            box.Children.Add(new TextBlock { Text = "Unrelated changes detected (rolled back):\n" + string.Join("\n", r.UnrelatedChanges), FontSize = 12, Foreground = Ui.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        box.Children.Add(Ui.Muted($"Display: {r.Display}", 11.5));
        box.Children.Add(Ui.Muted($"Method: {r.Method}" + (r.BackupNip is null ? "" : $" · backup: {r.BackupNip}") + (r.AppliedNip is null ? "" : $" · imported: {r.AppliedNip}"), 11.5));
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 0), Child = box,
        };
        _lgReport.Children.Add(border);
    }

    private async Task VerifyLegacyAsync(bool quiet)
    {
        if (_lgBusy) return;
        try
        {
            var preset = _lgPreset;
            var meta = _meta;
            var r = await Task.Run(() => NvidiaProfilePresets.Annotate(NvidiaProfilePresets.Verify(preset), meta));
            ShowReport(r, "Verify: " + preset.Name);
        }
        catch (Exception ex) when (!quiet) { Dialog.Show("Couldn't read the NVIDIA profile", ex.Message, DialogKind.Error); }
        catch (Exception ex) { Log.Warn("NVIDIA verify failed: " + ex.Message); _lgReport.Children.Clear(); _lgReport.Children.Add(Ui.Muted("Couldn't read the NVIDIA profile: " + ex.Message, 12)); }
    }

    private async Task ApplyLegacyAsync()
    {
        if (_lgBusy) return;
        var preset = _lgPreset;
        var (render, renderText) = await Task.Run<(RenderSupport, string)>(() =>
        {
            var g = FortnitePotato.Game;
            try { return NvidiaProfilePresets.RenderingMode(g.Installed ? File.ReadAllText(g.ConfigPath) : null); } catch (Exception ex) { return (RenderSupport.Unknown, ex.Message); }
        });
        var msg = new StringBuilder($"Writes these values to Fortnite's own NVIDIA profile ({NvidiaFortnite.Exe}). The global profile and every other NVIDIA setting stay as they are.\n\n");
        foreach (var i in preset.Items) msg.Append($"• {i.Field}: {NvidiaProfilePresets.Hex(i.Value)} ({i.ValueLabel})\n");
        msg.Append("\nEach field's ID is looked up from the driver's own setting name first; unsupported or mismatched fields are skipped and reported, and if the mandatory LOD bias can't be written nothing is changed.");
        msg.Append("\n\nThe whole profile is backed up first (JSON + .nip). After the import, every value is read back; if a mandatory value is missing or anything unrelated changed, the backup is imported again automatically.");
        msg.Append("\n\nLOD bias 0x00000020 makes textures, including skins, very flat and blurry, and can change how they look in colour (you saw green-looking skins with it before). Geometry is unchanged. Nothing about how skins look is guaranteed.");
        msg.Append("\n\nRenderer: " + renderText + (render == RenderSupport.LikelyIgnored ? "\nThe profile will be correct, but you may see no change in game." : ""));
        if (PotatoGraphics.IsRunning(FortnitePotato.Game)) msg.Append("\n\nFortnite is running: restart it for the driver profile to load.");
        if (!Dialog.Confirm($"Apply {preset.Name}?", msg.ToString(), "Apply", DialogKind.Warning)) return;
        await RunLegacyAsync($"Apply: {preset.Name}", () => NvidiaProfilePresets.Apply(preset, _np, inspector: _useInspector ? _inspector : null));
    }

    private async Task RunLegacyAsync(string title, Func<NvReport> work)
    {
        _lgBusy = true;
        RenderLegacy();
        _lgReport.Children.Clear();
        _lgReport.Children.Add(Ui.Loading("Working with the NVIDIA driver…"));
        try
        {
            var meta = _meta;
            var r = await Task.Run(() => NvidiaProfilePresets.Annotate(work(), meta));
            ShowReport(r, title);
            MainWindow.Current?.ShowToast(r.Ok ? "NVIDIA profile updated and verified. Restart Fortnite." : "NVIDIA profile not changed: see the report.", r.Ok ? "Success" : "Gold");
        }
        catch (NvApiException ex) { _lgReport.Children.Clear(); Dialog.Show("The NVIDIA driver refused", ex.Message + "\n\nNothing was saved to the driver.", DialogKind.Error); }
        catch (Exception ex) { _lgReport.Children.Clear(); Log.Error("NVIDIA preset failed", ex); Dialog.Show("Couldn't change the NVIDIA profile", ex.Message, DialogKind.Error); }
        finally { _lgBusy = false; RenderLegacy(); }
    }

    private async Task RestoreLegacyAsync()
    {
        if (_lgBusy || NvidiaProfilePresets.LoadSnapshot(_np.LastBackupJson) is not { } snap) return;
        var text = snap.CreatedProfile
            ? $"Deletes '{NvidiaFortnite.OwnProfileName}', which Money Tweaks created."
            : $"Imports the backup of '{snap.ProfileName}' from {snap.At:g} ({snap.Settings.Count} settings) and makes the profile match it exactly: settings added since are removed, changed ones go back.\n\n" +
              string.Join("\n", snap.Settings.Where(s => !s.Predefined).Select(s => $"• {(s.Name.Length > 0 ? s.Name : NvidiaProfilePresets.Hex(s.Id))} = {(s.Type == 0 ? NvidiaProfilePresets.Hex(s.Value) : s.Text)}"));
        if (!Dialog.Confirm("Restore previous profile?", text + "\n\nThe result is read back and compared with the backup.", "Restore", DialogKind.Warning)) return;
        _lgBusy = true;
        RenderLegacy();
        try
        {
            var log = await Task.Run(() => NvidiaProfilePresets.RestoreSnapshot(snap));
            Dialog.Show("Previous profile restored", string.Join("\n", log));
        }
        catch (Exception ex) { Log.Error("NVIDIA restore failed", ex); Dialog.Show("Restore incomplete", ex.Message + $"\n\nThe backup is also saved as a .nip in {NvidiaProfilePresets.BackupDir}.", DialogKind.Error); }
        finally { _lgBusy = false; RenderLegacy(); }
        await VerifyLegacyAsync(quiet: true);
        await RefreshNvidiaAsync();
    }

    private async Task ResetLegacyAsync()
    {
        if (_lgBusy) return;
        if (!Dialog.Confirm("Reset to NVIDIA's values?",
                "Removes Money Tweaks' presets from Fortnite's NVIDIA profile: these settings go back to NVIDIA's own value for Fortnite (or the global default):\n\n" +
                string.Join("\n", NvidiaProfilePresets.ManagedIds.Select(id => "• " + (NvidiaProfilePresets.All.SelectMany(p => p.Items).First(i => i.ExpectedId == id).Field) + " (" + NvidiaProfilePresets.Hex(id) + ")")) +
                "\n\nEverything else in the profile (V-Sync, frame limits, NVIDIA's predefined values) is not touched. A backup is not needed for this, but Restore Previous Profile stays available.",
                "Reset", DialogKind.Warning)) return;
        _lgBusy = true;
        RenderLegacy();
        try
        {
            var log = await Task.Run(() => NvidiaProfilePresets.Reset());
            Dialog.Show("Reset done", log.Count == 0 ? "Nothing to reset." : string.Join("\n", log));
        }
        catch (Exception ex) { Log.Error("NVIDIA reset failed", ex); Dialog.Show("Reset incomplete", ex.Message, DialogKind.Error); }
        finally { _lgBusy = false; RenderLegacy(); }
        await VerifyLegacyAsync(quiet: true);
        await RefreshNvidiaAsync();
    }
}
