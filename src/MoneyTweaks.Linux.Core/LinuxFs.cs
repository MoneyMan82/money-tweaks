using System.Text.RegularExpressions;

namespace MoneyTweaks.Linux;

/// <summary>
/// Read access to /proc and /sys. <see cref="Root"/> is "/" on a real system; tests point it at a folder that mirrors the
/// layout, so every parser runs against the same file formats the kernel produces.
/// </summary>
public sealed class LinuxFs(string root = "/")
{
    public string Root { get; } = root;

    public string Map(string abs) => Root == "/" ? abs : Path.Combine(Root, abs.TrimStart('/'));

    public string? Read(string abs)
    {
        try { return File.ReadAllText(Map(abs)); } catch { return null; }
    }

    public string? ReadLine(string abs) => Read(abs)?.Trim();

    public long? ReadLong(string abs) => long.TryParse(ReadLine(abs), out var v) ? v : null;

    public bool Exists(string abs) => File.Exists(Map(abs)) || Directory.Exists(Map(abs));

    /// <summary>Absolute paths of the entries in <paramref name="dirAbs"/> whose names match <paramref name="namePattern"/>.</summary>
    public IEnumerable<string> List(string dirAbs, string namePattern)
    {
        string dir = Map(dirAbs);
        if (!Directory.Exists(dir)) return [];
        var rx = new Regex("^" + namePattern + "$");
        return Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n is not null && rx.IsMatch(n))
            .Select(n => dirAbs.TrimEnd('/') + "/" + n).OrderBy(p => p, NaturalOrder.Instance).ToList();
    }
}

/// <summary>cpu2 before cpu10.</summary>
internal sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();
    public int Compare(string? a, string? b)
    {
        var ka = Key(a ?? ""); var kb = Key(b ?? "");
        int c = string.CompareOrdinal(ka.prefix, kb.prefix);
        return c != 0 ? c : ka.n.CompareTo(kb.n);
    }
    private static (string prefix, long n) Key(string s)
    {
        var m = Regex.Match(s, @"^(.*?)(\d+)(\D*)$");
        return m.Success ? (m.Groups[1].Value + "#" + m.Groups[3].Value, long.Parse(m.Groups[2].Value)) : (s, -1);
    }
}
