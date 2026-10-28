using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Service Nexus: dependency reasoning on a small fixed graph, and a read-only check of the real SCM snapshot.</summary>
public class ServiceNexusTests(ITestOutputHelper output)
{
    private static ServiceNode N(string name, string state, int start, params string[] deps) =>
        new(name, name + " display", false, state, start, false, false, deps, [], null, null);

    // A ← B (running, auto) ← C (stopped, manual); A ← D (disabled); E depends on nothing.
    private static ServiceSnapshot Graph() => new(
    [
        N("A", "Running", 2), N("B", "Running", 2, "A"), N("C", "Stopped", 3, "B"), N("D", "Stopped", 4, "A"), N("E", "Running", 2),
    ]);

    [Fact]
    public void Dependents_are_reversed_from_declared_dependencies_nearest_first()
    {
        var s = Graph();
        Assert.Equal(["B", "D"], s.Get("A")!.Dependents);
        var all = ServiceNexus.Dependents(s, "A");
        Assert.Equal(["B", "D", "C"], all.Select(x => x.node.Name));
        Assert.Equal(2, all.Single(x => x.node.Name == "C").depth);
        Assert.Empty(ServiceNexus.Dependents(s, "E"));
    }

    [Fact]
    public void Disabling_a_running_service_shows_who_stops_who_fails_and_who_is_unaffected()
    {
        var i = ServiceNexus.Impact(Graph(), "A", 4);
        var b = i.Effects.Single(e => e.Name == "B");
        Assert.Equal(ImpactLevel.Likely, b.Level);
        Assert.Contains("stopped now", b.After);
        var c = i.Effects.Single(e => e.Name == "C");
        Assert.Equal(ImpactLevel.Possible, c.Level);                // Manual and stopped: only if something starts it
        Assert.Contains("1068", c.After);
        Assert.Equal(ImpactLevel.None, i.Effects.Single(e => e.Name == "D").Level); // disabled itself
        Assert.Equal(2, i.Affected);
        Assert.Contains(i.Warnings, w => w.Contains("stops it right away") && w.Contains("B display"));
        Assert.NotEmpty(i.Uncertainty);                              // always explains what the evidence does not prove
        Assert.Contains("Disabled, stopped now", i.After);
    }

    [Fact]
    public void Manual_never_breaks_dependents_because_the_SCM_starts_them_on_demand()
    {
        var i = ServiceNexus.Impact(Graph(), "A", 3);
        Assert.All(i.Effects, e => Assert.Equal(ImpactLevel.None, e.Level));
        Assert.Equal(0, i.Affected);
        Assert.Contains(i.Uncertainty, u => u.Contains("on demand"));
    }

    [Fact]
    public void Nothing_depending_on_it_is_still_not_called_safe()
    {
        var i = ServiceNexus.Impact(Graph(), "E", 4);
        Assert.Empty(i.Effects);
        Assert.Contains(i.Uncertainty, u => u.Contains("doesn't guarantee"));
    }

    [Fact]
    public void Real_snapshot_comes_from_the_service_control_manager()
    {
        var s = ServiceSnapshot.Read();
        output.WriteLine($"{s.Nodes.Count} services and drivers, {s.Nodes.Values.Sum(n => n.DependsOn.Count)} dependency edges");
        Assert.True(s.Nodes.Values.Count(n => !n.IsDriver) > 50);
        var rpc = s.Get("RpcSs");
        Assert.NotNull(rpc);
        Assert.True(rpc!.Running);
        Assert.True(rpc.Dependents.Count > 5, "RpcSs has many dependents on every Windows install");
        Assert.NotNull(ServiceNexus.Critical("RpcSs"));
        var impact = ServiceNexus.Impact(s, "RpcSs", 4);
        Assert.True(impact.TouchesCritical);
        // Every catalog service tweak that exists on this PC can be previewed.
        foreach (var (svc, _, start, _, _, _) in TweakCatalog.ServiceTable)
            if (s.Get(svc) is not null) Assert.NotNull(ServiceNexus.Impact(s, svc, start));
        Assert.NotNull(ServiceNexus.TweakFor("DiagTrack"));
    }
}
