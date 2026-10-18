using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>Settings to apply. Null = leave unchanged. Clock limits: (0, 0) = remove the lock (driver default).</summary>
public sealed record TuningSettings
{
    public double? GpuPowerLimitW { get; init; }
    public (int min, int max)? GpuClockLock { get; init; }
    public (int min, int max)? MemClockLock { get; init; }
    public int? CpuBoostMode { get; init; }
    public int? CpuMaxState { get; init; }
    public int? CpuEpp { get; init; }
    public bool IsEmpty => GpuPowerLimitW is null && GpuClockLock is null && MemClockLock is null && CpuBoostMode is null && CpuMaxState is null && CpuEpp is null;
}

/// <summary>Configured = chosen; Applied = the driver/Windows accepted it; Verified = read back and matches.</summary>
public enum ItemStatus { Configured, Applied, Verified, Failed, RolledBack }

public sealed record TuningItemResult(string Item, string Value, ItemStatus Status, string Detail);

public sealed record TuningProfile(string Id, string Name, string Summary, TuningSettings Settings, string Caveat);

/// <summary>Original values from before Money Tweaks' first tuning change, plus what is applied now. tuning.json.</summary>
public sealed class TuningState
{
    public int GpuIndex { get; set; }
    public double? OrigGpuPowerLimitW { get; set; }
    public Dictionary<string, int[]> OrigCpu { get; set; } = []; // alias -> [ac, dc]
    public bool BackupTaken { get; set; }
    public bool GpuClocksLocked { get; set; }
    public bool MemClocksLocked { get; set; }
    public int? LockedMaxGpuMhz { get; set; }
    public List<TuningItemResult> LastApply { get; set; } = [];
    public string? ProfileName { get; set; }
    public DateTime? AppliedAt { get; set; }
    /// <summary>True from apply until a stability test passes or the user keeps the settings. If the app closes or
    /// crashes meanwhile, the next start offers to revert (it never re-applies anything on its own).</summary>
    public bool PendingConfirmation { get; set; }
    public string? LastTestSummary { get; set; }

    private static string PathFor => System.IO.Path.Combine(AppPaths.Root, "tuning.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static TuningState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<TuningState>(File.ReadAllText(PathFor), Json) ?? new(); }
        catch (Exception ex)
        {
            Log.Warn("tuning.json unreadable (kept as .bad): " + ex.Message);
            try { File.Copy(PathFor, PathFor + ".bad", true); } catch { }
        }
        return new();
    }

    public void Save() => AtomicFile.WriteAllText(PathFor, JsonSerializer.Serialize(this, Json));
    public bool HasChanges => GpuClocksLocked || MemClocksLocked || ProfileName is not null || LastApply.Any(r => r.Status is ItemStatus.Applied or ItemStatus.Verified);
}

/// <summary>
/// Applies tuning through <see cref="NvidiaSmi"/> and <see cref="CpuPowerSettings"/> with backup first, read-back,
/// all-or-nothing rollback and a crash marker. Nothing here runs at startup.
/// </summary>
public static class TuningEngine
{
    /// <summary>The I/O behind the engine. Tests replace it; the app uses nvidia-smi and powercfg.</summary>
    public interface IBackend
    {
        double? ReadPowerLimit(int gpu);
        (bool ok, string msg) SetPowerLimit(int gpu, double w);
        (bool ok, string msg) LockGpu(int gpu, int min, int max);
        (bool ok, string msg) ResetGpu(int gpu);
        (bool ok, string msg) LockMem(int gpu, int min, int max);
        (bool ok, string msg) ResetMem(int gpu);
        (int? ac, int? dc) ReadCpu(string alias);
        (bool ok, string msg) SetCpu(string alias, int ac, int dc);
    }

    private sealed class RealBackend : IBackend
    {
        public double? ReadPowerLimit(int gpu) => NvidiaSmi.Read(gpu)?.PowerLimitW;
        public (bool, string) SetPowerLimit(int gpu, double w) => NvidiaSmi.SetPowerLimit(gpu, w);
        public (bool, string) LockGpu(int gpu, int min, int max) => NvidiaSmi.LockGpuClocks(gpu, min, max);
        public (bool, string) ResetGpu(int gpu) => NvidiaSmi.ResetGpuClocks(gpu);
        public (bool, string) LockMem(int gpu, int min, int max) => NvidiaSmi.LockMemoryClocks(gpu, min, max);
        public (bool, string) ResetMem(int gpu) => NvidiaSmi.ResetMemoryClocks(gpu);
        public (int?, int?) ReadCpu(string alias) { var s = CpuPowerSettings.Read(alias); return (s.Ac, s.Dc); }
        public (bool, string) SetCpu(string alias, int ac, int dc) => CpuPowerSettings.Set(alias, ac, dc);
    }

    public static IBackend Backend { get; set; } = new RealBackend();

    // ---- validation & preview ---------------------------------------------------------------------------------------

    /// <summary>Problems that stop an apply: values outside what the driver/Windows reports as valid.</summary>
    public static List<string> Validate(TuningSettings s, NvidiaGpuLimits? gpu, IReadOnlyDictionary<string, CpuPowerSetting> cpu)
    {
        var e = new List<string>();
        if (s.GpuPowerLimitW is { } w)
        {
            if (gpu is null || !gpu.PowerLimitSupported) e.Add("This GPU's power limit can't be changed.");
            else if (w < gpu.MinPowerLimitW || w > gpu.MaxPowerLimitW) e.Add($"Power limit {w:0} W is outside {gpu.MinPowerLimitW:0}–{gpu.MaxPowerLimitW:0} W.");
        }
        if (s.GpuClockLock is { } g && g != (0, 0))
        {
            if (gpu is null || !gpu.ClockLockSupported) e.Add("This GPU's clocks can't be locked.");
            else if (g.min > g.max || !gpu.GraphicsClocks.Contains(g.max) || !gpu.GraphicsClocks.Contains(g.min)) e.Add($"GPU clock range {g.min}–{g.max} MHz isn't one the driver supports.");
        }
        if (s.MemClockLock is { } m && m != (0, 0))
        {
            if (gpu is null || !gpu.MemoryLockSupported) e.Add("This GPU's memory clock can't be locked.");
            else if (m.min > m.max || !gpu.MemoryClocks.Contains(m.max) || !gpu.MemoryClocks.Contains(m.min)) e.Add($"Memory clock range {m.min}–{m.max} MHz isn't supported.");
        }
        void Cpu(int? v, string alias)
        {
            if (v is not { } x) return;
            if (!cpu.TryGetValue(alias, out var c) || !c.Readable) e.Add($"{alias} can't be read on this power plan.");
            else if (x < c.Min || x > c.Max) e.Add($"{c.Label} {x} is outside {c.Min}–{c.Max}.");
        }
        Cpu(s.CpuBoostMode, CpuPowerSettings.BoostMode);
        Cpu(s.CpuMaxState, CpuPowerSettings.MaxState);
        Cpu(s.CpuEpp, CpuPowerSettings.Epp);
        return e;
    }

    /// <summary>"Current → new" lines for the confirmation preview.</summary>
    public static List<string> Preview(TuningSettings s, NvidiaGpuLimits? gpu, IReadOnlyDictionary<string, CpuPowerSetting> cpu, TuningState st)
    {
        var l = new List<string>();
        if (s.GpuPowerLimitW is { } w) l.Add($"GPU power limit: {gpu?.PowerLimitW:0} W → {w:0} W");
        if (s.GpuClockLock is { } g) l.Add(g == (0, 0) ? $"GPU clocks: {(st.GpuClocksLocked ? $"locked up to {st.LockedMaxGpuMhz} MHz" : "driver default")} → driver default" : $"GPU clocks: locked to {g.min}–{g.max} MHz");
        if (s.MemClockLock is { } m) l.Add(m == (0, 0) ? "Memory clock → driver default" : $"Memory clock: locked to {m.min}–{m.max} MHz");
        void Cpu(int? v, string alias)
        {
            if (v is not { } x || !cpu.TryGetValue(alias, out var c)) return;
            l.Add($"CPU {c.Label.ToLowerInvariant()}: {(c.Ac is { } a ? c.Describe(a) : "?")} → {c.Describe(x)} (plugged in and on battery)");
        }
        Cpu(s.CpuBoostMode, CpuPowerSettings.BoostMode);
        Cpu(s.CpuMaxState, CpuPowerSettings.MaxState);
        Cpu(s.CpuEpp, CpuPowerSettings.Epp);
        return l;
    }

    // ---- apply / rollback ----------------------------------------------------------------------------------------------

    /// <summary>Backs up originals (first time only), applies each item, reads it back, and undoes everything from this
    /// apply if any item fails. Returns per-item results.</summary>
    public static (bool ok, List<TuningItemResult> results) Apply(TuningSettings s, TuningState st, string? profileName = null)
    {
        var results = new List<TuningItemResult>();
        var undo = new List<Action>();
        var b = Backend;
        int gpu = st.GpuIndex;

        if (!st.BackupTaken)
        {
            st.OrigGpuPowerLimitW = b.ReadPowerLimit(gpu);
            foreach (var alias in new[] { CpuPowerSettings.BoostMode, CpuPowerSettings.MaxState, CpuPowerSettings.Epp })
                if (b.ReadCpu(alias) is ({ } ac, { } dc)) st.OrigCpu[alias] = [ac, dc];
            st.BackupTaken = true;
            st.Save(); // on disk before anything changes
        }
        st.PendingConfirmation = true; // crash marker, saved before the first change
        st.Save();

        bool Step(string item, string value, Func<(bool ok, string msg)> apply, Func<bool?> verify, Action revert)
        {
            var (ok, msg) = apply();
            if (!ok) { results.Add(new(item, value, ItemStatus.Failed, msg)); return false; }
            undo.Add(revert);
            var v = verify();
            results.Add(new(item, value, v switch { true => ItemStatus.Verified, false => ItemStatus.Failed, null => ItemStatus.Applied },
                v switch { true => "Read back from the driver/Windows and matches.", false => "Accepted, but the read-back doesn't match.", null => msg + " (can't be read back directly; checked during stability tests)" }));
            return v != false;
        }

        bool all = true;
        if (s.GpuPowerLimitW is { } w)
        {
            var before = b.ReadPowerLimit(gpu);
            all &= Step("GPU power limit", $"{w:0} W", () => b.SetPowerLimit(gpu, w), () => b.ReadPowerLimit(gpu) is { } now ? Math.Abs(now - w) < 0.6 : null,
                () => { if (before is { } x) b.SetPowerLimit(gpu, x); });
        }
        if (all && s.GpuClockLock is { } g)
        {
            bool wasLocked = st.GpuClocksLocked; int? wasMax = st.LockedMaxGpuMhz;
            all &= g == (0, 0)
                ? Step("GPU clocks", "driver default", () => b.ResetGpu(gpu), () => null, () => { })
                : Step("GPU clock lock", $"{g.min}–{g.max} MHz", () => b.LockGpu(gpu, g.min, g.max), () => null, () => b.ResetGpu(gpu));
            if (all) { st.GpuClocksLocked = g != (0, 0); st.LockedMaxGpuMhz = g == (0, 0) ? null : g.max; }
            else { st.GpuClocksLocked = wasLocked; st.LockedMaxGpuMhz = wasMax; }
        }
        if (all && s.MemClockLock is { } m)
        {
            all &= m == (0, 0)
                ? Step("Memory clocks", "driver default", () => b.ResetMem(gpu), () => null, () => { })
                : Step("Memory clock lock", $"{m.min}–{m.max} MHz", () => b.LockMem(gpu, m.min, m.max), () => null, () => b.ResetMem(gpu));
            if (all) st.MemClocksLocked = m != (0, 0);
        }
        void Cpu(int? v, string alias, string label)
        {
            if (!all || v is not { } x) return;
            var (ac, dc) = b.ReadCpu(alias);
            all &= Step(label, x.ToString(), () => b.SetCpu(alias, x, x), () => b.ReadCpu(alias) is ({ } a, { } d) ? a == x && d == x : null,
                () => { if (ac is { } oa && dc is { } od) b.SetCpu(alias, oa, od); });
        }
        Cpu(s.CpuBoostMode, CpuPowerSettings.BoostMode, "CPU boost mode");
        Cpu(s.CpuMaxState, CpuPowerSettings.MaxState, "CPU maximum state");
        Cpu(s.CpuEpp, CpuPowerSettings.Epp, "CPU energy preference");

        if (!all)
        {
            foreach (var u in Enumerable.Reverse(undo)) { try { u(); } catch (Exception ex) { Log.Error("tuning undo", ex); } }
            results = results.Select(r => r.Status is ItemStatus.Applied or ItemStatus.Verified ? r with { Status = ItemStatus.RolledBack, Detail = "Undone because another item failed." } : r).ToList();
            st.PendingConfirmation = st.HasChanges && st.PendingConfirmation;
        }
        else
        {
            st.ProfileName = profileName ?? "Custom";
            st.AppliedAt = DateTime.Now;
        }
        st.LastApply = results;
        st.Save();
        Log.Info($"Tuning apply ({profileName ?? "custom"}): {(all ? "ok" : "failed, rolled back")} – {string.Join(" | ", results.Select(r => $"{r.Item}={r.Value}:{r.Status}"))}");
        return (all, results);
    }

    /// <summary>Puts every original value back (from the first backup) and removes clock locks. Read back where possible.</summary>
    public static (bool ok, List<string> log) RevertAll(TuningState st)
    {
        var b = Backend; var log = new List<string>(); bool ok = true; int gpu = st.GpuIndex;
        if (st.GpuClocksLocked || st.LastApply.Any(r => r.Item.StartsWith("GPU clock", StringComparison.Ordinal)))
        {
            var r = b.ResetGpu(gpu); ok &= r.ok; log.Add($"GPU clocks → driver default: {(r.ok ? "done" : r.msg)}");
            if (r.ok) { st.GpuClocksLocked = false; st.LockedMaxGpuMhz = null; }
        }
        if (st.MemClocksLocked)
        {
            var r = b.ResetMem(gpu); ok &= r.ok; log.Add($"Memory clocks → driver default: {(r.ok ? "done" : r.msg)}");
            if (r.ok) st.MemClocksLocked = false;
        }
        if (st.OrigGpuPowerLimitW is { } pl && b.ReadPowerLimit(gpu) is { } now && Math.Abs(now - pl) >= 0.6)
        {
            var r = b.SetPowerLimit(gpu, pl);
            var back = b.ReadPowerLimit(gpu);
            bool v = r.ok && back is { } x && Math.Abs(x - pl) < 0.6;
            ok &= v; log.Add($"GPU power limit → {pl:0} W: {(v ? "verified" : r.msg)}");
        }
        foreach (var (alias, vals) in st.OrigCpu)
        {
            if (b.ReadCpu(alias) is ({ } a, { } d) && a == vals[0] && d == vals[1]) continue;
            var r = b.SetCpu(alias, vals[0], vals[1]);
            bool v = r.ok && b.ReadCpu(alias) is ({ } a2, { } d2) && a2 == vals[0] && d2 == vals[1];
            ok &= v; log.Add($"{alias} → {vals[0]}/{vals[1]}: {(v ? "verified" : r.msg)}");
        }
        if (ok)
        {
            st.ProfileName = null; st.AppliedAt = null; st.PendingConfirmation = false;
            st.LastApply = [];
        }
        st.Save();
        Log.Info($"Tuning revert: {(ok ? "ok" : "problems")} – {string.Join(" | ", log)}");
        return (ok, log);
    }

    // ---- profiles ----------------------------------------------------------------------------------------------------

    /// <summary>Supported presets for this hardware. Values come from the driver's own limits and Windows' documented ranges.</summary>
    public static List<TuningProfile> Profiles(NvidiaGpuLimits? gpu, IReadOnlyDictionary<string, CpuPowerSetting> cpu)
    {
        bool cpuOk = cpu.TryGetValue(CpuPowerSettings.BoostMode, out var bm) && bm.Readable && bm.Max >= 4
                     && cpu.TryGetValue(CpuPowerSettings.Epp, out var ep) && ep.Readable;
        double? Pl(double factor) => gpu is { PowerLimitSupported: true, DefaultPowerLimitW: { } d } ? Math.Clamp(Math.Round(d * factor), gpu.MinPowerLimitW!.Value, gpu.MaxPowerLimitW!.Value) : null;
        (int, int)? Cap(double factor)
        {
            if (gpu is not { ClockLockSupported: true }) return null;
            var target = gpu.MaxGraphics!.Value * factor;
            var max = gpu.GraphicsClocks.Where(c => c <= target).DefaultIfEmpty(gpu.MaxGraphics.Value).Max();
            return (gpu.MinGraphics!.Value, max);
        }
        return
        [
            new("efficiency", "Efficiency", "GPU power limit 85% of default; CPU boosts efficiently with a balanced energy preference.",
                new TuningSettings { GpuPowerLimitW = Pl(0.85), CpuBoostMode = cpuOk ? 4 : null, CpuEpp = cpuOk ? 33 : null },
                "Usually a few percent less GPU performance for noticeably less power and heat. Measure it in your games."),
            new("quiet", "Quiet", "GPU power limit 70% of default and a GPU clock cap; CPU boost less eager.",
                new TuningSettings { GpuPowerLimitW = Pl(0.70), GpuClockLock = Cap(0.78), CpuBoostMode = cpuOk ? 3 : null, CpuEpp = cpuOk ? 60 : null },
                "Lower fan noise and temperatures; clearly lower performance in demanding games."),
            new("headroom", "Maximum headroom", "GPU power limit raised to the maximum the driver allows; CPU boosts aggressively.",
                new TuningSettings { GpuPowerLimitW = gpu is { PowerLimitSupported: true } ? gpu.MaxPowerLimitW : null, CpuBoostMode = cpuOk ? 2 : null, CpuEpp = cpuOk ? 0 : null },
                "Raises limits only (no clock offset): more heat and power, and the gain is often small. For a real automatic GPU overclock use NVIDIA App › Automatic Tuning."),
        ];
    }
}
