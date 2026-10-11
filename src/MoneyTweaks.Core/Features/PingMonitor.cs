using System.Net.NetworkInformation;

namespace MoneyTweaks.Core.Features;

public sealed record PingStats(int Sent, int Lost, double? LastMs, double AvgMs, double MinMs, double MaxMs, double JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;

    /// <summary>A simple connection grade from latency, jitter and loss.</summary>
    public string Grade => Sent < 5 ? "Measuring…"
        : LossPercent > 2 || JitterMs > 30 ? "Unstable"
        : AvgMs > 100 || JitterMs > 10 ? "Fair"
        : "Stable";
}

/// <summary>Continuous ICMP ping with a rolling window, used by the Ping Stabilizer.</summary>
public sealed class PingMonitor
{
    private readonly List<double?> _window = [];
    private readonly int _size;
    public PingMonitor(int windowSize = 120) => _size = windowSize;

    public IReadOnlyList<double?> Samples { get { lock (_window) return _window.ToList(); } }

    public async Task<PingStats> PingOnceAsync(string host, int timeoutMs = 1000)
    {
        double? ms = null;
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, timeoutMs);
            if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
        }
        catch { /* DNS failure or no network counts as a lost packet */ }
        lock (_window)
        {
            _window.Add(ms);
            if (_window.Count > _size) _window.RemoveAt(0);
            return Compute(_window);
        }
    }

    public void Reset() { lock (_window) _window.Clear(); }

    internal static PingStats Compute(IReadOnlyList<double?> w)
    {
        var ok = w.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (ok.Count == 0) return new PingStats(w.Count, w.Count, null, 0, 0, 0, 0);
        double jitter = 0;
        for (int i = 1; i < ok.Count; i++) jitter += Math.Abs(ok[i] - ok[i - 1]);
        jitter = ok.Count > 1 ? jitter / (ok.Count - 1) : 0;
        return new PingStats(w.Count, w.Count - ok.Count, w[^1], ok.Average(), ok.Min(), ok.Max(), jitter);
    }

    /// <summary>Well-known anycast/game-adjacent targets that answer ICMP.</summary>
    public static readonly (string label, string host)[] Targets =
    [
        ("Cloudflare (1.1.1.1)", "1.1.1.1"),
        ("Google DNS (8.8.8.8)", "8.8.8.8"),
        ("Quad9 (9.9.9.9)", "9.9.9.9"),
        ("Your router (default gateway)", "gateway"),
    ];

    public static string? DefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))?.ToString();
}
