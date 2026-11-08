using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>
/// Receives events: the named pipe gives instant delivery from the publisher hook; the spool files make delivery durable
/// (every event is also appended there, so anything sent while the app was closed or the pipe was busy is imported).
/// Both feed <see cref="Hub.Ingest"/>, which de-duplicates by event id.
/// </summary>
public sealed class PipeListener : IDisposable
{
    public const string DefaultName = "MoneyTweaksAgentCenter";
    private readonly string _name;
    private readonly Action<AgentEvent> _onEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    /// <summary>Last time a client connected (the publisher connects once per hook call).</summary>
    public DateTimeOffset? LastConnection { get; private set; }
    public bool Listening { get; private set; }
    public string? LastError { get; private set; }

    public PipeListener(Action<AgentEvent> onEvent, string name = DefaultName) { _onEvent = onEvent; _name = name; }

    public void Start(int instances = 4)
    {
        for (int i = 0; i < instances; i++) _loops.Add(Task.Run(() => Loop(_cts.Token)));
    }

    private NamedPipeServerStream Create()
    {
        // Local only (pipes can't be reached over the network with this ACL: no Network SID access) and only this user.
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(_name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = Create();
                Listening = true; LastError = null;
                await pipe.WaitForConnectionAsync(ct);
                LastConnection = DateTimeOffset.Now;
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
                var total = 0;
                while (await ReadLineCapped(reader, ct) is { } line)
                {
                    total += line.Length;
                    if (total > 4 * 1024 * 1024) break;   // one hook call never sends this much
                    if (AgentEvent.Parse(line) is { } e) _onEvent(e);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }   // reconnect after a short pause
            }
            finally { pipe?.Dispose(); }
        }
        Listening = false;
    }

    private static async Task<string?> ReadLineCapped(StreamReader r, CancellationToken ct)
    {
        var line = await r.ReadLineAsync(ct);
        return line is { Length: > AgentEvent.MaxLineChars } ? "" : line;
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Unblock WaitForConnection by connecting once per loop.
        foreach (var _ in _loops) { try { using var c = new NamedPipeClientStream(".", _name, PipeDirection.Out); c.Connect(50); } catch { } }
        try { Task.WaitAll([.. _loops], 1000); } catch { }
    }
}

/// <summary>Imports new lines from the publisher's spool files (events-YYYYMMDD.jsonl), remembering how far it read.</summary>
public sealed class SpoolImporter
{
    private readonly string _folder;
    private readonly EventStore _store;
    private readonly Action<AgentEvent> _onEvent;
    private readonly object _lock = new();

    public SpoolImporter(string folder, EventStore store, Action<AgentEvent> onEvent) { _folder = folder; _store = store; _onEvent = onEvent; }

    /// <summary>Reads everything new; returns how many lines were read.</summary>
    public int ImportNew()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_folder)) return 0;
            var read = 0;
            foreach (var file in Directory.GetFiles(_folder, "events-*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var offset = _store.SpoolOffset(name);
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < offset) offset = 0;            // file replaced
                if (fs.Length == offset) continue;
                fs.Position = offset;
                var buf = new byte[fs.Length - offset];
                fs.ReadExactly(buf);
                // Only complete lines; a line being written right now is picked up next time.
                var lastNl = Array.LastIndexOf(buf, (byte)'\n');
                if (lastNl < 0) continue;
                foreach (var line in Encoding.UTF8.GetString(buf, 0, lastNl + 1).Split('\n'))
                {
                    if (line.Length == 0) continue;
                    read++;
                    if (AgentEvent.Parse(line.TrimEnd('\r')) is { } e) _onEvent(e);
                }
                _store.SetSpoolOffset(name, offset + lastNl + 1);
            }
            return read;
        }
    }
}
