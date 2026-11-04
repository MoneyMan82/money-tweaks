using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Tests;

/// <summary>The 1.79 library expansion: every new tweak names its source and license and stays on the safe side.</summary>
public class TweakSourcesTests
{
    private static readonly string[] Licenses = ["(MIT)", "(GPL-3.0; idea only, no code used)", "(freeware; idea only)"];
    private static List<Tweak> Expanded => [.. TweakCatalog.All.Where(t => t.Source is not null)];

    [Fact]
    public void Every_expanded_tweak_names_a_source_with_its_license_and_shows_it_in_the_details()
    {
        Assert.True(Expanded.Count >= 60, $"{Expanded.Count} tweaks with a source");
        foreach (var t in Expanded)
        {
            Assert.Contains(Licenses, l => t.Source!.Contains(l, StringComparison.Ordinal));
            var text = TweakInfo.For(t).ToText();
            Assert.Contains("Source: " + t.Source, text);
            Assert.Contains("Supported: ", text);
        }
        Assert.Contains("Pro, Enterprise", TweakInfo.For(TweakCatalog.Find("dbl.no-auto-reboot")!).Supported);
        Assert.Contains("Windows 11", TweakInfo.For(TweakCatalog.Find("gen.taskbar-never-combine")!).Supported);
    }

    [Fact]
    public void Expanded_tweaks_never_weaken_security_or_touch_defender()
    {
        string[] forbidden = ["Windows Defender", "SmartScreen", "EnableLUA", "ConsentPromptBehaviorAdmin", "SaveZoneInformation", "FeatureSettingsOverride", "NoAutoUpdate", "TyposquattingChecker"];
        foreach (var t in Expanded)
            foreach (var op in t.Ops)
                Assert.DoesNotContain(forbidden, f => op.Describe().Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Caution_tweaks_explain_the_trade_off_and_irreversible_parts_are_stated()
    {
        foreach (var t in Expanded.Where(t => t.Risk == Risk.Caution)) Assert.False(string.IsNullOrWhiteSpace(t.Warning), t.Id);
        Assert.Contains("Not fully reversible", TweakCatalog.Find("dbl.jumplist-recent")!.Description);
        // Context-menu entries are removed through the per-user blocked list (values), never by deleting system keys.
        foreach (var id in new[] { "gen.ctx-give-access", "gen.ctx-include-library", "gen.ctx-share" })
        {
            var op = Assert.IsType<RegValueOp>(Assert.Single(TweakCatalog.Find(id)!.Ops));
            Assert.Equal(Hive.HKCU, op.Hive);
            Assert.EndsWith(@"Shell Extensions\Blocked", op.Path);
        }
    }

    [Fact]
    public void Sources_file_lists_every_source_used()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MoneyTweaks.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var doc = File.ReadAllText(Path.Combine(root!, "docs", "TWEAK-SOURCES.md"));
        foreach (var name in new[] { "Win11Debloat", "WinUtil", "O&O ShutUp10++", "Sophia Script", "Optimizer" }) Assert.Contains(name, doc);
        foreach (var src in Expanded.Select(t => t.Source!.Split(" (")[0]).Distinct())
            Assert.True(doc.Contains(src.Replace("Chris Titus Tech ", "").Replace(" by Raphire", "").Replace(" by hellzerg", "").Replace(" setting list", "").Replace(" for Windows", ""), StringComparison.Ordinal), src);
    }
}
