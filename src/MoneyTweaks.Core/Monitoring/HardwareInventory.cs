using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Monitoring;

public sealed record CpuModel(string Name, int Cores, int Threads, int? BaseMhz, int? L2Kb, int? L3Kb, string Socket);
public sealed record GpuModel(string Name, string Vendor, double? VramGb, string Driver);
public sealed record MemoryModule(string Slot, string Manufacturer, string PartNumber, double CapacityGb, int? RatedMts, int? ConfiguredMts);
public sealed record DriveModel(int Number, string Model, double SizeGb, string Media, string Bus, IReadOnlyList<(string Letter, double FreeGb, double TotalGb)> Volumes);
public sealed record NetworkAdapterModel(string Name, string Description, string Type, double? LinkMbps);

/// <summary>The actual hardware models of this PC, read once from SMBIOS/WMI, DXGI and Windows storage (read-only).</summary>
public sealed record HardwareInventory(CpuModel Cpu, IReadOnlyList<GpuModel> Gpus, IReadOnlyList<MemoryModule> Memory, int MemorySlots,
    IReadOnlyList<DriveModel> Drives, IReadOnlyList<NetworkAdapterModel> Adapters, string Motherboard, string Bios)
{
    public static HardwareInventory Read()
    {
        using var _ = Perf.Measure("hardware inventory", 0);
        var specs = HardwareMonitor.ReadSpecs();
        return new HardwareInventory(ReadCpu(specs), ReadGpus(), ReadMemory(), HardwareMonitor.MemoryLayout().Slots, ReadDrives(), ReadAdapters(), specs.Motherboard.Trim(), specs.Bios);
    }

    private static IEnumerable<ManagementBaseObject> Query(string query, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try { using var s = new ManagementObjectSearcher(scope, query); foreach (var o in s.Get()) list.Add(o); }
        catch (Exception ex) { Log.Debug($"inventory ({query}): {ex.Message}"); }
        return list;
    }

    private static int? Int(object? o) => o is null ? null : int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out var i) && i > 0 ? i : null;

    private static CpuModel ReadCpu(SystemSpecs specs)
    {
        var o = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor").FirstOrDefault();
        if (o is null) return new CpuModel(specs.Cpu, specs.Cores, specs.Threads, null, null, null, "");
        return new CpuModel(o["Name"]?.ToString()?.Trim() ?? specs.Cpu, Int(o["NumberOfCores"]) ?? specs.Cores, Int(o["NumberOfLogicalProcessors"]) ?? specs.Threads,
            Int(o["MaxClockSpeed"]), Int(o["L2CacheSize"]), Int(o["L3CacheSize"]), o["SocketDesignation"]?.ToString()?.Trim() ?? "");
    }

    private static List<GpuModel> ReadGpus()
    {
        var drivers = Query("SELECT Name, DriverVersion FROM Win32_VideoController").ToDictionary(o => o["Name"]?.ToString()?.Trim() ?? "", o => o["DriverVersion"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        var list = Dxgi.Adapters().GroupBy(a => a.Name).Select(g => g.OrderByDescending(a => a.DedicatedVideoMemory).First())
            .Select(a => new GpuModel(a.Name, a.Vendor, a.DedicatedVideoMemory > 0 ? Math.Round(a.DedicatedVideoMemory / 1073741824.0, 1) : null, drivers.GetValueOrDefault(a.Name, ""))).ToList();
        if (list.Count == 0) list.AddRange(drivers.Select(d => new GpuModel(d.Key, "", null, d.Value)));
        return list;
    }

    private static List<MemoryModule> ReadMemory() =>
        [.. Query("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory").Select(o => new MemoryModule(
            o["DeviceLocator"]?.ToString()?.Trim() ?? "", o["Manufacturer"]?.ToString()?.Trim() ?? "", o["PartNumber"]?.ToString()?.Trim() ?? "",
            Math.Round(Convert.ToDouble(o["Capacity"] ?? 0, CultureInfo.InvariantCulture) / 1073741824.0, 1), Int(o["Speed"]), Int(o["ConfiguredClockSpeed"])))];

    private static List<DriveModel> ReadDrives()
    {
        // Volume letters per disk number: partitions → logical disks.
        var letters = new Dictionary<int, List<string>>();
        foreach (var p in Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
        {
            int code = p["DriveLetter"] switch { char ch => ch, null => 0, var x => Convert.ToInt32(x, CultureInfo.InvariantCulture) };
            if (code <= 0 || p["DiskNumber"] is null) continue;
            int n = Convert.ToInt32(p["DiskNumber"], CultureInfo.InvariantCulture);
            (letters.TryGetValue(n, out var l) ? l : letters[n] = []).Add((char)code + ":");
        }
        var drives = new List<DriveModel>();
        foreach (var o in Query("SELECT DeviceId, FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            int num = int.TryParse(o["DeviceId"]?.ToString(), out var d) ? d : -1;
            string media = Convert.ToInt32(o["MediaType"] ?? 0, CultureInfo.InvariantCulture) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
            string bus = Convert.ToInt32(o["BusType"] ?? 0, CultureInfo.InvariantCulture) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 15 => "File-backed", 16 => "Storage Spaces", _ => "" };
            var vols = new List<(string, double, double)>();
            foreach (var l in letters.GetValueOrDefault(num, []).Distinct().Order())
                try { var di = new DriveInfo(l); if (di.IsReady) vols.Add((l, di.AvailableFreeSpace / 1073741824.0, di.TotalSize / 1073741824.0)); } catch { }
            drives.Add(new DriveModel(num, o["FriendlyName"]?.ToString()?.Trim() ?? $"Disk {num}", Math.Round(Convert.ToDouble(o["Size"] ?? 0, CultureInfo.InvariantCulture) / 1e9, 0), media, bus, vols));
        }
        if (drives.Count == 0)   // storage namespace unavailable: fixed drives only
            drives.Add(new DriveModel(-1, "Fixed drives", 0, "", "", [.. DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady).Select(x => (x.Name.TrimEnd('\\'), x.AvailableFreeSpace / 1073741824.0, x.TotalSize / 1073741824.0))]));
        return [.. drives.OrderBy(x => x.Number)];
    }

    private static List<NetworkAdapterModel> ReadAdapters()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().Where(HardwareMonitor.CountsTraffic)
                .Select(n => new NetworkAdapterModel(n.Name, n.Description, n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi", NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => n.NetworkInterfaceType.ToString(),
                }, n.Speed > 0 ? n.Speed / 1e6 : null))];
        }
        catch { return []; }
    }
}
