using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MoneyTweaks.Core.Pocket;

namespace MoneyTweaks.Tests;

/// <summary>Pocket Panel: pairing, sessions, the action allowlist and the HTTP hardening, over a real server on loopback.</summary>
public sealed class PocketPanelTests : IDisposable
{
    private sealed class FakeHost : IPocketHost
    {
        public List<string> Ran { get; } = [];
        public List<bool> ActiveCalls { get; } = [];
        public int Frame;
        public event Action? FrameReady;
        public string FrameJson() => $"{{\"n\":{Frame}}}";
        public IReadOnlyList<PocketAction> Actions() => [new("overlay.show", "Show", "Overlay", false), new("overlay.style.Minimal", "Minimal", "Style", true)];
        public (bool ok, string message) Run(string id) { Ran.Add(id); return (true, "done"); }
        public void SetActive(bool active) { lock (ActiveCalls) ActiveCalls.Add(active); }
        public void Tick() { Frame++; FrameReady?.Invoke(); }
    }

    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly PocketSettings _settings = new();
    private readonly PocketAuth _auth;
    private readonly FakeHost _host = new();
    private readonly PocketServer _server;
    private readonly HttpClient _http;
    private readonly int _port;

    public PocketPanelTests()
    {
        _auth = new PocketAuth(_settings, () => _now);
        _server = new PocketServer(_auth, _host);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); _port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Assert.True(_server.Start(_port, [IPAddress.Loopback]));
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose() { _http.Dispose(); _server.Dispose(); }

    private async Task<string> PairAsync()
    {
        var (code, _) = _auth.StartPairing();
        var r = await _http.PostAsync("api/pair", Json(new { code, name = "Test phone" }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("session").GetString()!;
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    private HttpRequestMessage Req(HttpMethod m, string path, string? token, object? body = null)
    {
        var q = new HttpRequestMessage(m, path);
        if (token is not null) q.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) q.Content = Json(body);
        return q;
    }

    [Fact]
    public async Task The_dashboard_is_served_with_strict_headers()
    {
        var r = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Pocket Panel", await r.Content.ReadAsStringAsync());
        var csp = string.Join("", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/../../Windows/win.ini")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/index.html")).StatusCode);   // fixed routes only
    }

    [Fact]
    public async Task Foreign_host_names_and_cross_site_origins_are_refused()
    {
        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/");
        rebinding.Headers.Host = "evil.example.com";
        Assert.Equal((HttpStatusCode)421, (await _http.SendAsync(rebinding)).StatusCode);

        var (code, _) = _auth.StartPairing();
        var cross = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = Json(new { code, name = "x" }) };
        cross.Headers.Add("Origin", "http://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(cross)).StatusCode);
        Assert.Empty(_settings.Devices);
    }

    [Fact]
    public async Task Every_api_call_needs_a_valid_session()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/hello")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Req(HttpMethod.Get, "/api/frame", "made-up-token"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Req(HttpMethod.Post, "/api/action", null, new { id = "overlay.show" }))).StatusCode);
        Assert.Empty(_host.Ran);

        var token = await PairAsync();
        var hello = await _http.SendAsync(Req(HttpMethod.Get, "/api/hello", token));
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        Assert.Contains("overlay.show", await hello.Content.ReadAsStringAsync());
        Assert.NotEqual(token, Assert.Single(_settings.Devices).TokenHash);   // only the hash is kept
        Assert.Equal(PocketAuth.Hash(token), _settings.Devices[0].TokenHash);
    }

    [Fact]
    public async Task Pairing_codes_work_once_and_expire()
    {
        var (code, _) = _auth.StartPairing();
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync("api/pair", Json(new { code, name = "A" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsync("api/pair", Json(new { code, name = "B" }))).StatusCode);

        var (late, _) = _auth.StartPairing();
        _now += PocketAuth.PairingLifetime + TimeSpan.FromSeconds(1);
        Assert.Null(_auth.Pair(late, "C", "127.0.0.1"));
        Assert.Single(_settings.Devices);
    }

    [Fact]
    public void Guessing_codes_locks_the_address_out()
    {
        var (code, _) = _auth.StartPairing();
        for (int i = 0; i < PocketAuth.MaxFailures; i++) Assert.Null(_auth.Pair("wrong" + i, "x", "192.168.1.50"));
        Assert.Null(_auth.Pair(code, "x", "192.168.1.50"));      // even the right code, while locked out
        Assert.NotNull(_auth.Pair(code, "x", "192.168.1.51"));   // another device isn't affected
    }

    [Fact]
    public async Task Sessions_expire_and_can_be_revoked()
    {
        var token = await PairAsync();
        _auth.Revoke(_settings.Devices[0].Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Req(HttpMethod.Get, "/api/hello", token))).StatusCode);

        var t2 = await PairAsync();
        _now += PocketAuth.SessionLifetime + TimeSpan.FromMinutes(1);
        Assert.Null(_auth.Authenticate(t2, "127.0.0.1"));
    }

    [Fact]
    public async Task Only_allowlisted_actions_run()
    {
        var token = await PairAsync();
        foreach (var bad in new[] { "cmd /c calc", "overlay.style.Evil", "", "../../x", "overlay.show; rm" })
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Req(HttpMethod.Post, "/api/action", token, new { id = bad }))).StatusCode);
        Assert.Empty(_host.Ran);
        var ok = await _http.SendAsync(Req(HttpMethod.Post, "/api/action", token, new { id = "overlay.show" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["overlay.show"], _host.Ran);
    }

    [Fact]
    public async Task Live_events_stream_frames_and_stop_when_revoked()
    {
        var token = await PairAsync();
        using var res = await _http.SendAsync(Req(HttpMethod.Get, "/api/events", token), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        async Task<string> Next()
        {
            var sb = new StringBuilder();
            for (string? line; (line = await reader.ReadLineAsync()) is not null;) { if (line.Length == 0 && sb.Length > 0) break; if (line.Length > 0) sb.AppendLine(line); }
            return sb.ToString();
        }
        Assert.Contains("data: {\"n\":0}", await Next());
        lock (_host.ActiveCalls) Assert.Equal([true], _host.ActiveCalls);   // telemetry is only held while a phone is connected
        _host.Tick();
        Assert.Contains("data: {\"n\":1}", await Next());
        _auth.RevokeAll();
        _host.Tick();
        Assert.Contains("event: revoked", await Next());
    }

    [Fact]
    public void Only_local_addresses_are_accepted_or_bound()
    {
        foreach (var a in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.9", "172.31.255.1", "192.168.1.20", "169.254.3.4", "::1", "fe80::1", "fd00::5" })
            Assert.True(PocketAuth.IsLocal(IPAddress.Parse(a)), a);
        foreach (var a in new[] { "8.8.8.8", "172.32.0.1", "100.64.0.1", "193.168.1.1", "2001:4860::8888" })
            Assert.False(PocketAuth.IsLocal(IPAddress.Parse(a)), a);
        using var s = new PocketServer(new PocketAuth(new PocketSettings()), _host);
        Assert.False(s.Start(_port + 1, [IPAddress.Parse("8.8.8.8")]));   // public addresses are never bound
    }
}
