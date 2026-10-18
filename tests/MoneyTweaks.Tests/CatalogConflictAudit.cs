using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Lists every catalog pair the compatibility engine treats as conflicting, and pins the expected set.</summary>
public class CatalogConflictAudit(ITestOutputHelper output)
{
    [Fact]
    public void Catalog_conflicts_are_only_real_either_or_choices()
    {
        var all = TweakCatalog.All.Where(t => t.Kind == Core.Model.TweakKind.Toggle).ToList();
        var pairs = new List<string>();
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                if (Compatibility.Between(all[i], all[j]) == Compatibility.Overlap.Conflict) pairs.Add($"{all[i].Id} <> {all[j].Id}");
        foreach (var p in pairs) output.WriteLine(p);
        // Every conflicting pair must be an either/or choice (the three DNS providers); anything else is a catalog bug to review.
        Assert.All(pairs, p => Assert.Matches(@"^net\.dns-\w+ <> net\.dns-\w+$", p));
    }
}
