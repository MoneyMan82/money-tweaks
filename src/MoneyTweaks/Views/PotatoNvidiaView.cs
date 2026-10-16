using System.Text;
using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// NVIDIA texture-filtering controls for Fortnite's driver profile (NVAPI DRS). Values are read from the driver, every
/// write is confirmed, checked against the driver's own setting names and values, backed up and verified.
/// </summary>
public sealed partial class PotatoView
{
    private readonly NvFortniteState _nv = NvFortniteState.Load();
    private readonly StackPanel _nvBody = new();
    private Dictionary<string, uint?> _nvSel = [];
    private NvidiaFortnite.Status? _nvStatus;
    private bool _nvBusy;

    private UIElement BuildNvidiaCard()
    {
        _nvSel = _nv.Values.Count > 0 ? new(_nv.Values) : NvidiaFortnite.Preset("Default");
        var sec = Ui.Section("NVIDIA texture filtering for Fortnite",
            "Real NVIDIA driver settings, written to Fortnite's profile in the driver (the same database NVIDIA Control Panel and NVIDIA Profile Inspector edit). Only settings this driver itself reports, with documented values, can be written. The originals are backed up and can be restored.");
        sec.Children.Add(_nvBody);
        _nvBody.Children.Add(Ui.Loading("Reading the NVIDIA driver profile…"));
        _ = RefreshNvidiaAsync();
        return Ui.Card(sec);
    }

    private async Task RefreshNvidiaAsync()
    {
        _nvStatus = await Task.Run(() => NvidiaFortnite.Read());
        RenderNvidia();
    }

    private string NvPresetName()
    {
        foreach (var p in NvidiaFortnite.Presets)
        {
            var v = NvidiaFortnite.Preset(p);
            if (v.All(kv => _nvSel.TryGetValue(kv.Key, out var x) && x == kv.Value)) return p;
        }
        return "Custom";
    }

    private void RenderNvidia()
    {
        _nvBody.Children.Clear();
        var st = _nvStatus!;
        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.Tag(st.Available ? "NVIDIA driver found" : "Not available", st.Available ? "Success" : "Muted"));
        head.Children.Add(new TextBlock { Text = st.Message, FontSize = 12.5, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 760 });
        _nvBody.Children.Add(head);
        if (!st.Available) { _nvBody.Children.Add(Ui.Muted("AMD and Intel GPUs: use the manual steps in the next card.", 12)); return; }

        // Presets
        var presets = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var current = NvPresetName();
        var tips = new Dictionary<string, string>
        {
            ["Default"] = "Removes Money Tweaks' overrides: NVIDIA's own values apply again.",
            ["Mild Potato"] = "High-performance filtering, optimizations on, negative LOD clamped, LOD bias +0.375.",
            ["Extreme Potato"] = "Same, with LOD bias +0.5 for flatter textures.",
        };
        foreach (var p in NvidiaFortnite.Presets.Append("Custom"))
        {
            var rb = new RadioButton { Content = p, Style = Ui.Res<Style>("Chip"), GroupName = "nvpreset", IsChecked = current == p, Margin = new Thickness(0, 0, 8, 6), IsEnabled = p != "Custom" };
            if (tips.TryGetValue(p, out var tip)) rb.ToolTip = tip;
            var name = p;
            rb.Checked += (_, _) => { if (name == "Custom" || NvPresetName() == name) return; _nvSel = NvidiaFortnite.Preset(name); RenderNvidia(); };
            presets.Children.Add(rb);
        }
        _nvBody.Children.Add(presets);

        var plan = NvidiaFortnite.Plan(_nvSel, st);
        foreach (var s in NvidiaFortnite.Controls) _nvBody.Children.Add(NvRow(s, st, plan.FirstOrDefault(p => p.Setting.Id == s.Id)));
        var drv = plan.First(p => p.Setting.Id == NvidiaFortnite.DriverLodBiasId);
        _nvBody.Children.Add(Ui.Muted($"Also set automatically: {drv.Setting.Label} → {NvidiaFortnite.Describe(drv.Setting, drv.Target)} (the driver ignores a manual LOD bias while it controls it). Now in driver: {Now(drv.Setting, drv.Current)}.", 11.5));

        // What changes what
        var info = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Brush("Muted"),
                Text = "Texture detail vs geometry:\n" +
                       "• Texture detail: LOD bias (DirectX) is the only setting here that makes textures visibly flatter. Positive values make the GPU use smaller texture levels, so surfaces, skins and the map look blurrier and simpler.\n" +
                       "• Texture filtering: Quality, anisotropic sample optimization, trilinear optimization and negative LOD bias change how textures are sampled (sharpness at angles, shimmer, small FPS cost). They don't change what a texture shows.\n" +
                       "• Geometry: none of these driver settings change models, builds or terrain shapes. The rounded low-poly look comes from Fortnite's own Performance mode with Meshes: Low (the studio above).\n" +
                       "• No guarantee about skins: how a skin looks with a LOD bias depends on its textures and materials. Some may look washed out or differently coloured; Fortnite can also change its rendering in any update. Take a before/after screenshot and restore if you don't like it.\n" +
                       "• Undocumented flags are never written. Values like 0x00000020 (a +4.0 LOD bias, which made skins look green and flat) are refused even if typed into a profile elsewhere.\n" +
                       "• Takes effect the next time Fortnite starts. Driver LOD bias works in DirectX 11 (Performance mode); DirectX 12 may ignore it.",
            },
        };
        _nvBody.Children.Add(info);

        var actions = Ui.Row(Ui.Btn("Review & apply to NVIDIA profile…", async (_, _) => await ApplyNvidiaAsync(), "PrimaryButton"));
        var restore = Ui.Btn("Restore original values", async (_, _) => await RestoreNvidiaAsync());
        restore.IsEnabled = _nv.Backup is not null;
        actions.Children.Add(restore);
        actions.Children.Add(Ui.Btn("Reset to NVIDIA defaults", async (_, _) => { _nvSel = NvidiaFortnite.Preset("Default"); RenderNvidia(); await ApplyNvidiaAsync(); }, "GhostButton"));
        actions.Children.Add(Ui.Btn("Refresh", async (_, _) => await RefreshNvidiaAsync(), "GhostButton"));
        actions.Margin = new Thickness(0, 14, 0, 0);
        foreach (var b in actions.Children.OfType<Button>()) b.IsEnabled &= !_nvBusy;
        _nvBody.Children.Add(actions);
        if (_nv.Backup is { } bk)
            _nvBody.Children.Add(Ui.Muted($"Backup of the original values from {bk.At:g} (profile '{bk.ProfileName}'{(bk.CreatedProfile ? ", created by Money Tweaks" : "")}).", 11.5));
    }

    private static string Now(NvTextureSetting s, NvSettingState? c) =>
        c is null ? "not set (driver default)" : c.InProfile ? NvidiaFortnite.Describe(s, c.Current) + (c.IsCurrentPredefined ? " (NVIDIA's value for Fortnite)" : "") : NvidiaFortnite.Describe(s, c.Current) + " (inherited)";

    private UIElement NvRow(NvTextureSetting s, NvidiaFortnite.Status st, NvPlanItem? item)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var head = new WrapPanel();
        head.Children.Add(Ui.Text(s.Label, 13, FontWeights.SemiBold));
        head.Children.Add(new Border { Width = 8 });
        bool texture = s.Id == NvidiaFortnite.LodBiasDxId;
        head.Children.Add(Ui.Tag(s.Id == NvidiaFortnite.LodBiasOglId ? "No effect in Fortnite" : texture ? "Texture detail" : "Texture filtering", s.Id == NvidiaFortnite.LodBiasOglId ? "Muted" : texture ? "Gold" : "Accent"));
        head.Children.Add(Ui.Tag("Geometry: unchanged", "Muted"));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted(s.Explanation, 12));

        string? unsupported = null;
        if (s.Writable && st.Driver.TryGetValue(s.Id, out var d) && !string.Equals(d.name, s.DriverName, StringComparison.Ordinal))
            unsupported = $"This driver doesn't report this setting (expected '{s.DriverName}'), so it can't be changed here.";
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), IsEnabled = s.Writable && unsupported is null };
        var group = "nv-" + s.Key;
        _nvSel.TryGetValue(s.Key, out var sel);
        foreach (var c in s.Choices)
        {
            var choice = c;
            var rb = new RadioButton { Content = c.Label, Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = sel == c.Value, Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => { if (_nvSel.TryGetValue(s.Key, out var v) && v == choice.Value) return; _nvSel[s.Key] = choice.Value; RenderNvidia(); };
            chips.Children.Add(rb);
        }
        sp.Children.Add(chips);
        if (s.Writable)
        {
            st.Current.TryGetValue(s.Id, out var cur);
            var now = $"Now in driver: {Now(s, cur)}";
            if (cur is { InProfile: true } x && !s.Choices.Any(c => c.Value == x.Current)) now += "  ⚠ not a documented value";
            sp.Children.Add(new TextBlock { Text = now, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 4, 0, 0) });
        }
        if (unsupported is not null || item?.Refused is not null)
            sp.Children.Add(new TextBlock { Text = "⚠ " + (unsupported ?? item!.Refused), FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return sp;
    }

    private async Task ApplyNvidiaAsync()
    {
        if (_nvBusy || _nvStatus is not { Available: true }) return;
        _nvStatus = await Task.Run(() => NvidiaFortnite.Read());
        var plan = NvidiaFortnite.Plan(_nvSel, _nvStatus);
        if (plan.FirstOrDefault(p => p.Refused is not null) is { } bad)
        {
            Dialog.Show("Not applied", "Nothing was changed.\n\n" + bad.Refused, DialogKind.Warning);
            RenderNvidia();
            return;
        }
        var changes = plan.Where(p => p.Changes).ToList();
        if (changes.Count == 0) { MainWindow.Current?.ShowToast("The NVIDIA profile already has these values.", "Success"); RenderNvidia(); return; }
        var msg = new StringBuilder($"Fortnite's NVIDIA driver profile ({_nvStatus.ProfileName ?? NvidiaFortnite.OwnProfileName + ", will be created"}):\n\n");
        foreach (var c in changes)
            msg.Append($"• {c.Setting.Label}: {(c.Current is { InProfile: true } cur ? NvidiaFortnite.Describe(c.Setting, cur.Current) : "driver default")} → {NvidiaFortnite.Describe(c.Setting, c.Target)}\n");
        msg.Append("\nTexture LOD bias makes textures (including skins) flatter and blurrier; no setting here changes geometry. Skins may look different, which can't be guaranteed either way.");
        msg.Append(_nv.Backup is null ? "\n\nThe current values are backed up first; \"Restore original values\" puts them back." : "\n\nYour original values are already backed up from the first change.");
        msg.Append("\nAfter saving, every value is read back from the driver; if any doesn't match, everything is rolled back.\nTakes effect the next time Fortnite starts.");
        if (PotatoGraphics.IsRunning(FortnitePotato.Game)) msg.Append("\n\nFortnite is running: it keeps its current settings until you restart it.");
        if (!Dialog.Confirm("Apply NVIDIA texture settings?", msg.ToString(), "Apply", DialogKind.Warning)) return;
        _nvBusy = true;
        try
        {
            var sel = new Dictionary<string, uint?>(_nvSel);
            var (ok, m, log) = await Task.Run(() => NvidiaFortnite.Apply(sel, _nv));
            if (ok) { _nv.Preset = NvPresetName(); try { _nv.Save(); } catch (Exception ex) { Log.Error("Saving nvidia-fortnite.json failed", ex); } }
            Dialog.Show(ok ? "NVIDIA profile updated" : "Not applied", m + (log.Count > 0 ? "\n\n" + string.Join("\n", log) : ""), ok ? DialogKind.Info : DialogKind.Warning);
        }
        catch (NvApiException ex) { Dialog.Show("NVIDIA driver refused the change", ex.Message + "\n\nNothing was saved to the driver.", DialogKind.Error); }
        finally { _nvBusy = false; }
        await RefreshNvidiaAsync();
    }

    private async Task RestoreNvidiaAsync()
    {
        if (_nvBusy || _nv.Backup is not { } b) return;
        var unusual = NvidiaFortnite.UnusualBackupValues(b);
        var msg = b.CreatedProfile
            ? $"Deletes the '{NvidiaFortnite.OwnProfileName}' profile Money Tweaks created, so Fortnite is back to having no NVIDIA profile of its own."
            : $"Puts back the values Fortnite's NVIDIA profile had on {b.At:g}:\n\n" + string.Join("\n", b.Settings.Select(kv =>
                $"• {NvidiaFortnite.Managed.First(m => m.Id == kv.Key).Label}: {(kv.Value.InProfile ? NvidiaFortnite.Describe(NvidiaFortnite.Managed.First(m => m.Id == kv.Key), kv.Value.Value) : "driver default")}"));
        if (unusual.Count > 0)
            msg += "\n\n⚠ The original values include settings Money Tweaks would never write: " + string.Join(", ", unusual) +
                   ". Restoring brings them back exactly. To get rid of them instead, cancel and use \"Reset to NVIDIA defaults\".";
        if (!Dialog.Confirm("Restore original NVIDIA values?", msg, "Restore", DialogKind.Warning)) return;
        _nvBusy = true;
        try
        {
            var (ok, m, log) = await Task.Run(() => NvidiaFortnite.Restore(_nv));
            if (ok) _nvSel = NvidiaFortnite.Preset("Default");
            Dialog.Show(ok ? "Restored" : "Restore incomplete", m + (log.Count > 0 ? "\n\n" + string.Join("\n", log) : ""), ok ? DialogKind.Info : DialogKind.Warning);
        }
        catch (NvApiException ex) { Dialog.Show("NVIDIA driver refused the change", ex.Message, DialogKind.Error); }
        finally { _nvBusy = false; }
        await RefreshNvidiaAsync();
    }
}
