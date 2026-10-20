using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MoneyTweaks.Community.Server;

public sealed record AuthUser(string Id, string Provider, string ProviderId, string Login, string? AvatarUrl, bool IsAdmin, bool Banned,
    bool Trusted, DateTime? ProviderCreatedUtc, DateTime CreatedUtc);

public sealed class AuthFailed(string message) : Exception(message);

/// <summary>
/// Identity: GitHub accounts (the app signs in with GitHub's device flow and hands the server a GitHub token once).
/// The server checks the token with GitHub, then issues its own random session token; it stores only a SHA-256 hash of
/// that and never keeps the GitHub token. Every quota and permission is tied to the GitHub account id, which can't be
/// changed or spoofed by the client.
/// </summary>
public sealed class AuthService(Database db, CommunityOptions o, TimeProvider clock, IHttpClientFactory httpFactory)
{
    public const string GitHubApi = "https://api.github.com";

    public async Task<AuthUser> SignInGitHubAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 400) throw new AuthFailed("Missing GitHub token.");
        if (string.IsNullOrEmpty(o.GitHubClientId)) throw new AuthFailed("GitHub sign-in isn't configured on this server.");
        var http = httpFactory.CreateClient("github");

        // 1. The token must belong to Money Tweaks' own GitHub app (needs the client secret).
        if (!string.IsNullOrEmpty(o.GitHubClientSecret))
        {
            using var check = new HttpRequestMessage(HttpMethod.Post, $"{GitHubApi}/applications/{o.GitHubClientId}/token")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { access_token = accessToken }), Encoding.UTF8, "application/json"),
            };
            check.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.GitHubClientId}:{o.GitHubClientSecret}")));
            using var r = await http.SendAsync(check, ct);
            if (!r.IsSuccessStatusCode) throw new AuthFailed("GitHub didn't accept this sign-in. Try again.");
        }

        // 2. Who it is.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GitHubApi}/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new AuthFailed("GitHub didn't accept this sign-in. Try again.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var u = doc.RootElement;
        var id = u.GetProperty("id").GetInt64();
        var login = u.GetProperty("login").GetString() ?? throw new AuthFailed("GitHub returned no user name.");
        var avatar = u.TryGetProperty("avatar_url", out var a) ? a.GetString() : null;
        DateTime? created = u.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String ? Database.ParseUtc(c.GetString()!) : null;
        if (avatar is not null && !avatar.StartsWith("https://avatars.githubusercontent.com/", StringComparison.Ordinal)) avatar = null;
        return Upsert("github", id.ToString(), login, avatar, created);
    }

    /// <summary>Local development only (refused at startup outside Development). Lets tests and local previews sign in.</summary>
    public AuthUser SignInDev(string login, int accountAgeDays = 365)
    {
        if (!o.DevSignIn) throw new AuthFailed("Development sign-in is off.");
        login = new string((login ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(39).ToArray());
        if (login.Length == 0) throw new AuthFailed("Choose a user name.");
        return Upsert("dev", login.ToLowerInvariant(), login, null, clock.GetUtcNow().UtcDateTime.AddDays(-accountAgeDays));
    }

    private AuthUser Upsert(string provider, string providerId, string login, string? avatar, DateTime? providerCreated)
    {
        using var c = db.Open();
        var now = Database.Utc(clock.GetUtcNow().UtcDateTime);
        Database.Exec(c, """
            INSERT INTO users (id, provider, provider_id, login, avatar_url, provider_created_utc, created_utc)
            VALUES ($id, $p, $pid, $login, $avatar, $pc, $now)
            ON CONFLICT(provider, provider_id) DO UPDATE SET login = excluded.login, avatar_url = excluded.avatar_url,
                provider_created_utc = COALESCE(excluded.provider_created_utc, users.provider_created_utc)
            """,
            ("$id", Database.NewId()), ("$p", provider), ("$pid", providerId), ("$login", login), ("$avatar", avatar),
            ("$pc", providerCreated is null ? null : Database.Utc(providerCreated.Value)), ("$now", now));
        return Load(c, "provider = $p AND provider_id = $pid", ("$p", provider), ("$pid", providerId))!;
    }

    public AuthUser? FindUser(string id)
    {
        using var c = db.Open();
        return Load(c, "id = $id", ("$id", id));
    }

    public AuthUser? FindByLogin(string login)
    {
        using var c = db.Open();
        return Load(c, "login = $l COLLATE NOCASE", ("$l", login));
    }

    private AuthUser? Load(SqliteConnection c, string where, params (string, object?)[] args)
    {
        using var cmd = Database.Cmd(c, $"SELECT id, provider, provider_id, login, avatar_url, is_banned, is_trusted, provider_created_utc, created_utc FROM users WHERE {where}", args);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var provider = r.GetString(1);
        var pid = r.GetString(2);
        bool admin = provider == "github" && long.TryParse(pid, out var gid) && o.AdminGitHubIds.Contains(gid) || provider == "dev" && o.DevSignIn && pid.StartsWith("admin", StringComparison.Ordinal);
        return new AuthUser(r.GetString(0), provider, pid, r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), admin, r.GetInt64(5) != 0,
            r.GetInt64(6) != 0, r.IsDBNull(7) ? null : Database.ParseUtc(r.GetString(7)), Database.ParseUtc(r.GetString(8)));
    }

    public (string token, DateTime expires) CreateSession(AuthUser u)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddDays(o.SessionDays);
        using var c = db.Open();
        Database.Exec(c, "INSERT INTO sessions (token_hash, user_id, created_utc, expires_utc) VALUES ($h, $u, $c, $e)",
            ("$h", Hash(token)), ("$u", u.Id), ("$c", Database.Utc(now)), ("$e", Database.Utc(expires)));
        return (token, expires);
    }

    /// <summary>The signed-in user from "Authorization: Bearer &lt;token&gt;", or null.</summary>
    public AuthUser? Authenticate(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = h[7..].Trim();
        if (token.Length is < 20 or > 100) return null;
        using var c = db.Open();
        var uid = Database.Scalar(c, "SELECT user_id FROM sessions WHERE token_hash = $h AND expires_utc > $now",
            ("$h", Hash(token)), ("$now", Database.Utc(clock.GetUtcNow().UtcDateTime))) as string;
        return uid is null ? null : Load(c, "id = $id", ("$id", uid));
    }

    public void SignOut(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.Ordinal)) return;
        using var c = db.Open();
        Database.Exec(c, "DELETE FROM sessions WHERE token_hash = $h", ("$h", Hash(h[7..].Trim())));
    }

    /// <summary>Why this account can't post or upload yet, or null.</summary>
    public string? CannotContribute(AuthUser u)
    {
        if (u.Banned) return "This account is suspended from the Community Hub.";
        if (u.ProviderCreatedUtc is { } created && clock.GetUtcNow().UtcDateTime - created < TimeSpan.FromDays(o.MinAccountAgeDays))
            return $"GitHub accounts younger than {o.MinAccountAgeDays} days can't post or upload yet (spam protection).";
        return null;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The daily upload allowance. Each successful upload occupies one of N numbered slots for (account, UTC day); the
/// database primary key makes an (N+1)th slot impossible, and slots are claimed inside an IMMEDIATE transaction, so
/// parallel requests, retries, refreshes or direct API calls can't exceed it. A slot is reserved before the image is
/// processed and released if processing or storage fails, so failed uploads don't count. Deleting an image later does
/// not give the slot back (otherwise upload/delete loops would bypass the limit).
/// </summary>
public sealed class QuotaService(Database db, CommunityOptions o, TimeProvider clock)
{
    public sealed record Reservation(string UserId, string Day, int Slot);

    public static string DayOf(DateTime utc) => utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public QuotaInfo Info(string userId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var c = db.Open();
        var used = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM upload_slots WHERE user_id = $u AND day = $d AND (state = 'used' OR reserved_utc > $stale)",
            ("$u", userId), ("$d", DayOf(now)), ("$stale", Database.Utc(now.AddMinutes(-o.ReservationMinutes)))));
        return new QuotaInfo { Limit = o.DailyUploadLimit, Used = Math.Min(used, o.DailyUploadLimit), Remaining = Math.Max(0, o.DailyUploadLimit - used), ResetsAtUtc = now.Date.AddDays(1) };
    }

    public Reservation? TryReserve(string userId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var day = DayOf(now);
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: one writer at a time
        Database.Exec(c, "DELETE FROM upload_slots WHERE user_id = $u AND day = $d AND state = 'reserved' AND reserved_utc <= $stale",
            ("$u", userId), ("$d", day), ("$stale", Database.Utc(now.AddMinutes(-o.ReservationMinutes))));
        var taken = new HashSet<long>();
        using (var cmd = Database.Cmd(c, "SELECT slot FROM upload_slots WHERE user_id = $u AND day = $d", ("$u", userId), ("$d", day)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) taken.Add(r.GetInt64(0));
        int slot = Enumerable.Range(1, o.DailyUploadLimit).FirstOrDefault(s => !taken.Contains(s));
        if (slot == 0) { tx.Rollback(); return null; }
        Database.Exec(c, "INSERT INTO upload_slots (user_id, day, slot, state, reserved_utc) VALUES ($u, $d, $s, 'reserved', $now)",
            ("$u", userId), ("$d", day), ("$s", slot), ("$now", Database.Utc(now)));
        tx.Commit();
        return new Reservation(userId, day, slot);
    }

    public void Commit(Reservation r, string imageId)
    {
        using var c = db.Open();
        Database.Exec(c, "UPDATE upload_slots SET state = 'used', image_id = $i WHERE user_id = $u AND day = $d AND slot = $s",
            ("$i", imageId), ("$u", r.UserId), ("$d", r.Day), ("$s", r.Slot));
    }

    public void Release(Reservation r)
    {
        using var c = db.Open();
        Database.Exec(c, "DELETE FROM upload_slots WHERE user_id = $u AND day = $d AND slot = $s AND state = 'reserved'",
            ("$u", r.UserId), ("$d", r.Day), ("$s", r.Slot));
    }
}
