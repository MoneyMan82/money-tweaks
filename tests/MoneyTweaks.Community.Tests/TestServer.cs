using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MoneyTweaks.Community;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MoneyTweaks.Community.Tests;

/// <summary>A clock the tests can move (for the UTC-midnight quota reset and cleanup ages).</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The real server on a random 127.0.0.1 port with its own temporary data folder. Nothing leaves this machine.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public string Dir { get; }
    public ManualClock Clock { get; }
    public WebApplication App { get; }
    public Uri BaseUri { get; }

    private TestServer(WebApplication app, Uri uri, string dir, ManualClock clock) { App = app; BaseUri = uri; Dir = dir; Clock = clock; }

    public static async Task<TestServer> StartAsync(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null, string environment = "Development")
    {
        var dir = Path.Combine(Path.GetTempPath(), "mt-community-test-" + Guid.NewGuid().ToString("N")[..8]);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 20, 21, 30, 0, TimeSpan.Zero));
        var config = new Dictionary<string, string?>
        {
            ["Community:DataDirectory"] = dir,
            ["Community:DevSignIn"] = "true",
            ["Community:ImageReview"] = "post",
            ["Community:MaxUploadBytes"] = (2 * 1024 * 1024).ToString(),
            ["Community:MaxImageDimension"] = "2048",
            ["Community:RequestsPerMinutePerIp"] = "100000",
        };
        if (settings is not null) foreach (var (k, v) in settings) config[k] = v;
        var app = CommunityApp.Build([$"--environment={environment}"], b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock);
            services?.Invoke(b.Services);
            b.Logging.ClearProviders();
        });
        await app.StartAsync();
        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestServer(app, new Uri(addr), dir, clock);
    }

    public HttpClient Client(string? token = null)
    {
        var c = new HttpClient { BaseAddress = BaseUri };
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task<(HttpClient client, SessionInfo session)> SignInAsync(string login, int accountAgeDays = 365)
    {
        using var anon = Client();
        var r = await anon.PostAsJsonAsync("/api/v1/auth/dev", new { login, accountAgeDays });
        r.EnsureSuccessStatusCode();
        var s = (await r.Content.ReadFromJsonAsync<SessionInfo>(Api.Json))!;
        return (Client(s.Token), s);
    }

    public string ObjectsDir => Path.Combine(Dir, "objects");
    public int StoredFiles => Directory.Exists(ObjectsDir) ? Directory.GetFiles(ObjectsDir, "*", SearchOption.AllDirectories).Length : 0;

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public static class Img
{
    public static byte[] Png(int w = 64, int h = 64, bool transparent = true)
    {
        using var img = new Image<Rgba32>(w, h, transparent ? new Rgba32(0, 0, 0, 0) : new Rgba32(30, 40, 50, 255));
        for (int i = 0; i < Math.Min(w, h); i++) img[i, i] = new Rgba32(255, 0, 0, 255);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    public static byte[] JpegWithExif(int w = 120, int h = 80)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(10, 120, 200, 255));
        var exif = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Make, "SecretCamera");
        exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.GPSLatitude, [new Rational(60, 1), new Rational(10, 1), new Rational(0, 1)]);
        img.Metadata.ExifProfile = exif;
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    public static byte[] Webp(int w = 50, int h = 50)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(0, 200, 0, 255));
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms);
        return ms.ToArray();
    }

    public static byte[] Gif()
    {
        using var img = new Image<Rgba32>(32, 32, new Rgba32(0, 200, 0, 255));
        using var ms = new MemoryStream();
        img.SaveAsGif(ms);
        return ms.ToArray();
    }

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient c, byte[] data, string type = "image/png")
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        // Like the app: wait for the server's go-ahead, so a refused upload gets its answer instead of a dropped connection.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/images") { Content = content };
        req.Headers.ExpectContinue = true;
        return await c.SendAsync(req);
    }

    public static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
}
