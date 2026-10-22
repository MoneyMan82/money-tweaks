using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Macros;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

public sealed partial class CrosshairView
{
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private TextBlock? _liveText, _overlayStatus, _gameStatus;
    private readonly Dictionary<string, TextBlock> _hotkeyMsgs = [];

    // ---- properties of the selected layer -------------------------------------------------------------------------

    private void RenderProps()
    {
        _props.Children.Clear();
        var l = L;
        if (l is null)
        {
            _props.Children.Add(Ui.Section("Layer"));
            _props.Children.Add(Ui.Muted("This profile has no layers yet. Add lines, a dot, a circle or an image below.", 12.5));
            return;
        }
        var head = Ui.Section("Edit layer", KindHint(l.Kind));
        _props.Children.Add(head);

        var name = new TextBox { Text = l.Name, Margin = new Thickness(0, 0, 0, 6), ToolTip = "Layer name" };
        name.TextChanged += (_, _) => { if (L is { } x) { x.Name = CrosshairSanitizer.Name(name.Text, CrosshairLayer.DefaultName(x.Kind)); Edited(); RenderLayersSoon(); } };
        _props.Children.Add(name);
        var flags = new WrapPanel();
        flags.Children.Add(Check("Visible", l.Visible, v => { if (L is { } x) { x.Visible = v; Edited(true); RenderLayers(); } }));
        flags.Children.Add(Check("Reacts to firing", l.ReactsToFiring, v => { if (L is { } x) { x.ReactsToFiring = v; Edited(true); } }, "Grows with the firing animation (set it up under Firing animation)"));
        _props.Children.Add(flags);

        void Set(Action<CrosshairLayer> change) { if (L is { } x) { change(x); Edited(); } }

        switch (l.Kind)
        {
            case LayerKind.Lines:
                _props.Children.Add(Sub("Shape"));
                var arms = new WrapPanel();
                arms.Children.Add(Check("Top", l.Top, v => Set(x => x.Top = v)));
                arms.Children.Add(Check("Bottom", l.Bottom, v => Set(x => x.Bottom = v)));
                arms.Children.Add(Check("Left", l.Left, v => Set(x => x.Left = v)));
                arms.Children.Add(Check("Right", l.Right, v => Set(x => x.Right = v)));
                arms.Children.Add(Check("Round ends", l.RoundEnds, v => Set(x => x.RoundEnds = v)));
                _props.Children.Add(arms);
                _props.Children.Add(Num("Length", l.Length, 0, 60, 1, v => Set(x => x.Length = v), "px", 0, 300));
                _props.Children.Add(Num("Thickness", l.Thickness, 1, 12, 1, v => Set(x => x.Thickness = v), "px", 0.5, 50));
                _props.Children.Add(Num("Gap", l.Gap, -10, 40, 1, v => Set(x => x.Gap = v), "px", -50, 300, "Distance from the centre to where the lines start"));
                break;
            case LayerKind.Dot:
                _props.Children.Add(Sub("Shape"));
                var shapes = new WrapPanel();
                foreach (var sh in new[] { DotShape.Square, DotShape.Round })
                {
                    var s = sh;
                    var rb = Chip(s.ToString(), "xdot", l.DotShape == s);
                    rb.Click += (_, _) => Set(x => x.DotShape = s);
                    shapes.Children.Add(rb);
                }
                _props.Children.Add(shapes);
                _props.Children.Add(Num("Size", l.Size, 1, 20, 1, v => Set(x => x.Size = v), "px", 0.5, 200));
                break;
            case LayerKind.Circle:
                _props.Children.Add(Sub("Shape"));
                _props.Children.Add(Num("Radius", l.Size, 1, 80, 1, v => Set(x => x.Size = v), "px", 0.5, 1000));
                _props.Children.Add(Num("Thickness", l.Thickness, 1, 12, 1, v => Set(x => x.Thickness = v), "px", 0.5, 50));
                break;
            case LayerKind.Image:
                _props.Children.Add(Sub("Image"));
                _props.Children.Add(Ui.Muted(l.ImageFile is null ? "No image: choose a PNG." : CrosshairImages.Exists(l.ImageFile) ? $"PNG, {l.Size:0}×{l.Size * l.ImageAspect:0} px on screen." : "The image file is missing on this PC; choose it again.", 12));
                _props.Children.Add(Ui.Row(Ui.Btn(l.ImageFile is null ? "Choose PNG…" : "Replace PNG…", (_, _) => { if (L is { } x) ImportImage(x); })));
                _props.Children.Add(Num("Width", l.Size, 4, 400, 1, v => Set(x => x.Size = v), "px", 1, 1000, "Height follows the picture's proportions"));
                break;
        }

        if (l.Kind != LayerKind.Image)
        {
            _props.Children.Add(Sub("Colour"));
            _props.Children.Add(ColorPicker("Colour", l.Color, c => Set(x => x.Color = c)));
        }
        _props.Children.Add(Num("Opacity", l.Opacity, 0, 1, 0.05, v => Set(x => x.Opacity = v)));

        if (l.Kind != LayerKind.Image)
        {
            _props.Children.Add(Sub("Outline"));
            _props.Children.Add(Check("Outline", l.Outline, v => Set(x => x.Outline = v), "A border around the shape so it stays visible on any background"));
            _props.Children.Add(Num("Thickness", l.OutlineThickness, 0, 4, 0.5, v => Set(x => x.OutlineThickness = v), "px", 0, 10));
            _props.Children.Add(ColorPicker("Outline colour", l.OutlineColor, c => Set(x => x.OutlineColor = c), ["#D9000000", "#FF000000", "#FFFFFFFF", "#80000000"]));
        }

        _props.Children.Add(Sub("Position"));
        _props.Children.Add(Num("Offset X", l.OffsetX, -100, 100, 1, v => Set(x => x.OffsetX = v), "px", -2000, 2000));
        _props.Children.Add(Num("Offset Y", l.OffsetY, -100, 100, 1, v => Set(x => x.OffsetY = v), "px", -2000, 2000));
        _props.Children.Add(Num("Rotation", l.Rotation, -180, 180, 1, v => Set(x => x.Rotation = v), "°", -360, 360, "Turns this layer around its own centre (e.g. 45° makes an X)"));
        var quick = new WrapPanel { Margin = new Thickness(96, 4, 0, 0) };
        foreach (var deg in new[] { 0, 45, 90 })
        {
            int d = deg;
            quick.Children.Add(Ui.Btn(d + "°", (_, _) => { Set(x => x.Rotation = d); RenderProps(); }, "GhostButton", new Thickness(0, 0, 6, 0)));
        }
        _props.Children.Add(quick);

        _props.Children.Add(Sub("Whole profile"));
        _props.Children.Add(Check("Pixel-perfect (sharp edges)", P.PixelPerfect, v => { P.PixelPerfect = v; Edited(true); }, "Snaps unrotated shapes to whole screen pixels so thin lines don't look blurry"));
    }

    private static string KindHint(LayerKind k) => k switch
    {
        LayerKind.Lines => "Up to four lines around the centre.",
        LayerKind.Dot => "A dot at the centre (or offset).",
        LayerKind.Circle => "A ring around the centre.",
        _ => "Your own transparent PNG.",
    };

    private static TextBlock Sub(string text) => new() { Text = text.ToUpperInvariant(), Foreground = Ui.Brush("Subtle"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };

    private DispatcherOperation? _layersPending;
    private void RenderLayersSoon() => _layersPending ??= Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _layersPending = null; RenderLayers(); });

    // ---- layers ---------------------------------------------------------------------------------------------------

    private void RenderLayers()
    {
        _layers.Children.Clear();
        _layers.Children.Add(Ui.Section("Layers", "Top of the list is drawn in front. Click a layer to edit it."));
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        for (int i = P.Layers.Count - 1; i >= 0; i--)
        {
            var layer = P.Layers[i];
            bool sel = layer.Id == _layerId;
            var row = new DockPanel { LastChildFill = true };
            var eye = new Button
            {
                Content = new TextBlock { Text = layer.Visible ? "\uE890" : "\uED1A", FontFamily = Ui.Res<FontFamily>("Icons"), FontSize = 14 }, Width = 30, Height = 28, Padding = new Thickness(0),
                Style = Ui.Res<Style>("GhostButton"), ToolTip = layer.Visible ? "Hide this layer" : "Show this layer", Foreground = layer.Visible ? Ui.Brush("Text") : Ui.Brush("Subtle"),
            };
            eye.Click += (_, e) => { layer.Visible = !layer.Visible; Edited(true); RenderLayers(); RenderProps(); e.Handled = true; };
            DockPanel.SetDock(eye, Dock.Left);
            row.Children.Add(eye);
            var kind = Ui.Tag(layer.Kind.ToString(), sel ? "Accent" : "Violet");
            kind.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(kind, Dock.Right);
            row.Children.Add(kind);
            var sw = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(0), Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (Argb.TryParse(layer.Color, out var c) && layer.Kind != LayerKind.Image) sw.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            DockPanel.SetDock(sw, Dock.Left);
            row.Children.Add(sw);
            var name = Ui.Text(layer.Name, 13, sel ? FontWeights.SemiBold : FontWeights.Normal);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Opacity = layer.Visible ? 1 : 0.5;
            row.Children.Add(name);
            var b = new Border
            {
                Child = row, Padding = new Thickness(4, 3, 8, 3), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, 3), Cursor = Cursors.Hand,
                Background = sel ? Ui.Brush("AccentSoft") : Brushes.Transparent, BorderBrush = sel ? Ui.Brush("Accent") : Brushes.Transparent, BorderThickness = new Thickness(1),
            };
            b.MouseLeftButtonUp += (_, _) => { _layerId = layer.Id; RenderLayers(); RenderProps(); };
            list.Children.Add(b);
        }
        if (P.Layers.Count == 0) list.Children.Add(Ui.Muted("No layers.", 12));
        _layers.Children.Add(list);

        var add = new WrapPanel();
        add.Children.Add(Ui.Btn("+ Lines", (_, _) => AddLayer(LayerKind.Lines), "BaseButton", new Thickness(0, 0, 6, 6)));
        add.Children.Add(Ui.Btn("+ Dot", (_, _) => AddLayer(LayerKind.Dot), "BaseButton", new Thickness(0, 0, 6, 6)));
        add.Children.Add(Ui.Btn("+ Circle", (_, _) => AddLayer(LayerKind.Circle), "BaseButton", new Thickness(0, 0, 6, 6)));
        add.Children.Add(Ui.Btn("+ Image (PNG)…", (_, _) => AddLayer(LayerKind.Image), "BaseButton", new Thickness(0, 0, 6, 6)));
        _layers.Children.Add(add);
        int idx = P.Layers.FindIndex(x => x.Id == _layerId);
        var ops = new WrapPanel();
        Button Op(string text, Action a, bool enabled, string tip)
        {
            var btn = Ui.Btn(text, (_, _) => a(), "GhostButton", new Thickness(0, 0, 6, 6));
            btn.IsEnabled = enabled;
            btn.ToolTip = tip;
            return btn;
        }
        ops.Children.Add(Op("Duplicate", DuplicateLayer, idx >= 0 && P.Layers.Count < CrosshairSanitizer.MaxLayers, "Copy the selected layer"));
        ops.Children.Add(Op("Move up", () => MoveLayer(1), idx >= 0 && idx < P.Layers.Count - 1, "Draw it in front of the layer above"));
        ops.Children.Add(Op("Move down", () => MoveLayer(-1), idx > 0, "Draw it behind the layer below"));
        ops.Children.Add(Op("Delete", DeleteLayer, idx >= 0, "Delete the selected layer (Undo brings it back)"));
        _layers.Children.Add(ops);
    }

    private void AddLayer(LayerKind kind)
    {
        if (P.Layers.Count >= CrosshairSanitizer.MaxLayers) { MainWindow.Current?.ShowToast($"A profile can have up to {CrosshairSanitizer.MaxLayers} layers.", "Gold"); return; }
        var l = new CrosshairLayer { Kind = kind, Name = CrosshairLayer.DefaultName(kind) };
        if (kind == LayerKind.Dot) { l.Size = 2; l.ReactsToFiring = false; }
        if (kind == LayerKind.Circle) { l.Size = 14; l.Thickness = 1; }
        if (kind == LayerKind.Image)
        {
            l.Outline = false;
            l.ReactsToFiring = false;
            if (!ImportImage(l, addToProfile: true)) return;
            return;
        }
        P.Layers.Add(l);
        _layerId = l.Id;
        Edited(true);
        RenderLayers();
        RenderProps();
    }

    /// <summary>Lets the user pick a PNG, stores a copy and points the layer at it.</summary>
    private bool ImportImage(CrosshairLayer l, bool addToProfile = false)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a crosshair image (transparent PNG)", Filter = "PNG images|*.png" };
        if (dlg.ShowDialog() != true) return false;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > CrosshairImages.MaxBytes) throw new InvalidDataException($"The image is larger than {CrosshairImages.MaxBytes / 1024 / 1024} MB.");
            var name = CrosshairImages.Store(File.ReadAllBytes(dlg.FileName), out var png);
            l.ImageFile = name;
            l.ImageAspect = (double)png.Height / png.Width;
            if (addToProfile || l.Size <= 0.5) l.Size = Math.Min(png.Width, 128);
            l.Name = CrosshairSanitizer.Name(Path.GetFileNameWithoutExtension(dlg.FileName), "Image");
            if (addToProfile) P.Layers.Add(l);
            _layerId = l.Id;
            Edited(true);
            RenderLayers();
            RenderProps();
            MainWindow.Current?.ShowToast(png.HasAlpha
                ? $"Image added ({png.Width}×{png.Height})."
                : $"Image added, but it has no transparency: the whole {png.Width}×{png.Height} rectangle will show. Use a PNG with a transparent background.", png.HasAlpha ? "Success" : "Gold");
            return true;
        }
        catch (Exception ex)
        {
            Dialog.Show("Image not added", ex is InvalidDataException ? ex.Message : "The image couldn't be read: " + ex.Message, DialogKind.Warning);
            return false;
        }
    }

    private void DuplicateLayer()
    {
        var i = P.Layers.FindIndex(x => x.Id == _layerId);
        if (i < 0) return;
        var copy = System.Text.Json.JsonSerializer.Deserialize<CrosshairLayer>(System.Text.Json.JsonSerializer.Serialize(P.Layers[i]))!;
        copy.Id = CrosshairLayer.NewId();
        copy.Name = CrosshairSanitizer.Name(P.Layers[i].Name + " copy", "Layer");
        P.Layers.Insert(i + 1, copy);
        _layerId = copy.Id;
        Edited(true);
        RenderLayers();
        RenderProps();
    }

    private void MoveLayer(int dir)
    {
        var i = P.Layers.FindIndex(x => x.Id == _layerId);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= P.Layers.Count) return;
        (P.Layers[i], P.Layers[j]) = (P.Layers[j], P.Layers[i]);
        Edited(true);
        RenderLayers();
    }

    private void DeleteLayer()
    {
        var i = P.Layers.FindIndex(x => x.Id == _layerId);
        if (i < 0) return;
        P.Layers.RemoveAt(i);
        _layerId = P.Layers.Count == 0 ? null : P.Layers[Math.Min(i, P.Layers.Count - 1)].Id;
        Edited(true);
        RenderLayers();
        RenderProps();
        MainWindow.Current?.ShowToast("Layer deleted. Ctrl+Z (Undo) brings it back.", "Accent");
    }

    // ---- profiles ---------------------------------------------------------------------------------------------------

    private void RenderProfiles()
    {
        _profiles.Children.Clear();
        _profiles.Children.Add(Ui.Section("Profiles", $"{D.Profiles.Count} saved. Click one to use it; the overlay switches instantly."));
        var name = new TextBox { Text = P.Name, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Rename this profile" };
        name.LostKeyboardFocus += (_, _) => Rename(name);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Rename(name); e.Handled = true; } };
        var nameRow = new DockPanel();
        var nl = Ui.Muted("Name", 12.5);
        nl.Width = 50;
        nl.VerticalAlignment = VerticalAlignment.Center;
        nl.Margin = new Thickness(0, 0, 0, 8);
        nameRow.Children.Add(nl);
        nameRow.Children.Add(name);
        _profiles.Children.Add(nameRow);

        var list = new StackPanel();
        foreach (var p in D.Profiles)
        {
            var prof = p;
            bool active = p.Id == D.ActiveId;
            var row = new DockPanel();
            var games = D.Games.Where(g => g.ProfileId == p.Id).Select(g => g.Name.Length > 0 ? g.Name : g.Exe).ToList();
            if (active) { var t = Ui.Tag(CrosshairService.Visible ? "On screen" : "Active", "Success"); DockPanel.SetDock(t, Dock.Right); row.Children.Add(t); }
            if (games.Count > 0) { var t = Ui.Tag(string.Join(", ", games), "Violet"); t.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(t, Dock.Right); row.Children.Add(t); }
            var txt = new StackPanel();
            txt.Children.Add(Ui.Text(p.Name, 13, active ? FontWeights.SemiBold : FontWeights.Normal));
            txt.Children.Add(Ui.Muted($"{p.Layers.Count} layer{(p.Layers.Count == 1 ? "" : "s")}{(p.Firing.Enabled ? " · firing animation" : "")} · edited {p.Modified:g}", 11.5));
            row.Children.Add(txt);
            var b = new Border
            {
                Child = row, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand,
                Background = active ? Ui.Brush("AccentSoft") : Brushes.Transparent, BorderBrush = active ? Ui.Brush("Accent") : Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1),
            };
            b.MouseLeftButtonUp += (_, _) => { if (prof.Id != D.ActiveId) { FlushCommit(); CrosshairService.Activate(prof.Id); _layerId = null; RenderAll(); } };
            list.Children.Add(b);
        }
        _profiles.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var newBtn = Ui.Btn("New ▾", (_, _) => { }, "BaseButton", new Thickness(0, 0, 6, 6));
        var menu = new ContextMenu();
        foreach (var t in CrosshairTemplates.Names)
        {
            var tmpl = t;
            var mi = new MenuItem { Header = tmpl == "Empty" ? "Empty (no layers)" : tmpl };
            mi.Click += (_, _) => AddProfile(CrosshairTemplates.Create(tmpl), tmpl == "Empty" ? "New crosshair" : tmpl);
            menu.Items.Add(mi);
        }
        newBtn.ContextMenu = menu;
        newBtn.Click += (_, _) => { menu.PlacementTarget = newBtn; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        actions.Children.Add(newBtn);
        actions.Children.Add(Ui.Btn("Duplicate", (_, _) => { FlushCommit(); AddProfile(P.Copy(P.Name), P.Name + " copy"); }, "BaseButton", new Thickness(0, 0, 6, 6)));
        var del = Ui.Btn("Delete", (_, _) => DeleteProfile(), "GhostButton", new Thickness(0, 0, 6, 6));
        del.IsEnabled = D.Profiles.Count > 1;
        del.ToolTip = D.Profiles.Count > 1 ? "Delete this profile" : "You need at least one profile";
        actions.Children.Add(del);
        _profiles.Children.Add(actions);

        _profiles.Children.Add(Sub("Share"));
        var share = new WrapPanel();
        share.Children.Add(Ui.Btn("Share to Community Hub", (_, _) => { FlushCommit(); CrosshairService.SaveNow(); CommunityHubView.ShareCrosshair(P.Id); }, "PrimaryButton", new Thickness(0, 0, 6, 6)));
        share.Children.Add(Ui.Btn("Copy share code", (_, _) => CopyCode(), "BaseButton", new Thickness(0, 0, 6, 6)));
        share.Children.Add(Ui.Btn("Export to file…", (_, _) => ExportFile(), "BaseButton", new Thickness(0, 0, 6, 6)));
        share.Children.Add(Ui.Btn("Import file…", (_, _) => ImportFile(), "BaseButton", new Thickness(0, 0, 6, 6)));
        _profiles.Children.Add(share);
        var codeRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var code = new TextBox { ToolTip = "Paste a code that starts with MTX1:", Tag = "Paste a crosshair code (MTX1:…)" };
        var imp = Ui.Btn("Import code", (_, _) => ImportText(code.Text, "code"), "BaseButton", new Thickness(6, 0, 0, 0));
        DockPanel.SetDock(imp, Dock.Right);
        codeRow.Children.Add(imp);
        codeRow.Children.Add(code);
        _profiles.Children.Add(codeRow);
    }

    private void Rename(TextBox box)
    {
        var n = CrosshairSanitizer.Name(box.Text, P.Name);
        if (n == P.Name) { box.Text = n; return; }
        if (D.Profiles.Any(p => p.Id != P.Id && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) n = D.UniqueName(n);
        P.Name = n;
        box.Text = n;
        Edited(true);
        RenderProfiles();
        RenderGames();
        UpdateCaption();
    }

    private void AddProfile(CrosshairProfile p, string name)
    {
        FlushCommit();
        p.Name = D.UniqueName(name);
        D.Profiles.Add(p);
        CrosshairService.Activate(p.Id);
        History.Track(p);
        _layerId = null;
        CrosshairService.SaveSoon();
        RenderAll();
        MainWindow.Current?.ShowToast($"Created \"{p.Name}\".", "Success");
    }

    private void DeleteProfile()
    {
        if (D.Profiles.Count < 2) return;
        var p = P;
        var games = D.Games.Count(g => g.ProfileId == p.Id);
        if (!Dialog.Confirm("Delete crosshair profile?", $"\"{p.Name}\" will be deleted permanently.{(games > 0 ? $" {games} game rule{(games == 1 ? "" : "s")} using it will switch to no profile." : "")} Export it first if you might want it back.", "Delete", DialogKind.Warning)) return;
        int i = D.Profiles.IndexOf(p);
        D.Profiles.Remove(p);
        foreach (var g in D.Games.Where(g => g.ProfileId == p.Id)) g.ProfileId = "";
        History.Forget(p.Id);
        D.ActiveId = "";
        CrosshairService.Activate(D.Profiles[Math.Min(i, D.Profiles.Count - 1)].Id);
        _layerId = null;
        CrosshairService.Edited();
        RenderAll();
    }

    private void CopyCode()
    {
        FlushCommit();
        var notes = new List<string>();
        var code = CrosshairSharing.ToCode(P, notes);
        try { Clipboard.SetText(code); }
        catch (Exception ex) { Dialog.Show("Couldn't copy", "The clipboard is busy: " + ex.Message, DialogKind.Warning); return; }
        MainWindow.Current?.ShowToast($"Code for \"{P.Name}\" copied ({code.Length} characters)." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""), notes.Count > 0 ? "Gold" : "Success");
    }

    private void ExportFile()
    {
        FlushCommit();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export crosshair", Filter = "Money Tweaks crosshair|*.mtcrosshair|JSON|*.json",
            FileName = string.Concat(P.Name.Split(Path.GetInvalidFileNameChars())) + ".mtcrosshair",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var notes = new List<string>();
            AtomicFile.WriteAllText(dlg.FileName, CrosshairSharing.ToFile(P, notes));
            MainWindow.Current?.ShowToast($"Exported to {Path.GetFileName(dlg.FileName)}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""), notes.Count > 0 ? "Gold" : "Success");
        }
        catch (Exception ex) { Dialog.Show("Export failed", ex.Message, DialogKind.Error); }
    }

    private void ImportFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import crosshair", Filter = "Money Tweaks crosshair|*.mtcrosshair;*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > CrosshairSharing.MaxDecodedBytes) throw new InvalidDataException("That file is too large to be a crosshair.");
            ImportText(File.ReadAllText(dlg.FileName), "file");
        }
        catch (Exception ex) { Dialog.Show("Import failed", ex.Message, DialogKind.Warning); }
    }

    private void ImportText(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) { MainWindow.Current?.ShowToast("Paste a crosshair code first.", "Gold"); return; }
        try
        {
            var r = CrosshairSharing.Import(text);
            var p = r.Profile.Copy(r.Profile.Name);
            AddProfile(p, r.Profile.Name);
            if (r.Notes.Count > 0) Dialog.Show("Imported with notes", string.Join("\n", r.Notes), DialogKind.Info);
        }
        catch (InvalidDataException ex) { Dialog.Show($"Import failed", ex.Message, DialogKind.Warning); }
        catch (Exception ex) { Dialog.Show($"Import failed", $"That {what} couldn't be read: {ex.Message}", DialogKind.Warning); }
    }

    // ---- firing animation ---------------------------------------------------------------------------------------

    private void RenderFiring()
    {
        _firing.Children.Clear();
        var f = P.Firing;
        _firing.Children.Add(Ui.Section("Firing animation", "The crosshair spreads (bloom), fades or flashes while you hold the fire button, then recovers. Settings belong to this profile."));
        void Set(Action<FiringAnimation> change, bool rerender = false) { change(P.Firing); Edited(); if (rerender) RenderFiring(); UpdateCaption(); }
        var en = new WrapPanel();
        en.Children.Add(Check("Animate when firing", f.Enabled, v => Set(x => x.Enabled = v, true)));
        en.Children.Add(Check("Hide while aiming (right mouse held)", f.HideWhileAiming, v => Set(x => x.HideWhileAiming = v), "For games that show their own scope/sight when aiming down sights"));
        _firing.Children.Add(en);
        if (f.Enabled)
        {
            var trig = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var tl = Ui.Muted("Fire button", 12.5);
            tl.Width = 96;
            tl.VerticalAlignment = VerticalAlignment.Center;
            trig.Children.Add(tl);
            foreach (var (t, label) in new[] { (FireTrigger.LeftMouse, "Left mouse"), (FireTrigger.RightMouse, "Right mouse"), (FireTrigger.EitherMouse, "Either") })
            {
                var tt = t;
                var rb = Chip(label, "xtrig", f.Trigger == t);
                rb.Click += (_, _) => Set(x => x.Trigger = tt);
                trig.Children.Add(rb);
            }
            _firing.Children.Add(trig);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var a = new StackPanel();
            a.Children.Add(Num("Spread", f.Spread, 0, 30, 1, v => Set(x => x.Spread = v), "px", 0, 100, "How far gaps and circles open at full bloom"));
            a.Children.Add(Num("Rise time", f.RiseMs, 10, 1000, 10, v => Set(x => x.RiseMs = v), "ms", 1, 5000, "How long holding fire takes to reach full spread"));
            a.Children.Add(Num("Recovery", f.RecoverMs, 10, 1500, 10, v => Set(x => x.RecoverMs = v), "ms", 1, 5000, "How long it takes to settle after you release"));
            var b = new StackPanel();
            b.Children.Add(Num("Opacity firing", f.FiringOpacity, 0, 1, 0.05, v => Set(x => x.FiringOpacity = v), "", 0, 1, "Fade the crosshair while firing (1 = no fade)"));
            b.Children.Add(Check("Flash a colour while firing", f.Flash, v => Set(x => x.Flash = v, true)));
            if (f.Flash) b.Children.Add(ColorPicker("Flash colour", f.FlashColor, c => Set(x => x.FlashColor = c)));
            grid.Children.Add(a);
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
            _firing.Children.Add(grid);
        }
        _liveText = Ui.Muted("", 12);
        _liveText.Margin = new Thickness(0, 10, 0, 0);
        _liveText.TextWrapping = TextWrapping.Wrap;
        _firing.Children.Add(_liveText);
        _firing.Children.Add(Ui.Muted("How it's detected: Money Tweaks reads whether the mouse button is held, through Windows (GetAsyncKeyState), only while the crosshair is on screen and this profile uses it. No hooks, nothing injected into the game. It follows the button, not actual shots: it can't know your ammo, fire rate or whether you're in a menu. If it doesn't react in a game that runs as administrator, run Money Tweaks as administrator too.", 11.5));
        StartLiveInput();
    }

    private void StartLiveInput()
    {
        _live.Tick -= LiveTick;
        _live.Tick += LiveTick;
        if (_shown && (P.Firing.NeedsInput)) _live.Start(); else _live.Stop();
        LiveTick(null, EventArgs.Empty);
    }

    private void LiveTick(object? s, EventArgs e)
    {
        if (_liveText is null) return;
        if (!P.Firing.NeedsInput) { _liveText.Text = ""; return; }
        var (l, r) = CrosshairService.MouseButtons();
        var state = $"Mouse right now: left {(l ? "HELD" : "up")}, right {(r ? "HELD" : "up")}.";
        state += CrosshairService.Visible ? $" Overlay bloom {CrosshairService.CurrentBloom:P0}." : " Show the crosshair to see it animate on screen.";
        _liveText.Text = state;
    }

    // ---- overlay (display, position, hotkeys) ---------------------------------------------------------------------

    private void RenderOverlay()
    {
        _overlay.Children.Clear();
        var o = D.Overlay;
        _overlay.Children.Add(Ui.Section("Overlay on your screen", "Where and how the crosshair is drawn. These apply to every profile."));
        _overlayStatus = Ui.Muted("", 12.5);
        _overlayStatus.TextWrapping = TextWrapping.Wrap;
        _overlay.Children.Add(_overlayStatus);
        RenderOverlayStatus();

        void Opt(Action<CrosshairOverlayOptions> change) { change(D.Overlay); CrosshairService.Edited(); RenderPreview(); RenderOverlayStatus(); }

        var screens = Monitors.All();
        var mons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var ml = Ui.Muted("Monitor", 12.5);
        ml.Width = 96;
        ml.VerticalAlignment = VerticalAlignment.Center;
        mons.Children.Add(ml);
        for (int i = 0; i < screens.Count; i++)
        {
            int idx = i;
            var rb = Chip($"{(i == 0 ? "Main" : "Monitor " + (i + 1))} · {screens[i].Width}×{screens[i].Height}", "xmon", Math.Clamp(o.MonitorIndex, 0, screens.Count - 1) == i);
            rb.Click += (_, _) => Opt(x => x.MonitorIndex = idx);
            mons.Children.Add(rb);
        }
        _overlay.Children.Add(mons);
        var flags = new WrapPanel();
        flags.Children.Add(Check("Follow the game's monitor", o.FollowGameMonitor, v => { Opt(x => x.FollowGameMonitor = v); CrosshairService.UpdateWatcher(); }, "Moves to the monitor of a listed game, or of any borderless/fullscreen window in front"));
        flags.Children.Add(Check("Show when Money Tweaks starts", o.ShowOnStart, v => Opt(x => x.ShowOnStart = v)));
        _overlay.Children.Add(flags);

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var a = new StackPanel();
        a.Children.Add(Num("Position X", o.PositionX, -500, 500, 1, v => Opt(x => x.PositionX = v), "px", -4000, 4000, "Moves the crosshair from the monitor's centre (some games aim slightly off-centre)"));
        a.Children.Add(Num("Position Y", o.PositionY, -500, 500, 1, v => Opt(x => x.PositionY = v), "px", -4000, 4000));
        a.Children.Add(Ui.Row(Ui.Btn("Re-centre", (_, _) => { Opt(x => { x.PositionX = 0; x.PositionY = 0; }); RenderOverlay(); }, "GhostButton", new Thickness(96, 6, 0, 0))));
        var b = new StackPanel();
        b.Children.Add(Num("Scale", o.Scale, 0.25, 4, 0.25, v => Opt(x => x.Scale = v), "×", 0.25, 8, "Makes everything bigger or smaller (e.g. for 4K screens)"));
        b.Children.Add(Num("Opacity", o.Opacity, 0.05, 1, 0.05, v => Opt(x => x.Opacity = v), "", 0.05, 1));
        grid.Children.Add(a);
        Grid.SetColumn(b, 2);
        grid.Children.Add(b);
        _overlay.Children.Add(grid);

        _overlay.Children.Add(Sub("Hotkeys (work while a game has focus)"));
        _hotkeyMsgs.Clear();
        foreach (var (key, label) in CrosshairService.Hotkeys)
        {
            var k = key;
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var hl = Ui.Muted(label, 12.5);
            hl.Width = 120;
            hl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(hl);
            var box = new TextBox { Text = CrosshairService.HotkeyText(k), Width = 170, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Click, then press the key combination. Backspace clears it (no hotkey).", IsReadOnlyCaretVisible = false };
            box.PreviewKeyDown += (_, e) => { CaptureHotkey(box, e); };
            row.Children.Add(box);
            row.Children.Add(Ui.Btn("Set", (_, _) => { CrosshairService.SetHotkey(k, box.Text); box.Text = CrosshairService.HotkeyText(k); RenderHotkeyMsgs(); }, "BaseButton", new Thickness(8, 0, 0, 0)));
            var msg = Ui.Muted("", 11.5);
            msg.Margin = new Thickness(10, 0, 0, 0);
            msg.VerticalAlignment = VerticalAlignment.Center;
            msg.TextWrapping = TextWrapping.Wrap;
            msg.MaxWidth = 420;
            _hotkeyMsgs[k] = msg;
            row.Children.Add(msg);
            _overlay.Children.Add(row);
        }
        RenderHotkeyMsgs();
    }

    /// <summary>Records a key combination into the box (Ctrl/Alt/Shift/Win + key) instead of typing text.</summary>
    private static void CaptureHotkey(TextBox box, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab) return;
        e.Handled = true;
        if (key is Key.Back or Key.Delete or Key.Escape) { box.Text = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var name = VirtualKeys.Name(KeyInterop.VirtualKeyFromKey(key));
        if (name is null) return;
        var parts = new List<string>();
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);
        box.Text = string.Join("+", parts);
    }

    private void RenderHotkeyMsgs()
    {
        foreach (var (k, tb) in _hotkeyMsgs) tb.Text = CrosshairService.HotkeyStatus.GetValueOrDefault(k, "Registered when Money Tweaks starts.");
    }

    private void RenderOverlayStatus()
    {
        if (_overlayStatus is null) return;
        var mon = CrosshairService.TargetMonitor();
        var w = CrosshairService.Window;
        _overlayStatus.Text = CrosshairService.Visible && w is not null
            ? $"On screen: \"{P.Name}\", centred at pixel {w.Placement.x + w.Placement.size / 2}, {w.Placement.y + w.Placement.size / 2} on the {mon.w}×{mon.h} monitor. Click-through: your clicks go to the game."
            : $"Hidden. Press \"Show crosshair\" or {(D.Overlay.ToggleHotkey.Length > 0 ? D.Overlay.ToggleHotkey : "set a hotkey")} to draw it on the {mon.w}×{mon.h} monitor.";
        _overlayStatus.Foreground = CrosshairService.Visible ? Ui.Brush("Success") : Ui.Brush("Muted");
        RenderHotkeyMsgs();
        if (_liveText is not null) LiveTick(null, EventArgs.Empty);
    }

    // ---- games ------------------------------------------------------------------------------------------------------

    private void RenderGames()
    {
        _games.Children.Clear();
        _games.Children.Add(Ui.Section("Per-game profiles", "When a listed game is the window in front, its profile is used (and the crosshair can appear by itself). When the game closes, your previous profile comes back."));
        var flags = new WrapPanel();
        flags.Children.Add(Check("Switch automatically", D.AutoSwitch, v => { D.AutoSwitch = v; CrosshairService.Edited(); }));
        flags.Children.Add(Check("Hide while the game isn't in front", D.HideWhenGameUnfocused, v => { D.HideWhenGameUnfocused = v; CrosshairService.SaveSoon(); }, "Only for a crosshair that appeared automatically for the game"));
        _games.Children.Add(flags);
        _gameStatus = Ui.Muted("", 12);
        _gameStatus.TextWrapping = TextWrapping.Wrap;
        _gameStatus.Margin = new Thickness(0, 4, 0, 6);
        _games.Children.Add(_gameStatus);
        RenderGameStatus();

        foreach (var g in D.Games.ToList())
        {
            var game = g;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = Ui.Btn("Remove", (_, _) => { D.Games.Remove(game); CrosshairService.Edited(); RenderGames(); RenderProfiles(); }, "GhostButton");
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var show = new ToggleButton { Style = Ui.Res<Style>("Switch"), IsChecked = game.AutoShow, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show the crosshair automatically for this game", Margin = new Thickness(8, 0, 8, 0) };
            show.Click += (_, _) => { game.AutoShow = show.IsChecked == true; CrosshairService.SaveSoon(); };
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);
            var showLbl = Ui.Muted("Auto-show", 11.5);
            showLbl.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(showLbl, Dock.Right);
            row.Children.Add(showLbl);
            var combo = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            combo.Items.Add(new ComboBoxItem { Content = "(no profile: keep current)", Tag = "" });
            foreach (var p in D.Profiles) combo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
            combo.SelectedIndex = Math.Max(0, D.Profiles.FindIndex(p => p.Id == game.ProfileId) + 1);
            combo.SelectionChanged += (_, _) => { game.ProfileId = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; CrosshairService.SaveSoon(); RenderProfiles(); };
            DockPanel.SetDock(combo, Dock.Right);
            row.Children.Add(combo);
            var text = new StackPanel();
            text.Children.Add(Ui.Text(game.Name.Length > 0 ? game.Name : game.Exe, 13, FontWeights.SemiBold));
            text.Children.Add(Ui.Muted(CrosshairData.NormalizeExe(game.Exe), 11.5));
            row.Children.Add(text);
            _games.Children.Add(row);
        }
        if (D.Games.Count == 0) _games.Children.Add(Ui.Muted("No games yet.", 12));

        var add = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var exeBox = new TextBox { Width = 230, Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Game executable, e.g. FortniteClient-Win64-Shipping.exe", Tag = "Game .exe, e.g. VALORANT.exe" };
        add.Children.Add(exeBox);
        add.Children.Add(Ui.Btn("Add game", (_, _) => AddGame(exeBox.Text, null), "BaseButton", new Thickness(0, 0, 8, 6)));
        var running = new ComboBox { Width = 300, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Apps with an open window" };
        running.Items.Add(new ComboBoxItem { Content = "…or pick an app that's open now", IsEnabled = false });
        running.SelectedIndex = 0;
        running.DropDownOpened += (_, _) =>
        {
            while (running.Items.Count > 1) running.Items.RemoveAt(1);
            try
            {
                foreach (var p in Process.GetProcesses())
                    using (p)
                    {
                        try { if (p.MainWindowHandle != 0 && p.Id != Environment.ProcessId && p.MainWindowTitle.Length > 0) running.Items.Add(new ComboBoxItem { Content = $"{p.ProcessName} — {p.MainWindowTitle}", Tag = p.ProcessName }); }
                        catch { }
                    }
            }
            catch { }
        };
        add.Children.Add(running);
        add.Children.Add(Ui.Btn("Add selected app", (_, _) => { if (running.SelectedItem is ComboBoxItem { Tag: string n } it) AddGame(n, it.Content as string); }, "BaseButton", new Thickness(0, 0, 8, 6)));
        _games.Children.Add(add);
    }

    private void AddGame(string exe, string? label)
    {
        exe = exe.Trim();
        if (exe.Length == 0 || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { MainWindow.Current?.ShowToast("Type the game's .exe name, e.g. VALORANT.exe.", "Gold"); return; }
        if (D.RuleFor(exe) is not null) { MainWindow.Current?.ShowToast("That game is already in the list.", "Gold"); return; }
        D.Games.Add(new CrosshairGameRule { Exe = CrosshairData.NormalizeExe(exe), Name = label?.Split(" — ")[0] ?? Path.GetFileNameWithoutExtension(exe), ProfileId = P.Id, AutoShow = true });
        if (!D.AutoSwitch) D.AutoSwitch = true;
        CrosshairService.Edited();
        RenderGames();
        RenderProfiles();
    }

    private void RenderGameStatus()
    {
        if (_gameStatus is null) return;
        var s = CrosshairService.GameStatus;
        _gameStatus.Text = !D.AutoSwitch ? "Automatic switching is off." :
            s.Length > 0 ? s : D.Games.Count == 0 ? "Add a game below." : "Watching for these games (checks the window in front once a second).";
        _gameStatus.Foreground = s.Contains("exclusive fullscreen") ? Ui.Brush("Gold") : Ui.Brush("Muted");
    }

    // ---- honesty -----------------------------------------------------------------------------------------------------

    private static UIElement Compatibility()
    {
        var sec = Ui.Section("How the overlay works");
        sec.Children.Add(Ui.Muted(
            "• It's a normal transparent, always-on-top, click-through window drawn by Windows. Nothing is injected into games and no game files or memory are touched, so it doesn't interact with anti-cheat.\n" +
            "• Visible over windowed and borderless fullscreen games. True exclusive fullscreen doesn't let other windows draw on top, so the crosshair isn't visible there; switch the game to borderless/windowed fullscreen. Per-game status tells you when a listed game is in exclusive fullscreen.\n" +
            "• Some competitive games or tournaments don't allow crosshair overlays: check the rules of the game or event you play.\n" +
            "• Profiles, layers and images are saved in your Money Tweaks folder (crosshair.json) every time you change something.", 12));
        return sec;
    }
}
