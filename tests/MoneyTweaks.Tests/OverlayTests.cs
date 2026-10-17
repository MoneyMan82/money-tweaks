using System.Text.Json;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Sys;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class FrameTrackerTests
{
    private const long Ms = 10_000; // 100 ns units per millisecond

    [Fact]
    public void Steady_100_fps()
    {
        var t = new FrameTracker();
        for (int i = 0; i <= 200; i++) t.Add(42, 1_000_000_000 + i * 10 * Ms, PresentApi.Dxgi);
        var s = t.Stats(42, TimeSpan.FromSeconds(1))!;
        Assert.Equal(100, s.Fps, 1);
        Assert.Equal(10, s.AvgMs, 3);
        Assert.Equal(10, s.LastMs, 3);
        Assert.Equal(100, s.Low1PercentFps, 1);
        Assert.Equal(PresentApi.Dxgi, s.Api);
        Assert.True(s.RecentMs.Count <= 120);
    }

    [Fact]
    public void Window_is_anchored_to_newest_delivered_frame_and_goes_stale()
    {
        var t = new FrameTracker();
        long start = 5_000_000_000;
        for (int i = 0; i <= 120; i++) t.Add(3, start + i * 8 * Ms, PresentApi.Dxgi); // 125 FPS for ~1 s
        long newest = start + 120 * 8 * Ms;
        // Events are delivered ~1 s late: "now" is past the newest frame but the stats still describe the last second of frames.
        var s = t.Stats(3, TimeSpan.FromSeconds(1), 120, newest + 900 * Ms)!;
        Assert.Equal(125, s.Fps, 0);
        Assert.Null(t.Stats(3, TimeSpan.FromSeconds(1), 120, newest + 3000 * Ms)); // game stopped drawing
    }

    [Fact]
    public void Stutter_lowers_one_percent_low_not_average_much()
    {
        var times = new List<long>();
        long now = 0;
        for (int i = 0; i < 300; i++) { now += (i % 100 == 50 ? 50 : 5) * Ms; times.Add(now); }
        var s = FrameTracker.Compute(1, PresentApi.Dxgi, times)!;
        Assert.InRange(s.Fps, 150, 200);
        Assert.InRange(s.Low1PercentFps, 19, 21);   // the 50 ms frames = 20 FPS
        Assert.Equal(299, s.Frames);
    }

    [Fact]
    public void Dxgi_wins_over_duplicate_d3d9_and_active_list_and_prune()
    {
        var t = new FrameTracker();
        for (int i = 0; i < 50; i++) { t.Add(7, i * 4 * Ms, PresentApi.Dxgi); t.Add(7, i * 4 * Ms + 1, PresentApi.D3D9); }
        for (int i = 0; i < 10; i++) t.Add(9, i * 16 * Ms, PresentApi.D3D9);
        Assert.Equal(PresentApi.Dxgi, t.Stats(7, TimeSpan.FromSeconds(5))!.Api);
        Assert.Equal(250, t.Stats(7, TimeSpan.FromSeconds(5))!.Fps, 0);
        Assert.Equal(PresentApi.D3D9, t.Stats(9, TimeSpan.FromSeconds(5))!.Api);
        Assert.Equal(7, t.Active(TimeSpan.FromSeconds(5))[0].pid);
        Assert.Null(t.Stats(123, TimeSpan.FromSeconds(1)));
        t.Prune(TimeSpan.FromSeconds(60).Ticks);
        Assert.Empty(t.Active(TimeSpan.FromSeconds(5)));
    }
}

public class OverlaySettingsTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F10", OverlayHotkey.ModCtrl | OverlayHotkey.ModShift, 0x79)]
    [InlineData("alt+O", OverlayHotkey.ModAlt, 0x4F)]
    [InlineData("F9", 0u, 0x78)]
    public void Hotkeys_parse(string text, uint mods, int vk)
    {
        Assert.True(OverlayHotkey.TryParse(text, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("O")]            // plain letter would swallow typing
    [InlineData("Ctrl+Shift")]   // no key
    [InlineData("Hyper+F1")]
    [InlineData("")]
    public void Bad_hotkeys_rejected(string text) => Assert.False(OverlayHotkey.TryParse(text, out _, out _));

    [Fact]
    public void Profiles_resolve_by_exe_and_layouts_round_trip()
    {
        var s = OverlaySettings.WithDefaults();
        s.Games.Add(new OverlayGameProfile { Exe = "GeometryDash", Name = "Geometry Dash", CustomLayout = true, Layout = new OverlayLayout { FontSize = 20, Position = OverlayCorner.BottomRight, Graph = false } });
        Assert.Equal("Fortnite", s.ProfileFor("fortniteclient-win64-shipping.exe")!.Name);
        Assert.Equal(20, s.LayoutFor("GeometryDash.exe").FontSize);
        Assert.Same(s.Layout, s.LayoutFor("FortniteClient-Win64-Shipping.exe")); // no custom layout → default
        Assert.Same(s.Layout, s.LayoutFor("unknown.exe"));
        var back = JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(OverlayCorner.BottomRight, back.LayoutFor("geometrydash.exe").Position);
        Assert.False(back.LayoutFor("geometrydash.exe").Graph);
        Assert.Equal("Ctrl+Shift+F10", back.Hotkey);
    }

    [Fact]
    public void Window_mode_classification()
    {
        var mon = (0, 0, 2560, 1440);
        Assert.Equal(GameWindowMode.ExclusiveFullscreen, GameWindow.Classify(0, 0, 2560, 1440, mon, false, 3));
        Assert.Equal(GameWindowMode.Borderless, GameWindow.Classify(0, 0, 2560, 1440, mon, false, 2));
        Assert.Equal(GameWindowMode.Windowed, GameWindow.Classify(100, 100, 1280, 720, mon, true, 5));
        Assert.Equal(GameWindowMode.Windowed, GameWindow.Classify(0, 0, 2560, 1440, mon, true, 5)); // maximized window with a caption
    }
}

/// <summary>Real checks on this machine: ETW session start/stop/cleanup (or NeedsAdmin when not elevated) and the GPU temperature source.</summary>
[Collection("cpu-measure")]
public class PresentMonitorLiveTests(ITestOutputHelper output)
{
    [Fact]
    public void Session_starts_or_reports_admin_and_always_cleans_up()
    {
        var state = PresentMonitor.Start();
        output.WriteLine($"elevated={Admin.IsElevated} state={state}: {PresentMonitor.Message}");
        try
        {
            // Windows allows administrators and members of "Performance Log Users"; anyone else gets NeedsAdmin.
            if (state == CaptureState.Running)
            {
                Assert.Equal(CaptureState.Running, state);
                Assert.True(PresentMonitor.SessionExists());
                Thread.Sleep(1500);
                output.WriteLine($"present events in 1.5 s: {PresentMonitor.EventsSeen}");
                foreach (var (pid, frames) in PresentMonitor.Frames.Active(TimeSpan.FromSeconds(2)).Take(5))
                {
                    string name; try { using var p = System.Diagnostics.Process.GetProcessById(pid); name = p.ProcessName; } catch { name = "?"; }
                    var st = PresentMonitor.Frames.Stats(pid, TimeSpan.FromSeconds(1));
                    output.WriteLine($"  {name} ({pid}): {frames} presents, {st?.Fps:0.0} FPS, {st?.AvgMs:0.00} ms, api {st?.Api}");
                }
            }
            else Assert.Equal(CaptureState.NeedsAdmin, state); // the only expected refusal
        }
        finally { PresentMonitor.Stop(); }
        Assert.Equal(CaptureState.Stopped, PresentMonitor.State);
        Assert.False(PresentMonitor.SessionExists());
    }

    [Fact]
    public void Gpu_temperature_matches_nvidia_smi_when_available()
    {
        if (!NvApi.DriverPresent) { output.WriteLine("No NVIDIA driver; skipped."); return; }
        var t = NvApi.GpuTemperature();
        var smi = HealthChecks.NvidiaSmi();
        output.WriteLine($"NVAPI: {t} °C; nvidia-smi: {string.Join(", ", smi.Select(g => $"{g.Name} {g.TempC} °C"))}");
        Assert.NotNull(t);
        if (smi.FirstOrDefault()?.TempC is { } s) Assert.InRange(t!.Value, s - 6, s + 6);
    }
}
