namespace MoneyTweaks.Core.Model;

/// <summary>Official video tutorials (the Money Tweaks YouTube channel), by page tag. Opened in the user's browser.</summary>
public static class Tutorials
{
    public static IReadOnlyDictionary<string, string> ByPage { get; } = new Dictionary<string, string>
    {
        ["potato"] = "https://youtu.be/MA9j5LwYwPo",
        ["fnstretch"] = "https://youtu.be/QVDVv3ylheY",
    };

    public static string? For(string pageTag) => ByPage.GetValueOrDefault(pageTag);
}
