using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>A min/current/max/default control from Afterburner's control memory. Clocks in MHz, power in %, thermal in °C.</summary>
public sealed record AbRange(int Cur, int Min, int Max, int Def)
{
    public bool Adjustable => Max > Min;
}

/// <summary>One point of the voltage/frequency curve: voltage, current frequency and its offset (MHz).</summary>
public sealed record VfPoint(int MilliVolts, int FrequencyMhz, int OffsetMhz)
{
    public int BaseMhz => FrequencyMhz - OffsetMhz;
}

/// <summary>The master GPU's controls as Afterburner exposes them, plus the raw entry for an exact rollback.</summary>
public sealed record AbGpu(string GpuId, uint Flags, AbRange? CoreOffset, AbRange? MemOffset, AbRange? PowerLimit, AbRange? ThermalLimit,
    AbRange? Fan, bool FanAuto, bool FanAutoDefault, IReadOnlyList<VfPoint> Curve, bool CurveEnabled)
{
    public bool HasCurve => Curve.Count > 1;
}

/// <summary>
/// MSI Afterburner's documented third-party interfaces (SDK\Include\MACMSharedMemory.h and MAHMSharedMemory.h, shipped
/// with Afterburner): control memory for clock offsets, voltage/frequency curve, fan, power and thermal limits, and
/// monitoring memory for sensors (including CPU temperature through Afterburner's own driver). Money Tweaks follows the
/// SDK sample's protocol: lock the Global\Access_MACMSharedMemory mutex, copy the memory, change fields, write it back,
/// set the FLUSH command, notify Afterburner and wait for it to apply and read back. Only works while Afterburner runs;
/// Afterburner runs elevated, so changing settings needs Money Tweaks elevated too.
/// </summary>
public static class Afterburner
{
    public const uint SignatureMacm = 0x4D41434D; // 'MACM'
    public const uint SignatureMahm = 0x4D41484D; // 'MAHM'
    public const uint CommandFlush = 0x00AB0001;
    public const uint SrcCpuTemperature = 0x80, SrcGpuTemperature = 0x00;

    // MACM_SHARED_MEMORY_GPU_ENTRY flags
    public const uint FlagCoreClockBoost = 0x0800, FlagMemoryClockBoost = 0x1000, FlagFanSpeed = 0x0008, FlagPowerLimit = 0x0400,
        FlagThermalLimit = 0x2000, FlagVfCurve = 0x20000, FlagVfCurveEnabled = 0x40000;
    public const uint FanFlagAuto = 0x1;

    // Byte offsets in MACM_SHARED_MEMORY_GPU_ENTRY (all fields are 4-byte DWORD/LONG, so no padding)
    public const int OffFlags = 0, OffFanCur = 52, OffFanFlagsCur = 56, OffFanMin = 60, OffFanMax = 64, OffFanDef = 68, OffFanFlagsDef = 72,
        OffPowerLimit = 172, OffCoreBoost = 188, OffMemBoost = 204, OffThermal = 220, OffVf = 276;
    public const int VfPointsMax = 256, VfPointSize = 12;
    public const int OffVfPoints = OffVf + 12, OffVfLockIndex = OffVfPoints + VfPointsMax * VfPointSize, OffGpuId = OffVfLockIndex + 4 + 4 + 64 + 4 + 64;
    public const int EntrySizeV23 = OffGpuId + 260;

    // MACM header: signature, version, headerSize, numGpu, gpuEntrySize, masterGpu, flags, time32, command
    public const int HOffSignature = 0, HOffHeaderSize = 8, HOffNumGpu = 12, HOffEntrySize = 16, HOffMaster = 20, HOffCommand = 32;

    /// <summary>The shared memory, abstracted so tests can use a byte array laid out like the SDK header.</summary>
    public interface IControlMemory
    {
        byte[]? Read();                                 // whole control memory, or null if Afterburner isn't running
        bool Write(byte[] data, bool flushCommand);     // write back (under the SDK mutex) and optionally set FLUSH
        bool WaitForCompletion(int timeoutMs);          // notify Afterburner and wait until it has executed the command
    }

    public static IControlMemory Control { get; set; } = new SharedControlMemory();
    public static Func<byte[]?> MonitoringReader { get; set; } = ReadShared("MAHMSharedMemory");

    public static bool IsRunning => Control.Read() is { } d && d.Length >= 36 && U32(d, HOffSignature) == SignatureMacm;

    // ---- parse ---------------------------------------------------------------------------------------------------

    /// <summary>The master GPU's entry offset and size, or null if the memory isn't valid.</summary>
    public static (int offset, int size)? MasterEntry(byte[] d)
    {
        if (d.Length < 36 || U32(d, HOffSignature) != SignatureMacm) return null;
        int header = (int)U32(d, HOffHeaderSize), size = (int)U32(d, HOffEntrySize), n = (int)U32(d, HOffNumGpu), master = (int)U32(d, HOffMaster);
        if (n == 0 || master >= n || size < OffThermal || header < 36) return null;
        int off = header + master * size;
        return off + size <= d.Length ? (off, size) : null;
    }

    public static AbGpu? Parse(byte[] d)
    {
        if (MasterEntry(d) is not ({ } o, { } size)) return null;
        uint flags = U32(d, o + OffFlags);
        AbRange R(int at, bool khz) { int Div(int v) => khz ? v / 1000 : v; return new(Div(I32(d, o + at)), Div(I32(d, o + at + 4)), Div(I32(d, o + at + 8)), Div(I32(d, o + at + 12))); }
        var curve = new List<VfPoint>();
        if ((flags & FlagVfCurve) != 0 && size >= OffVfLockIndex)
        {
            int points = Math.Min(VfPointsMax, (int)U32(d, o + OffVf + 8));
            for (int i = 0; i < points; i++)
            {
                int p = o + OffVfPoints + i * VfPointSize;
                curve.Add(new((int)(U32(d, p) / 1000), (int)(U32(d, p + 4) / 1000), I32(d, p + 8) / 1000));
            }
        }
        string id = size >= EntrySizeV23 ? Encoding.ASCII.GetString(d, o + OffGpuId, 260).TrimEnd('\0') : "";
        return new(id, flags,
            (flags & FlagCoreClockBoost) != 0 ? R(OffCoreBoost, true) : null,
            (flags & FlagMemoryClockBoost) != 0 ? R(OffMemBoost, true) : null,
            (flags & FlagPowerLimit) != 0 ? R(OffPowerLimit, false) : null,
            (flags & FlagThermalLimit) != 0 && size >= OffThermal + 16 ? R(OffThermal, false) : null,
            (flags & FlagFanSpeed) != 0 ? new(I32(d, o + OffFanCur), I32(d, o + OffFanMin), I32(d, o + OffFanMax), I32(d, o + OffFanDef)) : null,
            (U32(d, o + OffFanFlagsCur) & FanFlagAuto) != 0, (U32(d, o + OffFanFlagsDef) & FanFlagAuto) != 0,
            curve, (flags & FlagVfCurveEnabled) != 0);
    }

    public static AbGpu? Read() => Control.Read() is { } d ? Parse(d) : null;

    // ---- change --------------------------------------------------------------------------------------------------------

    /// <summary>What to change; null = leave as is. Clock offsets in MHz, power in %, thermal in °C, fan in % (null + FanAuto = auto).</summary>
    public sealed record Change
    {
        public int? CoreOffsetMhz { get; init; }
        public int? MemOffsetMhz { get; init; }
        public int? PowerLimitPercent { get; init; }
        public int? ThermalLimitC { get; init; }
        public int? FanPercent { get; init; }
        public bool? FanAuto { get; init; }
        /// <summary>Undervolt: flatten the curve at this voltage to this clock (both must be set).</summary>
        public (int milliVolts, int mhz)? Undervolt { get; init; }
        public bool ResetCurve { get; init; }
    }

    /// <summary>Range checks against the limits Afterburner reports. Empty = OK.</summary>
    public static List<string> Validate(Change c, AbGpu g)
    {
        var e = new List<string>();
        void In(int? v, AbRange? r, string what, string unit)
        {
            if (v is not { } x) return;
            if (r is null || !r.Adjustable) e.Add($"{what} isn't adjustable on this GPU.");
            else if (x < r.Min || x > r.Max) e.Add($"{what} {x}{unit} is outside {r.Min}–{r.Max}{unit}.");
        }
        In(c.CoreOffsetMhz, g.CoreOffset, "Core clock offset", " MHz");
        In(c.MemOffsetMhz, g.MemOffset, "Memory clock offset", " MHz");
        In(c.PowerLimitPercent, g.PowerLimit, "Power limit", "%");
        In(c.ThermalLimitC, g.ThermalLimit, "Temperature limit", " °C");
        In(c.FanPercent, g.Fan, "Fan speed", "%");
        if (c.Undervolt is { } u)
        {
            if (!g.HasCurve) e.Add("This GPU's voltage/frequency curve isn't available in Afterburner.");
            else if (!g.Curve.Any(p => p.MilliVolts == u.milliVolts)) e.Add($"{u.milliVolts} mV isn't a point on the curve.");
            else if (u.mhz > g.Curve.Max(p => p.BaseMhz) + (g.CoreOffset?.Max ?? 0)) e.Add($"{u.mhz} MHz is above what the curve allows.");
        }
        return e;
    }

    /// <summary>Applies the change to a copy of the control memory (pure; tests use it directly).</summary>
    public static byte[] ApplyTo(byte[] src, Change c)
    {
        var d = (byte[])src.Clone();
        if (MasterEntry(d) is not ({ } o, { } size)) throw new InvalidOperationException("MSI Afterburner control memory isn't valid.");
        uint flags = U32(d, o + OffFlags);
        if (c.CoreOffsetMhz is { } core) W32(d, o + OffCoreBoost, core * 1000);
        if (c.MemOffsetMhz is { } mem) W32(d, o + OffMemBoost, mem * 1000);
        if (c.PowerLimitPercent is { } pl) W32(d, o + OffPowerLimit, pl);
        if (c.ThermalLimitC is { } tl) W32(d, o + OffThermal, tl);
        if (c.FanAuto == true) WU32(d, o + OffFanFlagsCur, U32(d, o + OffFanFlagsCur) | FanFlagAuto);
        if (c.FanPercent is { } fan)
        {
            W32(d, o + OffFanCur, fan);
            WU32(d, o + OffFanFlagsCur, U32(d, o + OffFanFlagsCur) & ~FanFlagAuto);
        }
        if ((c.Undervolt is not null || c.ResetCurve) && (flags & FlagVfCurve) != 0 && size >= OffVfLockIndex)
        {
            int points = Math.Min(VfPointsMax, (int)U32(d, o + OffVf + 8));
            for (int i = 0; i < points; i++)
            {
                int p = o + OffVfPoints + i * VfPointSize;
                int mv = (int)(U32(d, p) / 1000), freqKhz = (int)U32(d, p + 4), offKhz = I32(d, p + 8), baseKhz = freqKhz - offKhz;
                int newOff = c.Undervolt is { } u ? UndervoltOffsetKhz(mv, baseKhz, u.milliVolts, u.mhz * 1000, d, o, points) : 0;
                W32(d, p + 8, newOff);
                W32(d, p + 4, baseKhz + newOff);
            }
            flags = c.Undervolt is not null ? flags | FlagVfCurveEnabled : flags & ~FlagVfCurveEnabled;
            WU32(d, o + OffFlags, flags);
        }
        return d;
    }

    /// <summary>The classic undervolt: the point at the chosen voltage reaches the target clock, every higher-voltage point
    /// is capped at that clock (so the GPU never needs more voltage), and lower points get the same offset, never above it.</summary>
    private static int UndervoltOffsetKhz(int mv, int baseKhz, int targetMv, int targetKhz, byte[] d, int o, int points)
    {
        int targetBase = 0;
        for (int i = 0; i < points; i++)
        {
            int p = o + OffVfPoints + i * VfPointSize;
            if ((int)(U32(d, p) / 1000) == targetMv) { targetBase = (int)U32(d, p + 4) - I32(d, p + 8); break; }
        }
        int offsetAtTarget = targetKhz - targetBase;
        return mv >= targetMv ? targetKhz - baseKhz : Math.Min(offsetAtTarget, targetKhz - baseKhz);
    }

    /// <summary>Writes the change, waits for Afterburner to apply it and reads the result back.
    /// Returns the control memory as it was before (for rollback) and what Afterburner reports afterwards.</summary>
    public static (bool ok, string message, byte[]? before, AbGpu? after) Apply(Change c)
    {
        var mem = Control.Read();
        if (mem is null || Parse(mem) is not { } g) return (false, "MSI Afterburner isn't running (or its control memory isn't available).", null, null);
        var errors = Validate(c, g);
        if (errors.Count > 0) return (false, string.Join(" ", errors), null, null);
        var updated = ApplyTo(mem, c);
        if (!Control.Write(updated, flushCommand: true)) return (false, "Couldn't write to MSI Afterburner's control memory (run Money Tweaks as administrator).", null, null);
        if (!Control.WaitForCompletion(5000)) return (false, "MSI Afterburner didn't confirm the change within 5 seconds.", mem, Read());
        var after = Read();
        Log.Info($"Afterburner apply {c} -> {(after is null ? "no read-back" : "read back")}");
        return (true, "Applied by MSI Afterburner and read back.", mem, after);
    }

    /// <summary>Writes an earlier control-memory copy back (exact rollback of every Afterburner setting).</summary>
    public static (bool ok, string message) Restore(byte[] snapshot)
    {
        var mem = Control.Read();
        if (mem is null) return (false, "MSI Afterburner isn't running; start it to restore (or restore its default profile there).");
        if (mem.Length != snapshot.Length || MasterEntry(mem) is null) return (false, "MSI Afterburner's layout changed since the backup; use its Reset button instead.");
        // Copy only the master GPU entry back; the header (time, command) belongs to Afterburner.
        var (o, size) = MasterEntry(snapshot)!.Value;
        var copy = (byte[])mem.Clone();
        Buffer.BlockCopy(snapshot, o, copy, o, size);
        if (!Control.Write(copy, flushCommand: true)) return (false, "Couldn't write to MSI Afterburner's control memory.");
        return Control.WaitForCompletion(5000) ? (true, "Previous MSI Afterburner settings restored.") : (false, "MSI Afterburner didn't confirm the restore.");
    }

    // ---- monitoring ----------------------------------------------------------------------------------------------------

    /// <summary>CPU temperature from Afterburner's monitoring memory (hottest CPU temperature source), or null.</summary>
    public static double? CpuTemperature() => MonitoringValue(SrcCpuTemperature, cpu: true);

    public static double? MonitoringValue(uint srcId, bool cpu)
    {
        try
        {
            var d = MonitoringReader();
            if (d is null || d.Length < 32 || U32(d, 0) != SignatureMahm) return null;
            int header = (int)U32(d, 8), n = (int)U32(d, 12), size = (int)U32(d, 16);
            const int dataOff = 260 * 5, gpuOff = dataOff + 16, srcOff = dataOff + 20;
            if (size < srcOff + 4) return null;
            double? best = null;
            for (int i = 0; i < n; i++)
            {
                int e = header + i * size;
                if (e + size > d.Length) break;
                if (U32(d, e + srcOff) != srcId) continue;
                if (cpu != (U32(d, e + gpuOff) == 0xFFFFFFFF)) continue;
                float v = BitConverter.ToSingle(d, e + dataOff);
                if (v is >= float.MaxValue or <= 0 or > 150 || float.IsNaN(v)) continue;
                best = Math.Max(best ?? 0, v);
            }
            return best;
        }
        catch (Exception ex) { Log.Debug("afterburner monitoring: " + ex.Message); return null; }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    public static uint U32(byte[] d, int at) => BitConverter.ToUInt32(d, at);
    public static int I32(byte[] d, int at) => BitConverter.ToInt32(d, at);
    public static void W32(byte[] d, int at, int v) => BitConverter.GetBytes(v).CopyTo(d, at);
    public static void WU32(byte[] d, int at, uint v) => BitConverter.GetBytes(v).CopyTo(d, at);

    private static Func<byte[]?> ReadShared(string name) => () =>
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            var head = new byte[64];
            if (v.Read(head, 0, head.Length) < 32) return null;
            int total = name.StartsWith("MAHM", StringComparison.Ordinal)
                // MAHM header: signature, version, headerSize, numEntries, entrySize, time32, numGpuEntries (24), gpuEntrySize (28)
                ? (int)(U32(head, 8) + U32(head, 12) * U32(head, 16) + U32(head, 24) * U32(head, 28))
                : (int)(U32(head, 8) + U32(head, 12) * U32(head, 16));
            if (total <= 0 || total > 64 * 1024 * 1024) return null;
            var all = new byte[total];
            v.Position = 0;
            int read = 0;
            while (read < total) { int r = v.Read(all, read, total - read); if (r <= 0) break; read += r; }
            return read == total ? all : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception ex) { Log.Debug($"afterburner {name}: {ex.Message}"); return null; }
    };

    /// <summary>The real control memory, used exactly like the SDK sample.</summary>
    private sealed class SharedControlMemory : IControlMemory
    {
        private const string Name = "MACMSharedMemory", MutexName = @"Global\Access_MACMSharedMemory";
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);

        private static T WithMutex<T>(Func<T> f)
        {
            using var m = new Mutex(false, MutexName);
            bool owned = false;
            try { owned = m.WaitOne(3000); return f(); }
            catch (AbandonedMutexException) { owned = true; return f(); }
            finally { if (owned) m.ReleaseMutex(); }
        }

        public byte[]? Read() => WithMutex(() => ReadShared(Name)());

        public bool Write(byte[] data, bool flushCommand) => WithMutex(() =>
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.ReadWrite);
                using var v = mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.ReadWrite);
                if (v.ReadUInt32(HOffSignature) != SignatureMacm) return false;
                v.WriteArray(0, data, 0, data.Length);
                if (flushCommand) v.Write(HOffCommand, CommandFlush);
                return true;
            }
            catch (Exception ex) { Log.Warn("afterburner write: " + ex.Message); return false; }
        });

        public bool WaitForCompletion(int timeoutMs)
        {
            var hwnd = FindWindow(null, "MSI Afterburner ");
            if (hwnd != 0) PostMessage(hwnd, RegisterWindowMessage("MACMCmdNotification"), 0, 0);
            var end = Environment.TickCount64 + timeoutMs;
            do
            {
                var d = Read();
                if (d is null) return false;
                if (U32(d, HOffCommand) == 0) return true;
                Thread.Sleep(100);
            } while (Environment.TickCount64 < end);
            return false;
        }
    }
}
