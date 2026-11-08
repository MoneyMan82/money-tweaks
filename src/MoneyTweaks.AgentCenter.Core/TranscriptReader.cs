using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// Reads the team's conversation from Claude Code's own session transcripts on this PC
/// (%USERPROFILE%\.claude\projects\&lt;project&gt;\&lt;session&gt;.jsonl and ...\&lt;session&gt;\subagents\agent-&lt;id&gt;.jsonl):
/// what each agent wrote ("text"), the coordinator's full briefs (Agent tool calls), messages between agents
/// (SendMessage) and each agent's report (SubagentHandback). "thinking" parts are never read. Only files inside the
/// projects folder are opened; text is sanitized and capped. Each entry becomes an event of type "say", "brief",
/// "message" or "report" with source "transcript" (id derived from the entry, so re-reading never duplicates).
/// </summary>
public sealed class TranscriptReader
{
    private readonly string _projects;
    private readonly Action<AgentEvent> _emit;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _missingUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly long _notBefore;

    public TranscriptReader(Action<AgentEvent> emit, DateTimeOffset notBefore, string? projectsFolder = null)
    {
        _emit = emit;
        _notBefore = notBefore.ToUnixTimeMilliseconds();
        _projects = Path.GetFullPath(projectsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));
    }

    /// <summary>Main-session transcript of <paramref name="session"/> (searched in every project folder), or null.</summary>
    public string? MainTranscript(string session)
    {
        if (!IsSafeId(session) || !Directory.Exists(_projects)) return null;
        foreach (var dir in Directory.GetDirectories(_projects))
        {
            var f = Path.Combine(dir, session + ".jsonl");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public string? AgentTranscript(string session, string agentId)
    {
        if (!IsSafeId(agentId) || MainTranscript(session) is not { } main) return null;
        var f = Path.Combine(Path.ChangeExtension(main, null), "subagents", "agent-" + agentId + ".jsonl");
        return File.Exists(f) ? f : null;
    }

    private static bool IsSafeId(string s) => s.Length is > 0 and < 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Reads anything new from the transcripts of these (session, agent) pairs; agent "main" = the session itself.</summary>
    public int Poll(IEnumerable<(string Session, string Agent)> who)
    {
        var n = 0;
        lock (_lock)
        {
            foreach (var (session, agent) in who.Distinct())
            {
                var key = session + "|" + agent;
                if (_missingUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until) continue;   // look again in 15 s
                var file = agent == "main" ? MainTranscript(session) : AgentTranscript(session, agent);
                if (file is null) { _missingUntil[key] = DateTime.UtcNow.AddSeconds(15); continue; }
                _missingUntil.Remove(key);
                n += ReadNew(file, session, agent);
            }
        }
        return n;
    }

    internal int ReadNew(string file, string session, string agent)
    {
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(_projects + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 0;
        long offset = _offsets.GetValueOrDefault(full);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < offset) offset = 0;
        if (fs.Length == offset) return 0;
        // Very large transcripts: on the first read only the last 8 MB matter (older parts are outside the live window).
        if (offset == 0 && fs.Length > 8 * 1024 * 1024) offset = fs.Length - 8 * 1024 * 1024;
        fs.Position = offset;
        var buf = new byte[fs.Length - offset];
        fs.ReadExactly(buf);
        var lastNl = Array.LastIndexOf(buf, (byte)'\n');
        if (lastNl < 0) return 0;
        var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
        _offsets[full] = offset + lastNl + 1;
        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length < 20 || !line.Contains("\"assistant\"")) continue;   // cheap pre-filter
            foreach (var e in Parse(line, session, agent)) { _emit(e); count++; }
        }
        return count;
    }

    /// <summary>Events from one transcript line (only assistant entries; never "thinking").</summary>
    public IEnumerable<AgentEvent> Parse(string line, string session, string agent)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { yield break; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.GetString() != "assistant") yield break;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
            long ts = root.TryGetProperty("timestamp", out var tsEl) && DateTimeOffset.TryParse(tsEl.GetString(), out var dto) ? dto.ToUnixTimeMilliseconds() : DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (ts < _notBefore) yield break;
            var uuid = root.TryGetProperty("uuid", out var u) ? u.GetString() : null;
            var i = 0;
            foreach (var part in content.EnumerateArray())
            {
                i++;
                var pt = part.TryGetProperty("type", out var ptt) ? ptt.GetString() : null;
                AgentEvent? e = null;
                if (pt == "text" && part.TryGetProperty("text", out var tx) && tx.GetString() is { Length: > 0 } said)
                    e = New("say", Short(said), said);
                else if (pt == "tool_use" && part.TryGetProperty("name", out var nm) && part.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    string? S(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var useId = part.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    switch (nm.GetString())
                    {
                        case "Agent" or "Task":
                            e = New("brief", S("description") ?? "Delegated a task", S("prompt"));
                            e.To = S("subagent_type") ?? "general-purpose";
                            break;
                        case "SendMessage":
                            e = New("message", Short(S("summary") ?? S("message")), S("message"));
                            e.To = S("to");
                            break;
                        case "SubagentHandback":
                            e = New("report", Short(S("message")), S("message"));
                            break;
                    }
                    if (e is not null) e.Tool = new ToolInfo { Name = nm.GetString(), UseId = useId };
                }
                if (e is null) continue;
                e.Id = Id(uuid ?? line, i);
                e.Ts = ts;
                yield return Sanitizer.Clean(e);
            }
        }

        AgentEvent New(string type, string? summary, string? message) => new()
        {
            Session = session, Agent = agent, AgentType = agent == "main" ? "main" : null, Type = type, Source = "transcript",
            Summary = summary, Message = message,
        };
    }

    private static string? Short(string? s)
    {
        if (s is null) return null;
        var one = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 280 ? one : one[..279] + "…";
    }

    private static string Id(string seed, int part) =>
        "tx" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "#" + part)))[..30].ToLowerInvariant();
}
