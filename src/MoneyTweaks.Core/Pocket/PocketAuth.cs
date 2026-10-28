using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MoneyTweaks.Core.Pocket;

/// <summary>A paired phone or tablet. Only a SHA-256 hash of its session token is stored.</summary>
public sealed class PocketDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime PairedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string LastAddress { get; set; } = "";
}

/// <summary>Pocket Panel settings: off by default; paired devices survive restarts (hashed tokens only).</summary>
public sealed class PocketSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 47823;
    public List<PocketDevice> Devices { get; set; } = [];
    /// <summary>Dashboard pages shared by every paired device (null until first used: the default layout).</summary>
    public PocketDeck? Deck { get; set; }
}

/// <summary>
/// Pairing and sessions for Pocket Panel. A pairing code is random (256 bits), shown only in the QR code / link on this PC,
/// works once and expires after <see cref="PairingLifetime"/>. Pairing returns a random session token; the PC keeps only its
/// hash. Every API request must carry the session token; sessions expire after <see cref="SessionLifetime"/> or when revoked.
/// Repeated wrong pairing codes from one address lock that address out for a while.
/// </summary>
public sealed class PocketAuth(PocketSettings settings, Func<DateTime>? clock = null)
{
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public const int MaxDevices = 8, MaxFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private (string code, DateTime expires)? _pairing;
    private readonly Dictionary<string, (int count, DateTime until)> _failures = [];

    /// <summary>Raised when devices are added, removed or seen (for saving and the UI).</summary>
    public event Action? Changed;

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A new single-use pairing code (replaces any previous one).</summary>
    public (string code, DateTime expiresUtc) StartPairing()
    {
        lock (_gate)
        {
            var p = (NewToken(), _now() + PairingLifetime);
            _pairing = p;
            return p;
        }
    }

    public void CancelPairing() { lock (_gate) _pairing = null; }

    public DateTime? PairingExpires { get { lock (_gate) return _pairing is { } p && p.expires > _now() ? p.expires : null; } }

    /// <summary>Exchanges a pairing code for a session token. Null when the code is wrong, used or expired, or the address is locked out.</summary>
    public string? Pair(string code, string deviceName, string address)
    {
        lock (_gate)
        {
            var now = _now();
            if (_failures.TryGetValue(address, out var f) && f.count >= MaxFailures && f.until > now) return null;
            bool ok = _pairing is { } p && p.expires > now && FixedEquals(p.code, code);
            if (!ok)
            {
                var c = _failures.TryGetValue(address, out var prev) && prev.until > now ? prev.count + 1 : 1;
                _failures[address] = (c, now + Lockout);
                return null;
            }
            _pairing = null;   // single use
            _failures.Remove(address);
            var token = NewToken();
            var name = Clean(deviceName);
            settings.Devices.RemoveAll(d => d.ExpiresUtc <= now);
            while (settings.Devices.Count >= MaxDevices) settings.Devices.Remove(settings.Devices.MinBy(d => d.LastSeenUtc)!);
            settings.Devices.Add(new PocketDevice
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)), Name = name, TokenHash = Hash(token),
                PairedUtc = now, LastSeenUtc = now, ExpiresUtc = now + SessionLifetime, LastAddress = address,
            });
            Changed?.Invoke();
            return token;
        }
    }

    /// <summary>The device a session token belongs to, or null (unknown, revoked or expired).</summary>
    public PocketDevice? Authenticate(string? token, string address)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Hash(token);
        lock (_gate)
        {
            var now = _now();
            var d = settings.Devices.FirstOrDefault(x => FixedEquals(x.TokenHash, hash));
            if (d is null || d.ExpiresUtc <= now) return null;
            bool notify = now - d.LastSeenUtc > TimeSpan.FromMinutes(1) || d.LastAddress != address;
            d.LastSeenUtc = now;
            d.LastAddress = address;
            if (notify) Changed?.Invoke();
            return d;
        }
    }

    public void Revoke(string deviceId) { lock (_gate) settings.Devices.RemoveAll(d => d.Id == deviceId); Changed?.Invoke(); }
    public void RevokeAll() { lock (_gate) settings.Devices.Clear(); Changed?.Invoke(); }
    public IReadOnlyList<PocketDevice> Devices { get { lock (_gate) return [.. settings.Devices]; } }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Clean(string name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? "Phone or tablet" : s.Length > 40 ? s[..40] : s;
    }

    /// <summary>Addresses that can only come from this PC or the local network (RFC 1918, link-local, loopback, IPv6 ULA).</summary>
    public static bool IsLocal(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        return false;
    }
}
