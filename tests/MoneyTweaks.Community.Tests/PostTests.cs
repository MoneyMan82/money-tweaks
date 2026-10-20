using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MoneyTweaks.Community;
using MoneyTweaks.Community.Server;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MoneyTweaks.Community.Tests;

/// <summary>Publishing, permissions, reactions, search, moderation, crosshair previews and sign-in.</summary>
public sealed class PostTests
{
    private static object Crosshair(string color = "#FFFF00FF", double thickness = 2, string? imageFile = null)
    {
        var layers = new List<object> { new { kind = "Lines", name = "Lines", color, thickness, length = 10, gap = 4, outline = false } };
        if (imageFile is not null) layers.Add(new { kind = "Image", name = "Logo", imageFile, size = 40, imageAspect = 1.0 });
        return new { name = "Pro cross", layers };
    }

    [Fact]
    public async Task Publish_edit_and_delete_with_author_only_permissions()
    {
        await using var s = await TestServer.StartAsync();
        var (alice, _) = await s.SignInAsync("alice");
        var (bob, _) = await s.SignInAsync("bob");
        var up = (await (await Img.UploadAsync(alice, Img.Png())).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        var post = await UploadTests.Publish(alice, [up.Image.Id], crosshair: Crosshair());
        Assert.Equal("alice", post.Author.Login);
        Assert.Contains("Game settings", post.Sections);
        Assert.Contains("Crosshair", post.Sections);
        Assert.True(post.CanEdit);
        Assert.Equal(2, post.Images.Count); // upload + generated crosshair preview
        var preview = post.Images.Single(i => i.Kind == "crosshair-preview");
        Assert.Equal("approved", preview.Status);

        // The generated preview didn't use the daily allowance.
        Assert.Equal(1, (await alice.GetFromJsonAsync<MeInfo>("/api/v1/me", Api.Json))!.Quota.Remaining);

        var edit = new PostInput { Title = "Bob was here", Game = "Fortnite", Category = "Other", Content = new PostContent { Settings = new GameSettings { FpsCap = 240 } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync($"/api/v1/posts/{post.Id}", edit, Api.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/v1/posts/{post.Id}")).StatusCode);
        using var anon = s.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"/api/v1/posts/{post.Id}")).StatusCode);

        edit.Title = "Updated: Fortnite settings";
        var r = await alice.PutAsJsonAsync($"/api/v1/posts/{post.Id}", edit, Api.Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var updated = (await r.Content.ReadFromJsonAsync<PostDetail>(Api.Json))!;
        Assert.Equal("Updated: Fortnite settings", updated.Title);
        Assert.Empty(updated.Images); // image dropped from the post and the crosshair removed: files deleted
        Assert.Equal(0, s.StoredFiles);

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/posts/{post.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/posts/{post.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_post_deletes_its_image_files()
    {
        await using var s = await TestServer.StartAsync();
        var (a, _) = await s.SignInAsync("deleter");
        var up = (await (await Img.UploadAsync(a, Img.Png())).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        var post = await UploadTests.Publish(a, [up.Image.Id], crosshair: Crosshair());
        Assert.Equal(4, s.StoredFiles);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/v1/posts/{post.Id}")).StatusCode);
        Assert.Equal(0, s.StoredFiles);
        Assert.Equal(1, (await a.GetFromJsonAsync<MeInfo>("/api/v1/me", Api.Json))!.Quota.Remaining); // still counted
    }

    [Fact]
    public async Task Invalid_posts_are_rejected_and_crosshairs_are_clamped()
    {
        await using var s = await TestServer.StartAsync();
        var (c, _) = await s.SignInAsync("validator");
        async Task<HttpStatusCode> Try(object body) => (await c.PostAsJsonAsync("/api/v1/posts", body)).StatusCode;
        var ok = new { settings = new { fpsCap = 240 } };
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "hi", game = "Fortnite", category = "Other", content = ok }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Not a game", category = "Other", content = ok }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Fortnite", category = "Other", content = new { } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Fortnite", category = "Other", content = new { preset = new { name = "x", tweakIds = new[] { "gen.ok", "rm -rf /;" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Fortnite", category = "Other", content = new { gamingProfile = new { name = "x", tweakIds = new[] { "gen.ok" }, gameExes = new[] { @"C:\evil\game.exe" } } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Fortnite", category = "Other", description = "http://a.com http://b.com http://c.com", content = ok }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Fortnite", category = "Other", content = ok, imageIds = new[] { "0123456789abcdef0123456789abcdef" } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/posts", new StringContent("{not json", Encoding.UTF8, "application/json"))).StatusCode);

        // Someone else's upload can't be attached.
        var (other, _) = await s.SignInAsync("other");
        var theirs = (await (await Img.UploadAsync(other, Img.Png())).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { title = "Valid title", game = "Fortnite", category = "Other", content = ok, imageIds = new[] { theirs.Image.Id } }));

        // Crazy crosshair values are clamped by the app's own rules; a local image path is dropped.
        var post = await UploadTests.Publish(c, crosshair: new { name = "x", layers = new object[] { new { kind = "Lines", thickness = 1e9, gap = 5, color = "not a colour" }, new { kind = "Image", imageFile = "0123456789012345678901234567890123456789.png" } } });
        var xh = post.Content.Crosshair!.Value;
        var layer = xh.GetProperty("layers")[0];
        Assert.Equal(50, layer.GetProperty("thickness").GetDouble());
        Assert.Equal("#FF2BE38B", layer.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, xh.GetProperty("layers")[1].GetProperty("imageFile").ValueKind);
    }

    [Fact]
    public async Task Crosshair_preview_is_rendered_from_the_configuration()
    {
        await using var s = await TestServer.StartAsync(new() { ["Community:ImageReview"] = "pre" });
        var (c, _) = await s.SignInAsync("aimer");
        var (admin, _) = await s.SignInAsync("admin2");
        var logo = (await (await Img.UploadAsync(c, Img.Png(40, 40))).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        var post = await UploadTests.Publish(c, [logo.Image.Id], crosshair: Crosshair("#FFFF00FF", 2, "img:" + logo.Image.Id));
        var prev = post.Images.Single(i => i.Kind == "crosshair-preview");
        using var anon = s.Client();
        var png = await anon.GetByteArrayAsync(prev.Url);
        using (var img = Image.Load<Rgba32>(png))
        {
            Assert.Equal((256, 256), (img.Width, img.Height));
            int magenta = 0, red = 0;
            img.ProcessPixelRows(rows => { for (int y = 0; y < rows.Height; y++) foreach (var p in rows.GetRowSpan(y)) { if (p.R > 240 && p.G < 20 && p.B > 240) magenta++; if (p.R > 240 && p.G < 20 && p.B < 20) red++; } });
            Assert.True(magenta > 100, $"magenta pixels: {magenta}");
            Assert.Equal(0, red); // the logo is still pending: not drawn into the public preview
        }
        await admin.PostAsync($"/api/v1/admin/images/{logo.Image.Id}/approve", null);
        var after = (await anon.GetFromJsonAsync<PostDetail>($"/api/v1/posts/{post.Id}", Api.Json))!;
        var prev2 = after.Images.Single(i => i.Kind == "crosshair-preview");
        Assert.NotEqual(prev.Id, prev2.Id);
        using (var img = Image.Load<Rgba32>(await anon.GetByteArrayAsync(prev2.Url)))
        {
            int red = 0;
            img.ProcessPixelRows(rows => { for (int y = 0; y < rows.Height; y++) foreach (var p in rows.GetRowSpan(y)) if (p.R > 200 && p.G < 40 && p.B < 40) red++; });
            Assert.True(red > 20, $"red pixels after approval: {red}");
        }
    }

    [Fact]
    public async Task Likes_bookmarks_helpful_imports_search_and_sorting()
    {
        await using var s = await TestServer.StartAsync();
        var (a, _) = await s.SignInAsync("author");
        var (v1, _) = await s.SignInAsync("viewer1");
        var (v2, _) = await s.SignInAsync("viewer2");
        var p1 = await UploadTests.Publish(a, title: "Geometry wave settings");
        var p2 = await UploadTests.Publish(a, title: "Fortnite stretched 1440");

        Assert.True((await (await v1.PutAsync($"/api/v1/posts/{p2.Id}/like", null)).Content.ReadFromJsonAsync<PostDetail>(Api.Json))!.Liked);
        await v1.PutAsync($"/api/v1/posts/{p2.Id}/like", null); // idempotent
        await v2.PutAsync($"/api/v1/posts/{p2.Id}/like", null);
        await v1.PutAsync($"/api/v1/posts/{p1.Id}/bookmark", null);
        await v1.PutAsJsonAsync($"/api/v1/posts/{p2.Id}/helpful", new { helpful = true });
        await v2.PutAsJsonAsync($"/api/v1/posts/{p2.Id}/helpful", new { helpful = false });
        await v2.PutAsJsonAsync($"/api/v1/posts/{p2.Id}/helpful", new { helpful = true }); // changed its mind
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsJsonAsync($"/api/v1/posts/{p2.Id}/helpful", new { helpful = true })).StatusCode);
        await v1.PostAsync($"/api/v1/posts/{p1.Id}/imported", null);
        await v1.PostAsync($"/api/v1/posts/{p1.Id}/imported", null); // once per account

        using var anon = s.Client();
        var d2 = (await anon.GetFromJsonAsync<PostDetail>($"/api/v1/posts/{p2.Id}", Api.Json))!;
        Assert.Equal((2, 2, 0), (d2.Likes, d2.HelpfulYes, d2.HelpfulNo));
        Assert.Equal(1, (await anon.GetFromJsonAsync<PostDetail>($"/api/v1/posts/{p1.Id}", Api.Json))!.Imports);
        Assert.Equal(p2.Id, (await anon.GetFromJsonAsync<PostList>("/api/v1/posts?sort=top", Api.Json))!.Items[0].Id);
        Assert.Equal(p1.Id, (await anon.GetFromJsonAsync<PostList>("/api/v1/posts?sort=imports", Api.Json))!.Items[0].Id);
        var found = (await anon.GetFromJsonAsync<PostList>("/api/v1/posts?q=geometr", Api.Json))!;
        Assert.Equal(p1.Id, Assert.Single(found.Items).Id);
        Assert.Empty((await anon.GetFromJsonAsync<PostList>("/api/v1/posts?q=%22)%20OR%201%3D1%20--", Api.Json))!.Items); // query syntax is neutralized
        Assert.Equal(2, (await anon.GetFromJsonAsync<PostList>("/api/v1/posts?tag=stretched", Api.Json))!.Total);
        Assert.Equal(p1.Id, Assert.Single((await v1.GetFromJsonAsync<PostList>("/api/v1/me/bookmarks", Api.Json))!.Items).Id);
        var author = (await anon.GetFromJsonAsync<AuthorInfo>("/api/v1/users/author", Api.Json))!;
        Assert.Equal((2, 2), (author.PostCount, author.LikesReceived));
    }

    [Fact]
    public async Task Reports_hide_a_post_and_moderators_decide()
    {
        await using var s = await TestServer.StartAsync();
        var (a, _) = await s.SignInAsync("poster");
        var (admin, _) = await s.SignInAsync("admin3");
        var post = await UploadTests.Publish(a);
        using var anon = s.Client();
        for (int i = 0; i < 3; i++)
        {
            var (r, _) = await s.SignInAsync("reporter" + i);
            Assert.Equal(HttpStatusCode.NoContent, (await r.PostAsJsonAsync($"/api/v1/posts/{post.Id}/report", new { reason = "Spam or advertising", details = "ad" })).StatusCode);
            if (i == 0) Assert.Equal(HttpStatusCode.Conflict, (await r.PostAsJsonAsync($"/api/v1/posts/{post.Id}/report", new { reason = "Spam or advertising" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/posts/{post.Id}")).StatusCode); // hidden after 3 reports
        Assert.Equal("hidden", (await a.GetFromJsonAsync<PostDetail>($"/api/v1/posts/{post.Id}", Api.Json))!.Status); // the author still sees it
        var q = (await admin.GetFromJsonAsync<AdminQueue>("/api/v1/admin/queue", Api.Json))!;
        Assert.Equal(3, q.Reports.Count);
        Assert.Contains(q.HiddenPosts, p => p.Id == post.Id);

        await admin.PostAsync($"/api/v1/admin/posts/{post.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/v1/posts/{post.Id}")).StatusCode);
        await admin.PostAsJsonAsync($"/api/v1/admin/posts/{post.Id}/remove", new { note = "rules" });
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/posts/{post.Id}")).StatusCode);
        var edit = new PostInput { Title = "Trying to edit", Game = "Fortnite", Category = "Other", Content = new PostContent { Settings = new GameSettings { FpsCap = 1 } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PutAsJsonAsync($"/api/v1/posts/{post.Id}", edit, Api.Json)).StatusCode);

        // Suspending an account signs it out and hides its posts.
        var post2 = await UploadTests.Publish(a, title: "Another post here");
        var me = (await a.GetFromJsonAsync<MeInfo>("/api/v1/me", Api.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/admin/users/{me.User.Id}", new { banned = true, reason = "spam" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/posts/{post2.Id}")).StatusCode);
        using var signIn = s.Client();
        Assert.Equal(HttpStatusCode.Forbidden, (await signIn.PostAsJsonAsync("/api/v1/auth/dev", new { login = "poster" })).StatusCode);
    }

    [Fact]
    public async Task Spam_limits_apply_per_account()
    {
        await using var s = await TestServer.StartAsync(new() { ["Community:PostsPerDay"] = "2", ["Community:BlockedTerms:0"] = "badword" });
        var (c, _) = await s.SignInAsync("spammer");
        var bad = await c.PostAsJsonAsync("/api/v1/posts", new { title = "Buy BADWORD now", game = "Fortnite", category = "Other", content = new { settings = new { fpsCap = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await UploadTests.Publish(c, title: "First real post");
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/v1/posts", Same("First real post"))).StatusCode); // duplicate
        await UploadTests.Publish(c, title: "Second real post");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/v1/posts", Same("Third post today"))).StatusCode);
        static object Same(string t) => new
        {
            title = t, description = "Competitive settings I use.", game = "Fortnite", category = "Competitive", tags = new[] { "stretched", "1440p" },
            content = new { settings = new { resolutionWidth = 1440, resolutionHeight = 1080, stretched = true, refreshHz = 240, fpsCap = 0, sensitivity = 6.5, mouseDpi = 800, keybinds = new[] { new { action = "Wall", key = "Q" } } } },
        };
    }

    [Fact]
    public async Task GitHub_sign_in_checks_the_token_with_GitHub()
    {
        var fake = new FakeGitHub();
        await using var s = await TestServer.StartAsync(new()
        {
            ["Community:GitHubClientId"] = "Iv1.test", ["Community:GitHubClientSecret"] = "secret", ["Community:AdminGitHubIds:0"] = "4242",
        }, services => services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => fake));
        using var anon = s.Client();
        var r = await anon.PostAsJsonAsync("/api/v1/auth/github", new { accessToken = "gho_good" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var session = (await r.Content.ReadFromJsonAsync<SessionInfo>(Api.Json))!;
        Assert.Equal("octo", session.Me.User.Login);
        Assert.True(session.Me.IsAdmin);
        foreach (var f in Directory.GetFiles(s.Dir, "community.db*")) // the GitHub token is never stored (database and WAL)
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.Latin1);
            Assert.DoesNotContain("gho_good", await sr.ReadToEndAsync());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/github", new { accessToken = "gho_bad" })).StatusCode);
        Assert.Contains("POST /applications/Iv1.test/token", fake.Calls);
    }

    [Fact]
    public void Unsafe_production_configuration_is_refused()
    {
        var prod = new FakeEnv("Production");
        Assert.Throws<InvalidOperationException>(() => CommunityApp.CheckConfiguration(new CommunityOptions { DevSignIn = true, GitHubClientId = "x", GitHubClientSecret = "y" }, prod));
        Assert.Throws<InvalidOperationException>(() => CommunityApp.CheckConfiguration(new CommunityOptions(), prod));
        Assert.Throws<InvalidOperationException>(() => CommunityApp.CheckConfiguration(new CommunityOptions { GitHubClientId = "x", GitHubClientSecret = "y", Storage = "s3" }, prod));
        CommunityApp.CheckConfiguration(new CommunityOptions { GitHubClientId = "x", GitHubClientSecret = "y" }, prod);
    }

    [Fact]
    public void S3_signatures_match_the_AWS_reference_example()
    {
        // AWS Signature V4 documentation example (IAM ListUsers, 2015-08-30).
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8",
            ["host"] = "iam.amazonaws.com",
            ["x-amz-date"] = "20150830T123600Z",
        };
        var auth = SigV4.Authorization("GET", "/", "Action=ListUsers&Version=2010-05-08", headers,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc),
            "us-east-1", "iam", "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
        Assert.EndsWith("Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7", auth);
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            string token = req.Content is null ? req.Headers.Authorization?.Parameter ?? "" : JsonDocument.Parse(await req.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
            if (token != "gho_good") return new HttpResponseMessage(HttpStatusCode.NotFound);
            var body = req.RequestUri.AbsolutePath == "/user"
                ? """{"id":4242,"login":"octo","avatar_url":"https://avatars.githubusercontent.com/u/4242","created_at":"2015-01-01T00:00:00Z"}"""
                : """{"id":1,"user":{"id":4242,"login":"octo"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
