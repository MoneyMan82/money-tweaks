using Microsoft.Data.Sqlite;

namespace MoneyTweaks.AgentCenter.Core;

public sealed record SessionInfo(string Id, DateTimeOffset Start, DateTimeOffset End, int Events, int Agents, int Files, int TestsPassed, int TestsFailed, string? FirstTask);

/// <summary>Every event, stored locally in SQLite (%LOCALAPPDATA%\MoneyTweaksAgentCenter\history.db). Thread-safe.</summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public EventStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS events(
              id TEXT PRIMARY KEY, session TEXT NOT NULL, ts INTEGER NOT NULL, agent TEXT NOT NULL, agent_type TEXT,
              type TEXT NOT NULL, status TEXT, summary TEXT, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events_session_ts ON events(session, ts);
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);
            CREATE TABLE IF NOT EXISTS spool_state(file TEXT PRIMARY KEY, offset INTEGER NOT NULL);
            """);
    }

    private void Exec(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    /// <summary>Stores an event; false if an event with the same id is already stored (pipe + spool both deliver).</summary>
    public bool Add(AgentEvent e)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO events(id,session,ts,agent,agent_type,type,status,summary,json) VALUES($id,$s,$ts,$a,$at,$ty,$st,$su,$j)";
            c.Parameters.AddWithValue("$id", e.Id);
            c.Parameters.AddWithValue("$s", e.Session);
            c.Parameters.AddWithValue("$ts", e.Ts);
            c.Parameters.AddWithValue("$a", e.Agent);
            c.Parameters.AddWithValue("$at", (object?)e.AgentType ?? DBNull.Value);
            c.Parameters.AddWithValue("$ty", e.Type);
            c.Parameters.AddWithValue("$st", (object?)e.Status ?? DBNull.Value);
            c.Parameters.AddWithValue("$su", (object?)e.Summary ?? DBNull.Value);
            c.Parameters.AddWithValue("$j", e.ToJson());
            return c.ExecuteNonQuery() == 1;
        }
    }

    private List<AgentEvent> Read(string where, Action<SqliteCommand>? bind, string order = "ts, rowid", int limit = 200_000)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = $"SELECT json FROM events {where} ORDER BY {order} LIMIT {limit}";
            bind?.Invoke(c);
            using var r = c.ExecuteReader();
            var list = new List<AgentEvent>();
            while (r.Read()) if (AgentEvent.Parse(r.GetString(0)) is { } e) list.Add(e);
            return list;
        }
    }

    public List<AgentEvent> Session(string session) => Read("WHERE session=$s", c => c.Parameters.AddWithValue("$s", session));

    public List<AgentEvent> Since(long ts) => Read("WHERE ts>=$t", c => c.Parameters.AddWithValue("$t", ts));

    /// <summary>Full-text-ish search over summaries and the stored event text (newest first).</summary>
    public List<AgentEvent> Search(string text, int limit = 500)
    {
        var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return Read("WHERE summary LIKE $q ESCAPE '\\' OR json LIKE $q ESCAPE '\\'", c => c.Parameters.AddWithValue("$q", like), "ts DESC", limit);
    }

    public List<SessionInfo> Sessions()
    {
        var sessions = new List<(string Id, long Min, long Max, int N, int Agents)>();
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT session, MIN(ts), MAX(ts), COUNT(*), COUNT(DISTINCT agent) FROM events GROUP BY session ORDER BY MAX(ts) DESC";
            using var r = c.ExecuteReader();
            while (r.Read()) sessions.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4)));
        }
        return sessions.Select(s =>
        {
            var evs = Session(s.Id);
            var files = evs.Where(e => e.Type == "file_change").SelectMany(e => e.Files ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var tests = evs.Where(e => e.Tests is not null).SelectMany(e => e.Tests!).ToList();
            var task = evs.FirstOrDefault(e => e.Type == "task" && e.Task is not null)?.Task;
            return new SessionInfo(s.Id, DateTimeOffset.FromUnixTimeMilliseconds(s.Min), DateTimeOffset.FromUnixTimeMilliseconds(s.Max), s.N, s.Agents,
                files, tests.Sum(t => t.Passed), tests.Sum(t => t.Failed), task);
        }).ToList();
    }

    public int Count()
    {
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM events"; return Convert.ToInt32(c.ExecuteScalar()); }
    }

    public long SpoolOffset(string file)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT offset FROM spool_state WHERE file=$f";
            c.Parameters.AddWithValue("$f", file);
            return c.ExecuteScalar() is long l ? l : 0;
        }
    }

    public void SetSpoolOffset(string file, long offset)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT INTO spool_state(file,offset) VALUES($f,$o) ON CONFLICT(file) DO UPDATE SET offset=$o";
            c.Parameters.AddWithValue("$f", file);
            c.Parameters.AddWithValue("$o", offset);
            c.ExecuteNonQuery();
        }
    }

    public void Dispose() { lock (_lock) _db.Dispose(); }
}
