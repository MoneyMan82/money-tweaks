using System.Management;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Health;

/// <summary>What a repair does, its risk, and what it needs. The UI shows all of this in the confirmation.</summary>
public sealed record RepairInfo(string Id, string Title, string What, string Risk, bool NeedsAdmin, bool MayNeedRestart, string Rollback, string Duration);

/// <summary>
/// The only changes the Health Center can make, each one started by the user after a confirmation. Never touches drivers,
/// BIOS, security settings or anything not listed here. The service repair backs up the previous start types and can roll back.
/// </summary>
public static class SafeRepairs
{
    public const string SfcScanNow = "sfc-scannow", DismRestore = "dism-restore", UpdateServices = "wu-services", PnpRescan = "pnp-rescan";

    public static readonly IReadOnlyList<RepairInfo> All =
    [
        new(DismRestore, "Repair the Windows component store (DISM RestoreHealth)",
            "Checks the store Windows repairs itself from and downloads good copies of damaged parts from Windows Update.",
            "Low. Uses Microsoft's own files; doesn't touch your apps, games or settings. Can't be cancelled safely once started.",
            true, false, "Not reversible (it only replaces damaged Windows files with Microsoft's originals). A restore point is created first when possible.", "10–30 minutes, needs internet"),
        new(SfcScanNow, "Repair system files (sfc /scannow)",
            "Checks every protected Windows file and replaces damaged ones with the good copies from the component store.",
            "Low. Only Windows system files are replaced, with Microsoft's own copies. Can't be cancelled safely once started.",
            true, true, "Not reversible (files are restored to Microsoft's originals). A restore point is created first when possible.", "5–20 minutes"),
        new(UpdateServices, "Re-enable Windows Update services",
            "Sets disabled update services (Windows Update, BITS, Update Orchestrator, Cryptographic Services, Windows Modules Installer) back to their Windows default start type.",
            "Low. Windows will be able to download and install updates again, including restarts it schedules.",
            true, false, "Reversible: the previous start types are saved and \"Roll back\" puts them back.", "a few seconds"),
        new(PnpRescan, "Scan for hardware changes",
            "Asks Windows to re-detect devices (same as Device Manager › Action › Scan for hardware changes). Windows may then install a driver it already has.",
            "Very low. Nothing is uninstalled or downloaded by Money Tweaks.",
            true, false, "Nothing to roll back.", "a few seconds"),
    ];

    public static RepairInfo Get(string id) => All.First(r => r.Id == id);

    private static string ToSc(string wmiMode) => wmiMode.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "disabled" => "disabled",
        _ => "demand",
    };

    private static Dictionary<string, string> CurrentModes()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var s = new ManagementObjectSearcher("SELECT Name, StartMode FROM Win32_Service WHERE Name='wuauserv' OR Name='BITS' OR Name='UsoSvc' OR Name='CryptSvc' OR Name='TrustedInstaller'");
        foreach (var o in s.Get()) d[o["Name"]?.ToString() ?? ""] = o["StartMode"]?.ToString() ?? "";
        return d;
    }

    private static bool SetMode(string service, string wmiMode, List<string> log)
    {
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "sc.exe"), $"config {service} start= {ToSc(wmiMode)}", 30_000);
        var now = CurrentModes().GetValueOrDefault(service, "");
        bool ok = r.Ok && now.Equals(wmiMode, StringComparison.OrdinalIgnoreCase);
        log.Add($"{service}: set {wmiMode} → sc exit {r.ExitCode}, now {now}{(ok ? "" : " (not applied)")}");
        return ok;
    }

    /// <summary>Re-enables disabled update services. Backs up the old start types first; rolls back if any change doesn't stick.</summary>
    public static (bool ok, string message, List<string> log) EnableUpdateServices(HealthState state)
    {
        var log = new List<string>();
        if (!Admin.IsElevated) return (false, "Needs administrator rights.", log);
        var before = CurrentModes();
        var todo = HealthChecks.UpdateServiceList.Where(s => before.TryGetValue(s.Name, out var m) && m.Equals("Disabled", StringComparison.OrdinalIgnoreCase)).ToList();
        if (todo.Count == 0) return (true, "No update service is disabled; nothing changed.", log);
        foreach (var s in todo) state.ServiceBackup.TryAdd(s.Name, before[s.Name]); // keep the oldest original
        state.ServiceBackupAt ??= DateTime.Now;
        state.Save(); // backup on disk before anything changes
        var done = new List<string>();
        foreach (var s in todo)
        {
            if (SetMode(s.Name, s.DefaultMode, log)) { done.Add(s.Name); continue; }
            foreach (var d in done) SetMode(d, before[d], log); // all or nothing
            Log.Warn("Update service repair rolled back: " + string.Join(" | ", log));
            return (false, $"Windows didn't accept the change for {s.Display}; everything was put back as it was.", log);
        }
        Log.Info("Update services re-enabled: " + string.Join(" | ", log));
        return (true, $"Re-enabled {string.Join(", ", todo.Select(t => t.Display))} (verified). Use Roll back to restore the previous start types.", log);
    }

    public static (bool ok, string message, List<string> log) RollbackUpdateServices(HealthState state)
    {
        var log = new List<string>();
        if (!Admin.IsElevated) return (false, "Needs administrator rights.", log);
        if (state.ServiceBackup.Count == 0) return (true, "There is no service backup to roll back.", log);
        var failed = state.ServiceBackup.Where(kv => !SetMode(kv.Key, kv.Value, log)).Select(kv => kv.Key).ToList();
        if (failed.Count > 0) return (false, "Couldn't restore: " + string.Join(", ", failed), log);
        state.ServiceBackup.Clear();
        state.ServiceBackupAt = null;
        state.Save();
        Log.Info("Update services rolled back: " + string.Join(" | ", log));
        return (true, "Previous service start types restored (verified).", log);
    }

    public static (bool ok, string message) RescanDevices()
    {
        if (!Admin.IsElevated) return (false, "Needs administrator rights.");
        if (OsInfo.Build < 19041) return (false, "Needs Windows 10 version 2004 or newer. Use Device Manager › Action › Scan for hardware changes instead.");
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "pnputil.exe"), "/scan-devices", 120_000);
        Log.Info($"pnputil /scan-devices -> {r.ExitCode}");
        return r.Ok ? (true, "Windows re-scanned the hardware. Run the health scan again to see if device errors are gone.") : (false, $"pnputil returned {r.ExitCode}: {r.Combined}");
    }
}
