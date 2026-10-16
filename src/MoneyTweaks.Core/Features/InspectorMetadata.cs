using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace MoneyTweaks.Core.Features;

/// <summary>A setting as NVIDIA Profile Inspector names it, with its named values.</summary>
public sealed record InspectorSetting(uint Id, string Name, IReadOnlyDictionary<uint, string> Values, string Source);

/// <summary>
/// Reads NVIDIA Profile Inspector's own setting metadata (read-only): the CustomSettingNames.xml embedded in
/// nvidiaProfileInspector.exe (read through the .NET resource table, not by patching or running it) and the Reference.xml
/// next to it. Used to cross-check that a setting ID means what the preset says.
/// </summary>
public static class InspectorMetadata
{
    public static Dictionary<uint, InspectorSetting> Load(string inspectorExe)
    {
        var all = new Dictionary<uint, InspectorSetting>();
        var embedded = EmbeddedResource(inspectorExe, "CustomSettingNames.xml");
        if (embedded is not null) Parse(embedded, "embedded CustomSettingNames.xml", all);
        var reference = Path.Combine(Path.GetDirectoryName(inspectorExe) ?? "", "Reference.xml");
        if (File.Exists(reference)) Parse(File.ReadAllBytes(reference), "Reference.xml", all);
        return all;
    }

    private static void Parse(byte[] xml, string source, Dictionary<uint, InspectorSetting> into)
    {
        using var ms = new MemoryStream(xml);
        foreach (var cs in XDocument.Load(ms).Descendants("CustomSetting"))
        {
            if (!TryHex((string?)cs.Element("HexSettingID"), out var id) || into.ContainsKey(id)) continue;
            var values = new Dictionary<uint, string>();
            foreach (var v in cs.Descendants("CustomSettingValue"))
                if (TryHex((string?)v.Element("HexValue"), out var hv)) values.TryAdd(hv, (string?)v.Element("UserfriendlyName") ?? "");
            into[id] = new InspectorSetting(id, (string?)cs.Element("UserfriendlyName") ?? "", values, source);
        }
    }

    private static bool TryHex(string? s, out uint v)
    {
        v = 0;
        return s is not null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>Bytes of a managed resource whose name ends with <paramref name="suffix"/>, or null.</summary>
    internal static byte[]? EmbeddedResource(string exe, string suffix)
    {
        try
        {
            using var fs = File.OpenRead(exe);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor) return null;
            var md = pe.GetMetadataReader();
            foreach (var h in md.ManifestResources)
            {
                var r = md.GetManifestResource(h);
                if (!r.Implementation.IsNil || !md.GetString(r.Name).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var data = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress);
                var reader = data.GetReader((int)r.Offset, data.Length - (int)r.Offset);
                var len = reader.ReadInt32();
                return reader.ReadBytes(len);
            }
        }
        catch { /* not a .NET exe or unreadable: no metadata */ }
        return null;
    }

    /// <summary>One line comparing a preset field with Inspector's metadata.</summary>
    public static string Describe(NvPresetItem item, IReadOnlyDictionary<uint, InspectorSetting>? meta)
    {
        if (meta is null) return "NVIDIA Profile Inspector not available; checked against the driver's own names only.";
        if (!meta.TryGetValue(item.ExpectedId, out var s)) return $"Inspector has no entry for {NvidiaProfilePresets.Hex(item.ExpectedId)} (the driver's name is used).";
        var v = s.Values.TryGetValue(item.Value, out var name) ? $"value named '{name}'" : $"value {NvidiaProfilePresets.Hex(item.Value)} isn't one of Inspector's {s.Values.Count} named values (entered as hex, as in Inspector)";
        return $"Inspector ({s.Source}): '{s.Name}', {v}.";
    }
}
