using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Views;

/// <summary>
/// One row per team member: every registered agent (.claude/agents) with its most recent instance, plus instances of
/// agent types that aren't registered (built-in Explore, general-purpose...) and the sessions' main agents.
/// </summary>
public sealed record Lane(string Name, string Group, RegisteredAgent? Registered, AgentInstance? Instance, int Runs);

public static class Lanes
{
    public static List<Lane> Build(List<RegisteredAgent> registry, TeamState s)
    {
        var byType = s.Agents.Values.Where(a => !a.IsMain).GroupBy(a => a.Type).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.LastSeen).ToList());
        var mains = s.Agents.Values.Where(a => a.IsMain).OrderByDescending(a => a.LastSeen).ToList();
        var lanes = new List<Lane>();
        foreach (var r in registry)
        {
            if (r.Name == "chief-coordinator")
            {
                // The coordinator runs as a session's main agent: the latest main agent that delegated work, else the latest.
                var coord = mains.FirstOrDefault(m => s.Edges.Values.Any(e => e.From == m.Id)) ?? mains.FirstOrDefault();
                lanes.Add(new Lane(r.Name, r.Group, r, coord, mains.Count));
                continue;
            }
            byType.TryGetValue(r.Name, out var inst);
            lanes.Add(new Lane(r.Name, r.Group, r, inst?.FirstOrDefault(), inst?.Count ?? 0));
        }
        foreach (var (type, inst) in byType.Where(kv => registry.All(r => r.Name != kv.Key)))
            lanes.Add(new Lane(type, "Other agents", null, inst[0], inst.Count));
        return lanes;
    }

    public static readonly string[] GroupOrder = ["Lead", "Discovery", "Building", "Verification", "Improvement", "Pipeline test", "Other agents"];
}
