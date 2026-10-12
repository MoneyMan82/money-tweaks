using System.Diagnostics;

namespace MoneyTweaks.Core.Sys;

/// <summary>Lightweight timing: logs "[perf]" lines for operations slower than a threshold. Zero allocations when fast.</summary>
public static class Perf
{
    public static Scope Measure(string name, int thresholdMs = 30) => new(name, thresholdMs);

    public readonly struct Scope(string name, int thresholdMs) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= thresholdMs) Log.Info($"[perf] {name}: {ms:0} ms");
        }
    }
}
