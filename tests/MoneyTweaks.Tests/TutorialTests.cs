using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Tests;

public class TutorialTests
{
    [Fact]
    public void Every_tutorial_belongs_to_a_page_and_is_a_youtube_link()
    {
        Assert.NotEmpty(Tutorials.ByPage);
        foreach (var (tag, url) in Tutorials.ByPage)
        {
            Assert.Contains(AppNavigation.Pages, p => p.Tag == tag);
            Assert.Matches(@"^https://youtu\.be/[A-Za-z0-9_-]{11}$", url);
        }
        Assert.Null(Tutorials.For("dashboard"));
    }
}
