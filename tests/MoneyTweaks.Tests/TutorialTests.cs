using MoneyTweaks.Core.Model;

namespace MoneyTweaks.Tests;

public class TutorialTests
{
    [Fact]
    public void Every_tutorial_belongs_to_a_page_and_is_a_playable_youtube_link()
    {
        Assert.NotEmpty(Tutorials.ByPage);
        foreach (var (tag, t) in Tutorials.ByPage)
        {
            Assert.Contains(AppNavigation.Pages, p => p.Tag == tag);
            Assert.NotNull(Tutorials.VideoId(t.Url));
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
        }
        Assert.Null(Tutorials.For("dashboard"));
    }

    [Theory]
    [InlineData("https://youtu.be/MA9j5LwYwPo", "MA9j5LwYwPo")]
    [InlineData("https://www.youtube.com/watch?v=QVDVv3ylheY&t=10", "QVDVv3ylheY")]
    [InlineData("https://www.youtube-nocookie.com/embed/QVDVv3ylheY", "QVDVv3ylheY")]
    [InlineData("https://evil.example/youtu.be/MA9j5LwYwPo", null)]
    [InlineData("http://youtu.be/MA9j5LwYwPo", null)]
    [InlineData("https://youtu.be/short", null)]
    public void Video_ids_are_only_read_from_youtube_links(string url, string? id) => Assert.Equal(id, Tutorials.VideoId(url));
}
