using System.Text.Json;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Health;

/// <summary>Outcome of one health check. Unknown means "Unable to check" (no data, no permission, service unreachable).</summary>
public enum HealthStatus { Passed, Warning, Problem, Unknown }

public enum HealthArea { Drivers, Windows, Stability }

public sealed record HealthLink(string Label, string Target)
{
    /// <summary>Windows tools the checks link to. Anything else (a planted health.json entry) is never started.</summary>
    private static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase) { "devmgmt.msc", "perfmon.exe /rel", "mdsched.exe", "SystemPropertiesAdvanced.exe" };

    /// <summary>HTTPS pages, ms-settings: pages and the known Windows tools only.</summary>
    public static bool IsAllowedTarget(string? target) =>
        target is not null && (Tools.Contains(target.Trim())
            || target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) && !target.Contains(' ')
            || Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// One health check result. Details are the real values read from this PC (shown as technical details);
/// Uncertainty says what the check can't know. RepairId names a <see cref="SafeRepairs"/> action this result offers.
/// </summary>
public sealed record HealthResult(string Id, HealthArea Area, string Title, HealthStatus Status, Severity Severity, string Summary,
    IReadOnlyList<string> Details, DateTime CheckedAt)
{
    public string? Uncertainty { get; init; }
    public IReadOnlyList<HealthLink> Links { get; init; } = [];
    public string? RepairId { get; init; }

    public static string Label(HealthStatus s) => s switch
    {
        HealthStatus.Passed => "Passed",
        HealthStatus.Warning => "Warning",
        HealthStatus.Problem => "Problem detected",
        _ => "Unable to check",
    };
}

/// <summary>Result of an SFC or DISM run, kept so the page can show the last outcome with its time.</summary>
public sealed record FileCheckOutcome(string Tool, HealthStatus Status, string Summary, IReadOnlyList<string> Details, DateTime Finished, double Minutes, int ExitCode);

/// <summary>Last scan results, last SFC/DISM outcomes and service start-type backups, in health.json.</summary>
public sealed class HealthState
{
    public DateTime? LastScan { get; set; }
    public List<HealthResult> Results { get; set; } = [];
    public Dictionary<string, FileCheckOutcome> FileChecks { get; set; } = [];
    /// <summary>Service name → start mode before Money Tweaks changed it (for rollback).</summary>
    public Dictionary<string, string> ServiceBackup { get; set; } = [];
    public DateTime? ServiceBackupAt { get; set; }

    internal static string PathFor => Path.Combine(AppPaths.Root, "health.json");

    public static HealthState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<HealthState>(File.ReadAllText(PathFor)) ?? new(); }
        catch (Exception ex)
        {
            // Keep the damaged file (it may hold the only backup of original values) before the next Save replaces it.
            Log.Warn("health.json unreadable (kept as .bad): " + ex.Message);
            try { File.Copy(PathFor, PathFor + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: false); } catch { }
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        AtomicFile.WriteAllText(PathFor, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Worst status across results (Unknown never outranks a real Warning/Problem).</summary>
    public static HealthStatus Overall(IEnumerable<HealthResult> results)
    {
        var list = results.ToList();
        if (list.Any(r => r.Status == HealthStatus.Problem)) return HealthStatus.Problem;
        if (list.Any(r => r.Status == HealthStatus.Warning)) return HealthStatus.Warning;
        if (list.Count > 0 && list.All(r => r.Status == HealthStatus.Unknown)) return HealthStatus.Unknown;
        return HealthStatus.Passed;
    }
}
