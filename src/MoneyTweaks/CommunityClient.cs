using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MoneyTweaks.Community;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

public sealed class CommunityApiException(int status, string code, string message, QuotaInfo? quota = null, List<string>? problems = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public QuotaInfo? Quota { get; } = quota;
    public List<string> Problems { get; } = problems ?? [];
}

/// <summary>GitHub's device sign-in: the user enters <see cref="UserCode"/> at <see cref="VerificationUri"/>.</summary>
public sealed record DeviceSignIn(string DeviceCode, string UserCode, string VerificationUri, int IntervalSec, DateTime ExpiresUtc);

/// <summary>
/// Talks to the Community Hub server. The server address comes from settings; only https is accepted (plain http only
/// for this PC, for local testing). The session token is kept with Windows DPAPI (only this Windows account can read
/// it); the GitHub token from sign-in is handed to the server once and never stored.
/// </summary>
public static class CommunityClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly HttpClient GitHub = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string? _token;
    private static bool _loaded;

    public static MeInfo? Me { get; private set; }
    public static ServerConfig? Config { get; private set; }
    public static event Action? Changed;

    static CommunityClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"MoneyTweaks/{App.Version}");
        GitHub.DefaultRequestHeaders.UserAgent.ParseAdd($"MoneyTweaks/{App.Version}");
        GitHub.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private static string SessionFile => Path.Combine(AppPaths.Root, "community-session.dat");

    /// <summary>
    /// The official Community Hub: the Money Tweaks website's own Cloudflare Worker (the API is under /api/community/).
    /// Empty until that Worker is deployed with the Hub; then this is its address, e.g.
    /// "https://money-tweaks.&lt;subdomain&gt;.workers.dev". Users can still enter another server in the Hub's settings.
    /// </summary>
    public const string OfficialServer = "https://money-tweaks.barkgdd.workers.dev";

    /// <summary>The server in use: the user's own choice if set, otherwise the official one. Null when neither is set or allowed.</summary>
    public static Uri? BaseUri => Validate(App.Settings.CommunityServer.Length > 0 ? App.Settings.CommunityServer : OfficialServer, out _);

    public static Uri? Validate(string? address, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(address)) { problem = "No Community Hub server is set."; return null; }
        if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var u)) { problem = "That isn't a web address."; return null; }
        bool local = u.IsLoopback;
        if (u.Scheme != Uri.UriSchemeHttps && !(local && u.Scheme == Uri.UriSchemeHttp)) { problem = "The server must use https:// (http:// only for this PC)."; return null; }
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query)) { problem = "Enter just the server address."; return null; }
        return u;
    }

    public static bool SignedIn { get { EnsureLoaded(); return _token is not null; } }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(SessionFile))
                _token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SessionFile), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Warn("Community session couldn't be read: " + ex.Message); _token = null; }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MoneyTweaks.Community.Session.v1");

    private static void SaveToken(string? token)
    {
        _token = token;
        try
        {
            if (token is null) { if (File.Exists(SessionFile)) File.Delete(SessionFile); return; }
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllBytes(SessionFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Warn("Community session couldn't be saved: " + ex.Message); }
    }

    // ---- requests ---------------------------------------------------------------------------------------------------

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        EnsureLoaded();
        var root = BaseUri ?? throw new CommunityApiException(0, "no_server", "No Community Hub server is set.");
        var req = new HttpRequestMessage(method, new Uri(root, path.TrimStart('/')));
        if (_token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Api.Json);
        return req;
    }

    private static async Task<T> Send<T>(HttpRequestMessage req)
    {
        using (req)
        {
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req); }
            catch (HttpRequestException ex) { throw new CommunityApiException(0, "offline", "Couldn't reach the Community Hub: " + ex.Message); }
            catch (TaskCanceledException) { throw new CommunityApiException(0, "timeout", "The Community Hub didn't answer in time."); }
            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    if (typeof(T) == typeof(bool)) return (T)(object)true;
                    return (await resp.Content.ReadFromJsonAsync<T>(Api.Json))!;
                }
                await Throw(resp);
                throw new InvalidOperationException();
            }
        }
    }

    private static async Task Throw(HttpResponseMessage resp)
    {
        string code = "error", message = $"The Community Hub answered {(int)resp.StatusCode}.";
        QuotaInfo? quota = null;
        List<string>? problems = null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var e)) code = e.GetString() ?? code;
            if (r.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
            if (r.TryGetProperty("quota", out var q)) quota = q.Deserialize<QuotaInfo>(Api.Json);
            if (r.TryGetProperty("problems", out var p)) problems = p.Deserialize<List<string>>(Api.Json);
        }
        catch { if (resp.StatusCode == HttpStatusCode.TooManyRequests) message = "Too many requests. Wait a minute and try again."; }
        if (resp.StatusCode == HttpStatusCode.Unauthorized && code == "sign_in") { SaveToken(null); Me = null; Changed?.Invoke(); }
        if (quota is not null && Me is not null) { Me.Quota = quota; Changed?.Invoke(); }
        throw new CommunityApiException((int)resp.StatusCode, code, message, quota, problems);
    }

    public static Task<T> Get<T>(string path) => Send<T>(Request(HttpMethod.Get, path));
    public static Task<T> Post<T>(string path, object? body = null) => Send<T>(Request(HttpMethod.Post, path, body));
    public static Task<T> Put<T>(string path, object? body = null) => Send<T>(Request(HttpMethod.Put, path, body));
    public static Task<T> Delete<T>(string path) => Send<T>(Request(HttpMethod.Delete, path));

    /// <summary>Image bytes from a /media URL (with the session, so authors and moderators see pending images). Null if unavailable.</summary>
    public static async Task<byte[]?> GetBytesAsync(string relative)
    {
        try
        {
            using var req = Request(HttpMethod.Get, relative);
            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync() : null;
        }
        catch { return null; }
    }

    // ---- account ----------------------------------------------------------------------------------------------------

    public static async Task<ServerConfig> LoadConfigAsync()
    {
        Config = await Get<ServerConfig>("api/community/config");
        return Config;
    }

    public static async Task<MeInfo?> RefreshMeAsync()
    {
        if (!SignedIn) { Me = null; Changed?.Invoke(); return null; }
        try { Me = await Get<MeInfo>("api/community/me"); }
        catch (CommunityApiException e) when (e.Status == 401) { Me = null; }
        Changed?.Invoke();
        return Me;
    }

    private static void Accept(SessionInfo s)
    {
        SaveToken(s.Token);
        Me = s.Me;
        Changed?.Invoke();
    }

    /// <summary>Local test servers only (the server refuses this outside development).</summary>
    public static async Task SignInDevAsync(string login) => Accept(await Post<SessionInfo>("api/community/auth/dev", new { login }));

    public static async Task<DeviceSignIn> StartGitHubSignInAsync()
    {
        var clientId = (Config ?? await LoadConfigAsync()).GitHubClientId;
        if (string.IsNullOrEmpty(clientId)) throw new CommunityApiException(0, "no_github", "This Community Hub server doesn't have GitHub sign-in set up.");
        using var resp = await GitHub.PostAsync("https://github.com/login/device/code",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = "" }));
        if (!resp.IsSuccessStatusCode) throw new CommunityApiException((int)resp.StatusCode, "github", "GitHub didn't start the sign-in. Try again later.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        if (r.TryGetProperty("error", out var err)) throw new CommunityApiException(0, "github", "GitHub: " + err.GetString());
        var uri = r.GetProperty("verification_uri").GetString() ?? "";
        if (!uri.StartsWith("https://github.com/", StringComparison.Ordinal)) throw new CommunityApiException(0, "github", "Unexpected sign-in address from GitHub.");
        return new DeviceSignIn(r.GetProperty("device_code").GetString()!, r.GetProperty("user_code").GetString()!, uri,
            r.TryGetProperty("interval", out var i) ? i.GetInt32() : 5, DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32()));
    }

    /// <summary>Waits until the user approves the code on GitHub, then signs in to the Community Hub with it.</summary>
    public static async Task CompleteGitHubSignInAsync(DeviceSignIn d, CancellationToken ct)
    {
        var clientId = Config!.GitHubClientId!;
        int interval = Math.Max(5, d.IntervalSec);
        while (DateTime.UtcNow < d.ExpiresUtc)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var resp = await GitHub.PostAsync("https://github.com/login/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["device_code"] = d.DeviceCode, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }), ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (r.TryGetProperty("access_token", out var tok))
            {
                Accept(await Post<SessionInfo>("api/community/auth/github", new { accessToken = tok.GetString() }));
                return;
            }
            var error = r.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (error == "slow_down") interval += 5;
            else if (error is "expired_token" or "access_denied") throw new CommunityApiException(0, error, error == "access_denied" ? "Sign-in was cancelled on GitHub." : "The code expired. Start again.");
            else if (error is not "authorization_pending") throw new CommunityApiException(0, "github", "GitHub sign-in failed: " + error);
        }
        throw new CommunityApiException(0, "expired_token", "The code expired. Start again.");
    }

    public static async Task SignOutAsync()
    {
        try { if (SignedIn) await Post<bool>("api/community/auth/signout"); } catch { }
        SaveToken(null);
        Me = null;
        Changed?.Invoke();
    }

    /// <summary>Uploads a PNG/JPEG/WebP. Waits for the server's go-ahead first, so a refused upload (limit reached) isn't sent at all.</summary>
    // ---- Tweak Truth Index ---------------------------------------------------------------------------------------------

    public sealed record EvidenceShareResult(bool Accepted, bool Duplicate, int RemainingToday);

    /// <summary>Shares one sanitized trial (the user opted in and confirmed this exact content). Needs sign-in (rate limit only).</summary>
    public static Task<EvidenceShareResult> ShareEvidenceAsync(Core.Evidence.EvidenceSubmission s) =>
        Post<EvidenceShareResult>("api/community/evidence", s);

    /// <summary>Community aggregates for a tweak. Servers without the Tweak Truth Index answer 404 (code "not_found").</summary>
    public static Task<Core.Evidence.CommunityEvidence> GetEvidenceAsync(string tweakId) =>
        Get<Core.Evidence.CommunityEvidence>("api/community/evidence/" + Uri.EscapeDataString(tweakId));

    public static async Task<UploadResult> UploadAsync(byte[] data, string mime)
    {
        var req = Request(HttpMethod.Post, "api/community/images");
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        req.Headers.ExpectContinue = true;
        var r = await Send<UploadResult>(req);
        if (Me is not null) { Me.Quota = r.Quota; Changed?.Invoke(); }
        return r;
    }

    public static string MimeOf(byte[] d) => d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 ? "image/png"
        : d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 ? "image/jpeg"
        : d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[8] == 'W' && d[9] == 'E' ? "image/webp" : "application/octet-stream";
}
