using System.Text.RegularExpressions;

namespace MoneyTweaks.AgentCenter.Core;

public sealed record IdeaRow(string Id, string Title, string Status, string Notes);
public sealed record TaskRow(string Id, string Type, string Title, string Status, string Branch);

/// <summary>Reads the team's own tracking files (team/IDEAS.md, team/TASKS.md) - the backlog the agents maintain.</summary>
public static partial class TeamFiles
{
    [GeneratedRegex(@"^\|\s*(I-\d+)\s*\|(.*)\|\s*$", RegexOptions.Multiline)] private static partial Regex IdeaLine();
    [GeneratedRegex(@"^\|\s*(T-\d+)\s*\|(.*)\|\s*$", RegexOptions.Multiline)] private static partial Regex TaskLine();

    private static string[] Cells(string rest) => rest.Split('|').Select(c => c.Trim()).ToArray();

    public static List<IdeaRow> Ideas(Workspace ws)
    {
        var f = Path.Combine(ws.TeamFolder, "IDEAS.md");
        if (!File.Exists(f)) return [];
        // Columns: Id | Idea | Origin | Value | Effort | Status | Notes
        return IdeaLine().Matches(File.ReadAllText(f)).Select(m =>
        {
            var c = Cells(m.Groups[2].Value);
            return new IdeaRow(m.Groups[1].Value, Get(c, 0), Get(c, 4), Get(c, 5));
        }).ToList();
    }

    public static List<TaskRow> Tasks(Workspace ws)
    {
        var f = Path.Combine(ws.TeamFolder, "TASKS.md");
        if (!File.Exists(f)) return [];
        // Columns: Id | Type / severity | Title | Idea | Owner agent | Status | Branch | Acceptance criteria
        return TaskLine().Matches(File.ReadAllText(f)).Select(m =>
        {
            var c = Cells(m.Groups[2].Value);
            return new TaskRow(m.Groups[1].Value, Get(c, 0), Get(c, 1), Get(c, 4), Get(c, 5));
        }).ToList();
    }

    private static string Get(string[] c, int i) => i < c.Length ? c[i] : "";

    public static bool IsRejected(IdeaRow r) => r.Status.Contains("reject", StringComparison.OrdinalIgnoreCase);
    public static bool IsDone(IdeaRow r) => r.Status.Contains("done", StringComparison.OrdinalIgnoreCase) || r.Status.Contains("merged", StringComparison.OrdinalIgnoreCase);
    public static bool AwaitingReview(TaskRow t) => t.Status.Contains("awaiting", StringComparison.OrdinalIgnoreCase) || t.Status.Contains("in review", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Exports a session's events (JSON Lines, one event per line, already sanitized) or a readable timeline (CSV).</summary>
public static class Exporter
{
    public static void Jsonl(IEnumerable<AgentEvent> events, string path) =>
        File.WriteAllLines(path, events.Select(e => e.ToJson()));

    public static void Csv(IEnumerable<AgentEvent> events, string path)
    {
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        File.WriteAllLines(path, new[] { "time,session,agent,agent_type,type,status,summary,files" }
            .Concat(events.Select(e => string.Join(",", Q(e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")), Q(e.Session), Q(e.Agent), Q(e.AgentType), Q(e.Type), Q(e.Status), Q(e.Summary), Q(string.Join(" ", e.Files ?? []))))));
    }
}
