using System.Windows;
using System.Windows.Threading;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static bool ScreenshotMode { get; private set; }
    /// <summary>Started by Windows at sign-in (--background): open minimized and defer heavy work until shown.</summary>
    public static bool BackgroundStart { get; private set; }
    private static Mutex? _single;
    private static EventWaitHandle? _showSignal;
    private const string MutexName = @"Local\MoneyTweaks.SingleInstance", ShowName = @"Local\MoneyTweaks.Show";

    /// <summary>Fewer animations/effects: user setting, else Windows' "Animation effects" off or a GPU below render tier 2.</summary>
    public static bool ReduceMotion => Settings.ReduceMotion ?? (!SystemParameters.ClientAreaAnimation || (System.Windows.Media.RenderCapability.Tier >> 16) < 2);

    private static System.Windows.Threading.DispatcherTimer? _saveTimer;
    /// <summary>Coalesces rapid setting changes (sliders) into one disk write.</summary>
    public static void SaveSettingsSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTick;
        _saveTimer.Tick += SaveTick;
        _saveTimer.Start();
    }
    private static void SaveTick(object? s, EventArgs e) { _saveTimer!.Stop(); Settings.Save(); }
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static readonly System.Diagnostics.Stopwatch StartupClock = System.Diagnostics.Stopwatch.StartNew();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Background task failed", a.Exception); a.SetObserved(); };

        Settings = AppSettings.Load();
        Log.Info($"Money Tweaks {Version} ({Environment.ProcessPath}) started on {OsInfo.ProductName} (admin: {Admin.IsElevated})");

        UpdateService.CleanupAfterUpdate();
        var shotIdx = Array.IndexOf(e.Args, "--screenshots");
        ScreenshotMode = shotIdx >= 0;
        BackgroundStart = e.Args.Contains(AutoStart.BackgroundArg, StringComparer.OrdinalIgnoreCase) && !ScreenshotMode;
        if (!ScreenshotMode && !ClaimSingleInstance(e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase))) { Shutdown(); return; }
        if (!ScreenshotMode) AutoStart.EnsurePath(Environment.ProcessPath);
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length) _ = win.CaptureScreenshotsAsync(e.Args[shotIdx + 1]);
    }

    /// <summary>
    /// One Money Tweaks at a time (two copies would both run the Fortnite/profile watchers). A second launch brings the
    /// running window to the front and exits. Restarts (--restarted) wait for the old copy to finish closing.
    /// </summary>
    private static bool ClaimSingleInstance(bool restarted)
    {
        try
        {
            _single = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                try { created = _single.WaitOne(restarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
                catch (AbandonedMutexException) { created = true; } // previous copy crashed
            }
            if (!created)
            {
                try { using var ev = EventWaitHandle.OpenExisting(ShowName); ev.Set(); } catch { /* other copy can't be signalled */ }
                Log.Info("Money Tweaks is already running; brought the existing window to the front");
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Another copy runs as administrator and this one isn't: it can't be signalled across that boundary.
            Dialog.Show("Money Tweaks is already running", "Another copy of Money Tweaks is running as administrator. Switch to it from the taskbar.", DialogKind.Info);
            return false;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        var t = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.BringToFront());
        }) { IsBackground = true, Name = "SingleInstanceSignal" };
        t.Start();
        return true;
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI error", e.Exception);
        Dialog.Show("Something went wrong",
            $"{e.Exception.Message}\n\nThe error was logged to:\n{Log.CurrentFile}\n\nNo further changes were made.", DialogKind.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MacroService.Shutdown(); // releases any keys a macro is holding
        ProfileService.Shutdown(); // restores settings changed by an active gaming profile
        DisplayService.Shutdown(); // drops an unconfirmed test resolution
        Settings.Save();
        base.OnExit(e);
    }
}
