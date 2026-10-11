using System.Windows;
using System.Windows.Threading;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static bool ScreenshotMode { get; private set; }
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

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
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length) _ = win.CaptureScreenshotsAsync(e.Args[shotIdx + 1]);
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
        Settings.Save();
        base.OnExit(e);
    }
}
