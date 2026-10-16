using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace MoneyTweaks.Core.Features;

public sealed record EndpointResult(string Label, string Host, PingStats Stats);
public sealed record DiagnosticReport(List<EndpointResult> Endpoints, List<(string Host, double? Ms)> Dns, string Connection, List<string> Findings, DateTime At);

/// <summary>
/// One-shot connection diagnostic: pings the router and public endpoints at the same time, times DNS lookups and
/// explains where latency, jitter or loss comes from. Generic ICMP ping, not a game server's ping.
/// </summary>
public static class NetworkDiagnostics
{
    public static async Task<DiagnosticReport> RunAsync(int seconds, IProgress<int>? progress, CancellationToken ct)
    {
        var gw = PingMonitor.DefaultGateway();
        var targets = new List<(string label, string host)>();
        if (gw is not null) targets.Add(("Router", gw));
        targets.Add(("Cloudflare", "1.1.1.1"));
        targets.Add(("Google", "8.8.8.8"));
        var monitors = targets.Select(_ => new PingMonitor(seconds * 4)).ToList();
        var sw = Stopwatch.StartNew();
        var loops = targets.Select((t, i) => Task.Run(async () =>
        {
            while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
            {
                var t0 = sw.ElapsedMilliseconds;
                await monitors[i].PingOnceAsync(t.host);
                var wait = 250 - (int)(sw.ElapsedMilliseconds - t0);
                if (wait > 0) try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }
            }
        }, ct)).ToList();
        while (loops.Any(l => !l.IsCompleted)) { progress?.Report((int)Math.Min(100, sw.Elapsed.TotalSeconds * 100 / seconds)); await Task.WhenAny(Task.WhenAll(loops), Task.Delay(250, ct)); }
        ct.ThrowIfCancellationRequested();

        var dns = new List<(string, double?)>();
        foreach (var h in new[] { "epicgames.com", "microsoft.com", "cloudflare.com" })
        {
            var t0 = Stopwatch.StartNew();
            try { await Dns.GetHostAddressesAsync(h, ct); dns.Add((h, Math.Round(t0.Elapsed.TotalMilliseconds, 1))); }
            catch { dns.Add((h, null)); }
        }
        var eps = targets.Select((t, i) => new EndpointResult(t.label, t.host, PingMonitor.Compute(monitors[i].Samples))).ToList();
        var conn = ConnectionType();
        return new(eps, dns, conn, Diagnose(eps, dns, conn), DateTime.Now);
    }

    public static string ConnectionType()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)) && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
            if (up.Count == 0) return "No active connection";
            return string.Join(", ", up.Select(n => $"{(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet")} {n.Speed / 1_000_000} Mbps"));
        }
        catch { return "Unknown"; }
    }

    /// <summary>Plain-language findings from the measurements. Pure function so it can be tested.</summary>
    public static List<string> Diagnose(List<EndpointResult> eps, List<(string Host, double? Ms)> dns, string connection)
    {
        var f = new List<string>();
        var router = eps.FirstOrDefault(e => e.Label == "Router");
        var internet = eps.Where(e => e.Label != "Router").ToList();
        bool Bad(PingStats s) => s.LossPercent > 1 || s.JitterMs > 10;
        if (router is not null && router.Stats.Sent > 0 && router.Stats.Lost == router.Stats.Sent)
            f.Add("Your router doesn't answer ping (some block it), so local and internet problems can't be told apart.");
        else if (router is not null && Bad(router.Stats))
            f.Add($"Problem is inside your home network: the router itself shows {router.Stats.LossPercent:0.#}% loss and {router.Stats.JitterMs:0.#} ms jitter." +
                  (connection.Contains("Wi-Fi") ? " On Wi-Fi this usually means interference or weak signal; a cable usually fixes it." : " Check the cable, switch or router."));
        else if (internet.Count > 0 && internet.All(e => Bad(e.Stats)))
            f.Add("Your home network is fine (router is stable) but every internet endpoint shows loss or jitter: the issue is with your ISP or the route beyond your router.");
        else if (internet.Count > 0 && internet.Any(e => Bad(e.Stats)))
            f.Add($"Only {string.Join(", ", internet.Where(e => Bad(e.Stats)).Select(e => e.Label))} is unstable; others are fine. That points to a route/provider issue, not your PC.");
        else f.Add("Connection is stable: low jitter and no meaningful packet loss to the router or the internet.");
        if (router is { Stats.Sent: > 0 } && router.Stats.Lost < router.Stats.Sent && router.Stats.AvgMs > 5)
            f.Add($"Router latency is {router.Stats.AvgMs:0} ms; on a cable it's normally under 2 ms. " + (connection.Contains("Wi-Fi") ? "Typical for Wi-Fi." : "Check for a busy or overloaded router."));
        if (dns.Any(d => d.Ms is null)) f.Add("Some DNS lookups failed.");
        else if (dns.Count > 0 && dns.Average(d => d.Ms!.Value) > 150) f.Add($"DNS lookups are slow (avg {dns.Average(d => d.Ms!.Value):0} ms). A different DNS server (Networking → DNS) may help page loads; it does not change in-game ping.");
        f.Add("This is generic ping to public servers. Fortnite's real ping is shown in-game: Settings → Game UI → Net Debug Stats.");
        return f;
    }
}
