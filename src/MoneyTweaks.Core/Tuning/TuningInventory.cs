using System.Globalization;
using System.Management;
using System.Text.Json;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Tuning;

public sealed record GpuInfo(string Name, string Vendor, string DriverVersion, string DriverDate);
public sealed record RamModule(string Slot, string Manufacturer, string PartNumber, double Gb, int? RatedMts, int? ConfiguredMts);
public sealed record TuningTool(string Name, string? Path, string Url);

/// <summary>Hardware, firmware, drivers and installed vendor tools relevant to tuning. Read-only; cached on disk.</summary>
public sealed record TuningInventory
{
    public DateTime DetectedAt { get; init; }
    public string Cpu { get; init; } = "";
    public string CpuVendor { get; init; } = ""; // AMD, Intel or other
    public int Cores { get; init; }
    public int Threads { get; init; }
    public string Motherboard { get; init; } = "";
    public string Bios { get; init; } = "";
    public List<GpuInfo> Gpus { get; init; } = [];
    public List<RamModule> Ram { get; init; } = [];
    public int? RamRatedMts { get; init; }
    public int? RamConfiguredMts { get; init; }
    public List<TuningTool> Tools { get; init; } = [];

    public bool IsAmdCpu => CpuVendor == "AMD";
    public bool IsIntelCpu => CpuVendor == "Intel";
    public bool IsRyzen => IsAmdCpu && Cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
    /// <summary>Intel "K"/"KF"/"KS"/"HX" models have unlocked multipliers; others can't be overclocked by multiplier.</summary>
    public bool IntelUnlocked => IsIntelCpu && System.Text.RegularExpressions.Regex.IsMatch(Cpu, @"\d{4,5}(K|KF|KS|HX)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public bool HasNvidia => Gpus.Any(g => g.Vendor == "NVIDIA");
    /// <summary>A discrete Radeon (RX / Pro). Ryzen's integrated "AMD Radeon(TM) Graphics" has no tuning in Adrenalin worth handing off to.</summary>
    public bool HasAmdGpu => Gpus.Any(g => g.Vendor == "AMD" && System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"\bRX\s?\d|Radeon Pro|Radeon VII", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    public TuningTool? Tool(string name) => Tools.FirstOrDefault(t => t.Name == name);
    /// <summary>Changes when hardware or firmware changes: a different key means the cached inventory is stale.</summary>
    public string Key => $"{Cpu}|{Bios}|{string.Join(",", Gpus.Select(g => g.Name + g.DriverVersion))}";
}

public static class TuningDetector
{
    private static string CachePath => Path.Combine(AppPaths.Root, "tuning-inventory.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static TuningInventory? _current;
    private static Task<TuningInventory>? _scan;

    /// <summary>The last saved inventory (instant), or null on first use.</summary>
    public static TuningInventory? Cached()
    {
        if (_current is not null) return _current;
        try { return File.Exists(CachePath) ? _current = JsonSerializer.Deserialize<TuningInventory>(File.ReadAllText(CachePath)) : null; }
        catch (Exception ex) { Log.Warn("tuning cache unreadable: " + ex.Message); return null; }
    }

    /// <summary>One background scan per run (repeat calls share it); <paramref name="force"/> = Rescan button.</summary>
    public static Task<TuningInventory> ScanAsync(bool force = false)
    {
        if (!force && _scan is not null) return _scan;
        return _scan = Task.Run(() =>
        {
            var inv = Detect();
            _current = inv;
            try { AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(inv, Json)); } catch (Exception ex) { Log.Warn("tuning cache: " + ex.Message); }
            return inv;
        });
    }

    public static TuningInventory Detect()
    {
        using var perf = Perf.Measure("tuning inventory", 300);
        var specs = HardwareMonitor.ReadSpecs();
        string cpuVendor = specs.Cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || specs.Cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : specs.Cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Other";
        var gpus = new List<GpuInfo>();
        foreach (var o in Wmi("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate FROM Win32_VideoController"))
        {
            var name = Str(o, "Name");
            if (!Engine.SystemProfile.IsRealGpu(name)) continue;
            var comp = Str(o, "AdapterCompatibility");
            var vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || comp.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || comp.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : comp;
            var date = Str(o, "DriverDate");
            gpus.Add(new(name, vendor, Str(o, "DriverVersion"), date.Length >= 8 ? $"{date[..4]}-{date[4..6]}-{date[6..8]}" : ""));
        }
        var ram = Wmi("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory").Select(o => new RamModule(
            Str(o, "DeviceLocator"), Str(o, "Manufacturer"), Str(o, "PartNumber"),
            Math.Round(Convert.ToDouble(o["Capacity"] ?? 0d, CultureInfo.InvariantCulture) / 1073741824.0, 1),
            o["Speed"] is { } s ? Convert.ToInt32(s, CultureInfo.InvariantCulture) : null,
            o["ConfiguredClockSpeed"] is { } c ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : null)).ToList();

        return new TuningInventory
        {
            DetectedAt = DateTime.Now, Cpu = specs.Cpu, CpuVendor = cpuVendor, Cores = specs.Cores, Threads = specs.Threads,
            Motherboard = specs.Motherboard, Bios = specs.Bios, Gpus = gpus, Ram = ram,
            RamRatedMts = specs.RamRatedMts, RamConfiguredMts = specs.RamConfiguredMts, Tools = FindTools(),
        };
    }

    /// <summary>Vendor tuning tools, found by their uninstall entries and default install paths. Nothing is downloaded.</summary>
    private static List<TuningTool> FindTools()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        List<(string name, string location)> entries;
        try { entries = Games.GameScanner.UninstallEntries(); } catch { entries = []; }
        string? Find(string uninstallName, params string[] paths) =>
            paths.FirstOrDefault(File.Exists) ?? entries.Where(e => e.name.Contains(uninstallName, StringComparison.OrdinalIgnoreCase)).Select(e => e.location).FirstOrDefault(l => l.Length > 0);
        return
        [
            new("AMD Ryzen Master", Find("Ryzen Master", Path.Combine(pf, @"AMD\RyzenMaster\bin\AMD Ryzen Master.exe")), "https://www.amd.com/en/products/software/ryzen-master.html"),
            new("Intel XTU", Find("Extreme Tuning Utility", Path.Combine(pf, @"Intel\Intel(R) Extreme Tuning Utility\Client\XtuUiLauncher.exe"), Path.Combine(pf86, @"Intel\Intel(R) Extreme Tuning Utility\Client\XtuUiLauncher.exe")), "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html"),
            new("NVIDIA App", Find("NVIDIA App", Path.Combine(pf, @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe")), "https://www.nvidia.com/en-us/software/nvidia-app/"),
            new("MSI Afterburner", Find("MSI Afterburner", Path.Combine(pf86, @"MSI Afterburner\MSIAfterburner.exe"), Path.Combine(pf, @"MSI Afterburner\MSIAfterburner.exe")), "https://www.msi.com/Landing/afterburner/graphics-cards"),
            new("HWiNFO", Find("HWiNFO", Path.Combine(pf, @"HWiNFO64\HWiNFO64.exe"), Path.Combine(pf86, @"HWiNFO64\HWiNFO64.exe"), Path.Combine(pf, @"HWiNFO\HWiNFO64.exe")), HwInfo.DownloadUrl),
            new("AMD Software: Adrenalin", Find("AMD Software", Path.Combine(pf, @"AMD\CNext\CNext\RadeonSoftware.exe")), "https://www.amd.com/en/products/software/adrenalin.html"),
        ];
    }

    private static List<ManagementBaseObject> Wmi(string q)
    {
        try { using var s = new ManagementObjectSearcher(q); return s.Get().Cast<ManagementBaseObject>().ToList(); }
        catch (Exception ex) { Log.Debug("tuning wmi: " + ex.Message); return []; }
    }

    private static string Str(ManagementBaseObject o, string p) => (o[p]?.ToString() ?? "").Trim();
}
