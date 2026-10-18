using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>Per-core result of one test round.</summary>
public sealed record CoreTestResult(int Core, int LogicalCpu, bool Passed, string? Failure, long Iterations);

/// <summary>
/// Progress of a guided CPU undervolt. AMD: one Curve Optimizer value per core (Ryzen Master / BIOS). Intel: one core
/// voltage offset for all cores (XTU / BIOS). Money Tweaks can't set these (the vendors offer no interface for other apps),
/// so the user sets the values Money Tweaks suggests, and Money Tweaks tests every core and decides the next step.
/// Saved in curve-assistant.json.
/// </summary>
public sealed class CurveState
{
    public bool PerCore { get; set; } = true;
    /// <summary>The values set now (Curve Optimizer counts, or mV for Intel), as the user confirmed them.</summary>
    public List<int> Values { get; set; } = [];
    /// <summary>Per value slot: the most aggressive value that failed (never suggest that or beyond again).</summary>
    public List<int?> FailedAt { get; set; } = [];
    /// <summary>Per value slot: finished (at the best value found, or at the floor).</summary>
    public List<bool> Done { get; set; } = [];
    public List<CoreTestResult> LastResults { get; set; } = [];
    public DateTime? LastRound { get; set; }
    /// <summary>The suggestion the user still has to make in the vendor tool before the next round.</summary>
    public List<int>? Pending { get; set; }
    /// <summary>Set while a core is being tested. Still set on the next visit = the PC crashed or restarted during it.</summary>
    public int? InProgressCore { get; set; }

    public static string PathFor => Path.Combine(AppPaths.Root, "curve-assistant.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static CurveState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<CurveState>(File.ReadAllText(PathFor), Json) ?? new(); }
        catch (Exception ex) { Log.Warn("curve-assistant.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save() => AtomicFile.WriteAllText(PathFor, JsonSerializer.Serialize(this, Json));

    /// <summary>Starts over for this many slots (cores on AMD, 1 on Intel), all at 0.</summary>
    public void Reset(int slots, bool perCore)
    {
        PerCore = perCore;
        Values = Enumerable.Repeat(0, slots).ToList();
        FailedAt = Enumerable.Repeat<int?>(null, slots).ToList();
        Done = Enumerable.Repeat(false, slots).ToList();
        LastResults = []; LastRound = null; Pending = null; InProgressCore = null;
    }
}

public sealed record CurveRules(int Step, int Floor, string Unit, string Tool)
{
    /// <summary>Ryzen Master's Curve Optimizer range is -30…+30 counts; negative = undervolt.</summary>
    public static readonly CurveRules Amd = new(5, -30, "", "AMD Ryzen Master › Curve Optimizer › Per Core");
    /// <summary>Intel core voltage offset in XTU, in mV. Kept modest: deep offsets mostly gain nothing and many systems lock undervolting.</summary>
    public static readonly CurveRules Intel = new(10, -100, " mV", "Intel XTU › Advanced Tuning › Core Voltage Offset");
}

public sealed record CurveAdvice(List<int> Next, List<string> Lines, bool AllDone, bool Changed);

public static class CurveAssistant
{
    /// <summary>
    /// Pure: from the values that were tested and the per-core results, the next values. A slot that passed goes one step
    /// further (unless that step already failed once or the floor is reached: then it's done at the current value). A
    /// slot that failed goes back one step and remembers the failure. Intel has one slot, failed if any core failed.
    /// </summary>
    public static CurveAdvice Advise(CurveState st, IReadOnlyList<CoreTestResult> results, CurveRules rules)
    {
        var next = st.Values.ToList();
        var lines = new List<string>();
        bool changed = false;
        for (int i = 0; i < st.Values.Count; i++)
        {
            var mine = st.PerCore ? results.Where(r => r.Core == i).ToList() : results.ToList();
            if (mine.Count == 0) continue;
            string who = st.PerCore ? $"Core {i}" : "All cores";
            int v = st.Values[i];
            var failed = mine.FirstOrDefault(r => !r.Passed);
            if (failed is not null)
            {
                st.FailedAt[i] = st.FailedAt[i] is { } f ? Math.Max(f, v) : v;
                st.Done[i] = false;
                if (v >= 0)
                {
                    lines.Add($"{who} failed at {Fmt(v, rules)} ({failed.Failure}). It's unstable without any undervolt: set it to 0 and reset PBO/overclocks to default before going on.");
                    next[i] = 0;
                }
                else
                {
                    next[i] = Math.Min(0, v + rules.Step);
                    lines.Add($"{who} failed at {Fmt(v, rules)} ({failed.Failure}) → back to {Fmt(next[i], rules)}.");
                }
                changed |= next[i] != v;
                continue;
            }
            if (st.Done[i]) { lines.Add($"{who}: done at {Fmt(v, rules)} (passed again)."); continue; }
            int further = v - rules.Step;
            if (further < rules.Floor || (st.FailedAt[i] is { } lim && further <= lim))
            {
                st.Done[i] = true;
                lines.Add($"{who} passed at {Fmt(v, rules)}: done" + (further < rules.Floor ? $" (the {Fmt(rules.Floor, rules)} limit)." : $" ({Fmt(further, rules)} already failed)."));
            }
            else
            {
                next[i] = further;
                changed = true;
                lines.Add($"{who} passed at {Fmt(v, rules)} → try {Fmt(further, rules)}.");
            }
        }
        return new(next, lines, st.Done.Count > 0 && st.Done.All(d => d), changed);
    }

    public static string Fmt(int v, CurveRules r) => $"{v:+0;-0;0}{r.Unit}";

    // ---- per-core load -------------------------------------------------------------------------------------------------

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformation(nint buffer, ref uint length);

    /// <summary>One logical CPU per physical core (its first thread), in core order. This is the numbering Ryzen Master's
    /// per-core list uses on standard systems. Falls back to every other CPU when SMT is on and Windows doesn't answer.</summary>
    public static List<int> PhysicalCores()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformation(0, ref len);
            if (len > 0)
            {
                var buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (GetLogicalProcessorInformation(buf, ref len))
                    {
                        const int size = 32; // SYSTEM_LOGICAL_PROCESSOR_INFORMATION on x64: mask (8), relationship (4), padding (4), union (16)
                        var cores = new List<int>();
                        for (int off = 0; off + size <= len; off += size)
                        {
                            ulong mask = (ulong)Marshal.ReadInt64(buf, off);
                            int rel = Marshal.ReadInt32(buf, off + 8);
                            if (rel == 0 && mask != 0) cores.Add(System.Numerics.BitOperations.TrailingZeroCount(mask)); // RelationProcessorCore
                        }
                        if (cores.Count > 0) return cores.Distinct().Order().Where(c => c < 64).ToList();
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch (Exception ex) { Log.Debug("cpu topology: " + ex.Message); }
        int n = Environment.ProcessorCount;
        return Enumerable.Range(0, n).Where(i => n % 2 == 1 || i % 2 == 0).ToList();
    }

    /// <summary>Single-threaded self-checking load pinned to one logical CPU. One core alone boosts highest, which is
    /// where too much undervolt shows (an all-core load hides it). Wrong results, WHEA errors and temperature fail it.</summary>
    public static async Task<CoreTestResult> TestCoreAsync(int core, int logicalCpu, TimeSpan duration, CancellationToken ct,
        Func<DateTime, int>? wheaSince = null, Func<double?>? cpuTemp = null)
    {
        wheaSince ??= since => StabilityTest.CountEventsSince(since).whea;
        var start = DateTime.Now;
        var expected = Enumerable.Range(1, 8).Select(StabilityTest.Workload).ToArray();
        long iterations = 0; int errors = 0; bool pinned = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var worker = new Thread(() =>
        {
            pinned = SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << logicalCpu) != 0;
            int k = 0;
            while (!stop.IsCancellationRequested)
            {
                int seed = k++ % 8 + 1;
                if (StabilityTest.Workload(seed) != expected[seed - 1]) Interlocked.Increment(ref errors);
                Interlocked.Increment(ref iterations);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "core test " + core };
        worker.Start();
        string? failure = null;
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed < duration)
            {
                await Task.Delay(1000, ct);
                if (Volatile.Read(ref errors) > 0) { failure = "wrong calculation results"; break; }
                if (cpuTemp?.Invoke() is { } t && t >= 95) { failure = $"CPU reached {t:0} °C"; break; }
            }
        }
        finally { stop.Cancel(); worker.Join(); }
        if (!pinned) failure ??= "couldn't pin the test to this core";
        if (failure is null && wheaSince(start) is var w and > 0) failure = $"{w} WHEA hardware error(s)";
        return new(core, logicalCpu, failure is null, failure, Interlocked.Read(ref iterations));
    }
}
