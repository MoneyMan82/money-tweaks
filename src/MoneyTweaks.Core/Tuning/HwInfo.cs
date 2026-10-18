using System.IO.MemoryMappedFiles;
using System.Text;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

/// <summary>One sensor reading published by HWiNFO.</summary>
public sealed record HwInfoReading(int Type, string Sensor, string Label, string Unit, double Value);

/// <summary>
/// Reads HWiNFO's sensor values from its documented shared memory ("Shared Memory Support" in HWiNFO's settings; layout from
/// HWiNFO's hwisenssm2.h). Read-only: Money Tweaks only copies the published values. HWiNFO reads the CPU sensors with its
/// own signed driver; Money Tweaks installs nothing. In the free version the sharing switches itself off after 12 hours.
/// </summary>
public static class HwInfo
{
    public const string MemoryName = @"Global\HWiNFO_SENS_SM2";
    public const uint Signature = 0x53695748;      // "HWiS"
    public const uint SignatureDead = 0x44414544;  // "DEAD": HWiNFO closed or sharing ended
    public const int TypeTemperature = 1;
    public const string DownloadUrl = "https://www.hwinfo.com/download/";

    // Header (packed): signature, version, revision, poll time (int64), sensor section offset/size/count, reading section offset/size/count.
    public const int HOffSensorOffset = 20, HOffSensorSize = 24, HOffSensorCount = 28, HOffReadingOffset = 32, HOffReadingSize = 36, HOffReadingCount = 40, HeaderSize = 44;
    // Sensor element: id, instance, original name [128], user name [128].
    public const int SOffNameOrig = 8, SOffNameUser = 136, NameLen = 128;
    // Reading element: type, sensor index, reading id, original label [128], user label [128], unit [16], value, min, max, avg (doubles).
    public const int ROffType = 0, ROffSensorIndex = 4, ROffLabelOrig = 12, ROffLabelUser = 140, ROffUnit = 268, UnitLen = 16, ROffValue = 284, ReadingMinSize = 316;

    internal static Func<byte[]?> Reader { get; set; } = ReadShared;

    /// <summary>All readings, or null when HWiNFO isn't sharing (not running, sharing off, or the 12-hour limit passed).</summary>
    public static List<HwInfoReading>? Parse(byte[]? d)
    {
        if (d is null || d.Length < HeaderSize || BitConverter.ToUInt32(d, 0) != Signature) return null;
        int sOff = I(d, HOffSensorOffset), sSize = I(d, HOffSensorSize), sCount = I(d, HOffSensorCount);
        int rOff = I(d, HOffReadingOffset), rSize = I(d, HOffReadingSize), rCount = I(d, HOffReadingCount);
        if (rSize < ReadingMinSize || sSize < SOffNameUser + NameLen || rCount < 0 || sCount < 0) return null;
        var sensors = new List<string>();
        for (int i = 0; i < sCount; i++)
        {
            int e = sOff + i * sSize;
            if (e < 0 || e + sSize > d.Length) break;
            sensors.Add(Str(d, e + SOffNameUser, NameLen) is { Length: > 0 } u ? u : Str(d, e + SOffNameOrig, NameLen));
        }
        var list = new List<HwInfoReading>();
        for (int i = 0; i < rCount; i++)
        {
            int e = rOff + i * rSize;
            if (e < 0 || e + rSize > d.Length) break;
            int si = I(d, e + ROffSensorIndex);
            // The original label is HWiNFO's own name (stable across languages and user renames), so it is what's matched.
            list.Add(new(I(d, e + ROffType), si >= 0 && si < sensors.Count ? sensors[si] : "", Str(d, e + ROffLabelOrig, NameLen),
                Str(d, e + ROffUnit, UnitLen), BitConverter.ToDouble(d, e + ROffValue)));
        }
        return list;
    }

    /// <summary>The CPU temperature from the readings: AMD "CPU (Tctl/Tdie)", Intel "CPU Package", then other whole-CPU sensors. °C.</summary>
    public static double? CpuTemperature(IReadOnlyList<HwInfoReading>? readings)
    {
        if (readings is null) return null;
        string[] preferred = ["CPU (Tctl/Tdie)", "CPU Package", "CPU (Tdie)", "CPU (Tctl)", "CPU Die (average)", "Core Max", "CPU"];
        var cpuTemps = readings.Where(r => r.Type == TypeTemperature && r.Sensor.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var label in preferred)
            foreach (var r in cpuTemps.Where(r => r.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
                if (Celsius(r) is { } c) return c;
        return null;
    }

    public static double? CpuTemperature() => CpuTemperature(Parse(Reader()));

    /// <summary>All current readings (null when HWiNFO isn't sharing).</summary>
    public static List<HwInfoReading>? Readings() => Parse(Reader());

    public const int TypePower = 5;

    /// <summary>CPU package power in watts ("CPU Package Power" on AMD and Intel, "CPU PPT" on some Ryzen readouts).</summary>
    public static double? CpuPower(IReadOnlyList<HwInfoReading>? readings)
    {
        if (readings is null) return null;
        foreach (var label in new[] { "CPU Package Power", "CPU PPT" })
            foreach (var r in readings.Where(r => r.Type == TypePower && r.Sensor.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) && r.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
                if (r.Value is > 0 and < 1000) return r.Value;
        return null;
    }

    /// <summary>True when HWiNFO is running with sharing on.</summary>
    public static bool IsSharing => Parse(Reader()) is { Count: > 0 };

    private static double? Celsius(HwInfoReading r)
    {
        double v = r.Unit.EndsWith('F') ? (r.Value - 32) * 5 / 9 : r.Value;   // HWiNFO can be set to show Fahrenheit
        return double.IsNaN(v) || v <= 0 || v > 150 ? null : v;
    }

    private static int I(byte[] d, int at) => BitConverter.ToInt32(d, at);

    /// <summary>Fixed-size ANSI string (labels are ASCII; the unit's degree sign is Latin-1 0xB0).</summary>
    private static string Str(byte[] d, int at, int len)
    {
        int n = Array.IndexOf(d, (byte)0, at, len) is var z and >= 0 ? z - at : len;
        return Encoding.Latin1.GetString(d, at, n).Trim();
    }

    private static byte[]? ReadShared()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MemoryName, MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            var head = new byte[HeaderSize];
            if (v.Read(head, 0, HeaderSize) < HeaderSize || BitConverter.ToUInt32(head, 0) != Signature) return null;
            long total = Math.Max((long)I(head, HOffSensorOffset) + (long)I(head, HOffSensorSize) * I(head, HOffSensorCount),
                                  (long)I(head, HOffReadingOffset) + (long)I(head, HOffReadingSize) * I(head, HOffReadingCount));
            if (total <= HeaderSize || total > 64 * 1024 * 1024) return null;
            var all = new byte[total];
            v.Position = 0;
            int read = 0;
            while (read < total) { int r = v.Read(all, read, (int)total - read); if (r <= 0) break; read += r; }
            return read == total ? all : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception ex) { Log.Debug("hwinfo: " + ex.Message); return null; }
    }
}
