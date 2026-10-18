using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>Gaming Profile Manager: one-click switching, auto-switch on game launch, restore on exit.</summary>
public sealed class ProfilesView : UserControl, IPageLifecycle
{
    private readonly StackPanel _active = new();
    private readonly StackPanel _cards = new();
    private readonly StackPanel _editor = new() { Visibility = Visibility.Collapsed };
    private static ProfileFile F => ProfileService.File;

    public ProfilesView()
    {
        var actions = Ui.Row(
            Ui.Btn("+  New profile", (_, _) => OpenEditor(null), "PrimaryButton"),
            Ui.Btn("Import…", (_, _) => Import()),
            Ui.Btn("Game Library ›", (_, _) => MainWindow.Current?.Navigate("library")),
            Ui.Btn("Export my profiles…", (_, _) => Export(F.Profiles), margin: new Thickness(0, 0, 0, 8)));
        var (root, body) = Ui.Page("Gaming Profiles",
            "Switch a whole set of settings in one click, or let Money Tweaks switch automatically when a game starts and restore everything when it closes. Every change is shown first, backed up, and read back after restoring.",
            actions, "Profiles · Auto-switch · Restore", "\uE7FC");
        Content = root;
        body.Children.Add(_active);
        body.Children.Add(_editor);
        body.Children.Add(_cards);
        body.Children.Add(Ui.Muted("Profiles only use instant, reversible tweaks (no restart needed) and never touch security settings. Automatic switching works while Money Tweaks is running. Profiles aim for consistency; measure any FPS change with Test & Benchmark.", 11.5));
        ProfileService.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        Rebuild();
    }

    public void OnShown() => Rebuild();
    public void OnHidden() { }

    private void Rebuild()
    {
        _active.Children.Clear();
        var a = ProfileService.Active;
        var sp = new DockPanel();
        if (a is not null)
        {
            var off = Ui.Btn("Switch off & restore", async (_, _) => await ProfileService.DeactivateAsync(), "GoldButton");
            DockPanel.SetDock(off, Dock.Right);
            sp.Children.Add(off);
        }
        var txt = new StackPanel();
        txt.Children.Add(new TextBlock { Text = "ACTIVE PROFILE", Style = Ui.Res<Style>("Eyebrow") });
        txt.Children.Add(Ui.Text(a is null ? "None. Your normal Windows settings are in use." : a.ProfileName, 16, FontWeights.SemiBold));
        if (a is not null) txt.Children.Add(Ui.Muted($"Since {a.StartedAt:g}{(a.TriggeredBy is null ? " (switched on by you)" : $" (auto: {a.TriggeredBy} started; restores when it closes)")} · {a.OriginalTweakStates.Count(kv => !kv.Value)} tweak(s) changed" +
                                                   (a.PreviousPowerScheme is null ? "" : " · power plan switched") + (a.TimerMs is { } t ? $" · timer {t:0.0} ms" : ""), 12));
        sp.Children.Add(txt);
        var card = Ui.Card(sp);
        if (a is not null) card.BorderBrush = Ui.Brush("Success");
        _active.Children.Add(card);

        _cards.Children.Clear();
        var grid = (ResponsiveGrid)Ui.Grid(2, ProfileStore.All(F).Select(Card).ToArray());
        grid.MinItemWidth = 380;
        _cards.Children.Add(grid);
    }

    private UIElement Card(GamingProfile p)
    {
        bool isActive = ProfileService.Active?.ProfileId == p.Id;
        var sp = new StackPanel();
        var head = new DockPanel();
        var kind = Ui.Tag(isActive ? "ACTIVE" : p.BuiltIn ? "Built-in" : "Custom", isActive ? "Success" : p.BuiltIn ? "Violet" : "Accent");
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(kind);
        head.Children.Add(Ui.Text(p.Name, 15, FontWeights.SemiBold));
        sp.Children.Add(head);
        var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        tags.Children.Add(Ui.Tag($"{p.TweakIds.Count} tweaks", "Muted"));
        if (p.Power != PowerChoice.Keep) tags.Children.Add(Ui.Tag(p.Power == PowerChoice.HighPerformance ? "High performance power" : "Balanced power", "Muted"));
        if (p.TimerResolutionMs is { } ms) tags.Children.Add(Ui.Tag($"Timer {ms:0.0} ms", "Muted"));
        if (p.RaiseGamePriority) tags.Children.Add(Ui.Tag("Game priority", "Muted"));
        if (F.AutoSwitch.Contains(p.Id)) tags.Children.Add(Ui.Tag("Auto-switch on", "Gold"));
        var conflicts = ProfileEngine.Conflicts(p).Count(c => !c.StartsWith("Info", StringComparison.Ordinal));
        if (conflicts > 0) tags.Children.Add(Ui.Tag($"{conflicts} conflict(s)", "Gold"));
        sp.Children.Add(tags);
        sp.Children.Add(Ui.Muted(string.IsNullOrWhiteSpace(p.Description) ? "No description." : p.Description, 12.5));
        if (p.GameExes.Count > 0) { var g = Ui.Muted("Games: " + string.Join(", ", p.GameExes), 11.5); g.Margin = new Thickness(0, 6, 0, 0); sp.Children.Add(g); }

        var row = Ui.Row(isActive
            ? Ui.Btn("Switch off", async (_, _) => await ProfileService.DeactivateAsync(), "GoldButton")
            : Ui.Btn("Activate", async (_, _) => await ProfileService.ActivateAsync(p, null, confirm: true), "PrimaryButton"));
        row.Margin = new Thickness(0, 12, 0, 0);
        row.Children.Add(Ui.Btn("Preview", async (_, _) => Dialog.Show($"Preview: {p.Name}", await Task.Run(() => ProfileService.Describe(p)) + "\n\nNothing has been changed."), "BaseButton"));
        if (p.GameExes.Count > 0)
        {
            var auto = new CheckBox { Content = "Auto-switch", IsChecked = F.AutoSwitch.Contains(p.Id), Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 8), ToolTip = $"Activate when {string.Join(" / ", p.GameExes)} starts, restore when it closes" };
            auto.Click += async (_, _) => await ToggleAuto(p, auto);
            row.Children.Add(auto);
        }
        row.Children.Add(Ui.Btn("Duplicate", (_, _) => Duplicate(p), "GhostButton"));
        if (!p.BuiltIn)
        {
            row.Children.Add(Ui.Btn("Edit / rename", (_, _) => OpenEditor(p), "GhostButton"));
            row.Children.Add(Ui.Btn("Export", (_, _) => Export([p]), "GhostButton"));
            row.Children.Add(Ui.Btn("Delete", (_, _) => Delete(p), "GhostButton"));
        }
        sp.Children.Add(row);
        var card = Ui.Card(sp, new Thickness(0, 0, 14, 14));
        if (isActive) card.BorderBrush = Ui.Brush("Success");
        return card;
    }

    private async Task ToggleAuto(GamingProfile p, CheckBox box)
    {
        if (box.IsChecked == true)
        {
            var text = await Task.Run(() => ProfileService.Describe(p));
            if (!Dialog.Confirm($"Auto-switch “{p.Name}”?",
                    $"When {string.Join(" or ", p.GameExes)} starts, Money Tweaks will apply this profile without asking again, and restore everything when the game closes.\n\n{text}",
                    "Enable auto-switch", DialogKind.Warning)) { box.IsChecked = false; return; }
            if (!Admin.IsElevated && await Task.Run(() => ProfileEngine.NeedsAdmin(p)))
                Dialog.Show("Runs as admin only", "This profile changes system-wide settings, so automatic switching only works while Money Tweaks runs as administrator. You'll get a notice when the game starts otherwise.", DialogKind.Warning);
            F.AutoSwitch.Add(p.Id);
        }
        else F.AutoSwitch.Remove(p.Id);
        ProfileService.Save();
        if (F.AutoSwitch.Count > 0) ProfileService.StartWatcher(); else ProfileService.StopWatcher();
        Rebuild();
    }

    private void Duplicate(GamingProfile p)
    {
        var name = p.Name + " (copy)";
        for (int i = 2; ProfileStore.All(F).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{p.Name} (copy {i})";
        var copy = p.Copy(name);
        F.Profiles.Add(copy);
        ProfileService.Save();
        Rebuild();
        OpenEditor(copy);
    }

    private void Delete(GamingProfile p)
    {
        if (ProfileService.Active?.ProfileId == p.Id) { Dialog.Show("Profile is active", "Switch it off first so its settings are restored."); return; }
        if (!Dialog.Confirm($"Delete “{p.Name}”?", "The profile is removed. Your Windows settings are not changed.", "Delete", DialogKind.Warning)) return;
        F.Profiles.Remove(p);
        F.AutoSwitch.Remove(p.Id);
        ProfileService.Save();
        Rebuild();
    }

    private void Export(IReadOnlyList<GamingProfile> list)
    {
        if (list.Count == 0) { MainWindow.Current?.ShowToast("No custom profiles to export yet. Duplicate a built-in one to start.", "Gold"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Money Tweaks profiles|*.mtprofiles.json|JSON|*.json", FileName = list.Count == 1 ? string.Concat(list[0].Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)) + ".mtprofiles.json" : "my-profiles.mtprofiles.json" };
        if (dlg.ShowDialog() != true) return;
        try { ProfileStore.Export(list, dlg.FileName); MainWindow.Current?.ShowToast($"Exported {list.Count} profile(s).", "Success"); }
        catch (Exception ex) { Dialog.Show("Export failed", ex.Message, DialogKind.Error); }
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Money Tweaks profiles|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (added, problems) = ProfileStore.Import(dlg.FileName, ProfileStore.All(F));
            F.Profiles.AddRange(added);
            ProfileService.Save();
            Rebuild();
            Dialog.Show("Import finished", $"Imported {added.Count} profile(s). Auto-switch stays off for imported profiles until you turn it on." + (problems.Count > 0 ? "\n\n" + string.Join("\n", problems) : ""), problems.Count > 0 ? DialogKind.Warning : DialogKind.Info);
        }
        catch (Exception ex) { Dialog.Show("Import failed", ex.Message, DialogKind.Error); }
    }

    // ---- editor ---------------------------------------------------------------------------------------------------
    private void OpenEditor(GamingProfile? existing)
    {
        var p = existing ?? new GamingProfile { Name = "My profile" };
        var isNew = existing is null;
        _editor.Children.Clear();
        _editor.Visibility = Visibility.Visible;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(isNew ? "New profile" : $"Edit “{p.Name}”", 16, FontWeights.SemiBold));
        if (ProfileService.Active?.ProfileId == p.Id) sp.Children.Add(new TextBlock { Text = "This profile is active. Changes apply the next time it's activated.", Foreground = Ui.Brush("Gold"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        TextBlock L(string t) => new() { Text = t, Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 12, 0, 4) };
        var name = new TextBox { Text = p.Name, MaxLength = ProfileStore.MaxNameLength };
        var desc = new TextBox { Text = p.Description, MaxLength = 400, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 50 };
        var games = new TextBox { Text = string.Join(", ", p.GameExes), ToolTip = "Program file names, comma-separated, e.g. FortniteClient-Win64-Shipping.exe" };
        sp.Children.Add(L("Name")); sp.Children.Add(name);
        sp.Children.Add(L("Description")); sp.Children.Add(desc);
        sp.Children.Add(L("Games (.exe) that trigger auto-switch")); sp.Children.Add(games);

        var power = p.Power; double? timer = p.TimerResolutionMs;
        sp.Children.Add(L("Power plan while active"));
        sp.Children.Add(Chips("pw", [("Keep mine", "Keep"), ("High performance", "HighPerformance"), ("Balanced", "Balanced")], power.ToString(), v => power = Enum.Parse<PowerChoice>(v)));
        sp.Children.Add(L("Timer resolution while active (Input Delay Reducer)"));
        sp.Children.Add(Chips("tr", [("Off", ""), ("1.0 ms", "1.0"), ("0.5 ms", "0.5")], timer?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", v => timer = v.Length == 0 ? null : double.Parse(v, CultureInfo.InvariantCulture)));
        var prio = new CheckBox { Content = "Raise the game to High CPU priority while it runs", IsChecked = p.RaiseGamePriority, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(prio);
        string? tuning = p.TuningProfileId;
        sp.Children.Add(L("CPU & GPU tuning while active"));
        sp.Children.Add(Chips("tu", [("None", ""), ("Efficiency", "efficiency"), ("Quiet", "quiet"), ("Maximum headroom", "headroom")], tuning ?? "", v => tuning = v.Length == 0 ? null : v));
        sp.Children.Add(Ui.Muted("Applied when the profile starts and put back when it ends (also after a crash). GPU limits need Money Tweaks running as administrator; without it only the CPU part is applied, and you'll be told.", 11.5));

        sp.Children.Add(L("Tweaks (only instant, reversible ones are listed)"));
        var selected = p.TweakIds.ToHashSet();
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = "Search tweaks" };
        var list = new StackPanel();
        var conflictText = new TextBlock { Foreground = Ui.Brush("Gold"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var usable = TweakCatalog.All.Where(t => ProfileStore.Usable(t, out _)).OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
        void UpdateConflicts()
        {
            var probe = new GamingProfile { TweakIds = [.. selected], Power = power, RaiseGamePriority = prio.IsChecked == true };
            var c = ProfileEngine.Conflicts(probe);
            conflictText.Text = c.Count == 0 ? "" : string.Join("\n", c.Select(x => "⚠ " + x));
        }
        void Fill()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            foreach (var t in usable.Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || selected.Contains(t.Id)).Take(120))
            {
                var cb = new CheckBox { Content = $"{t.Name}  ·  {CategoryInfo.DisplayName(t.Category)}{(t.Risk == Risk.Caution ? "  ⚠" : "")}", IsChecked = selected.Contains(t.Id), Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 2, 0, 2), ToolTip = t.Description };
                cb.Click += (_, _) => { if (cb.IsChecked == true) selected.Add(t.Id); else selected.Remove(t.Id); UpdateConflicts(); };
                list.Children.Add(cb);
            }
        }
        search.TextChanged += (_, _) => Fill();
        prio.Click += (_, _) => UpdateConflicts();
        sp.Children.Add(search);
        sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        sp.Children.Add(conflictText);
        Fill(); UpdateConflicts();

        var err = new TextBlock { Foreground = Ui.Brush("Danger"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(err);
        var buttons = Ui.Row(
            Ui.Btn("Save profile", (_, _) =>
            {
                var draft = new GamingProfile
                {
                    Id = p.Id, Name = name.Text.Trim(), Description = desc.Text.Trim(), TweakIds = usable.Where(t => selected.Contains(t.Id)).Select(t => t.Id).ToList(),
                    Power = power, TimerResolutionMs = timer, RaiseGamePriority = prio.IsChecked == true, TuningProfileId = tuning,
                    GameExes = games.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
                var errors = ProfileStore.Validate(draft, ProfileStore.All(F));
                if (errors.Count > 0) { err.Text = string.Join("\n", errors); return; }
                (p.Name, p.Description, p.TweakIds, p.Power, p.TimerResolutionMs, p.RaiseGamePriority, p.GameExes, p.Modified) =
                    (draft.Name, draft.Description, draft.TweakIds, draft.Power, draft.TimerResolutionMs, draft.RaiseGamePriority, draft.GameExes, DateTime.Now);
                p.TuningProfileId = draft.TuningProfileId;
                if (isNew) F.Profiles.Add(p);
                if (p.GameExes.Count == 0) F.AutoSwitch.Remove(p.Id);
                ProfileService.Save();
                CloseEditor();
                Rebuild();
                MainWindow.Current?.ShowToast($"Saved “{p.Name}”.", "Success");
            }, "PrimaryButton"),
            Ui.Btn("Cancel", (_, _) => CloseEditor()));
        buttons.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(buttons);
        var card = Ui.Card(sp);
        card.BorderBrush = Ui.Brush("Accent");
        _editor.Children.Add(card);
        _editor.BringIntoView();
    }

    private void CloseEditor() { _editor.Visibility = Visibility.Collapsed; _editor.Children.Clear(); }

    private static WrapPanel Chips(string group, (string label, string value)[] opts, string initial, Action<string> pick)
    {
        var w = new WrapPanel();
        foreach (var (label, value) in opts)
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = group + Guid.NewGuid().ToString("N"), IsChecked = value == initial, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => pick(value);
            w.Children.Add(rb);
        }
        // Radio groups need one shared name per row.
        var g = group + Guid.NewGuid().ToString("N");
        foreach (RadioButton rb in w.Children) rb.GroupName = g;
        return w;
    }
}
