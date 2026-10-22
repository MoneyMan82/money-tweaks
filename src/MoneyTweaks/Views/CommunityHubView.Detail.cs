using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoneyTweaks.Community;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Community;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Profiles;

namespace MoneyTweaks.Views;

public sealed partial class CommunityHubView
{
    private async Task OpenAsync(string id)
    {
        await Try(async () =>
        {
            _detail = await CommunityClient.Get<PostDetail>($"api/community/posts/{id}");
            Render();
        });
    }

    private async Task ReloadDetailAsync()
    {
        if (_detail is null) return;
        try { _detail = await CommunityClient.Get<PostDetail>($"api/community/posts/{_detail.Id}"); }
        catch (CommunityApiException e) when (e.Status == 404) { _detail = null; await LoadListAsync(true); return; }
        Render();
    }

    private void RenderDetail()
    {
        var p = _detail!;
        _body.Children.Add(Ui.Row(Ui.Btn("← Back", async (_, _) => { _detail = null; await LoadListAsync(true); }, "GhostButton")));

        // Header
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = p.Title, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap });
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        meta.Children.Add(Ui.Muted($"{p.Game} · {p.Category} · by ", 12.5));
        var author = Ui.Btn(p.Author.Login, async (_, _) => { _detail = null; _tab = "discover"; _author = p.Author.Login; await LoadListAsync(true); }, "GhostButton");
        author.Padding = new Thickness(2, 0, 2, 0);
        meta.Children.Add(author);
        meta.Children.Add(Ui.Muted($" ({p.Author.PostCount} post{(p.Author.PostCount == 1 ? "" : "s")}, {p.Author.LikesReceived} likes) · published {Ago(p.CreatedUtc)}{(p.UpdatedUtc > p.CreatedUtc.AddMinutes(1) ? $" · edited {Ago(p.UpdatedUtc)}" : "")}", 12.5));
        head.Children.Add(meta);
        var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var t in p.Tags)
        {
            var tag = t;
            var b = Ui.Btn("#" + t, async (_, _) => { _detail = null; _tab = "discover"; _tag = tag; await LoadListAsync(true); }, "GhostButton", new Thickness(0, 0, 4, 0));
            tags.Children.Add(b);
        }
        head.Children.Add(tags);
        if (p.Status == "hidden") head.Children.Add(Note("Hidden: several people reported this post. Only you and moderators can see it until a moderator reviews it.", "Gold"));
        if (p.Status == "removed") head.Children.Add(Note("A moderator removed this post. Only you and moderators can see it.", "Danger"));
        if (p.Images.Any(i => i.Status == "pending")) head.Children.Add(Note("Some images are waiting for a moderator. Until then only you (and moderators) can see them.", "Gold"));
        head.Children.Add(ActionBar(p));
        _body.Children.Add(Ui.Card(head, new Thickness(0, 8, 0, 12)));

        // Images
        var shownImages = p.Images.Where(i => i.Kind != "crosshair-preview").ToList(); // the crosshair is drawn live below
        if (shownImages.Count > 0)
        {
            var gallery = new WrapPanel();
            foreach (var img in shownImages)
            {
                var im = new Image { Stretch = Stretch.Uniform, MaxHeight = 220, MaxWidth = 360 };
                _ = LoadInto(im, img.ThumbUrl, 0);
                var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
                var frame = new Border { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = im, Cursor = System.Windows.Input.Cursors.Hand };
                var full = img;
                frame.MouseLeftButtonUp += async (_, _) => await ShowFullAsync(full);
                cell.Children.Add(frame);
                var label = img.Kind == "crosshair-preview" ? "Crosshair preview (generated)" : img.Status == "pending" ? "Awaiting moderation" : $"{img.Width}×{img.Height}";
                cell.Children.Add(Ui.Muted(label, 11.5));
                if (CommunityClient.Me?.IsAdmin == true && img.Status == "pending")
                {
                    var id = img.Id;
                    cell.Children.Add(Ui.Row(
                        Ui.Btn("Approve", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/community/admin/images/{id}/approve"); await ReloadDetailAsync(); }), "PrimaryButton"),
                        Ui.Btn("Reject", async (_, _) => await Try(async () => { await CommunityClient.Post<bool>($"api/community/admin/images/{id}/reject"); await ReloadDetailAsync(); }), "DangerButton")));
                }
                gallery.Children.Add(cell);
            }
            _body.Children.Add(Ui.Card(gallery, new Thickness(0, 0, 0, 12)));
        }

        if (p.Description.Length > 0)
        {
            var d = new StackPanel();
            d.Children.Add(Ui.Section("Description"));
            d.Children.Add(new TextBox
            {
                Text = p.Description, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = Ui.Brush("Text"), FontSize = 13.5, Padding = new Thickness(0),
            });
            _body.Children.Add(Ui.Card(d, new Thickness(0, 0, 0, 12)));
        }

        var c = p.Content;
        if (c.Settings is { } s) _body.Children.Add(Ui.Card(SettingsSection(s), new Thickness(0, 0, 0, 12)));
        if (c.Crosshair is { ValueKind: JsonValueKind.Object } x) _body.Children.Add(Ui.Card(CrosshairSection(p, x), new Thickness(0, 0, 0, 12)));
        if (c.GamingProfile is { } gp) _body.Children.Add(Ui.Card(BundleSection(p, gp, profile: true), new Thickness(0, 0, 0, 12)));
        if (c.Preset is { } pr) _body.Children.Add(Ui.Card(BundleSection(p, pr, profile: false), new Thickness(0, 0, 0, 12)));
        if (c.Hardware is not null || c.Benchmark is not null) _body.Children.Add(Ui.Card(PcSection(c), new Thickness(0, 0, 0, 12)));
    }

    private UIElement ActionBar(PostDetail p)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var row = new WrapPanel();
        bool signed = CommunityClient.Me is not null, own = CommunityClient.Me?.User.Id == p.Author.Id;
        Button B(string text, Func<Task> act, string style = "BaseButton", bool enabled = true, string? tip = null)
        {
            var b = Ui.Btn(text, async (_, _) => await Try(act), style, new Thickness(0, 0, 8, 8));
            b.IsEnabled = enabled;
            b.ToolTip = tip ?? (signed ? null : "Sign in to do this");
            return b;
        }
        if (p.Status == "visible")
        {
            row.Children.Add(B((p.Liked ? "♥ Liked" : "♡ Like") + $"  {p.Likes}", async () =>
            {
                _detail = p.Liked ? await CommunityClient.Delete<PostDetail>($"api/community/posts/{p.Id}/like") : await CommunityClient.Put<PostDetail>($"api/community/posts/{p.Id}/like");
                Render();
            }, p.Liked ? "PrimaryButton" : "BaseButton", signed));
            row.Children.Add(B(p.Bookmarked ? "Bookmarked" : "Bookmark", async () =>
            {
                _detail = p.Bookmarked ? await CommunityClient.Delete<PostDetail>($"api/community/posts/{p.Id}/bookmark") : await CommunityClient.Put<PostDetail>($"api/community/posts/{p.Id}/bookmark");
                Render();
            }, p.Bookmarked ? "PrimaryButton" : "BaseButton", signed));
        }
        if (CanGet(p) && p.Status == "visible")
        {
            var get = Ui.Btn("⤓  Get", async (_, _) => await GetAsync(p), "PrimaryButton", new Thickness(0, 0, 8, 8));
            get.ToolTip = "Add this post's " + string.Join(", ", p.Sections.Where(x => x is "Crosshair" or "Gaming profile" or "Preset").Select(x => x.ToLowerInvariant())) + " to your library. Nothing is applied.";
            row.Children.Insert(0, get);
            if (_lastAdded is { } page)
                row.Children.Insert(1, Ui.Btn(page == "crosshair" ? "Open Custom Crosshair" : page == "profiles" ? "Open Gaming Profiles" : "Open Presets", (_, _) => MainWindow.Current?.Navigate(page), "BaseButton", new Thickness(0, 0, 8, 8)));
        }
        if (own)
        {
            if (p.CanEdit) row.Children.Add(B("Edit", () => { BeginEdit(p); return Task.CompletedTask; }));
            row.Children.Add(B("Delete", async () =>
            {
                if (!Dialog.Confirm("Delete this post?", $"\"{p.Title}\" and its images will be deleted for everyone. This can't be undone. (Images you uploaded today still count toward today's limit.)", "Delete", DialogKind.Warning)) return;
                await CommunityClient.Delete<bool>($"api/community/posts/{p.Id}");
                MainWindow.Current?.ShowToast("Post deleted.", "Accent");
                _detail = null;
                await LoadListAsync(true);
            }, "DangerButton"));
        }
        if (CommunityClient.Me?.IsAdmin == true)
        {
            if (p.Status != "removed") row.Children.Add(B("Remove (moderator)", async () =>
            {
                if (!Dialog.Confirm("Remove this post?", "It disappears for everyone except its author and moderators. Its images are deleted after the retention period.", "Remove", DialogKind.Warning)) return;
                await CommunityClient.Post<bool>($"api/community/admin/posts/{p.Id}/remove", new { note = "Removed from the app" });
                await ReloadDetailAsync();
            }, "DangerButton"));
            if (p.Status != "visible") row.Children.Add(B("Restore (moderator)", async () => { await CommunityClient.Post<bool>($"api/community/admin/posts/{p.Id}/restore"); await ReloadDetailAsync(); }));
            if (!own) row.Children.Add(B("Suspend author", async () =>
            {
                if (!Dialog.Confirm("Suspend this account?", $"{p.Author.Login} will be signed out and can't post, upload or react. Their posts are hidden. You can lift it later.", "Suspend", DialogKind.Warning)) return;
                await CommunityClient.Post<bool>($"api/community/admin/users/{p.Author.Id}", new { banned = true, reason = "Suspended from the app" });
                MainWindow.Current?.ShowToast($"{p.Author.Login} suspended.", "Accent");
            }, "GhostButton"));
        }
        sp.Children.Add(row);

        if (p.Status == "visible" && !own)
        {
            var help = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            int votes = p.HelpfulYes + p.HelpfulNo;
            var q = Ui.Muted($"Did this help?  {(votes == 0 ? "No ratings yet" : $"{p.HelpfulYes} of {votes} said yes")}   ", 12.5);
            q.VerticalAlignment = VerticalAlignment.Center;
            help.Children.Add(q);
            help.Children.Add(B(p.MyHelpful == true ? "✓ Yes" : "Yes", async () => { _detail = await CommunityClient.Put<PostDetail>($"api/community/posts/{p.Id}/helpful", new { helpful = p.MyHelpful == true ? (bool?)null : true }); Render(); }, p.MyHelpful == true ? "PrimaryButton" : "BaseButton", signed));
            help.Children.Add(B(p.MyHelpful == false ? "✓ No" : "No", async () => { _detail = await CommunityClient.Put<PostDetail>($"api/community/posts/{p.Id}/helpful", new { helpful = p.MyHelpful == false ? (bool?)null : false }); Render(); }, p.MyHelpful == false ? "PrimaryButton" : "BaseButton", signed));
            help.Children.Add(B(p.Reported ? "Reported" : "Report…", () => { ShowReport(sp, p); return Task.CompletedTask; }, "GhostButton", signed && !p.Reported));
            sp.Children.Add(help);
        }
        return sp;
    }

    private void ShowReport(StackPanel host, PostDetail p)
    {
        if (host.Children.OfType<Border>().Any(b => b.Tag as string == "report")) return;
        var form = new StackPanel();
        form.Children.Add(Ui.Section("Report this post", "Moderators review every report. Several reports hide the post until then."));
        var reason = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var r in CommunityClient.Config?.ReportReasons ?? Taxonomy.ReportReasons) reason.Items.Add(r);
        reason.SelectedIndex = 0;
        form.Children.Add(reason);
        var details = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 60, MaxLength = 500, Tag = "What's wrong? (optional)", Margin = new Thickness(0, 0, 0, 6) };
        form.Children.Add(details);
        var card = new Border { Tag = "report", Child = form, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = Ui.Brush("Surface"), Margin = new Thickness(0, 8, 0, 0) };
        form.Children.Add(Ui.Row(
            Ui.Btn("Send report", async (_, _) => await Try(async () =>
            {
                await CommunityClient.Post<bool>($"api/community/posts/{p.Id}/report", new ReportInput { Reason = reason.SelectedItem as string ?? "", Details = details.Text });
                MainWindow.Current?.ShowToast("Thanks. A moderator will look at it.", "Success");
                await ReloadDetailAsync();
            }), "PrimaryButton"),
            Ui.Btn("Cancel", (_, _) => host.Children.Remove(card), "GhostButton")));
        host.Children.Add(card);
    }

    private async Task ShowFullAsync(ImageInfo img)
    {
        var bytes = await CommunityClient.GetBytesAsync(img.Url);
        if (bytes is null) return;
        var bi = new BitmapImage();
        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new System.IO.MemoryStream(bytes); bi.EndInit(); bi.Freeze();
        var w = new Window
        {
            Title = "Image", Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0B)),
            Width = Math.Min(1400, img.Width + 40), Height = Math.Min(900, img.Height + 60),
            Content = new Image { Source = bi, Stretch = Stretch.Uniform, Margin = new Thickness(10) },
        };
        RenderOptions.SetBitmapScalingMode((Image)w.Content, img.Kind == "crosshair-preview" ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        w.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) w.Close(); };
        w.ShowDialog();
    }

    // ---- sections -----------------------------------------------------------------------------------------------------

    private static UIElement KeyValues(IEnumerable<(string k, string v)> rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int i = 0;
        foreach (var (k, v) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = Ui.Muted(k, 12.5); Grid.SetRow(kt, i);
            var vt = Ui.Text(v, 13); vt.TextWrapping = TextWrapping.Wrap; Grid.SetRow(vt, i); Grid.SetColumn(vt, 1);
            kt.Margin = vt.Margin = new Thickness(0, 2, 0, 2);
            g.Children.Add(kt); g.Children.Add(vt);
            i++;
        }
        return g;
    }

    private static UIElement SettingsSection(GameSettings s)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Game settings", "Shown for reference: Money Tweaks doesn't change in-game settings for you."));
        var rows = new List<(string, string)>();
        if (s.ResolutionWidth is { } w && s.ResolutionHeight is { } h) rows.Add(("Resolution", $"{w}×{h}{(s.Stretched == true ? " (stretched)" : "")}"));
        if (s.RefreshHz is { } hz) rows.Add(("Refresh rate", $"{hz} Hz"));
        if (s.FpsCap is { } cap) rows.Add(("FPS cap", cap == 0 ? "Unlimited" : cap.ToString(CultureInfo.CurrentCulture)));
        if (s.Sensitivity is { } se) rows.Add(("Sensitivity", se.ToString(CultureInfo.CurrentCulture)));
        if (s.AdsSensitivity is { } ads) rows.Add(("ADS sensitivity", ads.ToString(CultureInfo.CurrentCulture)));
        if (s.MouseDpi is { } dpi) rows.Add(("Mouse DPI", dpi.ToString(CultureInfo.CurrentCulture)));
        foreach (var g in s.Graphics) rows.Add((g.Name, g.Value));
        sp.Children.Add(KeyValues(rows));
        if (s.Keybinds.Count > 0)
        {
            var kb = Ui.Section("Keybinds");
            kb.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(kb);
            var wrap = new WrapPanel();
            foreach (var k in s.Keybinds)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 6) };
                cell.Children.Add(Ui.Muted(k.Action + "  ", 12.5));
                cell.Children.Add(Ui.KeyCaps(k.Key));
                wrap.Children.Add(cell);
            }
            sp.Children.Add(wrap);
        }
        if (s.Notes is { Length: > 0 } n) { var t = Ui.Muted(n, 12.5); t.Margin = new Thickness(0, 10, 0, 0); t.TextWrapping = TextWrapping.Wrap; sp.Children.Add(t); }
        sp.Children.Add(Ui.Row(Ui.Btn("Copy as text", (_, _) =>
        {
            try { Clipboard.SetText(CommunityImport.SettingsText(s)); MainWindow.Current?.ShowToast("Settings copied.", "Success"); } catch { }
        }, "BaseButton", new Thickness(0, 10, 0, 0))));
        return sp;
    }

    private UIElement CrosshairSection(PostDetail p, JsonElement x)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Crosshair", "Live preview drawn by Money Tweaks from the shared settings."));
        CrosshairProfile? profile = null;
        try { profile = x.Deserialize<CrosshairProfile>(new JsonSerializerOptions(Api.Json) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }); } catch { }
        if (profile is null) { sp.Children.Add(Ui.Muted("This crosshair couldn't be read.", 12.5)); return sp; }
        CrosshairSanitizer.Clean(profile);
        foreach (var l in profile.Layers.Where(l => l.Kind == LayerKind.Image)) l.ImageFile = null; // images show in the generated preview above
        var vis = new CrosshairVisual { Profile = profile, Zoom = FitZoom(profile, 260), Height = 260 };
        sp.Children.Add(new Border { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(8), Child = vis, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 8) });
        sp.Children.Add(Ui.Muted($"{profile.Layers.Count} layer(s){(profile.Firing.Enabled ? " · firing animation" : "")}{(profile.Layers.Any(l => l.Kind == LayerKind.Image) ? " · includes an image layer" : "")}", 12));
        sp.Children.Add(Ui.Row(Ui.Btn("Get crosshair", async (_, _) => await GetAsync(p, profile: false, preset: false), "PrimaryButton", new Thickness(0, 10, 0, 0))));
        return sp;
    }

    private UIElement BundleSection(PostDetail p, MoneyTweaks.Community.TweakBundle b, bool profile)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section(profile ? "Gaming profile: " + b.Name : "Preset: " + b.Name, b.Description.Length > 0 ? b.Description : null));
        var rows = new List<(string, string)>();
        if (profile)
        {
            if (b.Power is { } pw and not "Keep") rows.Add(("Power plan", pw == "HighPerformance" ? "High performance" : pw));
            if (b.TimerResolutionMs is { } ms) rows.Add(("Timer resolution", $"{ms.ToString(CultureInfo.CurrentCulture)} ms"));
            if (b.RaiseGamePriority) rows.Add(("Game priority", "High while the game runs"));
            if (b.GameExes.Count > 0) rows.Add(("Games", string.Join(", ", b.GameExes)));
        }
        if (rows.Count > 0) sp.Children.Add(KeyValues(rows));
        var lines = CommunityImport.Preview(b, profile);
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var l in lines)
        {
            var r = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var mark = new TextBlock { Text = l.WillImport ? "✓" : "✕", Width = 20, Foreground = l.WillImport ? Ui.Brush("Success") : Ui.Brush("Subtle") };
            r.Children.Add(mark);
            if (l.Caution) { var t = Ui.Tag("Caution", "Gold"); DockPanel.SetDock(t, Dock.Right); r.Children.Add(t); }
            var name = new StackPanel();
            name.Children.Add(Ui.Text(l.Name, 13));
            if (l.Note is not null && !l.WillImport) name.Children.Add(Ui.Muted(l.Note, 11.5));
            r.Children.Add(name);
            list.Children.Add(r);
        }
        sp.Children.Add(list);
        sp.Children.Add(Ui.Muted($"{lines.Count(l => l.WillImport)} of {lines.Count} tweaks will be added. Get adds the {(profile ? "profile" : "preset")} to your library; nothing changes on your PC until you {(profile ? "activate" : "apply")} it yourself.", 12));
        sp.Children.Add(Ui.Row(Ui.Btn(profile ? "Get gaming profile" : "Get preset", async (_, _) => await GetAsync(p, crosshair: false, profile: profile, preset: !profile), "PrimaryButton", new Thickness(0, 10, 0, 0))));
        return sp;
    }

    private static UIElement PcSection(PostContent c)
    {
        var sp = new StackPanel();
        if (c.Hardware is { } h)
        {
            sp.Children.Add(Ui.Section("Author's PC", "As entered by the author."));
            var rows = new List<(string, string)>();
            if (h.Cpu is { } cpu) rows.Add(("CPU", cpu));
            if (h.Gpu is { } gpu) rows.Add(("GPU", gpu));
            if (h.RamGb is { } ram) rows.Add(("Memory", $"{ram} GB"));
            if (h.Monitor is { } mon) rows.Add(("Monitor", mon));
            sp.Children.Add(KeyValues(rows));
        }
        if (c.Benchmark is { } b)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, c.Hardware is null ? 0 : 14, 0, 6) };
            head.Children.Add(Ui.Text("Benchmark", 15, FontWeights.SemiBold));
            var t = Ui.Tag("User-reported, not verified", "Gold");
            t.Margin = new Thickness(10, 0, 0, 0);
            head.Children.Add(t);
            sp.Children.Add(head);
            var rows = new List<(string, string)>();
            if (b.AverageFps is { } a) rows.Add(("Average FPS", a.ToString("0.#", CultureInfo.CurrentCulture)));
            if (b.OnePercentLowFps is { } l) rows.Add(("1% low FPS", l.ToString("0.#", CultureInfo.CurrentCulture)));
            if (b.Scenario is { } s) rows.Add(("Where", s));
            sp.Children.Add(KeyValues(rows));
            sp.Children.Add(Ui.Muted("Numbers depend on the whole PC, drivers and the match. Expect different results on yours.", 11.5));
        }
        return sp;
    }

    /// <summary>Whole-number magnification that makes the crosshair fill about 80% of a box this tall (sharp, like the overlay up close).</summary>
    public static double FitZoom(CrosshairProfile p, double box)
    {
        var ext = CrosshairGeometry.Extent(p, 1);
        return Math.Clamp(Math.Floor(box * 0.8 / (2 * ext)), 1, 16);
    }

    /// <summary>A crosshair drawn by the app from shared settings, or null when there isn't a valid one.</summary>
    public static UIElement? CrosshairPreview(JsonElement? json, double height)
    {
        if (json is not { ValueKind: JsonValueKind.Object } x) return null;
        CrosshairProfile? profile;
        try { profile = x.Deserialize<CrosshairProfile>(new JsonSerializerOptions(Api.Json) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }); }
        catch { return null; }
        if (profile is null) return null;
        CrosshairSanitizer.Clean(profile);
        foreach (var l in profile.Layers.Where(l => l.Kind == LayerKind.Image)) l.ImageFile = null; // only local images can be drawn
        return new CrosshairVisual { Profile = profile, Zoom = FitZoom(profile, height), Height = height };
    }

    // ---- get (import) ------------------------------------------------------------------------------------------------

    /// <summary>Whether a post has something you can add to your library.</summary>
    public static bool CanGet(PostSummary p) => p.Sections.Any(s => s is "Crosshair" or "Gaming profile" or "Preset");

    /// <summary>
    /// One click: adds the post's crosshair, gaming profile and/or preset to your library. Nothing is applied or activated,
    /// so there's no confirmation; tweaks that don't exist in this version are skipped and listed.
    /// </summary>
    private async Task GetAsync(PostDetail p, bool crosshair = true, bool profile = true, bool preset = true)
    {
        await Try(async () =>
        {
            var added = new List<(string what, string page)>();
            var notes = new List<string>();
            if (crosshair && p.Content.Crosshair is { ValueKind: JsonValueKind.Object } x)
            {
                var (cp, n) = await CommunityImport.ToCrosshairAsync(x, CrosshairService.Data.Profiles,
                    id => p.Images.FirstOrDefault(i => i.Id == id && i.Status == "approved") is { } img ? CommunityClient.GetBytesAsync(img.Url) : Task.FromResult<byte[]?>(null));
                notes.AddRange(n);
                if (cp is not null) { CrosshairService.Data.Profiles.Add(cp); CrosshairService.SaveNow(); added.Add(($"crosshair \"{cp.Name}\"", "crosshair")); }
            }
            if (profile && p.Content.GamingProfile is { } gb)
            {
                var file = ProfileStore.Load();
                var (gp, n) = CommunityImport.ToGamingProfile(gb, ProfileStore.All(file));
                notes.AddRange(n);
                if (gp is not null) { file.Profiles.Add(gp); ProfileStore.Save(file); added.Add(($"gaming profile \"{gp.Name}\"", "profiles")); }
            }
            if (preset && p.Content.Preset is { } pb)
            {
                var file = PresetStore.Load();
                var (pr, n) = CommunityImport.ToPreset(pb, PresetStore.BuiltIns.Concat(file.Presets));
                notes.AddRange(n);
                if (pr is not null) { file.Presets.Add(pr); PresetStore.Save(file); added.Add(($"preset \"{pr.Name}\"", "presets")); }
            }
            if (added.Count == 0) { Dialog.Show("Nothing added", notes.Count > 0 ? string.Join("\n", notes) : "This post has nothing to add to your library.", DialogKind.Warning); return; }
            await Recorded(p);
            var msg = "Added " + string.Join(" and ", added.Select(a => a.what)) + " to your library. Nothing was applied: open it to use it.";
            if (notes.Count > 0) msg += " (" + string.Join(" ", notes) + ")";
            MainWindow.Current?.ShowToast(msg, notes.Count > 0 ? "Gold" : "Success");
            _lastAdded = added[0].page;
            if (_detail?.Id == p.Id) Render();
        });
    }

    private string? _lastAdded;

    private static async Task Recorded(PostDetail p)
    {
        if (CommunityClient.SignedIn) try { await CommunityClient.Post<bool>($"api/community/posts/{p.Id}/imported"); } catch { }
    }
}
