using System.Security.Cryptography;
using MoneyTweaks.Core.Monitoring;

namespace MoneyTweaks.Tests;

/// <summary>Bundled third-party GPU models: files, checksums, licenses, attribution and which cards they're used for.</summary>
public class GpuAssetTests
{
    private static string Dir()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "THIRD-PARTY-NOTICES.md"))) d = Path.GetDirectoryName(d);
        return Path.Combine(d!, "src", "MoneyTweaks", "Assets", "Gpu");
    }

    private static GpuAssetManifest Manifest() => GpuAssetManifest.Parse(File.ReadAllText(Path.Combine(Dir(), "manifest.json")));

    [Fact]
    public void Every_bundled_model_is_verified_and_attributed()
    {
        var m = Manifest();
        Assert.NotEmpty(m.Assets);
        var notices = File.ReadAllText(Path.Combine(Dir(), "..", "..", "..", "..", "THIRD-PARTY-NOTICES.md"));
        foreach (var a in m.Assets)
        {
            var path = Path.Combine(Dir(), a.File);
            Assert.True(File.Exists(path), a.File);
            Assert.Equal(a.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ignoreCase: true);
            Assert.Contains(a.License, GpuAssetManifest.AllowedLicenses);
            Assert.StartsWith("https://", a.Source);
            Assert.StartsWith("https://creativecommons.org/", a.LicenseUrl);
            var author = a.Author.Split([" (", ","], StringSplitOptions.None)[0];   // the name, without profile link or former names
            Assert.Contains(author, a.Attribution);
            Assert.Contains(a.Source, a.Attribution);
            Assert.Contains(author, notices);
            Assert.Contains(a.Sha256, notices);
            Assert.All(a.Matches, id => Assert.Contains(GpuModels.All, s => s.Id == id));
        }
        // Nothing NonCommercial or unlicensed is ever bundled.
        Assert.DoesNotContain(m.Assets, a => a.License.Contains("NC") || a.License == "none");
        Assert.All(m.EvaluatedNotBundled, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080", 0x146710DEu, "rtx3080-founders-surovic.glb")]      // Founders Edition: the detailed RTX 30 Founders model
    [InlineData("NVIDIA GeForce RTX 3090", 0x147010DEu, "rtx3080-founders-surovic.glb")]      // same design
    [InlineData("NVIDIA GeForce RTX 3080", 0x38971462u, null)]                                // MSI partner card: never the Founders look
    [InlineData("NVIDIA GeForce RTX 4090", 0x16F110DEu, "rtx4090-founders-gihyeon.glb")]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", 0x18A110DEu, "rtx4090-founders-gihyeon.glb")] // 4080 Founders shares the 4090's design
    [InlineData("NVIDIA GeForce RTX 4090", 0x88E21043u, null)]                                // ASUS 4090: not the Founders model
    [InlineData("NVIDIA GeForce RTX 5070", 0x53131462u, "rtx5070-msi-ventus3x-darky.glb")]    // MSI 5070: the MSI Ventus model
    [InlineData("NVIDIA GeForce RTX 5070", 0x89F01043u, null)]                                // ASUS 5070: not an MSI card
    [InlineData("NVIDIA GeForce RTX 5070 Ti", 0x41501458u, "rtx5070ti-hakai315.glb")]        // any 5070 Ti: the typical 5070 Ti model
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", 0x89741043u, null)]                          // this PC's ASUS card: no verified model
    [InlineData("NVIDIA GeForce RTX 3070", 0x148410DEu, null)]                                // 3070 Founders looks different
    public void Verified_models_are_only_used_for_the_cards_they_depict(string name, uint subsys, string? file)
    {
        var match = GpuModels.Match(name, 0x10DE, subsys);
        Assert.Equal(file, Manifest().For(match)?.File);
    }
}
