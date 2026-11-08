using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Tests;

public sealed class ConversationTests : IDisposable
{
    private readonly string _projects = Directory.CreateTempSubdirectory("mt-projects-").FullName;
    private const string Session = "aaaabbbb-cccc-dddd-eeee-ffff00001111";

    // Shapes copied from real Claude Code transcripts on this PC (content shortened).
    private static string Assistant(string uuid, string ts, string contentJson) =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + ts + "\",\"agentId\":\"x\",\"message\":{\"role\":\"assistant\",\"content\":" + contentJson + "}}";

    private string MainFile => Path.Combine(_projects, "C--proj", Session + ".jsonl");
    private string AgentFile(string id) => Path.Combine(_projects, "C--proj", Session, "subagents", "agent-" + id + ".jsonl");

    [Fact]
    public void Transcripts_give_briefs_statements_and_reports_but_never_thinking()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile("a1"))!);
        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(MainFile,
        [
            """{"type":"user","uuid":"u0","timestamp":"2026-11-08T12:00:00Z","message":{"role":"user","content":"do it"}}""",
            Assistant("m1", now.AddSeconds(-30).ToString("O"), """[{"type":"thinking","thinking":"SECRET INNER THOUGHTS"},{"type":"text","text":"Main is green, so I'm taking T-006 next."},{"type":"tool_use","id":"toolu_1","name":"Agent","input":{"description":"Design fix T-006","subagent_type":"architect","prompt":"Design a fix for the backup bug. Read TweakEngine.cs first."}}]"""),
        ]);
        File.WriteAllLines(AgentFile("a1"),
        [
            Assistant("s1", now.AddSeconds(-20).ToString("O"), """[{"type":"text","text":"Reading TweakEngine.cs now."},{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"x"}}]"""),
            Assistant("s2", now.AddSeconds(-10).ToString("O"), """[{"type":"tool_use","id":"toolu_3","name":"SendMessage","input":{"to":"developer","message":"Use OpKey for matching.","summary":"OpKey tip"}},{"type":"tool_use","id":"toolu_4","name":"SubagentHandback","input":{"message":"Design ready: match backups by OpKey. password=hunter2"}}]"""),
        ]);
        var events = new List<AgentEvent>();
        var r = new TranscriptReader(events.Add, now.AddHours(-1), _projects);
        Assert.Equal(MainFile, r.MainTranscript(Session));
        Assert.Equal(5, r.Poll([(Session, "main"), (Session, "a1")]));   // main: say + brief; agent: say + message + report
        Assert.DoesNotContain(events, e => (e.Message + e.Summary).Contains("SECRET"));
        Assert.Contains(events, e => e.Type == "say" && e.Agent == "main" && e.Message == "Main is green, so I'm taking T-006 next.");
        var brief = Assert.Single(events, e => e.Type == "brief");
        Assert.Equal("architect", brief.To);
        Assert.Equal("Design fix T-006", brief.Summary);
        Assert.StartsWith("Design a fix for the backup bug.", brief.Message);
        Assert.Equal("toolu_1", brief.Tool!.UseId);
        Assert.Contains(events, e => e.Type == "say" && e.Agent == "a1" && e.Message == "Reading TweakEngine.cs now.");
        Assert.Contains(events, e => e.Type == "message" && e.To == "developer" && e.Message == "Use OpKey for matching.");
        var report = Assert.Single(events, e => e.Type == "report");
        Assert.DoesNotContain("hunter2", report.Message);          // sanitized
        // Re-reading finds nothing new; ids are stable.
        Assert.Equal(0, r.Poll([(Session, "main"), (Session, "a1")]));
        var again = new List<AgentEvent>();
        new TranscriptReader(again.Add, now.AddHours(-1), _projects).Poll([(Session, "main"), (Session, "a1")]);
        Assert.Equal(events.Select(e => e.Id).OrderBy(x => x), again.Select(e => e.Id).OrderBy(x => x));
    }

    [Fact]
    public void Only_files_inside_the_projects_folder_are_read_and_ids_are_checked()
    {
        var r = new TranscriptReader(_ => { }, DateTimeOffset.UtcNow.AddHours(-1), _projects);
        Assert.Null(r.MainTranscript("..\\..\\Windows"));
        Assert.Null(r.AgentTranscript(Session, "../x"));
        var outside = Path.Combine(Path.GetTempPath(), "mt-outside-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(outside, Assistant("z", DateTimeOffset.UtcNow.ToString("O"), """[{"type":"text","text":"hi"}]""") + "\n");
        try { Assert.Equal(0, r.ReadNew(outside, Session, "main")); }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Conversation_merges_the_hooks_short_copy_with_the_transcripts_full_text()
    {
        var s = new TeamState();
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        s.Apply(new AgentEvent { Id = "p", Ts = t0, Session = Session, Agent = "main", AgentType = "main", Type = "task", Task = "Run one autonomous development cycle for Money Tweaks." });
        // Hook: delegate (short task text) then the subagent starts.
        s.Apply(new AgentEvent { Id = "d", Ts = t0 + 1, Session = Session, Agent = "main", AgentType = "main", Type = "delegate", To = "architect", Summary = "Design fix T-006", Task = "Design a fix…", Tool = new ToolInfo { Name = "Agent", UseId = "toolu_1" } });
        s.Apply(new AgentEvent { Id = "st", Ts = t0 + 2, Session = Session, Agent = "a1", AgentType = "architect", Type = "agent_start" });
        // Transcript: same Agent call with the full prompt, plus what the agent wrote and its report.
        s.Apply(new AgentEvent { Id = "tb", Ts = t0 + 1, Session = Session, Agent = "main", AgentType = "main", Type = "brief", Source = "transcript", To = "architect", Summary = "Design fix T-006", Message = "Design a fix for the backup bug. Read TweakEngine.cs first and then write the design.", Tool = new ToolInfo { Name = "Agent", UseId = "toolu_1" } });
        s.Apply(new AgentEvent { Id = "ts", Ts = t0 + 3, Session = Session, Agent = "a1", Type = "say", Source = "transcript", Summary = "Reading TweakEngine.cs now.", Message = "Reading TweakEngine.cs now." });
        s.Apply(new AgentEvent { Id = "pr", Ts = t0 + 4, Session = Session, Agent = "a1", AgentType = "architect", Type = "progress", Source = "agent", Summary = "Design drafted", Decision = "Match by OpKey", Why = "positions change between versions" });
        // The agent's last message repeats its report (common): it must appear once, as the report.
        s.Apply(new AgentEvent { Id = "dup", Ts = t0 + 5, Session = Session, Agent = "a1", Type = "say", Source = "transcript", Summary = "Design ready", Message = "Design ready: match backups by OpKey." });
        s.Apply(new AgentEvent { Id = "tr", Ts = t0 + 5, Session = Session, Agent = "a1", Type = "report", Source = "transcript", Summary = "Design ready", Message = "Design ready: match backups by OpKey.", Tool = new ToolInfo { Name = "SubagentHandback", UseId = "toolu_4" } });

        var chat = s.Conversation;
        Assert.Equal(["owner", "brief", "system", "say", "note", "report"], chat.Select(c => c.Kind));
        var brief = chat.Single(c => c.Kind == "brief");
        Assert.StartsWith("Design a fix for the backup bug. Read TweakEngine.cs first", brief.Text);   // full text won
        Assert.Equal("architect", brief.ToName);
        Assert.Equal("chief-coordinator", s.NameOf(brief.FromId));
        Assert.Equal("chief-coordinator", chat.Single(c => c.Kind == "report").ToName);
        Assert.Contains("Decided: Match by OpKey — positions change between versions", chat.Single(c => c.Kind == "note").Text);
        Assert.Equal("Design ready: match backups by OpKey.", s.Agents["a1"].Report);
    }

    public void Dispose() { try { Directory.Delete(_projects, true); } catch { } }
}
