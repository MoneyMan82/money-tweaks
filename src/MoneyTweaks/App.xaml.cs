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
    /// <summary>Set by the tray menu's Exit; otherwise closing the window in background mode only hides it.</summary>
    public static bool ExitRequested { get; set; }
    /// <summary>The user sent the app to the background (tray) from the window; closing the window then hides it too.</summary>
    public static bool RunningInBackground { get; private set; }

    /// <summary>
    /// Keeps Money Tweaks running as a background process with only a tray icon, the same way a start with Windows does:
    /// profile auto-switch, Fortnite auto resolution, overlay hotkey and macro emergency stop keep working.
    /// </summary>
    public static void EnterBackground()
    {
        RunningInBackground = true;
        Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        TrayIcon.Create();
        Log.Info("Sent to the background by the user (tray icon only)");
    }
    private static Mutex? _single;
    private static EventWaitHandle? _showSignal, _exitSignal;
    private const string MutexName = @"Local\MoneyTweaks.SingleInstance", ShowName = @"Local\MoneyTweaks.Show", ExitName = @"Local\MoneyTweaks.Exit";

    /// <summary>Asks a running copy to exit normally (used by the uninstaller). False if none is listening.</summary>
    public static bool SignalExit()
    {
        try { using var ev = EventWaitHandle.OpenExisting(ExitName); ev.Set(); return true; }
        catch { return false; }
    }

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
    /// <summary>Display version in the two-decimal scheme ("1.24"); see AppVersioning.</summary>
    public static string Version => AppVersioning.Display(typeof(App).Assembly.GetName().Version ?? new System.Version(1, 0));

    public static readonly System.Diagnostics.Stopwatch StartupClock = System.Diagnostics.Stopwatch.StartNew();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Background task failed", a.Exception); a.SetObserved(); };

        Settings = AppSettings.Load();
        if (Settings.LegacyUi) LegacyTheme.Apply(Resources); // before any window or style is created
        // Run by the uninstaller: undo everything Money Tweaks changed, then exit. No watchers, tray or main window.
        if (UninstallMode.Handles(e.Args)) { UninstallMode.Run(this, e.Args); return; }
        Controls.Motion.InstallSmoothScrolling(); // smooth wheel scrolling in every scrollable area (off with reduced motion)
        Controls.Motion.InstallProgressGlide();   // determinate progress bars glide to new values
        Controls.Motion.InstallSquareWindows();   // square window corners on Windows 11, like the rest of the design
        MonitorService.Init();                    // sensor hub settings, CSV logging, warning toasts, compact window
        Log.Info($"Money Tweaks {Version} ({Environment.ProcessPath}) started on {OsInfo.ProductName} (admin: {Admin.IsElevated})");

        UpdateService.CleanupAfterUpdate();
        var shotIdx = Array.IndexOf(e.Args, "--screenshots");
        ScreenshotMode = shotIdx >= 0;
        BackgroundStart = e.Args.Contains(AutoStart.BackgroundArg, StringComparer.OrdinalIgnoreCase) && !ScreenshotMode;
        if (!ScreenshotMode && !ClaimSingleInstance(e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase))) { Shutdown(); return; }
        if (!ScreenshotMode) AutoStart.EnsurePath(Environment.ProcessPath);
        var win = new MainWindow();
        MainWindow = win;
        if (BackgroundStart)
        {
            // Background process: no window, just a tray icon. The window gets a handle (for the emergency-stop
            // hotkey) and runs its startup tasks, so every watcher works without being shown.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
            TrayIcon.Create();
            _ = win.StartupTasksAsync();
        }
        else win.Show();
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
        // The uninstaller asks the running copy to exit normally before it undoes Money Tweaks' changes.
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitName);
        new Thread(() =>
        {
            if (_exitSignal.WaitOne())
                Current?.Dispatcher.BeginInvoke(() => { Log.Info("Exit requested by the uninstaller"); ExitRequested = true; Current.Shutdown(); });
        }) { IsBackground = true, Name = "UninstallExitSignal" }.Start();
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
        OverlayService.Shutdown(); // stops the frame-timing ETW session
        CrosshairService.Shutdown(); // saves profiles, releases the crosshair hotkeys
        ProfileService.Shutdown(); // restores settings changed by an active gaming profile
        DisplayService.Shutdown(); // drops an unconfirmed test resolution
        if (FanCurveService.Running) FanCurveService.Stop(); // the curve only runs while Money Tweaks does: fan back to automatic
        MonitorService.Shutdown(); // closes the compact window and flushes the sensor log
        TrayIcon.Dispose();
        Settings.Save();
        base.OnExit(e);
    }
}
