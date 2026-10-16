using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Health;

public sealed record DriverInfo(string Device, string Class, string Provider, string Manufacturer, string Version, DateTime? Date, string DeviceId, string Inf);
public sealed record DeviceProblem(string Device, string Class, int Code, string DeviceId);
public sealed record EventHit(DateTime Time, int Id, string Provider, int Level, IReadOnlyList<string> Data);
public sealed record DriveSpace(string Name, string Label, double FreeGb, double TotalGb, bool IsSystem);
public sealed record UpdateHistoryEntry(string Title, int ResultCode, int HResult, DateTime Date);
public sealed record PendingUpdate(string Title, bool IsDriver, string DriverClass, string DriverModel, string MsrcSeverity);
public sealed record GpuSensor(string Name, double? TempC, double? UtilPercent, ulong? Reasons);

/// <summary>One unit of the health scan. A check can return results for several areas; Area is where its placeholder shows.</summary>
public sealed record HealthCheck(string Id, HealthArea Area, string Name, Func<CancellationToken, IReadOnlyList<HealthResult>> Run);

public sealed record HealthProgress(HealthCheck Check, IReadOnlyList<HealthResult> Results, int Done, int Total);

/// <summary>
/// Driver, Windows and stability health checks. Everything is read-only: WMI, the registry, event logs, performance counters,
/// the Windows Update agent (search only) and nvidia-smi when the NVIDIA driver installed it. Collection is separate from
/// analysis so the rules can be tested with hand-made data. Nothing is guessed: when data isn't available the result is
/// "Unable to check" with the reason.
/// </summary>
public static class HealthChecks
{
    public const int EventDays = 14;
    private static readonly HealthLink WindowsUpdateLink = new("Open Windows Update", "ms-settings:windowsupdate");
    private static readonly HealthLink OptionalUpdatesLink = new("Optional driver updates", "ms-settings:windowsupdate-optionalupdates");
    private static readonly HealthLink DeviceManagerLink = new("Open Device Manager", "devmgmt.msc");
    private static readonly HealthLink ReliabilityLink = new("Open Reliability Monitor", "perfmon.exe /rel");
    private static readonly HealthLink NvidiaLink = new("NVIDIA drivers (official)", "https://www.nvidia.com/en-us/drivers/");
    private static readonly HealthLink AmdLink = new("AMD drivers (official)", "https://www.amd.com/en/support/download/drivers.html");
    private static readonly HealthLink IntelLink = new("Intel Driver & Support Assistant (official)", "https://www.intel.com/content/www/us/en/support/detect.html");

    public static IReadOnlyList<HealthCheck> All { get; } =
    [
        new("drivers", HealthArea.Drivers, "Device drivers", _ => Drivers()),
        new("windows-version", HealthArea.Windows, "Windows version", _ => WindowsVersion()),
        new("wu-services", HealthArea.Windows, "Update services and policies", _ => UpdateServices()),
        new("reboot", HealthArea.Windows, "Pending restart", _ => RebootPending()),
        new("wu-history", HealthArea.Windows, "Update history", _ => UpdateHistory()),
        new("wu-search", HealthArea.Windows, "Windows Update search", _ => UpdateSearch()),
        new("events", HealthArea.Stability, "Crash and error logs", _ => Events()),
        new("load", HealthArea.Stability, "CPU/GPU load and throttling", _ => LoadAndThermal()),
        new("memory", HealthArea.Stability, "Memory pressure", _ => Memory()),
        new("disks", HealthArea.Stability, "Drives and free space", _ => Disks()),
    ];

    /// <summary>Runs every check in parallel; reports each as it finishes. A failing check becomes "Unable to check".</summary>
    public static async Task<List<HealthResult>> RunAllAsync(IProgress<HealthProgress>? progress, CancellationToken ct = default)
    {
        var all = new List<HealthResult>();
        int done = 0;
        var tasks = All.Select(c => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            IReadOnlyList<HealthResult> res;
            try { res = c.Run(ct); }
            catch (Exception ex)
            {
                Log.Error($"Health check {c.Id} failed", ex);
                res = [R(c.Id, c.Area, c.Name, HealthStatus.Unknown, Severity.Info, "This check couldn't run: " + ex.Message, [ex.GetType().Name + ": " + ex.Message])];
            }
            Log.Info($"Health check {c.Id}: {res.Count} result(s) in {sw.ElapsedMilliseconds} ms");
            lock (all) { all.AddRange(res); done++; }
            progress?.Report(new(c, res, done, All.Count));
        }, ct)).ToList();
        await Task.WhenAll(tasks);
        return all.OrderBy(r => r.Area).ToList();
    }

    public static HealthResult R(string id, HealthArea area, string title, HealthStatus status, Severity sev, string summary, IReadOnlyList<string> details) =>
        new(id, area, title, status, sev, summary, details, DateTime.Now);

    // ================================================================================================================
    // Drivers
    // ================================================================================================================

    private static List<ManagementBaseObject> Wmi(string query, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(scope, query);
        return s.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString()?.Trim() ?? ""; } catch { return ""; } }

    public static IReadOnlyList<HealthResult> Drivers()
    {
        var drivers = new List<DriverInfo>();
        foreach (var o in Wmi("SELECT DeviceName, DeviceClass, DriverProviderName, Manufacturer, DriverVersion, DriverDate, DeviceID, InfName FROM Win32_PnPSignedDriver"))
        {
            var name = Str(o, "DeviceName");
            if (name.Length == 0) continue;
            DateTime? date = null;
            try { if (Str(o, "DriverDate") is { Length: > 0 } d) date = ManagementDateTimeConverter.ToDateTime(d); } catch { }
            drivers.Add(new(name, Str(o, "DeviceClass").ToUpperInvariant(), Str(o, "DriverProviderName"), Str(o, "Manufacturer"), Str(o, "DriverVersion"), date, Str(o, "DeviceID"), Str(o, "InfName")));
        }
        var problems = new List<DeviceProblem>();
        foreach (var o in Wmi("SELECT Name, PNPClass, ConfigManagerErrorCode, DeviceID, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
        {
            try { if (o["Present"] is bool present && !present) continue; } catch { /* property missing on old builds */ }
            problems.Add(new(Str(o, "Name") is { Length: > 0 } n ? n : Str(o, "DeviceID"), Str(o, "PNPClass"), Convert.ToInt32(o["ConfigManagerErrorCode"]), Str(o, "DeviceID")));
        }
        var cpu = string.Join(" + ", Wmi("SELECT Name FROM Win32_Processor").Select(o => Str(o, "Name")));
        return AnalyzeDrivers(drivers, problems, cpu, DateTime.Now);
    }

    /// <summary>NVIDIA's public version from the Windows driver version: 32.0.15.6094 → 560.94.</summary>
    public static string? NvidiaVersion(string windowsVersion)
    {
        var p = windowsVersion.Split('.');
        if (p.Length != 4 || !p.All(x => x.All(char.IsDigit) && x.Length > 0)) return null;
        var digits = p[2] + p[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3], CultureInfo.InvariantCulture)}.{last5[3..]}";
    }

    private static bool IsPhysical(DriverInfo d) =>
        d.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
        d.DeviceId.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith(@"INTELAUDIO\", StringComparison.OrdinalIgnoreCase) ||
        d.DeviceId.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase);

    private static string Line(DriverInfo d) =>
        $"{d.Device} · {d.Provider} {d.Version}{(d.Date is { } dt ? " · " + dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")} · {d.Inf}";

    private static string Age(DateTime? date, DateTime now)
    {
        if (date is not { } d) return "date unknown";
        var months = (int)((now - d).TotalDays / 30.44);
        return months < 1 ? "less than a month old" : months == 1 ? "1 month old" : $"{months} months old";
    }

    /// <summary>Device Manager problem codes (CM_PROB_*), in plain words.</summary>
    public static string ProblemText(int code) => code switch
    {
        1 => "not configured correctly",
        3 => "driver may be corrupted, or the system is low on memory",
        10 => "device cannot start",
        12 => "not enough free resources",
        14 => "needs a restart to work",
        18 => "drivers need to be reinstalled",
        19 => "configuration in the registry is incomplete or damaged",
        21 => "Windows is removing the device",
        22 => "disabled",
        24 => "not present or not working (missing hardware or drivers)",
        28 => "no driver installed",
        31 => "not working properly; Windows couldn't load its drivers",
        32 => "driver service is disabled",
        37 => "driver failed to initialise",
        39 => "driver is corrupted or missing",
        41 => "driver loaded but the hardware wasn't found",
        43 => "stopped because it reported problems",
        45 => "not connected",
        47 => "prepared for safe removal",
        48 => "driver blocked because of known issues",
        52 => "driver signature couldn't be verified",
        _ => $"problem code {code}",
    };

    public static List<HealthResult> AnalyzeDrivers(List<DriverInfo> drivers, List<DeviceProblem> problems, string cpu, DateTime now)
    {
        var list = new List<HealthResult>();
        const HealthArea A = HealthArea.Drivers;

        // ---- GPU ----
        // Physical GPUs only; virtual displays (VR, remote desktop, capture) stay in the inventory below.
        var allDisplay = drivers.Where(d => d.Class == "DISPLAY").ToList();
        var display = allDisplay.Where(d => d.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (display.Count == 0) display = allDisplay;
        if (display.Count == 0)
            list.Add(R("drv-gpu", A, "Graphics driver", HealthStatus.Unknown, Severity.Info, "Windows didn't report any display adapter driver.", []));
        foreach (var g in display.DistinctBy(d => d.DeviceId))
        {
            var id = "drv-gpu-" + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(g.DeviceId)).ToString(CultureInfo.InvariantCulture);
            bool basic = g.Device.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                         (g.Provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && g.Inf.Equals("display.inf", StringComparison.OrdinalIgnoreCase));
            var details = new List<string> { Line(g), "Device ID: " + g.DeviceId, "Driver date: " + Age(g.Date, now) };
            bool nv = g.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Device.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            bool amd = g.Provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Provider.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) || g.Device.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
            bool intel = g.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            var links = new List<HealthLink>();
            if (nv) links.Add(NvidiaLink);
            if (amd) links.Add(AmdLink);
            if (intel) links.Add(IntelLink);
            if (basic)
            {
                links.AddRange([NvidiaLink, AmdLink, IntelLink]);
                list.Add(R(id, A, "Graphics driver missing", HealthStatus.Problem, Severity.High,
                    $"{g.Device} is running on Windows' basic display driver: no GPU acceleration, low FPS and limited resolutions. Install the driver from your GPU maker.", details)
                    with { Links = links.Distinct().ToList() });
                continue;
            }
            var nvVer = nv ? NvidiaVersion(g.Version) : null;
            if (nvVer is not null) details.Insert(1, $"NVIDIA driver version: {nvVer} (Windows reports {g.Version})");
            list.Add(R(id, A, "Graphics driver: " + g.Device, HealthStatus.Passed, Severity.Info,
                $"{(nvVer is not null ? "NVIDIA " + nvVer : g.Provider + " " + g.Version)} installed and loaded{(g.Date is { } d ? $", released {d:yyyy-MM-dd} ({Age(g.Date, now)})" : "")}.", details)
                with
                {
                    Links = links,
                    Uncertainty = "Money Tweaks doesn't contact your GPU maker, so it can't tell whether a newer driver exists. Compare the version above with the official page. The Windows Update check below shows drivers Windows itself offers.",
                });
        }

        // ---- Chipset ----
        var chipset = drivers.Where(d => d.Class == "SYSTEM" && d.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) &&
            (d.Provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) || d.Provider.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) || d.Provider.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(d => d.DeviceId).ToList();
        bool amdCpu = cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase), intelCpu = cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        var chipLinks = new List<HealthLink>();
        if (amdCpu) chipLinks.Add(AmdLink);
        if (intelCpu) chipLinks.Add(IntelLink);
        // Dual-CCD X3D chips rely on AMD's 3D V-Cache Performance Optimizer driver to put games on the V-Cache cores.
        bool dualCcdX3d = Regex.IsMatch(cpu, @"\b(7900X3D|7950X3D|9900X3D|9950X3D)\b", RegexOptions.IgnoreCase);
        bool hasVcacheDriver = drivers.Any(d => d.Device.Contains("3D V-Cache", StringComparison.OrdinalIgnoreCase));
        if (dualCcdX3d && !hasVcacheDriver)
            list.Add(R("drv-chipset", A, "AMD 3D V-Cache driver missing", HealthStatus.Warning, Severity.Medium,
                $"{cpu.Trim()} has two CPU dies and only one has the 3D V-Cache. The \"AMD 3D V-Cache Performance Optimizer\" device from AMD's chipset package isn't installed, so Windows may schedule games on the die without the cache.",
                chipset.Select(Line).DefaultIfEmpty("No AMD chipset drivers found; Windows' built-in drivers are in use.").ToList()) with { Links = chipLinks });
        else if (chipset.Count > 0)
            list.Add(R("drv-chipset", A, "Chipset drivers", HealthStatus.Passed, Severity.Info,
                $"{chipset.Count} chipset device(s) use drivers from {string.Join(" and ", chipset.Select(c => c.Provider).Distinct())}; newest dated {chipset.Max(c => c.Date):yyyy-MM-dd}.",
                chipset.Select(Line).ToList()) with { Links = chipLinks });
        else
            list.Add(R("drv-chipset", A, "Chipset drivers", HealthStatus.Passed, Severity.Info,
                "Windows' built-in chipset drivers are in use. They run the chipset correctly; the CPU maker's chipset package can add power-management and scheduler extras.",
                [$"CPU: {cpu.Trim()}", "No chipset device uses an AMD or Intel driver package."]) with { Links = chipLinks, Uncertainty = "Not having the vendor chipset package is not an error, so this isn't flagged." });

        // ---- Network, audio, storage ----
        void Group(string id, string title, Func<DriverInfo, bool> pick, string none)
        {
            var g = drivers.Where(d => pick(d) && IsPhysical(d)).DistinctBy(d => d.DeviceId).ToList();
            if (g.Count == 0) { list.Add(R(id, A, title, HealthStatus.Unknown, Severity.Info, none, [])); return; }
            list.Add(R(id, A, title, HealthStatus.Passed, Severity.Info,
                string.Join("; ", g.Take(3).Select(d => $"{d.Device} ({d.Provider} {d.Version})")) + (g.Count > 3 ? $" and {g.Count - 3} more" : "") + ".",
                g.Select(d => Line(d) + " · " + Age(d.Date, now)).ToList()) with { Links = [DeviceManagerLink] });
        }
        Group("drv-network", "Network drivers", d => d.Class == "NET", "Windows reports no physical network adapter driver.");
        Group("drv-audio", "Audio drivers", d => d.Class == "MEDIA", "Windows reports no physical audio device driver.");
        Group("drv-storage", "Storage controller drivers", d => d.Class is "SCSIADAPTER" or "HDC", "Windows reports no storage controller driver.");

        // ---- Device Manager errors ----
        var serious = problems.Where(p => p.Code is not (22 or 45 or 24 or 47 or 14)).ToList();
        var restart = problems.Where(p => p.Code == 14).ToList();
        var disabled = problems.Where(p => p.Code == 22).ToList();
        var pdetails = problems.Where(p => p.Code is not (45 or 47)).Select(p => $"{p.Device} [{p.Class}] code {p.Code}: {ProblemText(p.Code)} · {p.DeviceId}").ToList();
        if (serious.Count > 0)
        {
            var missing = serious.Count(p => p.Code == 28);
            list.Add(R("drv-errors", A, missing > 0 ? $"{missing} device(s) without a driver" : "Device errors in Device Manager", HealthStatus.Problem, missing > 0 ? Severity.High : Severity.Medium,
                $"{serious.Count} device(s) report a problem: " + string.Join("; ", serious.Take(3).Select(p => $"{p.Device} ({ProblemText(p.Code)})")) + (serious.Count > 3 ? "…" : "."),
                pdetails) with { Links = [DeviceManagerLink, OptionalUpdatesLink], RepairId = SafeRepairs.PnpRescan });
        }
        else if (restart.Count > 0)
            list.Add(R("drv-errors", A, "Devices waiting for a restart", HealthStatus.Warning, Severity.Low,
                $"{restart.Count} device(s) need a Windows restart to finish installing.", pdetails) with { Links = [DeviceManagerLink] });
        else
            list.Add(R("drv-errors", A, "Device Manager", HealthStatus.Passed, Severity.Info,
                disabled.Count > 0 ? $"No device errors. {disabled.Count} device(s) are disabled (fine if you disabled them on purpose)." : "No device reports an error or a missing driver.",
                pdetails) with { Links = [DeviceManagerLink] });

        // ---- Everything else (technical inventory) ----
        var thirdParty = drivers.Where(d => !d.Provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && d.Provider.Length > 0)
            .DistinctBy(d => d.DeviceId).OrderBy(d => d.Class).ThenBy(d => d.Device).ToList();
        list.Add(R("drv-inventory", A, "Other device drivers", HealthStatus.Passed, Severity.Info,
            $"{drivers.Count} drivers loaded, {thirdParty.Count} from hardware makers (the rest are Windows' own). See technical details for the full list.",
            thirdParty.Take(80).Select(d => $"[{d.Class}] " + Line(d)).ToList())
            with { Uncertainty = "Windows' own drivers often show a 2006 date; that is normal and doesn't mean they're outdated." });
        return list;
    }

    // ================================================================================================================
    // Windows
    // ================================================================================================================

    public static IReadOnlyList<HealthResult> WindowsVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = k?.GetValue("EditionID") as string ?? "";
        var display = k?.GetValue("DisplayVersion") as string ?? "";
        var buildStr = k?.GetValue("CurrentBuild") as string ?? OsInfo.Build.ToString(CultureInfo.InvariantCulture);
        var ubr = k?.GetValue("UBR") is int u ? u : (int?)null;
        int.TryParse(buildStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build);
        DateTime? installed = k?.GetValue("InstallDate") is int secs ? DateTimeOffset.FromUnixTimeSeconds((uint)secs).LocalDateTime : null;
        var details = new List<string>
        {
            $"Product: {OsInfo.ProductName}", $"Edition: {edition}", $"Version: {(display.Length > 0 ? display : "unknown")}",
            $"Build: {build}{(ubr is { } x ? "." + x : "")}", $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
        };
        if (installed is { } i) details.Add($"Installed or last upgraded: {i:yyyy-MM-dd}");
        var (status, sev, summary, note) = WindowsSupport(build, display, edition, DateTime.Today);
        return [R("win-version", HealthArea.Windows, $"{OsInfo.ProductName}", status, sev, summary, details) with { Uncertainty = note, Links = [WindowsUpdateLink] }];
    }

    /// <summary>Servicing end dates published by Microsoft (Home/Pro; Enterprise/Education get 12 more months on Windows 11).</summary>
    public static (HealthStatus, Severity, string, string?) WindowsSupport(int build, string displayVersion, string edition, DateTime today)
    {
        const string note = "Dates from Microsoft's Windows lifecycle pages as known to this version of Money Tweaks.";
        bool ent = edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase);
        if (build < 22000)
        {
            if (build >= 19045 || build == 0)
                return (HealthStatus.Warning, Severity.Medium, "Windows 10 reached end of support on 14 Oct 2025. Only PCs enrolled in Extended Security Updates still get security fixes. Upgrade to Windows 11 if this PC supports it.", note);
            return (HealthStatus.Problem, Severity.High, $"This Windows 10 version ({displayVersion}, build {build}) no longer gets security updates. Install the latest version through Windows Update.", note);
        }
        (DateTime home, DateTime entEnd)? end = build switch
        {
            22000 => (new(2023, 10, 10), new(2024, 10, 8)),
            22621 => (new(2024, 10, 8), new(2025, 10, 14)),
            22631 => (new(2025, 11, 11), new(2026, 11, 10)),
            26100 => (new(2026, 10, 13), new(2027, 10, 12)),
            26200 => (new(2027, 10, 12), new(2028, 10, 10)),
            _ => null,
        };
        var name = $"Windows 11 {(displayVersion.Length > 0 ? displayVersion : "build " + build)}";
        if (end is null)
            return build > 26200
                ? (HealthStatus.Passed, Severity.Info, $"{name} (build {build}) is newer than the versions Money Tweaks has lifecycle dates for (likely a Windows Insider build or a new release).", "Insider builds have their own expiry; Windows warns you before one expires.")
                : (HealthStatus.Unknown, Severity.Info, $"No lifecycle date known for build {build}.", null);
        var e = ent ? end.Value.entEnd : end.Value.home;
        var days = (e - today).TotalDays;
        if (days < 0)
            return (HealthStatus.Warning, Severity.High, $"{name} stopped getting security updates on {e:d MMM yyyy} ({(ent ? "Enterprise/Education" : "Home/Pro")}). Install the newest version from Windows Update.", note);
        if (days < 60)
            return (HealthStatus.Warning, Severity.Low, $"{name} gets security updates until {e:d MMM yyyy} ({(int)days} days). Windows Update will offer the next version.", note);
        return (HealthStatus.Passed, Severity.Info, $"{name} is supported until {e:d MMM yyyy}.", note);
    }

    public static readonly (string Name, string Display, string DefaultMode)[] UpdateServiceList =
    [
        ("wuauserv", "Windows Update", "Manual"),
        ("BITS", "Background Intelligent Transfer Service", "Manual"),
        ("UsoSvc", "Update Orchestrator Service", "Auto"),
        ("CryptSvc", "Cryptographic Services", "Auto"),
        ("TrustedInstaller", "Windows Modules Installer", "Manual"),
    ];

    public static IReadOnlyList<HealthResult> UpdateServices()
    {
        var modes = new Dictionary<string, (string mode, string state)>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Wmi("SELECT Name, StartMode, State FROM Win32_Service WHERE Name='wuauserv' OR Name='BITS' OR Name='UsoSvc' OR Name='CryptSvc' OR Name='TrustedInstaller'"))
            modes[Str(o, "Name")] = (Str(o, "StartMode"), Str(o, "State"));
        var list = new List<HealthResult> { AnalyzeServices(modes) };

        // Policies and pause: informational, never changed by Money Tweaks.
        var pol = new List<string>();
        using (var au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"))
            if (au?.GetValue("NoAutoUpdate") is int n && n == 1) pol.Add("Policy NoAutoUpdate = 1: automatic updates are turned off.");
        using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"))
        {
            if (wu?.GetValue("DisableWindowsUpdateAccess") is int d && d == 1) pol.Add("Policy DisableWindowsUpdateAccess = 1: Windows Update access is blocked.");
            if (wu?.GetValue("WUServer") is string srv && srv.Length > 0) pol.Add($"Updates come from a WSUS server: {srv} (managed PC).");
        }
        DateTime? pausedUntil = null;
        using (var ux = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"))
            if (ux?.GetValue("PauseUpdatesExpiryTime") is string p && DateTime.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until) && until > DateTime.UtcNow)
                pausedUntil = until.ToLocalTime();
        if (pausedUntil is { } pu) pol.Add($"Updates are paused until {pu:yyyy-MM-dd}.");
        bool blocking = pol.Any(x => x.Contains("turned off") || x.Contains("blocked"));
        list.Add(pol.Count == 0
            ? R("wu-policy", HealthArea.Windows, "Update policies", HealthStatus.Passed, Severity.Info, "No policy blocks or pauses Windows Update.", [])
            : R("wu-policy", HealthArea.Windows, "Update policies", HealthStatus.Warning, blocking ? Severity.Medium : Severity.Low,
                blocking ? "A Group Policy setting stops Windows Update from installing updates automatically." : pol[0], pol)
                with { Links = [WindowsUpdateLink], Uncertainty = "Policies are set by you, a tool or your organisation. Money Tweaks shows them but doesn't change them." });
        return list;
    }

    public static HealthResult AnalyzeServices(IReadOnlyDictionary<string, (string mode, string state)> modes)
    {
        var details = UpdateServiceList.Select(s => modes.TryGetValue(s.Name, out var m)
            ? $"{s.Display} ({s.Name}): start {m.mode}, {m.state}"
            : $"{s.Display} ({s.Name}): not found").ToList();
        if (modes.Count == 0)
            return R("wu-services", HealthArea.Windows, "Update services", HealthStatus.Unknown, Severity.Info, "Couldn't read the update services from Windows.", details);
        var disabled = UpdateServiceList.Where(s => modes.TryGetValue(s.Name, out var m) && m.mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)).ToList();
        if (disabled.Count > 0)
            return R("wu-services", HealthArea.Windows, "Update services disabled", HealthStatus.Problem, Severity.Medium,
                $"{string.Join(", ", disabled.Select(d => d.Display))} {(disabled.Count == 1 ? "is" : "are")} disabled, so Windows can't download or install updates (including security fixes).",
                details) with { RepairId = SafeRepairs.UpdateServices, Uncertainty = "If you disabled one with Money Tweaks' Services page, undo it there instead so its backup stays in sync." };
        return R("wu-services", HealthArea.Windows, "Update services", HealthStatus.Passed, Severity.Info,
            "All update services can start when Windows needs them (\"Stopped\" with Manual start is normal).", details);
    }

    public static IReadOnlyList<HealthResult> RebootPending()
    {
        var why = new List<string>();
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            if (k is not null) why.Add("Component Based Servicing: RebootPending (Windows components or updates installed)");
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            if (k is not null) why.Add("Windows Update: RebootRequired");
        var boot = TimeSpan.FromMilliseconds(Environment.TickCount64);
        why.Add($"Up for {(int)boot.TotalDays}d {boot.Hours}h since the last restart");
        return [why.Count > 1
            ? R("reboot", HealthArea.Windows, "Restart pending", HealthStatus.Warning, Severity.Low, "Windows is waiting for a restart to finish installing updates. Restart before a long gaming session.", why)
            : R("reboot", HealthArea.Windows, "Restart pending", HealthStatus.Passed, Severity.Info, "No update is waiting for a restart.", why)];
    }

    private const string Utf8 = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;";

    public static IReadOnlyList<HealthResult> UpdateHistory()
    {
        const string script = Utf8 + "try{$s=New-Object -ComObject Microsoft.Update.Session;$q=$s.CreateUpdateSearcher();$n=$q.GetTotalHistoryCount();$h=@();" +
            "if($n -gt 0){$h=@($q.QueryHistory(0,[Math]::Min($n,200))|Where-Object{$_.Operation -eq 1}|ForEach-Object{[pscustomobject]@{T=[string]$_.Title;R=[int]$_.ResultCode;H=[int]$_.HResult;D=$_.Date.ToUniversalTime().ToString('o')}})};" +
            "[pscustomobject]@{Ok=$true;H=$h}|ConvertTo-Json -Depth 3 -Compress}catch{[pscustomobject]@{Ok=$false;E=[int]$_.Exception.HResult;M=[string]$_.Exception.Message}|ConvertTo-Json -Compress}";
        var r = ProcessRunner.PowerShell(script, 60_000);
        try
        {
            using var doc = JsonDocument.Parse(LastJson(r.StdOut));
            var root = doc.RootElement;
            if (!root.GetProperty("Ok").GetBoolean())
                return [UnknownWu("wu-history", "Update history", root)];
            var entries = new List<UpdateHistoryEntry>();
            if (root.TryGetProperty("H", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (var e in h.EnumerateArray())
                    entries.Add(new(e.GetProperty("T").GetString() ?? "", e.GetProperty("R").GetInt32(), e.GetProperty("H").GetInt32(),
                        DateTime.Parse(e.GetProperty("D").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime()));
            return AnalyzeHistory(entries, DateTime.Now);
        }
        catch (Exception ex)
        {
            return [R("wu-history", HealthArea.Windows, "Update history", HealthStatus.Unknown, Severity.Info, "Couldn't read the Windows Update history.", [ex.Message, r.Combined.Length > 400 ? r.Combined[..400] : r.Combined])];
        }
    }

    private static string LastJson(string stdout) => stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{')) ?? throw new FormatException("No result from the Windows Update agent.");

    private static HealthResult UnknownWu(string id, string title, JsonElement root)
    {
        var code = root.TryGetProperty("E", out var e) ? e.GetInt32() : 0;
        var msg = root.TryGetProperty("M", out var m) ? m.GetString() ?? "" : "";
        return R(id, HealthArea.Windows, title, HealthStatus.Unknown, Severity.Info,
            "The Windows Update agent didn't answer: " + (HResultText(code) ?? "error 0x" + code.ToString("X8", CultureInfo.InvariantCulture)) + ".",
            [$"HRESULT 0x{code:X8}", msg]) with { Links = [WindowsUpdateLink] };
    }

    /// <summary>Plain-language meaning of common Windows Update error codes.</summary>
    public static string? HResultText(int hr) => unchecked((uint)hr) switch
    {
        0x800F081F => "source files couldn't be found (component store problem; DISM RestoreHealth often fixes it)",
        0x800F0922 => "the update couldn't be installed (often a full System Reserved partition, or a VPN blocking the update servers)",
        0x80070002 or 0x80070003 => "files were missing from the download (retrying usually helps)",
        0x8024402C or 0x8024401C or 0x80072EE2 or 0x80072EFD or 0x80072EE7 or 0x8024402F or 0x80244022 => "the update servers couldn't be reached (offline, proxy or firewall)",
        0x80070070 => "not enough disk space",
        0x800F0831 => "a package from an earlier update is missing (component store)",
        0x80073712 or 0x8007000D => "the component store is damaged (DISM RestoreHealth)",
        0x80240034 => "the download failed",
        0x80070422 => "a required service is disabled",
        0x800705B4 => "the operation timed out",
        0x80240022 => "all updates in the batch failed",
        0x80070005 => "access denied",
        0x8024001E => "the operation was cancelled (shutdown or restart during the update)",
        _ => null,
    };

    private static readonly Regex StoreApp = new(@"^[9X][A-Z0-9]{11}-", RegexOptions.Compiled);
    private static readonly Regex Kb = new(@"KB\d{6,8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Defender definitions/platform and the Malicious Software Removal Tool install constantly; they say nothing about Windows patches.
    private static readonly string[] Frequent = ["KB2267602", "KB4052623", "KB890830", "KB2311978", "KB915597"];

    public static List<HealthResult> AnalyzeHistory(List<UpdateHistoryEntry> entries, DateTime now)
    {
        var list = new List<HealthResult>();
        const HealthArea A = HealthArea.Windows;
        string Key(UpdateHistoryEntry e) => Kb.Match(e.Title) is { Success: true } m ? m.Value.ToUpperInvariant() : e.Title;
        bool Ok(UpdateHistoryEntry e) => e.ResultCode is 2 or 3;
        // Microsoft Store app installs ("9NMN2Z9TZP22-Publisher.App") also go through Windows Update; they aren't Windows health.
        entries = entries.Where(e => !StoreApp.IsMatch(e.Title)).ToList();
        var failed = entries.Where(e => e.ResultCode is 4 or 5 && (now - e.Date).TotalDays <= 30)
            .Where(f => !entries.Any(s => Ok(s) && Key(s) == Key(f) && s.Date > f.Date))
            .GroupBy(Key).Select(g => g.OrderByDescending(e => e.Date).First()).ToList();
        if (failed.Count > 0)
            list.Add(R("wu-failed", A, $"{failed.Count} update(s) failed to install", HealthStatus.Warning, failed.Count > 2 ? Severity.Medium : Severity.Low,
                $"Failed in the last 30 days and not installed since: {string.Join("; ", failed.Take(2).Select(f => f.Title))}{(failed.Count > 2 ? "…" : "")}",
                failed.Select(f => $"{f.Date:yyyy-MM-dd HH:mm} · {f.Title} · 0x{f.HResult:X8}{(HResultText(f.HResult) is { } t ? " (" + t + ")" : "")}").ToList())
                with { Links = [WindowsUpdateLink], RepairId = failed.Any(f => unchecked((uint)f.HResult) is 0x800F081F or 0x80073712 or 0x8007000D or 0x800F0831) ? SafeRepairs.DismRestore : null,
                       Uncertainty = "Windows often retries failed updates by itself; a single failure that later succeeds isn't counted." });
        else
            list.Add(R("wu-failed", A, "Failed updates", HealthStatus.Passed, Severity.Info,
                entries.Count == 0 ? "The Windows Update history is empty." : "No update has failed in the last 30 days without succeeding later.",
                entries.Where(e => !Ok(e)).Take(10).Select(e => $"{e.Date:yyyy-MM-dd} · {e.Title} · result {e.ResultCode} · 0x{e.HResult:X8}").ToList()));

        var real = entries.Where(e => Ok(e) && !Frequent.Contains(Key(e))).OrderByDescending(e => e.Date).ToList();
        if (real.Count == 0)
            list.Add(R("wu-last", A, "Last installed update", HealthStatus.Unknown, Severity.Info, "The Windows Update history has no installed Windows updates to date it by.", []));
        else
        {
            var days = (int)(now - real[0].Date).TotalDays;
            var status = days > 60 ? HealthStatus.Warning : HealthStatus.Passed;
            list.Add(R("wu-last", A, "Last installed update", status, days > 90 ? Severity.Medium : days > 60 ? Severity.Low : Severity.Info,
                $"{real[0].Title} was installed {(days == 0 ? "today" : days == 1 ? "yesterday" : days + " days ago")}." + (status == HealthStatus.Warning ? " Windows normally installs fixes every month." : ""),
                real.Take(12).Select(e => $"{e.Date:yyyy-MM-dd} · {e.Title}").ToList()) with { Links = [WindowsUpdateLink] });
        }
        return list;
    }

    public static IReadOnlyList<HealthResult> UpdateSearch()
    {
        const string script = Utf8 + "try{$s=New-Object -ComObject Microsoft.Update.Session;$s.ClientApplicationID='Money Tweaks health check';$q=$s.CreateUpdateSearcher();" +
            "$r=$q.Search(\"IsInstalled=0 and IsHidden=0\");$u=@($r.Updates|ForEach-Object{[pscustomobject]@{T=[string]$_.Title;Drv=($_.Type -eq 2);C=[string]$_.DriverClass;M=[string]$_.DriverModel;S=[string]$_.MsrcSeverity}});" +
            "[pscustomobject]@{Ok=$true;R=[int]$r.ResultCode;U=$u}|ConvertTo-Json -Depth 3 -Compress}catch{[pscustomobject]@{Ok=$false;E=[int]$_.Exception.HResult;M=[string]$_.Exception.Message}|ConvertTo-Json -Compress}";
        var r = ProcessRunner.PowerShell(script, 240_000);
        if (r.ExitCode == -1)
            return [R("wu-pending", HealthArea.Windows, "Available updates", HealthStatus.Unknown, Severity.Info, "Windows Update didn't answer within 4 minutes.", [r.StdErr]),
                    R("drv-wu", HealthArea.Drivers, "Driver updates from Windows Update", HealthStatus.Unknown, Severity.Info, "Windows Update didn't answer within 4 minutes.", [r.StdErr])];
        try
        {
            using var doc = JsonDocument.Parse(LastJson(r.StdOut));
            var root = doc.RootElement;
            if (!root.GetProperty("Ok").GetBoolean())
            {
                var u = UnknownWu("wu-pending", "Available updates", root);
                return [u, u with { Id = "drv-wu", Area = HealthArea.Drivers, Title = "Driver updates from Windows Update" }];
            }
            var pending = new List<PendingUpdate>();
            if (root.TryGetProperty("U", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    pending.Add(new(e.GetProperty("T").GetString() ?? "", e.GetProperty("Drv").GetBoolean(), e.GetProperty("C").GetString() ?? "", e.GetProperty("M").GetString() ?? "", e.GetProperty("S").GetString() ?? ""));
            return AnalyzePending(pending);
        }
        catch (Exception ex)
        {
            return [R("wu-pending", HealthArea.Windows, "Available updates", HealthStatus.Unknown, Severity.Info, "Couldn't read the Windows Update search result.", [ex.Message])];
        }
    }

    public static List<HealthResult> AnalyzePending(List<PendingUpdate> pending)
    {
        var sw = pending.Where(p => !p.IsDriver).ToList();
        var drv = pending.Where(p => p.IsDriver).ToList();
        bool security = sw.Any(p => p.MsrcSeverity is "Critical" or "Important");
        return
        [
            sw.Count == 0
                ? R("wu-pending", HealthArea.Windows, "Available updates", HealthStatus.Passed, Severity.Info, "Windows Update has nothing new to install.", [])
                : R("wu-pending", HealthArea.Windows, $"{sw.Count} update(s) waiting", HealthStatus.Warning, security ? Severity.Medium : Severity.Low,
                    $"Available but not installed yet{(security ? ", including security fixes rated Critical/Important" : "")}: {string.Join("; ", sw.Take(2).Select(p => p.Title))}{(sw.Count > 2 ? "…" : "")}",
                    sw.Select(p => p.Title + (p.MsrcSeverity.Length > 0 ? $" [{p.MsrcSeverity}]" : "")).ToList())
                    with { Links = [WindowsUpdateLink], Uncertainty = "New updates appear every month; having some waiting for a few days is normal." },
            drv.Count == 0
                ? R("drv-wu", HealthArea.Drivers, "Driver updates from Windows Update", HealthStatus.Passed, Severity.Info, "Windows Update offers no newer driver for your devices.", [])
                    with { Uncertainty = "Windows Update only has drivers that makers publish there; GPU makers usually release newer ones on their own sites first." }
                : R("drv-wu", HealthArea.Drivers, $"{drv.Count} newer driver(s) offered", HealthStatus.Warning, Severity.Low,
                    $"Windows Update has newer drivers for: {string.Join("; ", drv.Take(3).Select(d => d.DriverModel.Length > 0 ? d.DriverModel : d.Title))}{(drv.Count > 3 ? "…" : "")}. Install them from Optional updates if you want them.",
                    drv.Select(d => $"{d.Title} · class {d.DriverClass} · {d.DriverModel}").ToList())
                    with { Links = [OptionalUpdatesLink], Uncertainty = "Money Tweaks never installs drivers. For GPUs, the maker's own driver is usually newer than the Windows Update one." },
        ];
    }

    // ================================================================================================================
    // Stability: event logs
    // ================================================================================================================

    /// <summary>Reads matching events from the last <see cref="EventDays"/> days, newest first.</summary>
    public static List<EventHit> QueryEvents(string log, string provider, int[]? ids = null, int[]? levels = null, int max = 200)
    {
        var cond = $"Provider[@Name='{provider}']";
        if (ids is { Length: > 0 }) cond += " and (" + string.Join(" or ", ids.Select(i => $"EventID={i}")) + ")";
        if (levels is { Length: > 0 }) cond += " and (" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ")";
        cond += $" and TimeCreated[timediff(@SystemTime) <= {(long)EventDays * 86_400_000}]";
        var list = new List<EventHit>();
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, $"*[System[{cond}]]") { ReverseDirection = true });
        for (EventRecord? e = reader.ReadEvent(); e is not null && list.Count < max; e = reader.ReadEvent())
        {
            using (e)
            {
                List<string> data;
                try { data = e.Properties.Select(p => p.Value?.ToString() ?? "").ToList(); } catch { data = []; }
                list.Add(new(e.TimeCreated ?? DateTime.MinValue, e.Id, e.ProviderName, e.Level ?? 0, data));
            }
        }
        return list;
    }

    public static IReadOnlyList<HealthResult> Events()
    {
        var res = new List<HealthResult>();
        var failures = new List<string>();
        List<EventHit> Q(string log, string provider, int[]? ids = null, int[]? levels = null)
        {
            try { return QueryEvents(log, provider, ids, levels); }
            catch (EventLogNotFoundException) { return []; }
            catch (Exception ex) { failures.Add($"{log}/{provider}: {ex.Message}"); return []; }
        }
        var kp41 = Q("System", "Microsoft-Windows-Kernel-Power", [41]);
        var ev6008 = Q("System", "EventLog", [6008]);
        var bug = Q("System", "Microsoft-Windows-WER-SystemErrorReporting", [1001]);
        var tdr = Q("System", "Display", [4101]);
        var nv = Q("System", "nvlddmkm", levels: [1, 2]);
        var amd = Q("System", "amdkmdag", levels: [1, 2]).Concat(Q("System", "amdkmdap", levels: [1, 2])).ToList();
        var whea = Q("System", "Microsoft-Windows-WHEA-Logger");
        var disk = Q("System", "disk", [7, 11, 51, 153]).Concat(Q("System", "Ntfs", [55])).Concat(Q("System", "stornvme", levels: [1, 2])).ToList();
        var crashes = Q("Application", "Application Error", [1000]);
        var hangs = Q("Application", "Application Hang", [1002]);
        res.Add(AnalyzeRestarts(kp41, ev6008, bug));
        res.Add(AnalyzeGpuDriverEvents(tdr, nv, amd));
        res.Add(AnalyzeAppCrashes(crashes, hangs));
        res.Add(AnalyzeWhea(whea));
        res.Add(AnalyzeDiskEvents(disk));
        if (failures.Count > 0)
            res = res.Select(r => r with { Uncertainty = (r.Uncertainty is null ? "" : r.Uncertainty + " ") + "Some event logs couldn't be read: " + string.Join("; ", failures) }).ToList();
        return res;
    }

    private static string T(EventHit e) => e.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Common stop codes in plain words.</summary>
    public static string BugcheckName(uint code) => code switch
    {
        0x116 => "VIDEO_TDR_FAILURE (GPU driver stopped responding)",
        0x117 => "VIDEO_TDR_TIMEOUT_DETECTED (GPU driver timeout)",
        0x133 => "DPC_WATCHDOG_VIOLATION (a driver took too long)",
        0x124 => "WHEA_UNCORRECTABLE_ERROR (hardware error: unstable overclock/undervolt, heat or failing part)",
        0x1A => "MEMORY_MANAGEMENT (often unstable RAM/XMP/EXPO)",
        0x3B => "SYSTEM_SERVICE_EXCEPTION (driver or system file fault)",
        0x50 => "PAGE_FAULT_IN_NONPAGED_AREA (driver or RAM)",
        0xD1 => "DRIVER_IRQL_NOT_LESS_OR_EQUAL (faulty driver)",
        0xA => "IRQL_NOT_LESS_OR_EQUAL (driver or RAM)",
        0x9F => "DRIVER_POWER_STATE_FAILURE (driver sleep/wake problem)",
        0x7E => "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED (driver fault)",
        0xEF => "CRITICAL_PROCESS_DIED",
        0x139 => "KERNEL_SECURITY_CHECK_FAILURE (driver or memory corruption)",
        0x101 => "CLOCK_WATCHDOG_TIMEOUT (CPU stopped responding: often CPU overclock/undervolt)",
        0x19 => "BAD_POOL_HEADER (driver or RAM)",
        0xC2 => "BAD_POOL_CALLER (driver fault)",
        0x1E => "KMODE_EXCEPTION_NOT_HANDLED (driver fault)",
        0x7F => "UNEXPECTED_KERNEL_MODE_TRAP (hardware or driver)",
        0x154 => "UNEXPECTED_STORE_EXCEPTION (storage or memory)",
        0x0 => "no stop code (power loss, reset button or a hard hang)",
        _ => $"0x{code:X}",
    };

    public static uint? ParseBugcheck(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, @"^0x([0-9a-fA-F]+)");
        if (m.Success) return uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static HealthResult AnalyzeRestarts(List<EventHit> kp41, List<EventHit> ev6008, List<EventHit> bugchecks)
    {
        const HealthArea A = HealthArea.Stability;
        var details = new List<string>();
        foreach (var b in bugchecks)
            details.Add($"{T(b)} · Blue screen: {(b.Data.Count > 0 && ParseBugcheck(b.Data[0]) is { } c ? BugcheckName(c) : "stop code unknown")}");
        foreach (var k in kp41)
        {
            var code = k.Data.Count > 0 ? ParseBugcheck(k.Data[0]) : null;
            details.Add($"{T(k)} · Kernel-Power 41: Windows restarted without shutting down cleanly{(code is { } c and > 0 ? " · stop code " + BugcheckName(c) : "")}");
        }
        foreach (var e in ev6008) details.Add($"{T(e)} · EventLog 6008: the previous shutdown was unexpected");
        const string unc = "Kernel-Power 41 is also logged when you hold the power button, the power goes out or the PC is reset, so it can't always be told apart from a crash.";
        var bsods = bugchecks.Count + kp41.Count(k => k.Data.Count > 0 && ParseBugcheck(k.Data[0]) is > 0 && !bugchecks.Any(b => Math.Abs((b.Time - k.Time).TotalMinutes) < 15));
        var others = kp41.Count(k => !(k.Data.Count > 0 && ParseBugcheck(k.Data[0]) is > 0));
        if (bsods > 0)
            return R("stab-restarts", A, $"{bsods} blue screen crash(es) in {EventDays} days", HealthStatus.Problem, Severity.High,
                "Windows crashed with a stop error. The stop code in the details points at the cause (often a driver, unstable overclock/XMP or heat)." +
                (others > 0 ? $" There were also {others} other unexpected restart(s)." : ""), details)
                with { Uncertainty = unc, Links = [ReliabilityLink] };
        var unexpected = Math.Max(kp41.Count, ev6008.Count);
        if (unexpected > 0)
            return R("stab-restarts", A, $"{unexpected} unexpected restart(s) in {EventDays} days", HealthStatus.Warning, unexpected > 2 ? Severity.Medium : Severity.Low,
                "Windows didn't shut down cleanly. If you didn't cut the power yourself, this points to a freeze, power-supply or overheating problem.", details)
                with { Uncertainty = unc, Links = [ReliabilityLink] };
        return R("stab-restarts", A, "Unexpected restarts", HealthStatus.Passed, Severity.Info, $"No blue screens or unexpected restarts in the last {EventDays} days.", details);
    }

    public static HealthResult AnalyzeGpuDriverEvents(List<EventHit> tdr, List<EventHit> nv, List<EventHit> amd)
    {
        var details = tdr.Select(e => $"{T(e)} · Display 4101: the display driver{(e.Data.Count > 0 ? " " + e.Data[0] : "")} stopped responding and recovered")
            .Concat(nv.Select(e => $"{T(e)} · nvlddmkm event {e.Id}{(e.Data.Count > 2 ? ": " + string.Join(" ", e.Data.Skip(2).Take(2)) : "")}"))
            .Concat(amd.Select(e => $"{T(e)} · {e.Provider} event {e.Id}")).ToList();
        const string unc = "GPU driver resets also come from unstable GPU overclocks/undervolts, power-supply problems or overheating, not only from the driver itself.";
        if (tdr.Count >= 3)
            return R("stab-gpu", HealthArea.Stability, $"GPU driver reset {tdr.Count} times", HealthStatus.Problem, Severity.High,
                $"The graphics driver stopped responding {tdr.Count} times in {EventDays} days (games usually crash or freeze when this happens). Try a clean driver reinstall and remove GPU overclocks.", details)
                with { Uncertainty = unc };
        if (tdr.Count > 0 || nv.Count + amd.Count > 0)
            return R("stab-gpu", HealthArea.Stability, "GPU driver errors", HealthStatus.Warning, tdr.Count > 0 ? Severity.Medium : Severity.Low,
                tdr.Count > 0 ? $"The graphics driver stopped responding {tdr.Count} time(s) in {EventDays} days and Windows recovered it." : $"The graphics driver logged {nv.Count + amd.Count} error(s) in {EventDays} days.",
                details) with { Uncertainty = unc };
        return R("stab-gpu", HealthArea.Stability, "GPU driver stability", HealthStatus.Passed, Severity.Info, $"No graphics driver resets or errors in the last {EventDays} days.", details);
    }

    private static readonly string[] GpuModules = ["nvwgf2umx", "nvoglv64", "nvd3dumx", "nvldumdx", "amdxx64", "atidxx64", "amdxc64", "aticfx64", "igd10iumd64", "igd12umd64", "igxelpicd64", "dxgi.dll", "d3d11.dll", "d3d12"];

    public static HealthResult AnalyzeAppCrashes(List<EventHit> crashes, List<EventHit> hangs)
    {
        // Application Error 1000: [0] app, [1] app version, [3] faulting module, [6] exception code. Application Hang 1002: [0] app.
        var items = crashes.Select(e => (app: e.Data.ElementAtOrDefault(0) ?? "?", module: e.Data.ElementAtOrDefault(3) ?? "", code: e.Data.ElementAtOrDefault(6) ?? "", hang: false, e))
            .Concat(hangs.Select(e => (app: e.Data.ElementAtOrDefault(0) ?? "?", module: "", code: "", hang: true, e))).ToList();
        var byApp = items.GroupBy(i => i.app, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ToList();
        var details = byApp.Select(g => $"{g.Key}: {g.Count(x => !x.hang)} crash(es), {g.Count(x => x.hang)} hang(s)" +
            (g.Where(x => x.module.Length > 0).GroupBy(x => x.module, StringComparer.OrdinalIgnoreCase).OrderByDescending(m => m.Count()).FirstOrDefault() is { } top ? $" · faulting module {top.Key}" : "") +
            $" · last {T(g.Max(x => x.e.Time) is var t ? g.First(x => x.e.Time == t).e : g.First().e)}").ToList();
        var gpuFault = items.Where(i => GpuModules.Any(m => i.module.Contains(m, StringComparison.OrdinalIgnoreCase))).ToList();
        if (gpuFault.Count > 0)
            return R("stab-apps", HealthArea.Stability, "Crashes inside the graphics driver", HealthStatus.Warning, Severity.Medium,
                $"{gpuFault.Count} crash(es) happened in a graphics driver module ({string.Join(", ", gpuFault.Select(g => g.module).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}) in {string.Join(", ", gpuFault.Select(g => g.app).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}.",
                details) with { Uncertainty = "The crashing module is where the fault surfaced; the cause can still be the game, an overlay or an unstable GPU overclock.", Links = [ReliabilityLink] };
        var repeat = byApp.Where(g => g.Count() >= 3).ToList();
        if (repeat.Count > 0)
            return R("stab-apps", HealthArea.Stability, "Apps crashing repeatedly", HealthStatus.Warning, Severity.Low,
                $"{string.Join(", ", repeat.Take(3).Select(g => $"{g.Key} ({g.Count()}×)"))} crashed or hung repeatedly in {EventDays} days.", details)
                with { Links = [ReliabilityLink] };
        return R("stab-apps", HealthArea.Stability, "App and game crashes", HealthStatus.Passed, Severity.Info,
            items.Count == 0 ? $"No app crashes or hangs logged in the last {EventDays} days." : $"{items.Count} isolated crash(es)/hang(s) in {EventDays} days, none repeating.", details);
    }

    public static HealthResult AnalyzeWhea(List<EventHit> whea)
    {
        var fatal = whea.Where(e => e.Level is 1 or 2).ToList();
        var corrected = whea.Where(e => e.Level is 3 or 4 or 0).ToList();
        var details = whea.Take(40).Select(e => $"{T(e)} · WHEA event {e.Id} · level {(e.Level is 1 or 2 ? "error" : "warning/corrected")}").ToList();
        if (fatal.Count > 0)
            return R("stab-whea", HealthArea.Stability, "Hardware errors reported", HealthStatus.Problem, Severity.High,
                $"{fatal.Count} uncorrected hardware error(s) in {EventDays} days. Typical causes: unstable CPU/RAM overclock or XMP/EXPO, undervolting, heat or a failing component.", details)
                with { Uncertainty = "The event says which hardware unit reported it (see Event Viewer › System › WHEA-Logger); Money Tweaks shows the count and time." };
        if (corrected.Count > 0)
            return R("stab-whea", HealthArea.Stability, "Corrected hardware errors", HealthStatus.Warning, corrected.Count > 20 ? Severity.Medium : Severity.Low,
                $"The hardware corrected {corrected.Count} error(s) by itself in {EventDays} days. Occasional ones are harmless; many can mean an unstable overclock/XMP or a weak PCIe link.", details)
                with { Uncertainty = "PCIe corrected errors (event 17) are common with some GPUs and power-saving settings and usually don't affect games." };
        return R("stab-whea", HealthArea.Stability, "Hardware errors (WHEA)", HealthStatus.Passed, Severity.Info, $"No hardware errors logged in the last {EventDays} days.", details);
    }

    public static HealthResult AnalyzeDiskEvents(List<EventHit> disk)
    {
        string What(EventHit e) => (e.Provider, e.Id) switch
        {
            ("disk", 7) => "bad block found",
            ("disk", 11) => "controller error",
            ("disk", 51) => "error during a paging operation",
            ("disk", 153) => "I/O operation was retried",
            ("Ntfs", 55) => "file system structure is corrupt",
            _ => $"{e.Provider} error {e.Id}",
        };
        var details = disk.Take(40).Select(e => $"{T(e)} · {e.Provider} {e.Id}: {What(e)}{(e.Data.Count > 0 && e.Data[0].Length > 0 ? " · " + e.Data[0] : "")}").ToList();
        var bad = disk.Where(e => (e.Provider == "disk" && e.Id is 7 or 11) || e.Provider is "Ntfs" or "stornvme").ToList();
        const string unc = "USB sticks, card readers and external drives log these too; the details show which device (\\Device\\HarddiskN).";
        if (bad.Count > 0)
            return R("stab-disk", HealthArea.Stability, "Disk errors", HealthStatus.Problem, Severity.High,
                $"{bad.Count} disk or file-system error(s) in {EventDays} days. Back up important files and check the drive's health.", details) with { Uncertainty = unc };
        if (disk.Count > 0)
            return R("stab-disk", HealthArea.Stability, "Disk warnings", HealthStatus.Warning, Severity.Low,
                $"{disk.Count} retried or slow disk operation(s) in {EventDays} days. These can cause stutters while loading.", details) with { Uncertainty = unc };
        return R("stab-disk", HealthArea.Stability, "Disk errors", HealthStatus.Passed, Severity.Info, $"No disk or file-system errors in the last {EventDays} days.", details);
    }

    // ================================================================================================================
    // Stability: live load, throttling, memory, drives
    // ================================================================================================================

    public static IReadOnlyList<HealthResult> LoadAndThermal()
    {
        // CPU/GPU load over ~3 s (the same counters the live monitor uses) plus the busiest processes (scanner's sampler).
        double cpuSum = 0; double? gpuMax = null; double? temp = null; int n = 0;
        PerformanceCounter? limit = null;
        double? limitMin = null;
        try { limit = new PerformanceCounter("Processor Information", "% Performance Limit", "_Total"); limit.NextValue(); } catch { limit = null; }
        using (var mon = new HardwareMonitor())
        {
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1000);
                var s = mon.Sample();
                cpuSum += s.CpuPercent; n++;
                if (s.GpuPercent is { } g) gpuMax = Math.Max(gpuMax ?? 0, g);
                temp = s.CpuTempC ?? temp;
                try { if (limit is not null) limitMin = Math.Min(limitMin ?? 100, limit.NextValue()); } catch { }
            }
        }
        limit?.Dispose();
        var (_, busy) = OptimizationScanner.Processes();
        List<EventHit> fw = [];
        string? fwError = null;
        try { fw = QueryEvents("System", "Microsoft-Windows-Kernel-Processor-Power", [37]); } catch (Exception ex) { fwError = ex.Message; }
        var gpus = NvidiaSmi();
        return [AnalyzeLoad(cpuSum / Math.Max(1, n), gpuMax, busy), AnalyzeThermal(fw.Count, fw.FirstOrDefault()?.Time, limitMin, temp, gpus, fwError)];
    }

    /// <summary>GPU temperature and clock-limit reasons from nvidia-smi (installed with the NVIDIA driver). Empty if not present.</summary>
    public static List<GpuSensor> NvidiaSmi()
    {
        var exe = Path.Combine(ProcessRunner.System32, "nvidia-smi.exe");
        if (!File.Exists(exe)) return [];
        foreach (var field in new[] { "clocks_throttle_reasons.active", "clocks_event_reasons.active" })
        {
            try
            {
                var r = ProcessRunner.Run(exe, $"--query-gpu=name,temperature.gpu,utilization.gpu,{field} --format=csv,noheader,nounits", 10_000);
                if (!r.Ok) continue;
                return ParseNvidiaSmi(r.StdOut);
            }
            catch (Exception ex) { Log.Warn("nvidia-smi failed: " + ex.Message); return []; }
        }
        return [];
    }

    public static List<GpuSensor> ParseNvidiaSmi(string csv)
    {
        var list = new List<GpuSensor>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(',').Select(x => x.Trim()).ToArray();
            if (p.Length < 4) continue;
            double? D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            ulong? reasons = p[3].StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(p[3][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
            list.Add(new(p[0], D(p[1]), D(p[2]), reasons));
        }
        return list;
    }

    public static HealthResult AnalyzeLoad(double cpuAvg, double? gpuMax, List<(string name, double cpu)> busy)
    {
        var details = new List<string> { $"CPU average over 3 s: {cpuAvg:0}%", gpuMax is { } g ? $"GPU 3D peak: {g:0}%" : "GPU usage: not available from Windows counters" };
        details.AddRange(busy.Select(b => $"{b.name}: {b.cpu:0.#}% CPU"));
        const string unc = "Measured while this scan ran. If a game or render was running, high usage is expected.";
        if (cpuAvg >= 85 || gpuMax >= 90)
            return R("stab-load", HealthArea.Stability, "High CPU/GPU usage right now", HealthStatus.Warning, Severity.Low,
                $"{(cpuAvg >= 85 ? $"CPU averaged {cpuAvg:0}%" : "")}{(cpuAvg >= 85 && gpuMax >= 90 ? " and " : "")}{(gpuMax >= 90 ? $"GPU peaked at {gpuMax:0}%" : "")} during the scan." +
                (busy.Count > 0 ? $" Busiest: {string.Join(", ", busy.Take(3).Select(b => $"{b.name} {b.cpu:0}%"))}." : ""), details) with { Uncertainty = unc };
        return R("stab-load", HealthArea.Stability, "CPU/GPU usage", HealthStatus.Passed, Severity.Info,
            $"CPU {cpuAvg:0}%{(gpuMax is { } x ? $", GPU {x:0}%" : "")} during the scan; nothing in the background is hogging the system.", details) with { Uncertainty = unc };
    }

    public static HealthResult AnalyzeThermal(int firmwareLimitEvents, DateTime? lastLimit, double? perfLimitPct, double? cpuTempC, List<GpuSensor> gpus, string? eventError = null)
    {
        var details = new List<string>();
        var issues = new List<(string text, Severity sev)>();
        details.Add(cpuTempC is { } t ? $"CPU temperature (ACPI thermal zone): {t:0} °C" : "CPU temperature: not exposed by this PC's firmware (no kernel driver is installed to read the sensors)");
        if (cpuTempC >= 95) issues.Add(($"CPU is at {cpuTempC:0} °C", Severity.Medium));
        details.Add(perfLimitPct is { } pl ? $"CPU performance limit (Windows counter): {pl:0}% (100% = not limited)" : "CPU performance limit counter: not available");
        if (perfLimitPct < 80) issues.Add(($"CPU is limited to {perfLimitPct:0}% of its normal performance right now", Severity.Low));
        details.Add(eventError is null ? $"Firmware speed-limit events (Kernel-Processor-Power 37) in {EventDays} days: {firmwareLimitEvents}{(lastLimit is { } l ? ", last " + l.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")}" : "Firmware speed-limit events: couldn't read (" + eventError + ")");
        if (firmwareLimitEvents > 0) issues.Add(($"the firmware limited CPU speed {firmwareLimitEvents} time(s) in {EventDays} days", Severity.Low));
        foreach (var g in gpus)
        {
            var r = g.Reasons ?? 0;
            var flags = new List<string>();
            if ((r & 0x20) != 0) flags.Add("software thermal slowdown");
            if ((r & 0x40) != 0) flags.Add("hardware thermal slowdown");
            if ((r & 0x08) != 0) flags.Add("hardware slowdown");
            if ((r & 0x80) != 0) flags.Add("power-brake slowdown");
            if ((r & 0x04) != 0) flags.Add("power limit");
            details.Add($"{g.Name}: {(g.TempC is { } gt ? gt + " °C" : "temperature n/a")}, {(g.UtilPercent is { } u ? u + "% busy" : "usage n/a")}, clock limits: {(flags.Count == 0 ? "none" : string.Join(", ", flags))}");
            if ((r & 0x60) != 0) issues.Add(($"{g.Name} is thermally throttling", Severity.Medium));
            else if ((r & 0x88) != 0) issues.Add(($"{g.Name} reports a hardware slowdown", Severity.Medium));
            if (g.TempC >= 87) issues.Add(($"{g.Name} is at {g.TempC:0} °C", Severity.Low));
        }
        if (gpus.Count == 0) details.Add("GPU temperature: only available here for NVIDIA GPUs (through nvidia-smi).");
        const string unc = "Throttling mostly shows under load. This check reads the sensors at scan time; run it while a game is open (alt-tab) or use a sensor tool such as HWiNFO for full logs.";
        bool anyData = cpuTempC is not null || perfLimitPct is not null || gpus.Count > 0 || eventError is null;
        if (issues.Count > 0)
            return R("stab-thermal", HealthArea.Stability, "Thermal or power throttling", HealthStatus.Warning, issues.Max(i => i.sev),
                char.ToUpperInvariant(issues[0].text[0]) + string.Join("; ", issues.Select(i => i.text))[1..] + ". Check cooling, dust and power settings.", details) with { Uncertainty = unc };
        if (!anyData)
            return R("stab-thermal", HealthArea.Stability, "Thermal throttling", HealthStatus.Unknown, Severity.Info, "No temperature or throttling data is available on this PC without a kernel driver.", details) with { Uncertainty = unc };
        return R("stab-thermal", HealthArea.Stability, "Thermal throttling", HealthStatus.Passed, Severity.Info, "No throttling seen in the readings available.", details) with { Uncertainty = unc };
    }

    public static IReadOnlyList<HealthResult> Memory()
    {
        var (total, avail, load) = MemoryInfo.Status();
        var (commitTotal, commitAvail) = MemoryInfo.Commit();
        bool? auto = null; double? pagefileMb = null;
        try { foreach (var o in Wmi("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem")) auto = o["AutomaticManagedPagefile"] as bool?; } catch { }
        try { var pf = Wmi("SELECT AllocatedBaseSize FROM Win32_PageFileUsage"); pagefileMb = pf.Sum(o => Convert.ToDouble(o["AllocatedBaseSize"], CultureInfo.InvariantCulture)); } catch { }
        int exhaustion = 0; DateTime? lastEx = null;
        try { var ev = QueryEvents("System", "Microsoft-Windows-Resource-Exhaustion-Detector", [2004]); exhaustion = ev.Count; lastEx = ev.FirstOrDefault()?.Time; } catch { }
        return [AnalyzeMemory(total / 1073741824.0, load, commitTotal == 0 ? null : 100.0 * (commitTotal - commitAvail) / commitTotal, commitTotal / 1073741824.0, pagefileMb, auto, exhaustion, lastEx)];
    }

    public static HealthResult AnalyzeMemory(double totalGb, double loadPct, double? commitPct, double commitLimitGb, double? pagefileMb, bool? autoPagefile, int exhaustionEvents, DateTime? lastExhaustion)
    {
        var details = new List<string>
        {
            $"Physical memory: {totalGb:0.#} GB, {loadPct:0}% in use",
            commitPct is { } c ? $"Commit charge: {c:0}% of the {commitLimitGb:0.#} GB limit (RAM + page file)" : "Commit charge: unknown",
            pagefileMb is { } pf ? (pf == 0 ? "Page file: none" : $"Page file: {pf / 1024:0.#} GB{(autoPagefile == true ? " (managed by Windows)" : "")}") : "Page file: unknown",
            $"Low-memory warnings (Resource-Exhaustion-Detector 2004) in {EventDays} days: {exhaustionEvents}{(lastExhaustion is { } l ? ", last " + l.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")}",
        };
        if (commitPct >= 90 || loadPct >= 92)
            return R("stab-memory", HealthArea.Stability, "Memory almost full", HealthStatus.Problem, Severity.High,
                $"{(commitPct >= 90 ? $"Commit charge is at {commitPct:0}%" : $"RAM is {loadPct:0}% used")}: games can stutter or crash with out-of-memory errors. Close memory-heavy apps.", details);
        var warn = new List<string>();
        if (loadPct >= 80) warn.Add($"RAM is {loadPct:0}% used");
        if (commitPct >= 80) warn.Add($"commit charge is at {commitPct:0}%, close to the limit where programs fail to get memory (a bigger or Windows-managed page file raises it)");
        if (exhaustionEvents > 0) warn.Add($"Windows warned about low memory {exhaustionEvents} time(s) in {EventDays} days");
        if (pagefileMb == 0) warn.Add("there is no page file, so programs crash instead of slowing down when RAM runs out");
        if (totalGb < 11) warn.Add($"{totalGb:0} GB of RAM is below what most current games recommend (16 GB)");
        if (warn.Count > 0)
            return R("stab-memory", HealthArea.Stability, "Memory pressure", HealthStatus.Warning, exhaustionEvents > 0 || pagefileMb == 0 ? Severity.Medium : Severity.Low,
                char.ToUpperInvariant(warn[0][0]) + string.Join("; ", warn)[1..] + ".", details)
                with { Uncertainty = "RAM use is measured at scan time; Windows also uses free RAM for cache, which is normal." };
        return R("stab-memory", HealthArea.Stability, "Memory", HealthStatus.Passed, Severity.Info, $"{loadPct:0}% of {totalGb:0.#} GB RAM in use; no low-memory warnings.", details);
    }

    public static IReadOnlyList<HealthResult> Disks()
    {
        var sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var drives = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                drives.Add(new(d.Name, d.VolumeLabel, d.AvailableFreeSpace / 1073741824.0, d.TotalSize / 1073741824.0, string.Equals(d.Name, sysRoot, StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* locked or removed */ }
        }
        var list = new List<HealthResult> { AnalyzeDisks(drives) };
        // Physical disk health from the Storage Management API (same source as Settings › Storage › Disks).
        try
        {
            var disks = Wmi("SELECT FriendlyName, HealthStatus, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage")
                .Select(o => (name: Str(o, "FriendlyName"), health: Convert.ToInt32(o["HealthStatus"], CultureInfo.InvariantCulture), media: Str(o, "MediaType"), bus: Str(o, "BusType"))).ToList();
            list.Add(AnalyzeDriveHealth(disks));
        }
        catch (Exception ex)
        {
            list.Add(R("stab-drive-health", HealthArea.Stability, "Drive health", HealthStatus.Unknown, Severity.Info, "Windows' storage health data isn't available: " + ex.Message, []));
        }
        return list;
    }

    public static HealthResult AnalyzeDisks(List<DriveSpace> drives)
    {
        var details = drives.Select(d => $"{d.Name} {d.Label}: {d.FreeGb:0.#} GB free of {d.TotalGb:0} GB ({(d.TotalGb > 0 ? 100 * d.FreeGb / d.TotalGb : 0):0}%){(d.IsSystem ? " · Windows drive" : "")}").ToList();
        if (drives.Count == 0)
            return R("stab-space", HealthArea.Stability, "Free disk space", HealthStatus.Unknown, Severity.Info, "No fixed drives could be read.", details);
        double Pct(DriveSpace d) => d.TotalGb > 0 ? 100 * d.FreeGb / d.TotalGb : 100;
        var sys = drives.FirstOrDefault(d => d.IsSystem);
        if (sys is not null && (sys.FreeGb < 5 || Pct(sys) < 5))
            return R("stab-space", HealthArea.Stability, "Windows drive almost full", HealthStatus.Problem, Severity.High,
                $"Only {sys.FreeGb:0.#} GB is free on {sys.Name}. Updates fail, shader caches can't grow and games stutter or crash. Free up space (Settings › Storage).", details)
                with { Links = [new("Open Storage settings", "ms-settings:storagesense")] };
        var low = drives.Where(d => d.IsSystem ? d.FreeGb < 15 || Pct(d) < 10 : Pct(d) < 5 || d.FreeGb < 5).ToList();
        if (low.Count > 0)
            return R("stab-space", HealthArea.Stability, "Low disk space", HealthStatus.Warning, Severity.Low,
                string.Join("; ", low.Select(d => $"{d.Name} has {d.FreeGb:0.#} GB free")) + ". Game updates and shader caches need room.", details)
                with { Links = [new("Open Storage settings", "ms-settings:storagesense")] };
        return R("stab-space", HealthArea.Stability, "Free disk space", HealthStatus.Passed, Severity.Info, "All drives have enough free space.", details);
    }

    public static HealthResult AnalyzeDriveHealth(List<(string name, int health, string media, string bus)> disks)
    {
        string H(int h) => h switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
        string M(string m) => m switch { "3" => "HDD", "4" => "SSD", "5" => "SCM", _ => "unspecified media" };
        var details = disks.Select(d => $"{d.name}: {H(d.health)} · {M(d.media)}").ToList();
        if (disks.Count == 0)
            return R("stab-drive-health", HealthArea.Stability, "Drive health", HealthStatus.Unknown, Severity.Info, "Windows reported no physical disks.", details);
        var bad = disks.Where(d => d.health == 2).ToList();
        var warn = disks.Where(d => d.health == 1).ToList();
        if (bad.Count > 0)
            return R("stab-drive-health", HealthArea.Stability, "Drive failing", HealthStatus.Problem, Severity.High,
                $"Windows reports {string.Join(", ", bad.Select(b => b.name))} as Unhealthy. Back up your files now and plan to replace the drive.", details);
        if (warn.Count > 0)
            return R("stab-drive-health", HealthArea.Stability, "Drive health warning", HealthStatus.Warning, Severity.Medium,
                $"Windows reports a health warning for {string.Join(", ", warn.Select(b => b.name))} (often worn SSD cells or SMART warnings). Back up important files.", details);
        return R("stab-drive-health", HealthArea.Stability, "Drive health", HealthStatus.Passed, Severity.Info, $"Windows reports all {disks.Count} drive(s) as Healthy.", details)
            with { Uncertainty = "Based on the drives' own SMART/health reporting through Windows; drives can still fail without warning." };
    }
}
