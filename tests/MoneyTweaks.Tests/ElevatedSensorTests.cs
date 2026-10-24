using LibreHardwareMonitor.Hardware;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks.Tests;

/// <summary>The CPU sensor helper's pipe protocol: values round-trip exactly; nothing but finite CPU/motherboard readings is accepted.</summary>
public class ElevatedSensorTests
{
    [Fact]
    public void Readings_round_trip_exactly()
    {
        var r = (HardwareType.Cpu, "AMD Ryzen 5 7600X", SensorType.Temperature, "Core (Tctl/Tdie)", 52.375);
        Assert.Equal(r, ElevatedSensors.Parse(ElevatedSensors.Format(r)));
        var tabs = (HardwareType.SuperIO, "Nuvoton\tNCT6799D", SensorType.Fan, "Fan #2", 812.0);
        Assert.Equal("Nuvoton NCT6799D", ElevatedSensors.Parse(ElevatedSensors.Format(tabs))!.Value.device);
    }

    [Theory]
    [InlineData("GpuNvidia\tRTX\tTemperature\tGPU Core\t50")]     // the helper is for CPU and motherboard sensors only
    [InlineData("Cpu\tCPU\tTemperature\tCore\tNaN")]
    [InlineData("Cpu\tCPU\tTemperature\tCore\tInfinity")]
    [InlineData("Cpu\tCPU\tBogusType\tCore\t50")]
    [InlineData("Cpu\tCPU\tTemperature\t50")]
    [InlineData("#frame driver=1")]
    [InlineData("")]
    public void Anything_else_is_rejected(string line) => Assert.Null(ElevatedSensors.Parse(line));
}
