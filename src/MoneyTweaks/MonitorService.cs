using System.Windows;
using System.Windows.Threading;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Views;

namespace MoneyTweaks;

/// <summary>
/// App-level wiring of the sensor hub: settings, background history recording (from the first visit to the Hardware
/// Monitor, if enabled), CSV logging, warning toasts and the compact window. The hub itself only samples while one of these
/// (or a live view such as the dashboard) holds it.
/// </summary>
public static class MonitorService
{
    private static readonly object BackgroundToken = new(), LogToken = new();
    public static SensorHub Hub => SensorHub.Shared;
    private static MonitorSettings S => App.Settings.Monitor;

    public static void Init()
    {
        S.RefreshMs = MonitorSettings.RefreshChoices.Contains(S.RefreshMs) ? S.RefreshMs : 1000;
        S.RangeMinutes = MonitorSettings.RangeChoices.Contains(S.RangeMinutes) ? S.RangeMinutes : 1;
        Hub.Settings = () => App.Settings.Monitor;
        Hub.AlertStarted += a => Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!S.AlertToasts || MonitorView.IsShown || App.ScreenshotMode) return;
            MainWindow.Current?.ShowToast($"{a.Title}. {a.Detail}", a.Level == AlertLevel.Critical ? "Danger" : "Gold");
        });
        if (S.LogToFile) Hub.Acquire(LogToken);
        // Opted in: start the CPU sensor helper (Windows asks for administrator permission once).
        if (S.StartCpuSensors && S.UseLibreHardwareMonitor && !Admin.IsElevated && LhmSource.PawnIoInstalled && !App.ScreenshotMode)
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ElevatedSensors.Start());
        if (S.CompactOpen && !App.ScreenshotMode) Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => CompactMonitorWindow.Open());
    }

    /// <summary>The monitor page appeared: record history (and keep recording after it closes when that's on).</summary>
    public static void MonitorShown() => Hub.Acquire(BackgroundToken);

    public static void MonitorHidden() { if (!S.RecordInBackground) Hub.Release(BackgroundToken); }

    public static void SetRecordInBackground(bool on)
    {
        S.RecordInBackground = on;
        App.SaveSettingsSoon();
        if (!on && !MonitorView.IsShown) Hub.Release(BackgroundToken);
    }

    public static void SetLogging(bool on)
    {
        S.LogToFile = on;
        App.SaveSettingsSoon();
        if (on) Hub.Acquire(LogToken);
        else { Hub.Release(LogToken); Hub.CloseLog(); }
    }

    public static void Shutdown()
    {
        S.CompactOpen = CompactMonitorWindow.IsOpen;
        CompactMonitorWindow.Current?.Close();
        Hub.Dispose();   // flushes and closes the CSV log
    }
}
