using System.Net;
using System.Net.Http.Json;
using System.Text;
using MoneyTweaks.Community;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MoneyTweaks.Community.Tests;

/// <summary>Image uploads: formats, safety, re-encoding, and the 2-per-UTC-day quota enforced by the server.</summary>
public sealed class UploadTests
{
    [Fact]
    public async Task Png_keeps_transparency_and_jpeg_loses_its_metadata()
    {
        await using var s = await TestServer.StartAsync();
        var (c, _) = await s.SignInAsync("alice");

        var r = await Img.UploadAsync(c, Img.Png(64, 48));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var up = (await r.Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        Assert.True(up.Image.HasAlpha);
        Assert.Equal((64, 48), (up.Image.Width, up.Image.Height));
        var png = await c.GetByteArrayAsync(up.Image.Url);
        using (var back = Image.Load<Rgba32>(png))
        {
            Assert.Equal(0, back[5, 0].A); // transparent stays transparent
            Assert.Equal(255, back[3, 3].A);
        }
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x89, (await c.GetByteArrayAsync(up.Image.ThumbUrl))[0]);

        var (c2, _) = await s.SignInAsync("bob");
        var j = await Img.UploadAsync(c2, Img.JpegWithExif(), "image/jpeg");
        var upj = (await j.Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        var jpg = await c2.GetByteArrayAsync(upj.Image.Url);
        Assert.Equal(0xFF, jpg[0]);
        Assert.DoesNotContain("SecretCamera", Encoding.Latin1.GetString(jpg));
        using (var back = Image.Load(jpg)) Assert.Null(back.Metadata.ExifProfile);
        Assert.False(upj.Image.HasAlpha);

        var w = await Img.UploadAsync(c2, Img.Webp(), "image/webp");
        Assert.Equal(HttpStatusCode.OK, w.StatusCode); // WebP accepted, converted to JPEG/PNG for every client
        var upw = (await w.Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        Assert.NotEqual((byte)'R', (await c2.GetByteArrayAsync(upw.Image.Url))[0]);
    }

    [Theory]
    [InlineData("svg", "image/svg+xml", 415)]
    [InlineData("exe", "application/octet-stream", 415)]
    [InlineData("gif", "image/gif", 415)]
    [InlineData("html", "image/png", 415)]
    [InlineData("mismatch", "image/jpeg", 415)]
    [InlineData("corrupt", "image/png", 422)]
    [InlineData("tiny", "image/png", 422)]
    [InlineData("huge", "image/png", 422)]
    [InlineData("toolarge", "image/png", 413)]
    [InlineData("empty", "image/png", 400)]
    public async Task Bad_files_are_rejected_and_do_not_use_the_quota(string kind, string type, int status)
    {
        await using var s = await TestServer.StartAsync();
        var (c, _) = await s.SignInAsync("mallory");
        byte[] data = kind switch
        {
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
            "exe" => [0x4D, 0x5A, 0x90, 0x00, .. new byte[200]],
            "gif" => Img.Gif(),
            "html" => Encoding.UTF8.GetBytes("<html><body>hi</body></html>"),
            "mismatch" => Img.Png(),
            "corrupt" => [.. Img.Png().Take(40), .. Enumerable.Repeat((byte)7, 400)],
            "tiny" => Img.Png(8, 8),
            "huge" => Img.Png(3000, 20),
            "toolarge" => [.. Img.Png(), .. new byte[3 * 1024 * 1024]],
            _ => [],
        };
        var r = await Img.UploadAsync(c, data, type);
        Assert.Equal(status, (int)r.StatusCode);
        var me = (await c.GetFromJsonAsync<MeInfo>("/api/v1/me", Api.Json))!;
        Assert.Equal(0, me.Quota.Used);
        Assert.Equal(2, me.Quota.Remaining);
        Assert.Equal(0, s.StoredFiles);
    }

    [Fact]
    public async Task Two_uploads_per_utc_day_then_refused_until_midnight_utc()
    {
        await using var s = await TestServer.StartAsync();
        var (c, _) = await s.SignInAsync("carol");
        var a = (await (await Img.UploadAsync(c, Img.Png())).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        Assert.Equal(1, a.Quota.Remaining);
        Assert.Equal(new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), a.Quota.ResetsAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await Img.UploadAsync(c, Img.Png(32, 32))).StatusCode);

        var third = await Img.UploadAsync(c, Img.Png(40, 40));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var body = await Img.Json(third);
        Assert.Equal("quota_exceeded", body.GetProperty("error").GetString());
        Assert.Equal(0, body.GetProperty("quota").GetProperty("remaining").GetInt32());

        // Deleting an upload doesn't give the slot back (no upload/delete loops).
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/images/{a.Image.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Img.UploadAsync(c, Img.Png(41, 41))).StatusCode);

        // A new session (sign in again, "refresh") doesn't reset it either: it's per account.
        var (again, _) = await s.SignInAsync("carol");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Img.UploadAsync(again, Img.Png(42, 42))).StatusCode);

        // Another account has its own allowance.
        var (dave, _) = await s.SignInAsync("dave");
        Assert.Equal(HttpStatusCode.OK, (await Img.UploadAsync(dave, Img.Png())).StatusCode);

        s.Clock.Now = new DateTimeOffset(2026, 10, 20, 23, 59, 59, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Img.UploadAsync(c, Img.Png(43, 43))).StatusCode);
        s.Clock.Now = new DateTimeOffset(2026, 10, 21, 0, 0, 1, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await Img.UploadAsync(c, Img.Png(44, 44))).StatusCode);
    }

    [Fact]
    public async Task Parallel_uploads_cannot_exceed_the_limit()
    {
        await using var s = await TestServer.StartAsync();
        var (_, session) = await s.SignInAsync("racer");
        var (_, session2) = await s.SignInAsync("racer"); // two sessions of the same account
        var clients = Enumerable.Range(0, 16).Select(i => s.Client(i % 2 == 0 ? session.Token : session2.Token)).ToList();
        var results = await Task.WhenAll(clients.Select((cl, i) => Img.UploadAsync(cl, Img.Png(32 + i, 32))));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(14, results.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        var me = (await clients[0].GetFromJsonAsync<MeInfo>("/api/v1/me", Api.Json))!;
        Assert.Equal((2, 0), (me.Quota.Used, me.Quota.Remaining));
        Assert.Equal(4, s.StoredFiles); // 2 images + 2 thumbnails, nothing from the refused ones
        foreach (var cl in clients) cl.Dispose();
    }

    [Fact]
    public async Task Uploads_need_a_trustworthy_account()
    {
        await using var s = await TestServer.StartAsync();
        using var anon = s.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Img.UploadAsync(anon, Img.Png())).StatusCode);
        using var forged = s.Client("not-a-real-session-token-1234567890");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Img.UploadAsync(forged, Img.Png())).StatusCode);
        var (young, _) = await s.SignInAsync("brandnew", accountAgeDays: 2);
        var r = await Img.UploadAsync(young, Img.Png());
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("younger than 7 days", (await Img.Json(r)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Pending_images_are_private_until_a_moderator_approves_them()
    {
        await using var s = await TestServer.StartAsync(new() { ["Community:ImageReview"] = "pre" });
        var (owner, _) = await s.SignInAsync("owner");
        var (other, _) = await s.SignInAsync("stranger");
        var (admin, _) = await s.SignInAsync("admin1");
        using var anon = s.Client();
        var up = (await (await Img.UploadAsync(owner, Img.Png())).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        Assert.Equal("pending", up.Image.Status);
        var post = await Publish(owner, imageIds: [up.Image.Id]);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(up.Image.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(up.Image.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(up.Image.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(up.Image.Url)).StatusCode);
        var seen = (await anon.GetFromJsonAsync<PostDetail>($"/api/v1/posts/{post.Id}", Api.Json))!;
        Assert.DoesNotContain(seen.Images, i => i.Id == up.Image.Id);

        var queue = (await admin.GetFromJsonAsync<AdminQueue>("/api/v1/admin/queue", Api.Json))!;
        Assert.Contains(queue.PendingImages, i => i.Image.Id == up.Image.Id && i.Owner == "owner");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/admin/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"/api/v1/admin/images/{up.Image.Id}/approve", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/images/{up.Image.Id}/approve", null)).StatusCode);
        var r = await anon.GetAsync(up.Image.Url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());

        var up2 = (await (await Img.UploadAsync(owner, Img.Png(30, 30))).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        int files = s.StoredFiles;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/images/{up2.Image.Id}/reject", null)).StatusCode);
        Assert.Equal(files - 2, s.StoredFiles); // rejected files deleted at once
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(up2.Image.Url)).StatusCode);
    }

    [Fact]
    public async Task Media_urls_cannot_reach_other_files()
    {
        await using var s = await TestServer.StartAsync();
        using var anon = s.Client();
        foreach (var path in new[] { "/media/..%2F..%2Fcommunity.db/full", "/media/0123/full", "/media/0123456789abcdef0123456789abcdef/raw", "/media/%2e%2e/thumb" })
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(path)).StatusCode);
        Assert.Throws<ArgumentException>(() => Server.ObjectKeys.Check("../community.db"));
        Assert.Throws<ArgumentException>(() => Server.ObjectKeys.Check("img/../../x.png"));
    }

    [Fact]
    public async Task Unattached_uploads_are_cleaned_up_after_a_day_but_still_counted()
    {
        await using var s = await TestServer.StartAsync();
        var (c, _) = await s.SignInAsync("orphaner");
        var up = (await (await Img.UploadAsync(c, Img.Png())).Content.ReadFromJsonAsync<UploadResult>(Api.Json))!;
        Assert.Equal(2, s.StoredFiles);
        var svc = (Server.CommunityService)s.App.Services.GetService(typeof(Server.CommunityService))!;
        Assert.Equal(0, await svc.CleanupAsync());
        s.Clock.Now = s.Clock.Now.AddHours(25);
        Assert.Equal(1, await svc.CleanupAsync());
        Assert.Equal(0, s.StoredFiles);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(up.Image.Url)).StatusCode);
    }

    internal static async Task<PostDetail> Publish(HttpClient c, List<string>? imageIds = null, string title = "My Fortnite settings", object? crosshair = null)
    {
        var input = new
        {
            title, description = "Competitive settings I use.", game = "Fortnite", category = "Competitive", tags = new[] { "stretched", "1440p" },
            content = new
            {
                settings = new { resolutionWidth = 1440, resolutionHeight = 1080, stretched = true, refreshHz = 240, fpsCap = 0, sensitivity = 6.5, mouseDpi = 800,
                    keybinds = new[] { new { action = "Wall", key = "Q" } } },
                crosshair,
            },
            imageIds = imageIds ?? [],
        };
        var r = await c.PostAsJsonAsync("/api/v1/posts", input);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<PostDetail>(Api.Json))!;
    }
}
