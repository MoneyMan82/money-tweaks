using System.Text.RegularExpressions;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Tests;

/// <summary>The tweak counts shown in the README and on the website match the real catalog.</summary>
public class PublishedCountsTests
{
    private static readonly (TweakCategory Cat, string Readme, string Site)[] Rows =
    [
        (TweakCategory.General, "General", "General"),
        (TweakCategory.Debloating, "Debloating", "Debloating"),
        (TweakCategory.Networking, "Networking", "Networking"),
        (TweakCategory.Advanced, "Advanced", "Advanced"),
        (TweakCategory.Services, "Services", "Services"),
        (TweakCategory.AppOptimizer, "App Optimizer", "App Optimizer"),
        (TweakCategory.AppRemoval, "App Removal", "App Removal"),
        (TweakCategory.GameProfiles, "Game Profiles", "Game CPU Priority"),
        (TweakCategory.Bios, "BIOS", "BIOS"),
    ];

    private static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MoneyTweaks.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return root!;
    }

    private static int Count(TweakCategory c) => TweakCatalog.All.Count(t => t.Category == c);

    [Fact]
    public void Readme_counts_match_the_catalog()
    {
        var readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        foreach (var (cat, name, _) in Rows)
        {
            var m = Regex.Match(readme, @"^\| " + Regex.Escape(name) + @" \| (\d+) \|", RegexOptions.Multiline);
            Assert.True(m.Success, name);
            Assert.Equal(Count(cat), int.Parse(m.Groups[1].Value));
        }
        Assert.Contains($"It has {TweakCatalog.All.Count} reversible tweaks", readme);
    }

    [Fact]
    public void Website_counts_match_the_catalog()
    {
        var html = File.ReadAllText(Path.Combine(Root(), "docs", "index.html"));
        foreach (var (cat, _, name) in Rows)
        {
            var m = Regex.Match(html, @"<span class=""count"">(\d+)</span><h3>" + Regex.Escape(name) + "</h3>");
            Assert.True(m.Success, name);
            Assert.Equal(Count(cat), int.Parse(m.Groups[1].Value));
        }
        var total = TweakCatalog.All.Count.ToString();
        Assert.Contains($"<meta name=\"description\" content=\"Money Tweaks is a free, open-source Windows 10/11 tweaker: {total} reversible tweaks", html);
        Assert.Contains($"<meta property=\"og:description\" content=\"{total} reversible tweaks", html);
        Assert.Contains($"<b>{total}</b> reversible tweaks", html);
        Assert.Contains($"<h2>{total} tweaks in 9 categories</h2>", html);
        Assert.Contains($"Money Tweaks bundles {total} reversible tweaks,", html);
    }
}
