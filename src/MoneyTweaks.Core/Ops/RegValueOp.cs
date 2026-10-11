using System.Globalization;
using Microsoft.Win32;

namespace MoneyTweaks.Core.Ops;

public enum Hive { HKCU, HKLM }

/// <summary>Sets one registry value. Backup records whether the value (and key) existed and its old data.</summary>
public sealed class RegValueOp(Hive hive, string path, string name, RegistryValueKind kind, object value) : TweakOp
{
    public Hive Hive { get; } = hive;
    public string Path { get; } = path;
    public string Name { get; } = name;
    public RegistryValueKind ValueKind { get; } = kind;
    public object Value { get; } = value;
    /// <summary>If the key did not exist before Apply, delete the whole key on Undo (needed for marker keys).</summary>
    public bool RemoveKeyIfCreated { get; init; }
    /// <summary>Value written on Undo when no backup exists. Null = delete the value.</summary>
    public object? DefaultValue { get; init; }

    public override bool RequiresAdmin => Hive == Hive.HKLM;

    public override string Describe() =>
        $@"{Hive}\{Path}\{(Name.Length == 0 ? "(Default)" : Name)} = {FormatValue(Value, ValueKind)} ({ValueKind})";

    internal RegistryKey Root => Hive == Hive.HKLM ? Registry.LocalMachine : Registry.CurrentUser;

    public override OpState Check()
    {
        using var key = Root.OpenSubKey(Path);
        if (key is null) return OpState.NotApplied;
        var cur = key.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (cur is null) return OpState.NotApplied;
        return ValuesEqual(cur, Value) ? OpState.Applied : OpState.NotApplied;
    }

    public override OpBackup Capture()
    {
        var b = new OpBackup { Kind = "reg" };
        using var key = Root.OpenSubKey(Path);
        b.Data["keyExisted"] = (key is not null).ToString();
        var cur = key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (cur is not null && key is not null)
        {
            var k = key.GetValueKind(Name);
            b.Data["kind"] = k.ToString();
            b.Data["value"] = FormatValue(cur, k);
        }
        return b;
    }

    public override void Apply()
    {
        try
        {
            using var key = Root.CreateSubKey(Path, writable: true)
                ?? throw new TweakOpException($"Cannot open {Hive}\\{Path}");
            key.SetValue(Name, ConvertValue(Value, ValueKind), ValueKind);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new TweakOpException($"Access denied writing {Hive}\\{Path}\\{Name}. {ex.Message}", ex);
        }
    }

    public override void Undo(OpBackup? backup)
    {
        try
        {
            if (backup is not null && backup.Data.TryGetValue("kind", out var kindStr) && kindStr is not null)
            {
                var k = Enum.Parse<RegistryValueKind>(kindStr);
                using var key = Root.CreateSubKey(Path, writable: true);
                key.SetValue(Name, ParseValue(backup.Data["value"] ?? "", k), k);
                return;
            }
            bool keyExisted = backup is null || !backup.Data.TryGetValue("keyExisted", out var ke) || ke != "False";
            if (RemoveKeyIfCreated && !keyExisted)
            {
                Root.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
                return;
            }
            if (backup is null && DefaultValue is not null)
            {
                using var key = Root.CreateSubKey(Path, writable: true);
                key.SetValue(Name, ConvertValue(DefaultValue, ValueKind), ValueKind);
                return;
            }
            using var k2 = Root.OpenSubKey(Path, writable: true);
            k2?.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new TweakOpException($"Access denied restoring {Hive}\\{Path}\\{Name}. {ex.Message}", ex);
        }
    }

    internal static object ConvertValue(object v, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => v is uint u ? unchecked((int)u) : Convert.ToInt32(v, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        RegistryValueKind.String or RegistryValueKind.ExpandString => v.ToString() ?? "",
        RegistryValueKind.Binary => v is byte[] b ? b : Convert.FromHexString(v.ToString()!),
        RegistryValueKind.MultiString => v is string[] s ? s : (v.ToString() ?? "").Split('\0'),
        _ => v
    };

    internal static bool ValuesEqual(object current, object target)
    {
        switch (current)
        {
            case int i: return target switch { uint u => unchecked((int)u) == i, _ => Convert.ToInt64(target, CultureInfo.InvariantCulture) == i };
            case long l: return Convert.ToInt64(target, CultureInfo.InvariantCulture) == l;
            case byte[] b: return target is byte[] tb ? b.AsSpan().SequenceEqual(tb) : Convert.ToHexString(b) == target.ToString();
            case string[] arr: return target is string[] ta && arr.SequenceEqual(ta);
            default: return string.Equals(current.ToString(), target.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string FormatValue(object v, RegistryValueKind kind) => v switch
    {
        byte[] b => Convert.ToHexString(b),
        string[] arr => string.Join('\0', arr),
        int i when kind == RegistryValueKind.DWord => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        uint u => u.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    internal static object ParseValue(string s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(s, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromHexString(s),
        RegistryValueKind.MultiString => s.Split('\0'),
        _ => s
    };
}
