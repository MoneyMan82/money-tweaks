using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Display;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Fortnite Stretched Resolution, INI method: edits only Fortnite's GameUserSettings.ini. Windows desktop resolution,
/// monitor resolution and refresh rate are never changed here.
/// </summary>
public sealed class FortniteStretchedView : UserControl, IPageLifecycle
{
    private readonly TextBox _w = new() { Width = 110, MaxLength = 4, FontSize = 16, ToolTip = "Width in pixels (640–7680)" };
    private readonly TextBox _h = new() { Width = 110, MaxLength = 4, FontSize = 16, ToolTip = "Height in pixels (480–4320)" };
    private readonly TextBlock _aspect = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBlock _valid = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _fileInfo = new();
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private FortniteWindowMode _mode = FortniteWindowMode.Fullscreen;
    private bool _lock = true;

    public FortniteStretchedView()
    {
        var refresh = Ui.Btn("Refresh", (_, _) => ShowFile(), "BaseButton");
        refresh.ToolTip = "Read Fortnite's settings file again";
        var (root, body) = Ui.Page("Fortnite Stretched",
            "Set a custom resolution for Fortnite only, by editing Fortnite's own GameUserSettings.ini. Your Windows desktop resolution, monitor resolution and refresh rate are not changed.",
            refresh, "INI method · Fortnite only", "\uE740");
        Content = root;

        body.Children.Add(Ui.Card(Notice()));

        var file = Ui.Section("Fortnite settings file", "Found automatically in your Windows user profile.");
        file.Children.Add(_fileInfo);
        body.Children.Add(Ui.Card(file));

        var res = Ui.Section("Resolution for Fortnite", "Any width × height, e.g. 1720×1080, 1600×1080 or 1440×1080.");
        var row = new WrapPanel();
        row.Children.Add(Field("Width", _w));
        row.Children.Add(new TextBlock { Text = "×", FontSize = 18, Margin = new Thickness(8, 22, 8, 0), Foreground = Ui.Brush("Muted") });
        row.Children.Add(Field("Height", _h));
        var asp = new StackPanel();
        asp.Children.Add(new TextBlock { Text = "ASPECT RATIO", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(14, 0, 0, 6) });
        asp.Children.Add(_aspect);
        row.Children.Add(asp);
        res.Children.Add(row);
        res.Children.Add(_valid);
        _w.TextChanged += (_, _) => Validate();
        _h.TextChanged += (_, _) => Validate();

        res.Children.Add(Label("Common stretched resolutions"));
        var presets = new WrapPanel();
        foreach (var (w, h) in new[] { (1920, 1080), (1750, 1080), (1720, 1080), (1680, 1050), (1600, 1080), (1440, 1080), (1280, 1024), (1280, 960), (1154, 864), (1080, 1080) })
        {
            var b = Ui.Btn($"{w}×{h}", (_, _) => { _w.Text = w.ToString(CultureInfo.InvariantCulture); _h.Text = h.ToString(CultureInfo.InvariantCulture); }, "GhostButton", new Thickness(0, 0, 6, 6));
            b.ToolTip = DisplayManager.AspectLabel(w, h);
            presets.Children.Add(b);
        }
        res.Children.Add(presets);

        res.Children.Add(Label("Window mode written to the file"));
        var modes = new WrapPanel();
        foreach (var (m, label, tip) in new[]
        {
            (FortniteWindowMode.Fullscreen, "Fullscreen (recommended)", "Exclusive fullscreen at the resolution above. Stretched visuals also depend on GPU scaling being set to Full-screen."),
            (FortniteWindowMode.WindowedFullscreen, "Windowed fullscreen", "Fortnite then uses your desktop resolution, so a custom resolution here usually has no visible effect."),
            (FortniteWindowMode.Windowed, "Windowed", "A window of this size; not stretched."),
        })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "fsmode", IsChecked = m == _mode, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
            rb.Checked += (_, _) => _mode = m;
            modes.Children.Add(rb);
        }
        res.Children.Add(modes);
        var lockBox = new CheckBox
        {
            Content = "Make the file read-only after applying", IsChecked = _lock, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Fortnite tends to replace resolutions it doesn't offer in its menu when it starts. Read-only stops that, but in-game setting changes then won't be saved until you click Reset or Restore backup.",
        };
        lockBox.Click += (_, _) => _lock = lockBox.IsChecked == true;
        res.Children.Add(lockBox);

        var apply = Ui.Btn("Apply", (_, _) => Apply(), "PrimaryButton", new Thickness(0, 14, 8, 0));
        apply.ToolTip = "Shows every change first, backs up the original file (once), then writes and reads the values back";
        var restore = Ui.Btn("Restore backup", (_, _) => RestoreBackup(), "GoldButton", new Thickness(0, 14, 8, 0));
        restore.ToolTip = "Copies back the original GameUserSettings.ini saved before the first change from this page";
        var reset = Ui.Btn("Reset", (_, _) => Reset(), "GhostButton", new Thickness(0, 14, 8, 0));
        reset.ToolTip = "Writes your monitor's current resolution into Fortnite's file (keeping its window mode) and removes the read-only lock";
        res.Children.Add(Ui.Row(apply, restore, reset));
        res.Children.Add(_status);
        body.Children.Add(Ui.Card(res));
        body.Children.Add(Ui.Card(HowTo()));

        var fn = FortniteResolution.Current();
        _w.Text = (fn?.w ?? 1440).ToString(CultureInfo.InvariantCulture);
        _h.Text = (fn?.h ?? 1080).ToString(CultureInfo.InvariantCulture);
        ShowFile();
    }

    public void OnShown() => ShowFile();
    public void OnHidden() { }

    private static TextBlock Label(string t) => new() { Text = t.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 14, 0, 6) };

    private static StackPanel Field(string label, TextBox tb)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(tb);
        return sp;
    }

    private static UIElement Notice()
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(Ui.Tag("FORTNITE ONLY · DESKTOP UNCHANGED", "Accent"));
        sp.Children.Add(head);
        foreach (var line in new[]
        {
            "• This only edits Fortnite's GameUserSettings.ini. It never changes your Windows desktop resolution, monitor resolution or refresh rate.",
            "• Fortnite can override these values: it may reset a resolution it doesn't list in its video menu, and its rendering mode (DirectX 11/12, Performance) and window mode affect what you see.",
            "• The file alone can't guarantee stretched visuals. Whether the image is stretched also depends on Fullscreen mode, your GPU scaling setting (NVIDIA/AMD/Intel: Full-screen, perform scaling on GPU) and whether your display/driver accepts the resolution.",
            "• Money Tweaks never edits Fortnite's game files or interacts with anti-cheat.",
        })
            sp.Children.Add(new TextBlock { Text = line, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        return sp;
    }

    private static UIElement HowTo()
    {
        var sp = Ui.Section("If the image isn't stretched");
        foreach (var line in new[]
        {
            "1. Close Fortnite before clicking Apply (it rewrites the file when it exits).",
            "2. Choose Fullscreen above and keep the read-only option on.",
            "3. In NVIDIA Control Panel → Adjust desktop size and position (or AMD GPU Scaling / Intel Scaling) choose Full-screen and 'Perform scaling on: GPU'.",
            "4. If your driver doesn't offer the resolution, create it as a custom resolution in the GPU software first (the Stretched Resolution tab shows which ones your driver lists).",
            "5. Start Fortnite. If it changed the resolution back, check that the file is still read-only (shown above).",
        })
            sp.Children.Add(new TextBlock { Text = line, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        return sp;
    }

    private void ShowFile()
    {
        _fileInfo.Children.Clear();
        var path = FortniteResolution.ConfigPath;
        if (!FortniteResolution.Game.Installed)
        {
            _fileInfo.Children.Add(new TextBlock { Text = "Fortnite's settings file wasn't found.", Foreground = Ui.Brush("Gold"), FontSize = 13, FontWeight = FontWeights.SemiBold });
            _fileInfo.Children.Add(Ui.Muted($"Expected at {path}. Start Fortnite once (it creates the file on first launch), close it, then click Refresh.", 12));
            return;
        }
        string Line()
        {
            try
            {
                var cur = FortniteResolution.Current();
                return $"Fortnite now: {(cur is { } c ? $"{c.w}×{c.h} ({DisplayManager.AspectLabel(c.w, c.h)}), {c.mode}" : "resolution not found in file")}";
            }
            catch (Exception ex) { return "Couldn't read the file: " + ex.Message; }
        }
        _fileInfo.Children.Add(new TextBlock { Text = Line(), FontSize = 14, FontWeight = FontWeights.SemiBold });
        var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tags.Children.Add(Ui.Tag(FortniteResolution.IsLocked ? "Read-only (locked)" : "Writable", FortniteResolution.IsLocked ? "Gold" : "Muted"));
        tags.Children.Add(Ui.Tag(FortniteResolution.HasBackup ? "Original backed up" : "No backup yet", FortniteResolution.HasBackup ? "Success" : "Muted"));
        if (Core.Features.PotatoGraphics.IsRunning(FortniteResolution.Game)) tags.Children.Add(Ui.Tag("Fortnite is running: close it before applying", "Danger"));
        _fileInfo.Children.Add(tags);
        var pathRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        pathRow.Children.Add(new TextBlock { Text = path, FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
        pathRow.Children.Add(Ui.Btn("Open folder", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Dialog.Show("Couldn't open the folder", ex.Message, DialogKind.Error); }
        }, "GhostButton", new Thickness(0)));
        _fileInfo.Children.Add(pathRow);
    }

    private bool Read(out int w, out int h) => int.TryParse(_w.Text.Trim(), out w) & int.TryParse(_h.Text.Trim(), out h);

    private bool Validate()
    {
        if (!Read(out var w, out var h)) { _aspect.Text = "–"; _valid.Text = "Enter whole numbers for width and height."; _valid.Foreground = Ui.Brush("Danger"); return false; }
        _aspect.Text = DisplayManager.AspectLabel(w, h);
        var errs = DisplayManager.Validate(w, h);
        if (errs.Count > 0) { _valid.Text = string.Join(" ", errs); _valid.Foreground = Ui.Brush("Danger"); return false; }
        if (w < h) { _valid.Text = "Width is smaller than height (portrait). Fortnite will most likely reject this."; _valid.Foreground = Ui.Brush("Gold"); return true; }
        _valid.Text = $"Valid resolution. It will be written for Fortnite only.";
        _valid.Foreground = Ui.Brush("Success");
        return true;
    }

    private void Status(string text, string tone) { _status.Text = text; _status.Foreground = Ui.Brush(tone); }

    private string PlanText(int w, int h, FortniteWindowMode mode, bool locked)
    {
        var plan = FortniteResolution.Plan(w, h, mode);
        return $"Fortnite: {w}×{h} ({DisplayManager.AspectLabel(w, h)}), {mode}{(locked ? ", file set to read-only" : ", file left writable")}\n\n" +
               string.Join("\n", plan.Select(p => $"• {p.Key}: {(p.Missing ? "(not in your file, skipped)" : $"{p.Current} → {p.New}")}")) +
               "\n\nAll other settings in the file stay as they are. Windows display settings are not changed.";
    }

    private void Apply()
    {
        if (!Validate() || !Read(out var w, out var h)) { Status("Fix the resolution first.", "Danger"); return; }
        if (!FortniteResolution.Game.Installed) { Status("Fortnite's settings file wasn't found. Start Fortnite once, close it, then click Refresh.", "Danger"); return; }
        try
        {
            if (!Dialog.Confirm($"Apply {w}×{h} to Fortnite?", PlanText(w, h, _mode, _lock) +
                    $"\n\nThe original file is backed up first{(FortniteResolution.HasBackup ? " (a backup already exists and is kept)" : "")}. Fortnite or its rendering mode may still override these values.", "Apply", DialogKind.Confirm)) return;
            var (changed, missing) = FortniteResolution.Apply(w, h, _mode, _lock);
            Status($"Written and read back: {changed} value(s){(missing.Count > 0 ? $", {missing.Count} key(s) not in your file (skipped)" : "")}. Start Fortnite to use {w}×{h}. If it doesn't look stretched, see the steps below.", "Success");
        }
        catch (Exception ex) { Log.Warn("Fortnite stretched apply failed: " + ex.Message); Status(ex.Message, "Danger"); }
        ShowFile();
    }

    private void RestoreBackup()
    {
        if (!FortniteResolution.HasBackup) { Status("There's no backup yet: it's created the first time you click Apply.", "Gold"); return; }
        if (!Dialog.Confirm("Restore Fortnite's original settings file?", "Copies back GameUserSettings.ini as it was before the first change from Money Tweaks' stretched-resolution feature. Any in-game settings changed since then are replaced. The backup is kept.", "Restore", DialogKind.Warning)) return;
        try { Status(FortniteResolution.Restore(), "Success"); }
        catch (Exception ex) { Status("Couldn't restore: " + ex.Message, "Danger"); }
        if (FortniteResolution.Current() is { } c) { _w.Text = c.w.ToString(CultureInfo.InvariantCulture); _h.Text = c.h.ToString(CultureInfo.InvariantCulture); }
        ShowFile();
    }

    private void Reset()
    {
        if (!FortniteResolution.Game.Installed) { Status("Fortnite's settings file wasn't found.", "Danger"); return; }
        // Read-only: the monitor's current desktop resolution is only read, never changed.
        var primary = DisplayManager.Displays().FirstOrDefault(d => d.Primary);
        if (primary is null) { Status("Windows didn't report a primary display.", "Danger"); return; }
        int w = primary.Current.Width, h = primary.Current.Height;
        try
        {
            if (!Dialog.Confirm($"Reset Fortnite to {w}×{h}?", $"Writes your primary monitor's current resolution ({w}×{h}) into Fortnite's file, keeps its window mode, and removes the read-only lock so Fortnite can save settings again.\n\n" +
                    PlanText(w, h, FortniteResolution.CurrentMode() ?? FortniteWindowMode.WindowedFullscreen, false), "Reset", DialogKind.Confirm)) return;
            var (changed, _) = FortniteResolution.Reset(w, h);
            _w.Text = w.ToString(CultureInfo.InvariantCulture); _h.Text = h.ToString(CultureInfo.InvariantCulture);
            Status($"Fortnite reset to {w}×{h} ({changed} value(s) written) and the file is writable again.", "Success");
        }
        catch (Exception ex) { Status("Couldn't reset: " + ex.Message, "Danger"); }
        ShowFile();
    }
}
