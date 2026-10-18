using System.Text;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;
using AB = MoneyTweaks.Core.Tuning.Afterburner;

namespace MoneyTweaks.Tests;

/// <summary>
/// MSI Afterburner bridge, fan curve and per-game tuning. Control memory is a byte array laid out exactly like
/// SDK\Include\MACMSharedMemory.h; a fake "Afterburner" answers FLUSH like the SDK sample expects. Nothing real is touched.
/// </summary>
[Collection("registry")]
public class AfterburnerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-ab-" + Guid.NewGuid().ToString("N"));
    private readonly AB.IControlMemory _realControl = AB.Control;
    private readonly Func<byte[]?> _realMon = AB.MonitoringReader;
    private readonly TuningEngine.IBackend _realBackend = TuningEngine.Backend;

    public AfterburnerTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose()
    {
        AB.Control = _realControl; AB.MonitoringReader = _realMon; TuningEngine.Backend = _realBackend;
        TuningEngine.GpuLimits = () => NvidiaSmi.Limits().FirstOrDefault();
        TuningEngine.CpuSettings = () => new[] { CpuPowerSettings.BoostMode, CpuPowerSettings.MaxState, CpuPowerSettings.Epp }.Select(CpuPowerSettings.Read).ToDictionary(s => s.Alias);
        TuningEngine.IsAdmin = () => Admin.IsElevated;
        try { Directory.Delete(_tmp, true); } catch { }
    }

    // ---- a control memory built from the SDK header ------------------------------------------------------------------

    private const int Header = 36;

    private static byte[] Memory()
    {
        var d = new byte[Header + AB.EntrySizeV23];
        AB.WU32(d, 0, AB.SignatureMacm); AB.WU32(d, 4, 0x00020003); AB.WU32(d, 8, Header); AB.WU32(d, 12, 1); AB.WU32(d, 16, AB.EntrySizeV23); AB.WU32(d, 20, 0);
        int o = Header;
        AB.WU32(d, o, AB.FlagCoreClockBoost | AB.FlagMemoryClockBoost | AB.FlagFanSpeed | AB.FlagPowerLimit | AB.FlagThermalLimit | AB.FlagVfCurve);
        void R(int at, int cur, int min, int max, int def) { AB.W32(d, o + at, cur); AB.W32(d, o + at + 4, min); AB.W32(d, o + at + 8, max); AB.W32(d, o + at + 12, def); }
        R(AB.OffCoreBoost, 0, -1000_000, 1000_000, 0);     // ±1000 MHz in kHz
        R(AB.OffMemBoost, 0, -1000_000, 1500_000, 0);
        R(AB.OffPowerLimit, 100, 45, 110, 100);
        R(AB.OffThermal, 83, 65, 90, 83);
        AB.W32(d, o + AB.OffFanCur, 30); AB.WU32(d, o + AB.OffFanFlagsCur, AB.FanFlagAuto); AB.W32(d, o + AB.OffFanMin, 30); AB.W32(d, o + AB.OffFanMax, 100); AB.W32(d, o + AB.OffFanDef, 30); AB.WU32(d, o + AB.OffFanFlagsDef, AB.FanFlagAuto);
        // V/F curve: 800…1100 mV in 25 mV steps, base clock rising 1800 → 2900 MHz
        var pts = Enumerable.Range(0, 13).Select(i => (mv: 800 + i * 25, mhz: 1800 + i * 90 + (i == 12 ? 20 : 0))).ToList();
        AB.WU32(d, o + AB.OffVf + 8, (uint)pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            int p = o + AB.OffVfPoints + i * AB.VfPointSize;
            AB.WU32(d, p, (uint)(pts[i].mv * 1000)); AB.WU32(d, p + 4, (uint)(pts[i].mhz * 1000)); AB.W32(d, p + 8, 0);
        }
        Encoding.ASCII.GetBytes("VEN_10DE&DEV_2783").CopyTo(d, o + AB.OffGpuId);
        return d;
    }

    /// <summary>Behaves like Afterburner: on FLUSH it "applies" the memory (optionally clamping one value) and clears the command.</summary>
    private sealed class FakeAfterburner(byte[] mem) : AB.IControlMemory
    {
        public byte[] Mem = mem;
        public Action<byte[]>? Clamp;
        public int Writes;
        public byte[]? Read() => (byte[])Mem.Clone();
        public bool Write(byte[] data, bool flush) { Mem = (byte[])data.Clone(); if (flush) AB.WU32(Mem, AB.HOffCommand, AB.CommandFlush); Writes++; return true; }
        public bool WaitForCompletion(int timeoutMs) { Clamp?.Invoke(Mem); AB.WU32(Mem, AB.HOffCommand, 0); return true; }
    }

    // ---- parsing & changes ---------------------------------------------------------------------------------------------

    [Fact]
    public void Parses_the_documented_layout()
    {
        var g = AB.Parse(Memory())!;
        Assert.Equal((0, -1000, 1000), (g.CoreOffset!.Cur, g.CoreOffset.Min, g.CoreOffset.Max));
        Assert.Equal(100, g.PowerLimit!.Cur);
        Assert.Equal(83, g.ThermalLimit!.Cur);
        Assert.True(g.FanAuto);
        Assert.Equal(13, g.Curve.Count);
        Assert.Equal(950, g.Curve[6].MilliVolts);
        Assert.StartsWith("VEN_10DE", g.GpuId);
        Assert.Null(AB.Parse(new byte[64])); // no signature: not Afterburner
    }

    [Fact]
    public void Undervolt_caps_every_point_at_or_above_the_voltage_and_keeps_lower_points_below_it()
    {
        var after = AB.Parse(AB.ApplyTo(Memory(), new AB.Change { Undervolt = (950, 2700) }))!;
        Assert.True(after.CurveEnabled);
        Assert.All(after.Curve.Where(p => p.MilliVolts >= 950), p => Assert.Equal(2700, p.FrequencyMhz));
        Assert.All(after.Curve.Where(p => p.MilliVolts < 950), p => Assert.True(p.FrequencyMhz <= 2700));
        Assert.Equal(2700 - 2340, after.Curve.Single(p => p.MilliVolts == 950).OffsetMhz); // base at 950 mV is 2340 MHz
        var reset = AB.Parse(AB.ApplyTo(AB.ApplyTo(Memory(), new AB.Change { Undervolt = (950, 2700) }), new AB.Change { ResetCurve = true }))!;
        Assert.False(reset.CurveEnabled);
        Assert.All(reset.Curve, p => Assert.Equal(0, p.OffsetMhz));
    }

    [Fact]
    public void Values_outside_afterburners_limits_are_refused()
    {
        var g = AB.Parse(Memory())!;
        Assert.NotEmpty(AB.Validate(new AB.Change { CoreOffsetMhz = 1200 }, g));
        Assert.NotEmpty(AB.Validate(new AB.Change { FanPercent = 10 }, g));          // below the fan minimum
        Assert.NotEmpty(AB.Validate(new AB.Change { ThermalLimitC = 95 }, g));
        Assert.NotEmpty(AB.Validate(new AB.Change { Undervolt = (933, 2700) }, g));  // not a curve point
        Assert.Empty(AB.Validate(new AB.Change { CoreOffsetMhz = 150, MemOffsetMhz = 500, FanPercent = 60, Undervolt = (950, 2700) }, g));
    }

    [Fact]
    public void Apply_verifies_the_read_back_and_undo_all_restores_afterburners_original_settings()
    {
        var fake = new FakeAfterburner(Memory());
        AB.Control = fake;
        var st = new TuningState();
        var (ok, results) = TuningEngine.ApplyAfterburner(new AB.Change { CoreOffsetMhz = 150, FanPercent = 60 }, st, "Custom");
        Assert.True(ok);
        Assert.All(results, r => Assert.Equal(ItemStatus.Verified, r.Status));
        Assert.Equal(150, AB.Read()!.CoreOffset!.Cur);
        Assert.False(AB.Read()!.FanAuto);
        Assert.NotNull(st.AfterburnerBackup);
        Assert.Equal(0u, AB.U32(fake.Mem, AB.HOffCommand)); // command completed

        TuningEngine.ApplyAfterburner(new AB.Change { CoreOffsetMhz = 90 }, st);    // backup must still be the original
        var (rok, _) = TuningEngine.RevertAll(st);
        Assert.True(rok);
        var g = AB.Read()!;
        Assert.Equal(0, g.CoreOffset!.Cur);
        Assert.True(g.FanAuto);
        Assert.Null(st.AfterburnerBackup);
    }

    [Fact]
    public void A_value_afterburner_did_not_keep_is_a_failure_and_the_change_is_undone()
    {
        var fake = new FakeAfterburner(Memory()) { Clamp = m => AB.W32(m, Header + AB.OffCoreBoost, 100_000) }; // driver clamps +200 to +100
        AB.Control = fake;
        var (ok, results) = TuningEngine.ApplyAfterburner(new AB.Change { CoreOffsetMhz = 200, MemOffsetMhz = 300 }, new TuningState());
        Assert.False(ok);
        Assert.Contains(results, r => r.Item.Contains("core") && r.Status == ItemStatus.Failed);
        Assert.Contains(results, r => r.Item.Contains("memory") && r.Status == ItemStatus.RolledBack);
        fake.Clamp = null;
        Assert.Equal(0, AB.Read()!.MemOffset!.Cur); // restored
    }

    [Fact]
    public void Not_running_is_reported_honestly()
    {
        AB.Control = new FakeAfterburner(new byte[64]);
        var (ok, results) = TuningEngine.ApplyAfterburner(new AB.Change { CoreOffsetMhz = 50 }, new TuningState());
        Assert.False(ok);
        Assert.Contains("isn't running", results.Single().Detail);
    }

    // ---- monitoring: CPU temperature -------------------------------------------------------------------------------------

    [Fact]
    public void Cpu_temperature_is_read_from_the_monitoring_memory()
    {
        const int header = 32, entry = 1324;
        var d = new byte[header + 3 * entry];
        AB.WU32(d, 0, AB.SignatureMahm); AB.WU32(d, 8, header); AB.WU32(d, 12, 3); AB.WU32(d, 16, entry);
        void E(int i, uint src, uint gpu, float v)
        {
            int e = header + i * entry;
            BitConverter.GetBytes(v).CopyTo(d, e + 1300); AB.WU32(d, e + 1316, gpu); AB.WU32(d, e + 1320, src);
        }
        E(0, AB.SrcGpuTemperature, 0, 55);
        E(1, AB.SrcCpuTemperature, 0xFFFFFFFF, 61.5f);
        E(2, AB.SrcCpuTemperature, 0xFFFFFFFF, float.MaxValue); // "not available" per the header
        AB.MonitoringReader = () => d;
        Assert.Equal(61.5, AB.CpuTemperature()!.Value, 1);
        Assert.Equal(55, AB.MonitoringValue(AB.SrcGpuTemperature, cpu: false));
        AB.MonitoringReader = () => null;
        Assert.Null(AB.CpuTemperature());
    }

    // ---- fan curve --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Fan_curve_rules_and_interpolation()
    {
        var fan = new AbRange(30, 30, 100, 30);
        Assert.Empty(FanCurve.Validate(FanCurve.Default, fan));
        Assert.NotEmpty(FanCurve.Validate([[40, 30], [90, 100]], fan));            // 100% only after 85 °C
        Assert.NotEmpty(FanCurve.Validate([[40, 50], [60, 40], [80, 100]], fan));  // goes down
        Assert.NotEmpty(FanCurve.Validate([[40, 10], [80, 100]], fan));            // below the fan minimum
        Assert.Equal(30, FanCurve.Evaluate(FanCurve.Default, 20));
        Assert.Equal(53, FanCurve.Evaluate(FanCurve.Default, 65));
        Assert.Equal(100, FanCurve.Evaluate(FanCurve.Default, 95));
    }

    // ---- per-game tuning ---------------------------------------------------------------------------------------------------

    private sealed class Backend : TuningEngine.IBackend
    {
        public double Power = 220; public bool Locked;
        public Dictionary<string, (int, int)> Cpu = new() { ["PERFBOOSTMODE"] = (2, 2), ["PROCTHROTTLEMAX"] = (100, 100), ["PERFEPP"] = (0, 50) };
        public double? ReadPowerLimit(int gpu) => Power;
        public (bool, string) SetPowerLimit(int gpu, double w) { Power = w; return (true, ""); }
        public (bool, string) LockGpu(int gpu, int a, int b) { Locked = true; return (true, ""); }
        public (bool, string) ResetGpu(int gpu) { Locked = false; return (true, ""); }
        public (bool, string) LockMem(int gpu, int a, int b) => (true, "");
        public (bool, string) ResetMem(int gpu) => (true, "");
        public (int?, int?) ReadCpu(string a) => Cpu.TryGetValue(a, out var v) ? (v.Item1, v.Item2) : (null, null);
        public (bool, string) SetCpu(string a, int ac, int dc) { Cpu[a] = (ac, dc); return (true, ""); }
    }

    private static readonly NvidiaGpuLimits Gpu = new(0, "RTX", "617", 220, 220, 100, 242, Enumerable.Range(0, 200).Select(i => 210 + i * 15).Where(c => c <= 3105).ToList(), [10501], false);

    private void UseFakeHardware(Backend b, bool admin)
    {
        TuningEngine.Backend = b;
        TuningEngine.GpuLimits = () => Gpu;
        TuningEngine.CpuSettings = () => new()
        {
            ["PERFBOOSTMODE"] = new("PERFBOOSTMODE", "Boost mode", "", 0, 6, 1, CpuPowerSettings.BoostChoices, b.Cpu["PERFBOOSTMODE"].Item1, b.Cpu["PERFBOOSTMODE"].Item2),
            ["PROCTHROTTLEMAX"] = new("PROCTHROTTLEMAX", "Max", "", 0, 100, 1, null, 100, 100),
            ["PERFEPP"] = new("PERFEPP", "EPP", "", 0, 100, 1, null, b.Cpu["PERFEPP"].Item1, b.Cpu["PERFEPP"].Item2),
        };
        TuningEngine.IsAdmin = () => admin;
    }

    [Fact]
    public void Per_game_tuning_is_applied_for_the_game_and_put_back_exactly_afterwards()
    {
        var b = new Backend();
        UseFakeHardware(b, admin: true);
        var (restore, problems) = TuningEngine.ApplyForGame("quiet", "Fortnite");
        Assert.Empty(problems);
        Assert.NotNull(restore);
        Assert.True(b.Power < 220 && b.Locked);
        Assert.Equal((3, 3), b.Cpu["PERFBOOSTMODE"]);

        // The session is persisted as JSON (crash recovery), so the restore point must survive a round trip.
        var json = System.Text.Json.JsonSerializer.Serialize(new ProfileSession { TuningRestore = restore });
        var back = System.Text.Json.JsonSerializer.Deserialize<ProfileSession>(json)!.TuningRestore!;
        Assert.Empty(TuningEngine.RestoreAfterGame(back));
        Assert.Equal(220, b.Power);
        Assert.False(b.Locked);
        Assert.Equal((2, 2), b.Cpu["PERFBOOSTMODE"]);
        Assert.Equal((0, 50), b.Cpu["PERFEPP"]); // DC value kept, not overwritten with the AC one
    }

    [Fact]
    public void Without_admin_only_the_cpu_part_is_applied_and_that_is_reported()
    {
        var b = new Backend();
        UseFakeHardware(b, admin: false);
        var (restore, problems) = TuningEngine.ApplyForGame("efficiency", "Fortnite");
        Assert.Contains(problems, p => p.Contains("administrator"));
        Assert.Equal(220, b.Power);                     // GPU untouched
        Assert.Equal((4, 4), b.Cpu["PERFBOOSTMODE"]);   // CPU part applied
        Assert.Empty(TuningEngine.RestoreAfterGame(restore!));
        Assert.Equal((2, 2), b.Cpu["PERFBOOSTMODE"]);
    }

    [Fact]
    public void Gaming_profile_copies_keep_the_tuning_choice()
    {
        var p = new GamingProfile { Name = "A", TuningProfileId = "efficiency" };
        Assert.Equal("efficiency", p.Copy("B").TuningProfileId);
    }
}
