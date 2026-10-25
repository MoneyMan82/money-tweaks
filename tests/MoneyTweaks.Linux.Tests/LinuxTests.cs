namespace MoneyTweaks.Linux.Tests;

/// <summary>A temporary folder laid out like /proc and /sys, with the kernel's real file formats.</summary>
public sealed class FakeSys : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mt-linux-" + Guid.NewGuid().ToString("N"));
    public LinuxFs Fs { get; }
    public FakeSys() { Directory.CreateDirectory(Root); Fs = new LinuxFs(Root); }
    public void Put(string abs, string content) { var p = Fs.Map(abs); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content); }
    public string Get(string abs) => File.ReadAllText(Fs.Map(abs)).Trim();
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

/// <summary>Writes like the kernel would; paths in <see cref="Refuse"/> silently keep their old value (as some drivers do).</summary>
public sealed class FakeWriter(FakeSys sys) : IPrivilegedWriter
{
    public HashSet<string> Refuse { get; } = [];
    public List<Dictionary<string, string>> Calls { get; } = [];
    public void Write(IReadOnlyDictionary<string, string> values)
    {
        PkexecWriter.Validate(values);   // the same whitelist the real writer enforces
        Calls.Add(values.ToDictionary());
        foreach (var (p, v) in values) if (!Refuse.Contains(p)) sys.Put(p, v + "\n");
    }
}

public class ParserTests
{
    [Fact]
    public void Cpu_usage_comes_from_proc_stat_deltas()
    {
        var a = Proc.CpuTimes("cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 50 0 50 400 0 0 0 0 0 0\ncpu1 50 0 50 400 0 0 0 0 0 0\nintr 1 2 3\n");
        var b = Proc.CpuTimes("cpu  200 0 200 1000 0 0 0 0 0 0\ncpu0 150 0 50 500 0 0 0 0 0 0\ncpu1 50 0 150 500 0 0 0 0 0 0\n");
        Assert.Equal(3, a.Count);
        Assert.Equal(50, Proc.Percent(a[0], b[0]), 3);   // 200 busy of 400 jiffies
        Assert.Equal(50, Proc.Percent(a[1], b[1]), 3);
    }

    [Fact]
    public void Iowait_counts_as_idle()
    {
        var a = Proc.CpuTimes("cpu  0 0 0 0 0 0 0 0\n");
        var b = Proc.CpuTimes("cpu  10 0 0 40 50 0 0 0\n");
        Assert.Equal(10, Proc.Percent(a[0], b[0]), 3);
    }

    [Fact]
    public void Memory_and_network_parse_the_kernel_formats()
    {
        Assert.Equal((32768000L, 20000000L), Proc.Memory("MemTotal:       32768000 kB\nMemFree:         1000000 kB\nMemAvailable:   20000000 kB\n"));
        const string netdev = "Inter-|   Receive                                                |  Transmit\n face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n    lo: 999999    10    0    0    0     0          0         0   999999    10    0    0    0     0       0          0\n  eth0: 1000    5    0    0    0     0          0         0   2000    6    0    0    0     0       0          0\nwlan0: 500    5    0    0    0     0          0         0   100    6    0    0    0     0       0          0\n";
        Assert.Equal((1500L, 2100L), Proc.NetBytes(netdev));
    }

    [Fact]
    public void Nvidia_smi_csv_and_uptime()
    {
        Assert.Equal((37.0, 54.0), Proc.NvidiaSmi("37, 54\n"));
        Assert.Null(Proc.NvidiaSmi("[N/A], [N/A]\n"));
        Assert.Equal(TimeSpan.FromSeconds(3725.5), Proc.Uptime("3725.50 7000.00\n"));
    }

    [Fact]
    public void Cpu_temperature_prefers_tctl_and_skips_dead_sensors()
    {
        using var s = new FakeSys();
        s.Put("/sys/class/hwmon/hwmon0/name", "nvme\n"); s.Put("/sys/class/hwmon/hwmon0/temp1_input", "38850\n"); s.Put("/sys/class/hwmon/hwmon0/temp1_label", "Composite\n");
        s.Put("/sys/class/hwmon/hwmon2/name", "k10temp\n");
        s.Put("/sys/class/hwmon/hwmon2/temp1_input", "52125\n"); s.Put("/sys/class/hwmon/hwmon2/temp1_label", "Tctl\n");
        s.Put("/sys/class/hwmon/hwmon2/temp3_input", "0\n"); s.Put("/sys/class/hwmon/hwmon2/temp3_label", "Tccd1\n");
        var temps = Proc.HwmonTemps(s.Fs);
        Assert.Equal(2, temps.Count);   // the 0 °C CCD sensor isn't connected
        var cpu = Proc.CpuTemp(temps, s.Fs)!;
        Assert.Equal(52.125, cpu.Celsius, 3);
        Assert.Equal("Tctl", cpu.Label);
    }

    [Fact]
    public void Intel_package_and_thermal_zone_fallback()
    {
        using var s = new FakeSys();
        s.Put("/sys/class/hwmon/hwmon1/name", "coretemp\n");
        s.Put("/sys/class/hwmon/hwmon1/temp1_input", "61000\n"); s.Put("/sys/class/hwmon/hwmon1/temp1_label", "Package id 0\n");
        Assert.Equal(61, Proc.CpuTemp(Proc.HwmonTemps(s.Fs), s.Fs)!.Celsius);

        using var z = new FakeSys();
        z.Put("/sys/class/thermal/thermal_zone0/type", "acpitz\n"); z.Put("/sys/class/thermal/thermal_zone0/temp", "27800\n");
        z.Put("/sys/class/thermal/thermal_zone1/type", "x86_pkg_temp\n"); z.Put("/sys/class/thermal/thermal_zone1/temp", "45000\n");
        Assert.Equal(45, Proc.CpuTemp(Proc.HwmonTemps(z.Fs), z.Fs)!.Celsius);

        using var none = new FakeSys();
        Assert.Null(Proc.CpuTemp(Proc.HwmonTemps(none.Fs), none.Fs));
    }

    [Fact]
    public void Sampler_reads_a_whole_system()
    {
        using var s = new FakeSys();
        s.Put("/proc/stat", "cpu  100 0 100 800 0 0 0 0\ncpu0 100 0 100 800 0 0 0 0\n");
        s.Put("/proc/meminfo", "MemTotal: 16000000 kB\nMemAvailable: 8000000 kB\n");
        s.Put("/proc/net/dev", "h\nh\n  eth0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
        s.Put("/proc/uptime", "100.0 50.0\n");
        s.Put("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "4500000\n");
        s.Put("/sys/class/drm/card1/device/gpu_busy_percent", "12\n");
        var sampler = new Sampler(s.Fs);
        Assert.Null(sampler.Sample().CpuPercent);   // first sample has nothing to compare with
        s.Put("/proc/stat", "cpu  200 0 100 900 0 0 0 0\ncpu0 200 0 100 900 0 0 0 0\n");
        var snap = sampler.Sample();
        Assert.Equal(50, snap.CpuPercent!.Value, 3);
        Assert.Single(snap.CorePercent);
        Assert.Equal(4500, snap.CpuMhz);
        Assert.Equal(50, snap.MemUsedPercent, 3);
        Assert.Equal(12, snap.GpuPercent);
        Assert.Null(snap.CpuTempC);
        Assert.Null(snap.CpuTempSource);
    }

    [Fact]
    public void Missing_proc_files_are_reported_as_unreadable_not_as_zero()
    {
        using var s = new FakeSys();
        var snap = new Sampler(s.Fs).Sample();
        Assert.False(snap.CpuReadable);
        Assert.False(snap.NetReadable);
        Assert.False(snap.MemReadable);
        Assert.Null(snap.Uptime);
    }
}

public class TweakEngineTests : IDisposable
{
    private readonly FakeSys _s = new();
    private readonly FakeWriter _w;
    private readonly LinuxTweakEngine _e;
    private readonly string _backups;
    private static LinuxTweak Tw(string id) => LinuxCatalog.All.First(t => t.Id == id);
    private const string Gov0 = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor", Gov1 = "/sys/devices/system/cpu/cpu1/cpufreq/scaling_governor";

    public TweakEngineTests()
    {
        _w = new FakeWriter(_s);
        _backups = Path.Combine(_s.Root, "state", "backups");
        _e = new LinuxTweakEngine(_s.Fs, _w, _backups);
        _s.Put("/proc/sys/kernel/random/boot_id", "boot-1\n");
        foreach (var c in new[] { 0, 1 })
        {
            _s.Put($"/sys/devices/system/cpu/cpu{c}/cpufreq/scaling_governor", "powersave\n");
            _s.Put($"/sys/devices/system/cpu/cpu{c}/cpufreq/scaling_available_governors", "performance powersave\n");
        }
        _s.Put("/proc/sys/vm/swappiness", "60\n");
        _s.Put("/proc/sys/vm/max_map_count", "65530\n");
    }

    public void Dispose() => _s.Dispose();

    [Fact]
    public void Apply_backs_up_writes_verifies_and_undo_restores()
    {
        var t = Tw("cpu.governor");
        Assert.Equal(LinuxTweakState.NotApplied, _e.State(t).state);
        var preview = _e.Inspect(t);
        Assert.Equal([Gov0, Gov1], preview.Select(c => c.Path));
        Assert.All(preview, c => { Assert.Equal("powersave", c.Current); Assert.Equal("performance", c.Proposed); Assert.False(c.AlreadySet); });
        Assert.Empty(_w.Calls);   // inspecting writes nothing

        var r = _e.Apply(t);
        Assert.True(r.Success, r.Message);
        Assert.Equal("performance", _s.Get(Gov0));
        Assert.Equal(LinuxTweakState.Applied, _e.State(t).state);
        Assert.Equal("powersave", _e.Backup(t)!.Values[Gov1]);
        Assert.StartsWith("back to powersave", _e.Inspect(t)[0].Undo);

        Assert.True(_e.Undo(t).Success);
        Assert.Equal("powersave", _s.Get(Gov0));
        Assert.Equal("powersave", _s.Get(Gov1));
        Assert.Null(_e.Backup(t));
    }

    [Fact]
    public void Only_files_that_differ_are_written_and_the_first_backup_is_kept()
    {
        _s.Put(Gov1, "performance\n");
        var t = Tw("cpu.governor");
        Assert.Equal(LinuxTweakState.Partial, _e.State(t).state);
        Assert.True(_e.Apply(t).Success);
        Assert.Equal([Gov0], _w.Calls.Single().Keys);
        Assert.True(_e.Undo(t).Success);
        Assert.Equal("powersave", _s.Get(Gov0));
        Assert.Equal("performance", _s.Get(Gov1));   // it was performance before Money Tweaks
    }

    [Fact]
    public void A_value_the_kernel_doesnt_keep_is_rolled_back()
    {
        var t = Tw("cpu.governor");
        _w.Refuse.Add(Gov1);
        var r = _e.Apply(t);
        Assert.False(r.Success);
        Assert.Contains("rolled back", r.Message);
        Assert.Equal("powersave", _s.Get(Gov0));   // restored
        Assert.Null(_e.Backup(t));
    }

    [Fact]
    public void Unsupported_and_missing_settings_are_not_available()
    {
        _s.Put("/sys/devices/system/cpu/cpu0/cpufreq/scaling_available_governors", "schedutil\n");
        Assert.Equal(LinuxTweakState.NotAvailable, _e.State(Tw("cpu.governor")).state);
        Assert.Equal(LinuxTweakState.NotAvailable, _e.State(Tw("cpu.epp")).state);   // no EPP files on this "PC"
        Assert.False(_e.Apply(Tw("cpu.epp")).Success);
        Assert.Empty(_w.Calls);
    }

    [Fact]
    public void Limits_already_higher_count_as_applied()
    {
        _s.Put("/proc/sys/vm/max_map_count", "2147483642\n");   // SteamOS value
        var t = Tw("vm.max_map_count");
        Assert.Equal(LinuxTweakState.Applied, _e.State(t).state);
        Assert.Equal("Already set; nothing was changed.", _e.Apply(t).Message);
        Assert.Empty(_w.Calls);
    }

    [Fact]
    public void Backups_from_an_earlier_boot_are_dropped()
    {
        var t = Tw("vm.swappiness");
        Assert.True(_e.Apply(t).Success);
        Assert.NotNull(_e.Backup(t));
        _s.Put("/proc/sys/kernel/random/boot_id", "boot-2\n");
        _s.Put("/proc/sys/vm/swappiness", "60\n");   // the restart reset it
        Assert.Null(_e.Backup(t));
        Assert.False(_e.Undo(t).Success);
    }

    [Theory]
    [InlineData("/etc/passwd", "performance")]
    [InlineData("/proc/sys/vm/swappiness/../../kernel/x", "1")]
    [InlineData("/proc/sys/vm/swappiness", "10; rm -rf /")]
    [InlineData("/proc/sys/vm/swappiness", "10\nperformance")]
    [InlineData("/sys/devices/system/cpu/cpu0/cpufreq/scaling_max_freq", "1")]
    public void The_root_writer_refuses_anything_outside_its_whitelist(string path, string value) =>
        Assert.Throws<InvalidOperationException>(() => PkexecWriter.Validate(new Dictionary<string, string> { [path] = value }));

    [Fact]
    public void Feedback_report_and_link_hold_no_personal_details()
    {
        _s.Put("/etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n");
        _s.Put("/proc/sys/kernel/osrelease", "6.8.0-45-generic\n");
        _s.Put("/proc/cpuinfo", "processor\t: 0\nmodel name\t: AMD Ryzen 5 7600X 6-Core Processor\n");
        var report = LinuxDiagnostics.Report(_s.Fs, "1.68.0", _e);
        Assert.Contains("Ubuntu 24.04.1 LTS", report);
        Assert.Contains("AMD Ryzen 5 7600X", report);
        Assert.Contains("Tweak cpu.governor: NotApplied", report);
        foreach (var personal in new[] { Environment.UserName, Environment.MachineName }.Where(n => n.Length >= 3))
            Assert.DoesNotContain(personal, report);
        var url = LinuxDiagnostics.IssueUrl("Temp shows 0", report);
        Assert.StartsWith("https://github.com/MoneyMan82/money-tweaks/issues/new?title=%5BLinux%5D%20Temp%20shows%200&body=", url);
    }
}

/// <summary>Runs only on Linux (the CI's Ubuntu job): the parsers against a real kernel's /proc and /sys.</summary>
public class RealKernelTests
{
    [Fact]
    public void Reads_a_real_linux_system()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fs = new LinuxFs();
        var sampler = new Sampler(fs);
        sampler.Sample();
        Thread.Sleep(500);
        var s = sampler.Sample();
        Assert.True(s.CpuReadable && s.MemReadable && s.NetReadable);
        Assert.Equal(Environment.ProcessorCount, s.CorePercent.Count);
        Assert.InRange(s.CpuPercent!.Value, 0, 100);
        Assert.All(s.CorePercent, c => Assert.InRange(c, 0, 100));
        Assert.InRange(s.MemUsedPercent, 0.1, 100);
        Assert.True(s.Uptime > TimeSpan.Zero);
        if (s.CpuTempC is { } t) Assert.InRange(t, 1, 125);
        var engine = new LinuxTweakEngine(fs, new PkexecWriter(), Path.Combine(Path.GetTempPath(), "mt-linux-real-" + Guid.NewGuid().ToString("N")));
        foreach (var tw in LinuxCatalog.All)
        {
            var (state, _) = engine.State(tw);
            foreach (var c in engine.Inspect(tw)) Assert.StartsWith("/", c.Path);
            Console.WriteLine($"{tw.Id}: {state}");
        }
        var report = LinuxDiagnostics.Report(fs, "test", engine);
        Assert.DoesNotContain("unknown distribution", report);
        Console.WriteLine(report);
    }
}
