using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Tests;

public class NavigationTests
{
    [Fact]
    public void Every_page_from_before_the_reorganization_is_still_reachable()
    {
        foreach (var tag in AppNavigation.LegacyTags)
        {
            var p = AppNavigation.Pages.SingleOrDefault(x => x.Tag == tag);
            Assert.True(p is not null, $"{tag} is missing from navigation");
            Assert.True(p!.InSidebar, $"{tag} is no longer in the sidebar");
        }
        foreach (var c in Enum.GetValues<TweakCategory>())
            Assert.Contains(AppNavigation.Pages, p => p.Tag == "cat:" + c);
    }

    [Fact]
    public void Groups_match_the_requested_structure_and_tags_are_unique()
    {
        Assert.Equal(["Dashboard", "Optimization", "Gaming", "Monitoring & Benchmarks", "Tools", "Backup & Restore", "Settings"], AppNavigation.Groups.Select(g => g.Label));
        Assert.Equal(AppNavigation.Pages.Count, AppNavigation.Pages.Select(p => p.Tag).Distinct().Count());
        foreach (var g in AppNavigation.Groups) Assert.Contains(AppNavigation.Pages, p => p.Group == g.Id && p.InSidebar);
        Assert.All(AppNavigation.Pages, p => Assert.Contains(AppNavigation.Groups, g => g.Id == p.Group));
        Assert.All(AppNavigation.Pages, p => Assert.False(string.IsNullOrEmpty(p.Icon)));
        // Sections open an existing page; they never introduce a page of their own.
        Assert.All(AppNavigation.Pages.Where(p => p.Anchor is not null), p => Assert.Contains(AppNavigation.Pages, x => x.Tag == p.PageTag));
    }

    [Theory]
    [InlineData("scanner", "optimize")]
    [InlineData("cat:Services", "optimize")]
    [InlineData("library", "gaming")]
    [InlineData("potato", "gaming")]
    [InlineData("monitor#health", "monitor")]
    [InlineData("benchmark", "monitor")]
    [InlineData("startup", "tools")]
    [InlineData("cat:AppRemoval", "tools")]
    [InlineData("macros", "tools")]
    [InlineData("restore", "backup")]
    [InlineData("about", "settings")]
    public void Features_live_in_their_groups(string tag, string group) => Assert.Equal(group, AppNavigation.Find(tag)!.Group);

    [Fact]
    public void Find_resolves_sections_and_recent_list_is_bounded()
    {
        Assert.Equal("monitor", AppNavigation.Find("monitor#health")!.PageTag);
        Assert.Equal("health", AppNavigation.Find("monitor#health")!.Anchor);
        Assert.Equal("about", AppNavigation.Find("about#nonexistent")!.Tag); // unknown section falls back to the page
        var r = AppNavigation.PushRecent(["a", "b", "c"], "b");
        Assert.Equal(["b", "a", "c"], r);
        Assert.Equal(6, AppNavigation.PushRecent(Enumerable.Range(0, 10).Select(i => i.ToString()), "x").Count);
    }

    [Fact]
    public void Dashboard_never_appears_twice_in_the_sidebar()
    {
        Assert.True(AppNavigation.IsHome("dashboard"));
        Assert.False(AppNavigation.IsHome("tweaks"));
        // A saved favourites list that holds the dashboard, a duplicate and a page that no longer exists.
        Assert.Equal(["library", "tweaks"], AppNavigation.SidebarFavourites(["dashboard", "library", "gone-page", "tweaks", "library"]));
    }
}
