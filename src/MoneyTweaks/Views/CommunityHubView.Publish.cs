using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Community;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Community;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Profiles;

namespace MoneyTweaks.Views;

public sealed partial class CommunityHubView
{
    // Editor state (kept while switching tabs, so nothing typed is lost).
    private string? _editingId;
    private PostInput _draft = NewDraft();
    private bool _incSettings = true, _incCrosshair, _incProfile, _incPreset, _incHardware, _incBenchmark;
    private string? _crosshairId, _profileName, _presetName;
    private bool _crosshairImages;
    private readonly List<ImageInfo> _uploads = [];
    private string _keybindsText = "", _graphicsText = "";
    private List<string> _publishErrors = [];

    private static PostInput NewDraft() => new()
    {
        Game = "Fortnite", Category = "Competitive",
        Content = new PostContent { Settings = new GameSettings(), Hardware = new HardwareInfo(), Benchmark = new BenchmarkReport() },
    };

    private void BeginEdit(PostDetail p)
    {
        _editingId = p.Id;
        _draft = new PostInput
        {
            Title = p.Title, Description = p.Description, Game = p.Game, Category = p.Category, Tags = [.. p.Tags],
            Content = new PostContent
            {
                Settings = p.Content.Settings ?? new GameSettings(), Hardware = p.Content.Hardware ?? new HardwareInfo(), Benchmark = p.Content.Benchmark ?? new BenchmarkReport(),
                GamingProfile = p.Content.GamingProfile, Preset = p.Content.Preset, Crosshair = p.Content.Crosshair,
            },
        };
        _incSettings = p.Content.Settings is not null;
        _incHardware = p.Content.Hardware is not null;
        _incBenchmark = p.Content.Benchmark is not null;
        _incCrosshair = p.Content.Crosshair is not null;
        _incProfile = p.Content.GamingProfile is not null;
        _incPreset = p.Content.Preset is not null;
        _crosshairId = null; _profileName = null; _presetName = null; // keep the published ones unless another is picked
        _keybindsText = string.Join("\n", _draft.Content.Settings!.Keybinds.Select(k => $"{k.Action} = {k.Key}"));
        _graphicsText = string.Join("\n", _draft.Content.Settings!.Graphics.Select(g => $"{g.Name} = {g.Value}"));
        _uploads.Clear();
        _uploads.AddRange(p.Images.Where(i => i.Kind == "upload"));
        _publishErrors = [];
        _detail = null;
        _tab = "publish";
        Render();
    }

    private void ResetEditor()
    {
        _editingId = null;
        _draft = NewDraft();
        _incSettings = true; _incCrosshair = _incProfile = _incPreset = _incHardware = _incBenchmark = false;
        _crosshairId = _profileName = _presetName = null;
        _crosshairImages = false;
        _uploads.Clear();
        _keybindsText = _graphicsText = "";
        _publishErrors = [];
    }

    private void RenderPublish()
    {
        var me = CommunityClient.Me;
        if (me is null) { _body.Children.Add(Ui.Card(Ui.Empty("\uE716", "Sign in to publish", "Publishing needs a GitHub sign-in, so every post has a real author and limits apply per account."))); return; }
        if (me.Banned) { _body.Children.Add(Note("This account is suspended from the Community Hub.", "Danger")); return; }
        if (App.Settings.CommunityGuidelinesAccepted < CommunityGuidelines.Version)
        {
            var g = new StackPanel();
            g.Children.Add(Ui.Section("Before you post", "Please read the community guidelines once."));
            g.Children.Add(new TextBlock { Text = CommunityClient.Config?.Guidelines ?? CommunityGuidelines.Text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text"), LineHeight = 21 });
            g.Children.Add(Ui.Row(Ui.Btn("I'll follow these", (_, _) => { App.Settings.CommunityGuidelinesAccepted = CommunityGuidelines.Version; App.Settings.Save(); Render(); }, "PrimaryButton", new Thickness(0, 12, 0, 0))));
            _body.Children.Add(Ui.Card(g));
            return;
        }
        var d = _draft;
        var s = d.Content.Settings!;

        // ---- basics ----
        var basics = new StackPanel();
        basics.Children.Add(Ui.Section(_editingId is null ? "New post" : "Edit post", "Titles and descriptions are shown exactly as written. No links to cheats, no personal information."));
        basics.Children.Add(Field("Title", TextField(d.Title, v => d.Title = v, PostRules.TitleMax, "e.g. My 1440×1080 stretched Fortnite settings")));
        var gameCombo = Combo(Taxonomy.Games, d.Game, v => d.Game = v);
        var catCombo = Combo(Taxonomy.Categories, d.Category, v => d.Category = v);
        basics.Children.Add(Field("Game", gameCombo));
        basics.Children.Add(Field("Category", catCombo));
        basics.Children.Add(Field("Tags", TextField(string.Join(", ", d.Tags), v => d.Tags = v.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(), 200, "comma separated, e.g. stretched, 240hz, low-end")));
        var desc = new TextBox { Text = d.Description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = PostRules.DescriptionMax, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        desc.TextChanged += (_, _) => d.Description = desc.Text;
        basics.Children.Add(Field("Description", desc));
        _body.Children.Add(Ui.Card(basics, new Thickness(0, 0, 0, 12)));

        // ---- what to share ----
        var what = new StackPanel();
        what.Children.Add(Ui.Section("What are you sharing?", "Pick one or more."));
        var picks = new WrapPanel();
        picks.Children.Add(Toggle("Game settings", _incSettings, v => _incSettings = v));
        picks.Children.Add(Toggle("Crosshair", _incCrosshair, v => _incCrosshair = v));
        picks.Children.Add(Toggle("Gaming profile", _incProfile, v => _incProfile = v));
        picks.Children.Add(Toggle("Preset", _incPreset, v => _incPreset = v));
        what.Children.Add(picks);
        _body.Children.Add(Ui.Card(what, new Thickness(0, 0, 0, 12)));

        if (_incSettings) _body.Children.Add(Ui.Card(SettingsEditor(s), new Thickness(0, 0, 0, 12)));
        if (_incCrosshair) _body.Children.Add(Ui.Card(CrosshairPicker(), new Thickness(0, 0, 0, 12)));
        if (_incProfile) _body.Children.Add(Ui.Card(ProfilePicker(), new Thickness(0, 0, 0, 12)));
        if (_incPreset) _body.Children.Add(Ui.Card(PresetPicker(), new Thickness(0, 0, 0, 12)));

        // ---- optional PC + benchmark ----
        var pc = new StackPanel();
        pc.Children.Add(Ui.Section("Optional", "Only what you type here is shared. Nothing is read from your PC."));
        pc.Children.Add(Toggle("Show my PC's hardware", _incHardware, v => _incHardware = v));
        if (_incHardware)
        {
            var h = d.Content.Hardware!;
            pc.Children.Add(Field("CPU", TextField(h.Cpu ?? "", v => h.Cpu = v, 80, "e.g. Ryzen 5 7600X")));
            pc.Children.Add(Field("GPU", TextField(h.Gpu ?? "", v => h.Gpu = v, 80, "e.g. RTX 4070 SUPER")));
            pc.Children.Add(Field("Memory (GB)", NumberField(h.RamGb, v => h.RamGb = v is null ? null : (int)v)));
            pc.Children.Add(Field("Monitor", TextField(h.Monitor ?? "", v => h.Monitor = v, 80, "e.g. 27\" 1440p 240 Hz")));
        }
        pc.Children.Add(Toggle("Add my FPS results (shown as user-reported)", _incBenchmark, v => _incBenchmark = v));
        if (_incBenchmark)
        {
            var b = d.Content.Benchmark!;
            pc.Children.Add(Field("Average FPS", NumberField(b.AverageFps, v => b.AverageFps = v)));
            pc.Children.Add(Field("1% low FPS", NumberField(b.OnePercentLowFps, v => b.OnePercentLowFps = v)));
            pc.Children.Add(Field("Where", TextField(b.Scenario ?? "", v => b.Scenario = v, 80, "e.g. Battle Royale, late game")));
        }
        _body.Children.Add(Ui.Card(pc, new Thickness(0, 0, 0, 12)));

        // ---- images ----
        _body.Children.Add(Ui.Card(ImagesEditor(me), new Thickness(0, 0, 0, 12)));

        // ---- publish ----
        var actions = new StackPanel();
        foreach (var e in _publishErrors) actions.Children.Add(Note(e, "Danger"));
        actions.Children.Add(Ui.Row(
            Ui.Btn(_editingId is null ? "Publish" : "Save changes", async (_, _) => await PublishAsync(), "PrimaryButton"),
            Ui.Btn(_editingId is null ? "Clear" : "Cancel editing", (_, _) => { ResetEditor(); Render(); }, "GhostButton")));
        actions.Children.Add(Ui.Muted("Posts appear right away. New screenshots appear for others after a moderator checks them.", 12));
        _body.Children.Add(Ui.Card(actions));
    }

    private static UIElement Field(string label, UIElement input)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Ui.Muted(label, 12.5);
        l.VerticalAlignment = VerticalAlignment.Top;
        l.Margin = new Thickness(0, 6, 0, 0);
        g.Children.Add(l);
        Grid.SetColumn(input, 1);
        if (input is FrameworkElement fe && fe is not TextBox { AcceptsReturn: true }) { fe.HorizontalAlignment = HorizontalAlignment.Left; if (fe.Width is double.NaN && fe is TextBox) fe.Width = 420; }
        g.Children.Add(input);
        return g;
    }

    private static TextBox TextField(string value, Action<string> set, int max, string? hint = null)
    {
        var t = new TextBox { Text = value, MaxLength = max, Tag = hint };
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    private static TextBox NumberField(double? value, Action<double?> set)
    {
        var t = new TextBox { Text = value?.ToString(CultureInfo.CurrentCulture) ?? "", Width = 120 };
        t.TextChanged += (_, _) =>
        {
            var txt = t.Text.Trim();
            if (txt.Length == 0) { set(null); return; }
            if (double.TryParse(txt, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) set(v);
        };
        return t;
    }

    private static ComboBox Combo(IEnumerable<string> items, string? value, Action<string> set)
    {
        var c = new ComboBox { Width = 240 };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedItem = value;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string v) set(v); };
        return c;
    }

    private CheckBox Toggle(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 4, 16, 4) };
        c.Click += (_, _) => { set(c.IsChecked == true); Render(); };
        return c;
    }

    private UIElement SettingsEditor(GameSettings s)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Game settings"));
        var res = new StackPanel { Orientation = Orientation.Horizontal };
        var w = NumberField(s.ResolutionWidth, v => s.ResolutionWidth = v is null ? null : (int)v);
        var h = NumberField(s.ResolutionHeight, v => s.ResolutionHeight = v is null ? null : (int)v);
        w.Width = h.Width = 80;
        res.Children.Add(w);
        res.Children.Add(new TextBlock { Text = " × ", Foreground = Ui.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center });
        res.Children.Add(h);
        var stretched = new CheckBox { Content = "Stretched", IsChecked = s.Stretched == true, Foreground = Ui.Brush("Text"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        stretched.Click += (_, _) => s.Stretched = stretched.IsChecked == true ? true : null;
        res.Children.Add(stretched);
        sp.Children.Add(Field("Resolution", res));
        sp.Children.Add(Field("Refresh rate (Hz)", NumberField(s.RefreshHz, v => s.RefreshHz = v is null ? null : (int)v)));
        sp.Children.Add(Field("FPS cap (0 = none)", NumberField(s.FpsCap, v => s.FpsCap = v is null ? null : (int)v)));
        sp.Children.Add(Field("Sensitivity", NumberField(s.Sensitivity, v => s.Sensitivity = v)));
        sp.Children.Add(Field("ADS sensitivity", NumberField(s.AdsSensitivity, v => s.AdsSensitivity = v)));
        sp.Children.Add(Field("Mouse DPI", NumberField(s.MouseDpi, v => s.MouseDpi = v is null ? null : (int)v)));
        var kb = new TextBox { Text = _keybindsText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, Tag = "One per line, e.g.\nWall = Q\nFloor = F" };
        kb.TextChanged += (_, _) => _keybindsText = kb.Text;
        sp.Children.Add(Field("Keybinds", kb));
        var gr = new TextBox { Text = _graphicsText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, Tag = "One per line, e.g.\nRendering mode = Performance\nView distance = Epic" };
        gr.TextChanged += (_, _) => _graphicsText = gr.Text;
        sp.Children.Add(Field("Graphics", gr));
        var notes = new TextBox { Text = s.Notes ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50, MaxLength = PostRules.NotesMax };
        notes.TextChanged += (_, _) => s.Notes = notes.Text;
        sp.Children.Add(Field("Notes", notes));
        return sp;
    }

    private UIElement CrosshairPicker()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Crosshair", "Pick one of your Custom Crosshair profiles. Money Tweaks makes the preview image for you; it doesn't use your uploads."));
        var profiles = CrosshairService.Data.Profiles;
        var names = profiles.Select(p => p.Name).ToList();
        if (_editingId is not null && _crosshairId is null && _draft.Content.Crosshair is not null) names.Insert(0, "(keep the published crosshair)");
        var c = Combo(names, _crosshairId is { } id ? profiles.FirstOrDefault(p => p.Id == id)?.Name : names.FirstOrDefault(), v => _crosshairId = profiles.FirstOrDefault(p => p.Name == v)?.Id);
        if (_crosshairId is null && names.Count > 0 && !names[0].StartsWith('(')) _crosshairId = profiles[0].Id;
        sp.Children.Add(Field("Profile", c));
        var chosen = profiles.FirstOrDefault(p => p.Id == _crosshairId);
        int imgs = chosen?.Layers.Count(l => l.Kind == LayerKind.Image && l.ImageFile is { } f && CrosshairImages.Exists(f)) ?? 0;
        if (imgs > 0)
        {
            var cb = new CheckBox { Content = $"Include its image layer{(imgs == 1 ? "" : "s")} ({imgs} upload{(imgs == 1 ? "" : "s")} from today's allowance)", IsChecked = _crosshairImages, Foreground = Ui.Brush("Text"), Margin = new Thickness(140, 4, 0, 0) };
            cb.Click += (_, _) => _crosshairImages = cb.IsChecked == true;
            sp.Children.Add(cb);
        }
        if (chosen is not null)
        {
            var vis = new CrosshairVisual { Profile = chosen, Zoom = 3, Height = 120 };
            sp.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)), CornerRadius = new CornerRadius(8), Child = vis, Margin = new Thickness(140, 8, 0, 0), Width = 300, HorizontalAlignment = HorizontalAlignment.Left });
        }
        return sp;
    }

    private UIElement ProfilePicker()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Gaming profile", "Shared as a list of Money Tweaks tweaks and options. Others can preview and import it; it's never applied for them."));
        var all = ProfileStore.All(ProfileStore.Load()).ToList();
        var names = all.Select(p => p.Name).ToList();
        if (_editingId is not null && _profileName is null && _draft.Content.GamingProfile is not null) names.Insert(0, "(keep the published profile)");
        _profileName ??= names.FirstOrDefault(n => !n.StartsWith('('));
        if (names.Count > 0 && names[0].StartsWith('(')) _profileName = null;
        sp.Children.Add(Field("Profile", Combo(names, _profileName ?? names.FirstOrDefault(), v => _profileName = v.StartsWith('(') ? null : v)));
        return sp;
    }

    private UIElement PresetPicker()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Preset", "Shared as a list of tweak ids. Nothing on your PC is read beyond the preset itself."));
        var all = PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).ToList();
        var names = all.Select(p => p.Name).ToList();
        if (_editingId is not null && _presetName is null && _draft.Content.Preset is not null) names.Insert(0, "(keep the published preset)");
        _presetName ??= names.FirstOrDefault(n => !n.StartsWith('('));
        if (names.Count > 0 && names[0].StartsWith('(')) _presetName = null;
        sp.Children.Add(Field("Preset", Combo(names, _presetName ?? names.FirstOrDefault(), v => _presetName = v.StartsWith('(') ? null : v)));
        return sp;
    }

    private UIElement ImagesEditor(MeInfo me)
    {
        var cfg = CommunityClient.Config!;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Screenshots", $"Gameplay or settings screenshots, or a crosshair PNG. PNG, JPEG or WebP, up to {cfg.MaxUploadBytes / 1024 / 1024} MB and {cfg.MaxImageDimension}×{cfg.MaxImageDimension}. Up to {PostRules.MaxImages} per post."));
        sp.Children.Add(Ui.Muted(QuotaText(me.Quota), 12.5));
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var img in _uploads.ToList())
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 10, 8), Width = 170 };
            var im = new Image { Height = 100, Stretch = Stretch.UniformToFill };
            _ = LoadInto(im, img.ThumbUrl, 340);
            cell.Children.Add(new Border { Child = im, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)) });
            cell.Children.Add(Ui.Muted(img.Status == "pending" ? "Awaiting moderation" : img.Status == "approved" ? "Approved" : img.Status, 11.5));
            var i = img;
            cell.Children.Add(Ui.Btn("Remove", async (_, _) => await Try(async () =>
            {
                if (!Dialog.Confirm("Remove this image?", "It's deleted from the Community Hub. It still counts toward today's upload limit.", "Remove", DialogKind.Warning)) return;
                await CommunityClient.Delete<bool>($"api/v1/images/{i.Id}");
                _uploads.Remove(i);
                Render();
            }), "GhostButton"));
            wrap.Children.Add(cell);
        }
        sp.Children.Add(wrap);
        var add = Ui.Btn("Add screenshot…", async (_, _) => await AddImageAsync(), "BaseButton", new Thickness(0, 4, 0, 0));
        add.IsEnabled = me.Quota.Remaining > 0 && _uploads.Count < PostRules.MaxImages;
        if (me.Quota.Remaining == 0) add.ToolTip = "You've used today's uploads.";
        sp.Children.Add(Ui.Row(add));
        return sp;
    }

    private async Task AddImageAsync()
    {
        var cfg = CommunityClient.Config!;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a screenshot", Filter = "Images (PNG, JPEG, WebP)|*.png;*.jpg;*.jpeg;*.webp" };
        if (dlg.ShowDialog() != true) return;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > cfg.MaxUploadBytes) { Dialog.Show("Image too large", $"Images can be at most {cfg.MaxUploadBytes / 1024 / 1024} MB. This one is {info.Length / 1024.0 / 1024:0.0} MB.", DialogKind.Warning); return; }
        var data = await File.ReadAllBytesAsync(dlg.FileName);
        var mime = CommunityClient.MimeOf(data);
        if (mime == "application/octet-stream") { Dialog.Show("Not supported", "Only PNG, JPEG and WebP images can be uploaded.", DialogKind.Warning); return; }
        if (!Dialog.Confirm("Upload this image?", $"{Path.GetFileName(dlg.FileName)} uses 1 of your {CommunityClient.Me?.Quota.Remaining} remaining uploads today, even if you remove it later. Make sure it shows no personal information.", "Upload")) return;
        await Try(async () =>
        {
            var r = await CommunityClient.UploadAsync(data, mime);
            _uploads.Add(r.Image);
            MainWindow.Current?.ShowToast(r.Image.Status == "pending" ? "Uploaded. Others see it after a moderator approves it." : "Uploaded.", "Success");
            Render();
        });
    }

    private async Task PublishAsync()
    {
        _publishErrors = [];
        var d = _draft;
        var s = d.Content.Settings!;
        s.Keybinds = CommunityImport.ParsePairs(_keybindsText).Select(p => new Keybind { Action = p.a, Key = p.b }).ToList();
        s.Graphics = CommunityImport.ParsePairs(_graphicsText).Select(p => new SettingPair { Name = p.a, Value = p.b }).ToList();
        var input = new PostInput
        {
            Title = d.Title, Description = d.Description, Game = d.Game, Category = d.Category, Tags = [.. d.Tags],
            ImageIds = _uploads.Select(i => i.Id).ToList(),
            Content = new PostContent
            {
                Settings = _incSettings ? s : null,
                Hardware = _incHardware ? d.Content.Hardware : null,
                Benchmark = _incBenchmark ? d.Content.Benchmark : null,
            },
        };
        try
        {
            if (_incProfile)
                input.Content.GamingProfile = _profileName is null ? d.Content.GamingProfile
                    : ProfileStore.All(ProfileStore.Load()).FirstOrDefault(p => p.Name == _profileName) is { } gp ? CommunityImport.FromGamingProfile(gp) : null;
            if (_incPreset)
                input.Content.Preset = _presetName is null ? d.Content.Preset
                    : PresetStore.BuiltIns.Concat(PresetStore.Load().Presets).FirstOrDefault(p => p.Name == _presetName) is { } pr ? CommunityImport.FromPreset(pr) : null;
            if (_incCrosshair)
            {
                var chosen = CrosshairService.Data.Profiles.FirstOrDefault(p => p.Id == _crosshairId);
                if (chosen is null) input.Content.Crosshair = d.Content.Crosshair;
                else
                {
                    var map = new Dictionary<string, string>();
                    if (_crosshairImages)
                        foreach (var f in chosen.Layers.Where(l => l.Kind == LayerKind.Image && l.ImageFile is { } x && CrosshairImages.Exists(x)).Select(l => l.ImageFile!).Distinct())
                        {
                            if (_uploads.Count >= PostRules.MaxImages) break;
                            var r = await CommunityClient.UploadAsync(await File.ReadAllBytesAsync(CrosshairImages.PathOf(f)), "image/png");
                            _uploads.Add(r.Image);
                            input.ImageIds.Add(r.Image.Id);
                            map[f] = r.Image.Id;
                        }
                    input.Content.Crosshair = CommunityImport.CrosshairForPost(chosen, f => map.GetValueOrDefault(f));
                }
            }
        }
        catch (CommunityApiException e) { _publishErrors = [e.Message]; Render(); return; }

        // Same rules as the server, for instant feedback (the server checks again).
        var check = JsonSerializer.Deserialize<PostInput>(JsonSerializer.Serialize(input, Api.Json), Api.Json)!;
        var problems = PostRules.Validate(check);
        if (problems.Count > 0) { _publishErrors = problems; Render(); return; }

        try
        {
            var saved = _editingId is null
                ? await CommunityClient.Post<PostDetail>("api/v1/posts", input)
                : await CommunityClient.Put<PostDetail>($"api/v1/posts/{_editingId}", input);
            MainWindow.Current?.ShowToast(_editingId is null ? "Published." : "Saved.", "Success");
            ResetEditor();
            _detail = saved;
            Render();
        }
        catch (CommunityApiException e)
        {
            _publishErrors = e.Problems.Count > 0 ? e.Problems : [e.Message];
            Render();
        }
    }

    // ---- moderation ---------------------------------------------------------------------------------------------------

    private AdminQueue? _queue;

    private void RenderModeration()
    {
        if (CommunityClient.Me?.IsAdmin != true) { _body.Children.Add(Note("Moderators only.", "Danger")); return; }
        if (_queue is null) { _body.Children.Add(Ui.Card(Ui.Loading("Loading the review queue…"))); _ = LoadQueueAsync(); return; }
        var q = _queue;
        _body.Children.Add(Ui.Row(Ui.Btn("Refresh", async (_, _) => { _queue = null; await LoadQueueAsync(); }, "GhostButton", new Thickness(0, 0, 0, 8))));

        var imgs = new StackPanel();
        imgs.Children.Add(Ui.Section($"Images waiting for review ({q.PendingImages.Count})", "Approve only gameplay, settings or crosshair images that follow the guidelines."));
        var wrap = new WrapPanel();
        foreach (var i in q.PendingImages)
        {
            var cell = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 12, 12) };
            var im = new Image { Height = 130, Stretch = Stretch.Uniform };
            _ = LoadInto(im, i.Image.ThumbUrl, 440);
            cell.Children.Add(new Border { Child = im, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)), CornerRadius = new CornerRadius(6) });
            cell.Children.Add(Ui.Muted($"{i.Owner} · {Ago(i.CreatedUtc)}", 11.5));
            cell.Children.Add(Ui.Muted(i.PostTitle is null ? "Not attached to a post yet" : "Post: " + i.PostTitle, 11.5));
            var id = i.Image.Id;
            cell.Children.Add(Ui.Row(
                Ui.Btn("Approve", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/v1/admin/images/{id}/approve"); await LoadQueueAsync(); }), "PrimaryButton"),
                Ui.Btn("Reject", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/v1/admin/images/{id}/reject"); await LoadQueueAsync(); }), "DangerButton")));
            wrap.Children.Add(cell);
        }
        if (q.PendingImages.Count == 0) imgs.Children.Add(Ui.Muted("Nothing to review.", 12));
        imgs.Children.Add(wrap);
        _body.Children.Add(Ui.Card(imgs, new Thickness(0, 0, 0, 12)));

        var reps = new StackPanel();
        reps.Children.Add(Ui.Section($"Open reports ({q.Reports.Count})"));
        foreach (var r in q.Reports)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
            var postId = r.PostId; var rid = r.Id;
            var acts = Ui.Row(
                Ui.Btn("Open", async (_, _) => await OpenAsync(postId), "BaseButton"),
                Ui.Btn("Dismiss", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/v1/admin/reports/{rid}/dismiss"); await LoadQueueAsync(); }), "GhostButton"),
                Ui.Btn("Remove post", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/v1/admin/posts/{postId}/remove", new { note = r.Reason }); await LoadQueueAsync(); }), "DangerButton"));
            DockPanel.SetDock(acts, Dock.Right);
            row.Children.Add(acts);
            var t = new StackPanel();
            t.Children.Add(Ui.Text($"{r.PostTitle}  ({r.PostStatus}, {r.OpenReportsOnPost} open report{(r.OpenReportsOnPost == 1 ? "" : "s")})", 13, FontWeights.SemiBold));
            t.Children.Add(Ui.Muted($"{r.Reason} · by {r.Reporter} · {Ago(r.CreatedUtc)}{(r.Details.Length > 0 ? " · " + r.Details : "")}", 12));
            row.Children.Add(t);
            reps.Children.Add(row);
        }
        if (q.Reports.Count == 0) reps.Children.Add(Ui.Muted("No open reports.", 12));
        _body.Children.Add(Ui.Card(reps, new Thickness(0, 0, 0, 12)));

        var hidden = new StackPanel();
        hidden.Children.Add(Ui.Section($"Hidden after reports ({q.HiddenPosts.Count})", "Restore if the reports were wrong, or remove."));
        foreach (var p in q.HiddenPosts)
        {
            var id = p.Id;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var acts = Ui.Row(
                Ui.Btn("Open", async (_, _) => await OpenAsync(id)),
                Ui.Btn("Restore", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/v1/admin/posts/{id}/restore"); await LoadQueueAsync(); }), "PrimaryButton"),
                Ui.Btn("Remove", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/v1/admin/posts/{id}/remove", new { note = "After review" }); await LoadQueueAsync(); }), "DangerButton"));
            DockPanel.SetDock(acts, Dock.Right);
            row.Children.Add(acts);
            row.Children.Add(Ui.Text($"{p.Title} · by {p.Author.Login}", 13));
            hidden.Children.Add(row);
        }
        if (q.HiddenPosts.Count == 0) hidden.Children.Add(Ui.Muted("None.", 12));
        _body.Children.Add(Ui.Card(hidden));
    }

    private async Task LoadQueueAsync()
    {
        try { _queue = await CommunityClient.Get<AdminQueue>("api/v1/admin/queue"); }
        catch (CommunityApiException e) { _queue = new AdminQueue(); Dialog.Show("Moderation", e.Message, DialogKind.Warning); }
        if (_tab == "moderation" && _detail is null) Render();
    }
}
