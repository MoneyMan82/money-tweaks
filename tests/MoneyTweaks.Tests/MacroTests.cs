using MoneyTweaks.Core.Macros;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

public sealed class RecordingSink : IInputSink
{
    public readonly List<string> Events = [];
    public readonly HashSet<int> Physical = []; // keys the "user" is holding, for WaitKey steps
    public void Key(int vk, bool down) { lock (Events) Events.Add($"{VirtualKeys.Name(vk)}:{(down ? "down" : "up")}"); }
    public void Mouse(MouseBtn b, bool down) { lock (Events) Events.Add($"{b}:{(down ? "down" : "up")}"); }
    public bool IsKeyDown(int vk) { lock (Physical) return Physical.Contains(vk); }
    public List<string> Snapshot() { lock (Events) return [.. Events]; }
}

[Collection("registry")]
public class MacroTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-macros-" + Guid.NewGuid().ToString("N"));
    public MacroTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static MacroStep S(StepKind k, string key = "", int ms = 0, MouseBtn b = MouseBtn.Left) => new() { Kind = k, Key = key, Ms = ms, Button = b };

    [Fact]
    public void Key_names_roundtrip()
    {
        foreach (var n in new[] { "A", "1", "F12", "LShift", "Space", "Enter", "Num5", "Up" })
        {
            Assert.True(VirtualKeys.TryParse(n, out var vk), n);
            Assert.Equal(n, VirtualKeys.Name(vk), StringComparer.OrdinalIgnoreCase);
        }
        Assert.False(VirtualKeys.TryParse("NotAKey", out _));
    }

    [Fact]
    public async Task Player_runs_steps_in_order_with_repeats_and_timing()
    {
        var sink = new RecordingSink();
        var m = new Macro { Name = "t", Repeat = 2, Steps = [S(StepKind.KeyPress, "A", 10), S(StepKind.Delay, ms: 120), S(StepKind.Combo, "LCtrl+C", 5), S(StepKind.MouseClick, ms: 5, b: MouseBtn.Right)] };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reason = await new MacroPlayer(sink).RunAsync(m, CancellationToken.None);
        Assert.Equal(StopReason.Finished, reason);
        Assert.True(sw.ElapsedMilliseconds >= 240, $"delays honoured ({sw.ElapsedMilliseconds} ms)");
        var once = new[] { "A:down", "A:up", "LCtrl:down", "C:down", "C:up", "LCtrl:up", "Right:down", "Right:up" };
        Assert.Equal(once.Concat(once), sink.Snapshot());
    }

    [Fact]
    public async Task Stop_releases_everything_that_is_held()
    {
        var sink = new RecordingSink();
        var m = new Macro { Name = "t", Repeat = 1, Steps = [S(StepKind.KeyDown, "W"), S(StepKind.MouseDown), S(StepKind.KeyDown, "LShift"), S(StepKind.Delay, ms: 30_000)] };
        using var cts = new CancellationTokenSource(150);
        var reason = await new MacroPlayer(sink).RunAsync(m, cts.Token);
        Assert.Equal(StopReason.Stopped, reason);
        var ev = sink.Snapshot();
        foreach (var k in new[] { "W", "Left", "LShift" }) Assert.Contains($"{k}:up", ev);
        Assert.Equal(ev.Count(e => e.EndsWith(":down")), ev.Count(e => e.EndsWith(":up")));
    }

    [Fact]
    public async Task Hold_trigger_starts_on_press_and_stops_on_release()
    {
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        var m = new Macro { Name = "hold", Trigger = TriggerKind.HoldKey, TriggerKey = "RShift", Repeat = 0, Steps = [S(StepKind.KeyPress, "1", 5), S(StepKind.Delay, ms: 40)] };
        mgr.Armed = [m];
        var stopped = new TaskCompletionSource<StopReason>();
        mgr.Stopped += (_, r, _) => stopped.TrySetResult(r);
        VirtualKeys.TryParse("RShift", out var shift);
        mgr.OnKey(shift, true);
        mgr.OnKey(shift, true); // auto-repeat must not restart or stop it
        await Task.Delay(200);
        Assert.Same(m, mgr.Running);
        mgr.OnKey(shift, false);
        Assert.Equal(StopReason.TriggerReleased, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var ev = sink.Snapshot();
        Assert.True(ev.Count(e => e == "1:down") >= 2, "looped while held");
        Assert.Equal(ev.Count(e => e == "1:down"), ev.Count(e => e == "1:up"));
    }

    [Fact]
    public async Task Toggle_trigger_second_press_stops_and_releases()
    {
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        var m = MacroLibrary.BuiltIns.First(b => b.Id == "builtin.autowalk");
        mgr.Armed = [m];
        var stopped = new TaskCompletionSource<StopReason>();
        mgr.Stopped += (_, r, _) => stopped.TrySetResult(r);
        VirtualKeys.TryParse("F8", out var f8);
        mgr.OnKey(f8, true); mgr.OnKey(f8, false);
        await Task.Delay(150);
        Assert.Contains("W:down", sink.Snapshot());
        mgr.OnKey(f8, true); mgr.OnKey(f8, false);
        Assert.Equal(StopReason.Stopped, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("W:up", sink.Snapshot().Last());
    }

    [Fact]
    public async Task Emergency_stop_releases_immediately()
    {
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        var m = new Macro { Name = "e", Repeat = 1, Steps = [S(StepKind.KeyDown, "A"), S(StepKind.Delay, ms: 30_000)] };
        var stopped = new TaskCompletionSource<StopReason>();
        mgr.Stopped += (_, r, _) => stopped.TrySetResult(r);
        Assert.True(mgr.Start(m));
        await Task.Delay(100);
        mgr.EmergencyStop();
        Assert.Contains("A:up", sink.Snapshot());
        Assert.Equal(StopReason.Emergency, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Blocked_competitive_games_refuse_and_stop_macros()
    {
        var sink = new RecordingSink();
        var fg = "FortniteClient-Win64-Shipping";
        using var mgr = new MacroManager(() => sink, () => fg);
        var m = new Macro { Name = "b", Repeat = 0, Steps = [S(StepKind.KeyPress, "A", 5), S(StepKind.Delay, ms: 20)] };
        string? blocked = null;
        mgr.Stopped += (_, r, g) => { if (r == StopReason.BlockedGame) blocked = g; };
        Assert.False(mgr.Start(m));
        Assert.Equal("Fortnite", blocked);
        Assert.Empty(sink.Snapshot());

        // Started elsewhere, then the user switches to a blocked game: the macro stops and releases.
        fg = "notepad";
        var stopped = new TaskCompletionSource<(StopReason, string?)>();
        mgr.Stopped += (_, r, g) => stopped.TrySetResult((r, g));
        Assert.True(mgr.Start(m));
        await Task.Delay(100);
        fg = "VALORANT-Win64-Shipping";
        var (reason, game) = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(StopReason.BlockedGame, reason);
        Assert.Equal("VALORANT", game);
    }

    [Fact]
    public async Task Safety_timeout_stops_runaway_loops()
    {
        var sink = new RecordingSink();
        using var mgr = new MacroManager(() => sink, () => "notepad");
        var m = new Macro { Name = "loop", Repeat = 0, SafetyTimeoutSec = 1, Steps = [S(StepKind.KeyPress, "A", 1), S(StepKind.Delay, ms: 50)] };
        var stopped = new TaskCompletionSource<StopReason>();
        mgr.Stopped += (_, r, _) => stopped.TrySetResult(r);
        mgr.Start(m);
        Assert.Equal(StopReason.Timeout, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Test_mode_sends_no_input()
    {
        var real = new RecordingSink();
        using var mgr = new MacroManager(() => real, () => "notepad");
        var done = new TaskCompletionSource();
        var steps = 0;
        mgr.Step += (_, _) => Interlocked.Increment(ref steps);
        mgr.Stopped += (_, _, _) => done.TrySetResult();
        mgr.Start(MacroLibrary.BuiltIns.First(b => b.Id == "builtin.paste-enter"), dryRun: true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(real.Snapshot());
        Assert.Equal(3, steps);
    }

    [Fact]
    public void Builtins_are_valid_and_validation_catches_mistakes()
    {
        foreach (var b in MacroLibrary.BuiltIns) Assert.Empty(MacroPolicy.Validate(b, []));
        var bad = new Macro { Name = "", Repeat = 0, Trigger = TriggerKind.PressKey, TriggerKey = "F5", Steps = [S(StepKind.KeyPress, "Nope"), S(StepKind.Combo, "A")] };
        var e = MacroPolicy.Validate(bad, []);
        Assert.Contains(e, x => x.Contains("name"));
        Assert.Contains(e, x => x.Contains("Step 1"));
        Assert.Contains(e, x => x.Contains("Step 2"));
        Assert.Contains(e, x => x.Contains("way to stop"));
    }

    [Fact]
    public void Store_roundtrip_and_import_validation()
    {
        var f = new MacroFile();
        f.Macros.Add(MacroLibrary.BuiltIns[4].Copy("My sequence"));
        MacroStore.Save(f);
        var loaded = MacroStore.Load();
        Assert.Equal("My sequence", loaded.Macros.Single().Name);
        Assert.Equal(TriggerKind.HoldKey, loaded.Macros[0].Trigger);
        Assert.Equal(6, loaded.Macros[0].Steps.Count);

        var path = Path.Combine(_tmp, "m.json");
        MacroStore.Export(loaded.Macros, path);
        var (macros, problems) = MacroStore.Import(path, loaded.Macros);
        Assert.Empty(problems);
        Assert.Equal("My sequence (2)", Assert.Single(macros).Name);

        File.WriteAllText(path, "{\"format\":\"money-tweaks-macros\",\"version\":1,\"macros\":[{\"Name\":\"Evil\",\"Steps\":[{\"Kind\":\"KeyPress\",\"Key\":\"cmd /c del\"}]}]}");
        (macros, problems) = MacroStore.Import(path, []);
        Assert.Empty(macros);
        Assert.Contains(problems, p => p.Contains("choose a key"));
        File.WriteAllText(path, "{\"format\":\"other\"}");
        Assert.Contains(MacroStore.Import(path, []).problems, p => p.Contains("isn't a Money Tweaks macro export"));
    }
}
