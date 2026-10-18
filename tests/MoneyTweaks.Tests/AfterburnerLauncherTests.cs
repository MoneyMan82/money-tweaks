using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Tests;

/// <summary>Starting MSI Afterburner on demand. Nothing is launched: start, detection and waiting are fakes.</summary>
[Collection("registry")]
public class AfterburnerLauncherTests : IDisposable
{
    private bool _running, _elevated = true;
    private string? _exe = @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe";
    private int _starts, _asks, _readyAfterPolls = 2, _polls;
    private bool _answerStart = true, _answerDontAsk;
    private bool? _remembered;

    public AfterburnerLauncherTests()
    {
        AfterburnerLauncher.IsRunning = () => _running || (_starts > 0 && ++_polls >= _readyAfterPolls);
        AfterburnerLauncher.IsElevated = () => _elevated;
        AfterburnerLauncher.FindExe = () => _exe;
        AfterburnerLauncher.Start = _ => { _starts++; return true; };
        AfterburnerLauncher.Delay = _ => Task.CompletedTask;
    }

    public void Dispose()
    {
        AfterburnerLauncher.IsRunning = () => Afterburner.IsRunning;
        AfterburnerLauncher.IsElevated = () => MoneyTweaks.Core.Sys.Admin.IsElevated;
        AfterburnerLauncher.Start = _ => false;
        AfterburnerLauncher.Delay = Task.Delay;
    }

    private Task<(LaunchOutcome, string)> Run(bool auto) =>
        AfterburnerLauncher.EnsureRunningAsync(auto, () => { _asks++; return (_answerStart, _answerDontAsk); }, r => _remembered = r, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Asks_first_then_starts_and_waits_for_the_control_interface()
    {
        var (o, _) = await Run(auto: false);
        Assert.Equal(LaunchOutcome.Started, o);
        Assert.Equal(1, _asks);
        Assert.Equal(1, _starts);
        Assert.Null(_remembered);
    }

    [Fact]
    public async Task Dont_ask_again_is_remembered_and_auto_mode_skips_the_question()
    {
        _answerDontAsk = true;
        await Run(auto: false);
        Assert.True(_remembered);
        _starts = 0; _polls = 0; _asks = 0;
        var (o, _) = await Run(auto: true);
        Assert.Equal(LaunchOutcome.Started, o);
        Assert.Equal(0, _asks);
    }

    [Fact]
    public async Task Declining_starts_nothing()
    {
        _answerStart = false;
        var (o, _) = await Run(auto: false);
        Assert.Equal(LaunchOutcome.Declined, o);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task Already_running_not_elevated_and_not_installed_are_reported_without_starting()
    {
        _running = true;
        Assert.Equal(LaunchOutcome.AlreadyRunning, (await Run(auto: true)).Item1);
        _running = false; _elevated = false;
        Assert.Equal(LaunchOutcome.NeedsAdmin, (await Run(auto: true)).Item1);
        _elevated = true; _exe = null;
        Assert.Equal(LaunchOutcome.NotInstalled, (await Run(auto: true)).Item1);
        Assert.Equal(0, _starts);
        Assert.Equal(0, _asks);
    }

    [Fact]
    public async Task Gives_up_honestly_if_afterburner_never_becomes_ready()
    {
        _readyAfterPolls = int.MaxValue;
        var (o, msg) = await AfterburnerLauncher.EnsureRunningAsync(true, () => (true, false), _ => { }, TimeSpan.FromMilliseconds(30));
        Assert.Equal(LaunchOutcome.TimedOut, o);
        Assert.Contains("didn't appear", msg);
    }
}
