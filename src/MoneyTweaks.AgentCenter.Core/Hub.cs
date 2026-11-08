using System.Collections.Concurrent;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// Wires the pipeline: pipe + spool + file monitor -> sanitize -> SQLite (de-duplicated) -> queue -> <see cref="TeamState"/>
/// (applied on the caller's thread via <see cref="Drain"/>, the UI timer). Owns nothing visual.
/// </summary>
public sealed class Hub : IDisposable
{
    public string DataFolder { get; }
    public string SpoolFolder => Path.Combine(DataFolder, "spool");
    public Workspace Workspace { get; }
    public EventStore Store { get; }
    public Controls Controls { get; }
    public PipeListener Pipe { get; }
    public SpoolImporter Spool { get; }
    public FileMonitor? Files { get; private set; }
    public TeamState Live { get; } = new();
    public List<RegisteredAgent> Registry { get; private set; } = [];
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? LastEventAt { get; private set; }
    public long Received;
    private readonly ConcurrentQueue<AgentEvent> _queue = new();
    private readonly Timer _spoolTimer;
    private readonly TimeSpan _liveWindow;

    public Hub(Workspace ws, string dataFolder, string pipeName = PipeListener.DefaultName, TimeSpan? liveWindow = null)
    {
        Workspace = ws;
        DataFolder = dataFolder;
        _liveWindow = liveWindow ?? TimeSpan.FromHours(12);
        Directory.CreateDirectory(DataFolder);
        Store = new EventStore(Path.Combine(DataFolder, "history.db"));
        Controls = new Controls(Path.Combine(DataFolder, "control"));
        Live.Workspace = ws;
        Pipe = new PipeListener(Ingest, pipeName);
        Spool = new SpoolImporter(SpoolFolder, Store, Ingest);
        _spoolTimer = new Timer(_ => { try { Spool.ImportNew(); } catch { } }, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Loads recent history into the live view, imports anything spooled while closed, then starts listening.</summary>
    public void Start(bool watchFiles = true, bool listen = true)
    {
        Registry = AgentRegistry.Load(Workspace);
        foreach (var e in Store.Since(DateTimeOffset.Now.Add(-_liveWindow).ToUnixTimeMilliseconds())) Live.Apply(e);
        if (!listen) return;   // read-only viewer (screenshot mode): another instance owns the pipe and the spool
        Spool.ImportNew();
        Pipe.Start();
        _spoolTimer.Change(2000, 2000);   // catch-up for anything the pipe missed
        if (watchFiles) { Files = new FileMonitor(Workspace, OnFilesChanged); Files.Start(); }
    }

    public void ReloadRegistry() => Registry = AgentRegistry.Load(Workspace);

    /// <summary>Thread-safe entry for every event source.</summary>
    public void Ingest(AgentEvent e)
    {
        if (!Store.Add(e)) return;   // already have it (pipe and spool both deliver)
        Interlocked.Increment(ref Received);
        LastEventAt = DateTimeOffset.Now;
        _queue.Enqueue(e);
    }

    /// <summary>
    /// Only files git reports as modified or new become events: creating a worktree writes thousands of files that are
    /// identical to HEAD, and those are not changes. One `git status` per checkout per batch.
    /// </summary>
    private void OnFilesChanged(IReadOnlyList<string> files)
    {
        foreach (var group in files.Where(File.Exists).GroupBy(Workspace.CheckoutOf, StringComparer.OrdinalIgnoreCase))
        {
            var changed = Workspace.ChangedFiles(group.Key);
            if (changed is null) continue;
            foreach (var full in group.Where(changed.Contains)) OnFileChanged(full);
        }
    }

    private void OnFileChanged(string full)
    {
        var checkout = Workspace.CheckoutOf(full);
        var stat = Workspace.NumStat(full);
        Ingest(new AgentEvent
        {
            Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(), Session = "filesystem", Agent = "filesystem", AgentType = "filesystem",
            Type = "file_change", Source = "filesystem", Files = [full], Branch = Workspace.Branch(checkout),
            Lines = stat is { } s ? new LineCounts { Added = s.Added, Removed = s.Removed } : null,
            Summary = "Changed on disk: " + Workspace.Relative(full),
        });
    }

    /// <summary>Applies queued events to <see cref="Live"/> (call from one thread, e.g. the UI timer). Returns them.</summary>
    public List<AgentEvent> Drain(int max = 2000)
    {
        var list = new List<AgentEvent>();
        while (list.Count < max && _queue.TryDequeue(out var e)) { Live.Apply(e); list.Add(e); }
        return list;
    }

    /// <summary>"Live", "Idle" or "Offline": is the pipeline receiving from Claude Code?</summary>
    public string Connection(DateTimeOffset now) =>
        !Pipe.Listening ? "Offline" : LastEventAt is { } t && now - t < TimeSpan.FromMinutes(2) ? "Live" : "Idle";

    public static TeamState Replay(IEnumerable<AgentEvent> events, Workspace ws, DateTimeOffset? until = null)
    {
        var s = new TeamState { Workspace = ws };
        foreach (var e in events) { if (until is { } u && e.Time > u) break; s.Apply(e); }
        return s;
    }

    public void Dispose()
    {
        _spoolTimer.Dispose();
        Files?.Dispose();
        Pipe.Dispose();
        Store.Dispose();
    }
}
