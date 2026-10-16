using Microsoft.Win32;

namespace MoneyTweaks.Core.Ops;

/// <summary>
/// Sets one "Key=Value;" entry inside a REG_SZ that holds several such entries (e.g. DirectX's
/// DirectXUserGlobalSettings = "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;"). Other entries are left untouched;
/// Undo restores the exact original string.
/// </summary>
public sealed class RegStringFlagOp(Hive hive, string path, string name, string flag, string value) : TweakOp
{
    public Hive Hive { get; } = hive;
    public string Path { get; } = path;
    public string Name { get; } = name;
    public string Flag { get; } = flag;
    public string Value { get; } = value;
    private RegistryKey Root => Hive == Hive.HKLM ? Registry.LocalMachine : Registry.CurrentUser;

    public override bool RequiresAdmin => Hive == Hive.HKLM;
    public override string Describe() => $@"{Hive}\{Path}\{Name}: {Flag}={Value}; (other entries kept)";

    private string? Read()
    {
        using var k = Root.OpenSubKey(Path);
        return k?.GetValue(Name) as string;
    }

    public static Dictionary<string, string> Parse(string? s) =>
        (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0].Length > 0)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces or adds <paramref name="flag"/> while keeping the order and every other entry.</summary>
    public static string With(string? s, string flag, string? value)
    {
        var parts = (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int i = parts.FindIndex(p => p.Split('=', 2)[0].Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (value is null) { if (i >= 0) parts.RemoveAt(i); }
        else if (i >= 0) parts[i] = $"{flag}={value}";
        else parts.Add($"{flag}={value}");
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }

    public override OpState Check() => Parse(Read()).TryGetValue(Flag, out var v) && v == Value ? OpState.Applied : OpState.NotApplied;

    public override OpBackup Capture()
    {
        var b = new OpBackup { Kind = "regflag" };
        if (Read() is { } s) b.Data["value"] = s;
        return b;
    }

    public override void Apply()
    {
        try
        {
            using var k = Root.CreateSubKey(Path, writable: true) ?? throw new TweakOpException($"Cannot open {Hive}\\{Path}");
            k.SetValue(Name, With(k.GetValue(Name) as string, Flag, Value), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { throw new TweakOpException($"Access denied writing {Hive}\\{Path}\\{Name}. {ex.Message}", ex); }
    }

    public override void Undo(OpBackup? backup)
    {
        using var k = Root.OpenSubKey(Path, writable: true);
        if (k is null) return;
        if (backup?.Data.GetValueOrDefault("value") is { } original) { k.SetValue(Name, original, RegistryValueKind.String); return; }
        // No original string: remove only our entry, and the value if nothing else is left.
        var rest = With(k.GetValue(Name) as string, Flag, null);
        if (rest.Length == 0) k.DeleteValue(Name, throwOnMissingValue: false);
        else k.SetValue(Name, rest, RegistryValueKind.String);
    }
}
