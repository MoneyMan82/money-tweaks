using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Tests;

public class RunnerTests
{
    private static DateTimeOffset At(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, DateTimeOffset.Now.Offset);

    [Fact]
    public void Off_schedule_is_never_due() => Assert.Null(new RunSchedule { Mode = ScheduleMode.Off }.NextDue(At(8, 12)));

    [Fact]
    public void Interval_schedule_counts_from_the_last_scheduled_run()
    {
        var s = new RunSchedule { Mode = ScheduleMode.Interval, IntervalHours = 4, LastAutoRun = At(8, 10) };
        Assert.Equal(At(8, 14), s.NextDue(At(8, 11)));
        Assert.False(s.IsDue(At(8, 13, 59)));
        Assert.True(s.IsDue(At(8, 14)));
    }

    [Fact]
    public void Daily_schedule_runs_once_per_day_at_the_chosen_time()
    {
        var s = new RunSchedule { Mode = ScheduleMode.Daily, DailyAt = "18:30" };
        Assert.Equal(At(8, 18, 30), s.NextDue(At(8, 9)));      // later today
        Assert.True(s.IsDue(At(8, 19)));                        // missed while closed -> runs when opened
        s.LastAutoRun = At(8, 19);
        Assert.Equal(At(9, 18, 30), s.NextDue(At(8, 20)));     // done for today
        Assert.False(s.IsDue(At(8, 23)));
        s.LastAutoRun = At(8, 9);                               // a run earlier today doesn't count for 18:30
        Assert.True(s.IsDue(At(8, 18, 31)));
    }

    [Fact]
    public void Bad_daily_time_falls_back_to_nine()
    {
        var s = new RunSchedule { Mode = ScheduleMode.Daily, DailyAt = "25:99" };
        Assert.Equal(At(8, 9), s.NextDue(At(8, 8)));
    }

    [Fact]
    public void Prompts_follow_the_cycle_and_forbid_publishing()
    {
        Assert.Contains(".claude/skills/cycle/SKILL.md", AgentRunner.Prompt(RunKind.Cycle));
        foreach (var k in new[] { RunKind.Cycle, RunKind.Brainstorm })
        {
            var p = AgentRunner.Prompt(k);
            Assert.Contains("Never push", p);
            Assert.Contains("progress notes", p);
        }
        Assert.Contains("idea-critic", AgentRunner.Prompt(RunKind.Brainstorm));
        Assert.DoesNotContain("Edit", string.Join(",", AgentRunner.AllowedTools.Where(t => t.StartsWith("Notebook"))));
    }

    [Fact]
    public void Run_log_is_turned_into_readable_lines()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(f,
            [
                """{"type":"system","subtype":"init","model":"claude-opus-5-5"}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Starting the health check."},{"type":"tool_use","name":"Agent","input":{"description":"CI health check","subagent_type":"ci-investigator"}}]}}""",
                """{"type":"stderr","text":"warning: something"}""",
                """{"type":"result","subtype":"success","result":"Cycle done: T-006 in the inbox. token=ghp_abcdefghijklmnopqrstuvwxyz0123"}""",
            ]);
            var lines = AgentRunner.ReadableLog(f);
            Assert.Equal("Session started · claude-opus-5-5", lines[0]);
            Assert.Equal("Starting the health check.", lines[1]);
            Assert.Equal("→ Agent: CI health check", lines[2]);
            Assert.Equal("! warning: something", lines[3]);
            Assert.StartsWith("RESULT: Cycle done", lines[4]);
            Assert.DoesNotContain("ghp_", lines[4]);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Settings_round_trip_and_read_the_old_format()
    {
        var f = Path.Combine(Path.GetTempPath(), "mt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(f, """{"workspace":"C:\\x"}""");            // written by the first version
            var s = CenterSettings.Load(f);
            Assert.Equal(@"C:\x", s.Workspace);
            Assert.Equal(ScheduleMode.Off, s.Schedule.Mode);
            s.Schedule.Mode = ScheduleMode.Daily; s.Schedule.DailyAt = "07:15";
            s.Save(f);
            var again = CenterSettings.Load(f);
            Assert.Equal(ScheduleMode.Daily, again.Schedule.Mode);
            Assert.Equal("07:15", again.Schedule.DailyAt);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Claude_code_is_found_on_this_pc_if_installed()
    {
        var path = AgentRunner.FindClaude();
        if (path is null) return;   // CI runners don't have it
        Assert.EndsWith("claude.exe", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }
}
