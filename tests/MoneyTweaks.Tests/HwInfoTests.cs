using System.Text;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Tests;

/// <summary>HWiNFO shared memory, built byte for byte in the hwisenssm2.h layout (HWiNFO isn't needed to run these).</summary>
[Collection("registry")]
public class HwInfoTests : IDisposable
{
    public void Dispose() => HwInfo.Reader = () => null;

    private sealed record R(int Type, int Sensor, string Label, string Unit, double Value, string UserLabel = "");

    /// <summary>Real HWiNFO appends fields to each element in newer versions, so elements here are larger than the minimum.</summary>
    private static byte[] Memory(string[] sensors, R[] readings, uint signature = HwInfo.Signature, int sensorSize = 264 + 128, int readingSize = 316 + 600)
    {
        int sOff = HwInfo.HeaderSize, rOff = sOff + sensors.Length * sensorSize;
        var d = new byte[rOff + readings.Length * readingSize];
        void W(int at, int v) => BitConverter.GetBytes(v).CopyTo(d, at);
        void S(int at, string s) => Encoding.Latin1.GetBytes(s).CopyTo(d, at);
        BitConverter.GetBytes(signature).CopyTo(d, 0);
        W(4, 2); W(8, 1);
        W(HwInfo.HOffSensorOffset, sOff); W(HwInfo.HOffSensorSize, sensorSize); W(HwInfo.HOffSensorCount, sensors.Length);
        W(HwInfo.HOffReadingOffset, rOff); W(HwInfo.HOffReadingSize, readingSize); W(HwInfo.HOffReadingCount, readings.Length);
        for (int i = 0; i < sensors.Length; i++) { int e = sOff + i * sensorSize; W(e, 1000 + i); S(e + HwInfo.SOffNameOrig, sensors[i]); }
        for (int i = 0; i < readings.Length; i++)
        {
            var r = readings[i];
            int e = rOff + i * readingSize;
            W(e + HwInfo.ROffType, r.Type); W(e + HwInfo.ROffSensorIndex, r.Sensor); W(e + 8, 1000 + i);
            S(e + HwInfo.ROffLabelOrig, r.Label); S(e + HwInfo.ROffLabelUser, r.UserLabel.Length > 0 ? r.UserLabel : r.Label); S(e + HwInfo.ROffUnit, r.Unit);
            BitConverter.GetBytes(r.Value).CopyTo(d, e + HwInfo.ROffValue);
        }
        return d;
    }

    private const string C = "°C", F = "°F";

    [Fact]
    public void Ryzen_uses_tctl_tdie_not_the_motherboard_or_gpu()
    {
        var d = Memory(["System: ASUS TUF GAMING B650-E WIFI", "CPU [#0]: AMD Ryzen 5 7600X: Enhanced", "GPU [#0]: NVIDIA GeForce RTX 4070 SUPER"],
        [
            new(1, 0, "CPU", C, 41),               // motherboard's CPU socket sensor
            new(1, 1, "CPU Core", C, 0),
            new(1, 1, "CPU (Tctl/Tdie)", C, 62.4, UserLabel: "my cpu"),
            new(1, 2, "GPU Temperature", C, 51),
            new(5, 1, "CPU Package Power", "W", 88),
        ]);
        HwInfo.Reader = () => d;
        Assert.Equal(62.4, HwInfo.CpuTemperature()!.Value, 1);
        Assert.True(HwInfo.IsSharing);
        var all = HwInfo.Parse(d)!;
        Assert.Equal(5, all.Count);
        Assert.Equal("CPU [#0]: AMD Ryzen 5 7600X: Enhanced", all[2].Sensor);
        Assert.Equal(C, all[2].Unit);
    }

    [Fact]
    public void Intel_package_and_fahrenheit_are_handled()
    {
        var d = Memory(["CPU [#0]: Intel Core i5-13600K: DTS"], [new(1, 0, "Core Max", F, 170.6), new(1, 0, "CPU Package", F, 158)]);
        HwInfo.Reader = () => d;
        Assert.Equal(70, HwInfo.CpuTemperature()!.Value, 1); // CPU Package preferred over Core Max; 158 °F = 70 °C
    }

    [Fact]
    public void Not_sharing_closed_or_damaged_memory_gives_nothing()
    {
        HwInfo.Reader = () => null;
        Assert.Null(HwInfo.CpuTemperature());
        Assert.False(HwInfo.IsSharing);
        var dead = Memory(["CPU [#0]: AMD"], [new(1, 0, "CPU (Tctl/Tdie)", C, 60)], signature: HwInfo.SignatureDead);
        HwInfo.Reader = () => dead;
        Assert.Null(HwInfo.CpuTemperature());
        var cut = Memory(["CPU [#0]: AMD"], [new(1, 0, "CPU (Tctl/Tdie)", C, 60)])[..60];
        Assert.Null(HwInfo.Parse(cut) is { Count: > 0 } ? 1 : null);
        var noCpu = Memory(["GPU [#0]: NVIDIA"], [new(1, 0, "GPU Temperature", C, 50), new(1, 0, "CPU", C, 0)]);
        Assert.Null(HwInfo.CpuTemperature(HwInfo.Parse(noCpu)));
    }
}
