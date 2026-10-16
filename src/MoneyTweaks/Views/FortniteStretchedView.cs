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
    private readonly StackPanel _gpuSteps = new();
    private readonly StackPanel _auto = new();
    private string _vendor = "";
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
        body.Children.Add(Ui.Card(_auto));
        DisplayService.AutoChanged += () => Dispatcher.BeginInvoke(BuildAuto);

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
        foreach (var (w, h) in new[] { (2288, 1440), (1920, 1080), (1750, 1080), (1720, 1080), (1680, 1050), (1600, 1080), (1440, 1080), (1280, 1024), (1280, 960), (1154, 864), (1080, 1080) })
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
        body.Children.Add(Ui.Card(_gpuSteps));
        body.Children.Add(Ui.Card(HowTo()));
        _w.TextChanged += (_, _) => BuildGpuSteps();
        _h.TextChanged += (_, _) => BuildGpuSteps();

        var fn = FortniteResolution.Current();
        _w.Text = (fn?.w ?? 1440).ToString(CultureInfo.InvariantCulture);
        _h.Text = (fn?.h ?? 1080).ToString(CultureInfo.InvariantCulture);
        ShowFile();
        BuildGpuSteps();
        BuildAuto();
    }

    public void OnShown() { ShowFile(); BuildAuto(); }
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

    /// <summary>Step-by-step custom resolution guide for NVIDIA and AMD, filled in with the chosen size and the monitor's refresh rate.</summary>
    private void BuildGpuSteps()
    {
        _gpuSteps.Children.Clear();
        int w = int.TryParse(_w.Text.Trim(), out var a) ? a : 0, h = int.TryParse(_h.Text.Trim(), out var b) ? b : 0;
        var primary = SafePrimary();
        var hz = primary?.Current.Hz ?? 60;
        if (_vendor.Length == 0)
            _vendor = primary?.AdapterName.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true || primary?.AdapterName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) == true ? "AMD" : "NVIDIA";
        var res = w > 0 && h > 0 ? $"{w}×{h}" : "your resolution";
        var sec = Ui.Section("Make the custom resolution in your GPU software",
            $"Fortnite can only show {res} if your graphics driver offers it. Do this once, then use Apply above.{(primary is null ? "" : $" Your main display runs on: {primary.AdapterName}, {hz} Hz.")}");
        var tabs = new WrapPanel();
        foreach (var v in new[] { "NVIDIA", "AMD" })
        {
            var rb = new RadioButton { Content = v == "NVIDIA" ? "NVIDIA Control Panel" : "AMD Software: Adrenalin", Style = Ui.Res<Style>("Chip"), GroupName = "gpuv" + GetHashCode(), IsChecked = v == _vendor, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _vendor = v; BuildGpuSteps(); };
            tabs.Children.Add(rb);
        }
        sec.Children.Add(tabs);
        string W = w > 0 ? w.ToString(CultureInfo.InvariantCulture) : "your width", H = h > 0 ? h.ToString(CultureInfo.InvariantCulture) : "your height";
        string[] steps = _vendor == "AMD"
            ?
            [
                "Right-click the desktop and open AMD Software: Adrenalin Edition.",
                "Click Gaming → Display (or the gear icon → Display).",
                "Turn on GPU Scaling and set Scaling Mode to Full Panel. This is what stretches the image.",
                $"Find Custom Resolutions and click Create New (accept the notice the first time).",
                $"Enter Horizontal Resolution {W}, Vertical Resolution {H} and Refresh Rate {hz} Hz. Leave the timing settings as they are, then click Create/Save.",
                $"Open Windows Settings → System → Display, and set Display resolution to {res} (or use the Stretched Resolution tab in Money Tweaks, which reverts automatically if something goes wrong).",
                $"Back here: Apply {res} to Fortnite with read-only on, then start Fortnite.",
            ]
            :
            [
                "Right-click the desktop and open NVIDIA Control Panel.",
                "Go to Display → Adjust desktop size and position. Set Scaling to Full-screen, Perform scaling on: GPU, tick 'Override the scaling mode set by games and programs', then click Apply.",
                "Go to Display → Change resolution and pick your monitor, then click Customize…",
                "Tick 'Enable resolutions not exposed by the display', then click Create Custom Resolution…",
                $"Enter Horizontal pixels {W}, Vertical lines {H} and Refresh rate {hz} Hz. Leave Timing on Automatic, then click Test and confirm with Yes.",
                $"Back in Change resolution, select {res} under Custom and click Apply (or use the Stretched Resolution tab in Money Tweaks, which reverts automatically if something goes wrong).",
                $"Back here: Apply {res} to Fortnite with read-only on, then start Fortnite.",
            ];
        for (int i = 0; i < steps.Length; i++)
            sec.Children.Add(new TextBlock { Text = $"{i + 1}. {steps[i]}", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7), Foreground = Ui.Brush("Text") });
        sec.Children.Add(Ui.Muted("If the driver refuses the resolution, try the monitor's 60 Hz first, or a size closer to your native resolution. Menu names can differ slightly between driver versions.", 11.5));
        _gpuSteps.Children.Add(sec);
    }

    /// <summary>Desktop auto-switch while Fortnite runs: setup, driver check, quick test, live state.</summary>
    private void BuildAuto()
    {
        _auto.Children.Clear();
        var a = DisplayService.State.AutoFortnite;
        var sec = Ui.Section("Auto-switch desktop resolution while Fortnite runs",
            "When Fortnite starts, your monitor switches to the stretched resolution; when Fortnite closes, it goes back to your normal resolution. The switch is temporary and never saved as your Windows setting.");
        List<DisplayInfo> displays;
        try { displays = DisplayManager.Displays(); } catch { displays = []; }
        var target = DisplayService.AutoTarget(displays);
        if (target is null) { sec.Children.Add(Ui.Muted("Windows reported no display.")); _auto.Children.Add(sec); return; }

        var wBox = new TextBox { Text = a.Width.ToString(CultureInfo.InvariantCulture), Width = 100, MaxLength = 4, FontSize = 15 };
        var hBox = new TextBox { Text = a.Height.ToString(CultureInfo.InvariantCulture), Width = 100, MaxLength = 4, FontSize = 15 };
        var row = new WrapPanel();
        row.Children.Add(Field("Width while Fortnite runs", wBox));
        row.Children.Add(new TextBlock { Text = "×", FontSize = 18, Margin = new Thickness(8, 22, 8, 0), Foreground = Ui.Brush("Muted") });
        row.Children.Add(Field("Height", hBox));
        sec.Children.Add(row);

        if (displays.Count > 1)
        {
            sec.Children.Add(Label("Monitor"));
            var mons = new WrapPanel();
            foreach (var d in displays)
            {
                var rb = new RadioButton { Content = d.Label, Style = Ui.Res<Style>("Chip"), GroupName = "automon" + GetHashCode(), IsChecked = d.DeviceName == target.DeviceName, Margin = new Thickness(0, 0, 6, 6) };
                var dd = d;
                rb.Checked += (_, _) => { a.Device = dd.DeviceName; DisplayService.Save(); BuildAuto(); };
                mons.Children.Add(rb);
            }
            sec.Children.Add(mons);
        }

        int.TryParse(wBox.Text, out var w); int.TryParse(hBox.Text, out var h);
        var hz = DisplayService.AutoHz(target);
        var test = DisplayManager.Validate(w, h).Count == 0 ? DisplayManager.Test(target.DeviceName, w, h, hz, a.Scaling) : ChangeResult.BadParam;
        var ok = test == ChangeResult.Ok;
        sec.Children.Add(new TextBlock
        {
            Text = ok ? $"✓ Your driver accepts {w}×{h}{(hz > 0 ? $" @ {hz} Hz" : "")} on {target.MonitorName}. Back to: {target.Registry.Width}×{target.Registry.Height} @ {target.Registry.Hz} Hz when Fortnite closes."
                      : $"✗ Your driver doesn't accept {w}×{h} on {target.MonitorName} yet. Create it as a custom resolution first (steps further down this page), then come back.",
            Foreground = Ui.Brush(ok ? "Success" : "Gold"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        var state = DisplayService.AutoActive ? $"Active now: {target.Current.Width}×{target.Current.Height} while Fortnite runs."
            : a.Enabled ? "On. Waiting for Fortnite to start." : "Off.";
        sec.Children.Add(Ui.Muted(state, 12.5));

        var toggle = new CheckBox { Content = "Switch automatically when Fortnite starts", IsChecked = a.Enabled, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
        toggle.Click += (_, _) =>
        {
            if (toggle.IsChecked == true)
            {
                if (!int.TryParse(wBox.Text, out var nw) || !int.TryParse(hBox.Text, out var nh) || DisplayManager.Validate(nw, nh).Count > 0) { toggle.IsChecked = false; Status("Enter a valid width and height first.", "Danger"); return; }
                a.Width = nw; a.Height = nh;
                var t = DisplayManager.Test(target.DeviceName, nw, nh, DisplayService.AutoHz(target), a.Scaling);
                if (t != ChangeResult.Ok) { toggle.IsChecked = false; Dialog.Show("Create the resolution first", DisplayManager.Explain(t, nw, nh, 0) + "\n\nThe step-by-step guide is further down this page.", DialogKind.Warning); BuildAuto(); return; }
                a.Enabled = true;
                DisplayService.Save();
                DisplayService.StartAutoSwitch();
                MainWindow.Current?.ShowToast($"Auto-switch on: {nw}×{nh} while Fortnite runs.", "Success");
            }
            else
            {
                a.Enabled = false;
                DisplayService.Save();
                DisplayService.StopAutoSwitch();
                MainWindow.Current?.ShowToast("Auto-switch off. Your resolution is back to normal.", "Success");
            }
            BuildAuto();
        };
        sec.Children.Add(toggle);
        var save = Ui.Btn("Save size", (_, _) =>
        {
            if (!int.TryParse(wBox.Text, out var nw) || !int.TryParse(hBox.Text, out var nh) || DisplayManager.Validate(nw, nh).Count > 0) { Status("Enter a valid width and height.", "Danger"); return; }
            a.Width = nw; a.Height = nh; DisplayService.Save(); BuildAuto();
        }, "GhostButton", new Thickness(0, 10, 8, 0));
        var tryIt = Ui.Btn("Try it for 10 s", async (_, _) =>
        {
            var (_, msg) = await DisplayService.TryModeAsync(target, a.Width, a.Height, DisplayService.AutoHz(target), a.Scaling, previewOnly: true);
            Status(msg, msg.StartsWith("Test finished", StringComparison.Ordinal) ? "Success" : "Danger");
        }, "BaseButton", new Thickness(0, 10, 8, 0));
        tryIt.ToolTip = "Switches now for 10 seconds, then always goes back";
        sec.Children.Add(Ui.Row(save, tryIt));
        var withWin = new CheckBox { Content = "Start Money Tweaks with Windows (minimized) so this works right after sign-in", IsChecked = Core.Features.AutoStart.IsEnabled, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 10, 0, 0) };
        withWin.Click += (_, _) =>
        {
            try { if (withWin.IsChecked == true && Environment.ProcessPath is { } exe) Core.Features.AutoStart.Enable(exe); else Core.Features.AutoStart.Disable(); }
            catch (Exception ex) { withWin.IsChecked = Core.Features.AutoStart.IsEnabled; Status("Couldn't change the startup setting: " + ex.Message, "Danger"); }
        };
        sec.Children.Add(withWin);
        sec.Children.Add(Ui.Muted("Works while Money Tweaks is running (it can stay minimized). Scaling is set to Fullscreen (stretched); your GPU's own scaling setting can override it. Closing Money Tweaks also puts your normal resolution back.", 11.5));
        _auto.Children.Add(sec);
    }

    private static DisplayInfo? SafePrimary()
    {
        try { return DisplayManager.Displays().FirstOrDefault(d => d.Primary); }
        catch { return null; }
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
        // Screenshot mode (website images) hides the Windows user name in the path.
        var shownPath = App.ScreenshotMode ? path.Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) : path;
        pathRow.Children.Add(new TextBlock { Text = shownPath, FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
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
            Status($"Written and read back: {changed} value(s){(missing.Count > 0 ? $", {missing.Count} key(s) not in your file (skipped)" : "")}. Next: create {w}×{h} as a custom resolution in your GPU software (steps below), then start Fortnite.", "Success");
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
