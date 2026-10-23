using System.Text.Json;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Tests;

/// <summary>Gaming HUD settings: ordered readings, migration from older settings, themes, built-in game profiles, frame sources.</summary>
public class OverlayHudTests
{
    private const long Ms = 10_000;

    [Fact]
    public void Older_settings_become_an_ordered_list_of_the_same_readings()
    {
        var l = new OverlayLayout { Ram = false, Temps = true };   // as saved by 1.57 and earlier: no Items
        Assert.Null(l.Items);
        Assert.Equal(["fps", "frametime", "low1", "graph", "cpu", "cputemp", "gpu", "gputemp", "target"], l.EffectiveItems());
        l.SetItem(OverlayMetric.Vram, true);
        Assert.Equal(OverlayMetric.Vram, l.Items![^1]);
        l.SetItem(OverlayMetric.Graph, false);
        Assert.False(l.Graph);                                     // the original switches stay in step
        l.Move(OverlayMetric.Vram, -1);
        Assert.Equal(OverlayMetric.Vram, l.Items[^2]);
        l.Move(OverlayMetric.Fps, -1);                             // already first: nothing happens
        Assert.Equal(OverlayMetric.Fps, l.Items[0]);
        var c = l.Clone();
        c.Items!.Add(OverlayMetric.Ping);
        Assert.DoesNotContain(OverlayMetric.Ping, l.Items);        // clones don't share the list
    }

    [Fact]
    public void Own_colours_from_older_settings_keep_a_custom_theme()
    {
        Assert.Equal("Vector", new OverlayLayout().EffectiveTheme);
        var old = new OverlayLayout { AccentColor = "#FFFF5C7A" };
        Assert.Equal("Custom", old.EffectiveTheme);
        Assert.Equal("#FFFF5C7A", OverlayTheme.Resolve(old).Accent);
        Assert.Equal("#FFFFB020", OverlayTheme.Resolve(new OverlayLayout { Theme = "Amber" }).Accent);
        Assert.Equal("Vector", OverlayTheme.Resolve(new OverlayLayout { Theme = "Nonexistent" }).Name);
    }

    [Fact]
    public void Built_in_game_profiles_are_added_once_and_respect_existing_ones()
    {
        var s = OverlaySettings.WithDefaults();
        s.Games[0].AutoShow = false;                               // the user's own choice for Fortnite
        s.Games.Add(new OverlayGameProfile { Exe = "GeometryDash.exe", Name = "GD (mine)", CustomLayout = true, Layout = new OverlayLayout { FontSize = 22 } });
        s.EnsureDefaultGames();
        Assert.Equal(OverlayStyle.Compact, s.LayoutFor("FortniteClient-Win64-Shipping.exe").Style);
        Assert.False(s.ProfileFor("FortniteClient-Win64-Shipping.exe")!.AutoShow);
        Assert.Equal(22, s.LayoutFor("GeometryDash.exe").FontSize);      // not replaced
        Assert.Equal(OverlayStyle.Compact, s.LayoutFor("RobloxPlayerBeta.exe").Style);
        int n = s.Games.Count;
        s.Games.RemoveAll(g => g.Name == "Roblox");
        s.EnsureDefaultGames();
        Assert.Equal(n - 1, s.Games.Count);                        // a removed profile isn't added back
        var fresh = OverlaySettings.WithDefaults();
        fresh.EnsureDefaultGames();
        Assert.Equal(OverlayStyle.Minimal, fresh.LayoutFor("geometrydash.exe").Style);
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.FrameTime, OverlayMetric.Low1], fresh.LayoutFor("GeometryDash").EffectiveItems());
    }

    [Fact]
    public void New_layout_settings_round_trip()
    {
        var s = OverlaySettings.WithDefaults();
        s.EnsureDefaultGames();
        s.ClickThrough = false; s.EditHotkey = "Ctrl+Alt+F9"; s.RefreshMs = 500;
        s.Layout.Items = [OverlayMetric.Gpu, OverlayMetric.Fps];
        s.Layout.Style = OverlayStyle.Minimal; s.Layout.Theme = "Ice"; s.Layout.Scale = 1.4; s.Layout.WindowOpacity = 0.8;
        s.Layout.CustomPosition = true; s.Layout.X = 0.4; s.Layout.Y = 0.1; s.Layout.Monitor = @"\\.\DISPLAY2";
        var back = JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(s))!;
        Assert.False(back.ClickThrough);
        Assert.Equal("Ctrl+Alt+F9", back.EditHotkey);
        Assert.Equal(500, back.RefreshMs);
        Assert.Equal([OverlayMetric.Gpu, OverlayMetric.Fps], back.Layout.Items);
        Assert.Equal(OverlayStyle.Minimal, back.Layout.Style);
        Assert.Equal("Ice", back.Layout.EffectiveTheme);
        Assert.Equal(@"\\.\DISPLAY2", back.Layout.Monitor);
        Assert.Equal(1, back.DefaultsVersion);
        // Settings written by 1.57 (no new fields) load with sensible defaults.
        var old = JsonSerializer.Deserialize<OverlaySettings>("""{"Layout":{"Fps":true,"Ram":false},"Hotkey":"Ctrl+Shift+F10"}""")!;
        Assert.True(old.ClickThrough);
        Assert.Equal("Ctrl+Shift+F11", old.EditHotkey);
        Assert.Equal(0, old.DefaultsVersion);
        Assert.DoesNotContain(OverlayMetric.Ram, old.Layout.EffectiveItems());
    }

    [Fact]
    public void Kernel_presents_count_only_for_apps_without_a_runtime_source()
    {
        var t = new FrameTracker();
        for (int i = 0; i < 60; i++) t.Add(5, i * 16 * Ms, PresentApi.Other);                 // an OpenGL game: kernel presents only
        var gl = t.Stats(5, TimeSpan.FromSeconds(5))!;
        Assert.Equal(PresentApi.Other, gl.Api);
        Assert.Equal(62.5, gl.Fps, 1);
        for (int i = 0; i < 60; i++) t.Add(6, i * 7 * Ms + 3, PresentApi.Other);              // a DirectX game: kernel events first…
        for (int i = 0; i < 60; i++) { t.Add(6, (500 + i * 4) * Ms, PresentApi.Dxgi); t.Add(6, (500 + i * 4) * Ms + 5, PresentApi.Other); }
        var dx = t.Stats(6, TimeSpan.FromSeconds(5))!;
        Assert.Equal(PresentApi.Dxgi, dx.Api);                                                 // …then the runtime takes over
        Assert.Equal(250, dx.Fps, 0);                                                          // and the kernel duplicates are ignored
        Assert.Equal(59, dx.Frames);
    }
}
