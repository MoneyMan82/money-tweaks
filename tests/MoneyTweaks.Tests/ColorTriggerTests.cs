using MoneyTweaks.Core.Macros;

namespace MoneyTweaks.Tests;

public class ColorTriggerTests
{
    private static byte[] Px(params (byte r, byte g, byte b)[] colors) => colors.SelectMany(c => new[] { c.b, c.g, c.r, (byte)255 }).ToArray();

    [Fact]
    public void Modes_evaluate_with_tolerance()
    {
        (int, int, int)? bl = null;
        var appear = new ColorTrigger { Color = "#FF8800", Tolerance = 10, Mode = ColorMode.Appears };
        Assert.True(appear.Evaluate(Px((0, 0, 0), (250, 140, 5)), ref bl));
        Assert.False(appear.Evaluate(Px((0, 0, 0), (230, 136, 0)), ref bl));
        var gone = new ColorTrigger { Color = "#FF8800", Tolerance = 10, Mode = ColorMode.Disappears };
        Assert.True(gone.Evaluate(Px((0, 0, 0)), ref bl));
        var change = new ColorTrigger { Tolerance = 20, Mode = ColorMode.Changes };
        bl = null;
        Assert.False(change.Evaluate(Px((100, 100, 100)), ref bl));   // establishes baseline
        Assert.False(change.Evaluate(Px((110, 105, 100)), ref bl));
        Assert.True(change.Evaluate(Px((160, 100, 100)), ref bl));
        Assert.True(ColorTrigger.TryParseHex("#00ff7f", out var c) && c == (0, 255, 127));
        Assert.False(ColorTrigger.TryParseHex("#12345", out _));
    }

    private sealed class FakeScreen
    {
        public (byte r, byte g, byte b) A = (0, 0, 0), B = (0, 0, 0);
        public byte[] Capture(int x, int y, int w, int h)
        {
            var d = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                var c = x + (i % w) < 100 ? A : B;
                d[i * 4] = c.b; d[i * 4 + 1] = c.g; d[i * 4 + 2] = c.r; d[i * 4 + 3] = 255;
            }
            return d;
        }
    }

    private static Macro ColorMacro(TriggerLogic logic, int repeat = 1) => new()
    {
        Name = "c", Trigger = TriggerKind.Color, ColorLogic = logic, ColorPollMs = 50, Repeat = repeat,
        ColorTriggers = [new() { X = 10, Color = "#00FF00", Tolerance = 5 }, new() { X = 500, Color = "#0000FF", Tolerance = 5 }],
        Steps = [new() { Kind = StepKind.KeyPress, Key = "A", Ms = 5 }, new() { Kind = StepKind.Delay, Ms = 30 }],
    };

    [Fact]
    public async Task Watcher_and_or_logic_with_edges()
    {
        var screen = new FakeScreen();
        using var w = new ColorWatcher(screen.Capture, () => "notepad");
        var andM = ColorMacro(TriggerLogic.All);
        var orM = ColorMacro(TriggerLogic.Any); orM.Id = "or";
        var rising = new List<string>();
        var falling = new List<string>();
        w.Rising += m => { lock (rising) rising.Add(m.Id == "or" ? "or" : "and"); };
        w.Falling += m => { lock (falling) falling.Add(m.Id == "or" ? "or" : "and"); };
        w.Start([andM, orM]);
        await Task.Delay(200);
        Assert.Empty(rising);
        screen.A = (0, 255, 0);                      // only first condition true
        await Task.Delay(250);
        lock (rising) Assert.Equal(["or"], rising);
        Assert.True(w.IsTrue(orM));
        Assert.False(w.IsTrue(andM));
        screen.B = (0, 0, 255);                      // both true
        await Task.Delay(250);
        lock (rising) Assert.Contains("and", rising);
        lock (rising) Assert.Single(rising, "or");    // no repeated rising edge while it stays true
        screen.A = (0, 0, 0); screen.B = (0, 0, 0);
        await Task.Delay(250);
        lock (falling) Assert.Contains("or", falling);
        lock (falling) Assert.Contains("and", falling);
    }

    [Fact]
    public async Task Watcher_pauses_in_blocked_games()
    {
        var screen = new FakeScreen { A = (0, 255, 0), B = (0, 0, 255) };
        var fg = "cs2";
        using var w = new ColorWatcher(screen.Capture, () => fg);
        int fired = 0;
        bool sawPaused = false;
        w.Rising += _ => Interlocked.Increment(ref fired);
        w.Status += s => { if (s.PausedForGame) sawPaused = true; };
        w.Start([ColorMacro(TriggerLogic.All)]);
        await Task.Delay(250);
        Assert.Equal(0, fired);
        Assert.True(sawPaused);
        fg = "notepad";
        await Task.Delay(250);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Color_trigger_starts_macro_and_loop_stops_when_color_disappears()
    {
        var screen = new FakeScreen();
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        using var w = new ColorWatcher(screen.Capture, () => "notepad");
        var m = ColorMacro(TriggerLogic.Any, repeat: 0);
        mgr.Armed = [m];
        w.Rising += mgr.OnColorRising;
        w.Falling += mgr.OnColorFalling;
        var stopped = new TaskCompletionSource<StopReason>();
        mgr.Stopped += (_, r, _) => stopped.TrySetResult(r);
        w.Start([m]);
        screen.A = (0, 255, 0);
        await Task.Delay(300);
        Assert.Same(m, mgr.Running);
        screen.A = (0, 0, 0);
        Assert.Equal(StopReason.TriggerReleased, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var ev = sink.Snapshot();
        Assert.Contains("A:down", ev);
        Assert.Equal(ev.Count(e => e == "A:down"), ev.Count(e => e == "A:up"));
    }

    [Fact]
    public void Key_trigger_with_color_condition_is_AND()
    {
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        var m = new Macro { Name = "k", Trigger = TriggerKind.PressKey, TriggerKey = "F5", Repeat = 1, ColorTriggers = [new() { Color = "#FFFFFF" }], Steps = [new() { Kind = StepKind.KeyPress, Key = "B", Ms = 1 }] };
        bool colorOk = false;
        mgr.ColorGate = _ => colorOk;
        mgr.Armed = [m];
        VirtualKeys.TryParse("F5", out var f5);
        mgr.OnKey(f5, true); mgr.OnKey(f5, false);
        Assert.Null(mgr.Running);
        colorOk = true;
        mgr.OnKey(f5, true); mgr.OnKey(f5, false);
        Assert.Same(m, mgr.Running);
        mgr.EmergencyStop();
    }

    [Fact]
    public async Task Pause_releases_and_resume_represses()
    {
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        var m = new Macro { Name = "p", Repeat = 1, Steps = [new() { Kind = StepKind.KeyDown, Key = "W" }, new() { Kind = StepKind.Delay, Ms = 400 }, new() { Kind = StepKind.KeyPress, Key = "E", Ms = 1 }] };
        var done = new TaskCompletionSource<StopReason>();
        mgr.Stopped += (_, r, _) => done.TrySetResult(r);
        mgr.Start(m);
        await Task.Delay(100);
        mgr.Pause();
        Assert.True(mgr.Paused);
        Assert.Equal("W:up", sink.Snapshot().Last());
        await Task.Delay(600);                       // delay step finishes but the next step waits
        Assert.DoesNotContain("E:down", sink.Snapshot());
        mgr.Resume();
        Assert.Equal(StopReason.Finished, await done.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var ev = sink.Snapshot();
        Assert.Equal(["W:down", "W:up", "W:down", "E:down", "E:up", "W:up"], ev);
    }

    [Fact]
    public void Validation_covers_color_triggers()
    {
        var m = new Macro { Name = "v", Trigger = TriggerKind.Color, Steps = [new() { Kind = StepKind.KeyPress, Key = "A" }] };
        Assert.Contains(MacroPolicy.Validate(m, []), e => e.Contains("colour condition"));
        m.ColorTriggers.Add(new() { Color = "nope", Width = 500 });
        m.ColorPollMs = 10;
        var errs = MacroPolicy.Validate(m, []);
        Assert.Contains(errs, e => e.Contains("#FF8800"));
        Assert.Contains(errs, e => e.Contains("region"));
        Assert.Contains(errs, e => e.Contains("interval"));
    }

    [Fact]
    public void Real_screen_capture_returns_pixels()
    {
        // Read-only smoke test of GDI capture on this machine (no input is sent).
        var (x, y) = ScreenCapture.CursorPosition();
        var px = ScreenCapture.Capture(x, y, 4, 3);
        Assert.NotNull(px);
        Assert.Equal(4 * 3 * 4, px!.Length);
    }
}

/// <summary>Measures whole-process CPU, so it must not run alongside other tests.</summary>
[CollectionDefinition("cpu-measure", DisableParallelization = true)]
public class CpuMeasureCollection { }

[Collection("cpu-measure")]
public class ColorWatcherCpuTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Real_watcher_cpu_usage_is_low()
    {
        // 4 conditions, 50x50 px each, every 100 ms, on the real screen. Reports average CPU of one core.
        var m = new Macro { Name = "cpu", Trigger = TriggerKind.Color, ColorPollMs = 100, ColorLogic = TriggerLogic.Any,
            ColorTriggers = Enumerable.Range(0, 4).Select(i => new ColorTrigger { X = i * 60, Y = 0, Width = 50, Height = 50, Color = "#123456", Tolerance = 3 }).ToList(),
            Steps = [new() { Kind = StepKind.KeyPress, Key = "A" }] };
        using var w = new ColorWatcher(foreground: () => "notepad");
        int ticks = 0;
        w.Status += _ => Interlocked.Increment(ref ticks);
        // Idle baseline of this test process (xUnit host threads etc.).
        var p0 = System.Diagnostics.Process.GetCurrentProcess();
        await Task.Delay(500);
        p0.Refresh();
        var idle0 = p0.TotalProcessorTime;
        var idleSw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(2000);
        p0.Refresh();
        var idlePct = 100.0 * (p0.TotalProcessorTime - idle0).TotalMilliseconds / idleSw.ElapsedMilliseconds;
        w.Start([m]);
        await Task.Delay(700); // warm-up (JIT) excluded from the measurement
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var t0 = Volatile.Read(ref ticks);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(3000);
        w.Stop();
        proc.Refresh();
        var cpuPct = 100.0 * (proc.TotalProcessorTime - cpu0).TotalMilliseconds / sw.ElapsedMilliseconds;
        ticks -= t0;
        output.WriteLine($"{ticks} checks in {sw.ElapsedMilliseconds} ms, process CPU ≈ {cpuPct:0.0}% of one core (measured after warm-up; host start-up window was {idlePct:0.0}%)");
        Assert.InRange(ticks, 20, 40);
        Assert.True(cpuPct < 5, $"process CPU with watcher running {cpuPct:0.0}%");
    }
}
