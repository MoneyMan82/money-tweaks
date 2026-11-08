using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Tests;

/// <summary>
/// End-to-end: the real publisher hook (.claude/hooks/command_center.py, run with Python exactly as Claude Code runs it)
/// receives hook payloads shaped like the ones Claude Code sent on this PC, and the app's pipe listener, spool importer,
/// SQLite store and team state pick them up.
/// </summary>
public sealed class PipelineTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("mt-agentcenter-").FullName;
    private readonly string _pipe = "MTAgentCenterTest" + Guid.NewGuid().ToString("N")[..8];
    private string Data => Path.Combine(_tmp, "MoneyTweaksAgentCenter");

    private static string Repo()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "MoneyTweaks.slnx"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("repo not found");
    }

    private static string Hook => Path.Combine(Repo(), ".claude", "hooks", "command_center.py");

    /// <summary>Runs the hook like Claude Code does: JSON on stdin. Returns stdout.</summary>
    private string RunHook(object payload, string? arg = null)
    {
        var psi = new ProcessStartInfo("python") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(Hook);
        if (arg is not null) psi.ArgumentList.Add(arg);
        psi.Environment["LOCALAPPDATA"] = _tmp;
        psi.Environment["MT_AGENT_CENTER_PIPE"] = _pipe;
        using var p = Process.Start(psi)!;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        p.StandardInput.BaseStream.Write(bytes); p.StandardInput.Close();
        var outText = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "hook did not finish");
        Assert.Equal(0, p.ExitCode);
        return outText;
    }

    private const string Session = "11111111-2222-3333-4444-555555555555";

    private static Dictionary<string, object?> Payload(string ev, string? agentId = null, string? agentType = null) => new()
    {
        ["session_id"] = Session, ["hook_event_name"] = ev, ["cwd"] = @"C:\Users\x\OneDrive\Ty\ufffdp\ufffdyt\ufffd\MoneyTweaks",
        ["prompt_id"] = "p1", ["agent_id"] = agentId, ["agent_type"] = agentType,
    };

    private static Dictionary<string, object?> Tool(string ev, string tool, object input, string? agentId = null, string? agentType = null, object? response = null)
    {
        var d = Payload(ev, agentId, agentType);
        d["tool_name"] = tool; d["tool_input"] = input; d["tool_use_id"] = "toolu_" + Guid.NewGuid().ToString("N")[..10];
        if (response is not null) d["tool_response"] = response;
        if (ev == "PostToolUse") d["duration_ms"] = 12;
        return d;
    }

    private List<AgentEvent> Spooled()
    {
        var folder = Path.Combine(Data, "spool");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.jsonl").SelectMany(File.ReadAllLines).Select(AgentEvent.Parse).Where(e => e is not null).Select(e => e!).ToList()
            : [];
    }

    [Fact]
    public void Two_test_agents_full_pipeline_through_the_real_hook_pipe_spool_and_store()
    {
        var ws = new Workspace(Repo());
        using var hub = new Hub(ws, Path.Combine(_tmp, "app"), _pipe);
        // The app reads the same spool the hook writes (LOCALAPPDATA redirected for the test).
        var spool = new SpoolImporter(Path.Combine(Data, "spool"), hub.Store, hub.Ingest);
        hub.Pipe.Start(2);
        Thread.Sleep(200);

        var testFile = Path.Combine(ws.Root, "tests", "agent-center-sample.txt");

        // Coordinator (main) delegates to two test agents.
        var prompt = Payload("UserPromptSubmit");
        prompt["prompt"] = "Run the two test agents";
        RunHook(prompt);
        RunHook(Tool("PreToolUse", "Agent", new { description = "Alpha: research", subagent_type = "test-agent-alpha", prompt = "research" }));
        RunHook(Payload("SubagentStart", "a-alpha-1", "test-agent-alpha"));
        RunHook(Tool("PreToolUse", "Agent", new { description = "Beta: edit", subagent_type = "test-agent-beta", prompt = "edit" }));
        RunHook(Payload("SubagentStart", "b-beta-1", "test-agent-beta"));

        // Alpha publishes progress and research.
        RunHook(Tool("PreToolUse", "Bash", new { command = "python .claude/hooks/command_center.py publish --status 'RESEARCHING' --phase 'Competitor scan' --summary 'Comparing WinUtil and Sophia network tweaks' --finding 'WinUtil has no undo for NetBIOS' --source 'https://github.com/ChrisTitusTech/winutil' --decision 'Recommend a reversible NetBIOS tweak' --why 'Money Tweaks backs up every change' --next 'Hand back to the coordinator' --needs 'researcher'" }, "a-alpha-1", "test-agent-alpha"));
        RunHook(Tool("PreToolUse", "WebFetch", new { url = "https://learn.microsoft.com/windows/", prompt = "NetBIOS settings" }, "a-alpha-1", "test-agent-alpha"));
        // Beta edits a file and runs tests.
        RunHook(Tool("PreToolUse", "Edit", new { file_path = testFile, old_string = "a", new_string = "b\nc" }, "b-beta-1", "test-agent-beta"));
        RunHook(Tool("PostToolUse", "Edit", new { file_path = testFile, old_string = "a", new_string = "b\nc" }, "b-beta-1", "test-agent-beta", new { filePath = testFile }));
        RunHook(Tool("PostToolUse", "Bash", new { command = "dotnet test tests/X" }, "b-beta-1", "test-agent-beta",
            new { stdout = "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s - X.Tests.dll (net10.0)", stderr = "", exitCode = 0 }));
        // Both report and stop; the coordinator's turn ends.
        RunHook(Tool("PreToolUse", "SubagentHandback", new { message = "Alpha done: one reversible tweak proposed. token=ghp_abcdefghijklmnopqrstuvwxyz123456" }, "a-alpha-1", "test-agent-alpha"));
        RunHook(Payload("SubagentStop", "a-alpha-1", "test-agent-alpha"));
        RunHook(Tool("PreToolUse", "SubagentHandback", new { message = "Beta done" }, "b-beta-1", "test-agent-beta"));
        RunHook(Payload("SubagentStop", "b-beta-1", "test-agent-beta"));
        RunHook(Payload("Stop"));

        // Pipe delivered in real time...
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (hub.Received < 14 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        hub.Drain();
        // ...and the spool holds the same events; importing it adds nothing new (de-duplicated by id).
        var before = hub.Store.Count();
        spool.ImportNew();
        Assert.Equal(before, hub.Store.Count());
        Assert.Equal(Spooled().Count, hub.Store.Count());

        var s = hub.Live;
        var alpha = s.Agents["a-alpha-1"];
        var beta = s.Agents["b-beta-1"];
        var main = s.Agents[TeamState.Key(Session, "main")];
        Assert.Equal("test-agent-alpha", alpha.Type);
        Assert.Equal(AgentStatus.Completed, alpha.Status);
        Assert.Equal(AgentStatus.Completed, beta.Status);
        Assert.Equal(AgentStatus.Idle, main.Status);
        Assert.Equal(main.Id, alpha.ParentId);
        Assert.Equal("Alpha: research", alpha.Task);
        Assert.Equal("Competitor scan", alpha.Phase);
        Assert.Contains("WinUtil has no undo for NetBIOS", alpha.Findings);
        Assert.Contains(alpha.Decisions, d => d.What.StartsWith("Recommend") && d.Why == "Money Tweaks backs up every change");
        Assert.Equal("Hand back to the coordinator", alpha.Next);
        Assert.Equal("researcher", alpha.NeedsHelp);
        Assert.Contains("https://github.com/ChrisTitusTech/winutil", alpha.Sources);
        Assert.Contains("https://learn.microsoft.com/windows/", alpha.Sources);
        Assert.DoesNotContain("ghp_", alpha.Report);                  // secret redacted by the publisher
        Assert.True(s.Edges.Values.Count(x => x.Kind == "assign" && x.Completed) == 2);
        var fa = s.FileMap[Path.GetFullPath(testFile)];
        Assert.Equal("b-beta-1", fa.AgentId);
        Assert.Contains("agent", fa.Confirmed);
        Assert.Equal(2, fa.Added);
        Assert.Equal(12, beta.LastTests!.Passed);
        Assert.Equal(12, fa.TestsAfter!.Passed);
        Assert.True(s.Research.Count >= 3);

        // History: the whole session is stored and can be replayed into the same picture.
        var replay = Hub.Replay(hub.Store.Session(Session), ws);
        Assert.Equal(AgentStatus.Completed, replay.Agents["a-alpha-1"].Status);
        Assert.Contains(hub.Store.Sessions(), x => x.Id == Session && x.TestsPassed == 12 && x.Files == 1);
        Assert.NotEmpty(hub.Store.Search("WinUtil"));
    }

    [Fact]
    public void Events_sent_while_the_app_is_closed_are_kept_in_the_spool_and_imported_later()
    {
        RunHook(Tool("PreToolUse", "Read", new { file_path = "README.md" }, "x1", "researcher"));   // no app listening
        Assert.Single(Spooled());
        var ws = new Workspace(Repo());
        using var hub = new Hub(ws, Path.Combine(_tmp, "app2"), _pipe);
        var spool = new SpoolImporter(Path.Combine(Data, "spool"), hub.Store, hub.Ingest);
        Assert.Equal(1, spool.ImportNew());
        Assert.Equal(0, spool.ImportNew());            // offset remembered
        hub.Drain();
        Assert.Equal(AgentStatus.Analyzing, hub.Live.Agents["x1"].Status);
    }

    [Fact]
    public void Stop_control_refuses_the_agents_next_tool_but_lets_it_report()
    {
        Directory.CreateDirectory(Path.Combine(Data, "control"));
        File.WriteAllText(Path.Combine(Data, "control", "stop-developer"), "");
        var deny = RunHook(Tool("PreToolUse", "Edit", new { file_path = "a.cs" }, "d1", "developer"));
        using var doc = JsonDocument.Parse(deny);
        Assert.Equal("deny", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
        Assert.Equal("", RunHook(Tool("PreToolUse", "SubagentHandback", new { message = "stopping" }, "d1", "developer")).Trim());
        Assert.Equal("", RunHook(Tool("PreToolUse", "Edit", new { file_path = "a.cs" }, "t1", "tester")).Trim());   // other agents unaffected
    }

    [Fact]
    public async Task Pause_control_holds_the_tool_call_until_resumed()
    {
        var control = new Controls(Path.Combine(Data, "control"));
        control.PauseAll();
        var sw = Stopwatch.StartNew();
        var t = Task.Run(() => RunHook(Tool("PreToolUse", "Read", new { file_path = "a" }, "r1", "researcher")));
        Thread.Sleep(2500);
        Assert.False(t.IsCompleted, "the hook should wait while paused");
        control.ResumeAll();
        Assert.Same(t, await Task.WhenAny(t, Task.Delay(10_000)));
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(2));
        Assert.Contains(Spooled(), e => e.Type == "paused" && e.Status == AgentStatus.Paused);
        Assert.Contains(Spooled(), e => e.Type == "resumed");
    }

    [Theory]
    // Exact shapes real agents used in the first live test (cd prefix; absolute script path from a worktree).
    [InlineData("cd \"C:/Users/antti/OneDrive/Ty\uFFFDp\uFFFDyt\uFFFD/MoneyTweaks\" && python .claude/hooks/command_center.py publish --status 'PLANNING' --summary 'Alpha started'")]
    [InlineData("cd \"C:/x/.claude/worktrees/agent-1\" && python \"C:/Users/antti/OneDrive/Ty\uFFFDp\uFFFDyt\uFFFD/MoneyTweaks/.claude/hooks/command_center.py\" publish --status 'PLANNING' --summary 'Alpha started'")]
    [InlineData("python .claude/hooks/command_center.py publish --status 'PLANNING' --summary 'Alpha started'")]
    public void Progress_notes_are_recognised_in_every_form_agents_write_them(string command)
    {
        RunHook(Tool("PreToolUse", "Bash", new { command }, "z1", "researcher"));
        var e = Assert.Single(Spooled());
        Assert.Equal("progress", e.Type);
        Assert.Equal(AgentStatus.Planning, e.Status);
        Assert.Equal("Alpha started", e.Summary);
        Assert.Equal("agent", e.Source);
    }

    [Fact]
    public void Publish_cli_itself_only_prints_published()
    {
        var psi = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(Hook); psi.ArgumentList.Add("publish"); psi.ArgumentList.Add("--summary"); psi.ArgumentList.Add("x");
        psi.Environment["LOCALAPPDATA"] = _tmp;
        using var p = Process.Start(psi)!;
        Assert.Equal("published", p.StandardOutput.ReadToEnd().Trim());
        p.WaitForExit();
        Assert.Empty(Spooled());   // the hook (not the CLI) records the note, with the agent's identity
    }

    [Fact]
    public void Malformed_or_foreign_lines_are_rejected()
    {
        Assert.Null(AgentEvent.Parse("not json"));
        Assert.Null(AgentEvent.Parse("{\"schema\":\"other\",\"id\":\"1\",\"ts\":1,\"type\":\"x\"}"));
        Assert.Null(AgentEvent.Parse(new string('a', AgentEvent.MaxLineChars + 1)));
        var e = AgentEvent.Parse("{\"schema\":\"mt.agent-event/1\",\"id\":\"abc\",\"ts\":5,\"type\":\"progress\",\"agent\":\"../../x\",\"status\":\"hacked\",\"summary\":\"pw: password=hunter2 \\u001b[31mred\",\"sources\":[\"javascript:alert(1)\",\"https://ok.example\"]}");
        Assert.NotNull(e);
        Assert.Equal("....x", e!.Agent);
        Assert.Null(e.Status);
        Assert.False(e.Summary!.Contains("hunter2"), e.Summary);
        Assert.False(e.Summary.Contains('\u001b') || e.Summary.Contains("[31m"), e.Summary);
        Assert.Equal(["https://ok.example"], e.Sources);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }
}
