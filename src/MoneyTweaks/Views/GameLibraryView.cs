using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Game Library: catalogue and your own games, detected from launcher data and real process identity. Removing a game
/// only removes Money Tweaks' entry; games are never uninstalled.
/// </summary>
public sealed class GameLibraryView : UserControl, IPageLifecycle
{
    private static LibraryFile L => GameLibrary.Current;
    private readonly TextBox _search = new() { Width = 260, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Search by name or exe", Tag = "Search games or .exe names…" };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _summary = Ui.Muted("Scanning Steam, Epic, Riot, Roblox and Minecraft…", 12);
    private readonly StackPanel _picker = new(), _grid = new(), _removed = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<(LibraryGame game, InstallResult install, GameMatch match, GameStatus status)> _rows = [];
    private GameCategory? _category;
    private bool _favOnly, _hideMissing, _busy;

    public GameLibraryView()
    {
        var actions = Ui.Row(
            Ui.Btn("+  Add game (.exe)…", (_, _) => AddExe(), "PrimaryButton"),
            Ui.Btn("Add game folder…", (_, _) => AddFolder()),
            Ui.Btn("Refresh", async (_, _) => { GameRuntime.Invalidate(); await RefreshAsync(); }, margin: new Thickness(0, 0, 0, 8)));
        var (root, body) = Ui.Page("Game Library",
            "Your games, found from Steam and Epic manifests, the Riot Client, Roblox and Minecraft, plus any game you add by its exe or folder. A game counts as running only when its real executable is running from its install (Roblox Player and Roblox Studio are told apart). Removing a game here never uninstalls it.",
            actions, "Games · Detection · Profiles", "\uE8B7");
        Content = root;

        var bar = new WrapPanel();
        bar.Children.Add(new TextBlock { Text = "Search", Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(_search);
        var fav = new CheckBox { Content = "★ Favourites only", Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        fav.Click += (_, _) => { _favOnly = fav.IsChecked == true; Render(); };
        bar.Children.Add(fav);
        var hide = new CheckBox { Content = "Hide not installed", Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        hide.Click += (_, _) => { _hideMissing = hide.IsChecked == true; Render(); };
        bar.Children.Add(hide);
        body.Children.Add(bar);
        _search.TextChanged += (_, _) => Render();
        body.Children.Add(_chips);
        body.Children.Add(_summary);
        body.Children.Add(_picker);
        body.Children.Add(_grid);
        body.Children.Add(_removed);
        body.Children.Add(Ui.Muted("Profiles chosen here use the normal Gaming Profiles engine: every change is previewed, backed up and read back when the game closes. Games without game-specific settings can still be launched, tested and given a profile. Money Tweaks never injects into games or edits their files from this page.", 11.5));
        _refresh.Tick += async (_, _) => await RefreshAsync(quiet: true);
        BuildChips();
    }

    public async void OnShown() { _refresh.Start(); await RefreshAsync(); }

    /// <summary>Used by global search: show one game.</summary>
    public void FocusGame(string name)
    {
        _search.Text = name;
        _search.Focus();
        _search.SelectAll();
    }
    public void OnHidden() => _refresh.Stop();

    private void Save()
    {
        try { GameLibrary.Save(L); } catch (Exception ex) { Dialog.Show("Couldn't save the library", ex.Message, DialogKind.Error); }
    }

    private async Task RefreshAsync(bool quiet = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var games = GameLibrary.Items(L);
            _rows = await Task.Run(() => GameRuntime.Statuses(games));
            Render();
        }
        catch (Exception ex) { if (!quiet) _summary.Text = "Scan failed: " + ex.Message; Log.Error("Game Library scan failed", ex); }
        finally { _busy = false; }
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        void Chip(string label, GameCategory? cat)
        {
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "libcat", IsChecked = _category == cat, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _category = cat; Render(); };
            _chips.Children.Add(rb);
        }
        Chip("All", null);
        foreach (var c in Enum.GetValues<GameCategory>()) Chip(GameCatalog.Label(c), c);
    }

    private static string Tone(GameStatus s) => s switch { GameStatus.Running => "Success", GameStatus.Installed => "Accent", GameStatus.NotDetected => "Gold", _ => "Muted" };

    private void Render()
    {
        var visible = GameLibrary.Filter(_rows.Select(r => r.game), _search.Text, _category, _favOnly).Select(g => _rows.First(r => r.game.Key == g.Key))
            .Where(r => !_hideMissing || r.status != GameStatus.NotInstalled).ToList();
        int running = _rows.Count(r => r.status == GameStatus.Running), installed = _rows.Count(r => r.status == GameStatus.Installed);
        _summary.Text = $"{_rows.Count} games in your library · {running} running · {installed} installed · {_rows.Count(r => r.status == GameStatus.NotDetected)} not detected · showing {visible.Count}";
        _grid.Children.Clear();
        // Running and installed first, then by name (favourites float to the top inside each group).
        var ordered = visible.OrderBy(r => r.status).ThenByDescending(r => r.game.Favourite).ThenBy(r => r.game.Def.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 0) { _grid.Children.Add(Ui.Card(Ui.Empty("\uE721", "No games match", "Clear the search or filters, or add a game by its exe."))); }
        else
        {
            var grid = (ResponsiveGrid)Ui.Grid(2, ordered.Select(Card).ToArray());
            grid.MinItemWidth = 420;
            _grid.Children.Add(grid);
        }
        RenderRemoved();
    }

    private UIElement Card((LibraryGame game, InstallResult install, GameMatch match, GameStatus status) row)
    {
        var (g, inst, match, status) = row;
        var sp = new StackPanel();
        var head = new DockPanel();
        var star = new Button { Content = g.Favourite ? "★" : "☆", Style = Ui.Res<Style>("GhostButton"), Margin = new Thickness(6, 0, 0, 0), ToolTip = g.Favourite ? "Remove from favourites" : "Add to favourites", FontSize = 16 };
        star.Click += (_, _) => { var e = GameLibrary.EntryFor(L, g); e.Favourite = !e.Favourite; Save(); Reload(); };
        DockPanel.SetDock(star, Dock.Right);
        head.Children.Add(star);
        var tag = Ui.Tag(GameDetector.Label(status), Tone(status));
        tag.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(tag, Dock.Right);
        head.Children.Add(tag);
        head.Children.Add(Ui.Text(g.Def.Name, 15, FontWeights.SemiBold));
        sp.Children.Add(head);
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        meta.Children.Add(Ui.Tag(g.Custom ? "Added by you" : GameCatalog.Label(g.Def.Category), g.Custom ? "Violet" : "Muted"));
        if (inst.Install is { } i) meta.Children.Add(Ui.Tag(i.Source, "Muted"));
        sp.Children.Add(meta);
        var where = match.Process is { } p ? $"Running: {p.Path ?? p.Exe} (pid {p.Pid})"
            : inst.Install is { } ii ? ii.ExePath ?? ii.InstallDir
            : inst.Checks.FirstOrDefault(c => !c.Found)?.Detail ?? "No launcher data for this game; add its exe to track it.";
        sp.Children.Add(new TextBlock { Text = where, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        // Profile for this game (real Gaming Profiles engine) and runtime priority.
        var entry = g.Entry;
        var prof = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Gaming profile to use with this game" };
        prof.Items.Add(new ComboBoxItem { Content = "No profile", Tag = null });
        foreach (var pr in ProfileStore.All(ProfileService.File)) prof.Items.Add(new ComboBoxItem { Content = pr.Name + (pr.BuiltIn ? "" : " (yours)"), Tag = pr.Id });
        prof.SelectedItem = prof.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string?)x.Tag == entry?.ProfileId) ?? prof.Items[0];
        var auto = new CheckBox { Content = "Auto-activate while running", IsChecked = entry?.AutoActivate == true, IsEnabled = entry?.ProfileId is not null, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) };
        prof.SelectionChanged += (_, _) =>
        {
            var e = GameLibrary.EntryFor(L, g);
            e.ProfileId = (string?)((ComboBoxItem)prof.SelectedItem).Tag;
            if (e.ProfileId is null) e.AutoActivate = false;
            Save();
            auto.IsEnabled = e.ProfileId is not null;
            auto.IsChecked = e.AutoActivate;
        };
        auto.Click += async (_, _) => await ToggleAutoAsync(g, auto);
        var prio = new CheckBox { Content = "High priority while running", IsChecked = entry?.RaisePriority == true, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6),
            ToolTip = "Raises only the verified game process to High priority while it runs. Runtime only: nothing is stored in Windows and it ends with the game." };
        prio.Click += (_, _) =>
        {
            GameLibrary.EntryFor(L, g).RaisePriority = prio.IsChecked == true;
            Save();
            ProfileService.StartWatcher();
        };
        var profRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        profRow.Children.Add(prof);
        profRow.Children.Add(auto);
        profRow.Children.Add(prio);
        sp.Children.Add(profRow);

        // Real game-specific settings that exist in Money Tweaks.
        var feats = new WrapPanel();
        foreach (var f in g.Def.Features)
        {
            var (label, tagName) = f switch
            {
                "potato" => ("Potato Graphics", "potato"),
                "fnstretch" => ("Fortnite Stretched", "fnstretch"),
                "overlay" => ("Performance Overlay", "overlay"),
                _ => (f, f),
            };
            feats.Children.Add(Ui.Btn(label + " ›", (_, _) => MainWindow.Current?.Navigate(tagName), "GhostButton"));
        }
        if (g.Def.PriorityTweak is not null) feats.Children.Add(Ui.Btn("CPU priority tweak ›", (_, _) => MainWindow.Current?.Navigate("cat:GameProfiles"), "GhostButton"));
        if (feats.Children.Count > 0) sp.Children.Add(feats);
        else sp.Children.Add(Ui.Muted("No game-specific settings in Money Tweaks for this game; a profile and runtime priority still work.", 11.5));

        var row2 = Ui.Row();
        row2.Margin = new Thickness(0, 8, 0, 0);
        var launch = Ui.Btn(status == GameStatus.Running ? "Running" : "Launch", (_, _) => Launch(g, inst.Install), "PrimaryButton");
        launch.IsEnabled = status != GameStatus.Running && inst.Install is { } li && (li.LaunchUri is not null || li.LaunchExe is not null || li.ExePath is not null);
        row2.Children.Add(launch);
        row2.Children.Add(Ui.Btn("Test detection", async (_, _) => await TestAsync(g)));
        row2.Children.Add(Ui.Btn(g.Entry?.ExePath is null ? "Set exe…" : "Change exe…", (_, _) => ChangeExe(g), "GhostButton"));
        row2.Children.Add(Ui.Btn("Remove…", (_, _) => Remove(g), "GhostButton"));
        sp.Children.Add(row2);
        var card = Ui.Card(sp, new Thickness(0, 0, 14, 14));
        if (status == GameStatus.Running) card.BorderBrush = Ui.Brush("Success");
        return card;
    }

    private void Reload() { _ = RefreshAsync(); }

    private async Task ToggleAutoAsync(LibraryGame g, CheckBox box)
    {
        var e = GameLibrary.EntryFor(L, g);
        if (box.IsChecked == true)
        {
            if (ProfileStore.All(ProfileService.File).FirstOrDefault(p => p.Id == e.ProfileId) is not { } p) { box.IsChecked = false; return; }
            var text = await Task.Run(() => ProfileService.Describe(p));
            if (!Dialog.Confirm($"Auto-activate “{p.Name}” for {g.Def.Name}?",
                    $"When {g.Def.Name} is detected running (its real executable, not just a matching name), Money Tweaks applies this profile and restores everything when the game closes.\n\n{text}",
                    "Enable", DialogKind.Warning)) { box.IsChecked = false; return; }
            if (!Admin.IsElevated && await Task.Run(() => ProfileEngine.NeedsAdmin(p)))
                Dialog.Show("Runs as admin only", "This profile changes system-wide settings, so automatic activation only works while Money Tweaks runs as administrator. You'll get a notice when the game starts otherwise.", DialogKind.Warning);
            e.AutoActivate = true;
        }
        else e.AutoActivate = false;
        Save();
        ProfileService.StartWatcher();
    }

    /// <summary>
    /// Starts a game the way its launcher expects. If the user set up a cleanup rule for this game with "clean when I
    /// launch it", the approved apps are closed first (PC Cleaner › Clean &amp; Play), with a measured report.
    /// </summary>
    internal static async void Launch(LibraryGame g, GameInstall? i)
    {
        if (i is null) return;
        var cleaner = Core.Cleaner.CleanerSettings.Load();
        if (cleaner.RuleFor(g.Key) is { CleanOnLaunch: true, Close.Count: > 0 } rule)
        {
            try
            {
                MainWindow.Current?.ShowToast($"Closing {rule.Close.Count} approved app(s) before {g.Def.Name}…", "Accent");
                var report = await Core.Cleaner.ProcessCleaner.CleanAsync(rule.Close, rule.ForceIfIgnored, "Clean & Play", g.Def.ExeNames.FirstOrDefault(), cleaner.ProcessExclusions);
                cleaner.AddHistory(Core.Cleaner.ProcessCleaner.Summarize(report) + $" (before {g.Def.Name})");
                cleaner.Save();
            }
            catch (Exception ex) { Log.Warn("clean before launch: " + ex.Message); }
        }
        try
        {
            if (i.LaunchUri is not null) Process.Start(new ProcessStartInfo(i.LaunchUri) { UseShellExecute = true });
            else
            {
                var exe = i.LaunchExe ?? i.ExePath!;
                Process.Start(new ProcessStartInfo(exe, i.LaunchArgs ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
            Log.Info($"Game Library: asked {(i.LaunchUri is not null ? i.Source : "Windows")} to start {g.Def.Name}");
            MainWindow.Current?.ShowToast($"Starting {g.Def.Name} through {(i.LaunchUri is not null ? i.Source : "its exe")}. It shows as Running once its process is detected.", "Accent");
        }
        catch (Exception ex) { Dialog.Show($"Couldn't start {g.Def.Name}", ex.Message, DialogKind.Error); }
    }

    private static async Task TestAsync(LibraryGame g)
    {
        var r = await Task.Run(() => GameRuntime.Test(g));
        var sb = new StringBuilder();
        foreach (var l in r.Lines) sb.AppendLine(l);
        if (r.Process is { } p) sb.AppendLine($"\nMatched: {p.Exe} · {p.Path ?? "path not readable"} · pid {p.Pid}");
        else if (r.Install?.ExePath is { } exe) sb.AppendLine($"\nMatched executable (not running): {exe}");
        Log.Info("Game detection test:\n" + sb);
        Dialog.Show($"Detection: {g.Def.Name}", sb.ToString());
    }

    // ---- adding and changing exes ------------------------------------------------------------------------------------

    private void AddExe()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the game's .exe", Filter = "Programs|*.exe" };
        if (dlg.ShowDialog() != true) return;
        AddPath(dlg.FileName);
    }

    private void AddPath(string exe)
    {
        try
        {
            var (g, msg) = GameLibrary.AddExe(L, exe);
            Save();
            GameRuntime.Invalidate();
            MainWindow.Current?.ShowToast(msg, "Success");
            _picker.Children.Clear();
            Reload();
        }
        catch (Exception ex) { Dialog.Show("Couldn't add the game", ex.Message, DialogKind.Warning); }
    }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the game's installation folder" };
        if (dlg.ShowDialog() != true) return;
        var folder = dlg.FolderName;
        var cands = GameScanner.CandidateExes(folder);
        _picker.Children.Clear();
        var sec = Ui.Section("Which program is the game?", $"Executables found in {folder} (installers, updaters and crash reporters are left out). The usual choice is listed first.");
        if (cands.Count == 0) sec.Children.Add(Ui.Muted("No game executables found in that folder. Use “Add game (.exe)” to pick it directly.", 12));
        foreach (var (path, size) in cands)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var use = Ui.Btn("Add this", (_, _) => AddPath(path), "PrimaryButton", new Thickness(8, 0, 0, 0));
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(new TextBlock { Text = $"{Path.GetRelativePath(folder, path)}  ·  {size / 1048576.0:0.#} MB", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap });
            sec.Children.Add(row);
        }
        sec.Children.Add(Ui.Row(Ui.Btn("Cancel", (_, _) => _picker.Children.Clear(), "GhostButton")));
        _picker.Children.Add(Ui.Card(sec));
    }

    private void ChangeExe(LibraryGame g)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Choose {g.Def.Name}'s .exe", Filter = "Programs|*.exe" };
        if (dlg.ShowDialog() != true) return;
        var e = GameLibrary.EntryFor(L, g);
        var exe = Path.GetFileName(dlg.FileName);
        e.ExePath = dlg.FileName;
        if (g.Custom) e.ProcessNames = [exe];
        else if (!g.Def.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase))
            Dialog.Show("Different program name", $"{g.Def.Name} normally runs as {string.Join(" / ", g.Def.ExeNames)}, not {exe}. The install folder is used, but running detection still looks for the game's own process. Add {exe} as its own game if it's a different program.", DialogKind.Warning);
        Save();
        GameRuntime.Invalidate();
        Reload();
    }

    // ---- removing ------------------------------------------------------------------------------------------------

    private void Remove(LibraryGame g)
    {
        if (ProfileService.Active?.TriggeredGameKey == g.Key) { Dialog.Show("Profile is active", $"A profile is active because {g.Def.Name} is running. Switch it off on Gaming Profiles first so its settings are restored."); return; }
        var linked = GameLibrary.LinkedCustomProfiles(g, ProfileService.File.Profiles);
        if (!Dialog.Confirm($"Remove {g.Def.Name} from the library?",
                $"Only Money Tweaks' entry is removed{(g.Entry is not null ? " (your favourite, profile choice and exe path for it)" : "")}. The game stays installed: nothing is uninstalled, moved or deleted." +
                (g.Custom ? "" : "\n\nYou can add it back any time from “Removed games” below.") +
                (linked.Count > 0 ? $"\n\nYour profile(s) linked to this game: {string.Join(", ", linked.Select(p => p.Name))}. You'll be asked about them next." : ""),
                "Remove from library", DialogKind.Warning)) return;
        if (linked.Count > 0)
        {
            if (Dialog.Show("Export linked profiles first?", $"Save {string.Join(", ", linked.Select(p => p.Name))} to a file before deciding whether to delete them?", DialogKind.Confirm, "Export…", "Skip"))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Money Tweaks profiles|*.mtprofiles.json", FileName = string.Concat(g.Def.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)) + "-profiles.mtprofiles.json" };
                if (dlg.ShowDialog() == true)
                {
                    try { ProfileStore.Export(linked, dlg.FileName); MainWindow.Current?.ShowToast($"Exported {linked.Count} profile(s).", "Success"); }
                    catch (Exception ex) { Dialog.Show("Export failed", ex.Message + "\n\nNothing was deleted.", DialogKind.Error); return; }
                }
            }
            var deletable = linked.Where(p => ProfileService.Active?.ProfileId != p.Id).ToList();
            if (deletable.Count > 0 && Dialog.Confirm("Delete the linked profiles too?",
                    $"Delete {string.Join(", ", deletable.Select(p => p.Name))}? Choose Keep to leave them on Gaming Profiles. Your Windows settings are not changed either way.", "Delete profiles", DialogKind.Warning))
            {
                foreach (var p in deletable) { ProfileService.File.Profiles.Remove(p); ProfileService.File.AutoSwitch.Remove(p.Id); }
                ProfileService.Save();
            }
        }
        GameLibrary.Remove(L, g);
        Save();
        MainWindow.Current?.ShowToast($"{g.Def.Name} removed from the library. The game itself wasn't touched.", "Success");
        Reload();
    }

    private void RenderRemoved()
    {
        _removed.Children.Clear();
        var removed = GameLibrary.Removed(L).ToList();
        if (removed.Count == 0) return;
        var sec = Ui.Section("Removed games", "Catalogue games you removed from the library. Add one back to track and launch it again.");
        var wrap = new WrapPanel();
        foreach (var d in removed.OrderBy(d => d.Name))
        {
            var def = d;
            wrap.Children.Add(Ui.Btn("+ " + d.Name, (_, _) => { GameLibrary.Restore(L, def.Id); Save(); Reload(); }, "GhostButton"));
        }
        sec.Children.Add(wrap);
        _removed.Children.Add(Ui.Card(sec));
    }
}
