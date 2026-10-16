using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Display;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>Stretched Resolution: real desktop mode changes with rollback, plus Fortnite's in-game resolution.</summary>
public sealed class StretchedView : UserControl, IPageLifecycle
{
    private List<DisplayInfo> _displays = [];
    private DisplayInfo? _sel;
    private readonly WrapPanel _monitorChips = new();
    private readonly StackPanel _monitorFacts = new();
    private readonly StackPanel _modesList = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _w = new() { Width = 110, MaxLength = 4, ToolTip = "Width in pixels (640–7680)" };
    private readonly TextBox _h = new() { Width = 110, MaxLength = 4, ToolTip = "Height in pixels (480–4320)" };
    private readonly TextBlock _aspect = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBlock _support = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly WrapPanel _rates = new();
    private readonly WrapPanel _presets = new();
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _fortnite = new();
    private int _hz;
    private ScalingMode _scaling = ScalingMode.Stretch;
    private ResolutionPreset? _selectedPreset;
    private bool _busy;
    private static DisplayState S => DisplayService.State;

    public StretchedView()
    {
        var refresh = Ui.Btn("Refresh", (_, _) => Load(), "BaseButton");
        refresh.ToolTip = "Read monitors and supported modes again (e.g. after creating a custom resolution in your GPU software)";
        var (root, body) = Ui.Page("Stretched Resolution",
            "Change your desktop resolution safely: every change is checked with your display driver first and stays temporary until you confirm, otherwise it reverts automatically. Fortnite's in-game resolution is set separately in its own settings file.",
            refresh, "Desktop · In-game · Safe rollback", "\uE7F4");
        Content = root;

        body.Children.Add(Ui.Card(Explainer()));

        var mon = Ui.Section("Monitor", "Pick the display to change. Values come from Windows and your display driver.");
        mon.Children.Add(_monitorChips);
        mon.Children.Add(_monitorFacts);
        var showModes = Ui.Btn("Show supported resolutions", (s, _) =>
        {
            _modesList.Visibility = _modesList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            ((Button)s!).Content = _modesList.Visibility == Visibility.Visible ? "Hide supported resolutions" : "Show supported resolutions";
        }, "GhostButton", new Thickness(0, 8, 8, 0));
        showModes.HorizontalAlignment = HorizontalAlignment.Left;
        mon.Children.Add(showModes);
        mon.Children.Add(_modesList);
        body.Children.Add(Ui.Card(mon));

        var desk = Ui.Section("Desktop resolution", "Type any resolution or pick a preset. The aspect ratio updates live.");
        var row = new WrapPanel();
        row.Children.Add(Field("Width", _w));
        row.Children.Add(new TextBlock { Text = "×", FontSize = 18, Margin = new Thickness(8, 22, 8, 0), Foreground = Ui.Brush("Muted") });
        row.Children.Add(Field("Height", _h));
        var asp = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        asp.Children.Add(new TextBlock { Text = "ASPECT RATIO", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(14, 0, 0, 6) });
        asp.Children.Add(_aspect);
        row.Children.Add(asp);
        desk.Children.Add(row);
        desk.Children.Add(_support);
        _w.TextChanged += (_, _) => OnSizeChanged();
        _h.TextChanged += (_, _) => OnSizeChanged();

        desk.Children.Add(Label("Aspect ratio at the current height"));
        var ratios = new WrapPanel();
        foreach (var (name, r) in new[] { ("4:3", 4.0 / 3), ("5:4", 1.25), ("16:10", 1.6), ("16:9", 16.0 / 9), ("21:9", 21.0 / 9) })
        {
            var b = Ui.Btn(name, (_, _) => { if (int.TryParse(_h.Text, out var hh)) _w.Text = (((int)Math.Round(hh * r)) / 2 * 2).ToString(CultureInfo.InvariantCulture); }, "GhostButton", new Thickness(0, 0, 6, 6));
            b.ToolTip = $"Width = height × {name}";
            ratios.Children.Add(b);
        }
        desk.Children.Add(ratios);
        desk.Children.Add(Label("Presets"));
        desk.Children.Add(_presets);
        desk.Children.Add(Ui.Row(Ui.Btn("Save preset", (_, _) => SavePreset(), "GhostButton"), Ui.Btn("Delete preset", (_, _) => DeletePreset(), "GhostButton")));
        desk.Children.Add(Label("Refresh rate (reported by the driver for this resolution)"));
        desk.Children.Add(_rates);
        desk.Children.Add(Label("Scaling"));
        var scal = new WrapPanel();
        foreach (var (m, label, tip) in new[]
        {
            (ScalingMode.Stretch, "Fullscreen (stretched)", "Fill the whole screen. This is what makes a 4:3 resolution look stretched."),
            (ScalingMode.Default, "Aspect ratio", "Windows has no direct 'aspect ratio' request; this sends 'default', which lets the GPU/monitor decide (usually keeps the aspect ratio with black bars)."),
            (ScalingMode.Center, "No scaling", "Centre the image at 1:1 pixels with black borders."),
        })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "scal", IsChecked = m == _scaling, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
            rb.Checked += (_, _) => _scaling = m;
            scal.Children.Add(rb);
        }
        desk.Children.Add(scal);
        desk.Children.Add(Ui.Muted("Your GPU control panel (NVIDIA 'Adjust desktop size and position', AMD 'GPU Scaling', Intel 'Scaling') or the monitor's own setting can override this. If the image isn't stretched, set scaling to Full-screen there and to 'Perform scaling on: GPU'.", 11.5));

        var test = Ui.Btn("Test resolution (10 s)", async (_, _) => await TryAsync(previewOnly: true), "BaseButton", new Thickness(0, 14, 8, 0));
        test.ToolTip = "Switches for 10 seconds, then always goes back";
        var apply = Ui.Btn("Apply", async (_, _) => await TryAsync(previewOnly: false), "PrimaryButton", new Thickness(0, 14, 8, 0));
        apply.ToolTip = "Switches, then asks you to keep it within 15 seconds; otherwise reverts";
        var native = Ui.Btn("Reset to native", async (_, _) => await NativeAsync(), "GhostButton", new Thickness(0, 14, 8, 0));
        native.ToolTip = "Largest resolution the driver reports for this monitor, with the same confirmation countdown";
        var restore = Ui.Btn("Restore original", (_, _) => RestoreOriginal(), "GoldButton", new Thickness(0, 14, 8, 0));
        restore.ToolTip = "Back to the resolution this monitor had before Money Tweaks first changed it";
        desk.Children.Add(Ui.Row(test, apply, native, restore));
        desk.Children.Add(_status);
        body.Children.Add(Ui.Card(desk));

        body.Children.Add(Ui.Card(_fortnite));
        Load();
    }

    public void OnShown() { if (!_busy) Load(keepFields: true); }
    public void OnHidden() { }

    private static TextBlock Label(string t) => new() { Text = t.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 14, 0, 6) };

    private static StackPanel Field(string label, TextBox tb)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 0, 0, 6) });
        tb.FontSize = 16;
        sp.Children.Add(tb);
        return sp;
    }

    private static UIElement Explainer()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text("Desktop resolution vs in-game resolution", 14, FontWeights.SemiBold));
        foreach (var line in new[]
        {
            "• Desktop resolution is what Windows sends to your monitor. Changing it here affects every app.",
            "• In-game resolution is stored by each game. For a stretched look, set the same resolution in the game's video settings, use Fullscreen mode, and let the GPU stretch it (scaling: Fullscreen).",
            "• For Fortnite, the section at the bottom writes the resolution and window mode into Fortnite's own settings file.",
            "• Resolutions your driver doesn't list (like 1720×1080 on many monitors) must be created first as a custom resolution in NVIDIA/AMD/Intel software. Money Tweaks asks the driver before every change and tells you if it refuses.",
        })
            sp.Children.Add(new TextBlock { Text = line, Style = Ui.Res<Style>("MutedText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return sp;
    }

    // ---- data -------------------------------------------------------------------------------------------------------
    private void Load(bool keepFields = false)
    {
        try { _displays = DisplayManager.Displays(); }
        catch (Exception ex) { Log.Error("Enumerating displays failed", ex); _displays = []; }
        _sel = _displays.FirstOrDefault(d => d.DeviceName == _sel?.DeviceName) ?? _displays.FirstOrDefault();
        _monitorChips.Children.Clear();
        if (_displays.Count == 0) { _monitorFacts.Children.Clear(); _monitorFacts.Children.Add(Ui.Muted("Windows reported no active displays.")); return; }
        var group = "mon" + Guid.NewGuid().ToString("N");
        foreach (var d in _displays)
        {
            var rb = new RadioButton { Content = d.Label, Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = d == _sel, Margin = new Thickness(0, 0, 6, 6), ToolTip = $"{d.AdapterName} · position {d.X},{d.Y}" };
            var dd = d;
            rb.Checked += (_, _) => { _sel = dd; ShowMonitor(false); };
            _monitorChips.Children.Add(rb);
        }
        ShowMonitor(keepFields);
        BuildFortnite();
    }

    private void ShowMonitor(bool keepFields)
    {
        if (_sel is not { } d) return;
        _monitorFacts.Children.Clear();
        var facts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Fact(string label, string value, string? tip = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 8), ToolTip = tip };
            sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Res<Style>("Eyebrow") });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) });
            facts.Children.Add(sp);
        }
        Fact("Active now", $"{d.Current.Width}×{d.Current.Height} @ {d.Current.Hz} Hz", DisplayManager.AspectLabel(d.Current.Width, d.Current.Height));
        Fact("Saved setting", $"{d.Registry.Width}×{d.Registry.Height} @ {d.Registry.Hz} Hz", "What Windows uses after sign-in");
        Fact("Native", d.Native is { } n ? $"{n.Width}×{n.Height} @ {n.Hz} Hz" : "unknown", "Largest mode the driver reports (the panel's native resolution on LCDs)");
        Fact("Scaling", d.CurrentScaling switch { ScalingMode.Stretch => "Fullscreen", ScalingMode.Center => "No scaling", _ => "Default (GPU/monitor)" }, "As reported by Windows; the GPU control panel can override it");
        Fact("Supported modes", $"{d.Modes.Select(m => (m.Width, m.Height)).Distinct().Count()} resolutions");
        _monitorFacts.Children.Add(facts);
        if (S.Originals.TryGetValue(d.DeviceName, out var o))
            _monitorFacts.Children.Add(Ui.Muted($"Original before Money Tweaks changed it: {o.Width}×{o.Height} @ {o.Hz} Hz (saved {o.SavedAt:g}). Use 'Restore original' to go back.", 12));

        _modesList.Children.Clear();
        var text = string.Join("\n", d.Modes.GroupBy(m => (m.Width, m.Height)).Select(g => $"{g.Key.Width}×{g.Key.Height}  ({DisplayManager.AspectLabel(g.Key.Width, g.Key.Height)})  ·  {string.Join(", ", g.Select(m => m.Hz).Distinct().OrderByDescending(x => x))} Hz"));
        _modesList.Children.Add(new TextBox { Text = text, IsReadOnly = true, FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.NoWrap });

        if (!keepFields || _w.Text.Length == 0) { _w.Text = d.Current.Width.ToString(CultureInfo.InvariantCulture); _h.Text = d.Current.Height.ToString(CultureInfo.InvariantCulture); }
        BuildPresets();
        OnSizeChanged();
    }

    private bool ReadSize(out int w, out int h) => int.TryParse(_w.Text.Trim(), out w) & int.TryParse(_h.Text.Trim(), out h);

    private void OnSizeChanged()
    {
        _rates.Children.Clear();
        if (!ReadSize(out var w, out var h)) { _aspect.Text = "–"; _support.Text = "Enter whole numbers."; _support.Foreground = Ui.Brush("Danger"); return; }
        _aspect.Text = DisplayManager.AspectLabel(w, h);
        var errs = DisplayManager.Validate(w, h);
        if (errs.Count > 0) { _support.Text = string.Join(" ", errs); _support.Foreground = Ui.Brush("Danger"); return; }
        if (_sel is not { } d) return;
        var rates = d.RatesFor(w, h).ToList();
        if (rates.Count > 0)
        {
            _support.Text = $"✓ Your display driver lists {w}×{h} ({rates.Count} refresh rate{(rates.Count == 1 ? "" : "s")}).";
            _support.Foreground = Ui.Brush("Success");
        }
        else
        {
            _support.Text = $"{w}×{h} isn't in the driver's list for this monitor. 'Test' and 'Apply' will ask the driver anyway, but most drivers refuse unlisted modes until you create a custom resolution in NVIDIA/AMD/Intel software.";
            _support.Foreground = Ui.Brush("Gold");
        }
        var group = "hz" + Guid.NewGuid().ToString("N");
        _hz = rates.Contains(d.Current.Hz) ? d.Current.Hz : rates.FirstOrDefault();
        var auto = new RadioButton { Content = "Driver default", Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = _hz == 0, Margin = new Thickness(0, 0, 6, 6), ToolTip = "Let the driver pick" };
        auto.Checked += (_, _) => _hz = 0;
        _rates.Children.Add(auto);
        foreach (var r in rates)
        {
            var rb = new RadioButton { Content = $"{r} Hz", Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = r == _hz, Margin = new Thickness(0, 0, 6, 6) };
            var rr = r;
            rb.Checked += (_, _) => _hz = rr;
            _rates.Children.Add(rb);
        }
    }

    private void BuildPresets()
    {
        _presets.Children.Clear();
        var group = "pre" + Guid.NewGuid().ToString("N");
        var all = new List<ResolutionPreset>();
        if (_sel?.Native is { } n) all.Add(new ResolutionPreset { Name = $"Native {n.Width}×{n.Height}", Width = n.Width, Height = n.Height, BuiltIn = true });
        all.AddRange(DisplayState.BuiltIns);
        all.AddRange(S.Presets);
        foreach (var p in all)
        {
            var listed = _sel?.Reports(p.Width, p.Height) == true;
            var rb = new RadioButton
            {
                Content = p.Name + (p.BuiltIn ? "" : "  ★"), Style = Ui.Res<Style>("Chip"), GroupName = group, Margin = new Thickness(0, 0, 6, 6), Opacity = listed ? 1 : 0.6,
                ToolTip = (listed ? "Listed by your display driver" : "Not listed by your driver: needs a custom resolution in GPU software") + (p.BuiltIn ? "" : " · your preset"),
            };
            var pp = p;
            rb.Checked += (_, _) => { _selectedPreset = pp; _w.Text = pp.Width.ToString(CultureInfo.InvariantCulture); _h.Text = pp.Height.ToString(CultureInfo.InvariantCulture); };
            _presets.Children.Add(rb);
        }
    }

    private void SavePreset()
    {
        if (!ReadSize(out var w, out var h) || DisplayManager.Validate(w, h).Count > 0) { Status("Enter a valid width and height first.", "Danger"); return; }
        var name = $"{w}×{h} ({DisplayManager.AspectLabel(w, h).Split(' ')[0]})";
        if (S.Presets.Any(p => p.Width == w && p.Height == h) || DisplayState.BuiltIns.Any(p => p.Width == w && p.Height == h)) { Status($"{w}×{h} is already a preset.", "Gold"); return; }
        S.Presets.Add(new ResolutionPreset { Name = name, Width = w, Height = h });
        DisplayService.Save();
        BuildPresets();
        Status($"Saved preset {name}.", "Success");
    }

    private void DeletePreset()
    {
        if (_selectedPreset is not { BuiltIn: false } p) { Status("Select one of your own presets (marked ★) first. Built-in presets can't be deleted.", "Gold"); return; }
        if (!Dialog.Confirm("Delete preset?", $"Delete “{p.Name}”? Your display isn't changed.", "Delete", DialogKind.Warning)) return;
        S.Presets.Remove(p);
        _selectedPreset = null;
        DisplayService.Save();
        BuildPresets();
        Status($"Deleted preset {p.Name}.", "Success");
    }

    private void Status(string text, string tone) { _status.Text = text; _status.Foreground = Ui.Brush(tone == "Muted" ? "Muted" : tone); }

    // ---- actions ----------------------------------------------------------------------------------------------------
    private async Task TryAsync(bool previewOnly)
    {
        if (_busy || _sel is not { } d) return;
        if (!ReadSize(out var w, out var h)) { Status("Enter whole numbers for width and height.", "Danger"); return; }
        var errs = DisplayManager.Validate(w, h);
        if (errs.Count > 0) { Status(string.Join(" ", errs), "Danger"); return; }
        if (!previewOnly && !Dialog.Confirm($"Apply {w}×{h}?",
                $"Monitor: {d.MonitorName}\nFrom {d.Current} to {w}×{h}{(_hz > 0 ? $" @ {_hz} Hz" : "")} · scaling: {_scaling}\n\nThe change is temporary until you click 'Keep' in the countdown. If you don't, or the screen goes black, it reverts after 15 seconds.",
                "Apply", DialogKind.Confirm)) return;
        _busy = true;
        Status(previewOnly ? "Testing…" : "Applying…", "Muted");
        try
        {
            var (kept, msg) = await DisplayService.TryModeAsync(d, w, h, _hz, _scaling, previewOnly);
            Status(msg, kept ? "Success" : msg.StartsWith("Test finished", StringComparison.Ordinal) || msg.StartsWith("Reverted", StringComparison.Ordinal) ? "Muted" : "Danger");
        }
        catch (Exception ex) { Log.Error("Resolution change failed", ex); Status("Failed: " + ex.Message, "Danger"); DisplayManager.RevertToSaved(d.DeviceName); }
        finally { _busy = false; Load(keepFields: true); }
    }

    private async Task NativeAsync()
    {
        if (_sel?.Native is not { } n) { Status("The driver reported no modes for this monitor.", "Danger"); return; }
        _w.Text = n.Width.ToString(CultureInfo.InvariantCulture);
        _h.Text = n.Height.ToString(CultureInfo.InvariantCulture);
        _hz = n.Hz;
        await TryAsync(previewOnly: false);
    }

    private void RestoreOriginal()
    {
        if (_sel is not { } d) return;
        if (!S.Originals.TryGetValue(d.DeviceName, out var o)) { Status("Money Tweaks hasn't changed this monitor, so there's nothing to restore.", "Muted"); return; }
        if (!Dialog.Confirm("Restore original resolution?", $"{d.MonitorName}: back to {o.Width}×{o.Height} @ {o.Hz} Hz (as it was on {o.SavedAt:g}).", "Restore", DialogKind.Confirm)) return;
        var msg = DisplayService.RestoreOriginal(d);
        Status(msg, msg.StartsWith("Restored", StringComparison.Ordinal) ? "Success" : "Danger");
        Load();
    }

    // ---- Fortnite ---------------------------------------------------------------------------------------------------
    private FortniteWindowMode _fnMode = FortniteWindowMode.Fullscreen;
    private bool _fnLock = true;

    private void BuildFortnite()
    {
        // Fortnite's in-game resolution has its own page (INI method, Fortnite only, desktop untouched).
        _fortnite.Children.Clear();
        var sec = Ui.Section("Fortnite in-game resolution", "Set Fortnite's own resolution (GameUserSettings.ini) without changing your desktop.");
        var cur = FortniteResolution.Game.Installed ? FortniteResolution.Current() : null;
        sec.Children.Add(Ui.Muted(FortniteResolution.Game.Installed ? $"Fortnite now: {(cur is { } c ? $"{c.w}×{c.h}, {c.mode}" : "unknown")}" : "Fortnite's settings file wasn't found.", 12.5));
        var open = Ui.Btn("Open Fortnite Stretched", (_, _) => MainWindow.Current?.Navigate("fnstretch"), "PrimaryButton", new Thickness(0, 10, 8, 0));
        sec.Children.Add(open);
        _fortnite.Children.Add(sec);
    }

    private string? FnPlanText(out int w, out int h)
    {
        if (!ReadSize(out w, out h) || DisplayManager.Validate(w, h).Count > 0) { Dialog.Show("Check the resolution", "Enter a valid width and height in the Desktop section."); return null; }
        var plan = FortniteResolution.Plan(w, h, _fnMode);
        var lines = plan.Select(p => $"• {p.Key}: {(p.Missing ? "(not in your file, skipped)" : $"{p.Current} → {p.New}")}");
        return $"Fortnite: {w}×{h} ({DisplayManager.AspectLabel(w, h)}), {_fnMode}{(_fnLock ? ", file locked read-only" : "")}\n\n" + string.Join("\n", lines);
    }

    private void FnPreview()
    {
        if (FnPlanText(out _, out _) is { } t) Dialog.Show("Fortnite changes (preview)", t + "\n\nNothing has been changed.");
    }

    private void FnApply()
    {
        if (FnPlanText(out var w, out var h) is not { } t) return;
        if (!Dialog.Confirm("Write to Fortnite?", t + "\n\nThe original file is backed up first (once) and can be restored from this tab.", "Write", DialogKind.Confirm)) return;
        try
        {
            var (changed, missing) = FortniteResolution.Apply(w, h, _fnMode, _fnLock);
            Dialog.Show("Fortnite updated", $"{changed} value(s) written and read back.{(missing.Count > 0 ? $" {missing.Count} key(s) weren't in your file and were skipped." : "")}\n\nStart Fortnite to use {w}×{h}.");
        }
        catch (Exception ex) { Dialog.Show("Couldn't update Fortnite", ex.Message, DialogKind.Error); }
        BuildFortnite();
    }

    private void FnRestore()
    {
        if (!Dialog.Confirm("Restore Fortnite's settings file?", "Puts GameUserSettings.ini back to how it was before the first change from this tab. The backup is kept.", "Restore", DialogKind.Warning)) return;
        try { Dialog.Show("Fortnite", FortniteResolution.Restore()); }
        catch (Exception ex) { Dialog.Show("Couldn't restore", ex.Message, DialogKind.Error); }
        BuildFortnite();
    }
}
