using System.Text.Json;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Tests;

public class UpdatePickTests
{
    private static string R(string tag, bool draft = false, bool pre = false, bool sums = true) =>
        $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(pre ? "true" : "false")}},"assets":[{"name":"{{(sums ? "SHA256SUMS.txt" : "x.zip")}}"}]}""";

    private static string? Pick(params string[] releases)
    {
        using var doc = JsonDocument.Parse("[" + string.Join(",", releases) + "]");
        return UpdateService.PickNewest(doc.RootElement)?.GetProperty("tag_name").GetString();
    }

    [Fact]
    public void Newest_is_chosen_by_version_not_publish_order()
    {
        // GitHub listed v1.48.0 as "latest" because it finished publishing after v1.49.0.
        Assert.Equal("v1.49.0", Pick(R("v1.48.0"), R("v1.49.0"), R("v1.47.0")));
        Assert.Equal("v1.10.0", Pick(R("v1.9.0"), R("v1.10.0")));
    }

    [Fact]
    public void Drafts_prereleases_and_incomplete_releases_are_skipped()
    {
        Assert.Equal("v1.49.0", Pick(R("v1.50.0", draft: true), R("v1.51.0", pre: true), R("v1.52.0", sums: false), R("v1.49.0")));
        Assert.Null(Pick());
    }
}
