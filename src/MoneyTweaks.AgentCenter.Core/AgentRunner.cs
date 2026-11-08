using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoneyTweaks.AgentCenter.Core;

public enum RunKind { Cycle, Brainstorm }
public enum ScheduleMode { Off, Interval, Daily }

/// <summary>When the Command Center starts cycles on its own (only while it is open).</summary>
public sealed class RunSchedule
{
    public ScheduleMode Mode { get; set; } = ScheduleMode.Off;
    public int IntervalHours { get; set; } = 4;
    /// <summary>Local time of day for <see cref="ScheduleMode.Daily"/>, "HH:mm".</summary>
    public string DailyAt { get; set; } = "09:00";
    public bool SkipWhileGaming { get; set; } = true;
    public DateTimeOffset? LastAutoRun { get; set; }

    public static readonly int[] Intervals = [1, 2, 3, 4, 6, 8, 12, 24];

    /// <summary>The next time a scheduled cycle is due, or null when scheduling is off.</summary>
    public DateTimeOffset? NextDue(DateTimeOffset now)
    {
        switch (Mode)
        {
            case ScheduleMode.Interval:
                var hours = Math.Clamp(IntervalHours, 1, 24);
                return LastAutoRun is { } last ? last.AddHours(hours) : now;   // first one right away
            case ScheduleMode.Daily:
                if (!TimeSpan.TryParse(DailyAt, out var at) || at < TimeSpan.Zero || at >= TimeSpan.FromDays(1)) at = TimeSpan.FromHours(9);
                var local = now.ToLocalTime();
                var todayAt = new DateTimeOffset(local.Date + at, local.Offset);
                // Already ran at/after today's time -> tomorrow; otherwise today's time (due now if it has passed).
                return LastAutoRun is { } l && l >= todayAt ? todayAt.AddDays(1) : todayAt;
            default:
                return null;
        }
    }

    public bool IsDue(DateTimeOffset now) => NextDue(now) is { } due && now >= due;
}

public sealed record RunRecord(string Id, RunKind Kind, bool Scheduled, DateTimeOffset Started, DateTimeOffset? Ended, int? ExitCode, string LogPath, string? Result);

/// <summary>
/// Starts real Claude Code sessions for the agent team through the official Claude Code command line in
/// non-interactive mode (`claude -p`), in the Money Tweaks folder, with only pre-approved tools
/// (`--permission-mode dontAsk --allowedTools ...`). The project's hooks still apply: the guard (activated by the
/// run marker this class writes before starting) and the Command Center publisher, so the run shows up live.
/// </summary>
public sealed class AgentRunner : IDisposable
{
    public static readonly string[] AllowedTools = ["Bash", "Read", "Edit", "Write", "Glob", "Grep", "Agent", "WebFetch", "WebSearch", "TodoWrite", "SendMessage"];

    private readonly Workspace _ws;
    private readonly string _dataFolder;
    private Process? _proc;
    private RunRecord? _current;
    private readonly object _lock = new();
    public event Action? Changed;

    public AgentRunner(Workspace ws, string dataFolder) { _ws = ws; _dataFolder = dataFolder; }

    public string? ClaudePath { get; private set; }
    public bool? SignedIn { get; private set; }
    public string? AccountInfo { get; private set; }
    public RunRecord? Current { get { lock (_lock) return _current; } }
    public bool Running => Current is { Ended: null };
    public string RunsFile => Path.Combine(_dataFolder, "runs.json");
    private string MarkerPath => Path.Combine(_ws.Root, ".claude", "autonomous-run");

    /// <summary>The Claude Code CLI: override, then the copy bundled with the Claude desktop app (newest), then ~/.local/bin, then PATH.</summary>
    public static string? FindClaude(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var data = Path.Combine(local, "Claude-Data");
            if (Directory.Exists(data))
            {
                var found = Directory.GetDirectories(data)
                    .Select(d => Path.Combine(d, "claude-code")).Where(Directory.Exists)
                    .SelectMany(cc => Directory.GetDirectories(cc))
                    .SelectMany(ver => Directory.GetDirectories(ver).Select(h => (ver: Path.GetFileName(ver), exe: Path.Combine(h, "claude.exe"))))
                    .Where(x => File.Exists(x.exe))
                    .OrderByDescending(x => Version.TryParse(x.ver, out var v) ? v : new Version(0, 0))
                    .ThenByDescending(x => File.GetLastWriteTimeUtc(x.exe))
                    .Select(x => x.exe).FirstOrDefault();
                if (found is not null) return found;
            }
        }
        catch { }
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(home)) return home;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private ProcessStartInfo Psi(string exe)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = _ws.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // A run is its own session: don't pass on another Claude session's environment (the Command Center may have been
        // started from one), so it uses the sign-in of the Claude Code CLI.
        foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(k);
        return psi;
    }

    /// <summary>Finds the CLI and asks it whether it is signed in (`claude auth status`, no usage spent).</summary>
    public async Task RefreshAsync(string? overridePath = null)
    {
        ClaudePath = FindClaude(overridePath);
        if (ClaudePath is null) { SignedIn = null; Changed?.Invoke(); return; }
        try
        {
            var psi = Psi(ClaudePath);
            psi.ArgumentList.Add("auth"); psi.ArgumentList.Add("status");
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            using var doc = JsonDocument.Parse(output);
            SignedIn = doc.RootElement.TryGetProperty("loggedIn", out var li) && li.GetBoolean();
            AccountInfo = doc.RootElement.TryGetProperty("authMethod", out var am) ? am.GetString() : null;
        }
        catch { SignedIn = null; }
        Changed?.Invoke();
    }

    /// <summary>Opens a console window running the official `claude auth login` so the owner signs in (in the browser).</summary>
    public void OpenSignIn()
    {
        if (ClaudePath is null) throw new InvalidOperationException("Claude Code wasn't found on this PC.");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = _ws.Root };
        psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add(ClaudePath);
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("login");
        Process.Start(psi);
    }

    public static string Prompt(RunKind kind) => kind switch
    {
        RunKind.Brainstorm => """
            Act as the Chief Coordinator of the Money Tweaks agent team (read .claude/agents/chief-coordinator.md, team/RULES.md, team/AGENTS.md and team/SCORING.md). This run was started by the owner from the Agent Command Center with "Brainstorm now".
            Run a brainstorming competition now and do not build anything:
            1. Write the current time into .claude/autonomous-run.
            2. In parallel, ask innovation-scout, competitor-analyst and one domain specialist (windows-tweak-specialist, gaming-performance-analyst or hardware-specialist - pick the area with the fewest ideas in team/IDEAS.md) for 5 original, evidence-backed ideas each. Tell them the ideas already in team/IDEAS.md so they don't repeat them.
            3. Send every new idea to idea-critic to score with team/SCORING.md and rank them.
            4. Add every idea with its score, verdict and the critic's reason to team/IDEAS.md (next free I- ids), append a dated entry to team/LOG.md, and mark any brainstorm request under "Owner notes" in team/INBOX.md as done.
            5. Delete .claude/autonomous-run and finish with the ranked top 5.
            Publish progress notes as team/RULES.md describes. Never push, merge, tag, release or deploy.
            """,
        _ => """
            Run one autonomous development cycle for Money Tweaks. This run was started from the Agent Command Center.
            Read .claude/skills/cycle/SKILL.md and follow it exactly: act as the Chief Coordinator (.claude/agents/chief-coordinator.md), follow team/CYCLE.md step by step, and delegate each step to the agents in team/AGENTS.md.
            Publish progress notes as team/RULES.md describes. Never push any branch, merge into main, tag, release, deploy, delete files or rewrite history. Finished work goes into team/INBOX.md with a "/merge <branch>" line.
            """,
    };

    /// <summary>Starts a run. Throws when one is already running, Claude Code isn't found or isn't signed in.</summary>
    public RunRecord Start(RunKind kind, bool scheduled)
    {
        lock (_lock)
        {
            if (_current is { Ended: null }) throw new InvalidOperationException("A run is already in progress.");
            if (ClaudePath is null) throw new InvalidOperationException("Claude Code wasn't found on this PC.");
            if (SignedIn != true) throw new InvalidOperationException("Claude Code isn't signed in. Use \"Sign in to Claude Code\" first.");
            Directory.CreateDirectory(Path.Combine(_dataFolder, "runs"));
            var id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + kind.ToString().ToLowerInvariant();
            var log = Path.Combine(_dataFolder, "runs", id + ".jsonl");
            // Guard on from the first tool call (the cycle rewrites the marker in its Step 0 anyway).
            File.WriteAllText(MarkerPath, DateTimeOffset.Now.ToString("O"));
            var psi = Psi(ClaudePath);
            foreach (var a in new[] { "-p", Prompt(kind), "--permission-mode", "dontAsk", "--output-format", "stream-json", "--verbose", "--allowedTools" }) psi.ArgumentList.Add(a);
            foreach (var t in AllowedTools) psi.ArgumentList.Add(t);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var writer = new StreamWriter(log, false, new UTF8Encoding(false)) { AutoFlush = true };
            string? result = null;
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (writer) writer.WriteLine(Sanitizer.Text(e.Data, 200_000));
                if (e.Data.Contains("\"type\":\"result\"")) result = ResultText(e.Data);
            };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(JsonSerializer.Serialize(new { type = "stderr", text = Sanitizer.Text(e.Data, 4000) })); };
            p.Exited += (_, _) =>
            {
                try { p.WaitForExit(); } catch { }
                lock (writer) writer.Dispose();
                lock (_lock)
                {
                    _current = _current! with { Ended = DateTimeOffset.Now, ExitCode = SafeExit(p), Result = result ?? LastText(log) };
                    Save(_current);
                }
                // A run that ended without its own clean-up must not leave the guard's run marker behind.
                try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
                Changed?.Invoke();
            };
            if (!p.Start()) throw new InvalidOperationException("Claude Code didn't start.");
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _proc = p;
            _current = new RunRecord(id, kind, scheduled, DateTimeOffset.Now, null, null, log, null);
            Save(_current);
            Changed?.Invoke();
            return _current;
        }
    }

    private static int? SafeExit(Process p) { try { return p.ExitCode; } catch { return null; } }

    /// <summary>Stops the run this app started (its whole process tree).</summary>
    public void Stop()
    {
        Process? p;
        lock (_lock) p = _proc;
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
    }

    private static string? ResultText(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            return d.RootElement.TryGetProperty("result", out var r) ? Sanitizer.Text(r.GetString(), 4000) : null;
        }
        catch { return null; }
    }

    /// <summary>Readable lines from a run's stream-json log: the coordinator's messages, tool calls and the result.</summary>
    public static List<string> ReadableLog(string path, int max = 400)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            while (r.ReadLine() is { } l)
            {
                try
                {
                    using var d = JsonDocument.Parse(l);
                    var root = d.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "assistant" && root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                        foreach (var part in c.EnumerateArray())
                        {
                            var pt = part.TryGetProperty("type", out var ptt) ? ptt.GetString() : null;
                            if (pt == "text" && part.TryGetProperty("text", out var tx)) lines.Add(Sanitizer.Text(tx.GetString(), 1200)!);
                            else if (pt == "tool_use" && part.TryGetProperty("name", out var nm)) lines.Add("→ " + nm.GetString() + Describe(part));
                        }
                    else if (type == "result") lines.Add("RESULT: " + (root.TryGetProperty("result", out var res) ? Sanitizer.Text(res.GetString(), 3000) : root.GetRawText()));
                    else if (type == "stderr" && root.TryGetProperty("text", out var se)) lines.Add("! " + se.GetString());
                    else if (type == "system" && root.TryGetProperty("subtype", out var st) && st.GetString() == "init") lines.Add("Session started" + (root.TryGetProperty("model", out var mo) ? " · " + mo.GetString() : ""));
                }
                catch (JsonException) { if (l.Length > 0) lines.Add(Sanitizer.Text(l, 500)!); }
            }
        }
        catch { }
        return lines.TakeLast(max).ToList();
    }

    private static string Describe(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var i) || i.ValueKind != JsonValueKind.Object) return "";
        foreach (var k in new[] { "description", "subagent_type", "file_path", "command", "url", "query", "pattern" })
            if (i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) return ": " + Sanitizer.Text(v.GetString(), 160);
        return "";
    }

    private static string? LastText(string path) => ReadableLog(path, 3).LastOrDefault();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<RunRecord> History()
    {
        try { return File.Exists(RunsFile) ? JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(RunsFile), Json) ?? [] : []; }
        catch { return []; }
    }

    private void Save(RunRecord r)
    {
        var all = History().Where(x => x.Id != r.Id).Append(r).OrderByDescending(x => x.Started).Take(200).ToList();
        Directory.CreateDirectory(_dataFolder);
        var tmp = RunsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
        File.Move(tmp, RunsFile, true);
    }

    /// <summary>Same game list as team/RULES.md: no runs while one is running.</summary>
    public static bool GameRunning() =>
        Process.GetProcesses().Any(p => { try { return System.Text.RegularExpressions.Regex.IsMatch(p.ProcessName, "Fortnite|GeometryDash|Roblox|javaw|PenguinHotel", System.Text.RegularExpressions.RegexOptions.IgnoreCase); } catch { return false; } });

    public void Dispose() { }
}
