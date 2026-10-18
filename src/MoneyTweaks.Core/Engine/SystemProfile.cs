using System.Globalization;
using System.Management;
using Microsoft.Win32;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

/// <summary>
/// What this PC is, as far as tweak compatibility goes. Detected read-only from Windows (registry, WMI, Win32), once per
/// run. Every field is nullable or has a safe "unknown" meaning, so a failed detection never blocks a tweak by itself;
/// tests construct it by hand.
/// </summary>
public sealed record SystemProfile
{
    public int Build { get; init; }
    /// <summary>EditionID, e.g. Professional, Core, Enterprise, Education.</summary>
    public string Edition { get; init; } = "";
    public bool? HasBattery { get; init; }
    public double RamGb { get; init; }
    /// <summary>True = the Windows drive is an SSD, false = a hard disk, null = unknown.</summary>
    public bool? SystemDiskIsSsd { get; init; }
    /// <summary>Real display adapters (virtual, remote and basic display drivers left out).</summary>
    public IReadOnlyList<string> Gpus { get; init; } = [];
    public int? PhysicalPrinters { get; init; }
    public bool? HasBiometricDevice { get; init; }
    public bool? HasBluetooth { get; init; }
    public bool? RemoteDesktopEnabled { get; init; }
    public bool? HasWiredAdapter { get; init; }

    public bool IsEnterpriseLike => Edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || Edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
        || Edition.Contains("Server", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<SystemProfile> Cached = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);
    private static SystemProfile? _override;

    /// <summary>This PC. The first call does the WMI queries (about a second); call it off the UI thread.</summary>
    public static SystemProfile Current => _override ?? Cached.Value;
    /// <summary>True once <see cref="Current"/> is available without waiting.</summary>
    public static bool IsDetected => _override is not null || Cached.IsValueCreated;
    /// <summary>Tests: use a hand-built profile instead of this PC.</summary>
    internal static void OverrideForTests(SystemProfile? p) => _override = p;

    public static SystemProfile Detect()
    {
        using var perf = Perf.Measure("system profile", 200);
        var (total, _, _) = MemoryInfo.Status();
        return new SystemProfile
        {
            Build = OsInfo.Build,
            Edition = Try(() => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "") as string) ?? "",
            HasBattery = Try(() => (bool?)(Wmi("SELECT Name FROM Win32_Battery").Count > 0)),
            RamGb = Math.Round(total / 1073741824.0, 1),
            SystemDiskIsSsd = Try(SystemDiskSsd),
            Gpus = Try(() => Wmi("SELECT Name FROM Win32_VideoController").Select(o => o["Name"]?.ToString() ?? "").Where(IsRealGpu).ToList()) ?? [],
            PhysicalPrinters = Try(() => (int?)Wmi("SELECT Name FROM Win32_Printer").Count(o => IsPhysicalPrinter(o["Name"]?.ToString()))),
            HasBiometricDevice = Try(() => (bool?)(Wmi("SELECT Name FROM Win32_PnPEntity WHERE PNPClass='Biometric' AND Status='OK'").Count > 0)),
            HasBluetooth = Try(() => (bool?)(Wmi("SELECT Name FROM Win32_PnPEntity WHERE PNPClass='Bluetooth' AND Status='OK'").Count > 0)),
            RemoteDesktopEnabled = Try(() => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", null) is int d ? d == 0 : (bool?)null),
            HasWiredAdapter = Try(() => (bool?)System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))),
        };
    }

    /// <summary>Virtual, remote-desktop and fallback adapters aren't real GPUs for compatibility purposes.</summary>
    public static bool IsRealGpu(string name) =>
        name.Length > 0 && !new[] { "Basic Display", "Basic Render", "Virtual", "Remote", "Parsec", "Meta ", "Citrix", "VMware", "Hyper-V", "Mirror" }
            .Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Software printers (PDF, XPS, OneNote, Fax) don't need the Print Spooler for anything but printing to files.</summary>
    public static bool IsPhysicalPrinter(string? name) =>
        name is { Length: > 0 } && !new[] { "PDF", "XPS", "OneNote", "Fax", "Send To" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static bool? SystemDiskSsd()
    {
        var letter = Path.GetPathRoot(Environment.SystemDirectory)?[0].ToString() ?? "C";
        const string ns = @"root\Microsoft\Windows\Storage";
        var part = Wmi($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='{letter}'", ns).FirstOrDefault();
        if (part is null) return null;
        var disk = Convert.ToUInt32(part["DiskNumber"], CultureInfo.InvariantCulture);
        var pd = Wmi($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='{disk}'", ns).FirstOrDefault();
        return pd?["MediaType"] is { } m ? Convert.ToInt32(m, CultureInfo.InvariantCulture) switch { 4 => true, 3 => false, _ => null } : null;
    }

    private static List<ManagementBaseObject> Wmi(string query, string ns = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(ns, query);
        return s.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static T? Try<T>(Func<T?> f)
    {
        try { return f(); }
        catch (Exception ex) { Log.Debug("system profile: " + ex.Message); return default; }
    }

    public string Describe() =>
        $"Windows build {Build} {Edition}; {(HasBattery == true ? "laptop/battery" : HasBattery == false ? "desktop (no battery)" : "battery unknown")}; " +
        $"{RamGb:0.#} GB RAM; system drive {(SystemDiskIsSsd is { } s ? (s ? "SSD" : "hard disk") : "type unknown")}; " +
        $"GPU{(Gpus.Count == 1 ? "" : "s")}: {(Gpus.Count == 0 ? "none detected" : string.Join(", ", Gpus))}";
}
