using System.Globalization;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>Hardware group a metric belongs to (the monitor's sections).</summary>
public enum MetricGroup { Cpu, Gpu, Memory, Storage, Network, Fans }

/// <summary>
/// One live sensor the hub found on this PC. Ids are stable across runs ("cpu.load", "cpu.core3.clock", "gpu0.temp",
/// "disk1.read", "net.down") so pins, logs and warnings survive restarts. Max is the natural scale (100 for percentages),
/// or null when the chart should scale to the data.
/// </summary>
public sealed record MetricInfo(string Id, MetricGroup Group, string Device, string Label, string Unit, string Source, double? Max = null)
{
    public string Format(double? v) => Metric.Format(v, Unit);
    public string FullName => string.IsNullOrEmpty(Device) ? Label : $"{Device} · {Label}";

    /// <summary>Name that stands on its own without its section: "CPU usage", "GPU temperature", "RAM in use", "Ethernet · Download".</summary>
    public string ShortName => Group switch
    {
        MetricGroup.Cpu when Device.Length == 0 => "CPU " + Label.ToLowerInvariant(),
        MetricGroup.Gpu => "GPU " + Label.ToLowerInvariant(),
        MetricGroup.Memory => "RAM " + Label.ToLowerInvariant(),
        MetricGroup.Storage when Device.Length == 0 => "Drive " + Label.ToLowerInvariant(),
        _ => FullName,
    };
}

/// <summary>A reading Money Tweaks looked for but this PC can't provide, and why. Shown instead of a made-up value.</summary>
public sealed record UnsupportedReading(MetricGroup Group, string Device, string Label, string Reason);

public static class Metric
{
    public const string Percent = "%", Celsius = "°C", Mhz = "MHz", Watts = "W", Gb = "GB", Mb = "MB", MBps = "MB/s", KBps = "KB/s", Rpm = "RPM", PerSec = "/s", Volts = "V";

    public static string Format(double? v, string unit)
    {
        if (v is not { } x || double.IsNaN(x)) return "—";
        var c = CultureInfo.CurrentCulture;
        return unit switch
        {
            Percent => x.ToString(x < 10 ? "0.0" : "0", c) + " %",
            Celsius => x.ToString("0", c) + " °C",
            Mhz => x >= 1000 ? (x / 1000).ToString("0.00", c) + " GHz" : x.ToString("0", c) + " MHz",
            Watts => x.ToString(x < 10 ? "0.0" : "0", c) + " W",
            Gb => x.ToString(x < 10 ? "0.00" : "0.0", c) + " GB",
            Mb => x >= 1024 ? (x / 1024).ToString("0.00", c) + " GB" : x.ToString("0", c) + " MB",
            MBps => x >= 1000 ? (x / 1024).ToString("0.00", c) + " GB/s" : x.ToString(x < 10 ? "0.00" : "0.0", c) + " MB/s",
            KBps => x >= 1024 ? (x / 1024).ToString("0.0", c) + " MB/s" : x.ToString("0", c) + " KB/s",
            Rpm => x.ToString("0", c) + " RPM",
            PerSec => x.ToString("0", c) + " /s",
            Volts => x.ToString("0.000", c) + " V",
            _ => x.ToString("0.##", c) + (unit.Length > 0 ? " " + unit : ""),
        };
    }
}

/// <summary>Current, minimum, maximum and average of one metric over a time window (null when no samples).</summary>
public readonly record struct MetricStats(double? Current, double? Min, double? Max, double? Average, int Samples);

/// <summary>
/// Fixed-memory sensor history: one shared ring of sample times and one float ring per metric (NaN = no reading at that
/// sample). Sized for one hour at the fastest refresh rate; old samples are overwritten, nothing grows.
/// Thread-safe: the sampler writes, the UI reads.
/// </summary>
public sealed class MetricHistory
{
    private readonly long[] _times;
    private readonly Dictionary<string, float[]> _series = [];
    private int _head = -1, _count;
    private readonly object _gate = new();
    public int Capacity { get; }

    public MetricHistory(int capacity = 7200)
    {
        Capacity = capacity;
        _times = new long[capacity];
    }

    public int Count { get { lock (_gate) return _count; } }

    public void Add(DateTime utc, IReadOnlyDictionary<string, double?> values)
    {
        lock (_gate)
        {
            _head = (_head + 1) % Capacity;
            _times[_head] = utc.Ticks;
            if (_count < Capacity) _count++;
            foreach (var s in _series.Values) s[_head] = float.NaN;
            foreach (var (id, v) in values)
            {
                if (!_series.TryGetValue(id, out var arr)) { arr = new float[Capacity]; Array.Fill(arr, float.NaN); _series[id] = arr; }
                arr[_head] = v is { } x && !double.IsNaN(x) ? (float)x : float.NaN;
            }
        }
    }

    public void Clear() { lock (_gate) { _head = -1; _count = 0; _series.Clear(); } }

    /// <summary>Samples of one metric from <paramref name="sinceUtc"/> on, oldest first (times in UTC ticks; NaN = gap).</summary>
    public List<(long ticks, float value)> Range(string id, DateTime sinceUtc)
    {
        var list = new List<(long, float)>();
        lock (_gate)
        {
            if (_count == 0) return list;
            _series.TryGetValue(id, out var arr);
            long since = sinceUtc.Ticks;
            int start = FirstIndexSince(since, out int n);
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % Capacity;
                list.Add((_times[i], arr?[i] ?? float.NaN));
            }
        }
        return list;
    }

    public MetricStats Stats(string id, DateTime sinceUtc)
    {
        lock (_gate)
        {
            if (_count == 0 || !_series.TryGetValue(id, out var arr)) return default;
            int start = FirstIndexSince(sinceUtc.Ticks, out int n);
            double min = double.MaxValue, max = double.MinValue, sum = 0; int c = 0; double? cur = null;
            for (int k = 0; k < n; k++)
            {
                float v = arr[(start + k) % Capacity];
                if (float.IsNaN(v)) continue;
                min = Math.Min(min, v); max = Math.Max(max, v); sum += v; c++;
            }
            float last = arr[_head];
            if (!float.IsNaN(last)) cur = last;
            return c == 0 ? new MetricStats(cur, null, null, null, 0) : new MetricStats(cur, min, max, sum / c, c);
        }
    }

    /// <summary>Every sample as rows (UTC time + one value per id, null for gaps), oldest first. For CSV export.</summary>
    public (List<string> ids, List<(DateTime utc, double?[] values)> rows) Snapshot(IEnumerable<string>? onlyIds = null)
    {
        lock (_gate)
        {
            var ids = (onlyIds ?? _series.Keys).Where(_series.ContainsKey).ToList();
            var rows = new List<(DateTime, double?[])>(_count);
            int start = (_head - _count + 1 + Capacity) % Capacity;
            for (int k = 0; k < _count; k++)
            {
                int i = (start + k) % Capacity;
                var vals = new double?[ids.Count];
                for (int j = 0; j < ids.Count; j++) { float v = _series[ids[j]][i]; vals[j] = float.IsNaN(v) ? null : Math.Round(v, 3); }
                rows.Add((new DateTime(_times[i], DateTimeKind.Utc), vals));
            }
            return (ids, rows);
        }
    }

    // Oldest-first ring index of the first sample at or after `since`, and how many samples follow it.
    private int FirstIndexSince(long since, out int n)
    {
        int oldest = (_head - _count + 1 + Capacity) % Capacity;
        // Binary search over the logical (chronological) order.
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_times[(oldest + mid) % Capacity] < since) lo = mid + 1; else hi = mid;
        }
        n = _count - lo;
        return (oldest + lo) % Capacity;
    }
}
