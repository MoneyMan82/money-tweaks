using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.ViewModels;

namespace MoneyTweaks.Views;

/// <summary>A compact list of catalog tweaks with switches, used by tool pages to bundle related tweaks.</summary>
public sealed class TweakBundle : StackPanel
{
    public List<TweakItem> Items { get; }

    public TweakBundle(IEnumerable<string> ids)
    {
        Items = ids.Select(TweakCatalog.Find).Where(t => t is not null).Select(t => new TweakItem(t!)).ToList();
        foreach (var it in Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var sw = new System.Windows.Controls.Primitives.ToggleButton { Style = Ui.Res<Style>("Switch"), IsThreeState = true, VerticalAlignment = VerticalAlignment.Center };
            sw.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(TweakItem.IsOn)) { Mode = System.Windows.Data.BindingMode.OneWay });
            sw.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(TweakItem.CanToggle)));
            sw.DataContext = it;
            sw.Click += async (_, _) =>
            {
                sw.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
                await TweakService.ToggleAsync(it);
                sw.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            };
            DockPanel.SetDock(sw, Dock.Right);
            row.Children.Add(sw);
            var txt = new StackPanel();
            var name = new WrapPanel();
            name.Children.Add(Ui.Text(it.Name, 13, FontWeights.SemiBold));
            name.Children.Add(new Border { Width = 6 });
            name.Children.Add(Ui.Pill(it.RiskLabel, it.IsCaution));
            var status = Ui.Muted("", 11.5);
            status.Foreground = Ui.Brush("Accent");
            status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TweakItem.StatusText)));
            status.DataContext = it;
            name.Children.Add(status);
            txt.Children.Add(name);
            txt.Children.Add(Ui.Muted(it.Description, 12));
            row.Children.Add(txt);
            Children.Add(row);
        }
    }

    public Task RefreshAsync() => TweakService.RefreshStatesAsync(Items);

    public async Task ApplyAllAsync()
    {
        await RefreshAsync();
        var (ok, failed, reboot) = await TweakService.BatchAsync(Items, true);
        MainWindow.Current?.ShowToast($"{ok} applied{(failed > 0 ? $", {failed} failed" : "")}{(reboot ? ". Restart to finish." : ".")}");
    }
}

public sealed class PotatoView : UserControl, IPageLifecycle
{
    private readonly FortnitePotatoSettings _s = App.Settings.FortnitePotato;
    private readonly PotatoPreview _preview = new() { Height = 400 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly WrapPanel _presets = new() { Margin = new Thickness(0, 10, 0, 12) };
    private readonly WrapPanel _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _tris = Ui.Muted("", 12);
    private readonly StackPanel _left = new(), _right = new();
    private readonly Button _reset, _unlock;
    private readonly StackPanel _others = new();
    private readonly StackPanel _driver = new();
    private readonly TweakBundle _windows = new(["gen.visualfx-performance", "gen.transparency", "gen.background-recording", "adv.fso", "adv.system-responsiveness", "adv.games-task", "gen.ultimate-plan"]);
    private bool _building;

    public PotatoView()
    {
        var (root, body) = Ui.Page("Potato Graphics", "Get Fortnite's smooth, rounded, low-poly \"potato\" look and the FPS that comes with it. Pick a preset, compare it in the preview, review every change, then apply. Money Tweaks only changes Fortnite's own video settings, backs up the file first, and can put everything back in one click.", eyebrow: "Gaming tools", icon: "\uE790");

        // ---- Fortnite studio -----------------------------------------------------------------------------------
        var studio = new StackPanel();
        var head = new DockPanel();
        DockPanel.SetDock(_status, Dock.Right);
        head.Children.Add(_status);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = "", Style = Ui.Res<Style>("Icon"), Foreground = Ui.Brush("Accent"), FontSize = 20, Margin = new Thickness(0, 0, 10, 0) });
        title.Children.Add(Ui.Text("Fortnite potato studio", 18, FontWeights.SemiBold));
        head.Children.Add(title);
        studio.Children.Add(head);
        studio.Children.Add(_presets);

        var frame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = _preview, BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1) };
        frame.Clip = null;
        studio.Children.Add(new Border { CornerRadius = new CornerRadius(12), Child = frame });
        var under = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var rotate = new CheckBox { Content = "Auto-rotate", IsChecked = true };
        rotate.Click += (_, _) => _preview.AutoRotate = rotate.IsChecked == true;
        DockPanel.SetDock(rotate, Dock.Right);
        under.Children.Add(rotate);
        under.Children.Add(_tris);
        studio.Children.Add(under);
        studio.Children.Add(ScreenshotRow());
        studio.Children.Add(Ui.Muted("Illustration only: an original Money Tweaks scene, not Fortnite footage. It shows what each setting removes or simplifies; exact in-game results depend on Fortnite's renderer and your GPU.", 11.5));

        var cols = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_right, 2);
        cols.Children.Add(_left);
        cols.Children.Add(_right);
        studio.Children.Add(cols);

        _reset = Ui.Btn("Reset to original", (_, _) => Reset());
        _unlock = Ui.Btn("Unlock settings file", (_, _) => { try { FortnitePotato.Unlock(); MainWindow.Current?.ShowToast("Unlocked. Fortnite can save in-game changes again."); } catch (Exception ex) { Dialog.Show("Couldn't unlock", ex.Message, DialogKind.Error); } RefreshStatus(); });
        var actions = Ui.Row(
            Ui.Btn("Review & apply", (_, _) => ReviewAndApply(), "PrimaryButton"),
            _reset, _unlock,
            Ui.Btn("Open settings folder", (_, _) => OpenFolder()));
        actions.Margin = new Thickness(0, 18, 0, 0);
        studio.Children.Add(actions);
        body.Children.Add(Ui.Card(studio));

        body.Children.Add(Ui.Card(Honesty()));
        var drv = Ui.Section("Graphics driver tweaks (optional, manual)", "These settings live in your GPU driver, which Money Tweaks doesn't change for you. They don't change models; they trim texture-filtering cost and input latency.");
        drv.Children.Add(_driver);
        body.Children.Add(Ui.Card(drv));

        var og = Ui.Section("Other games", "Same idea, applied to each game's own settings file.");
        og.Children.Add(_others);
        body.Children.Add(Ui.Card(og));

        var ws = Ui.Section("Windows potato preset", "System-side changes that help every game. Each one is a regular, reversible tweak.");
        ws.Children.Add(_windows);
        ws.Children.Add(Ui.Row(Ui.Btn("Apply Windows preset", async (_, _) => await _windows.ApplyAllAsync(), "PrimaryButton", new Thickness(0, 10, 0, 0))));
        body.Children.Add(Ui.Card(ws));
        Content = root;

        _debounce.Tick += (_, _) => { _debounce.Stop(); _preview.SetAfter(PreviewLook.From(_s), PresetName(_s.Preset)); };
        _preview.TrianglesChanged += UpdateTris;
        BuildPresets();
        BuildControls();
        _preview.SetAfter(PreviewLook.From(_s), PresetName(_s.Preset));
        BuildOthers();
        _ = LoadDriverGuideAsync();
        RefreshStatus();
    }

    // ---- compare with the user's own real Fortnite screenshots ------------------------------------------------
    private readonly TextBlock _shotStatus = Ui.Muted("", 12);

    private UIElement ScreenshotRow()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var modes = new WrapPanel();
        var illo = new RadioButton { Content = "3D illustration", Style = Ui.Res<Style>("Chip"), GroupName = "pmode", IsChecked = true, Margin = new Thickness(0, 0, 6, 6) };
        var mine = new RadioButton { Content = "My Fortnite screenshots", Style = Ui.Res<Style>("Chip"), GroupName = "pmode", Margin = new Thickness(0, 0, 6, 6) };
        illo.Checked += (_, _) => _preview.ShowIllustration(PresetName(_s.Preset));
        mine.Checked += async (_, _) => await ShowShotsAsync();
        modes.Children.Add(illo);
        modes.Children.Add(mine);
        modes.Children.Add(Ui.Btn("Choose BEFORE screenshot…", async (_, _) => { if (Pick(true)) { mine.IsChecked = true; await ShowShotsAsync(); } }, margin: new Thickness(6, 0, 6, 6)));
        modes.Children.Add(Ui.Btn("Choose AFTER screenshot…", async (_, _) => { if (Pick(false)) { mine.IsChecked = true; await ShowShotsAsync(); } }, margin: new Thickness(0, 0, 6, 6)));
        sp.Children.Add(modes);
        sp.Children.Add(Ui.Muted("To see the real in-game difference: take a screenshot in Fortnite before applying (Win+Alt+PrtScn saves to Videos\\Captures; NVIDIA Alt+F1 also works), apply a preset, restart Fortnite, take a second one from the same spot, then pick both here. Your images stay on this PC.", 11.5));
        sp.Children.Add(_shotStatus);
        return sp;
    }

    private static bool Pick(bool before)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = before ? "Screenshot BEFORE applying" : "Screenshot AFTER applying",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.jxr|All files|*.*",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures"),
        };
        if (dlg.ShowDialog() != true) return false;
        if (before) App.Settings.PotatoBeforeShot = dlg.FileName; else App.Settings.PotatoAfterShot = dlg.FileName;
        App.SaveSettingsSoon();
        return true;
    }

    private async Task ShowShotsAsync()
    {
        string? b = App.Settings.PotatoBeforeShot, a = App.Settings.PotatoAfterShot;
        if (b is null || a is null || !File.Exists(b) || !File.Exists(a))
        {
            _shotStatus.Text = "Pick both a BEFORE and an AFTER screenshot to compare them.";
            return;
        }
        try
        {
            var imgs = await Task.WhenAll(PotatoPreview.LoadImageAsync(b), PotatoPreview.LoadImageAsync(a));
            _preview.ShowScreenshots(imgs[0], imgs[1]);
            _shotStatus.Text = $"Comparing {System.IO.Path.GetFileName(b)} → {System.IO.Path.GetFileName(a)}";
        }
        catch (Exception ex) { _shotStatus.Text = "Couldn't open those images: " + ex.Message; }
    }

    private static string PresetName(FnPreset p) => p switch
    {
        FnPreset.Competitive => "Competitive", FnPreset.Potato => "Potato", FnPreset.UltraPotato => "Ultra potato", _ => "Custom",
    };

    private void UpdateTris()
    {
        var b = _preview.BeforeTriangles; var a = _preview.AfterTriangles;
        _tris.Text = $"Preview scene geometry: {b:N0} → {a:N0} triangles ({(b == 0 ? 0 : 100.0 * (b - a) / b):0}% fewer)";
    }

    // ---- presets & controls ------------------------------------------------------------------------------------
    private void BuildPresets()
    {
        _presets.Children.Clear();
        (FnPreset p, string label, string tip)[] items =
        [
            (FnPreset.Competitive, "Competitive", "Performance mode with normal meshes and a longer view distance: max FPS while keeping the world detailed."),
            (FnPreset.Potato, "Potato", "Performance mode + Low meshes: rounded low-poly builds and terrain, flat textures, no grass or clutter."),
            (FnPreset.UltraPotato, "Ultra potato", "Everything at the absolute minimum, nearest view distance and cosmetics preloaded: the most toy-like look."),
            (FnPreset.Custom, "Custom", "Your own mix. Changing any control switches to Custom."),
        ];
        foreach (var (p, label, tip) in items)
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "fnpreset", IsChecked = _s.Preset == p, ToolTip = tip, Margin = new Thickness(0, 0, 8, 6) };
            rb.Checked += (_, _) =>
            {
                if (_building || p == FnPreset.Custom) { _s.Preset = p; return; }
                var fresh = FortnitePotatoSettings.FromPreset(p);
                CopyInto(fresh, _s);
                BuildControls();
                Changed(keepPreset: true);
            };
            _presets.Children.Add(rb);
        }
    }

    private static void CopyInto(FortnitePotatoSettings from, FortnitePotatoSettings to)
    {
        foreach (var prop in typeof(FortnitePotatoSettings).GetProperties().Where(p => p.CanWrite))
            if (prop.Name is not nameof(FortnitePotatoSettings.Stretched) and not nameof(FortnitePotatoSettings.StretchWidth) and not nameof(FortnitePotatoSettings.StretchHeight))
                prop.SetValue(to, prop.GetValue(from));
    }

    private void Changed(bool keepPreset = false)
    {
        if (_building) return;
        if (!keepPreset && _s.Preset != FnPreset.Custom)
        {
            _s.Preset = FnPreset.Custom;
            _building = true;
            foreach (var rb in _presets.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Content == "Custom";
            _building = false;
        }
        App.SaveSettingsSoon();
        _debounce.Stop();
        _debounce.Start();
    }

    private void BuildControls()
    {
        _building = true;
        _left.Children.Clear();
        _right.Children.Clear();
        _left.Children.Add(Ui.Section("Look & geometry"));
        _left.Children.Add(SwitchRow("Performance mode", "Fortnite's DirectX 11 low-end renderer. The biggest FPS gain, and it unlocks the Meshes option.", _s.PerformanceMode, v => { _s.PerformanceMode = v; BuildControls(); }));
        _left.Children.Add(SwitchRow("Low meshes: rounded low-poly", "Fortnite's own \"Meshes: Low\": simplified, rounded \"bubble-wrap\" builds, terrain and props. This is the potato look.", _s.LowMeshes, v => _s.LowMeshes = v, enabled: _s.PerformanceMode, disabledNote: "Needs Performance mode"));
        _left.Children.Add(LevelRow("Textures", "Low = flat, simple colours with little surface detail.", _s.Textures, v => _s.Textures = v));
        _left.Children.Add(LevelRow("View distance", "How far away buildings, trees and terrain stay detailed. Near removes distant clutter.", _s.ViewDistance, v => _s.ViewDistance = v));
        _left.Children.Add(LevelRow("Foliage", "Bushes, small plants and ground clutter.", _s.Foliage, v => _s.Foliage = v));
        _left.Children.Add(SwitchRow("Show grass", "Grass blades on the ground. Off gives clean, flat terrain.", _s.ShowGrass, v => _s.ShowGrass = v));
        _left.Children.Add(LevelRow("Effects", "Particles, sparkles, explosions and smoke.", _s.Effects, v => _s.Effects = v));

        string? pm = _s.PerformanceMode ? "Performance mode already turns this off; used in DirectX 11/12 modes" : null;
        _right.Children.Add(Ui.Section("Lighting & performance"));
        _right.Children.Add(LevelRow("Shadows", "Dynamic shadows from the sun and objects.", _s.Shadows, v => _s.Shadows = v, pm));
        _right.Children.Add(LevelRow("Post-processing", "Ambient occlusion, bloom and colour grading. Low = clean, basic shading.", _s.PostProcessing, v => _s.PostProcessing = v, pm));
        _right.Children.Add(LevelRow("Lighting & reflections", "Global illumination and reflections. Low = simple, even lighting.", _s.Lighting, v => _s.Lighting = v, pm));
        _right.Children.Add(LevelRow("Surface detail", "Shading, landscape and anti-aliasing quality.", _s.SurfaceDetail, v => _s.SurfaceDetail = v, pm));
        _right.Children.Add(SliderRow());
        _right.Children.Add(SwitchRow("Uncap FPS", "Removes Fortnite's frame-rate limit for the smoothest motion your PC can deliver.", _s.UncapFps, v => _s.UncapFps = v));
        _right.Children.Add(SwitchRow("Preload cosmetics", "Turns off cosmetic streaming so skins load up front: fewer texture pop-ins and stutters mid-match.", _s.PreloadCosmetics, v => _s.PreloadCosmetics = v));
        _right.Children.Add(SwitchRow($"Stretched {_s.StretchWidth}×{_s.StretchHeight}", "Classic 4:3 stretched view. The settings file is locked read-only so Fortnite keeps it; in-game changes won't save until you unlock or reset.", _s.Stretched, v => _s.Stretched = v, caution: true));
        _building = false;
    }

    private UIElement SliderRow()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        var head = new DockPanel();
        var val = Ui.Text($"{_s.RenderScale}%", 13, FontWeights.SemiBold);
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(Ui.Text("3D resolution", 13, FontWeights.SemiBold));
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted("Keep 100% for a clean, sharp potato. Lowering it only adds blur (more FPS on very weak GPUs).", 12));
        var sl = new Slider { Minimum = 50, Maximum = 100, Value = _s.RenderScale, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 0) };
        sl.ValueChanged += (_, e) => { _s.RenderScale = (int)e.NewValue; val.Text = $"{_s.RenderScale}%"; Changed(); };
        sp.Children.Add(sl);
        return sp;
    }

    private UIElement SwitchRow(string label, string desc, bool value, Action<bool> set, bool enabled = true, string? disabledNote = null, bool caution = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8), Opacity = enabled ? 1 : 0.5 };
        var sw = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0) };
        sw.Click += (_, _) => { set(sw.IsChecked == true); Changed(); };
        DockPanel.SetDock(sw, Dock.Right);
        row.Children.Add(sw);
        var txt = new StackPanel();
        var name = new WrapPanel();
        name.Children.Add(Ui.Text(label, 13, FontWeights.SemiBold));
        if (caution) { name.Children.Add(new Border { Width = 8 }); name.Children.Add(Ui.Pill("Caution", true)); }
        txt.Children.Add(name);
        txt.Children.Add(Ui.Muted(enabled || disabledNote is null ? desc : $"{desc} ({disabledNote})", 12));
        row.Children.Add(txt);
        return row;
    }

    private UIElement LevelRow(string label, string desc, int value, Action<int> set, string? note = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8), Opacity = note is null ? 1 : 0.6 };
        sp.Children.Add(Ui.Text(label, 13, FontWeights.SemiBold));
        sp.Children.Add(Ui.Muted(note is null ? desc : $"{desc} {note}.", 12));
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var group = "lvl-" + label + Guid.NewGuid().ToString("N")[..6];
        for (int i = 0; i < 4; i++)
        {
            int level = i;
            var rb = new RadioButton { Content = FortnitePotato.LevelNames[i], Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = value == i, Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => { set(level); Changed(); };
            chips.Children.Add(rb);
        }
        sp.Children.Add(chips);
        return sp;
    }

    // ---- honesty / driver / other games ----------------------------------------------------------------------
    private static UIElement Honesty()
    {
        var sp = Ui.Section("What this really changes");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var can = new StackPanel();
        can.Children.Add(Ui.Pill("Applied by Money Tweaks"));
        can.Children.Add(Ui.Muted("• Fortnite's own video settings in GameUserSettings.ini: Rendering mode, Meshes, Textures, View distance, Effects, Foliage, Grass, Shadows, Post-processing, Lighting, 3D resolution, FPS limit, cosmetic streaming, resolution.\n• The rounded low-poly look comes from Fortnite's Performance mode with Meshes: Low.\n• The file is backed up before the first change, and Reset restores it exactly.\n• Only settings already in your file are changed; nothing new is injected.", 12.5));
        var cant = new StackPanel();
        cant.Children.Add(Ui.Pill("Not possible / never done", true));
        cant.Children.Add(Ui.Muted("• Replacing Fortnite's models, textures or shaders, or making shapes rounder than Fortnite's own Low meshes.\n• Engine.ini console-variable hacks: unsupported by Epic, often ignored, and can break after updates.\n• Editing game files (.pak/.utoc) or injecting into the game: protected by Easy Anti-Cheat and can get accounts banned.\n• The preview above is an illustration of the effect, not a capture of the game.", 12.5));
        Grid.SetColumn(cant, 2);
        g.Children.Add(can);
        g.Children.Add(cant);
        sp.Children.Add(g);
        return sp;
    }

    private async Task LoadDriverGuideAsync()
    {
        var gpus = await Task.Run(() => HardwareMonitor.ReadSpecs().Gpus);
        _driver.Children.Clear();
        bool nv = gpus.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase), amd = gpus.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpus.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        _driver.Children.Add(Ui.Muted($"Detected: {gpus}", 12));
        if (nv)
        {
            _driver.Children.Add(Ui.Text("NVIDIA Control Panel → Manage 3D settings → Program settings → add Fortnite (FortniteClient-Win64-Shipping.exe):", 13, FontWeights.SemiBold));
            _driver.Children.Add(Ui.Muted("1. Texture filtering - Quality: High performance\n2. Texture filtering - Anisotropic sample optimization: On, Trilinear optimization: On\n3. Low Latency Mode: On (or Ultra)\n4. Power management mode: Prefer maximum performance\n5. Vertical sync: Off\n6. For stretched: Display → Adjust desktop size and position → Scaling: Full-screen, Perform scaling on: GPU", 12.5));
            _driver.Children.Add(Ui.Row(Ui.Btn("Open NVIDIA Control Panel", (_, _) => OpenNvidia(), margin: new Thickness(0, 8, 0, 0))));
        }
        if (amd)
        {
            _driver.Children.Add(Ui.Text("AMD Software → Gaming → Fortnite:", 13, FontWeights.SemiBold));
            _driver.Children.Add(Ui.Muted("1. Texture Filtering Quality: Performance\n2. Surface Format Optimization: On\n3. Radeon Anti-Lag: On\n4. Wait for Vertical Refresh: Always off\n5. For stretched: Display → GPU Scaling: On, Scaling Mode: Full panel", 12.5));
        }
        if (!nv && !amd) _driver.Children.Add(Ui.Muted("Open your graphics control panel, set texture filtering to Performance, turn V-Sync off and, for stretched, set scaling to Full-screen.", 12.5));
    }

    private static void OpenNvidia()
    {
        var classic = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\Control Panel Client\nvcplui.exe");
        try
        {
            if (File.Exists(classic)) Process.Start(new ProcessStartInfo(classic) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jk63e8tpmy8!NVIDIACorp.NVIDIAControlPanel") { UseShellExecute = true });
        }
        catch (Exception ex) { Dialog.Show("Couldn't open NVIDIA Control Panel", ex.Message + "\n\nRight-click the desktop and choose NVIDIA Control Panel instead.", DialogKind.Warning); }
    }

    private void BuildOthers()
    {
        _others.Children.Clear();
        foreach (var g in PotatoGraphics.Games.Where(g => g.Id != "fortnite")) _others.Children.Add(GameCard(g));
    }

    private static PotatoOptions OptionsFor(PotatoGame g)
    {
        if (!App.Settings.Potato.TryGetValue(g.Id, out var o)) { o = new PotatoOptions { PerformanceMode = false }; App.Settings.Potato[g.Id] = o; }
        return o;
    }

    private UIElement GameCard(PotatoGame g)
    {
        var o = OptionsFor(g);
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
        var head = new DockPanel();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var apply = Ui.Btn("Apply potato", (_, _) => RunOther(g, true), "PrimaryButton");
        apply.IsEnabled = g.Installed;
        var restore = Ui.Btn("Restore original", (_, _) => RunOther(g, false), margin: new Thickness(0));
        restore.IsEnabled = g.HasBackup;
        btns.Children.Add(apply); btns.Children.Add(restore);
        DockPanel.SetDock(btns, Dock.Right);
        head.Children.Add(btns);
        var title = new WrapPanel();
        title.Children.Add(Ui.Text(g.Name, 15, FontWeights.SemiBold));
        title.Children.Add(new Border { Width = 8 });
        title.Children.Add(Ui.Pill(g.Installed ? (g.HasBackup ? "Potato applied" : "Detected") : "Not found", !g.Installed));
        head.Children.Add(title);
        sp.Children.Add(head);
        sp.Children.Add(Ui.Muted(g.Notes, 12));
        var levels = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var lvl in Enum.GetValues<PotatoLevel>())
        {
            var rb = new RadioButton { Content = lvl switch { PotatoLevel.Low => "Low", PotatoLevel.Potato => "Potato", _ => "Ultra potato" }, Style = Ui.Res<Style>("Chip"), GroupName = "lvl-" + g.Id, IsChecked = o.Level == lvl };
            rb.Checked += (_, _) => { o.Level = lvl; o.RenderScale = PotatoOptions.DefaultScale(lvl) < 75 ? 75 : PotatoOptions.DefaultScale(lvl); App.Settings.Save(); };
            levels.Children.Add(rb);
        }
        sp.Children.Add(levels);
        return sp;
    }

    private void RunOther(PotatoGame g, bool apply)
    {
        if (apply && PotatoGraphics.IsRunning(g)) { Dialog.Show("Close the game first", $"{g.Name} is running and would overwrite its settings file when it exits.", DialogKind.Warning); return; }
        try
        {
            var msg = apply ? PotatoGraphics.Apply(g, OptionsFor(g)) : PotatoGraphics.Restore(g);
            App.Settings.Save();
            MainWindow.Current?.ShowToast($"{g.Name}: {msg}");
        }
        catch (Exception ex) { Log.Error($"Potato Graphics {g.Name}", ex); Dialog.Show("Couldn't change settings", ex.Message, DialogKind.Error); }
        BuildOthers();
    }

    // ---- apply / reset -----------------------------------------------------------------------------------------
    private void RefreshStatus()
    {
        var g = FortnitePotato.Game;
        _status.Children.Clear();
        _status.Children.Add(Ui.Pill(g.Installed ? "Fortnite detected" : "Fortnite settings not found", !g.Installed));
        if (g.HasBackup) _status.Children.Add(Ui.Pill("Original backed up"));
        if (FortnitePotato.IsLocked) _status.Children.Add(Ui.Pill("File locked (read-only)", true));
        _reset.IsEnabled = g.HasBackup;
        _unlock.Visibility = FortnitePotato.IsLocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReviewAndApply()
    {
        var g = FortnitePotato.Game;
        if (!g.Installed) { Dialog.Show("Fortnite settings not found", $"Launch Fortnite once so it creates its settings file, then come back.\n\nExpected at:\n{g.ConfigPath}", DialogKind.Warning); return; }
        if (PotatoGraphics.IsRunning(g)) { Dialog.Show("Close Fortnite first", "Fortnite rewrites its settings file when it exits, which would undo these changes.", DialogKind.Warning); return; }
        List<SettingChange> plan;
        try { plan = FortnitePotato.Plan(_s); }
        catch (Exception ex) { Dialog.Show("Couldn't read Fortnite's settings", ex.Message, DialogKind.Error); return; }
        var changes = plan.Where(p => p.Changes).GroupBy(p => p.Label).Select(x => x.First()).ToList();
        var same = plan.Count(p => !p.Missing && !p.Changes);
        var missing = plan.Where(p => p.Missing).GroupBy(p => p.Label).Select(x => x.First()).ToList();
        var sb = new System.Text.StringBuilder();
        if (changes.Count == 0) sb.AppendLine("Your file already matches these settings.");
        else
        {
            sb.AppendLine($"Will change ({changes.Count}):");
            foreach (var c in changes) sb.AppendLine($"• {c.Label}   ({c.Key.Replace("|", "] ")}: {c.Current} → {c.New})");
        }
        if (same > 0) sb.AppendLine($"\nAlready set: {same}");
        if (missing.Count > 0)
        {
            sb.AppendLine($"\nNot in your file yet ({missing.Count}). Set these in Fortnite's menu:");
            foreach (var m in missing) sb.AppendLine($"• {m.Label}");
            if (missing.Any(m => m.Key.EndsWith("MeshQuality"))) sb.AppendLine("  (Meshes appears once you've switched to Performance mode in-game.)");
        }
        sb.AppendLine($"\nA backup of GameUserSettings.ini is {(g.HasBackup ? "already saved" : "made first")}. Reset puts it back.");
        if (_s.Stretched) sb.AppendLine("The file will be locked read-only to keep the stretched resolution.");
        if (!Dialog.Confirm($"Apply \"{PresetName(_s.Preset)}\" to Fortnite?", sb.ToString().TrimEnd(), "Apply")) return;
        try
        {
            var (changed, miss) = FortnitePotato.Apply(_s);
            var msg = $"{changed} settings written to Fortnite.";
            msg += "\n\nIn Fortnite, open Settings › Video and check:\n• Rendering Mode: Performance\n• Meshes: " + (_s.LowMeshes ? "Low" : "your choice") + "\n• Textures: " + FortnitePotato.LevelNames[_s.Textures];
            if (miss.Count > 0) msg += $"\n\n{miss.Count} setting(s) weren't in your file yet; set them in-game once.";
            Dialog.Show("Fortnite is now a potato", msg);
        }
        catch (Exception ex)
        {
            Log.Error("Fortnite potato apply", ex);
            Dialog.Show("Couldn't apply", ex.Message, DialogKind.Error);
        }
        RefreshStatus();
    }

    private void Reset()
    {
        var g = FortnitePotato.Game;
        if (!g.HasBackup) { MainWindow.Current?.ShowToast("No backup yet. Nothing to reset."); return; }
        if (PotatoGraphics.IsRunning(g)) { Dialog.Show("Close Fortnite first", "Fortnite would overwrite the restored file when it exits.", DialogKind.Warning); return; }
        if (!Dialog.Confirm("Reset Fortnite graphics?", "Restores GameUserSettings.ini exactly as it was before Money Tweaks first changed it, and removes the read-only lock.", "Reset", DialogKind.Warning)) return;
        try { MainWindow.Current?.ShowToast("Fortnite: " + FortnitePotato.Reset()); }
        catch (Exception ex) { Dialog.Show("Couldn't reset", ex.Message, DialogKind.Error); }
        RefreshStatus();
    }

    private static void OpenFolder()
    {
        var dir = System.IO.Path.GetDirectoryName(FortnitePotato.Game.ConfigPath)!;
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        else MainWindow.Current?.ShowToast("Fortnite's config folder doesn't exist yet.");
    }

    /// <summary>Screenshot mode only: show a preset in the preview without changing the user's saved settings.</summary>
    internal void PreviewPresetOnly(FnPreset? p)
    {
        _preview.AutoRotate = false;
        var s = p is { } preset ? FortnitePotatoSettings.FromPreset(preset) : _s;
        _preview.SetAfter(PreviewLook.From(s), PresetName(s.Preset));
    }

    public void OnShown() { _preview.AutoRotate = true; RefreshStatus(); BuildOthers(); _ = _windows.RefreshAsync(); }
    public void OnHidden() { _preview.AutoRotate = false; App.Settings.Save(); }
}

public sealed class BoosterView : UserControl, IPageLifecycle
{
    private readonly GameBooster _booster = new();
    private readonly WrapPanel _procs = new();
    private readonly TextBox _game = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _purge = new() { Content = "Clear standby memory (admin)", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _report = Ui.Muted("");
    private readonly Button _start, _end;

    public BoosterView()
    {
        _start = Ui.Btn("⚡ Start boost", Start, "PrimaryButton");
        _end = Ui.Btn("End boost", End);
        _end.IsEnabled = false;
        var (root, body) = Ui.Page("Game Booster", "One click before you play: switches to the High/Ultimate Performance power plan, closes the background apps you choose, raises your game to High priority and clears cached standby memory. End boost restores your power plan.",
            Ui.Row(_start, _end), eyebrow: "Gaming tools", icon: "\uE945");

        var gs = Ui.Section("Game to prioritize", "Process name of the running game, e.g. FortniteClient-Win64-Shipping or cs2. Leave it empty to skip.");
        _game.Text = App.Settings.BoosterGameProcess;
        gs.Children.Add(_game);
        _purge.IsChecked = App.Settings.BoosterPurgeStandby;
        gs.Children.Add(_purge);
        body.Children.Add(Ui.Card(gs));

        var ps = Ui.Section("Apps to close while boosting", "Your choices are remembered. System processes, anti-cheat and Money Tweaks itself are never listed. Apps with a window get a normal close request first so they can save.");
        ps.Children.Add(Ui.Row(Ui.Btn("Refresh list", (_, _) => LoadProcs(), margin: new Thickness(0, 0, 0, 8))));
        ps.Children.Add(_procs);
        body.Children.Add(Ui.Card(ps));
        var rs = Ui.Section("Last boost");
        rs.Children.Add(_report);
        body.Children.Add(Ui.Card(rs));
        Content = root;
    }

    private bool _loadingProcs;
    private async void LoadProcs()
    {
        if (_loadingProcs) return;
        _loadingProcs = true;
        _procs.Children.Clear();
        _procs.Children.Add(Ui.Muted("Scanning running apps…"));
        // Enumerating processes and window titles takes 100-300 ms: keep it off the UI thread.
        var procs = await Task.Run(() => GameBooster.ClosableProcesses().Take(60).ToList());
        _loadingProcs = false;
        _procs.Children.Clear();
        foreach (var p in procs)
        {
            var cb = new CheckBox
            {
                Content = $"{p.Name}  ·  {p.WorkingSetMb:0} MB", Tag = p.Name, Width = 300, Margin = new Thickness(0, 4, 8, 4),
                IsChecked = App.Settings.BoosterCloseList.Contains(p.Name, StringComparer.OrdinalIgnoreCase), ToolTip = p.Title
            };
            cb.Checked += (_, _) => { if (!App.Settings.BoosterCloseList.Contains(p.Name)) App.Settings.BoosterCloseList.Add(p.Name); };
            cb.Unchecked += (_, _) => App.Settings.BoosterCloseList.RemoveAll(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            _procs.Children.Add(cb);
        }
    }

    private async void Start(object s, RoutedEventArgs e)
    {
        App.Settings.BoosterGameProcess = _game.Text.Trim();
        App.Settings.BoosterPurgeStandby = _purge.IsChecked == true;
        App.Settings.Save();
        var close = App.Settings.BoosterCloseList.ToList();
        if (close.Count > 0 && !Dialog.Confirm("Close these apps?", string.Join("\n", close.Select(c => "• " + c)) + "\n\nUnsaved work in them may be lost.", "Boost")) return;
        _start.IsEnabled = false;
        var lines = await Task.Run(() => _booster.Start(close, App.Settings.BoosterGameProcess, App.Settings.BoosterPurgeStandby));
        _report.Text = string.Join("\n", lines);
        _end.IsEnabled = true;
        MainWindow.Current?.ShowToast("Boost active. Good luck!");
        LoadProcs();
    }

    private async void End(object s, RoutedEventArgs e)
    {
        var msg = await Task.Run(_booster.End);
        _report.Text = msg;
        _end.IsEnabled = false;
        _start.IsEnabled = true;
    }

    public void OnShown() => LoadProcs();
    public void OnHidden() { }
}

public sealed class InputDelayView : UserControl, IPageLifecycle
{
    private readonly TextBlock _current = Ui.Text("", 28, FontWeights.SemiBold);
    private readonly TextBlock _range = Ui.Muted("");
    private readonly Slider _slider = new() { Minimum = 0.5, Maximum = 1.0, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _sliderVal = Ui.Text("", 13);
    private readonly Button _hold, _release;
    private double? _held;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TweakBundle _bundle = new(["gen.mouse-accel", "gen.keyboard-repeat", "gen.sticky-keys", "gen.usb-suspend", "adv.fso", "adv.global-timer-res", "adv.timer-dynamic-tick", "adv.priority-separation", "adv.gpu-msi"]);

    public InputDelayView()
    {
        _hold = Ui.Btn("Hold high-precision timer", Hold, "PrimaryButton");
        _release = Ui.Btn("Release", Release);
        _release.IsEnabled = false;
        var (root, body) = Ui.Page("Input Delay Reducer", "Lowers the Windows timer interval so games and input polling wake up more often, and bundles the input-related tweaks in one place. The timer request is held only while Money Tweaks is open; close the app and Windows goes back to normal.", eyebrow: "Gaming tools", icon: "\uE962");

        var ts = Ui.Section("Kernel timer resolution");
        ts.Children.Add(_current);
        ts.Children.Add(_range);
        _slider.Value = Math.Clamp(App.Settings.TimerResolutionMs, 0.5, 1.0);
        _slider.ValueChanged += (_, _) => _sliderVal.Text = $"Request: {_slider.Value:0.00} ms";
        _sliderVal.Text = $"Request: {_slider.Value:0.00} ms";
        var sliderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) };
        sliderRow.Children.Add(_slider);
        sliderRow.Children.Add(new Border { Width = 12 });
        sliderRow.Children.Add(_sliderVal);
        ts.Children.Add(sliderRow);
        ts.Children.Add(Ui.Row(_hold, _release));
        ts.Children.Add(Ui.Muted(OsInfo.IsWindows11
            ? "Windows 11: a timer request only affects the requesting process unless 'Global timer resolution requests' (below) is applied and the PC restarted."
            : "Windows 10: the request applies system-wide while it is held.", 12));
        body.Children.Add(Ui.Card(ts));

        var bs = Ui.Section("Input tweak bundle", "Each switch is a normal Money Tweaks tweak with its own backup.");
        bs.Children.Add(_bundle);
        body.Children.Add(Ui.Card(bs));
        Content = root;
        _timer.Tick += (_, _) => UpdateReadout();
    }

    private void UpdateReadout()
    {
        var (min, max, cur) = TimerResolution.Query();
        _current.Text = $"{cur:0.###} ms";
        _range.Text = $"Supported range {max:0.###} – {min:0.###} ms{(_held is null ? "" : $" · holding {_held:0.##} ms")}";
    }

    private void Hold(object s, RoutedEventArgs e)
    {
        if (_held is { } old) TimerResolution.Release(old);
        _held = _slider.Value;
        var got = TimerResolution.Set(_slider.Value);
        App.Settings.TimerResolutionMs = _slider.Value;
        Log.Info($"Timer resolution requested {_slider.Value:0.00} ms, system now {got:0.###} ms");
        _release.IsEnabled = true;
        UpdateReadout();
    }

    private void Release(object s, RoutedEventArgs e)
    {
        if (_held is { } h) TimerResolution.Release(h);
        _held = null;
        _release.IsEnabled = false;
        UpdateReadout();
    }

    public void OnShown() { _timer.Start(); UpdateReadout(); _ = _bundle.RefreshAsync(); }
    public void OnHidden() => _timer.Stop();
}

public sealed class PingView : UserControl, IPageLifecycle
{
    private readonly PingMonitor _mon = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly TextBox _host = new() { Width = 220 };
    private readonly TextBlock _grade = Ui.Text("Idle", 26, FontWeights.SemiBold);
    private readonly TextBlock _stats = Ui.Muted("");
    private readonly Canvas _chart = new() { Height = 160, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Polyline _line = new() { Stroke = Ui.Brush("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Button _toggle;
    private readonly TweakBundle _bundle = new(["net.nagle", "net.throttling-index", "net.qos-reserve", "net.adapter-power", "net.autotuning", "net.dns-cloudflare"]);
    private bool _running, _busy;

    public PingView()
    {
        _toggle = Ui.Btn("▶ Start monitoring", Toggle, "PrimaryButton");
        var (root, body) = Ui.Page("Ping Stabilizer", "Measures latency, jitter and packet loss continuously, then applies the network tweaks that cut lag spikes. It can't fix a bad route to a game server; it removes the local causes of spikes.", eyebrow: "Gaming tools", icon: "\uE701");
        _host.Text = App.Settings.PingHost;
        var hostRow = new StackPanel { Orientation = Orientation.Horizontal };
        hostRow.Children.Add(_host);
        hostRow.Children.Add(new Border { Width = 8 });
        foreach (var (label, host) in PingMonitor.Targets)
            hostRow.Children.Add(Ui.Btn(label.Split(' ')[0] == "Your" ? "Router" : label.Split(' ')[0], (_, _) => _host.Text = host == "gateway" ? PingMonitor.DefaultGateway() ?? "" : host));
        var ms = Ui.Section("Live connection");
        ms.Children.Add(hostRow);
        ms.Children.Add(Ui.Row(_toggle, Ui.Btn("Flush DNS cache", (_, _) => { var r = ProcessRunner.Run(System.IO.Path.Combine(ProcessRunner.System32, "ipconfig.exe"), "/flushdns"); MainWindow.Current?.ShowToast(r.Ok ? "DNS cache flushed." : "Flush failed: " + r.Combined); })));
        ms.Children.Add(new Border { Height = 10 });
        ms.Children.Add(_grade);
        ms.Children.Add(_stats);
        _chart.Children.Add(_line);
        ms.Children.Add(new Border { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6), Child = _chart });
        body.Children.Add(Ui.Card(ms));
        var bs = Ui.Section("Stabilize", "Apply the network tweaks most associated with lag spikes. Each is reversible.");
        bs.Children.Add(_bundle);
        bs.Children.Add(Ui.Row(Ui.Btn("Apply all", async (_, _) => await _bundle.ApplyAllAsync(), "PrimaryButton", new Thickness(0, 10, 0, 0))));
        body.Children.Add(Ui.Card(bs));
        Content = root;
        _timer.Tick += Tick;
        _chart.SizeChanged += (_, _) => Draw();
    }

    private void Toggle(object s, RoutedEventArgs e)
    {
        _running = !_running;
        _toggle.Content = _running ? "■ Stop" : "▶ Start monitoring";
        if (_running) { _mon.Reset(); App.Settings.PingHost = _host.Text.Trim(); _timer.Start(); } else _timer.Stop();
    }

    private async void Tick(object? s, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var st = await _mon.PingOnceAsync(_host.Text.Trim());
            _grade.Text = st.Grade;
            _grade.Foreground = Ui.Brush(st.Grade == "Stable" ? "Accent" : st.Grade == "Fair" ? "Gold" : st.Grade == "Unstable" ? "Danger" : "Text");
            _stats.Text = $"Now {(st.LastMs is { } l ? $"{l:0} ms" : "timeout")} · avg {st.AvgMs:0.0} ms · min {st.MinMs:0} · max {st.MaxMs:0} · jitter {st.JitterMs:0.0} ms · loss {st.LossPercent:0.#}% ({st.Sent} samples)";
            Draw();
        }
        finally { _busy = false; }
    }

    private void Draw()
    {
        var samples = _mon.Samples;
        double w = _chart.ActualWidth, h = _chart.ActualHeight;
        if (w <= 0 || samples.Count < 2) return;
        var max = Math.Max(50, samples.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(50).Max() * 1.2);
        var pts = new PointCollection();
        for (int i = 0; i < samples.Count; i++)
            pts.Add(new Point(w * i / 119.0, samples[i] is { } v ? h - h * v / max : h));
        _line.Points = pts;
    }

    public void OnShown() => _ = _bundle.RefreshAsync();
    public void OnHidden() { }
}
