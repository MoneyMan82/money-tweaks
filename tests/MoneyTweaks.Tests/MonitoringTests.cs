using System.Globalization;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks.Tests;

/// <summary>Hardware Monitor core: history ring, statistics, warnings, formatting, CSV, and a live sample on this machine.</summary>
[Collection("registry")]
public class MonitoringTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static Dictionary<string, double?> V(params (string id, double? v)[] xs) => xs.ToDictionary(x => x.id, x => x.v);

    [Fact]
    public void History_ranges_and_stats_follow_the_window_and_skip_gaps()
    {
        var h = new MetricHistory(100);
        for (int i = 0; i < 10; i++) h.Add(T0.AddSeconds(i), V(("cpu.load", i == 5 ? null : i * 10.0)));
        Assert.Equal(10, h.Count);
        var last5 = h.Range("cpu.load", T0.AddSeconds(5));
        Assert.Equal(5, last5.Count);
        Assert.True(float.IsNaN(last5[0].value));        // the gap stays a gap
        var st = h.Stats("cpu.load", T0.AddSeconds(5));
        Assert.Equal(4, st.Samples);
        Assert.Equal(60, st.Min);
        Assert.Equal(90, st.Max);
        Assert.Equal(75, st.Average);
        Assert.Equal(90, st.Current);
        Assert.Equal(0, h.Stats("missing", T0).Samples);
    }

    [Fact]
    public void History_is_a_fixed_size_ring()
    {
        var h = new MetricHistory(8);
        for (int i = 0; i < 20; i++) h.Add(T0.AddSeconds(i), V(("x", i)));
        Assert.Equal(8, h.Count);
        var all = h.Range("x", DateTime.MinValue);
        Assert.Equal(Enumerable.Range(12, 8).Select(i => (float)i), all.Select(p => p.value));
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.ticks < p.Second.ticks));
    }

    [Fact]
    public void A_metric_that_appears_later_has_no_invented_past()
    {
        var h = new MetricHistory(10);
        h.Add(T0, V(("a", 1)));
        h.Add(T0.AddSeconds(1), V(("a", 2), ("b", 5)));
        var b = h.Range("b", DateTime.MinValue);
        Assert.True(float.IsNaN(b[0].value));
        Assert.Equal(5, b[1].value);
        var (ids, rows) = h.Snapshot();
        Assert.Equal(["a", "b"], ids.Order());
        Assert.Null(rows[0].values[ids.IndexOf("b")]);
    }

    private static readonly IReadOnlyList<MetricInfo> Metrics =
    [
        new("cpu.temp", MetricGroup.Cpu, "", "Temperature", Metric.Celsius, "test"),
        new("gpu0.temp", MetricGroup.Gpu, "Test GPU", "Temperature", Metric.Celsius, "test"),
        new("disk0.temp", MetricGroup.Storage, "Test SSD", "Temperature", Metric.Celsius, "test"),
    ];

    [Fact]
    public void Warnings_need_the_condition_to_last_and_clear_when_it_stops()
    {
        var a = new MonitorAlerts();
        var s = new MonitorSettings { CpuTempWarnC = 90 };
        var none = new Dictionary<string, string>();
        Assert.Empty(a.Evaluate(T0, V(("cpu.temp", 95)), Metrics, none, s));                 // a spike alone: nothing yet
        Assert.Empty(a.Evaluate(T0.AddSeconds(3), V(("cpu.temp", 95)), Metrics, none, s));
        var on = a.Evaluate(T0.AddSeconds(5), V(("cpu.temp", 96)), Metrics, none, s);
        var w = Assert.Single(on);
        Assert.Equal("cpu.temp", w.Key);
        Assert.Equal(AlertLevel.Critical, w.Level);                                           // 5 °C over the limit
        Assert.Equal(T0, w.SinceUtc);
        Assert.Empty(a.Evaluate(T0.AddSeconds(6), V(("cpu.temp", 70)), Metrics, none, s));   // cooled down: gone
        Assert.Empty(a.Evaluate(T0.AddSeconds(7), V(("cpu.temp", 95)), Metrics, none, s));   // and the hold restarts
    }

    [Fact]
    public void Warnings_cover_gpu_throttling_drives_memory_and_sustained_usage()
    {
        var a = new MonitorAlerts();
        var s = new MonitorSettings { SustainedSeconds = 30, SustainedCpuPercent = 85, DiskTempWarnC = 65, RamWarnPercent = 90 };
        var flags = new Dictionary<string, string> { ["gpu0.reasons"] = "thermal limit", ["cpu.top"] = "game.exe 70 %" };
        var vals = V(("gpu0.temp", 70), ("disk0.temp", 70), ("mem.load", 95), ("mem.avail", 1.5), ("cpu.load", 99), ("cpu.limit", 80), ("net.up", 100));
        a.Evaluate(T0, vals, Metrics, flags, s);
        var at10 = a.Evaluate(T0.AddSeconds(10), vals, Metrics, flags, s).Select(x => x.Key).ToHashSet();
        Assert.Contains("gpu0.reasons", at10);
        Assert.Contains("disk0.temp", at10);
        Assert.Contains("mem.load", at10);
        Assert.Contains("cpu.throttle", at10);
        Assert.DoesNotContain("gpu0.temp", at10);          // 70 °C is below the GPU limit
        Assert.DoesNotContain("cpu.sustained", at10);      // not 30 s yet
        Assert.DoesNotContain("net.upload", at10);         // 100 KB/s is far below 5 MB/s
        var at30 = a.Evaluate(T0.AddSeconds(30), vals, Metrics, flags, s);
        var cpu = Assert.Single(at30, x => x.Key == "cpu.sustained");
        Assert.Contains("game.exe", cpu.Detail);
        Assert.Empty(a.Evaluate(T0.AddSeconds(31), vals, Metrics, flags, new MonitorSettings { AlertsEnabled = false }));
    }

    [Fact]
    public void Third_party_notices_cover_every_bundled_library()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "THIRD-PARTY-NOTICES.md"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!, "THIRD-PARTY-NOTICES.md"));
        foreach (var name in new[] { "LibreHardwareMonitorLib 0.9.6", "DiskInfoToolkit", "RAMSPDToolkit", "HidSharp", "Mozilla Public License Version 2.0", "Apache License", "github.com/LibreHardwareMonitor/LibreHardwareMonitor" })
            Assert.Contains(name, text);
        // The version in the notices must match the referenced package.
        var csproj = File.ReadAllText(Path.Combine(dir!, "src", "MoneyTweaks.Core", "MoneyTweaks.Core.csproj"));
        Assert.Contains("\"LibreHardwareMonitorLib\" Version=\"0.9.6\"", csproj);
    }

    [Fact]
    public void Gpu_clock_limit_reasons_are_named()
    {
        Assert.Empty(Nvml.Reasons(Nvml.ReasonIdle));
        Assert.Equal(["power limit"], Nvml.Reasons(Nvml.ReasonSwPowerCap));
        Assert.Equal(["thermal limit", "power limit"], Nvml.Reasons(Nvml.ReasonHwThermal | Nvml.ReasonSwPowerCap));
        Assert.True(Nvml.IsThermal(Nvml.ReasonSwThermal));
        Assert.False(Nvml.IsThermal(Nvml.ReasonSwPowerCap));
    }

    [Fact]
    public void Values_are_formatted_with_units_and_missing_values_as_a_dash()
    {
        var c = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("—", Metric.Format(null, Metric.Percent));
            Assert.Equal("—", Metric.Format(double.NaN, Metric.Celsius));
            Assert.Equal("42 %", Metric.Format(42.4, Metric.Percent));
            Assert.Equal("4.83 GHz", Metric.Format(4830, Metric.Mhz));
            Assert.Equal("765 MHz", Metric.Format(765, Metric.Mhz));
            Assert.Equal("1.5 MB/s", Metric.Format(1536, Metric.KBps));
            Assert.Equal("2.22 GB", Metric.Format(2270, Metric.Mb));
            Assert.Equal("55 °C", Metric.Format(55.2, Metric.Celsius));
        }
        finally { CultureInfo.CurrentCulture = c; }
    }

    [Fact]
    public void Csv_rows_use_invariant_numbers_and_quote_names_with_commas()
    {
        var head = SensorHub.Header([new MetricInfo("x", MetricGroup.Cpu, "Dev, Inc", "Usage", Metric.Percent, "t")]);
        Assert.Equal("Time,\"Dev, Inc · Usage (%)\"", head);
        var row = SensorHub.Row(T0, [1.23456, null]);
        Assert.EndsWith(",1.235,", row);
        Assert.Equal(SensorHub.Short("abc"), SensorHub.Short("abc"));
        Assert.NotEqual(SensorHub.Short("abc"), SensorHub.Short("abd"));
    }

    /// <summary>Real sensors: every value is finite and in range, and every missing reading says why.</summary>
    [Fact]
    public void Live_sample_reads_real_counters_and_explains_what_is_missing()
    {
        using var hub = new SensorHub();
        SensorFrame? first = null;
        int frames = 0;
        hub.Sampled += f => { if (++frames == 2) first = f; };
        var owner = new object();
        hub.Acquire(owner);
        var until = DateTime.UtcNow.AddSeconds(40);
        while (first is null && DateTime.UtcNow < until) Thread.Sleep(100);
        hub.Release(owner);
        Assert.NotNull(first);
        Assert.InRange(first!.Values["mem.load"]!.Value, 1, 100);
        Assert.True(first.Values.ContainsKey("net.down"));
        foreach (var (id, v) in first.Values)
            if (v is { } x) Assert.True(double.IsFinite(x) && x >= 0, $"{id} = {x}");
        foreach (var m in hub.Metrics.Where(m => m.Max is not null && m.Unit == Metric.Percent))
            if (first.Values.GetValueOrDefault(m.Id) is { } p) Assert.InRange(p, 0, 100);
        Assert.All(hub.Unsupported, u => Assert.False(string.IsNullOrWhiteSpace(u.Reason)));
        // No placeholder zeros: a temperature, clock, power or voltage reading is always above zero.
        foreach (var m in hub.Metrics.Where(m => m.Unit is Metric.Celsius or Metric.Mhz or Metric.Watts or Metric.Volts))
            if (first.Values.GetValueOrDefault(m.Id) is { } x) Assert.True(x > 0, $"{m.Id} ({m.Source}) = {x}");
        // Without LibreHardwareMonitor's driver path, none of its CPU or motherboard readings may appear.
        if (!LhmSource.DriverActive)
            Assert.DoesNotContain(hub.Metrics, m => m.Source.Contains("PawnIO"));
        Assert.True(hub.History.Count >= 2);
    }
}
