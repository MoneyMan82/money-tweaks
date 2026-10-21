using System.Text.RegularExpressions;

namespace MoneyTweaks.Core.Model;

public sealed record Tutorial(string Url, string Title);

/// <summary>Official video tutorials (the Money Tweaks YouTube channel), by page tag. Played in the app, or opened in the browser.</summary>
public static class Tutorials
{
    public static IReadOnlyDictionary<string, Tutorial> ByPage { get; } = new Dictionary<string, Tutorial>
    {
        ["potato"] = new("https://youtu.be/MA9j5LwYwPo", "Potato Graphics"),
        ["fnstretch"] = new("https://youtu.be/QVDVv3ylheY", "Fortnite Stretched"),
    };

    public static Tutorial? For(string pageTag) => ByPage.GetValueOrDefault(pageTag);

    /// <summary>The 11-character YouTube video id of a youtu.be, watch?v= or embed link; null for anything else.</summary>
    public static string? VideoId(string url)
    {
        var m = Regex.Match(url, @"^https://(?:youtu\.be/|(?:www\.)?youtube(?:-nocookie)?\.com/(?:watch\?v=|embed/))([A-Za-z0-9_-]{11})(?:[?&#].*)?$");
        return m.Success ? m.Groups[1].Value : null;
    }
}
