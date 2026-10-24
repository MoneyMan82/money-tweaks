using System.Text.RegularExpressions;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>How a card is drawn. Placeholders are used whenever the exact card isn't known.</summary>
public enum GpuDesign
{
    /// <summary>NVIDIA Founders Edition (RTX 30/40/50): metal frame, flow-through cooler, one fan on each side.</summary>
    NvidiaFounders,
    /// <summary>AMD reference (RX 6000/7000): dark shroud with red accents, two or three fans.</summary>
    AmdReference,
    /// <summary>Intel Arc Limited Edition: matte black, two fans, light strips.</summary>
    IntelLimited,
    /// <summary>A typical board-partner card of this chip (the exact model isn't known from the PCI IDs).</summary>
    Partner,
    /// <summary>Graphics inside the CPU: there is no card.</summary>
    Integrated,
    /// <summary>A laptop GPU: soldered to the mainboard, no card.</summary>
    Laptop,
    /// <summary>Not in the library: a clean placeholder instead of a wrong card.</summary>
    Unknown,
}

/// <summary>
/// One graphics card model of the library: the specs NVIDIA, AMD and Intel publish (chip, memory, rated board power) and
/// its shape. <see cref="RefLengthMm"/>/<see cref="RefHeightMm"/>/<see cref="RefSlots"/> are the reference or Founders
/// Edition dimensions where such a card exists; otherwise a typical size for the tier (<see cref="HasReferenceCard"/> false).
/// </summary>
public sealed record GpuModelSpec(
    string Id, string Vendor, string Name, string[] Tokens, string Chip, int MemoryGb, string MemoryType, int BusBits, int BoardPowerW,
    int RefLengthMm, int RefHeightMm, double RefSlots, int RefFans, bool HasReferenceCard, int PartnerFans, bool Popular = false);

/// <summary>The result of matching this PC's adapter to the library.</summary>
public sealed record GpuMatch(GpuDesign Design, GpuModelSpec? Spec, string AdapterName, string? Maker, string Why)
{
    public bool IsCard => Spec is not null && Design is GpuDesign.NvidiaFounders or GpuDesign.AmdReference or GpuDesign.IntelLimited or GpuDesign.Partner;
    /// <summary>Card dimensions drawn (mm and slots): the reference card's, or a typical partner card's.</summary>
    public (int lengthMm, int heightMm, double slots, int fans) Shape => Spec is null ? (240, 112, 2, 0)
        : Design == GpuDesign.Partner ? (Math.Max(Spec.RefLengthMm, PartnerLength(Spec)), Math.Max(Spec.RefHeightMm, 120), Math.Max(Spec.RefSlots, Spec.PartnerFans >= 3 ? 2.5 : 2), Spec.PartnerFans)
        : (Spec.RefLengthMm, Spec.RefHeightMm, Spec.RefSlots, Spec.RefFans);

    private static int PartnerLength(GpuModelSpec s) => s.PartnerFans >= 3 ? 300 : s.PartnerFans == 2 ? 240 : 175;
}

/// <summary>
/// Curated library of popular graphics cards and the strict matcher that maps this PC's adapter onto it. Matching is by
/// the exact set of model tokens ("rtx 4070 super" never matches "rtx 4070 ti super" or "rtx 4070"), with the PCI vendor
/// confirming the brand, so a near miss gives the clean placeholder instead of the wrong card. Laptop and integrated GPUs
/// are recognised and never drawn as add-in cards. The board maker comes from the PCI subsystem vendor.
/// </summary>
public static class GpuModels
{
    // Declared before the library: static fields initialise in order, and building the library tokenises every name.
    private static readonly HashSet<string> Noise = ["nvidia", "geforce", "amd", "ati", "radeon", "intel", "r", "tm", "graphics", "gpu", "series", "video", "adapter"];

    private static GpuModelSpec Nv(string id, string name, string chip, int gb, string mem, int bus, int w, int len, int h, double slots, int fans, bool fe, int partnerFans, bool popular = false)
        => new(id, "NVIDIA", name, Tok(name), chip, gb, mem, bus, w, len, h, slots, fans, fe, partnerFans, popular);
    private static GpuModelSpec Amd(string id, string name, string chip, int gb, string mem, int bus, int w, int len, int h, double slots, int fans, bool reference, int partnerFans, bool popular = false)
        => new(id, "AMD", name, Tok(name), chip, gb, mem, bus, w, len, h, slots, fans, reference, partnerFans, popular);
    private static GpuModelSpec Arc(string id, string name, string chip, int gb, int bus, int w, int len, int h, bool le, int partnerFans)
        => new(id, "Intel", name, Tok(name), chip, gb, "GDDR6", bus, w, len, h, 2, 2, le, partnerFans);

    // Reference / Founders Edition dimensions where those cards exist; otherwise a typical size for the tier.
    public static readonly IReadOnlyList<GpuModelSpec> All =
    [
        // NVIDIA GeForce RTX 50
        Nv("rtx5090", "RTX 5090", "GB202", 32, "GDDR7", 512, 575, 304, 137, 2, 2, true, 3),
        Nv("rtx5080", "RTX 5080", "GB203", 16, "GDDR7", 256, 360, 304, 137, 2, 2, true, 3),
        Nv("rtx5070ti", "RTX 5070 Ti", "GB203", 16, "GDDR7", 256, 300, 304, 137, 2.5, 3, false, 3),
        Nv("rtx5070", "RTX 5070", "GB205", 12, "GDDR7", 192, 250, 242, 112, 2, 2, true, 2),
        Nv("rtx5060ti", "RTX 5060 Ti", "GB206", 16, "GDDR7", 128, 180, 240, 112, 2, 2, false, 2),
        Nv("rtx5060", "RTX 5060", "GB206", 8, "GDDR7", 128, 145, 240, 112, 2, 2, false, 2),
        // NVIDIA GeForce RTX 40
        Nv("rtx4090", "RTX 4090", "AD102", 24, "GDDR6X", 384, 450, 304, 137, 3, 2, true, 3, popular: true),
        Nv("rtx4080super", "RTX 4080 SUPER", "AD103", 16, "GDDR6X", 256, 320, 304, 137, 3, 2, true, 3),
        Nv("rtx4080", "RTX 4080", "AD103", 16, "GDDR6X", 256, 320, 304, 137, 3, 2, true, 3, popular: true),
        Nv("rtx4070tisuper", "RTX 4070 Ti SUPER", "AD103", 16, "GDDR6X", 256, 285, 300, 125, 2.5, 3, false, 3),
        Nv("rtx4070ti", "RTX 4070 Ti", "AD104", 12, "GDDR6X", 192, 285, 300, 125, 2.5, 3, false, 3),
        Nv("rtx4070super", "RTX 4070 SUPER", "AD104", 12, "GDDR6X", 192, 220, 244, 112, 2, 2, true, 2, popular: true),
        Nv("rtx4070", "RTX 4070", "AD104", 12, "GDDR6X", 192, 200, 244, 112, 2, 2, true, 2),
        Nv("rtx4060ti", "RTX 4060 Ti", "AD106", 8, "GDDR6", 128, 160, 244, 98, 2, 2, true, 2),
        Nv("rtx4060", "RTX 4060", "AD107", 8, "GDDR6", 128, 115, 240, 112, 2, 2, false, 2, popular: true),
        // NVIDIA GeForce RTX 30
        Nv("rtx3090ti", "RTX 3090 Ti", "GA102", 24, "GDDR6X", 384, 450, 313, 138, 3, 2, true, 3),
        Nv("rtx3090", "RTX 3090", "GA102", 24, "GDDR6X", 384, 350, 313, 138, 3, 2, true, 3),
        Nv("rtx3080ti", "RTX 3080 Ti", "GA102", 12, "GDDR6X", 384, 350, 285, 112, 2, 2, true, 3),
        Nv("rtx3080", "RTX 3080", "GA102", 10, "GDDR6X", 320, 320, 285, 112, 2, 2, true, 3),
        Nv("rtx3070ti", "RTX 3070 Ti", "GA104", 8, "GDDR6X", 256, 290, 270, 110, 2, 2, true, 3),
        Nv("rtx3070", "RTX 3070", "GA104", 8, "GDDR6", 256, 220, 242, 112, 2, 2, true, 2),
        Nv("rtx3060ti", "RTX 3060 Ti", "GA104", 8, "GDDR6", 256, 200, 242, 112, 2, 2, true, 2),
        Nv("rtx3060", "RTX 3060", "GA106", 12, "GDDR6", 192, 170, 240, 112, 2, 2, false, 2, popular: true),
        Nv("rtx3050", "RTX 3050", "GA106", 8, "GDDR6", 128, 130, 200, 112, 2, 2, false, 2),
        // NVIDIA GeForce GTX 16
        Nv("gtx1660super", "GTX 1660 SUPER", "TU116", 6, "GDDR6", 192, 125, 200, 112, 2, 2, false, 2),
        Nv("gtx1650", "GTX 1650", "TU117", 4, "GDDR5", 128, 75, 175, 112, 2, 1, false, 1),
        // AMD Radeon RX 9000
        Amd("rx9070xt", "RX 9070 XT", "Navi 48", 16, "GDDR6", 256, 304, 300, 125, 2.5, 3, false, 3),
        Amd("rx9070", "RX 9070", "Navi 48", 16, "GDDR6", 256, 220, 280, 120, 2.5, 3, false, 3),
        Amd("rx9060xt", "RX 9060 XT", "Navi 44", 16, "GDDR6", 128, 160, 240, 115, 2, 2, false, 2),
        // AMD Radeon RX 7000
        Amd("rx7900xtx", "RX 7900 XTX", "Navi 31", 24, "GDDR6", 384, 355, 287, 123, 2.5, 3, true, 3, popular: true),
        Amd("rx7900xt", "RX 7900 XT", "Navi 31", 20, "GDDR6", 320, 315, 276, 113, 2, 3, true, 3),
        Amd("rx7900gre", "RX 7900 GRE", "Navi 31", 16, "GDDR6", 256, 260, 276, 113, 2, 3, true, 3),
        Amd("rx7800xt", "RX 7800 XT", "Navi 32", 16, "GDDR6", 256, 263, 267, 111, 2, 2, true, 2, popular: true),
        Amd("rx7700xt", "RX 7700 XT", "Navi 32", 12, "GDDR6", 192, 245, 267, 111, 2, 2, false, 2),
        Amd("rx7600xt", "RX 7600 XT", "Navi 33", 16, "GDDR6", 128, 190, 240, 115, 2, 2, false, 2),
        Amd("rx7600", "RX 7600", "Navi 33", 8, "GDDR6", 128, 165, 204, 115, 2, 2, true, 2, popular: true),
        // AMD Radeon RX 6000
        Amd("rx6950xt", "RX 6950 XT", "Navi 21", 16, "GDDR6", 256, 335, 267, 120, 2.5, 3, true, 3),
        Amd("rx6900xt", "RX 6900 XT", "Navi 21", 16, "GDDR6", 256, 300, 267, 120, 2.5, 3, true, 3),
        Amd("rx6800xt", "RX 6800 XT", "Navi 21", 16, "GDDR6", 256, 300, 267, 120, 2.5, 3, true, 3),
        Amd("rx6800", "RX 6800", "Navi 21", 16, "GDDR6", 256, 250, 267, 120, 2, 3, true, 3),
        Amd("rx6750xt", "RX 6750 XT", "Navi 22", 12, "GDDR6", 192, 250, 267, 110, 2, 2, false, 2),
        Amd("rx6700xt", "RX 6700 XT", "Navi 22", 12, "GDDR6", 192, 230, 267, 110, 2, 2, true, 2),
        Amd("rx6650xt", "RX 6650 XT", "Navi 23", 8, "GDDR6", 128, 180, 240, 115, 2, 2, false, 2),
        Amd("rx6600xt", "RX 6600 XT", "Navi 23", 8, "GDDR6", 128, 160, 240, 115, 2, 2, false, 2),
        Amd("rx6600", "RX 6600", "Navi 23", 8, "GDDR6", 128, 132, 200, 115, 2, 2, false, 2, popular: true),
        // Intel Arc
        Arc("b580", "Arc B580", "BMG-G21", 12, 192, 190, 272, 115, true, 2),
        Arc("b570", "Arc B570", "BMG-G21", 10, 160, 150, 250, 115, false, 2),
        Arc("a770", "Arc A770", "ACM-G10", 16, 256, 225, 268, 111, true, 2),
        Arc("a750", "Arc A750", "ACM-G10", 8, 256, 225, 268, 111, true, 2),
        Arc("a580", "Arc A580", "ACM-G10", 8, 256, 185, 250, 115, false, 2),
        Arc("a380", "Arc A380", "ACM-G11", 6, 96, 75, 175, 112, false, 1),
    ];

    /// <summary>Board makers by PCI subsystem vendor ID (low 16 bits of the subsystem ID).</summary>
    public static readonly IReadOnlyDictionary<int, string> Makers = new Dictionary<int, string>
    {
        [0x10DE] = "NVIDIA", [0x1002] = "AMD", [0x8086] = "Intel", [0x1043] = "ASUS", [0x1462] = "MSI", [0x1458] = "Gigabyte",
        [0x3842] = "EVGA", [0x19DA] = "ZOTAC", [0x196E] = "PNY", [0x1569] = "Palit", [0x10B0] = "Gainward", [0x1B4C] = "KFA2/Galax",
        [0x7377] = "Colorful", [0x1DA2] = "Sapphire", [0x148C] = "PowerColor", [0x1682] = "XFX", [0x1849] = "ASRock", [0x174B] = "PC Partner",
        [0x1ACC] = "Point of View", [0x1D05] = "Tongfang", [0x17AA] = "Lenovo", [0x1028] = "Dell", [0x103C] = "HP", [0x1025] = "Acer", [0x172F] = "Sparkle",
    };

    /// <summary>Model tokens of a name: lower case, without brand words and punctuation ("Intel(R) Arc(TM) A770 Graphics" → arc, a770).</summary>
    public static string[] Tok(string name) =>
        [.. Regex.Split(name.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0 && !Noise.Contains(t))];

    /// <summary>Matches an adapter (DXGI name, PCI vendor and subsystem IDs) to the library.</summary>
    public static GpuMatch Match(string adapterName, uint vendorId = 0, uint subSysId = 0)
    {
        var name = adapterName.Trim();
        var tokens = Tok(name);
        string? maker = subSysId != 0 && Makers.TryGetValue((int)(subSysId & 0xFFFF), out var m) ? m : null;
        string brand = vendorId switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", _ => "" };

        if (tokens.Contains("laptop") || tokens.Contains("mobile") || Regex.IsMatch(name, @"\b(Max-Q|Laptop)\b", RegexOptions.IgnoreCase))
            return new(GpuDesign.Laptop, null, name, maker, "A laptop GPU is soldered to the mainboard; there is no card to show.");
        bool integratedName = tokens.Length == 0 || tokens.SequenceEqual(["uhd"]) || (tokens.Length >= 1 && tokens[0] is "uhd" or "hd" or "iris" or "xe" or "vega") || tokens.SequenceEqual(["arc"])
            || Regex.IsMatch(name, @"^(AMD )?Radeon(\(TM\))? (Graphics|\d{3,4}M|Vega)", RegexOptions.IgnoreCase) || Regex.IsMatch(name, @"Intel.*(UHD|Iris|HD Graphics)", RegexOptions.IgnoreCase);
        if (integratedName && brand != "NVIDIA")
            return new(GpuDesign.Integrated, null, name, maker, "Integrated graphics: part of the processor, there is no separate card.");

        var spec = All.FirstOrDefault(s => s.Tokens.Length == tokens.Length && s.Tokens.All(tokens.Contains) && (brand.Length == 0 || s.Vendor == brand));
        if (spec is null)
            return new(GpuDesign.Unknown, null, name, maker, "This card isn't in Money Tweaks' model library, so a neutral placeholder is shown instead of a different card.");

        bool reference = spec.HasReferenceCard && maker is null || (maker == spec.Vendor && spec.HasReferenceCard);
        var design = !reference ? GpuDesign.Partner : spec.Vendor switch { "NVIDIA" => GpuDesign.NvidiaFounders, "AMD" => GpuDesign.AmdReference, _ => GpuDesign.IntelLimited };
        string why = design switch
        {
            GpuDesign.Partner when maker is not null => $"Made by {maker}. The PCI IDs don't say which {maker} model it is, so a typical {spec.PartnerFans}-fan {spec.Name} shape is shown; your card's cooler may look different.",
            GpuDesign.Partner => $"No reference card exists for the {spec.Name}, so a typical {spec.PartnerFans}-fan board-partner shape is shown.",
            GpuDesign.NvidiaFounders => maker is null ? $"Drawn as the {spec.Name} Founders Edition (the card maker isn't reported)." : $"{spec.Name} Founders Edition, built to its published dimensions.",
            GpuDesign.AmdReference => maker is null ? $"Drawn as AMD's reference {spec.Name} (the card maker isn't reported)." : $"AMD reference {spec.Name}, built to its published dimensions.",
            _ => maker is null ? $"Drawn as Intel's {spec.Name} Limited Edition (the card maker isn't reported)." : $"Intel {spec.Name} Limited Edition, built to its published dimensions.",
        };
        return new(design, spec, name, maker, why);
    }

    /// <summary>The adapter to show: the dedicated card with the most video memory, else the first adapter.</summary>
    public static DxgiAdapter? Primary(IReadOnlyList<DxgiAdapter> adapters) =>
        adapters.OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault();
}
