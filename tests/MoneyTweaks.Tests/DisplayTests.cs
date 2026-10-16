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

/// <summary>Works on a temporary copy of a GameUserSettings.ini; the real Fortnite file is never touched.</summary>
[Collection("registry")]
public class FortniteIniTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-fn-" + Guid.NewGuid().ToString("N"));
    private readonly string _ini;
    private const string Sample = "[/Script/FortniteGame.FortGameUserSettings]\r\nResolutionSizeX=2560\r\nResolutionSizeY=1440\r\nLastUserConfirmedResolutionSizeX=2560\r\nLastUserConfirmedResolutionSizeY=1440\r\nDesiredScreenWidth=2560\r\nDesiredScreenHeight=1440\r\nLastUserConfirmedDesiredScreenWidth=2560\r\nLastUserConfirmedDesiredScreenHeight=1440\r\nFullscreenMode=1\r\nLastConfirmedFullscreenMode=1\r\nPreferredFullscreenMode=1\r\nFrameRateLimit=240.000000\r\nbUseVSync=False\r\n\r\n[ScalabilityGroups]\r\nsg.ResolutionQuality=100.000000\r\n";

    public FortniteIniTests()
    {
        Directory.CreateDirectory(_tmp);
        MoneyTweaks.Core.Sys.AppPaths.OverrideRoot(Path.Combine(_tmp, "app"));
        _ini = Path.Combine(_tmp, "GameUserSettings.ini");
        File.WriteAllText(_ini, Sample);
        FortniteResolution.ConfigOverride = _ini;
    }

    public void Dispose()
    {
        FortniteResolution.ConfigOverride = null;
        try { File.SetAttributes(_ini, FileAttributes.Normal); Directory.Delete(_tmp, true); } catch { }
    }

    [Fact]
    public void Apply_writes_only_resolution_keys_backs_up_and_locks()
    {
        var (changed, missing) = FortniteResolution.Apply(1720, 1080, FortniteWindowMode.Fullscreen, lockFile: true);
        Assert.Equal(11, changed);                      // 8 size keys + 3 window-mode keys
        Assert.Equal(2, missing.Count);                 // letterbox keys aren't in this file: skipped, not added
        var text = File.ReadAllText(_ini);
        Assert.Contains("ResolutionSizeX=1720", text);
        Assert.Contains("LastUserConfirmedDesiredScreenHeight=1080", text);
        Assert.Contains("FullscreenMode=0", text);
        Assert.Contains("FrameRateLimit=240.000000", text);       // unrelated settings preserved
        Assert.Contains("sg.ResolutionQuality=100.000000", text);
        Assert.DoesNotContain("bShouldLetterbox", text);
        Assert.True(new FileInfo(_ini).IsReadOnly);
        Assert.Equal(Sample, File.ReadAllText(FortniteResolution.BackupPath)); // original backed up
        Assert.Equal((1720, 1080, "Fullscreen"), FortniteResolution.Current());
    }

    [Fact]
    public void Read_only_file_is_handled_and_backup_is_kept_from_the_first_apply()
    {
        File.SetAttributes(_ini, FileAttributes.ReadOnly);
        FortniteResolution.Apply(1440, 1080, FortniteWindowMode.Fullscreen, lockFile: false);
        Assert.False(new FileInfo(_ini).IsReadOnly);
        FortniteResolution.Apply(1600, 1080, FortniteWindowMode.Fullscreen, lockFile: false);
        Assert.Equal(Sample, File.ReadAllText(FortniteResolution.BackupPath)); // still the true original
        Assert.Contains("back to how it was", FortniteResolution.Restore());
        Assert.Equal(Sample, File.ReadAllText(_ini));
    }

    [Fact]
    public void Reset_keeps_window_mode_and_unlocks()
    {
        FortniteResolution.Apply(1440, 1080, FortniteWindowMode.Fullscreen, lockFile: true);
        FortniteResolution.Reset(2560, 1440);
        Assert.Equal((2560, 1440, "Fullscreen"), FortniteResolution.Current());
        Assert.False(new FileInfo(_ini).IsReadOnly);
    }

    [Fact]
    public void Invalid_input_and_missing_file_give_clear_errors()
    {
        Assert.Throws<ArgumentException>(() => FortniteResolution.Apply(100, 99999, FortniteWindowMode.Fullscreen, false));
        Assert.Equal(Sample, File.ReadAllText(_ini));   // nothing written
        File.Delete(_ini);
        var ex = Assert.Throws<FileNotFoundException>(() => FortniteResolution.Apply(1440, 1080, FortniteWindowMode.Fullscreen, false));
        Assert.Contains("Launch Fortnite once", ex.Message);
        Assert.Null(FortniteResolution.Current());
    }
}
