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
            Assert.Contains(a.Author, a.Attribution);
            Assert.Contains(a.Source, a.Attribution);
            Assert.Contains(a.Author, notices);
            Assert.All(a.Matches, id => Assert.Contains(GpuModels.All, s => s.Id == id));
        }
        // Nothing NonCommercial or unlicensed is ever bundled.
        Assert.DoesNotContain(m.Assets, a => a.License.Contains("NC") || a.License == "none");
        Assert.All(m.EvaluatedNotBundled, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080", 0x146710DEu, true)]     // Founders Edition: the verified RTX 30 Founders model
    [InlineData("NVIDIA GeForce RTX 3090", 0x147010DEu, true)]
    [InlineData("NVIDIA GeForce RTX 3080", 0x38971462u, false)]    // MSI partner card: generic model, never the Founders look
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", 0x89741043u, false)] // this PC's ASUS card: no verified model exists
    [InlineData("NVIDIA GeForce RTX 4090", 0x16F110DEu, false)]   // 40-series Founders looks different: not the 30-series model
    [InlineData("NVIDIA GeForce RTX 3070", 0x148410DEu, false)]   // 3070 Founders has two front fans and no X frame
    public void Verified_models_are_only_used_for_the_cards_they_depict(string name, uint subsys, bool expected)
    {
        var match = GpuModels.Match(name, 0x10DE, subsys);
        Assert.Equal(expected, Manifest().For(match) is not null);
    }
}
