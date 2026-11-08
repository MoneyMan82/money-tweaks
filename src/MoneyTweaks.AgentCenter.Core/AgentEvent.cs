using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// One structured agent event, schema "mt.agent-event/1". Produced by .claude/hooks/command_center.py from Claude Code
/// hook payloads (tool use, subagent start/stop, prompts, turns) and from agents' own progress notes
/// (`command_center.py publish`), and by this app's file monitor (source "filesystem").
/// </summary>
public sealed class AgentEvent
{
    public const string SchemaId = "mt.agent-event/1";

    public string Schema { get; set; } = SchemaId;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Unix time in milliseconds.</summary>
    public long Ts { get; set; }
    public string Session { get; set; } = "unknown";
    /// <summary>Claude Code agent id for subagents, "main" for the session's main agent, "filesystem" for the monitor.</summary>
    public string Agent { get; set; } = "main";
    /// <summary>Subagent type (e.g. "developer"), "main" for the main agent.</summary>
    public string? AgentType { get; set; }
    public string Type { get; set; } = "";
    /// <summary>"hook" (observed by Claude Code), "agent" (the agent's own progress note), "filesystem", "app".</summary>
    public string Source { get; set; } = "hook";
    public string? Cwd { get; set; }
    public string? PromptId { get; set; }
    public string? Status { get; set; }
    public string? Task { get; set; }
    public string? Phase { get; set; }
    public string? Summary { get; set; }
    /// <summary>Full text of a brief, report, message or statement (conversation view); Summary is its one-line form.</summary>
    public string? Message { get; set; }
    public string? Decision { get; set; }
    public string? Why { get; set; }
    public string? Next { get; set; }
    public string? NeedsHelp { get; set; }
    public string? Error { get; set; }
    public string? Blocker { get; set; }
    public string? To { get; set; }
    public string? Query { get; set; }
    public List<string>? Findings { get; set; }
    public List<string>? Files { get; set; }
    public List<string>? Sources { get; set; }
    public ToolInfo? Tool { get; set; }
    public LineCounts? Lines { get; set; }
    public List<TestRun>? Tests { get; set; }
    public List<TodoItem>? Todos { get; set; }
    /// <summary>Set by the app: the git branch of the file's worktree (file events).</summary>
    public string? Branch { get; set; }

    [JsonIgnore] public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Ts);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parses one line; returns null for anything that isn't a valid event of this schema.</summary>
    public static AgentEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineChars) return null;
        try
        {
            var e = JsonSerializer.Deserialize<AgentEvent>(line, Json);
            if (e is null || e.Schema != SchemaId || string.IsNullOrEmpty(e.Id) || e.Id.Length > 64 || e.Ts <= 0 || string.IsNullOrEmpty(e.Type)) return null;
            return Sanitizer.Clean(e);
        }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public const int MaxLineChars = 64 * 1024;
}

public sealed class ToolInfo
{
    public string? Name { get; set; }
    public string? Target { get; set; }
    public string? UseId { get; set; }
    public bool? Ok { get; set; }
    public long? Ms { get; set; }
}

public sealed class LineCounts
{
    public int? Added { get; set; }
    public int? Removed { get; set; }
}

public sealed class TestRun
{
    public int Failed { get; set; }
    public int Passed { get; set; }
    public int Skipped { get; set; }
    public int Total { get; set; }
    public string? Suite { get; set; }
}

public sealed class TodoItem
{
    public string? Content { get; set; }
    public string? Status { get; set; }
    public string? Active { get; set; }
}

public static class AgentStatus
{
    public const string Idle = "IDLE", Queued = "QUEUED", Planning = "PLANNING", Researching = "RESEARCHING",
        Analyzing = "ANALYZING", Coding = "CODING", Testing = "TESTING", Reviewing = "REVIEWING", Blocked = "BLOCKED",
        Completed = "COMPLETED", Failed = "FAILED", Paused = "PAUSED";

    public static readonly string[] All = [Idle, Queued, Planning, Researching, Analyzing, Coding, Testing, Reviewing, Blocked, Completed, Failed, Paused];

    public static bool IsActive(string s) => s is Planning or Researching or Analyzing or Coding or Testing or Reviewing or Queued;
}
