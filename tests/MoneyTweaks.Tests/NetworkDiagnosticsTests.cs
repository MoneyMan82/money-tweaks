using MoneyTweaks.Core.Features;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class NetworkDiagnosticsTests(ITestOutputHelper output)
{
    private static EndpointResult E(string label, int sent, int lost, double avg, double jitter) =>
        new(label, "x", new PingStats(sent, lost, avg, avg, avg, avg, jitter));

    [Fact]
    public void Diagnosis_separates_home_network_from_internet_problems()
    {
        var dns = new List<(string, double?)> { ("a", 20.0) };
        var local = NetworkDiagnostics.Diagnose([E("Router", 80, 6, 9, 22), E("Cloudflare", 80, 6, 30, 25)], dns, "Wi-Fi 866 Mbps");
        Assert.Contains(local, f => f.Contains("inside your home network") && f.Contains("Wi-Fi"));
        var isp = NetworkDiagnostics.Diagnose([E("Router", 80, 0, 1, 0.3), E("Cloudflare", 80, 5, 30, 18), E("Google", 80, 4, 31, 15)], dns, "Ethernet 1000 Mbps");
        Assert.Contains(isp, f => f.Contains("ISP"));
        var ok = NetworkDiagnostics.Diagnose([E("Router", 80, 0, 1, 0.2), E("Cloudflare", 80, 0, 12, 0.8)], dns, "Ethernet 1000 Mbps");
        Assert.Contains(ok, f => f.StartsWith("Connection is stable"));
        Assert.All([local, isp, ok], l => Assert.Contains(l, f => f.Contains("Net Debug Stats"))); // never presented as game ping
    }

    [Fact]
    public async Task Real_diagnostic_runs()
    {
        var r = await NetworkDiagnostics.RunAsync(4, null, CancellationToken.None);
        foreach (var e in r.Endpoints) output.WriteLine($"{e.Label} {e.Host}: avg {e.Stats.AvgMs:0.0} jitter {e.Stats.JitterMs:0.0} loss {e.Stats.LossPercent:0.#}% ({e.Stats.Sent})");
        foreach (var f in r.Findings) output.WriteLine("• " + f);
        Assert.True(r.Endpoints.Count >= 2);
        Assert.All(r.Endpoints, e => Assert.InRange(e.Stats.Sent, 2, 20)); // ~16 when idle; fewer under parallel test load
    }
}
