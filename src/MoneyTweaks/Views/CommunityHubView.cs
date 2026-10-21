using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoneyTweaks.Community;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Community Hub: discover, rate and import settings, gaming profiles, presets and crosshairs; publish your own with
/// screenshots. Everything goes through <see cref="CommunityClient"/>; all rules (quota, permissions, moderation) are
/// enforced by the server, the page only explains them.
/// </summary>
public sealed partial class CommunityHubView : UserControl, IPageLifecycle
{
    private readonly StackPanel _account = new(), _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) }, _body = new();
    private string _tab = "discover";
    private string? _q, _game, _category, _tag, _author;
    private string _sort = "new";
    private int _page = 1;
    private readonly List<PostSummary> _items = [];
    private int _total;
    private PostDetail? _detail;
    private DeviceSignIn? _device;
    private CancellationTokenSource? _signInCts;
    private string? _error;
    private bool _busy;
    private static readonly Dictionary<string, BitmapSource> Thumbs = [];

    public CommunityHubView()
    {
        var (root, body) = Ui.Page("Community Hub",
            "Settings, gaming profiles, presets and crosshairs shared by other players. Preview anything before importing: imports go into your library and are never applied by themselves.",
            null, eyebrow: "Gaming tools", icon: "\uE716");
        body.Children.Add(_account);
        body.Children.Add(_tabs);
        body.Children.Add(_body);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _detail is not null) { _detail = null; Render(); e.Handled = true; } };
    }

    public void OnShown()
    {
        CommunityClient.Changed += OnClientChanged;
        _ = StartAsync();
    }

    public void OnHidden()
    {
        CommunityClient.Changed -= OnClientChanged;
    }

    private void OnClientChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnClientChanged); return; }
        RenderAccount();
    }

    private async Task StartAsync()
    {
        _error = null;
        if (CommunityClient.BaseUri is null) { Render(); return; }
        _busy = true;
        Render();
        try
        {
            await CommunityClient.LoadConfigAsync();
            await CommunityClient.RefreshMeAsync();
            _busy = false;
            if (ApplyPendingShare()) { Render(); return; }
            await LoadListAsync(reset: true);
        }
        catch (CommunityApiException e) { _busy = false; _error = e.Message; Render(); }
    }

    // ---- shell ------------------------------------------------------------------------------------------------------

    private void Render()
    {
        RenderAccount();
        RenderTabs();
        _body.Children.Clear();
        if (CommunityClient.BaseUri is null) { RenderNoServer(); return; }
        if (_busy) { _body.Children.Add(Ui.Card(Ui.Loading("Connecting to the Community Hub…"))); return; }
        if (_error is not null && CommunityClient.Config is null)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Empty("\uE716", "Couldn't reach the Community Hub", _error));
            sp.Children.Add(Ui.Row(Ui.Btn("Try again", async (_, _) => await StartAsync(), "PrimaryButton"), Ui.Btn("Server settings", (_, _) => { _tab = "guidelines"; Render(); })));
            _body.Children.Add(Ui.Card(sp));
            if (_tab == "guidelines") RenderGuidelines();
            return;
        }
        if (_detail is not null) { RenderDetail(); return; }
        switch (_tab)
        {
            case "publish": RenderPublish(); break;
            case "moderation": RenderModeration(); break;
            case "guidelines": RenderGuidelines(); break;
            default: RenderList(); break;
        }
    }

    private void RenderTabs()
    {
        _tabs.Children.Clear();
        if (CommunityClient.BaseUri is null) return;
        var tabs = new List<(string id, string label)> { ("discover", "Discover"), ("bookmarks", "Bookmarks"), ("mine", "My posts"), ("publish", _editingId is null ? "Publish" : "Edit post") };
        if (CommunityClient.Me?.IsAdmin == true) tabs.Add(("moderation", "Moderation"));
        tabs.Add(("guidelines", "Guidelines & server"));
        foreach (var (id, label) in tabs)
        {
            var t = id;
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "hubtab", IsChecked = _tab == id && _detail is null, Margin = new Thickness(0, 0, 6, 0) };
            rb.Click += async (_, _) =>
            {
                _detail = null;
                _tab = t;
                if (t is "discover" or "bookmarks" or "mine") await LoadListAsync(reset: true);
                else Render();
            };
            _tabs.Children.Add(rb);
        }
    }

    private void RenderNoServer()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Empty("\uE716", "The Community Hub isn't online yet",
            "Sharing needs a server, and the official Money Tweaks Community Hub hasn't been launched. Nothing is uploaded until it is. If you run your own Community Hub server, enter its address under Guidelines & server."));
        _body.Children.Add(Ui.Card(sp, new Thickness(0, 0, 0, 12)));
        RenderGuidelines();
    }

    // ---- account bar --------------------------------------------------------------------------------------------------

    private void RenderAccount()
    {
        _account.Children.Clear();
        if (CommunityClient.BaseUri is null || CommunityClient.Config is null) return;
        var bar = new DockPanel();
        var me = CommunityClient.Me;
        if (me is null)
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            if (_device is null)
            {
                if (!string.IsNullOrEmpty(CommunityClient.Config.GitHubClientId))
                    right.Children.Add(Ui.Btn("Sign in with GitHub", async (_, _) => await StartGitHubAsync(), "PrimaryButton"));
                if (CommunityClient.Config.DevSignIn && CommunityClient.BaseUri.IsLoopback)
                {
                    var box = new TextBox { Width = 140, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "test user name", ToolTip = "Local test server only" };
                    right.Children.Add(box);
                    right.Children.Add(Ui.Btn("Test sign-in", async (_, _) => await Try(async () => { await CommunityClient.SignInDevAsync(box.Text); await LoadListAsync(true); })));
                }
            }
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var text = new StackPanel();
            text.Children.Add(Ui.Text("Browsing as a guest", 14, FontWeights.SemiBold));
            text.Children.Add(Ui.Muted(_device is null
                ? "Sign in with GitHub to publish, upload screenshots, like, bookmark, rate or report. Money Tweaks gets only your public GitHub name and picture; no password, email or repositories."
                : "", 12));
            bar.Children.Add(text);
        }
        else
        {
            var right = Ui.Row(Ui.Btn("Sign out", async (_, _) => { await CommunityClient.SignOutAsync(); await LoadListAsync(true); }, "GhostButton"));
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var text = new StackPanel();
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(Ui.Text(me.User.Login, 14, FontWeights.SemiBold));
            if (me.IsAdmin) { var t = Ui.Tag("Moderator", "Violet"); t.Margin = new Thickness(8, 0, 0, 0); name.Children.Add(t); }
            if (me.Banned) { var t = Ui.Tag("Suspended", "Danger"); t.Margin = new Thickness(8, 0, 0, 0); name.Children.Add(t); }
            text.Children.Add(name);
            text.Children.Add(Ui.Muted(QuotaText(me.Quota), 12));
            bar.Children.Add(text);
        }
        if (CommunityClient.Config.NotConfigured.Count > 0)
            _account.Children.Add(Note("This Community Hub server isn't fully set up yet, so some things won't work: " + string.Join(", ", CommunityClient.Config.NotConfigured) +
                ". Browsing works; signing in or uploading may be refused until the owner finishes the setup.", "Gold"));
        _account.Children.Add(Ui.Card(bar, new Thickness(0, 0, 0, 12)));
        if (_device is { } d) _account.Children.Add(Ui.Card(DevicePanel(d), new Thickness(0, 0, 0, 12)));
    }

    public static string QuotaText(QuotaInfo q)
    {
        var left = q.ResetsAtUtc - DateTime.UtcNow;
        var when = left.TotalMinutes < 1 ? "in a moment" : left.TotalHours >= 1 ? $"in {(int)left.TotalHours} h {left.Minutes} min" : $"in {left.Minutes} min";
        return $"Image uploads today: {q.Remaining} of {q.Limit} left · resets {when} ({q.ResetsAtUtc.ToLocalTime():HH:mm} your time, 00:00 UTC)";
    }

    private UIElement DevicePanel(DeviceSignIn d)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Sign in with GitHub", "Open GitHub, sign in there and enter this code. Money Tweaks never sees your GitHub password."));
        sp.Children.Add(new TextBlock { Text = d.UserCode, FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Text"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 0, 8) });
        sp.Children.Add(Ui.Row(
            Ui.Btn("Open GitHub", (_, _) => { try { Process.Start(new ProcessStartInfo(d.VerificationUri) { UseShellExecute = true }); } catch { } }, "PrimaryButton"),
            Ui.Btn("Copy code", (_, _) => { try { Clipboard.SetText(d.UserCode); } catch { } }),
            Ui.Btn("Cancel", (_, _) => { _signInCts?.Cancel(); }, "GhostButton")));
        sp.Children.Add(Ui.Muted($"Waiting for GitHub… The code expires at {d.ExpiresUtc.ToLocalTime():HH:mm}.", 12));
        return sp;
    }

    private async Task StartGitHubAsync()
    {
        try
        {
            _device = await CommunityClient.StartGitHubSignInAsync();
            _signInCts = new CancellationTokenSource();
            RenderAccount();
            await CommunityClient.CompleteGitHubSignInAsync(_device, _signInCts.Token);
            MainWindow.Current?.ShowToast($"Signed in as {CommunityClient.Me?.User.Login}.", "Success");
            _device = null;
            await LoadListAsync(true);
        }
        catch (OperationCanceledException) { _device = null; RenderAccount(); }
        catch (CommunityApiException e) { _device = null; RenderAccount(); Dialog.Show("Sign-in didn't finish", e.Message, DialogKind.Warning); }
        catch (Exception e) { _device = null; RenderAccount(); Dialog.Show("Sign-in didn't finish", e.Message, DialogKind.Warning); }
    }

    /// <summary>Runs an action and turns failures into a readable message.</summary>
    private async Task Try(Func<Task> action)
    {
        try { await action(); }
        catch (CommunityApiException e)
        {
            var msg = e.Problems.Count > 1 ? string.Join("\n", e.Problems.Select(p => "• " + p)) : e.Message;
            if (e.Status == 401) msg = "Sign in first. " + (e.Code == "sign_in" ? "" : msg);
            Dialog.Show("Community Hub", msg, e.Status is 0 or >= 500 ? DialogKind.Error : DialogKind.Warning);
        }
    }

    // ---- lists ------------------------------------------------------------------------------------------------------

    private async Task LoadListAsync(bool reset)
    {
        if (CommunityClient.BaseUri is null || CommunityClient.Config is null) { Render(); return; }
        if (reset) { _page = 1; _items.Clear(); }
        try
        {
            string path = _tab switch
            {
                "bookmarks" when CommunityClient.SignedIn => $"api/community/me/bookmarks?page={_page}",
                "mine" when CommunityClient.SignedIn => $"api/community/me/posts?page={_page}",
                _ => "api/community/posts?" + string.Join("&", new[]
                {
                    $"page={_page}", "sort=" + _sort,
                    _q is { Length: > 0 } ? "q=" + Uri.EscapeDataString(_q) : null,
                    _game is not null ? "game=" + Uri.EscapeDataString(_game) : null,
                    _category is not null ? "category=" + Uri.EscapeDataString(_category) : null,
                    _tag is not null ? "tag=" + Uri.EscapeDataString(_tag) : null,
                    _author is not null ? "author=" + Uri.EscapeDataString(_author) : null,
                }.Where(x => x is not null)),
            };
            if (_tab is "bookmarks" or "mine" && !CommunityClient.SignedIn) { _items.Clear(); _total = 0; Render(); return; }
            var list = await CommunityClient.Get<PostList>(path);
            _items.AddRange(list.Items);
            _total = list.Total;
            _error = null;
        }
        catch (CommunityApiException e) { _error = e.Message; }
        Render();
    }

    private void RenderList()
    {
        if (_tab == "discover") _body.Children.Add(Ui.Card(Filters(), new Thickness(0, 0, 0, 12)));
        if (_error is not null) _body.Children.Add(Note(_error, "Danger"));
        if (_tab is "bookmarks" or "mine" && !CommunityClient.SignedIn)
        {
            _body.Children.Add(Ui.Card(Ui.Empty("\uE716", "Sign in to see this", _tab == "mine" ? "Your posts appear here after you sign in." : "Bookmark posts to find them again here.")));
            return;
        }
        if (_items.Count == 0)
        {
            _body.Children.Add(Ui.Card(Ui.Empty("\uE716",
                _tab switch { "mine" => "You haven't published anything yet", "bookmarks" => "No bookmarks yet", _ => _q is null && _game is null && _category is null && _tag is null && _author is null ? "No posts yet" : "Nothing matches" },
                _tab switch { "mine" => "Share your settings, a gaming profile or a crosshair under Publish.", "bookmarks" => "Use Bookmark on a post to save it here.", _ => _q is null && _game is null ? "Be the first to share your settings." : "Try other words or clear the filters." })));
            return;
        }
        var head = Ui.Muted($"{_total} post{(_total == 1 ? "" : "s")}{(_author is not null ? $" by {_author}" : "")}", 12);
        head.Margin = new Thickness(2, 0, 0, 8);
        _body.Children.Add(head);
        var wrap = new WrapPanel();
        foreach (var p in _items) wrap.Children.Add(Card(p));
        _body.Children.Add(wrap);
        if (_items.Count < _total)
            _body.Children.Add(Ui.Row(Ui.Btn("Load more", async (_, _) => { _page++; await LoadListAsync(false); })));
    }

    private UIElement Filters()
    {
        var sp = new StackPanel();
        var row = new WrapPanel();
        var search = new TextBox { Text = _q ?? "", Width = 280, Margin = new Thickness(0, 0, 8, 8), Tag = "Search: e.g. fortnite stretched 1440", VerticalAlignment = VerticalAlignment.Center };
        search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { _q = search.Text.Trim(); await LoadListAsync(true); } };
        row.Children.Add(search);
        row.Children.Add(Ui.Btn("Search", async (_, _) => { _q = search.Text.Trim(); await LoadListAsync(true); }, "BaseButton", new Thickness(0, 0, 12, 8)));
        ComboBox Combo(string all, string[] values, string? current, Action<string?> set)
        {
            var c = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 8) };
            c.Items.Add(all);
            foreach (var v in values) c.Items.Add(v);
            c.SelectedItem = current ?? all;
            c.SelectionChanged += async (_, _) => { set(c.SelectedItem as string == all ? null : c.SelectedItem as string); await LoadListAsync(true); };
            return c;
        }
        row.Children.Add(Combo("All games", Taxonomy.Games, _game, v => _game = v));
        row.Children.Add(Combo("All categories", Taxonomy.Categories, _category, v => _category = v));
        sp.Children.Add(row);
        var sorts = new WrapPanel();
        foreach (var (id, label) in new[] { ("new", "Newest"), ("top", "Most liked"), ("helpful", "Most helpful"), ("imports", "Most imported") })
        {
            var s = id;
            var rb = new RadioButton { Content = label, Style = Ui.Res<Style>("Chip"), GroupName = "hubsort", IsChecked = _sort == id, Margin = new Thickness(0, 0, 6, 0) };
            rb.Click += async (_, _) => { _sort = s; await LoadListAsync(true); };
            sorts.Children.Add(rb);
        }
        foreach (var (label, clear) in new (string?, Action)[] { (_tag is null ? null : "Tag: " + _tag, () => _tag = null), (_author is null ? null : "By " + _author, () => _author = null) })
        {
            if (label is null) continue;
            var c = clear;
            sorts.Children.Add(Ui.Btn(label + "  ✕", async (_, _) => { c(); await LoadListAsync(true); }, "GhostButton", new Thickness(8, 0, 0, 0)));
        }
        sp.Children.Add(sorts);
        return sp;
    }

    private UIElement Card(PostSummary p)
    {
        var sp = new StackPanel();
        var img = new Image { Height = 140, Stretch = Stretch.UniformToFill };
        var frame = new Border { Height = 140, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 10) };
        if (p.ThumbUrl is { } u) { frame.Child = img; _ = LoadInto(img, u); }
        else frame.Child = new TextBlock { Text = "\uEB9F", Style = Ui.Res<Style>("Icon"), FontSize = 34, Foreground = Ui.Brush("Subtle"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(frame);
        var title = Ui.Text(p.Title, 14, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        sp.Children.Add(title);
        sp.Children.Add(Ui.Muted($"{p.Game} · {p.Category}", 12));
        sp.Children.Add(Ui.Muted($"by {p.Author.Login} · {Ago(p.CreatedUtc)}", 12));
        var stats = Ui.Muted(Stats(p), 12);
        stats.Margin = new Thickness(0, 6, 0, 6);
        sp.Children.Add(stats);
        var pills = new WrapPanel();
        foreach (var s in p.Sections) { var t = Ui.Tag(s, s.Contains("user-reported") ? "Gold" : "Accent"); t.Margin = new Thickness(0, 0, 4, 4); pills.Children.Add(t); }
        if (p.Status != "visible") { var t = Ui.Tag(p.Status == "hidden" ? "Hidden: under review" : "Removed", "Danger"); t.Margin = new Thickness(0, 0, 4, 4); pills.Children.Add(t); }
        sp.Children.Add(pills);
        if (CanGet(p) && p.Status == "visible")
        {
            var get = Ui.Btn("⤓  Get", async (_, e) =>
            {
                e.Handled = true;
                await Try(async () => await GetAsync(await CommunityClient.Get<PostDetail>($"api/community/posts/{p.Id}")));
            }, "PrimaryButton", new Thickness(0, 8, 0, 0));
            get.ToolTip = "Add to your library in one click. Nothing is applied.";
            get.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(get);
        }
        var card = Ui.ClickCard(sp, async () => await OpenAsync(p.Id), new Thickness(0, 0, 12, 12));
        card.Width = 262;
        return card;
    }

    private static string Stats(PostSummary p)
    {
        int votes = p.HelpfulYes + p.HelpfulNo;
        return $"♥ {p.Likes}   ·   {(votes == 0 ? "no ratings" : $"{100 * p.HelpfulYes / votes}% helpful ({votes})")}   ·   ⤓ {p.Imports}";
    }

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    private static Border Note(string text, string tone) => new()
    {
        Background = Ui.Brush(tone + "Soft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text"), FontSize = 12.5 },
    };

    /// <summary>Loads a /media image (with the session, so pending images show for their author and moderators).</summary>
    private static async Task LoadInto(Image target, string url, int decodeWidth = 520)
    {
        var key = url + "|" + decodeWidth;
        if (Thumbs.TryGetValue(key, out var cached)) { target.Source = cached; return; }
        var bytes = await CommunityClient.GetBytesAsync(url);
        if (bytes is null) return;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.EndInit();
            bi.Freeze();
            if (Thumbs.Count > 300) Thumbs.Clear();
            Thumbs[key] = bi;
            target.Source = bi;
        }
        catch { }
    }

    // ---- guidelines and server --------------------------------------------------------------------------------------

    private void RenderGuidelines()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Section("Community guidelines"));
        sp.Children.Add(new TextBlock { Text = CommunityClient.Config?.Guidelines ?? CommunityGuidelines.Text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Text"), FontSize = 13, LineHeight = 21 });
        sp.Children.Add(Ui.Section("How it's kept safe"));
        sp.Children.Add(Ui.Muted(
            "• Accounts are GitHub accounts, so every limit applies per real account. New GitHub accounts (under a week old) can't post yet.\n" +
            $"• Each account can upload {CommunityClient.Config?.DailyUploadLimit ?? 2} images per day (resets at 00:00 UTC). Only PNG, JPEG and WebP; every image is re-encoded on the server, which removes hidden data such as camera and location details. Crosshair previews are drawn by the server and don't count.\n" +
            "• New screenshots are checked by a moderator before others can see them. Posts reported by several people are hidden until a moderator reviews them.\n" +
            "• Imports are previewed first and only added to your library. Tweaks that don't exist in your version are skipped. Nothing is applied until you apply it yourself, with the usual backups and confirmations.\n" +
            "• FPS numbers in posts are reported by their authors and aren't verified by Money Tweaks.", 12.5));
        _body.Children.Add(Ui.Card(sp, new Thickness(0, 0, 0, 12)));

        var srv = new StackPanel();
        srv.Children.Add(Ui.Section("Server", "Money Tweaks connects only to the Community Hub server set here. Leave it empty to keep the Hub off."));
        var box = new TextBox { Text = App.Settings.CommunityServer, Width = 360, Margin = new Thickness(0, 0, 8, 0), Tag = CommunityClient.OfficialServer.Length > 0 ? CommunityClient.OfficialServer + " (default)" : "https://money-tweaks.<subdomain>.workers.dev", VerticalAlignment = VerticalAlignment.Center };
        var msg = Ui.Muted("", 12);
        srv.Children.Add(Ui.Row(box, Ui.Btn("Save", async (_, _) =>
        {
            var text = box.Text.Trim();
            if (text.Length > 0 && CommunityClient.Validate(text, out var problem) is null) { msg.Text = problem; return; }
            if (text != App.Settings.CommunityServer) await CommunityClient.SignOutAsync(); // a session belongs to one server
            App.Settings.CommunityServer = text;
            App.Settings.Save();
            msg.Text = text.Length == 0 ? "Community Hub turned off." : "Saved.";
            _tab = "discover";
            await StartAsync();
        })));
        srv.Children.Add(msg);
        _body.Children.Add(Ui.Card(srv));
    }
}
