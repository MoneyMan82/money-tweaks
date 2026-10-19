using System.Diagnostics;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Cleaner;

/// <summary>Standby (cached) and free memory from Windows' Memory performance counters, plus available RAM.</summary>
public sealed record StandbySnapshot(long StandbyBytes, long FreeBytes, long AvailableBytes, long TotalBytes);

public sealed record StandbyClearResult(bool Ok, string Message, StandbySnapshot? Before, StandbySnapshot? After);

/// <summary>
/// Clearing the standby list (like RAMMap's "Empty Standby List"), measured. Standby memory is a cache of recently used
/// files: Windows hands it to any app that needs RAM instantly, so it already counts as available. Clearing it turns
/// cache into free memory; it can help on PCs where a game hitches while Windows repurposes cache, and costs slower
/// loading of anything that was cached. Uses the existing <see cref="MemoryInfo.PurgeStandbyList"/> (administrator).
/// </summary>
public static class StandbyMemory
{
    private static readonly string[] StandbyCounters = ["Standby Cache Core Bytes", "Standby Cache Normal Priority Bytes", "Standby Cache Reserve Bytes"];

    public static StandbySnapshot? Snapshot()
    {
        try
        {
            long standby = 0;
            foreach (var c in StandbyCounters)
            {
                using var pc = new PerformanceCounter("Memory", c, readOnly: true);
                standby += (long)pc.NextValue();
            }
            using var free = new PerformanceCounter("Memory", "Free & Zero Page List Bytes", readOnly: true);
            var (total, available, _) = MemoryInfo.Status();
            return new(standby, (long)free.NextValue(), (long)available, (long)total);
        }
        catch (Exception ex) { Log.Debug("standby counters: " + ex.Message); return null; }
    }

    public static StandbyClearResult Clear()
    {
        var before = Snapshot();
        var (ok, msg) = MemoryInfo.PurgeStandbyList();
        Thread.Sleep(500);
        var after = Snapshot();
        Log.Info($"Standby clear: {ok} {msg} standby {before?.StandbyBytes} → {after?.StandbyBytes}");
        return new(ok, msg, before, after);
    }

    public static string Describe(StandbyClearResult r) =>
        !r.Ok ? "Standby memory not cleared: " + r.Message
        : r.Before is { } b && r.After is { } a
            ? $"Standby (cached) memory {StorageCleaner.Size(b.StandbyBytes)} → {StorageCleaner.Size(a.StandbyBytes)}, free memory {StorageCleaner.Size(b.FreeBytes)} → {StorageCleaner.Size(a.FreeBytes)} (read from Windows)."
            : "Standby list cleared (Windows didn't report the sizes).";
}
