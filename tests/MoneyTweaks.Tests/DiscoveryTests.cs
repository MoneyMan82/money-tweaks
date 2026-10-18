using System.Diagnostics;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Discovery;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Intelligent Feature Discovery on the real catalog: natural-language search, related tweaks, conflicts, and a 1,200-entry scale test.</summary>
public class DiscoveryTests(ITestOutputHelper output)
{
    private static TweakKnowledge K => TweakKnowledge.Current;

    [Fact]
    public void Natural_language_query_finds_relevant_tweaks_and_respects_what_you_rule_out()
    {
        var r = TweakSearch.Search(K, "make Fortnite smoother without increasing temperatures");
        Assert.Contains("Fortnite", r.Intent.Games);
        Assert.Contains(SideEffect.MorePowerAndHeat, r.Intent.Avoid);
        Assert.StartsWith("game.fortnite", r.Hits[0].Tweak.Id.Replace("gpu.fortnite-high-performance", "game.fortnite"));
        Assert.True(r.HiddenBySideEffect > 0);
        Assert.All(r.Hits, h => Assert.DoesNotContain(SideEffect.MorePowerAndHeat, K.Profile(h.Tweak.Id)!.Effects));
        Assert.DoesNotContain(r.Hits, h => h.Tweak.Id == "gen.min-processor-state" || h.Tweak.Id == "gen.core-parking");
        // Never invents: every result is a real catalog tweak.
        Assert.All(r.Hits, h => Assert.NotNull(TweakCatalog.Find(h.Tweak.Id)));
        Assert.Contains("no extra heat", r.Intent.Understood());
    }

    [Fact]
    public void Intent_words_rank_by_topic_not_by_verbs_and_known_limits_are_honest()
    {
        var ping = TweakSearch.Search(K, "lower ping");
        Assert.All(ping.Hits.Take(5), h => Assert.Contains("network", K.Profile(h.Tweak.Id)!.Purposes));
        var nagle = ping.Hits.First(h => h.Tweak.Id == "net.nagle");
        Assert.Contains(nagle.Reasons, x => x.StartsWith("limited effect", StringComparison.Ordinal)); // honest: only TCP
        var ads = TweakSearch.Search(K, "stop ads in start menu");
        Assert.Equal("dbl.start-suggestions", ads.Hits[0].Tweak.Id);
        var exact = TweakSearch.Search(K, "nagle");
        Assert.Equal("net.nagle", exact.Hits[0].Tweak.Id);
        Assert.False(exact.Intent.IsNatural);
        // A disabled security trade-off is never offered.
        Assert.DoesNotContain(TweakSearch.Search(K, "gaming performance memory integrity").Hits, h => h.Tweak.Id == "adv.vbs-hvci");
    }

    [Fact]
    public void Hardware_compatibility_and_goal_change_the_ranking()
    {
        var plain = TweakSearch.Search(K, "smoother gaming");
        var blockFirst = plain.Hits[0].Tweak.Id;
        var ctx = new SearchContext(t => t.Id == blockFirst ? CompatSeverity.Block : null);
        var withPc = TweakSearch.Search(K, "smoother gaming", ctx);
        Assert.NotEqual(blockFirst, withPc.Hits[0].Tweak.Id);
        Assert.Contains("not applicable on this PC", withPc.Hits.First(h => h.Tweak.Id == blockFirst).Reasons);
        var cool = TweakSearch.Search(K, "smoother gaming", new SearchContext(GoalPreset: "cool"));
        var hot = cool.Hits.FirstOrDefault(h => K.Profile(h.Tweak.Id)!.Effects.Contains(SideEffect.MorePowerAndHeat));
        if (hot is not null) Assert.Contains(hot.Reasons, x => x.Contains("your goal", StringComparison.Ordinal));
    }

    [Fact]
    public void Related_tweaks_put_conflicts_first_and_explain_the_relationship()
    {
        var dns = K.Related("net.dns-cloudflare");
        Assert.Equal(RelationKind.Conflict, dns[0].Kind);
        Assert.Contains(dns.Take(2), r => r.Other.Id == "net.dns-google");
        Assert.All(dns, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
        var parking = K.Related("gen.core-parking");
        Assert.Equal(RelationKind.TradeOff, parking[0].Kind);
        Assert.Equal("gen.ultimate-plan", parking[0].Other.Id);
        var prefetch = K.Related("adv.prefetch");
        Assert.Contains(prefetch, r => r.Other.Id == "svc.sysmain" && r.Kind == RelationKind.OverlappingEffect);
        // Related never suggests a disabled tweak as something that "works with" it.
        foreach (var t in TweakCatalog.All.Take(120))
            Assert.DoesNotContain(K.Related(t.Id), r => r.Kind == RelationKind.Complements && TweakAudit.DisabledReason(r.Other) is not null);
        // Game Mode's related list reaches beyond its own category.
        Assert.Contains(K.Related("gen.game-mode"), r => r.Other.Category != TweakCategory.General);
    }

    [Fact]
    public void Conflicts_match_the_engine_and_overlaps_are_found_without_noise()
    {
        var conflicts = K.Findings.Where(f => f.Kind == RelationKind.Conflict).ToList();
        Assert.All(conflicts, f => Assert.Equal(Compatibility.Overlap.Conflict, Compatibility.Between(f.A, f.B)));
        Assert.Equal(3, conflicts.Count); // the three DNS choices, as the catalog conflict audit expects
        Assert.Contains(K.Findings, f => f.Kind == RelationKind.OverlappingEffect && new[] { f.A.Id, f.B.Id }.Order().SequenceEqual(new[] { "adv.prefetch", "svc.sysmain" }));
        // The word-collision false positives seen during development stay out.
        Assert.DoesNotContain(K.Findings, f => new[] { f.A.Id, f.B.Id }.Contains("gen.pcie-aspm-off") && f.Kind == RelationKind.OverlappingEffect);
        Assert.DoesNotContain(K.Findings, f => f.A.Id == f.B.Id);
        Assert.Equal(K.Findings.Count, K.Findings.Select(f => string.Join("|", new[] { f.A.Id, f.B.Id }.Order())).Distinct().Count());
    }

    [Fact]
    public void Every_tweak_gets_a_purpose_and_purposes_are_sensible()
    {
        Assert.All(K.Profiles, p => Assert.NotEmpty(p.Purposes));
        Assert.Equal("network", K.Profile("net.nagle")!.Home);
        Assert.Equal("latency", K.Profile("gen.mouse-accel")!.Home);
        Assert.Equal("apps", K.Profiles.First(p => p.Tweak.Category == TweakCategory.AppRemoval).Home);
        Assert.Contains(SideEffect.MorePowerAndHeat, K.Profile("gen.min-processor-state")!.Effects);
        Assert.Contains(SideEffect.WeakerSecurity, K.Profile("adv.vbs-hvci")!.Effects);
        Assert.Empty(K.Profile("net.nagle")!.Games); // mentions Fortnite in its description, but isn't Fortnite-specific
    }

    /// <summary>A library of 1,200+ entries: the real catalog plus renamed copies (so there are many genuine duplicates).</summary>
    [Fact]
    public void Scales_beyond_a_thousand_entries()
    {
        var real = TweakCatalog.All;
        var big = new List<Tweak>(real);
        string[] flavours = ["Pro", "Alt", "Legacy"];
        for (int i = 0; big.Count < 1200; i++)
        {
            var t = real[i % real.Count];
            big.Add(t with { Id = $"{t.Id}.x{i}", Name = $"{t.Name} ({flavours[i % 3]} {i})" });
        }
        var sw = Stopwatch.StartNew();
        var k = new TweakKnowledge(big);
        long build = sw.ElapsedMilliseconds;
        sw.Restart();
        foreach (var t in big.Take(300)) k.Related(t.Id);
        double related = sw.Elapsed.TotalMilliseconds / 300;
        sw.Restart();
        string[] queries = ["make Fortnite smoother without increasing temperatures", "reduce input lag", "privacy", "stop ads in start menu", "lower ping", "boot faster"];
        foreach (var q in queries) TweakSearch.Search(k, q);
        double search = sw.Elapsed.TotalMilliseconds / queries.Length;
        output.WriteLine($"{big.Count} entries: build {build} ms, related {related:0.00} ms each, search {search:0.0} ms per query, {k.Findings.Count} findings");
        Assert.Equal(big.Count, k.Profiles.Count);
        Assert.True(build < 5000, $"build {build} ms");
        Assert.True(related < 20, $"related {related} ms");
        Assert.True(search < 100, $"search {search} ms");
        // Copies writing the same setting are found as duplicates; the copy of a DNS choice conflicts with the other DNS choices.
        Assert.Contains(k.Findings, f => f.Kind == RelationKind.Duplicate);
        var copyOfCloudflare = big.First(t => t.Id.StartsWith("net.dns-cloudflare.x", StringComparison.Ordinal));
        Assert.Contains(k.Related(copyOfCloudflare.Id), r => r.Kind is RelationKind.Conflict or RelationKind.Duplicate);
        Assert.Equal(TweakSearch.Search(k, "nagle").Hits.Count(h => h.Tweak.Name.Contains("Nagle", StringComparison.Ordinal)), big.Count(t => t.Name.Contains("Nagle", StringComparison.Ordinal)));
    }
}
