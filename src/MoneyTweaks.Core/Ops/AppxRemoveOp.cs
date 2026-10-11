using MoneyTweaks.Core.Sys;
using Windows.Management.Deployment;

namespace MoneyTweaks.Core.Ops;

/// <summary>
/// Removes a Microsoft Store (AppX/MSIX) package for the current user. Undo re-registers the package when Windows
/// still has its files (provisioned apps); otherwise the app must be reinstalled from the Microsoft Store.
/// </summary>
public sealed class AppxRemoveOp(string packageName) : TweakOp
{
    public string PackageName { get; } = packageName;
    public override bool RequiresAdmin => false;
    public override string Describe() => $"Remove Store app package {PackageName} (current user)";

    private static readonly Lock CacheGate = new();
    private static HashSet<string>? _installed;
    private static DateTime _cachedAt;

    /// <summary>Names of packages installed for the current user, cached for a few seconds.</summary>
    public static HashSet<string> InstalledNames()
    {
        lock (CacheGate)
        {
            if (_installed is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(10)) return _installed;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in new PackageManager().FindPackagesForUser(string.Empty))
                    set.Add(p.Id.Name);
            }
            catch (Exception ex) { Log.Warn($"Could not enumerate Store apps: {ex.Message}"); }
            _installed = set;
            _cachedAt = DateTime.UtcNow;
            return set;
        }
    }

    public static void InvalidateCache() { lock (CacheGate) _installed = null; }

    // Not installed: NotApplicable. The engine reports Applied instead when Money Tweaks removed it (a backup exists).
    public override OpState Check() => InstalledNames().Contains(PackageName) ? OpState.NotApplied : OpState.NotApplicable;

    public override OpBackup Capture() => new() { Kind = "appx", Data = { ["installed"] = InstalledNames().Contains(PackageName).ToString() } };

    public override void Apply()
    {
        if (!InstalledNames().Contains(PackageName)) return;
        var r = ProcessRunner.PowerShell($"Get-AppxPackage -Name '{Escape(PackageName)}' | Remove-AppxPackage");
        InvalidateCache();
        if (!r.Ok) throw new TweakOpException($"Windows refused to remove {PackageName}: {r.Combined}");
    }

    public override void Undo(OpBackup? backup)
    {
        if (backup?.Data.GetValueOrDefault("installed") == "False") return; // wasn't installed before us
        var n = Escape(PackageName);
        var script =
            $"$p = Get-AppxPackage -AllUsers -Name '{n}' -ErrorAction SilentlyContinue | Select-Object -First 1;" +
            "if (-not $p) { $pp = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object DisplayName -eq '" + n + "' | Select-Object -First 1;" +
            "  if ($pp) { $m = Join-Path $env:ProgramFiles ('WindowsApps\\' + $pp.PackageName + '\\AppxManifest.xml'); if (Test-Path $m) { Add-AppxPackage -DisableDevelopmentMode -Register $m; exit 0 } } ;" +
            "  Write-Error 'Package files are gone. Reinstall it from the Microsoft Store.' }" +
            "else { Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $p.InstallLocation 'AppxManifest.xml') }";
        var r = ProcessRunner.PowerShell(script);
        InvalidateCache();
        if (!r.Ok) throw new TweakOpException($"Could not restore {PackageName} automatically: {r.Combined}");
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
