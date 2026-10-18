using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>Regression tests for the functional-completeness audit (temp folders only; nothing on the PC is changed).</summary>
[Collection("registry")]
public class FunctionalAuditTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-func-" + Guid.NewGuid().ToString("N"));
    public FunctionalAuditTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Applying_a_tweak_whose_target_is_missing_is_not_reported_as_success()
    {
        // A service that doesn't exist: the op is NotApplicable, so Apply must refuse before any change or backup.
        var t = new Tweak
        {
            Id = "test.missing-service", Category = TweakCategory.Services, Name = "missing", Description = "",
            Ops = [new ServiceStartOp("MoneyTweaksNoSuchService_" + Guid.NewGuid().ToString("N")[..8], 4)],
        };
        Assert.Equal(TweakState.NotApplicable, TweakEngine.GetState(t));
        var r = TweakEngine.Apply(t);
        Assert.False(r.Success);
        Assert.Equal(TweakState.NotApplicable, r.NewState);
        Assert.False(BackupStore.Exists(t.Id)); // no stale backup that would later read as "applied"
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]   // Task Manager "enabled"
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, false)]  // Task Manager "disabled"
    [InlineData(new byte[] { 7, 0, 0, 0 }, false)]
    public void Startup_approved_flags_are_read_like_task_manager(byte[]? flag, bool enabled) =>
        Assert.Equal(enabled, StartupManager.IsEnabledFlag(flag));

    [Fact]
    public void Startup_list_includes_startup_folder_entries_when_present()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList() : [];
        var list = StartupManager.List();
        foreach (var f in files) Assert.Contains(list, e => e.Command == f && e.Location == "Startup folder");
    }

    [Theory]
    [InlineData("Power plan → High performance", false)]
    [InlineData("Power plan: High performance is not available on this PC (Modern Standby devices hide it).", true)]
    [InlineData("Could not close Discord: Access is denied.", true)]
    [InlineData("Could not clear standby memory: Requires administrator rights.", true)]
    [InlineData("Memory: Standby list cleared.", false)]
    [InlineData("FortniteClient-Win64-Shipping is not running. Start the game first, then boost.", true)]
    [InlineData("Closed Discord (2)", false)]
    public void Booster_report_lines_are_classified(string line, bool problem) =>
        Assert.Equal(problem, GameBooster.IsProblem(line));
}
