using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>Monitor bounds in physical pixels via EnumDisplayMonitors (replaces WinForms Screen, which cost ~300 ms to load).</summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>All monitors, primary first.</summary>
    public static List<Int32Rect> All()
    {
        var list = new List<(Int32Rect r, bool primary)>();
        EnumDisplayMonitors(0, 0, (m, _, _, _) =>
        {
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref mi))
                list.Add((new Int32Rect(mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, mi.Monitor.Bottom - mi.Monitor.Top), (mi.Flags & 1) != 0));
            return true;
        }, 0);
        if (list.Count == 0) list.Add((new Int32Rect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight), true));
        return list.OrderByDescending(x => x.primary).Select(x => x.r).ToList();
    }
}

/// <summary>
/// Crosshair editor: layers, live preview, profiles, firing animation, the desktop overlay and per-game profiles.
/// Every edit goes to <see cref="CrosshairService"/>, which redraws the real overlay immediately and saves.
/// </summary>
public sealed partial class CrosshairView : UserControl, IPageLifecycle
{
    /// <summary>Undo history survives leaving and reopening the page.</summary>
    private static readonly CrosshairHistory History = new();
    private static CrosshairData D => CrosshairService.Data;
    private static CrosshairProfile P => CrosshairService.Active;

    private string? _layerId;
    private string _shownProfile = "";
    private readonly CrosshairVisual _preview = new();
    private readonly Grid _previewArea = new() { Height = 420, ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Image _previewImage = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly StackPanel _toolbar = new(), _props = new(), _layers = new(), _profiles = new(), _firing = new(), _overlay = new(), _games = new(), _notices = new();
    private readonly TextBlock _previewCaption = Ui.Muted("", 12);
    private readonly Button _show, _undo, _redo;
    private readonly DispatcherTimer _commit = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _anim = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly BloomState _previewBloom = new();
    private readonly System.Diagnostics.Stopwatch _animClock = new();
    private bool _previewFiring, _shown;

    private CrosshairLayer? L => P.Layers.FirstOrDefault(l => l.Id == _layerId);

    public CrosshairView()
    {
        _show = Ui.Btn("Show crosshair", (_, _) => CrosshairService.Toggle(), "PrimaryButton");
        _undo = Ui.Btn("", (_, _) => Undo(), "GhostButton");
        _redo = Ui.Btn("", (_, _) => Redo(), "GhostButton");
        _undo.Content = IconText("\uE7A7", "Undo");
        _redo.Content = IconText("\uE7A6", "Redo");
        _undo.ToolTip = "Undo (Ctrl+Z)";
        _redo.ToolTip = "Redo (Ctrl+Y)";
        var (root, body) = Ui.Page("Custom Crosshair",
            "Build a crosshair from layers, see it live, and it's drawn on your screen as a click-through overlay. Every change updates the overlay immediately. Works over windowed and borderless games (not exclusive fullscreen).",
            Ui.Row(_undo, _redo, _show), eyebrow: "Gaming tools", icon: "\uE710");

        body.Children.Add(_notices);

        // Row 1: big preview | properties of the selected layer
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var pv = new StackPanel();
        pv.Children.Add(_toolbar);
        _previewArea.Children.Add(_previewImage);
        _previewArea.Children.Add(_preview);
        pv.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _previewArea, BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1) });
        _previewCaption.Margin = new Thickness(0, 8, 0, 0);
        _previewCaption.TextWrapping = TextWrapping.Wrap;
        pv.Children.Add(_previewCaption);
        top.Children.Add(Ui.Card(pv, new Thickness(0, 0, 12, 12)));
        var propsScroll = new ScrollViewer { Content = _props, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        var pc = Ui.Card(propsScroll, new Thickness(0, 0, 0, 12));
        pc.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(pc, 1);
        top.Children.Add(pc);
        body.Children.Add(top);

        // Row 2: layers | profiles
        var mid = new Grid();
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lc = Ui.Card(_layers, new Thickness(0, 0, 12, 12));
        mid.Children.Add(lc);
        var prc = Ui.Card(_profiles, new Thickness(0, 0, 0, 12));
        Grid.SetColumn(prc, 1);
        mid.Children.Add(prc);
        body.Children.Add(mid);

        body.Children.Add(Ui.Card(_firing, new Thickness(0, 0, 0, 12)));
        body.Children.Add(Ui.Card(_overlay, new Thickness(0, 0, 0, 12)));
        body.Children.Add(Ui.Card(_games, new Thickness(0, 0, 0, 12)));
        body.Children.Add(Ui.Card(Compatibility()));
        Content = root;

        // Hold the mouse on the preview to see the firing animation.
        _previewArea.MouseLeftButtonDown += (_, e) => { _previewFiring = true; _previewArea.CaptureMouse(); StartAnim(); e.Handled = true; };
        _previewArea.MouseLeftButtonUp += (_, _) => { _previewFiring = false; _previewArea.ReleaseMouseCapture(); };
        _previewArea.LostMouseCapture += (_, _) => _previewFiring = false;
        _anim.Tick += (_, _) => AnimTick();
        _commit.Tick += (_, _) => { _commit.Stop(); History.Commit(P); UpdateUndo(); };
        PreviewKeyDown += OnKeys;

        RenderAll();
    }

    public void OnShown()
    {
        _shown = true;
        CrosshairService.Changed += OnServiceChanged;
        OnServiceChanged();
        StartLiveInput();
    }

    public void OnHidden()
    {
        _shown = false;
        CrosshairService.Changed -= OnServiceChanged;
        _live.Stop();
        FlushCommit();
        CrosshairService.SaveNow();
    }

    private void OnServiceChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnServiceChanged); return; }
        if (P.Id != _shownProfile) { RenderAll(); return; } // switched by hotkey or a game
        UpdateHeader();
        RenderNotices();
        RenderOverlayStatus();
        RenderGameStatus();
    }

    // ---- rendering ----------------------------------------------------------------------------------------------

    private void RenderAll()
    {
        _shownProfile = P.Id;
        History.Track(P);
        if (L is null) _layerId = P.Layers.LastOrDefault()?.Id;
        RenderNotices();
        RenderToolbar();
        RenderPreview();
        RenderProps();
        RenderLayers();
        RenderProfiles();
        RenderFiring();
        RenderOverlay();
        RenderGames();
        UpdateHeader();
        UpdateUndo();
    }

    private void UpdateHeader() => _show.Content = CrosshairService.Visible ? "Hide crosshair" : "Show crosshair";

    private void UpdateUndo()
    {
        _undo.IsEnabled = History.CanUndo(P) || _commit.IsEnabled;
        _redo.IsEnabled = History.CanRedo(P) && !_commit.IsEnabled;
    }

    private void RenderNotices()
    {
        _notices.Children.Clear();
        void Note(string text, string tone)
        {
            var b = new Border { Background = Ui.Brush(tone + "Soft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10) };
            b.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text"), FontSize = 12.5 };
            _notices.Children.Add(b);
        }
        if (CrosshairService.LoadNotice is { } n) Note(n, "Gold");
        if (CrosshairService.SaveError.Length > 0) Note(CrosshairService.SaveError, "Danger");
    }

    /// <summary>One edit happened: overlay + preview update now, undo step after a short pause, save soon.</summary>
    private void Edited(bool structural = false)
    {
        P.Modified = DateTime.Now;
        CrosshairService.Edited();
        _preview.Redraw();
        if (structural) { FlushCommit(); History.Commit(P); }
        else { _commit.Stop(); _commit.Start(); }
        UpdateUndo();
        UpdateCaption();
    }

    private void FlushCommit()
    {
        if (!_commit.IsEnabled) return;
        _commit.Stop();
        History.Commit(P);
    }

    private void Undo()
    {
        FlushCommit();
        if (History.Undo(P) is { } prev) ReplaceActive(prev);
    }

    private void Redo()
    {
        FlushCommit();
        if (History.Redo(P) is { } next) ReplaceActive(next);
    }

    private void ReplaceActive(CrosshairProfile p)
    {
        int i = D.Profiles.FindIndex(x => x.Id == p.Id);
        if (i < 0) return;
        D.Profiles[i] = p;
        CrosshairService.Edited();
        RenderAll();
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return; // text boxes keep their own undo
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.Z && !shift) { Undo(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.Y || e.Key == Key.Z && shift)) { Redo(); e.Handled = true; }
        else if (e.Key == Key.Delete && L is not null) { DeleteLayer(); e.Handled = true; }
    }

    // ---- preview --------------------------------------------------------------------------------------------------

    private static readonly (string id, string label)[] Backgrounds = [("Dark", "Dark"), ("Grey", "Grey"), ("Sky", "Sky"), ("Grass", "Grass"), ("Image", "Your screenshot…")];

    private void RenderToolbar()
    {
        _toolbar.Children.Clear();
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (id, label) in Backgrounds)
        {
            var bg = id;
            var rb = Chip(label, "xbg", D.PreviewBackground == bg);
            rb.Click += (_, _) =>
            {
                if (bg == "Image" && !ChoosePreviewImage()) { RenderToolbar(); return; }
                D.PreviewBackground = bg;
                CrosshairService.SaveSoon();
                RenderPreview();
            };
            row.Children.Add(rb);
        }
        row.Children.Add(new Border { Width = 14 });
        foreach (var z in new[] { 1, 2, 3, 4, 6, 8 })
        {
            int zoom = z;
            var rb = Chip(z + "×", "xzoom", Math.Abs(D.PreviewZoom - z) < 0.01);
            rb.ToolTip = z == 1 ? "Actual size: exactly as it appears on your screen" : $"Magnified {z}×";
            rb.Click += (_, _) => { D.PreviewZoom = zoom; CrosshairService.SaveSoon(); RenderPreview(); };
            row.Children.Add(rb);
        }
        var grid = new CheckBox { Content = "Pixel grid", IsChecked = D.PreviewGrid, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 6), ToolTip = "Shows screen pixels at 4× and more" };
        grid.Click += (_, _) => { D.PreviewGrid = grid.IsChecked == true; CrosshairService.SaveSoon(); RenderPreview(); };
        row.Children.Add(grid);
        _toolbar.Children.Add(row);
    }

    private bool ChoosePreviewImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a game screenshot for the preview background", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog() != true) return false;
        D.PreviewImage = dlg.FileName;
        return true;
    }

    private void RenderPreview()
    {
        _previewArea.Background = D.PreviewBackground switch
        {
            "Grey" => new SolidColorBrush(Color.FromRgb(0x7A, 0x7F, 0x86)),
            "Sky" => new LinearGradientBrush(Color.FromRgb(0x8C, 0xC8, 0xF5), Color.FromRgb(0xE4, 0xF2, 0xFC), 90),
            "Grass" => new LinearGradientBrush(Color.FromRgb(0x5E, 0x8F, 0x3A), Color.FromRgb(0x34, 0x5E, 0x22), 90),
            _ => new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)),
        };
        _previewImage.Source = null;
        if (D.PreviewBackground == "Image" && D.PreviewImage is { } path)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(path); bi.DecodePixelWidth = 1600; bi.EndInit(); bi.Freeze();
                _previewImage.Source = bi;
            }
            catch { _previewCaption.Text = "The preview screenshot couldn't be loaded."; }
        }
        _preview.Profile = P;
        _preview.Zoom = D.PreviewZoom;
        _preview.ShowPixelGrid = D.PreviewGrid;
        _preview.CrosshairScale = D.Overlay.Scale;
        _preview.GlobalOpacity = D.Overlay.Opacity;
        RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);
        _preview.Redraw();
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        var zoom = D.PreviewZoom <= 1 ? "actual size" : $"magnified {D.PreviewZoom:0}×";
        var fire = P.Firing.Enabled ? " Hold the mouse button on the preview to test the firing animation." : "";
        _previewCaption.Text = $"\"{P.Name}\" · {P.Layers.Count} layer{(P.Layers.Count == 1 ? "" : "s")} · {zoom}, includes overlay scale {D.Overlay.Scale:0.##}× and opacity {D.Overlay.Opacity:P0}.{fire}";
    }

    private void StartAnim()
    {
        if (_anim.IsEnabled) return;
        _animClock.Restart();
        _anim.Start();
    }

    private void AnimTick()
    {
        var dt = _animClock.Elapsed.TotalMilliseconds;
        _animClock.Restart();
        var f = P.Firing;
        bool firing = _previewFiring && f.Enabled;
        if (_previewBloom.Update(firing, dt, f)) { _preview.Bloom = _previewBloom.Value; _preview.Redraw(); }
        if (!firing && _previewBloom.Value <= 0) _anim.Stop();
    }

    // ---- shared controls ------------------------------------------------------------------------------------------

    private static RadioButton Chip(string text, string group, bool on) =>
        new() { Content = text, Style = Ui.Res<Style>("Chip"), GroupName = group, IsChecked = on, Margin = new Thickness(0, 0, 6, 6) };

    private static CheckBox Check(string text, bool value, Action<bool> set, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 6, 14, 2), ToolTip = tip };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    /// <summary>Label, slider, exact value box and ± buttons. Values outside the slider range can be typed (within the hard limits).</summary>
    private static UIElement Num(string label, double value, double min, double max, double step, Action<double> set, string unit = "", double hardMin = double.NaN, double hardMax = double.NaN, string? tip = null)
    {
        if (double.IsNaN(hardMin)) hardMin = min;
        if (double.IsNaN(hardMax)) hardMax = max;
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0), ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Ui.Muted(label, 12.5);
        name.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(name);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = step, LargeChange = step * 5, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = step };
        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        string Fmt(double v) => v.ToString(step < 0.1 ? "0.##" : step < 1 ? "0.#" : "0", CultureInfo.CurrentCulture);
        var box = new TextBox { Text = Fmt(value), Width = 54, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Type an exact value ({Fmt(hardMin)} to {Fmt(hardMax)}{unit})" };
        bool sync = false;
        void Set(double v, bool fromSlider)
        {
            v = Math.Clamp(Math.Round(v / step) * step, hardMin, hardMax);
            sync = true;
            if (!fromSlider) slider.Value = Math.Clamp(v, min, max);
            box.Text = Fmt(v);
            sync = false;
            set(v);
        }
        slider.ValueChanged += (_, e) => { if (!sync) Set(e.NewValue, true); };
        void Commit()
        {
            if (double.TryParse(box.Text.Replace(unit, "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
                double.TryParse(box.Text.Replace(unit, "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) Set(v, false);
            else box.Text = Fmt(value);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Up) { Commit(); Set(Current() + step, false); e.Handled = true; }
            else if (e.Key == Key.Down) { Commit(); Set(Current() - step, false); e.Handled = true; }
        };
        double Current() => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var c) ? c : slider.Value;
        Button Nudge(string t, double d)
        {
            var b = new Button { Content = t, Width = 24, Height = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Style = Ui.Res<Style>("GhostButton"), ToolTip = (d > 0 ? "+" : "−") + Fmt(step) + unit };
            b.Click += (_, _) => Set(Current() + d, false);
            return b;
        }
        right.Children.Add(Nudge("−", -step));
        right.Children.Add(box);
        right.Children.Add(Nudge("+", step));
        if (unit.Length > 0) right.Children.Add(new TextBlock { Text = unit, Foreground = Ui.Brush("Muted"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), Width = 22 });
        else right.Children.Add(new Border { Width = 25 });
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return g;
    }

    private static readonly string[] Swatches = ["#FF2BE38B", "#FF00FF00", "#FF00FFFF", "#FFFFFF00", "#FFFF2D55", "#FFFF00FF", "#FFFF8C00", "#FFFFFFFF", "#FF000000"];

    /// <summary>Swatches plus an exact #RRGGBB / #AARRGGBB box.</summary>
    private static UIElement ColorPicker(string label, string value, Action<string> set, string[]? swatches = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var head = new DockPanel();
        var name = Ui.Muted(label, 12.5);
        name.Width = 96;
        name.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(name);
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(swatch, Dock.Left);
        head.Children.Add(swatch);
        var box = new TextBox { Text = value, Width = 96, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "#RRGGBB or #AARRGGBB (AA = transparency)" };
        head.Children.Add(box);
        sp.Children.Add(head);
        void Paint(string hex) { if (Argb.TryParse(hex, out var c)) swatch.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)); }
        Paint(value);
        void Apply(string hex)
        {
            if (!Argb.TryParse(hex, out var c)) { box.Text = value; return; }
            value = c.ToString();
            box.Text = value;
            Paint(value);
            set(value);
        }
        box.LostKeyboardFocus += (_, _) => { if (box.Text != value) Apply(box.Text); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(box.Text); e.Handled = true; } };
        var row = new WrapPanel { Margin = new Thickness(96, 6, 0, 0) };
        foreach (var hex in swatches ?? Swatches)
        {
            var h = hex;
            var b = new Button { Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(0), Style = Ui.Res<Style>("BaseButton"), ToolTip = hex, Background = (Brush)new BrushConverter().ConvertFrom(hex)! };
            b.Click += (_, _) => Apply(h);
            row.Children.Add(b);
        }
        sp.Children.Add(row);
        return sp;
    }

    private static StackPanel IconText(string icon, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = icon, FontFamily = Ui.Res<FontFamily>("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

}
