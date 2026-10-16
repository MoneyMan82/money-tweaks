using MoneyTweaks.Core.Display;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

/// <summary>Read-only: enumerates displays and uses CDS_TEST (asks the driver without changing anything).</summary>
public class DisplayTests(ITestOutputHelper output)
{
    [Fact]
    public void Aspect_ratio_labels()
    {
        Assert.Equal("16:9", DisplayManager.AspectLabel(1920, 1080));
        Assert.Equal("4:3", DisplayManager.AspectLabel(1440, 1080));
        Assert.Equal("5:4", DisplayManager.AspectLabel(1280, 1024));
        Assert.Equal("16:10", DisplayManager.AspectLabel(1728, 1080));
        Assert.Equal("≈21:9 (64:27)", DisplayManager.AspectLabel(2560, 1080));
        Assert.Equal("≈16:10 (43:27)", DisplayManager.AspectLabel(1720, 1080));
        Assert.Equal("≈3:2 (40:27)", DisplayManager.AspectLabel(1600, 1080));
        Assert.Matches(@"^25:18 \(≈1[.,]39:1, between 4:3 and 3:2\)$", DisplayManager.AspectLabel(1500, 1080)); // not near any common ratio
        Assert.Equal("–", DisplayManager.AspectLabel(0, 1080));
    }

    [Fact]
    public void Validation_rejects_out_of_range()
    {
        Assert.Empty(DisplayManager.Validate(1440, 1080));
        Assert.Equal(2, DisplayManager.Validate(100, 99999).Count);
    }

    [Fact]
    public void Real_displays_are_enumerated_and_driver_test_is_honest()
    {
        var displays = DisplayManager.Displays();
        Assert.NotEmpty(displays);
        Assert.Single(displays, d => d.Primary);
        foreach (var d in displays)
        {
            output.WriteLine($"{d.Label}: now {d.Current}, saved {d.Registry}, native {d.Native}, scaling {d.CurrentScaling}, {d.Modes.Count} modes; 1440x1080 listed: {d.Reports(1440, 1080)}");
            Assert.NotEmpty(d.Modes);
            Assert.True(d.Reports(d.Current.Width, d.Current.Height), "current mode should be in the driver's list");
            // The current mode must pass the driver test; a nonsense mode must not.
            Assert.Equal(ChangeResult.Ok, DisplayManager.Test(d.DeviceName, d.Current.Width, d.Current.Height, d.Current.Hz));
            Assert.NotEqual(ChangeResult.Ok, DisplayManager.Test(d.DeviceName, 7680, 4320, 999));
            output.WriteLine($"  CDS_TEST 1720x1080: {DisplayManager.Test(d.DeviceName, 1720, 1080, 0)}, 1440x1080: {DisplayManager.Test(d.DeviceName, 1440, 1080, 0)}");
        }
        // Nothing changed.
        var again = DisplayManager.Displays();
        Assert.Equal(displays.Select(d => d.Current), again.Select(d => d.Current));
    }

    [Fact]
    public void Fortnite_plan_writes_only_resolution_and_window_keys()
    {
        var keys = FortniteResolution.Values(1440, 1080, FortniteWindowMode.Fullscreen).Select(v => v.key).ToHashSet();
        Assert.Equal(13, keys.Count);
        Assert.Contains("ResolutionSizeX", keys);
        Assert.Contains("FullscreenMode", keys);
        Assert.Contains("bShouldLetterbox", keys);
        Assert.All(FortniteResolution.Values(1440, 1080, FortniteWindowMode.WindowedFullscreen).Where(v => v.key.Contains("FullscreenMode")), v => Assert.Equal("1", v.value));
        if (FortniteResolution.Game.Installed)
        {
            var plan = FortniteResolution.Plan(1440, 1080, FortniteWindowMode.Fullscreen); // read-only
            output.WriteLine($"Fortnite now: {FortniteResolution.Current()}; plan: {string.Join(", ", plan.Select(p => $"{p.Key}:{p.Current ?? "missing"}->{p.New}"))}");
            Assert.Contains(plan, p => !p.Missing);
        }
    }
}
