using System.Globalization;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>Hardware Monitor preferences: refresh, history range, pins, logging, compact window and warning thresholds.</summary>
public sealed class MonitorSettings
{
    /// <summary>Sampling interval in milliseconds (500, 1000, 2000 or 5000).</summary>
    public int RefreshMs { get; set; } = 1000;
    /// <summary>Chart history window in minutes (1, 5, 15 or 60).</summary>
    public int RangeMinutes { get; set; } = 1;
    /// <summary>Metric ids pinned to the top of the monitor and shown in the compact window.</summary>
    public List<string> Pinned { get; set; } = ["cpu.load", "cpu.temp", "gpu0.load", "gpu0.temp", "mem.load"];
    /// <summary>Keep recording history while other pages are open (after the monitor was opened once this session).</summary>
    public bool RecordInBackground { get; set; } = true;
    /// <summary>Write every sample to a CSV file under the logs folder while on.</summary>
    public bool LogToFile { get; set; }
    /// <summary>The compact always-on-top window was open when Money Tweaks closed (reopened at start).</summary>
    public bool CompactOpen { get; set; }
    public bool CompactTopmost { get; set; } = true;
    public double? CompactLeft { get; set; }
    public double? CompactTop { get; set; }
    /// <summary>Detailed sections the user collapsed.</summary>
    public List<string> Collapsed { get; set; } = [];

    public bool AlertsEnabled { get; set; } = true;
    /// <summary>Also show a toast when a warning starts while another page is open.</summary>
    public bool AlertToasts { get; set; } = true;
    public double CpuTempWarnC { get; set; } = 90;
    public double GpuTempWarnC { get; set; } = 83;
    public double DiskTempWarnC { get; set; } = 65;
    public double RamWarnPercent { get; set; } = 90;
    public double CommitWarnPercent { get; set; } = 90;
    /// <summary>Sustained high usage: CPU load at or above this for <see cref="SustainedSeconds"/> while nothing else explains it.</summary>
    public double SustainedCpuPercent { get; set; } = 85;
    public double SustainedUploadMBps { get; set; } = 5;
    public int SustainedSeconds { get; set; } = 120;

    public static readonly int[] RefreshChoices = [500, 1000, 2000, 5000];
    public static readonly int[] RangeChoices = [1, 5, 15, 60];
}

public enum AlertLevel { Notice, Warning, Critical }

/// <summary>An active monitor warning. Key identifies the condition so a toast is shown once per occurrence.</summary>
public sealed record MonitorAlert(string Key, AlertLevel Level, string Title, string Detail, DateTime SinceUtc);

/// <summary>
/// Turns live readings into warnings: high temperatures, thermal throttling, memory pressure and sustained unusual usage.
/// Each condition must hold for a few seconds (or the configured sustained time) before it shows, and clears as soon as it
/// stops, so a single spike never raises a warning. Pure logic: fed by the sensor hub and by tests.
/// </summary>
public sealed class MonitorAlerts
{
    private readonly Dictionary<string, DateTime> _since = [];
    public const int ShortHoldSeconds = 5, MemoryHoldSeconds = 10;

    /// <summary>
    /// Evaluates one frame. <paramref name="flags"/> carries non-numeric facts from the sampler, e.g. "gpu0.reasons" =
    /// "thermal limit" or "cpu.top" = the busiest processes.
    /// </summary>
    public List<MonitorAlert> Evaluate(DateTime now, IReadOnlyDictionary<string, double?> v, IReadOnlyList<MetricInfo> metrics,
        IReadOnlyDictionary<string, string> flags, MonitorSettings s)
    {
        var list = new List<MonitorAlert>();
        if (!s.AlertsEnabled) { _since.Clear(); return list; }
        var seen = new HashSet<string>();
        var c = CultureInfo.CurrentCulture;

        void Hold(string key, bool on, int seconds, AlertLevel level, string title, Func<string> detail)
        {
            if (!on) return;
            seen.Add(key);
            if (!_since.TryGetValue(key, out var t)) _since[key] = t = now;
            if ((now - t).TotalSeconds >= seconds) list.Add(new MonitorAlert(key, level, title, detail(), t));
        }
        double? V(string id) => v.TryGetValue(id, out var x) ? x : null;
        string Name(string id) => metrics.FirstOrDefault(m => m.Id == id)?.Device is { Length: > 0 } d ? d : id;

        if (V("cpu.temp") is { } ct)
            Hold("cpu.temp", ct >= s.CpuTempWarnC, ShortHoldSeconds, ct >= s.CpuTempWarnC + 5 ? AlertLevel.Critical : AlertLevel.Warning,
                "CPU is running hot", () => $"{ct.ToString("0", c)} °C, above your {s.CpuTempWarnC.ToString("0", c)} °C warning. Check cooling, dust and fan curves.");
        if (V("cpu.limit") is { } lim)
            Hold("cpu.throttle", lim < 95, ShortHoldSeconds, AlertLevel.Warning, "CPU is being held below full speed",
                () => $"Windows reports a performance limit of {lim.ToString("0", c)} % (thermal or power limiting by the firmware).");

        foreach (var m in metrics.Where(m => m.Group == MetricGroup.Gpu && m.Id.EndsWith(".temp")))
            if (V(m.Id) is { } gt)
                Hold(m.Id, gt >= s.GpuTempWarnC, ShortHoldSeconds, gt >= s.GpuTempWarnC + 5 ? AlertLevel.Critical : AlertLevel.Warning,
                    "GPU is running hot", () => $"{m.Device}: {gt.ToString("0", c)} °C, above your {s.GpuTempWarnC.ToString("0", c)} °C warning.");
        foreach (var (key, reasons) in flags.Where(f => f.Key.EndsWith(".reasons")))
            Hold(key, reasons.Contains("thermal") || reasons.Contains("slowdown"), ShortHoldSeconds, AlertLevel.Warning, "GPU is thermal throttling",
                () => $"{Name(key.Replace(".reasons", ".temp"))}: the driver reports {reasons}, so clocks are being reduced.");
        foreach (var m in metrics.Where(m => m.Group == MetricGroup.Storage && m.Unit == Metric.Celsius))
            if (V(m.Id) is { } dt)
                Hold(m.Id, dt >= s.DiskTempWarnC, ShortHoldSeconds, AlertLevel.Warning, "Drive is running hot",
                    () => $"{m.Device}: {dt.ToString("0", c)} °C, above your {s.DiskTempWarnC.ToString("0", c)} °C warning. SSDs slow down when hot.");

        if (V("mem.load") is { } ram)
            Hold("mem.load", ram >= s.RamWarnPercent, MemoryHoldSeconds, ram >= 97 ? AlertLevel.Critical : AlertLevel.Warning, "Memory pressure",
                () => $"{ram.ToString("0", c)} % of RAM in use{(V("mem.avail") is { } a ? $", {a.ToString("0.0", c)} GB available" : "")}. Close heavy apps or browser tabs before gaming.");
        if (V("mem.commit") is { } com)
            Hold("mem.commit", com >= s.CommitWarnPercent, MemoryHoldSeconds, AlertLevel.Warning, "Commit charge is nearly full",
                () => $"{com.ToString("0", c)} % of RAM + page file is committed. Programs may fail to allocate memory; don't disable the page file.");

        if (V("cpu.load") is { } load)
            Hold("cpu.sustained", load >= s.SustainedCpuPercent, s.SustainedSeconds, AlertLevel.Notice, "Sustained high CPU usage",
                () => $"Above {s.SustainedCpuPercent.ToString("0", c)} % for {Minutes(s.SustainedSeconds)}." + (flags.TryGetValue("cpu.top", out var top) ? $" Busiest: {top}." : ""));
        if (V("net.up") is { } up)
            Hold("net.upload", up >= s.SustainedUploadMBps * 1024, s.SustainedSeconds, AlertLevel.Notice, "Sustained upload traffic",
                () => $"Uploading more than {s.SustainedUploadMBps.ToString("0.#", c)} MB/s for {Minutes(s.SustainedSeconds)}. Expected for streams, backups and cloud sync; otherwise check what is sending.");
        if (V("disk.active") is { } disk)
            Hold("disk.sustained", disk >= 95, s.SustainedSeconds, AlertLevel.Notice, "Drive busy all the time",
                () => $"A drive has been near 100 % active for {Minutes(s.SustainedSeconds)} (updates, indexing, antivirus scans or a game install can cause it).");

        foreach (var k in _since.Keys.Where(k => !seen.Contains(k)).ToList()) _since.Remove(k);
        return list;
    }

    /// <summary>True while a sustained-CPU condition is building (the sampler then looks up the busiest processes).</summary>
    public bool Building(string key) => _since.ContainsKey(key);

    private static string Minutes(int seconds) => seconds >= 120 ? $"{seconds / 60} minutes" : $"{seconds} seconds";
}
