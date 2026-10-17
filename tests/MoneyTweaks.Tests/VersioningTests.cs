using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

public class VersioningTests
{
    [Theory]
    [InlineData("1.23.0", AppVersioning.Kind.Update, "1.33")]
    [InlineData("1.23.0", AppVersioning.Kind.Minor, "1.24")]
    [InlineData("1.95.0", AppVersioning.Kind.Update, "2.05")]
    [InlineData("1.99.0", AppVersioning.Kind.Minor, "2.00")]
    [InlineData("1.22.1", AppVersioning.Kind.Minor, "1.23")]   // a legacy patch number is dropped
    [InlineData("2.00.0", AppVersioning.Kind.Update, "2.10")]
    public void Update_adds_a_tenth_and_minor_a_hundredth(string from, AppVersioning.Kind kind, string expected)
    {
        var next = AppVersioning.Next(AppVersioning.Parse(from), kind);
        Assert.Equal(expected, AppVersioning.Display(next));
        Assert.Equal("v" + expected + ".0", AppVersioning.Tag(next));
    }

    [Fact]
    public void Display_two_decimals_and_legacy_patch()
    {
        Assert.Equal("1.23", AppVersioning.Display(new Version(1, 23, 0)));
        Assert.Equal("1.30", AppVersioning.Display(new Version(1, 30, 0)));
        Assert.Equal("2.05", AppVersioning.Display(new Version(2, 5)));
        Assert.Equal("1.22.1", AppVersioning.Display(new Version(1, 22, 1)));
        Assert.Equal("1.23", AppVersioning.Display(new Version(1, 23, 0, 0))); // assembly version
    }

    [Fact]
    public void Ordering_works_for_the_updater_across_old_and_new_numbers()
    {
        var chain = new[] { "1.22.1", "1.23.0", "1.24.0", "1.33.0", "1.99.0", "2.00.0", "2.05.0", "2.10.0" }.Select(AppVersioning.Parse).ToList();
        for (int i = 1; i < chain.Count; i++) Assert.True(UpdateService.IsNewer(chain[i], chain[i - 1]), $"{chain[i]} should be newer than {chain[i - 1]}");
        Assert.False(UpdateService.IsNewer(UpdateService.ParseVersion("v1.23.0"), new Version(1, 23, 0, 0)));
        Assert.Equal(new Version(1, 24, 0), UpdateService.ParseVersion("v1.24.0"));
        // Older copies format the release version with three parts; tags must keep one so that never throws.
        Assert.Equal("1.24.0", UpdateService.ParseVersion(AppVersioning.Tag(new Version(1, 24, 0))).ToString(3));
    }

    [Theory]
    [InlineData("1.24", true)]
    [InlineData("1.24.0", true)]
    [InlineData("1.22.1", true)]
    [InlineData("1.3", false)]     // one decimal would sort below 1.24
    [InlineData("1.24.5", true)]   // legacy-shaped still accepted
    [InlineData("v1.24", false)]
    public void Valid_release_versions(string v, bool ok) => Assert.Equal(ok, AppVersioning.IsValid(v));
}
