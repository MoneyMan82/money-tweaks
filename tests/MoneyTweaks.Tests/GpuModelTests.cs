using MoneyTweaks.Core.Monitoring;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Reality Lab GPU models: strict matching to the library, board makers, placeholders, and this PC's real adapter.</summary>
public class GpuModelTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", 0x10DE, "rtx3060")]
    [InlineData("NVIDIA GeForce RTX 4060", 0x10DE, "rtx4060")]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", 0x10DE, "rtx4070super")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", 0x10DE, "rtx4070tisuper")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti", 0x10DE, "rtx4070ti")]
    [InlineData("NVIDIA GeForce RTX 4070", 0x10DE, "rtx4070")]
    [InlineData("NVIDIA GeForce RTX 4080", 0x10DE, "rtx4080")]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", 0x10DE, "rtx4080super")]
    [InlineData("NVIDIA GeForce RTX 4090", 0x10DE, "rtx4090")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", 0x10DE, "gtx1660super")]
    [InlineData("AMD Radeon RX 7900 XTX", 0x1002, "rx7900xtx")]
    [InlineData("AMD Radeon RX 7900 XT", 0x1002, "rx7900xt")]
    [InlineData("AMD Radeon RX 6600", 0x1002, "rx6600")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 0x8086, "a770")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", 0x8086, "b580")]
    public void Popular_cards_match_exactly(string name, uint vendor, string id)
    {
        var m = GpuModels.Match(name, vendor);
        Assert.Equal(id, m.Spec?.Id);
        Assert.True(m.IsCard);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090 D", 0x10DE, GpuDesign.Unknown)]                // a variant not in the library: never the plain 4090
    [InlineData("NVIDIA RTX A4000", 0x10DE, GpuDesign.Unknown)]                         // workstation card
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 0x10DE, GpuDesign.Laptop)]
    [InlineData("NVIDIA GeForce RTX 3070 Ti Laptop GPU", 0x10DE, GpuDesign.Laptop)]
    [InlineData("AMD Radeon(TM) Graphics", 0x1002, GpuDesign.Integrated)]
    [InlineData("AMD Radeon 780M Graphics", 0x1002, GpuDesign.Integrated)]
    [InlineData("Intel(R) UHD Graphics 770", 0x8086, GpuDesign.Integrated)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 0x8086, GpuDesign.Integrated)]
    [InlineData("Intel(R) Arc(TM) Graphics", 0x8086, GpuDesign.Integrated)]
    [InlineData("Microsoft Basic Render Driver", 0x1414, GpuDesign.Unknown)]
    [InlineData("AMD Radeon RX 7900 XTX", 0x10DE, GpuDesign.Unknown)]                   // name and PCI vendor disagree: don't guess
    public void Near_misses_get_a_placeholder_never_a_wrong_card(string name, uint vendor, GpuDesign design)
    {
        var m = GpuModels.Match(name, vendor);
        Assert.Equal(design, m.Design);
        Assert.Null(m.Spec);
        Assert.False(m.IsCard);
        Assert.False(string.IsNullOrWhiteSpace(m.Why));
    }

    [Fact]
    public void Board_maker_decides_between_reference_and_partner_shapes()
    {
        var fe = GpuModels.Match("NVIDIA GeForce RTX 4090", 0x10DE, 0x16F110DE);           // subsystem vendor NVIDIA: Founders Edition
        Assert.Equal(GpuDesign.NvidiaFounders, fe.Design);
        Assert.Equal((304, 137, 3.0, 2), fe.Shape);
        var asus = GpuModels.Match("NVIDIA GeForce RTX 4070 SUPER", 0x10DE, 0x89741043);  // ASUS
        Assert.Equal(GpuDesign.Partner, asus.Design);
        Assert.Equal("ASUS", asus.Maker);
        Assert.Contains("ASUS", asus.Why);
        var noFe = GpuModels.Match("NVIDIA GeForce RTX 4060", 0x10DE, 0);                  // no Founders Edition exists
        Assert.Equal(GpuDesign.Partner, noFe.Design);
        var amdRef = GpuModels.Match("AMD Radeon RX 7900 XTX", 0x1002, 0x0E3A1002);
        Assert.Equal(GpuDesign.AmdReference, amdRef.Design);
        Assert.Equal(3, amdRef.Shape.fans);
        var arc = GpuModels.Match("Intel(R) Arc(TM) A770 Graphics", 0x8086, 0x10208086);
        Assert.Equal(GpuDesign.IntelLimited, arc.Design);
        var sapphire = GpuModels.Match("AMD Radeon RX 7800 XT", 0x1002, 0xE3401DA2);
        Assert.Equal("Sapphire", sapphire.Maker);
        Assert.Equal(GpuDesign.Partner, sapphire.Design);
    }

    [Fact]
    public void Every_library_entry_is_complete_and_unique()
    {
        Assert.Equal(GpuModels.All.Count, GpuModels.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal(GpuModels.All.Count, GpuModels.All.Select(s => string.Join(" ", s.Tokens.Order())).Distinct().Count());
        Assert.All(GpuModels.All, s =>
        {
            Assert.InRange(s.RefLengthMm, 150, 360);
            Assert.InRange(s.RefHeightMm, 90, 160);
            Assert.InRange(s.RefSlots, 1, 3.5);
            Assert.InRange(s.PartnerFans, 1, 3);
            Assert.True(s.MemoryGb > 0 && s.BoardPowerW > 0 && s.BusBits > 0 && s.Chip.Length > 0);
        });
        foreach (var popular in new[] { "rtx3060", "rtx4060", "rtx4070super", "rtx4080", "rtx4090" })
            Assert.Contains(GpuModels.All, s => s.Id == popular && s.Popular);
    }

    [Fact]
    public void This_pcs_adapters_match_something_sensible()
    {
        var adapters = Dxgi.Adapters();
        foreach (var a in adapters)
        {
            var m = GpuModels.Match(a.Name, a.VendorId, a.SubSysId);
            output.WriteLine($"{a.Name} (vendor 0x{a.VendorId:X4}, device 0x{a.DeviceId:X4}, subsys 0x{a.SubSysId:X8}) → {m.Design} {m.Spec?.Id} maker {m.Maker}: {m.Why}");
            Assert.False(string.IsNullOrWhiteSpace(m.Why));
            if (a.VendorId == 0x10DE && a.Name.Contains("RTX 4070 SUPER")) Assert.Equal("rtx4070super", m.Spec?.Id);
        }
        if (GpuModels.Primary(adapters) is { } p) output.WriteLine("shown: " + p.Name);
    }
}
