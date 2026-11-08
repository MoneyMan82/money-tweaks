namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// Watches the Money Tweaks workspace (main checkout and agent worktrees) and reports real file changes as events with
/// source "filesystem". Build output, git internals and dependency folders are ignored. Changes are de-bounced.
/// </summary>
public sealed class FileMonitor : IDisposable
{
    private readonly Workspace _ws;
    private readonly Action<string> _onChange;
    private FileSystemWatcher? _w;
    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _flush;

    public FileMonitor(Workspace ws, Action<string> onChange)
    {
        _ws = ws; _onChange = onChange;
        _flush = new Timer(_ => Flush(), null, 400, 400);
    }

    public void Start()
    {
        _w = new FileSystemWatcher(_ws.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _w.Changed += (_, e) => Queue(e.FullPath);
        _w.Created += (_, e) => Queue(e.FullPath);
        _w.Renamed += (_, e) => Queue(e.FullPath);
        _w.EnableRaisingEvents = true;
    }

    private static readonly string[] Ignored = ["\\.git\\", "\\bin\\", "\\obj\\", "\\node_modules\\", "\\.vs\\", "\\TestResults\\", "\\artifacts\\", "\\dist\\", "\\__pycache__\\", "\\.wrangler\\"];

    public static bool IsIgnored(string relative)
    {
        var r = "\\" + relative.Replace('/', '\\');
        if (Ignored.Any(i => r.Contains(i, StringComparison.OrdinalIgnoreCase))) return true;
        var name = Path.GetFileName(r);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~") || name.EndsWith(".swp")
            || r.EndsWith("\\.claude\\autonomous-run", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".db-wal") || name.EndsWith(".db-shm");
    }

    private void Queue(string full)
    {
        var rel = _ws.Relative(full);
        if (IsIgnored(rel) || Directory.Exists(full)) return;
        lock (_pending) _pending[full] = DateTime.UtcNow;
    }

    private void Flush()
    {
        List<string> ready;
        lock (_pending)
        {
            ready = _pending.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromMilliseconds(350)).Select(p => p.Key).ToList();
            foreach (var r in ready) _pending.Remove(r);
        }
        foreach (var r in ready) { try { _onChange(r); } catch { } }
    }

    public void Dispose() { _w?.Dispose(); _flush.Dispose(); }
}

/// <summary>
/// The owner's controls, as files the publisher hook reads at every PreToolUse (the only supported way for another
/// program to affect a running Claude Code agent): "paused" makes agents wait at their next tool call; "stop-&lt;id&gt;"
/// makes that agent's tool calls be refused so it reports and finishes. Nothing here can run commands.
/// </summary>
public sealed class Controls(string folder)
{
    public string Folder { get; } = folder;
    private string PausedFile => Path.Combine(Folder, "paused");
    private static string Safe(string id) => new(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
    private string StopFile(string id) => Path.Combine(Folder, "stop-" + Safe(id));

    public bool Paused => File.Exists(PausedFile);
    public void PauseAll() { Directory.CreateDirectory(Folder); File.WriteAllText(PausedFile, DateTimeOffset.Now.ToString("O")); }
    public void ResumeAll() { if (File.Exists(PausedFile)) File.Delete(PausedFile); }
    public bool IsStopped(string id) => File.Exists(StopFile(id));
    public void Stop(string id) { Directory.CreateDirectory(Folder); File.WriteAllText(StopFile(id), DateTimeOffset.Now.ToString("O")); }
    public void ClearStop(string id) { if (File.Exists(StopFile(id))) File.Delete(StopFile(id)); }
    public IEnumerable<string> Stopped() => Directory.Exists(Folder)
        ? Directory.GetFiles(Folder, "stop-*").Select(f => Path.GetFileName(f)[5..]) : [];
}
