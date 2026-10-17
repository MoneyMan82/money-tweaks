using System.Globalization;
using System.Text.RegularExpressions;

namespace MoneyTweaks.Core.Sys;

/// <summary>
/// Money Tweaks version numbers are decimals with two places: an update adds 0.1 (1.23 → 1.33), a minor update adds
/// 0.01 (1.23 → 1.24), and 1.99 → 2.00. Minor is always 0–99 and shown with two digits ("1.30", never "1.3"), so version
/// comparison orders them correctly. Tags and file versions keep a third part of 0 (v1.24.0): copies of Money Tweaks up to
/// 1.22.1 format the release version with three parts and would fail on a two-part tag, so this keeps them able to update.
/// Older releases with a real patch number (1.22.1) still display as before.
/// </summary>
public static partial class AppVersioning
{
    public enum Kind { Update, Minor }

    [GeneratedRegex(@"^\d+\.\d{2}(\.0)?$")]
    private static partial Regex Decimal();
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex Legacy();

    /// <summary>"1.24" in the decimal scheme; an older release with a patch number keeps it ("1.22.1").</summary>
    public static string Display(Version v) =>
        v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>Git tag and build version: v1.24.0 (the trailing .0 keeps older installs able to update).</summary>
    public static string Tag(Version v) => "v" + Full(v);

    public static string Full(Version v) => $"{v.Major}.{v.Minor.ToString("00", CultureInfo.InvariantCulture)}.0";

    /// <summary>Next version: Update = +0.1, Minor = +0.01 (a legacy patch number is dropped).</summary>
    public static Version Next(Version current, Kind kind)
    {
        int hundredths = current.Major * 100 + Math.Clamp(current.Minor, 0, 99) + (kind == Kind.Update ? 10 : 1);
        return new Version(hundredths / 100, hundredths % 100, 0);
    }

    /// <summary>A version a release may use: 1.24 / 1.24.0 (decimal scheme) or 1.22.1 (older releases).</summary>
    public static bool IsValid(string version) => Decimal().IsMatch(version) || Legacy().IsMatch(version);

    public static Version Parse(string version) => Version.Parse(version.TrimStart('v', 'V'));
}
