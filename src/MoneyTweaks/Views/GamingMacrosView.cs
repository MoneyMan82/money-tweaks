using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Macros;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

public sealed class GamingMacrosView : UserControl, IPageLifecycle
{
    private MacroFile _file = MacroStore.Load();
    private readonly WrapPanel _filters = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _search = new() { Width = 260, Padding = new Thickness(32, 8, 10, 8) };
    private readonly StackPanel _cards = new();
    private readonly StackPanel _lists = new();
    private readonly Border _editorHost = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Text("No macro running.", 13, FontWeights.SemiBold);
    private readonly TextBlock _statusSub = Ui.Muted("", 12);
    private readonly Button _armBtn, _stopBtn;
    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0) };
    private string _game = "All";
    private DispatcherTimer? _countdown;

    private static MacroManager Mgr => MacroService.Manager;
    private IEnumerable<Macro> All => MacroLibrary.BuiltIns.Concat(_file.Macros);

    public GamingMacrosView()
    {
        _armBtn = Ui.Btn("Arm hotkey triggers", (_, _) => ToggleArm(), "PrimaryButton");
        var emergency = Ui.Btn($"Emergency stop  ({MacroService.EmergencyHotkeyText})", (_, _) => { Mgr.EmergencyStop(); CancelCountdown(); }, "DangerButton");
        var actions = Ui.Row(Ui.Btn("+  New macro", (_, _) => OpenEditor(null), "PrimaryButton"), Ui.Btn("Import…", (_, _) => Import()),
            Ui.Btn("Export mine…", (_, _) => Export(_file.Macros), margin: new Thickness(0, 0, 0, 8)));
        var (root, body) = Ui.Page("Gaming Macros",
            "Timed keyboard and mouse-button sequences with hotkey triggers. They run through standard Windows input (like an on-screen keyboard): no drivers, no game injection, no cursor movement. Meant for single-player games, accessibility, desktop and streaming. Competitive anti-cheat games are blocked.",
            actions, eyebrow: "Gaming tools", icon: "\uE765");

        // Live status / control bar
        _stopBtn = Ui.Btn("Stop", (_, _) => { Mgr.Stop(); CancelCountdown(); });
        var statusText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        statusText.Children.Add(_status);
        statusText.Children.Add(_statusSub);
        var statusRow = new DockPanel();
        var btns = Ui.Row(_stopBtn, emergency, _armBtn);
        btns.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(btns, Dock.Right);
        statusRow.Children.Add(btns);
        statusRow.Children.Add(_dot);
        statusRow.Children.Add(statusText);
        body.Children.Add(Ui.Card(statusRow, new Thickness(0, 0, 14, 16)));
        body.Children.Add(_editorHost);

        // Filters + search
        var searchGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        searchGrid.Children.Add(_search);
        var hint = new TextBlock { Text = "Search macros", Foreground = Ui.Brush("Subtle"), Margin = new Thickness(33, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchGrid.Children.Add(hint);
        searchGrid.Children.Add(new TextBlock { Text = "\uE721", Style = Ui.Res<Style>("Icon"), FontSize = 13, Foreground = Ui.Brush("Muted"), Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false });
        _search.TextChanged += (_, _) => { hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RebuildCards(); };
        _lists.Children.Add(searchGrid);
        _lists.Children.Add(_filters);
        _lists.Children.Add(_cards);
        _lists.Children.Add(Ui.Card(Compatibility(), new Thickness(0, 6, 14, 14)));
        body.Children.Add(_lists);
        Content = root;

        Mgr.Started += m => Dispatcher.BeginInvoke(() => UpdateStatus($"Running: {m.Name}", Mgr.DryRun ? "Test mode: no input is sent." : m.TriggerText));
        Mgr.Step += (m, i) => Dispatcher.BeginInvoke(() => { if (Mgr.Running == m && i < m.Steps.Count) _statusSub.Text = $"{(Mgr.DryRun ? "Test · " : "")}Step {i + 1}/{m.Steps.Count}: {m.Steps[i].Describe()}"; });
        Mgr.Stopped += (m, r, d) => Dispatcher.BeginInvoke(() => OnStopped(m, r, d));
        Rebuild();
        UpdateStatus(null, null);
    }

    // ---- status ---------------------------------------------------------------------------------------------
    private void UpdateStatus(string? title, string? sub)
    {
        bool running = Mgr.Running is not null;
        _status.Text = title ?? (running ? $"Running: {Mgr.Running!.Name}" : "No macro running");
        _statusSub.Text = sub ?? (MacroService.Armed ? "Hotkey triggers are armed: your macros start from their trigger keys." : "Hotkey triggers are off. Use Play, or arm triggers to use trigger keys.");
        _dot.Fill = Ui.Brush(running ? "Success" : MacroService.Armed ? "Accent" : "Subtle");
        _stopBtn.IsEnabled = running || _countdown is not null;
        _armBtn.Content = MacroService.Armed ? "Disarm triggers" : "Arm hotkey triggers";
    }

    private void OnStopped(Macro m, StopReason r, string? detail)
    {
        var (msg, tone) = r switch
        {
            StopReason.Finished => ($"“{m.Name}” finished.", "Success"),
            StopReason.TriggerReleased => ($"“{m.Name}” stopped (trigger released).", "Accent"),
            StopReason.Emergency => ($"Emergency stop: “{m.Name}” stopped and all keys were released.", "Gold"),
            StopReason.Timeout => ($"“{m.Name}” hit its safety timeout ({m.SafetyTimeoutSec} s) and stopped.", "Gold"),
            StopReason.BlockedGame => ($"Macros are blocked in {detail}: its rules/anti-cheat don't allow automated input. Nothing was sent.", "Danger"),
            StopReason.Error => ($"“{m.Name}” stopped: {detail}", "Danger"),
            _ => ($"“{m.Name}” stopped.", "Accent"),
        };
        MainWindow.Current?.ShowToast(msg, tone);
        UpdateStatus(null, null);
    }

    private void ToggleArm()
    {
        if (MacroService.Armed) MacroService.Disarm();
        else
        {
            var triggered = All.Where(m => m.Trigger != TriggerKind.Manual).ToList();
            var clashes = triggered.GroupBy(m => m.TriggerKey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            if (clashes.Count > 0 && !Dialog.Confirm("Some macros share a trigger key",
                    string.Join("\n", clashes.Select(c => $"{c.Key}: {string.Join(", ", c.Select(x => x.Name))}")) + "\n\nOnly the first macro listed for each key will run. Change trigger keys in the editor to avoid this.", "Arm anyway", DialogKind.Warning))
                return;
            try { MacroService.Arm(triggered); }
            catch (Exception ex) { Dialog.Show("Couldn't arm triggers", ex.Message, DialogKind.Error); }
        }
        UpdateStatus(null, null);
    }

    // ---- cards ----------------------------------------------------------------------------------------------
    private void Rebuild()
    {
        _filters.Children.Clear();
        foreach (var g in new[] { "All" }.Concat(All.Select(m => m.Game).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)))
        {
            var rb = new RadioButton { Content = g, Style = Ui.Res<Style>("Chip"), GroupName = "mgame", IsChecked = g == _game, Margin = new Thickness(0, 0, 6, 6) };
            var game = g;
            rb.Checked += (_, _) => { _game = game; RebuildCards(); };
            _filters.Children.Add(rb);
        }
        RebuildCards();
        if (MacroService.Armed) MacroService.Arm(All.Where(m => m.Trigger != TriggerKind.Manual).ToList());
    }

    private void RebuildCards()
    {
        _cards.Children.Clear();
        var q = _search.Text.Trim();
        var shown = All.Where(m => _game == "All" || m.Game.Equals(_game, StringComparison.OrdinalIgnoreCase))
            .Where(m => q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Category.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Game.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (shown.Count == 0) { _cards.Children.Add(Ui.Card(Ui.Empty("\uE721", "No macros match", "Try another game filter or search term, or create a new macro."), new Thickness(0, 0, 14, 14))); return; }
        var grid = (ResponsiveGrid)Ui.Grid(2, shown.Select(Card).ToArray());
        grid.MinItemWidth = 360;
        _cards.Children.Add(grid);
    }

    private UIElement Card(Macro m)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var kind = Ui.Tag(m.BuiltIn ? "Built-in" : "Custom", m.BuiltIn ? "Violet" : "Accent");
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(kind);
        head.Children.Add(Ui.Text(m.Name, 15, FontWeights.SemiBold));
        sp.Children.Add(head);
        var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        tags.Children.Add(Ui.Tag(m.Game, "Muted"));
        tags.Children.Add(Ui.Tag(m.Category, "Muted"));
        tags.Children.Add(Ui.Tag(m.TriggerText, m.Trigger == TriggerKind.Manual ? "Muted" : "Accent"));
        tags.Children.Add(Ui.Tag($"{m.Steps.Count} steps · {(m.Repeat == 0 ? "loops" : m.Repeat == 1 ? "once" : $"×{m.Repeat}")}", "Muted"));
        sp.Children.Add(tags);
        sp.Children.Add(Ui.Muted(string.IsNullOrWhiteSpace(m.Description) ? "No description." : m.Description, 12.5));
        var preview = Ui.Muted(string.Join("  →  ", m.Steps.Take(6).Select(s => s.Describe())) + (m.Steps.Count > 6 ? "  …" : ""), 11.5);
        preview.Margin = new Thickness(0, 8, 0, 0);
        preview.FontFamily = Ui.Res<FontFamily>("MonoFont");
        sp.Children.Add(preview);
        var row = Ui.Row(Ui.Btn("Play", (_, _) => Play(m), "PrimaryButton"), Ui.Btn("Test", (_, _) => Test(m)), Ui.Btn("Duplicate", (_, _) => Duplicate(m), "GhostButton"));
        row.Margin = new Thickness(0, 14, 0, 0);
        if (!m.BuiltIn)
        {
            row.Children.Add(Ui.Btn("Edit / rename", (_, _) => OpenEditor(m), "GhostButton"));
            row.Children.Add(Ui.Btn("Export", (_, _) => Export([m]), "GhostButton"));
            row.Children.Add(Ui.Btn("Delete", (_, _) => Delete(m), "GhostButton"));
        }
        sp.Children.Add(row);
        return Ui.Card(sp, new Thickness(0, 0, 14, 14));
    }

    // ---- run --------------------------------------------------------------------------------------------------
    /// <summary>Play: 3-second countdown so you can switch to the target window, then real input.</summary>
    private void Play(Macro m)
    {
        if (Mgr.Running is not null || _countdown is not null) { MainWindow.Current?.ShowToast("A macro is already running. Stop it first.", "Gold"); return; }
        if (m.Repeat == 0 && m.Trigger == TriggerKind.Manual)
            MainWindow.Current?.ShowToast($"This macro loops: stop it with Stop or {MacroService.EmergencyHotkeyText}.", "Gold");
        int left = 3;
        UpdateStatus($"Starting “{m.Name}” in {left}…", "Switch to the window that should receive the input.");
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (--left > 0) { _status.Text = $"Starting “{m.Name}” in {left}…"; return; }
            CancelCountdown();
            Mgr.Start(m);
            UpdateStatus(null, null);
        };
        _countdown.Start();
        _stopBtn.IsEnabled = true;
    }

    private void CancelCountdown()
    {
        if (_countdown is null) return;
        _countdown.Stop();
        _countdown = null;
        UpdateStatus(null, null);
    }

    /// <summary>Test: walks the timeline with real timing but sends no input.</summary>
    private void Test(Macro m)
    {
        if (Mgr.Running is not null) { MainWindow.Current?.ShowToast("A macro is already running. Stop it first.", "Gold"); return; }
        var copy = m.Copy(m.Name);
        if (copy.Repeat == 0) copy.Repeat = 1; // a test never loops forever
        copy.SafetyTimeoutSec = Math.Min(copy.SafetyTimeoutSec, 60);
        Mgr.Start(copy, dryRun: true);
    }

    // ---- manage -----------------------------------------------------------------------------------------------
    private void SaveFile()
    {
        try { MacroStore.Save(_file); }
        catch (Exception ex) { Log.Error("Saving macros failed", ex); Dialog.Show("Couldn't save macros", ex.Message, DialogKind.Error); }
    }

    private void Duplicate(Macro m)
    {
        var name = m.Name + " (copy)";
        for (int i = 2; All.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{m.Name} (copy {i})";
        var copy = m.Copy(name);
        _file.Macros.Add(copy);
        SaveFile();
        Rebuild();
        OpenEditor(copy);
    }

    private void Delete(Macro m)
    {
        if (!Dialog.Confirm($"Delete “{m.Name}”?", "The macro is removed from this PC. This can't be undone (export it first if you want a copy).", "Delete", DialogKind.Warning)) return;
        if (Mgr.Running?.Id == m.Id) Mgr.Stop();
        _file.Macros.RemoveAll(x => x.Id == m.Id);
        SaveFile();
        Rebuild();
    }

    private void Export(IEnumerable<Macro> macros)
    {
        var list = macros.ToList();
        if (list.Count == 0) { MainWindow.Current?.ShowToast("There are no custom macros to export yet."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Money Tweaks macros|*.mtmacros.json|JSON|*.json", FileName = list.Count == 1 ? string.Concat(list[0].Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)) + ".mtmacros.json" : "my-macros.mtmacros.json" };
        if (dlg.ShowDialog() != true) return;
        try { MacroStore.Export(list, dlg.FileName); MainWindow.Current?.ShowToast($"Exported {list.Count} macro(s).", "Success"); }
        catch (Exception ex) { Dialog.Show("Export failed", ex.Message, DialogKind.Error); }
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Money Tweaks macros|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        var (macros, problems) = MacroStore.Import(dlg.FileName, All);
        if (macros.Count == 0) { Dialog.Show("Nothing imported", string.Join("\n", problems.DefaultIfEmpty("The file had no macros.")), DialogKind.Warning); return; }
        _file.Macros.AddRange(macros);
        SaveFile();
        Rebuild();
        var msg = $"Imported: {string.Join(", ", macros.Select(m => m.Name))}.";
        if (problems.Count > 0) Dialog.Show("Imported with notes", msg + "\n\n" + string.Join("\n", problems), DialogKind.Warning);
        else MainWindow.Current?.ShowToast(msg, "Success");
    }

    private static UIElement Compatibility()
    {
        var sp = Ui.Section("Compatibility & fair play", "Read this before using macros in a game.");
        sp.Children.Add(Ui.Muted(
            "• Macros send input the same way Windows' on-screen keyboard does. Some games read the hardware directly and ignore it, so a macro may do nothing in a particular game.\n" +
            "• If a game runs as administrator, Money Tweaks must run as administrator too, or Windows blocks the input.\n" +
            "• Anti-cheat can see that input is automated. Macros never run while one of these games is focused: " +
            string.Join(", ", MacroPolicy.Blocked.Select(b => b.game).Distinct()) + ".\n" +
            "• Many online games and servers forbid macros, AFK farming or automation (check the rules first). Don't use macros for rapid-fire, recoil control or anything that gives an unfair advantage.\n" +
            "• Timing is accurate to roughly ±15 ms (Windows timer resolution).\n" +
            $"• Every held key/button is released when a macro stops, errors, times out, or on emergency stop ({MacroService.EmergencyHotkeyText}).", 12.5));
        return sp;
    }

    // ---- editor -----------------------------------------------------------------------------------------------
    private void OpenEditor(Macro? existing)
    {
        var m = existing?.Copy(existing.Name) ?? new Macro { Name = "", Steps = [new() { Kind = StepKind.KeyPress, Key = "E", Ms = 30 }] };
        if (existing is not null) m.Id = existing.Id;
        var name = new TextBox { Text = m.Name, MaxLength = 60 };
        var desc = new TextBox { Text = m.Description, MaxLength = 400, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 50 };
        var game = new TextBox { Text = m.Game, MaxLength = 40, Width = 200 };
        var cat = new TextBox { Text = m.Category, MaxLength = 40, Width = 200 };
        var repeat = NumberBox(m.Repeat, 90);
        var timeout = NumberBox(m.SafetyTimeoutSec, 90);
        var trigKey = KeyBox(m.TriggerKey, combo: false, k => m.TriggerKey = k);
        var errors = new TextBlock { Foreground = Ui.Brush("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var timeline = new StackPanel();

        void RenderTimeline()
        {
            timeline.Children.Clear();
            if (m.Steps.Count == 0) timeline.Children.Add(Ui.Muted("No steps yet. Add some with the buttons below.", 12.5));
            for (int i = 0; i < m.Steps.Count; i++) timeline.Children.Add(StepRow(m, i, RenderTimeline));
        }
        RenderTimeline();

        var trig = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, kind) in new[] { ("Run button", TriggerKind.Manual), ("Press key", TriggerKind.PressKey), ("Hold key", TriggerKind.HoldKey), ("Toggle key", TriggerKind.ToggleKey) })
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "trig" + m.Id, IsChecked = m.Trigger == kind, Margin = new Thickness(0, 0, 6, 6), VerticalAlignment = VerticalAlignment.Center };
            rb.Checked += (_, _) => { m.Trigger = kind; trigKey.IsEnabled = kind != TriggerKind.Manual; };
            trig.Children.Add(rb);
        }
        trigKey.IsEnabled = m.Trigger != TriggerKind.Manual;
        trig.Children.Add(trigKey);

        var add = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, kind) in new[] { ("Key press", StepKind.KeyPress), ("Key down", StepKind.KeyDown), ("Key up", StepKind.KeyUp), ("Combination", StepKind.Combo),
                     ("Mouse click", StepKind.MouseClick), ("Mouse down", StepKind.MouseDown), ("Mouse up", StepKind.MouseUp), ("Delay", StepKind.Delay),
                     ("Wait for key press", StepKind.WaitKeyDown), ("Wait for key release", StepKind.WaitKeyUp) })
            add.Children.Add(Ui.Btn("+ " + label, (_, _) =>
            {
                m.Steps.Add(new MacroStep { Kind = kind, Ms = kind == StepKind.Delay ? 500 : kind is StepKind.WaitKeyDown or StepKind.WaitKeyUp ? 0 : 30, Key = kind == StepKind.Combo ? "LCtrl+C" : kind is StepKind.KeyPress or StepKind.KeyDown or StepKind.KeyUp or StepKind.WaitKeyDown or StepKind.WaitKeyUp ? "E" : "" });
                RenderTimeline();
            }, "GhostButton", new Thickness(0, 0, 4, 4)));

        var sp = new StackPanel();
        sp.Children.Add(Ui.Section(existing is null ? "New macro" : $"Edit “{existing.Name}”", "Build the timeline step by step. Click a key box and press the key you want."));
        sp.Children.Add(Labeled("Name", name));
        sp.Children.Add(Labeled("Description", desc));
        var meta = Ui.Row(Labeled("Game", game), Labeled("Category", cat), Labeled("Repeat (0 = loop until stopped)", repeat), Labeled("Safety timeout (s)", timeout));
        foreach (FrameworkElement c in meta.Children) c.Margin = new Thickness(0, 0, 16, 0);
        sp.Children.Add(meta);
        sp.Children.Add(Labeled("Trigger", trig));
        sp.Children.Add(new TextBlock { Text = "Timeline", Style = Ui.Res<Style>("H2"), Margin = new Thickness(0, 16, 0, 8) });
        sp.Children.Add(timeline);
        sp.Children.Add(add);
        sp.Children.Add(errors);
        sp.Children.Add(Ui.Row(
            Ui.Btn("Save macro", (_, _) =>
            {
                m.Name = name.Text.Trim(); m.Description = desc.Text.Trim(); m.Game = string.IsNullOrWhiteSpace(game.Text) ? "General" : game.Text.Trim();
                m.Category = string.IsNullOrWhiteSpace(cat.Text) ? "Utility" : cat.Text.Trim();
                if (!int.TryParse(repeat.Text, out var r)) { errors.Text = "Repeat must be a whole number."; return; }
                if (!int.TryParse(timeout.Text, out var t)) { errors.Text = "Safety timeout must be a whole number."; return; }
                m.Repeat = r; m.SafetyTimeoutSec = t;
                var problems = MacroPolicy.Validate(m, All);
                if (problems.Count > 0) { errors.Text = string.Join("\n", problems); return; }
                m.Modified = DateTime.Now;
                var idx = _file.Macros.FindIndex(x => x.Id == m.Id);
                if (idx >= 0) _file.Macros[idx] = m; else _file.Macros.Add(m);
                SaveFile();
                CloseEditor();
                Rebuild();
                MainWindow.Current?.ShowToast($"Saved “{m.Name}”.", "Success");
            }, "PrimaryButton"),
            Ui.Btn("Test timeline", (_, _) => Test(m)),
            Ui.Btn("Cancel", (_, _) => CloseEditor())));
        _editorHost.Child = Ui.Card(sp, new Thickness(0, 0, 14, 18));
        _editorHost.Visibility = Visibility.Visible;
        _lists.Visibility = Visibility.Collapsed;
        name.Focus();
    }

    private UIElement StepRow(Macro m, int i, Action rerender)
    {
        var s = m.Steps[i];
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        Button Tool(string glyph, string tip, Action a, bool enabled = true)
        {
            var b = new Button { Style = Ui.Res<Style>("GhostButton"), ToolTip = tip, IsEnabled = enabled, Padding = new Thickness(8, 5, 8, 5),
                Content = new TextBlock { Text = glyph, Style = Ui.Res<Style>("Icon"), FontSize = 12 } };
            b.Click += (_, _) => { a(); rerender(); };
            return b;
        }
        tools.Children.Add(Tool("\uE70E", "Move up", () => (m.Steps[i - 1], m.Steps[i]) = (m.Steps[i], m.Steps[i - 1]), i > 0));
        tools.Children.Add(Tool("\uE70D", "Move down", () => (m.Steps[i + 1], m.Steps[i]) = (m.Steps[i], m.Steps[i + 1]), i < m.Steps.Count - 1));
        tools.Children.Add(Tool("\uE8C8", "Duplicate step", () => m.Steps.Insert(i + 1, s.Clone()), m.Steps.Count < MacroPolicy.MaxSteps));
        tools.Children.Add(Tool("\uE74D", "Remove step", () => m.Steps.RemoveAt(i)));
        DockPanel.SetDock(tools, Dock.Right);
        row.Children.Add(tools);

        var num = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Ui.Brush("AccentSoft"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        row.Children.Add(num);
        var p = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Text(s.Kind switch
        {
            StepKind.KeyPress => "Key press", StepKind.KeyDown => "Key down", StepKind.KeyUp => "Key up", StepKind.Combo => "Combination",
            StepKind.MouseClick => "Mouse click", StepKind.MouseDown => "Mouse down", StepKind.MouseUp => "Mouse up", StepKind.Delay => "Delay",
            StepKind.WaitKeyDown => "Wait for press", _ => "Wait for release",
        }, 13, FontWeights.SemiBold);
        label.Width = 120; label.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(label);
        if (s.Kind is StepKind.KeyPress or StepKind.KeyDown or StepKind.KeyUp or StepKind.WaitKeyDown or StepKind.WaitKeyUp or StepKind.Combo)
            p.Children.Add(KeyBox(s.Key, s.Kind == StepKind.Combo, k => s.Key = k));
        if (s.Kind is StepKind.MouseClick or StepKind.MouseDown or StepKind.MouseUp)
            foreach (var b in Enum.GetValues<MouseBtn>())
            {
                var rb = new RadioButton { Content = b.ToString(), Style = Ui.Res<Style>("Chip"), GroupName = $"btn{m.Id}{i}", IsChecked = s.Button == b, Margin = new Thickness(0, 0, 6, 0) };
                rb.Checked += (_, _) => s.Button = b;
                p.Children.Add(rb);
            }
        if (s.Kind is not (StepKind.KeyDown or StepKind.KeyUp or StepKind.MouseDown or StepKind.MouseUp))
        {
            var ms = NumberBox(s.Ms, 80);
            ms.Margin = new Thickness(10, 0, 6, 0);
            ms.TextChanged += (_, _) => { if (int.TryParse(ms.Text, out var v)) s.Ms = v; };
            p.Children.Add(ms);
            p.Children.Add(new TextBlock
            {
                Text = s.Kind switch { StepKind.Delay => "ms", StepKind.WaitKeyDown or StepKind.WaitKeyUp => "ms max (0 = no limit)", _ => "ms hold" },
                Foreground = Ui.Brush("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            });
        }
        row.Children.Add(p);
        return new Border { Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Child = row };
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(Ui.Text(label, 12.5, FontWeights.SemiBold));
        sp.Children.Add(control);
        return sp;
    }

    private static TextBox NumberBox(int value, double width)
    {
        var tb = new TextBox { Text = value.ToString(), Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        return tb;
    }

    /// <summary>Click, then press a key (or hold several for a combination). Shows the captured key names.</summary>
    private static TextBox KeyBox(string initial, bool combo, Action<string> set)
    {
        var tb = new TextBox { Text = string.IsNullOrEmpty(initial) ? "Click, then press a key" : initial, IsReadOnly = true, Width = combo ? 200 : 150, Cursor = Cursors.Hand, ToolTip = combo ? "Hold the keys together, then release" : "Press the key to use" };
        var held = new List<string>();
        tb.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            var n = VirtualKeys.Name(vk);
            if (n is null) { tb.Text = "Unsupported key"; return; }
            if (!combo) { tb.Text = n; set(n); return; }
            if (held.Count == 0 || !held.Contains(n, StringComparer.OrdinalIgnoreCase)) { if (held.Count < 5) held.Add(n); }
            tb.Text = string.Join("+", held);
            set(tb.Text);
        };
        tb.PreviewKeyUp += (_, e) => { e.Handled = true; if (combo && Keyboard.Modifiers == ModifierKeys.None) held.Clear(); };
        tb.GotKeyboardFocus += (_, _) => { held.Clear(); tb.SelectAll(); };
        return tb;
    }

    private void CloseEditor()
    {
        _editorHost.Child = null;
        _editorHost.Visibility = Visibility.Collapsed;
        _lists.Visibility = Visibility.Visible;
    }

    internal void ShowEditorForScreenshot() => OpenEditor(MacroLibrary.BuiltIns.First(b => b.Id == "builtin.hold-sequence").Copy("Example: hold Shift sequence"));
    internal void CloseEditorForScreenshot() => CloseEditor();

    public void OnShown() { if (_editorHost.Visibility != Visibility.Visible) { _file = MacroStore.Load(); Rebuild(); } UpdateStatus(null, null); }
    public void OnHidden() { }
}
