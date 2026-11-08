using System.Text.RegularExpressions;

namespace MoneyTweaks.AgentCenter.Core;

public sealed record RegisteredAgent(string Name, string Description, string? Model, bool ReadOnly, bool Worktree, string Group);

/// <summary>The team as defined in .claude/agents/*.md (frontmatter name, description, tools, model, isolation).</summary>
public static partial class AgentRegistry
{
    [GeneratedRegex(@"^---\r?\n(.*?)\r?\n---", RegexOptions.Singleline)] private static partial Regex Front();

    public static List<RegisteredAgent> Load(Workspace ws)
    {
        var list = new List<RegisteredAgent>();
        if (!Directory.Exists(ws.AgentsFolder)) return list;
        foreach (var f in Directory.GetFiles(ws.AgentsFolder, "*.md").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            var m = Front().Match(text);
            if (!m.Success) continue;
            string? Field(string k) => Regex.Match(m.Groups[1].Value, $@"^{k}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } x ? x.Groups[1].Value.Trim() : null;
            var name = Field("name");
            if (string.IsNullOrEmpty(name)) continue;
            var tools = Field("tools") ?? "";
            var hookMode = Regex.Match(m.Groups[1].Value, @"agent_guard\.py""?\s+(\w+)").Groups[1].Value;
            list.Add(new RegisteredAgent(name, Field("description") ?? "", Field("model"),
                ReadOnly: !tools.Contains("Edit") && !tools.Contains("Write"),
                Worktree: Field("isolation") == "worktree",
                Group: GroupOf(name, hookMode)));
        }
        return list;
    }

    private static string GroupOf(string name, string mode) => name switch
    {
        "chief-coordinator" => "Lead",
        "developer" or "ui-designer" or "web-developer" or "docs-writer" => "Building",
        "tester" or "code-reviewer" or "security-reviewer" or "license-auditor" or "ux-reviewer" or "ci-investigator" => "Verification",
        "process-improver" => "Improvement",
        _ when name.StartsWith("test-agent", StringComparison.Ordinal) => "Pipeline test",
        _ when mode == "developer" => "Building",
        _ => "Discovery",
    };
}
