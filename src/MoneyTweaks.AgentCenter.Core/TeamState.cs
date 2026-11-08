namespace MoneyTweaks.AgentCenter.Core;

public sealed record Decision(DateTimeOffset Time, string What, string? Why);
public sealed record ToolUse(DateTimeOffset Time, string Tool, string? Target, bool? Ok, long? Ms);
public sealed record Note(DateTimeOffset Time, string Text);

/// <summary>One running (or finished) agent: a Claude Code subagent instance, or a session's main agent.</summary>
public sealed class AgentInstance
{
    public required string Id { get; init; }
    public required string Type { get; set; }
    public required string Session { get; init; }
    public string Status { get; set; } = AgentStatus.Queued;
    public string? Task { get; set; }
    public string? Phase { get; set; }
    public string? Summary { get; set; }
    public string? Next { get; set; }
    public string? NeedsHelp { get; set; }
    public string? ParentId { get; set; }
    public string? Worktree { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? Ended { get; set; }
    public ToolUse? CurrentTool { get; set; }
    public string? StatusBeforePause { get; set; }
    public List<Note> Progress { get; } = [];
    public List<string> Findings { get; } = [];
    public List<Decision> Decisions { get; } = [];
    public List<Note> Errors { get; } = [];
    public List<ToolUse> Tools { get; } = [];
    public List<string> Sources { get; } = [];
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Peers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TodoItem> Todos { get; set; } = [];
    public List<string> CompletedSubtasks { get; } = [];
    public string? Report { get; set; }
    public TestRun? LastTests { get; set; }

    public bool IsMain => Id == "main" || Id.StartsWith("main:");
    public TimeSpan Elapsed(DateTimeOffset now) => (Ended ?? (AgentStatus.IsActive(Status) || Status == AgentStatus.Paused || Status == AgentStatus.Blocked ? now : LastSeen)) - Started;
    /// <summary>Active but nothing heard for a while: its session may have been closed.</summary>
    public bool IsStale(DateTimeOffset now) => Ended is null && AgentStatus.IsActive(Status) && now - LastSeen > TimeSpan.FromMinutes(15);
}

public sealed class Edge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Kind { get; init; }   // "assign" or "message"
    public string? Label { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset LastActivity { get; set; }
    public int Count { get; set; }
}

public sealed class FileActivity
{
    public required string Path { get; init; }
    public required string Relative { get; init; }
    public string? AgentId { get; set; }
    public string? AgentType { get; set; }
    /// <summary>"agent" (the agent's own Edit/Write, confirmed by Claude Code) and/or "filesystem" (seen on disk).</summary>
    public HashSet<string> Confirmed { get; } = new(StringComparer.Ordinal);
    public string? Branch { get; set; }
    public int? Added { get; set; }
    public int? Removed { get; set; }
    public int Changes { get; set; }
    public DateTimeOffset LastChange { get; set; }
    public TestRun? TestsAfter { get; set; }
}

public sealed record ResearchItem(DateTimeOffset Time, string AgentId, string AgentType, string Kind, string Text, string? Link);

public sealed record FeedItem(DateTimeOffset Time, string AgentId, string AgentType, string Kind, string Text, string? Detail, string? Status, string EventId)
{
    /// <summary>Increasing number, so views can tell which items are new even after old ones are trimmed.</summary>
    public long Seq { get; init; }
}

/// <summary>
/// One line of the team conversation: the owner's instruction, a brief from one agent to another, a report back, a
/// message, what an agent wrote while working (from its transcript), a progress note, or a tool call.
/// </summary>
public sealed class ChatItem
{
    public required DateTimeOffset Time { get; init; }
    public required string FromId { get; init; }
    public required string Kind { get; init; }   // owner, brief, report, message, say, note, tool, system
    public string? ToName { get; set; }
    public string? Summary { get; set; }
    public string? Text { get; set; }
    public string? UseId { get; init; }
    public required string EventId { get; init; }
    public long Seq { get; set; }
}

public sealed class Stats
{
    public int ActiveAgents, CompletedTasks, InProgress, Failed, ResearchDiscoveries, CodeChanges, TestsPassed, TestsFailed, Delegations, Errors;
    public DateTimeOffset? First, Last;
}

/// <summary>
/// Turns the event stream into the current picture of the team. Pure and deterministic: replaying the same events gives
/// the same state (used for live view, history and replay).
/// </summary>
public sealed class TeamState
{
    public Dictionary<string, AgentInstance> Agents { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Edge> Edges { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FileActivity> FileMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ResearchItem> Research { get; } = [];
    public List<FeedItem> Feed { get; } = [];
    public List<ChatItem> Conversation { get; } = [];
    private readonly Dictionary<string, ChatItem> _chatByUse = new(StringComparer.Ordinal);
    /// <summary>Sessions whose main agent runs the team (its instruction was the cycle, a brainstorm or /cycle).</summary>
    private readonly HashSet<string> _coordinatorSessions = new(StringComparer.Ordinal);
    private long _chatSeq;
    public Stats Stats { get; } = new();
    /// <summary>Latest result of each test suite (a later run replaces an earlier one), for the "tests now" figure.</summary>
    public Dictionary<string, (DateTimeOffset Time, TestRun Run)> LatestTests { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Workspace? Workspace { get; set; }
    public int MaxFeed { get; set; } = 5000;
    private readonly Dictionary<string, Queue<(string From, string? Label, string? Task)>> _pendingDelegations = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private long _seq;

    public static string Key(string session, string agent) => agent == "main" ? "main:" + session : agent;

    public AgentInstance Get(AgentEvent e)
    {
        var key = Key(e.Session, e.Agent);
        if (!Agents.TryGetValue(key, out var a))
        {
            a = new AgentInstance { Id = key, Type = e.Agent == "main" ? "main" : e.AgentType ?? "agent", Session = e.Session, Started = e.Time, LastSeen = e.Time };
            Agents[key] = a;
        }
        if (a.Type == "agent" && e.AgentType is { } t) a.Type = t;
        return a;
    }

    public void Apply(AgentEvent e)
    {
        if (!_seen.Add(e.Id)) return;
        Stats.First = Stats.First is { } f && f < e.Time ? f : e.Time;
        Stats.Last = Stats.Last is { } l && l > e.Time ? l : e.Time;

        if (e.Source == "filesystem") { ApplyFileSystem(e); return; }
        // Claude Code also reports stops of its own helper agents (no type, never started visibly): not team members.
        if (e.Type == "agent_stop" && e.AgentType is null && !Agents.ContainsKey(Key(e.Session, e.Agent))) return;
        var a = Get(e);
        a.LastSeen = e.Time > a.LastSeen ? e.Time : a.LastSeen;
        if (a.Worktree is null && Workspace?.WorktreeName(e.Cwd) is { } wt) a.Worktree = wt;
        if (e.Status is { } st && e.Type != "tool_end" && a.Status != AgentStatus.Paused) a.Status = st;

        Chat(e, a);
        switch (e.Type)
        {
            case "say":
                return;   // conversation only
            case "brief":
            case "message" when e.Source == "transcript":
                return;   // hook "delegate"/"message" events already drive the graph and statuses
            case "report" when e.Source == "transcript":
                if (e.Message is not null) a.Report = e.Message;
                return;
            case "task":
                if (IsCoordinatorPrompt(e.Task)) _coordinatorSessions.Add(e.Session);
                a.Task = e.Task; a.Ended = null; a.Report = null;
                if (a.IsMain) a.Started = e.Time;
                Add(e, a, "task", "New task: " + Short(e.Task, 160), e.Task);
                break;
            case "agent_start":
                a.Started = e.Time; a.Ended = null; a.Status = AgentStatus.Planning;
                if (_pendingDelegations.TryGetValue(e.Session + "|" + a.Type, out var q) && q.Count > 0)
                {
                    var (from, label, task) = q.Dequeue();
                    a.ParentId = from; a.Task = label ?? task;
                    var edge = EdgeFor(from, a.Id, "assign"); edge.Label = label;
                    if (Agents.TryGetValue(from, out var p)) { p.Peers.Add(a.Id); a.Peers.Add(from); }
                }
                Add(e, a, "start", $"{a.Type} started" + (a.Task is null ? "" : ": " + Short(a.Task, 140)), null);
                break;
            case "agent_stop":
                a.Ended = e.Time; a.CurrentTool = null;
                if (a.Status != AgentStatus.Failed) a.Status = AgentStatus.Completed;
                Stats.CompletedTasks++;
                if (a.ParentId is { } pid && Edges.TryGetValue(EdgeKey(pid, a.Id, "assign"), out var done)) { done.Completed = true; done.LastActivity = e.Time; }
                Add(e, a, "stop", $"{a.Type} finished", null);
                break;
            case "report":
                a.Report = e.Summary; a.Status = AgentStatus.Completed; a.CurrentTool = null;
                Add(e, a, "report", "Reported back: " + Short(e.Summary, 220), e.Summary);
                break;
            case "turn_end":
                a.CurrentTool = null;
                if (a.IsMain) a.Status = AgentStatus.Idle;
                break;
            case "delegate":
                var to = e.To ?? "general-purpose";
                var k = e.Session + "|" + to;
                if (!_pendingDelegations.TryGetValue(k, out var dq)) _pendingDelegations[k] = dq = new();
                dq.Enqueue((a.Id, e.Summary, e.Task));
                Stats.Delegations++;
                Add(e, a, "delegate", $"Assigned to {to}: {e.Summary}", e.Task);
                break;
            case "message":
                if (e.To is { } target)
                {
                    var toId = Agents.ContainsKey(target) ? target : Agents.Values.FirstOrDefault(x => x.Type == target && x.Ended is null)?.Id ?? target;
                    EdgeFor(a.Id, toId, "message").Label = Short(e.Summary, 80);
                    a.Peers.Add(toId);
                    if (Agents.TryGetValue(toId, out var peer)) peer.Peers.Add(a.Id);
                }
                Add(e, a, "message", $"Message to {e.To}: {Short(e.Summary, 160)}", e.Summary);
                break;
            case "progress":
                if (e.Task is not null) a.Task = e.Task;
                if (e.Phase is not null) a.Phase = e.Phase;
                if (e.Summary is not null) { a.Summary = e.Summary; a.Progress.Add(new(e.Time, e.Summary)); }
                if (e.Next is not null) a.Next = e.Next;
                a.NeedsHelp = e.NeedsHelp ?? a.NeedsHelp;
                foreach (var fnd in e.Findings ?? []) { a.Findings.Add(fnd); Research.Add(new(e.Time, a.Id, a.Type, "finding", fnd, null)); Stats.ResearchDiscoveries++; }
                foreach (var s in e.Sources ?? []) { if (!a.Sources.Contains(s)) a.Sources.Add(s); Research.Add(new(e.Time, a.Id, a.Type, "source", s, s)); }
                if (e.Decision is not null) a.Decisions.Add(new(e.Time, e.Decision, e.Why));
                if (e.Error is not null || e.Blocker is not null) { a.Errors.Add(new(e.Time, e.Error ?? e.Blocker!)); Stats.Errors++; }
                foreach (var file in e.Files ?? []) if (Resolve(file, e.Cwd) is { } full) a.Files.Add(full);
                Add(e, a, "progress", e.Summary ?? "Progress update", Detail(e));
                break;
            case "todos":
                a.Todos = e.Todos ?? [];
                foreach (var t in a.Todos.Where(t => t.Status == "completed" && t.Content is not null))
                    if (!a.CompletedSubtasks.Contains(t.Content!)) a.CompletedSubtasks.Add(t.Content!);
                if (e.Phase is not null) { a.Phase = e.Phase; a.Summary = e.Phase; }
                Add(e, a, "plan", e.Phase is null ? "Updated its plan" : "Now: " + e.Phase, string.Join("\n", a.Todos.Select(t => $"[{t.Status}] {t.Content}")));
                break;
            case "research":
                if (e.Query is not null && e.Sources is null) Research.Add(new(e.Time, a.Id, a.Type, "search", e.Query, null));
                foreach (var s in e.Sources ?? []) { if (!a.Sources.Contains(s)) a.Sources.Add(s); Research.Add(new(e.Time, a.Id, a.Type, "source", s, s)); }
                a.CurrentTool = new(e.Time, e.Tool?.Name ?? "Web", e.Tool?.Target, null, null);
                Add(e, a, "research", e.Summary ?? "Researching", e.Query);
                break;
            case "tool_start":
                a.CurrentTool = new(e.Time, e.Tool?.Name ?? "tool", e.Tool?.Target, null, null);
                foreach (var file in e.Files ?? []) if (Resolve(file, e.Cwd) is { } full && e.Tool?.Name is "Edit" or "Write" or "MultiEdit" or "NotebookEdit") a.Files.Add(full);
                break;
            case "tool_end":
                if (e.Tool is { } ti) { a.Tools.Add(new(e.Time, ti.Name ?? "tool", ti.Target, ti.Ok, ti.Ms)); if (a.Tools.Count > 300) a.Tools.RemoveAt(0); }
                a.CurrentTool = null;
                if (e.Error is not null) { a.Errors.Add(new(e.Time, e.Error)); Stats.Errors++; Add(e, a, "error", e.Summary ?? e.Error, e.Error); }
                break;
            case "file_change":
                a.Tools.Add(new(e.Time, e.Tool?.Name ?? "Edit", e.Tool?.Target, true, e.Tool?.Ms));
                a.CurrentTool = null;
                foreach (var file in e.Files ?? [])
                {
                    if (Resolve(file, e.Cwd) is not { } full) continue;
                    a.Files.Add(full);
                    var fa = FileFor(full);
                    fa.AgentId = a.Id; fa.AgentType = a.Type; fa.Confirmed.Add("agent"); fa.Changes++; fa.LastChange = e.Time;
                    if (e.Lines?.Added is { } add) fa.Added = (fa.Added ?? 0) + add;
                    if (e.Lines?.Removed is { } rem) fa.Removed = (fa.Removed ?? 0) + rem;
                    if (e.Branch is not null) fa.Branch = e.Branch;
                    Stats.CodeChanges++;
                }
                Add(e, a, "code", e.Summary ?? "Changed a file", string.Join("\n", e.Files ?? []));
                break;
            case "test_result":
                a.CurrentTool = null;
                if (e.Tests is { Count: > 0 } tests)
                {
                    var sum = new TestRun { Passed = tests.Sum(t => t.Passed), Failed = tests.Sum(t => t.Failed), Skipped = tests.Sum(t => t.Skipped), Total = tests.Sum(t => t.Total) };
                    a.LastTests = sum;
                    Stats.TestsPassed += sum.Passed; Stats.TestsFailed += sum.Failed;
                    foreach (var t in tests)
                    {
                        var suite = t.Suite ?? "tests";
                        if (!LatestTests.TryGetValue(suite, out var prev) || prev.Time <= e.Time) LatestTests[suite] = (e.Time, t);
                    }
                    foreach (var path in a.Files) if (FileMap.TryGetValue(path, out var changed) && changed.LastChange <= e.Time) changed.TestsAfter = sum;
                    Add(e, a, sum.Failed > 0 ? "error" : "test", e.Summary ?? $"Tests: {sum.Passed} passed, {sum.Failed} failed", string.Join("\n", tests.Select(t => $"{t.Suite}: {t.Passed} passed, {t.Failed} failed, {t.Skipped} skipped")));
                }
                break;
            case "blocked":
                a.Errors.Add(new(e.Time, e.Blocker ?? "Blocked")); Stats.Errors++;
                Add(e, a, "blocked", "Blocked by the safety guard: " + Short(e.Blocker, 200), e.Blocker);
                break;
            case "paused":
                if (a.Status != AgentStatus.Paused) a.StatusBeforePause = a.Status;
                a.Status = AgentStatus.Paused;
                Add(e, a, "control", e.Summary ?? "Paused", null);
                break;
            case "resumed":
                a.Status = a.StatusBeforePause ?? AgentStatus.Planning; a.StatusBeforePause = null;
                Add(e, a, "control", e.Summary ?? "Resumed", null);
                break;
            case "notification":
                if (e.Blocker is not null) a.Errors.Add(new(e.Time, e.Blocker));
                Add(e, a, "notice", e.Summary ?? "Notification", null);
                break;
            case "session_start":
            case "session_end":
                Add(e, a, "session", e.Summary ?? e.Type, null);
                if (e.Type == "session_end") foreach (var x in Agents.Values.Where(x => x.Session == e.Session && x.Ended is null)) { x.Ended = e.Time; if (AgentStatus.IsActive(x.Status)) x.Status = AgentStatus.Idle; }
                break;
        }
    }

    /// <summary>Adds the event to the conversation (transcript text wins over the hook's shorter copy of the same call).</summary>
    private void Chat(AgentEvent e, AgentInstance a)
    {
        string? kind = e.Type switch
        {
            "task" => "owner", "say" => "say", "brief" or "delegate" => "brief", "report" => "report", "message" => "message",
            "progress" => "note", "agent_start" or "agent_stop" or "paused" or "resumed" => "system", "blocked" => "note",
            "tool_start" => "tool", "research" => "tool", "test_result" => "note", _ => null,
        };
        if (kind is null) return;
        var useId = e.Tool?.UseId;
        var to = kind switch
        {
            "brief" or "message" => e.To,
            "report" => a.ParentId is { } p && Agents.TryGetValue(p, out var parent) ? (parent.IsMain ? "chief-coordinator" : parent.Type) : "chief-coordinator",
            "owner" => a.IsMain ? "chief-coordinator" : a.Type,
            _ => null,
        };
        var text = kind switch
        {
            "owner" => e.Task,
            "brief" => e.Message ?? e.Task,
            "note" => string.Join("\n", new[] { e.Summary, e.Phase is null ? null : "Phase: " + e.Phase,
                e.Findings is { Count: > 0 } f ? "Found: " + string.Join("; ", f) : null,
                e.Decision is null ? null : "Decided: " + e.Decision + (e.Why is null ? "" : " — " + e.Why),
                e.Next is null ? null : "Next: " + e.Next, e.NeedsHelp is null ? null : "Needs help from: " + e.NeedsHelp,
                e.Error ?? e.Blocker }.Where(x => !string.IsNullOrWhiteSpace(x))),
            "tool" => e.Tool?.Target ?? e.Summary,
            _ => e.Message ?? e.Summary,
        };
        var summary = kind switch
        {
            "system" => e.Summary,
            "tool" => (e.Tool?.Name ?? "Tool") + (e.Tool?.Target is { } t ? ": " + t : ""),
            _ => e.Summary,
        };
        if (useId is not null && kind is "brief" or "report" or "message" && _chatByUse.TryGetValue(useId, out var existing))
        {
            // Same call seen twice (hook + transcript): keep one, with the longer text.
            if ((text?.Length ?? 0) > (existing.Text?.Length ?? 0)) existing.Text = text;
            existing.ToName ??= to;
            return;
        }
        if (kind == "report" && text is { Length: > 0 })
        {
            // Agents usually write their final message and hand the same text back: show it once, as the report.
            var dup = Conversation.LastOrDefault(c => c.FromId == a.Id && c.Kind == "say" && (e.Time - c.Time).Duration() < TimeSpan.FromMinutes(2));
            if (dup?.Text is { } said && Similar(said, text)) Conversation.Remove(dup);
        }
        var item = new ChatItem { Time = e.Time, FromId = a.Id, Kind = kind, ToName = to, Summary = summary, Text = text, UseId = useId, EventId = e.Id, Seq = ++_chatSeq };
        if (useId is not null && kind is "brief" or "report" or "message") _chatByUse[useId] = item;
        // Transcript lines can arrive a moment after hook events: keep the list in time order.
        var idx = Conversation.Count;
        while (idx > 0 && Conversation[idx - 1].Time > item.Time) idx--;
        Conversation.Insert(idx, item);
        if (Conversation.Count > MaxFeed * 2) Conversation.RemoveRange(0, Conversation.Count - MaxFeed * 2);
    }

    private static bool Similar(string a, string b)
    {
        static string N(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var x = N(a); var y = N(b);
        var n = Math.Min(160, Math.Min(x.Length, y.Length));
        return n > 20 && string.Compare(x, 0, y, 0, n, StringComparison.Ordinal) == 0;
    }

    /// <summary>Display name of an agent instance: the main agent is the Chief Coordinator once it delegates.</summary>
    public string NameOf(string id) => Agents.TryGetValue(id, out var a)
        ? (a.IsMain ? (_coordinatorSessions.Contains(a.Session) ? "chief-coordinator" : "Claude · your session") : a.Type)
        : id;

    /// <summary>The cycle, a brainstorm or /cycle: the main agent of that session is the Chief Coordinator.</summary>
    public static bool IsCoordinatorPrompt(string? task) => task is not null &&
        (task.StartsWith("Run one autonomous development cycle", StringComparison.OrdinalIgnoreCase)
         || task.StartsWith("Act as the Chief Coordinator", StringComparison.OrdinalIgnoreCase)
         || task.TrimStart().StartsWith("/cycle", StringComparison.OrdinalIgnoreCase)
         || task.Contains("<command-name>/cycle</command-name>", StringComparison.OrdinalIgnoreCase)
         || task.TrimStart().StartsWith("/build", StringComparison.OrdinalIgnoreCase));

    public bool IsCoordinator(AgentInstance a) => a.IsMain && _coordinatorSessions.Contains(a.Session);

    private static string? Detail(AgentEvent e)
    {
        var parts = new List<string>();
        if (e.Phase is not null) parts.Add("Phase: " + e.Phase);
        if (e.Findings is { Count: > 0 }) parts.Add("Found: " + string.Join("; ", e.Findings));
        if (e.Decision is not null) parts.Add("Decision: " + e.Decision + (e.Why is null ? "" : " — because " + e.Why));
        if (e.Next is not null) parts.Add("Next: " + e.Next);
        if (e.NeedsHelp is not null) parts.Add("Needs help from: " + e.NeedsHelp);
        if (e.Error is not null) parts.Add("Error: " + e.Error);
        if (e.Blocker is not null) parts.Add("Blocked: " + e.Blocker);
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private void ApplyFileSystem(AgentEvent e)
    {
        foreach (var file in e.Files ?? [])
        {
            if (Resolve(file, null) is not { } full) continue;
            var fa = FileFor(full);
            fa.Confirmed.Add("filesystem"); fa.Changes++; fa.LastChange = e.Time;
            if (e.Branch is not null) fa.Branch = e.Branch;
            if (e.Lines is { } l) { fa.Added = l.Added; fa.Removed = l.Removed; }   // git numstat of the working copy
            if (fa.AgentId is null)
            {
                // Attribute only with evidence: the agent working in that worktree, or one that touched the file.
                var wt = Workspace?.WorktreeName(full);
                var owner = Agents.Values.Where(x => (wt is not null && x.Worktree == wt) || x.Files.Contains(full)).OrderByDescending(x => x.LastSeen).FirstOrDefault();
                if (owner is not null) { fa.AgentId = owner.Id; fa.AgentType = owner.Type; }
            }
            Feed.Add(new(e.Time, fa.AgentId ?? "filesystem", fa.AgentType ?? "file monitor", "file", "File changed on disk: " + fa.Relative, fa.AgentId is null ? "Not attributed to an agent (no agent event or worktree links it)." : null, null, e.Id) { Seq = ++_seq });
            Trim();
        }
    }

    private string? Resolve(string file, string? cwd) => Workspace is null ? file : Workspace.Resolve(file, cwd);

    private FileActivity FileFor(string full)
    {
        if (!FileMap.TryGetValue(full, out var fa))
            FileMap[full] = fa = new FileActivity { Path = full, Relative = Workspace?.Relative(full) ?? full };
        return fa;
    }

    private static string EdgeKey(string from, string to, string kind) => from + "|" + to + "|" + kind;

    private Edge EdgeFor(string from, string to, string kind)
    {
        var k = EdgeKey(from, to, kind);
        if (!Edges.TryGetValue(k, out var edge)) Edges[k] = edge = new Edge { From = from, To = to, Kind = kind };
        edge.Count++; edge.LastActivity = Stats.Last ?? DateTimeOffset.Now; edge.Completed = false;
        return edge;
    }

    private void Add(AgentEvent e, AgentInstance a, string kind, string text, string? detail)
    {
        Feed.Add(new(e.Time, a.Id, a.Type, kind, text, detail, a.Status, e.Id) { Seq = ++_seq });
        Trim();
    }

    private void Trim() { if (Feed.Count > MaxFeed) Feed.RemoveRange(0, Feed.Count - MaxFeed); }

    private static string Short(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Agents that delegated work which hasn't finished yet are waiting for responses.</summary>
    public bool IsWaiting(AgentInstance a) => Edges.Values.Any(x => x.From == a.Id && x.Kind == "assign" && !x.Completed);

    public Stats Compute(DateTimeOffset now)
    {
        Stats.ActiveAgents = Agents.Values.Count(x => x.Ended is null && AgentStatus.IsActive(x.Status) && !x.IsStale(now));
        Stats.InProgress = Agents.Values.Count(x => x.Ended is null && (AgentStatus.IsActive(x.Status) || x.Status is AgentStatus.Paused or AgentStatus.Blocked) && !x.IsMain);
        Stats.Failed = Agents.Values.Count(x => x.Status == AgentStatus.Failed);
        return Stats;
    }
}
