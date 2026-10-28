using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Pocket;

/// <summary>
/// One allowed Pocket Panel action. The phone can only ask for these ids; anything else is refused. Kind tells the phone how
/// to draw it: "switch" (flips the setting, Active = on now), "choice" (one of a group, Active = selected) or "button".
/// </summary>
public sealed record PocketAction(string Id, string Label, string Group, bool Active, string Kind = "button", string? Hint = null);

/// <summary>What the app provides to Pocket Panel: live telemetry, the allowed actions and how to run them.</summary>
public interface IPocketHost
{
    /// <summary>The current telemetry as a JSON object (already serialised).</summary>
    string FrameJson();
    IReadOnlyList<PocketAction> Actions();
    /// <summary>Runs one allowlisted action; only called with an id from <see cref="Actions"/>.</summary>
    (bool ok, string message) Run(string actionId);
    /// <summary>Raised when new telemetry is ready (any thread).</summary>
    event Action? FrameReady;
    /// <summary>Telemetry is needed (a phone is connected) or no longer needed.</summary>
    void SetActive(bool active);
    /// <summary>Games a dashboard page can be set to open for (key and name from the Game Library).</summary>
    IReadOnlyList<PocketGame> Games() => [];
}

/// <summary>A game from the Game Library, for assigning a dashboard page to it.</summary>
public sealed record PocketGame(string Key, string Name);

/// <summary>
/// Pocket Panel's web server: serves the phone dashboard and its API on this PC's local network only. It binds to private
/// (RFC 1918 / link-local) addresses and loopback, drops connections from anywhere else, checks the Host header (no DNS
/// rebinding), requires a session token on every API call, and only runs actions from <see cref="IPocketHost.Actions"/>.
/// No files are served from disk and nothing else can be requested. Plain HTTP: meant for a trusted home network.
/// </summary>
public sealed class PocketServer(PocketAuth auth, IPocketHost host, PocketDeckStore? deckStore = null) : IDisposable
{
    private const int MaxHeaderBytes = 8192, MaxBodyBytes = 64 * 1024;
    private readonly PocketDeckStore _deck = deckStore ?? new PocketDeckStore(new PocketSettings());
    private static readonly JsonSerializerOptions DeckJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8 };
    private readonly List<TcpListener> _listeners = [];
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private int _streams;
    public int Port { get; private set; }
    public IReadOnlyList<string> Urls { get; private set; } = [];
    public bool Running => _cts is not null;
    public int Clients => _streams;
    public string? LastError { get; private set; }

    /// <summary>This PC's IPv4 addresses on local networks (up, non-loopback adapters with a private address).</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && PocketAuth.IsLocal(u.Address) && !IPAddress.IsLoopback(u.Address)) list.Add(u.Address);
            }
        }
        catch (Exception ex) { Log.Warn("Pocket Panel: couldn't list network adapters: " + ex.Message); }
        return list.Distinct().ToList();
    }

    /// <summary>Starts listening on the given local addresses (default: every local-network IPv4 address plus loopback).</summary>
    public bool Start(int port, IEnumerable<IPAddress>? addresses = null)
    {
        Stop();
        var addrs = (addresses ?? LocalAddresses().Append(IPAddress.Loopback)).Where(PocketAuth.IsLocal).Distinct().ToList();
        var cts = new CancellationTokenSource();
        var urls = new List<string>();
        foreach (var a in addrs)
        {
            try
            {
                var l = new TcpListener(a, port);
                l.Start(16);
                _listeners.Add(l);
                string h = $"{(a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString())}:{port}";
                _hosts.Add(h);
                if (IPAddress.IsLoopback(a)) _hosts.Add($"localhost:{port}");
                else urls.Add("http://" + h + "/");
                _ = AcceptLoop(l, cts.Token);
            }
            catch (SocketException ex) { LastError = $"Couldn't listen on {a}:{port}: {ex.Message}"; Log.Warn("Pocket Panel: " + LastError); }
        }
        if (_listeners.Count == 0) { cts.Dispose(); return false; }
        _cts = cts;
        Port = port;
        Urls = urls;
        Log.Info($"Pocket Panel listening on {string.Join(", ", _hosts)}");
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        foreach (var l in _listeners) try { l.Stop(); } catch { }
        _listeners.Clear();
        _hosts.Clear();
        _cts?.Dispose();
        _cts = null;
        Urls = [];
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Handle(c, ct), ct);
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body, string Remote);

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is null || !PocketAuth.IsLocal(remote)) return;   // only this PC and the local network
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var req = await Read(stream, remote.ToString(), timeout.Token);
                if (req is null) { await Respond(stream, 400, "text/plain", "Bad request"u8.ToArray()); return; }
                await Route(req, stream, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            catch (Exception ex) { Log.Warn("Pocket Panel request failed: " + ex.Message); }
        }
    }

    private static async Task<Request?> Read(NetworkStream s, string remote, CancellationToken ct)
    {
        var buf = new byte[MaxHeaderBytes];
        int len = 0, end = -1;
        while (end < 0)
        {
            if (len == buf.Length) return null;
            int n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) return null;
            len += n;
            end = IndexOf(buf, len, "\r\n\r\n"u8);
        }
        var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c <= 0) return null;
            headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int bodyLen = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
        if (bodyLen is < 0 or > MaxBodyBytes) return null;
        var body = new byte[bodyLen];
        int have = Math.Min(bodyLen, len - end - 4);
        Array.Copy(buf, end + 4, body, 0, have);
        while (have < bodyLen)
        {
            int n = await s.ReadAsync(body.AsMemory(have), ct);
            if (n == 0) return null;
            have += n;
        }
        var path = first[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return new(first[0], path, headers, body, remote);
    }

    private static int IndexOf(byte[] b, int len, ReadOnlySpan<byte> pat) => b.AsSpan(0, len).IndexOf(pat);

    private async Task Route(Request r, NetworkStream s, CancellationToken ct)
    {
        // DNS rebinding: a page on another site that resolves to this PC sends its own Host name. Only our addresses pass.
        if (!r.Headers.TryGetValue("Host", out var hostHeader) || !_hosts.Contains(hostHeader)) { await Respond(s, 421, "text/plain", "Unknown host"u8.ToArray()); return; }
        // Requests from other web pages carry their own Origin: refuse them (the dashboard is same-origin).
        if (r.Headers.TryGetValue("Origin", out var origin) && origin != "http://" + hostHeader) { await Respond(s, 403, "text/plain", "Forbidden"u8.ToArray()); return; }

        switch (r.Method, r.Path)
        {
            case ("GET", "/"): await Asset(s, "index.html", "text/html; charset=utf-8"); return;
            case ("GET", "/app.js"): await Asset(s, "app.js", "text/javascript; charset=utf-8"); return;
            case ("GET", "/app.css"): await Asset(s, "app.css", "text/css; charset=utf-8"); return;
            case ("POST", "/api/pair"):
            {
                var j = ParseJson(r.Body);
                string code = j?.TryGetProperty("code", out var c) == true ? c.GetString() ?? "" : "";
                string name = j?.TryGetProperty("name", out var n) == true ? n.GetString() ?? "" : "";
                var token = auth.Pair(code, name, r.Remote);
                Log.Info($"Pocket Panel: pairing from {r.Remote} {(token is null ? "refused" : "accepted")}");
                if (token is null) await Json(s, 403, new { error = "This pairing link is wrong, already used or expired. Open Pocket Panel in Money Tweaks and scan a new QR code." });
                else await Json(s, 200, new { session = token });
                return;
            }
        }
        if (!r.Path.StartsWith("/api/", StringComparison.Ordinal)) { await Respond(s, 404, "text/plain", "Not found"u8.ToArray()); return; }

        string? bearer = r.Headers.TryGetValue("Authorization", out var a) && a.StartsWith("Bearer ", StringComparison.Ordinal) ? a[7..].Trim() : null;
        var device = auth.Authenticate(bearer, r.Remote);
        if (device is null) { await Json(s, 401, new { error = "Not paired. Scan the QR code in Money Tweaks › Pocket Panel." }); return; }

        switch (r.Method, r.Path)
        {
            case ("GET", "/api/hello"):
                await Json(s, 200, new { device = device.Name, actions = host.Actions(), deck = _deck.Get(), games = host.Games() });
                return;
            case ("GET", "/api/deck"):
                await Json(s, 200, _deck.Get());
                return;
            case ("PUT", "/api/deck"):
            {
                // Base version + pages; validated and rebuilt by the store, conflicts return the current deck.
                PocketDeck? incoming = null; int baseVersion = -1;
                try
                {
                    using var doc = JsonDocument.Parse(r.Body, new JsonDocumentOptions { MaxDepth = 8 });
                    baseVersion = doc.RootElement.GetProperty("baseVersion").GetInt32();
                    incoming = new PocketDeck { Pages = doc.RootElement.GetProperty("pages").Deserialize<List<PocketPage>>(DeckJson) ?? [] };
                }
                catch { await Json(s, 400, new { ok = false, message = "That layout couldn't be read." }); return; }
                var (ok, deck, error) = _deck.TrySave(baseVersion, incoming);
                Log.Info($"Pocket Panel: {device.Name} saved dashboards: {(ok ? "v" + deck.Version : error)}");
                await Json(s, ok ? 200 : 409, new { ok, message = error, deck });
                return;
            }
            case ("GET", "/api/frame"):
                await Respond(s, 200, "application/json", Encoding.UTF8.GetBytes(host.FrameJson()));
                return;
            case ("GET", "/api/events"):
                await Events(s, bearer!, r.Remote, ct);
                return;
            case ("POST", "/api/action"):
            {
                var j = ParseJson(r.Body);
                string id = j?.TryGetProperty("id", out var i) == true ? i.GetString() ?? "" : "";
                if (!host.Actions().Any(x => x.Id == id))
                {
                    Log.Warn($"Pocket Panel: refused action '{(id.Length > 40 ? id[..40] : id)}' from {device.Name} (not allowed)");
                    await Json(s, 403, new { ok = false, message = "That action isn't allowed." });
                    return;
                }
                var (ok, msg) = host.Run(id);
                Log.Info($"Pocket Panel: {device.Name} ran {id}: {(ok ? "ok" : msg)}");
                await Json(s, ok ? 200 : 409, new { ok, message = msg, actions = host.Actions() });
                return;
            }
            case ("POST", "/api/unpair"):
                auth.Revoke(device.Id);
                await Json(s, 200, new { ok = true });
                return;
        }
        await Respond(s, 404, "text/plain", "Not found"u8.ToArray());
    }

    /// <summary>Server-sent events: one telemetry frame per sample while the phone stays connected and paired.</summary>
    private async Task Events(NetworkStream s, string token, string remote, CancellationToken ct)
    {
        var head = Headers(200, "text/event-stream; charset=utf-8", -1) + "Connection: keep-alive\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        var signal = new SemaphoreSlim(0, 1);
        void Ready() { if (signal.CurrentCount == 0) try { signal.Release(); } catch (SemaphoreFullException) { } }
        host.FrameReady += Ready;
        _deck.Changed += Ready;
        int deckSent = _deck.Get().Version;
        if (Interlocked.Increment(ref _streams) == 1) host.SetActive(true);
        try
        {
            await Send(s, "frame", host.FrameJson(), ct);
            while (!ct.IsCancellationRequested)
            {
                bool got = await signal.WaitAsync(TimeSpan.FromSeconds(15), ct);
                if (auth.Authenticate(token, remote) is null) { await Send(s, "revoked", "{}", ct); return; }   // revoked while connected
                if (_deck.Get() is { } d && d.Version != deckSent) { deckSent = d.Version; await Send(s, "deck", JsonSerializer.Serialize(d, DeckJson), ct); }   // changed on another device or the PC
                if (got) await Send(s, "frame", host.FrameJson(), ct);
                else await s.WriteAsync(": keep-alive\n\n"u8.ToArray(), ct);
            }
        }
        finally
        {
            host.FrameReady -= Ready;
            _deck.Changed -= Ready;
            if (Interlocked.Decrement(ref _streams) == 0) host.SetActive(false);
        }
    }

    private static Task Send(NetworkStream s, string ev, string json, CancellationToken ct) =>
        s.WriteAsync(Encoding.UTF8.GetBytes($"event: {ev}\ndata: {json}\n\n"), ct).AsTask();

    private static JsonElement? ParseJson(byte[] body)
    {
        try { return body.Length == 0 ? null : JsonDocument.Parse(body).RootElement; } catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Task Json(NetworkStream s, int status, object value) =>
        Respond(s, status, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts));

    private static async Task Asset(NetworkStream s, string name, string type)
    {
        using var res = typeof(PocketServer).Assembly.GetManifestResourceStream("MoneyTweaks.Pocket." + name);
        if (res is null) { await Respond(s, 404, "text/plain", "Not found"u8.ToArray()); return; }
        using var ms = new MemoryStream();
        await res.CopyToAsync(ms);
        await Respond(s, 200, type, ms.ToArray());
    }

    private static string Headers(int status, string type, int length) =>
        $"HTTP/1.1 {status} {(status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 421 => "Misdirected Request", _ => "Error" })}\r\n" +
        $"Content-Type: {type}\r\n" + (length >= 0 ? $"Content-Length: {length}\r\nConnection: close\r\n" : "") +
        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\n" +
        "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n";

    private static async Task Respond(NetworkStream s, int status, string type, byte[] body)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(Headers(status, type, body.Length) + "\r\n"));
        await s.WriteAsync(body);
    }
}
