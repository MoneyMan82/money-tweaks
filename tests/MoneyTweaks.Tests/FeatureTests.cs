using MoneyTweaks.Core.Features;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class FeatureTests(ITestOutputHelper output)
{
    [Fact]
    public void Potato_replaces_existing_keys_only_and_keeps_line_endings()
    {
        var ini = "[ScalabilityGroups]\r\nsg.ResolutionQuality=100.000000\r\nsg.ShadowQuality=3\r\nOther=1\r\n";
        var (text, changed) = PotatoGraphics.ApplyValues(ini,
            new Dictionary<string, string> { ["sg.ResolutionQuality"] = "50.000000", ["sg.ShadowQuality"] = "0", ["NotInFile"] = "x" }, isIni: true);
        Assert.Equal(2, changed);
        Assert.Equal("[ScalabilityGroups]\r\nsg.ResolutionQuality=50.000000\r\nsg.ShadowQuality=0\r\nOther=1\r\n", text);
        Assert.DoesNotContain("NotInFile", text);
    }

    [Fact]
    public void Potato_minecraft_uses_colon_separator()
    {
        var (text, changed) = PotatoGraphics.ApplyValues("renderDistance:12\nao:true\n", new Dictionary<string, string> { ["renderDistance"] = "2", ["ao"] = "false" }, isIni: false);
        Assert.Equal(2, changed);
        Assert.Equal("renderDistance:2\nao:false\n", text);
    }

    [Fact]
    public void Potato_fortnite_levels_and_options()
    {
        var fn = PotatoGraphics.Games.Single(g => g.Id == "fortnite");
        var v = fn.Build(new PotatoOptions { Level = PotatoLevel.UltraPotato, RenderScale = 25, PerformanceMode = true, Stretched = true, UncapFps = true });
        Assert.Equal("25.000000", v["sg.ResolutionQuality"]);
        Assert.Equal("es31", v["PreferredFeatureLevel"]);
        Assert.Equal("1440", v["ResolutionSizeX"]);
        Assert.Equal("0.000000", v["FrameRateLimit"]);
        var low = fn.Build(new PotatoOptions { Level = PotatoLevel.Low, RenderScale = 100, PerformanceMode = false, Stretched = false, UncapFps = false });
        Assert.False(low.ContainsKey("PreferredFeatureLevel"));
        Assert.False(low.ContainsKey("ResolutionSizeX"));
    }

    [Fact]
    public void Section_scoped_keys_only_change_inside_their_section()
    {
        const string nl = "\r\n";
        var ini = "[Other]" + nl + "MeshQuality=5" + nl + "[PerformanceMode]" + nl + "MeshQuality=2" + nl + "[Next]" + nl + "MeshQuality=7" + nl;
        Assert.Equal("2", PotatoGraphics.ReadValue(ini, "PerformanceMode|MeshQuality", true));
        var (text, changed) = PotatoGraphics.ApplyValues(ini, new Dictionary<string, string> { ["PerformanceMode|MeshQuality"] = "0" }, true);
        Assert.Equal(1, changed);
        Assert.Equal(ini.Replace("MeshQuality=2", "MeshQuality=0"), text);
        Assert.Null(PotatoGraphics.ReadValue(ini, "Missing|MeshQuality", true));
    }

    [Fact]
    public void Fortnite_presets_map_to_supported_keys()
    {
        var potato = FortnitePotato.Values(FortnitePotatoSettings.FromPreset(FnPreset.Potato)).ToDictionary(v => v.key, v => v.value);
        Assert.Equal("es31", potato["PreferredFeatureLevel"]);
        Assert.Equal("0", potato["PerformanceMode|MeshQuality"]);
        Assert.Equal("False", potato["bShowGrass"]);
        Assert.Equal("100.000000", potato["sg.ResolutionQuality"]); // potato look comes from meshes, not blur
        var comp = FortnitePotato.Values(FortnitePotatoSettings.FromPreset(FnPreset.Competitive)).ToDictionary(v => v.key, v => v.value);
        Assert.False(comp.ContainsKey("PerformanceMode|MeshQuality")); // competitive keeps the user's mesh choice
        var dx = FortnitePotatoSettings.FromPreset(FnPreset.Potato); dx.PerformanceMode = false;
        Assert.DoesNotContain(FortnitePotato.Values(dx), v => v.key is "PreferredFeatureLevel" or "PerformanceMode|MeshQuality");
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("v2.0.1-beta", 2, 0, 1)]
    public void Version_tags_parse(string tag, int major, int minor, int build)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.Equal((major, minor, build), (v.Major, v.Minor, v.Build));
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]     // newer
    [InlineData("1.1.0", "1.1.0", false)]    // equal
    [InlineData("1.0.9", "1.1.0", false)]    // older
    [InlineData("1.2", "1.2.0.0", false)]    // equal, different formatting
    [InlineData("1.2.1", "1.2.0.0", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.10.0", "1.9.0", true)]    // numeric, not string, comparison
    public void Version_comparison_older_equal_newer(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(Version.Parse(candidate), Version.Parse(current)));

    [Fact]
    public void Same_version_from_tag_is_not_an_update() =>
        Assert.False(UpdateService.IsNewer(UpdateService.ParseVersion("v1.1.0"), new Version(1, 1, 0, 0)));

    [Fact]
    public async Task Update_check_and_verified_download_against_real_github()
    {
        if (Environment.GetEnvironmentVariable("MT_INTEGRATION") != "1") { output.WriteLine("skipped (set MT_INTEGRATION=1)"); return; }
        // A very new version never sees an update.
        Assert.Null(await UpdateService.CheckAsync(new Version(999, 0, 0)));
        // An old version sees the latest release, and its download passes the SHA-256 + version checks.
        var u = await UpdateService.CheckAsync(new Version(0, 0, 1));
        Assert.NotNull(u);
        output.WriteLine($"latest {u!.Tag}, setup={u.SetupUrl is not null}, portable={u.PortableName}, sums={u.SumsUrl is not null}");
        var path = await UpdateService.DownloadAsync(u, new Version(0, 0, 1));
        var fileVersion = UpdateService.FileVersionOf(path);
        output.WriteLine($"verified {path} ({new FileInfo(path).Length / 1048576} MB), file version {fileVersion}");
        File.Delete(path);
        // Regression for the endless-update loop: if the latest release's files are not newer than what we run,
        // the download must be refused instead of "installing" the same version again.
        if (fileVersion is not null && UpdateService.IsNewer(u.Version, fileVersion))
        {
            output.WriteLine($"{u.Tag} is mislabeled (files are {fileVersion}); expecting refusal when running {fileVersion}");
            await Assert.ThrowsAsync<UpdateMismatchException>(() => UpdateService.DownloadAsync(u, fileVersion));
        }
    }
}
