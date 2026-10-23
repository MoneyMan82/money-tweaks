using System.Runtime.InteropServices;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

/// <summary>Where a process's frames were seen. Other = presented through the kernel directly (OpenGL, Vulkan and others).</summary>
public enum PresentApi { None, Dxgi, D3D9, Other }

/// <summary>Frame statistics for one process over a time window. Frame time = time between Present calls (PresentMon's MsBetweenPresents).</summary>
public sealed record PresentStats(int Pid, PresentApi Api, double Fps, double AvgMs, double LastMs, double Low1PercentFps, int Frames, IReadOnlyList<double> RecentMs);

/// <summary>
/// Present timestamps per process, fed by <see cref="PresentMonitor"/>. Thread-safe; computes FPS, average/last frame time
/// and 1% low from the gaps between consecutive Present calls. Timestamps are in 100 ns units.
/// </summary>
public sealed class FrameTracker
{
    private const int Capacity = 4096;
    private sealed class Ring
    {
        public readonly long[] T = new long[Capacity];
        public int Count, Head;
        public int Dxgi, D3D9, Other;
        public long Last;
    }
    private readonly Dictionary<int, Ring> _byPid = [];
    private readonly object _gate = new();

    public void Add(int pid, long time100ns, PresentApi api)
    {
        lock (_gate)
        {
            if (!_byPid.TryGetValue(pid, out var r)) _byPid[pid] = r = new Ring();
            // One source per process, the most direct one: DXGI, then D3D9, then the kernel's present events (which DXGI and
            // D3D9 presents also go through). If a runtime event shows up after kernel-only frames, start over with it.
            if ((api is PresentApi.Dxgi or PresentApi.D3D9) && r.Dxgi == 0 && r.D3D9 == 0 && r.Other > 0) { r.Count = 0; r.Head = 0; }
            switch (api) { case PresentApi.Dxgi: r.Dxgi++; break; case PresentApi.D3D9: r.D3D9++; break; default: r.Other++; break; }
            if (api == PresentApi.D3D9 && r.Dxgi > 0) return;
            if (api == PresentApi.Other && (r.Dxgi > 0 || r.D3D9 > 0)) return;
            r.T[r.Head] = time100ns;
            r.Head = (r.Head + 1) % Capacity;
            r.Count = Math.Min(r.Count + 1, Capacity);
            r.Last = Math.Max(r.Last, time100ns);
        }
    }

    /// <summary>Processes that presented within <paramref name="window"/> of the newest event, busiest first.</summary>
    public List<(int pid, int frames)> Active(TimeSpan window)
    {
        lock (_gate)
        {
            long newest = _byPid.Values.Select(r => r.Last).DefaultIfEmpty(0).Max();
            long from = newest - window.Ticks;
            return _byPid.Select(kv => (kv.Key, Times(kv.Value).Count(t => t >= from))).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).ToList();
        }
    }

    private static IEnumerable<long> Times(Ring r)
    {
        for (int i = 0; i < r.Count; i++) yield return r.T[(r.Head - r.Count + i + Capacity) % Capacity];
    }

    /// <summary>Longest gap between the newest delivered frame and "now" before a process counts as no longer drawing.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Stats over the <paramref name="window"/> ending at the process's newest frame. ETW real-time events arrive in
    /// buffers (up to about a second late), so the window is anchored to the newest delivered frame, not the wall clock.
    /// With <paramref name="now100ns"/>, a process whose newest frame is older than <see cref="Stale"/> returns null.
    /// </summary>
    public PresentStats? Stats(int pid, TimeSpan window, int graphPoints = 120, long? now100ns = null)
    {
        long[] t;
        PresentApi api;
        lock (_gate)
        {
            if (!_byPid.TryGetValue(pid, out var r) || r.Count < 2) return null;
            if (now100ns is { } n && n - r.Last > Stale.Ticks) return null;
            long end = r.Last;
            t = Times(r).Where(x => x <= end && x >= end - window.Ticks).ToArray();
            api = r.Dxgi > 0 ? PresentApi.Dxgi : r.D3D9 > 0 ? PresentApi.D3D9 : PresentApi.Other;
        }
        return Compute(pid, api, t, graphPoints);
    }

    public static PresentStats? Compute(int pid, PresentApi api, IReadOnlyList<long> t, int graphPoints = 120)
    {
        if (t.Count < 2) return null;
        var gaps = new double[t.Count - 1];
        for (int i = 1; i < t.Count; i++) gaps[i - 1] = (t[i] - t[i - 1]) / 10_000.0;
        double spanMs = (t[^1] - t[0]) / 10_000.0;
        if (spanMs <= 0) return null;
        double fps = gaps.Length * 1000.0 / spanMs;
        // 1% low: average FPS of the slowest 1% of frames (at least one frame).
        var slow = gaps.OrderByDescending(g => g).Take(Math.Max(1, gaps.Length / 100)).Average();
        return new PresentStats(pid, api, fps, gaps.Average(), gaps[^1], slow > 0 ? 1000.0 / slow : 0, gaps.Length, gaps.TakeLast(graphPoints).ToArray());
    }

    /// <summary>Present timestamps of one process newer than <paramref name="after100ns"/>, oldest first. Polled about once a
    /// second, this collects every frame of a longer capture (the ring holds the last 4096).</summary>
    public List<long> TimesAfter(int pid, long after100ns)
    {
        lock (_gate)
            return _byPid.TryGetValue(pid, out var r) ? Times(r).Where(x => x > after100ns).ToList() : [];
    }

    /// <summary>Forgets processes with no Present for 10 s (closed games, finished launchers).</summary>
    public void Prune(long now100ns)
    {
        lock (_gate)
            foreach (var pid in _byPid.Where(kv => now100ns - kv.Value.Last > TimeSpan.FromSeconds(10).Ticks).Select(kv => kv.Key).ToList())
                _byPid.Remove(pid);
    }

    public void Clear() { lock (_gate) _byPid.Clear(); }
}

public enum CaptureState { Stopped, Running, NeedsAdmin, Failed }

/// <summary>
/// Real-time ETW session on the same Present events PresentMon uses: Microsoft-Windows-DXGI Present_Start (DirectX 10/11/12,
/// any window mode), Microsoft-Windows-D3D9 Present_Start (DirectX 9), and Microsoft-Windows-DxgKrnl Present_Info, which
/// the kernel logs for presents that bypass those runtimes (OpenGL, Vulkan; PresentMon's "Other" runtime). Only that one
/// kernel event is enabled, through an event-ID filter, so the session stays light. Nothing is injected into games: ETW
/// events are written by Windows' own graphics stack. Windows only lets administrators (or members of "Performance Log
/// Users") start ETW sessions, so without that the state is NeedsAdmin.
/// </summary>
public static unsafe class PresentMonitor
{
    public const string SessionName = "MoneyTweaks-Present";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const ushort DxgKrnlPresentInfo = 184;
    private const ulong DxgKrnlKeywords = 0x8000000000000001; // Base | Performance
    private static readonly Guid SessionGuid = new("6B8F3C2E-5D41-4E0B-9A77-4D2F1C0B7E11");
    private const ushort DxgiPresentStart = 42, D3D9PresentStart = 1;

    private const uint ErrorSuccess = 0, ErrorAccessDenied = 5, ErrorAlreadyExists = 183;
    private const uint EventTraceControlStop = 1, EnableProvider = 1;
    private const int PropsSize = 120, NameBytes = 1024 * 2;
    private const ulong InvalidTrace = ulong.MaxValue;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] private static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll")] private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);

    public static FrameTracker Frames { get; } = new();
    public static CaptureState State { get; private set; } = CaptureState.Stopped;
    public static string Message { get; private set; } = "Not started.";
    public static long EventsSeen => Interlocked.Read(ref _events);

    private static readonly object Gate = new();
    private static ulong _session, _trace = InvalidTrace;
    private static Thread? _thread;
    private static IntPtr _logfile, _loggerName;
    private static long _events;

    private static IntPtr NewProps()
    {
        var p = Marshal.AllocHGlobal(PropsSize + NameBytes);
        NativeMemory.Clear((void*)p, PropsSize + NameBytes);
        var b = (byte*)p;
        *(uint*)(b + 0) = PropsSize + NameBytes;   // Wnode.BufferSize
        *(Guid*)(b + 24) = SessionGuid;            // Wnode.Guid
        *(uint*)(b + 40) = 1;                      // Wnode.ClientContext: QPC timestamps
        *(uint*)(b + 44) = 0x00020000;             // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(b + 48) = 8;                      // BufferSize (KB): small buffers flush sooner, so frames arrive with less delay
        *(uint*)(b + 64) = 0x00000100;             // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(b + 68) = 1;                      // FlushTimer: 1 s
        *(uint*)(b + 116) = PropsSize;             // LoggerNameOffset
        return p;
    }

    /// <summary>Starts the session (no-op if running). Returns the resulting state; never throws.</summary>
    public static CaptureState Start()
    {
        lock (Gate)
        {
            if (State == CaptureState.Running) return State;
            var props = NewProps();
            try
            {
                var st = StartTraceW(out _session, SessionName, props);
                if (st == ErrorAlreadyExists)
                {
                    // Left over from a crash: stop the stale session with our name, then start again.
                    ControlTraceW(0, SessionName, props, EventTraceControlStop);
                    Marshal.FreeHGlobal(props);
                    props = NewProps();
                    st = StartTraceW(out _session, SessionName, props);
                }
                if (st == ErrorAccessDenied) return Set(CaptureState.NeedsAdmin, "Windows only allows administrators (or members of the Performance Log Users group) to read frame events. Restart Money Tweaks as administrator for FPS and frame time.");
                if (st != ErrorSuccess) return Set(CaptureState.Failed, $"Couldn't start the frame-timing session (Windows error {st}).");

                foreach (var g in new[] { DxgiProvider, D3D9Provider })
                {
                    var e = EnableTraceEx2(_session, in g, EnableProvider, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
                    if (e != ErrorSuccess) Log.Warn($"ETW provider {g} not enabled: {e}");
                }
                KernelPresents = EnableKernelPresents();

                _loggerName = Marshal.StringToHGlobalUni(SessionName);
                _logfile = Marshal.AllocHGlobal(448); // EVENT_TRACE_LOGFILEW (x64)
                NativeMemory.Clear((void*)_logfile, 448);
                var lf = (byte*)_logfile;
                *(IntPtr*)(lf + 8) = _loggerName;                                     // LoggerName
                *(uint*)(lf + 28) = 0x00000100 | 0x10000000;                          // ProcessTraceMode: REAL_TIME | EVENT_RECORD
                *(IntPtr*)(lf + 424) = (IntPtr)(delegate* unmanaged<byte*, void>)&OnEvent; // EventRecordCallback
                _trace = OpenTraceW(_logfile);
                if (_trace == InvalidTrace)
                {
                    var err = Marshal.GetLastPInvokeError();
                    StopLocked();
                    return Set(CaptureState.Failed, $"Couldn't open the frame-timing session (Windows error {err}).");
                }
                Interlocked.Exchange(ref _events, 0);
                Frames.Clear();
                var handle = _trace;
                _thread = new Thread(() => { var h = handle; ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true, Name = "PresentMonitor ETW" };
                _thread.Start();
                Log.Info("Frame-timing ETW session started (DXGI + D3D9 Present events)");
                return Set(CaptureState.Running, KernelPresents ? "Measuring Present calls (DirectX 9/10/11/12, plus kernel presents for OpenGL/Vulkan)." : "Measuring Present calls (DirectX 9/10/11/12).");
            }
            catch (Exception ex)
            {
                StopLocked();
                return Set(CaptureState.Failed, "Frame timing isn't available: " + ex.Message);
            }
            finally { Marshal.FreeHGlobal(props); }
        }
    }

    /// <summary>True when the kernel present events (OpenGL/Vulkan frames) are enabled in the running session.</summary>
    public static bool KernelPresents { get; private set; }

    /// <summary>
    /// Enables only DxgKrnl's Present_Info event (EVENT_FILTER_TYPE_EVENT_ID), never the provider's whole firehose. If the
    /// filter isn't supported, the provider stays off rather than flooding the session while a game runs.
    /// </summary>
    private static bool EnableKernelPresents()
    {
        var filter = (byte*)NativeMemory.AllocZeroed(8);     // EVENT_FILTER_EVENT_ID: FilterIn, Reserved, Count, Events[1]
        var desc = (byte*)NativeMemory.AllocZeroed(16);      // EVENT_FILTER_DESCRIPTOR: Ptr, Size, Type
        var parms = (byte*)NativeMemory.AllocZeroed(48);     // ENABLE_TRACE_PARAMETERS (version 2)
        try
        {
            filter[0] = 1;                                   // FilterIn: only the listed events
            *(ushort*)(filter + 2) = 1;
            *(ushort*)(filter + 4) = DxgKrnlPresentInfo;
            *(ulong*)desc = (ulong)filter;
            *(uint*)(desc + 8) = 6;                          // FIELD_OFFSET(Events) + 1 × USHORT
            *(uint*)(desc + 12) = 0x80000200;                // EVENT_FILTER_TYPE_EVENT_ID
            *(uint*)parms = 2;                               // ENABLE_TRACE_PARAMETERS_VERSION_2
            *(IntPtr*)(parms + 32) = (IntPtr)desc;
            *(uint*)(parms + 40) = 1;
            var e = EnableTraceEx2(_session, in DxgKrnlProvider, EnableProvider, 4, DxgKrnlKeywords, 0, 0, (IntPtr)parms);
            if (e != ErrorSuccess) { Log.Info($"Kernel present events not enabled ({e}); OpenGL/Vulkan frames won't be measured"); return false; }
            return true;
        }
        finally { NativeMemory.Free(filter); NativeMemory.Free(desc); NativeMemory.Free(parms); }
    }

    private static CaptureState Set(CaptureState s, string msg)
    {
        State = s;
        Message = msg;
        if (s is CaptureState.NeedsAdmin or CaptureState.Failed) Log.Info("Frame timing: " + msg);
        return s;
    }

    /// <summary>Stops the session and releases everything (also called on app exit).</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (State != CaptureState.Running) { State = CaptureState.Stopped; return; }
            StopLocked();
            Set(CaptureState.Stopped, "Stopped.");
            Log.Info($"Frame-timing ETW session stopped ({EventsSeen} present events)");
        }
    }

    private static void StopLocked()
    {
        var props = NewProps();
        try { ControlTraceW(0, SessionName, props, EventTraceControlStop); } finally { Marshal.FreeHGlobal(props); }
        if (_trace != InvalidTrace) { CloseTrace(_trace); _trace = InvalidTrace; }
        _thread?.Join(2000);
        _thread = null;
        if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
        if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        _session = 0;
    }

    /// <summary>True if a session with our name exists (used by tests to check cleanup).</summary>
    public static bool SessionExists()
    {
        var props = NewProps();
        try { return ControlTraceW(0, SessionName, props, 0 /* EVENT_TRACE_CONTROL_QUERY */) == ErrorSuccess; }
        finally { Marshal.FreeHGlobal(props); }
    }

    // EVENT_RECORD: EventHeader.ProcessId @12, TimeStamp @16 (100 ns), ProviderId @24, EventDescriptor.Id @40.
    [UnmanagedCallersOnly]
    private static void OnEvent(byte* rec)
    {
        try
        {
            var id = *(ushort*)(rec + 40);
            var provider = *(Guid*)(rec + 24);
            PresentApi api;
            if (id == DxgiPresentStart && provider == DxgiProvider) api = PresentApi.Dxgi;
            else if (id == D3D9PresentStart && provider == D3D9Provider) api = PresentApi.D3D9;
            else if (id == DxgKrnlPresentInfo && provider == DxgKrnlProvider) api = PresentApi.Other;
            else return;
            Interlocked.Increment(ref _events);
            Frames.Add((int)*(uint*)(rec + 12), *(long*)(rec + 16), api);
        }
        catch { /* never let an exception cross into native code */ }
    }

    /// <summary>The current time on the same clock as event timestamps (FILETIME, 100 ns).</summary>
    public static long Now => DateTime.UtcNow.ToFileTimeUtc();
}
