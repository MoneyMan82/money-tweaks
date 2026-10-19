using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Core.Advisor;

/// <summary>
/// Builds <see cref="HardwareFacts"/> from the existing tuning inventory and system profile, plus RAM type/slots, storage
/// devices, VRAM and the game's disk. Socket and platform are inferred from the CPU model only when the model is
/// recognised; anything Windows can't tell (PSU, case clearance, cooler) is listed as missing instead of guessed.
/// </summary>
public static class HardwareFactsDetector
{
    public static HardwareFacts Detect(TuningInventory inv, string? gameDir)
    {
        var missing = new List<string> { "Power supply wattage and connectors (Windows can't read them)", "Case space for a longer graphics card", "CPU cooler model" };
        var gpu = inv.Gpus.FirstOrDefault(g => g.Vendor is "NVIDIA" or "AMD" && !g.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) ?? inv.Gpus.FirstOrDefault();
        double? vram = null; double? pl = null, plMax = null;
        if (gpu?.Vendor == "NVIDIA" && NvidiaSmi.Available)
        {
            if (NvidiaSmi.ReadMemory(0) is { } m) vram = m.totalMb / 1024.0;
            if (NvidiaSmi.Limits().FirstOrDefault() is { } l) { pl = l.PowerLimitW; plMax = l.MaxPowerLimitW; }
        }
        vram ??= RegistryVram(gpu?.Name);
        if (vram is null) missing.Add("Graphics memory (VRAM) size");

        var (ramType, slots, channels) = RamLayout();
        if (ramType is null) missing.Add("RAM type (DDR4/DDR5)");
        bool? dual = inv.Ram.Count == 1 ? false : channels is { } ch ? ch >= 2 : null;
        if (dual is null && inv.Ram.Count > 1) missing.Add("Which channels the RAM sticks are in");

        var storage = StorageDevices();
        string? drive = gameDir is { Length: > 2 } d && d[1] == ':' ? d[..1].ToUpperInvariant() : null;
        var gameDisk = drive is null ? null : storage.FirstOrDefault(s => s.DriveLetters.Contains(drive));
        if (storage.Count == 0) missing.Add("Storage devices (Windows didn't report them)");

        var (socket, platform) = Socket(inv.Cpu);
        if (socket is null) missing.Add("CPU socket (CPU model not recognised)");
        bool? laptop = Engine.SystemProfile.IsDetected ? Engine.SystemProfile.Current.HasBattery : null;
        return new HardwareFacts
        {
            Cpu = inv.Cpu, CpuVendor = inv.CpuVendor, Cores = inv.Cores, Threads = inv.Threads, Socket = socket, Platform = platform,
            Motherboard = inv.Motherboard, Bios = inv.Bios, Gpu = gpu?.Name ?? "", GpuVendor = gpu?.Vendor ?? "", VramGb = vram,
            RamGb = inv.Ram.Sum(r => r.Gb), RamType = ramType, RamModules = inv.Ram.Count, RamSlots = slots, RamRatedMts = inv.RamRatedMts, RamConfiguredMts = inv.RamConfiguredMts,
            DualChannel = dual, Storage = storage, GameDrive = drive, GameDisk = gameDisk, Laptop = laptop, GpuPowerLimitW = pl, GpuMaxPowerLimitW = plMax, Missing = missing,
        };
    }

    /// <summary>Socket and platform from the CPU model, only for families we can recognise with certainty.</summary>
    public static (string? socket, string? platform) Socket(string cpu)
    {
        var c = cpu.Replace("(R)", "").Replace("(TM)", "");
        var ryzen = Regex.Match(c, @"Ryzen\s+[3579]\s+(?:PRO\s+)?(\d)(\d{3})([A-Z0-9]*)", RegexOptions.IgnoreCase);
        if (ryzen.Success)
        {
            var suffix = ryzen.Groups[3].Value.ToUpperInvariant();
            if (Regex.IsMatch(suffix, @"^(U|H|HS|HX|HX3D)$")) return ("laptop (soldered)", "Laptop CPU: not upgradeable");
            int gen = int.Parse(ryzen.Groups[1].Value);
            if (gen is >= 7 and <= 9) return ("AM5", "AMD AM5 (DDR5): Ryzen 7000/8000/9000 drop in, with a BIOS that supports them");
            if (gen is >= 1 and <= 5) return ("AM4", "AMD AM4 (DDR4): Ryzen 5000 is the end of the line; most boards need a BIOS update for it");
        }
        var core = Regex.Match(c, @"Core\s+i[3579]-(\d{4,5})([A-Z]*)", RegexOptions.IgnoreCase);
        if (core.Success)
        {
            var num = core.Groups[1].Value; var suffix = core.Groups[2].Value.ToUpperInvariant();
            if (Regex.IsMatch(suffix, @"^(U|H|HK|HQ|HX|P|G\d|Y)$")) return ("laptop (soldered)", "Laptop CPU: not upgradeable");
            int gen = num.Length == 5 ? int.Parse(num[..2]) : int.Parse(num[..1]);
            return gen switch
            {
                >= 12 and <= 14 => ("LGA1700", "Intel LGA1700: 12th–14th gen drop in (13th/14th gen need a BIOS update on 600-series boards)"),
                10 or 11 => ("LGA1200", "Intel LGA1200: 10th/11th gen"),
                8 or 9 => ("LGA1151 (300-series)", "Intel LGA1151 v2: 8th/9th gen"),
                6 or 7 => ("LGA1151 (100/200-series)", "Intel LGA1151: 6th/7th gen"),
                _ => (null, null),
            };
        }
        if (Regex.IsMatch(c, @"Core\s+Ultra\s+[579]\s+2\d\d[KF]*\b", RegexOptions.IgnoreCase)) return ("LGA1851", "Intel LGA1851: Core Ultra 200S");
        return (null, null);
    }

    private static (string? type, int? slots, int? channels) RamLayout()
    {
        string? type = null; int? slots = null; var channelLetters = new HashSet<char>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SMBIOSMemoryType, DeviceLocator, BankLabel FROM Win32_PhysicalMemory");
            foreach (var o in s.Get())
            {
                type ??= Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", 29 => "LPDDR3", _ => null };
                var loc = $"{o["DeviceLocator"]} {o["BankLabel"]}";
                var m = Regex.Match(loc, @"(?:Channel\s*|DIMM_?|CH)([A-D])", RegexOptions.IgnoreCase);
                if (m.Success) channelLetters.Add(char.ToUpperInvariant(m.Groups[1].Value[0]));
            }
            using var a = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            foreach (var o in a.Get()) slots = (slots ?? 0) + Convert.ToInt32(o["MemoryDevices"] ?? 0);
        }
        catch (Exception ex) { Log.Debug("ram layout: " + ex.Message); }
        return (type, slots is > 0 ? slots : null, channelLetters.Count > 0 ? channelLetters.Count : null);
    }

    public static List<StorageDevice> StorageDevices()
    {
        var list = new List<StorageDevice>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var letters = new Dictionary<string, List<string>>();
            using (var ps = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                foreach (var o in ps.Get())
                {
                    var dl = Convert.ToChar(o["DriveLetter"] ?? '\0');
                    if (dl == '\0') continue;
                    var key = Convert.ToString(o["DiskNumber"]) ?? "";
                    (letters.TryGetValue(key, out var l) ? l : letters[key] = []).Add(dl.ToString().ToUpperInvariant());
                }
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk"));
            foreach (var o in s.Get())
            {
                var media = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "unknown" };
                var bus = Convert.ToInt32(o["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 3 => "ATA", _ => "other" };
                if (bus == "NVMe" && media == "unknown") media = "SSD";
                list.Add(new(Convert.ToString(o["FriendlyName"]) ?? "Disk", media, bus, Convert.ToDouble(o["Size"] ?? 0) / 1e9, letters.GetValueOrDefault(Convert.ToString(o["DeviceId"]) ?? "") ?? []));
            }
        }
        catch (Exception ex) { Log.Debug("storage devices: " + ex.Message); }
        return list;
    }

    /// <summary>VRAM from the display driver's registry entry (64-bit value; WMI's AdapterRAM caps at 4 GB).</summary>
    private static double? RegistryVram(string? gpuName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k is null) continue;
                var desc = k.GetValue("DriverDesc") as string;
                if (gpuName is not null && desc is not null && !gpuName.Contains(desc, StringComparison.OrdinalIgnoreCase) && !desc.Contains(gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue("HardwareInformation.qwMemorySize") is long q && q > 0) return q / 1073741824.0;
                if (k.GetValue("HardwareInformation.qwMemorySize") is byte[] b && b.Length >= 8) return BitConverter.ToInt64(b) / 1073741824.0;
            }
        }
        catch (Exception ex) { Log.Debug("vram registry: " + ex.Message); }
        return null;
    }
}
