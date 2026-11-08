using System.Text.Json;
using System.Windows;
using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter;

public partial class App : Application
{
    public static Hub Hub { get; private set; } = null!;
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoneyTweaksAgentCenter");
    private static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // --screenshot <folder>: read-only, off-screen render of every view to PNG (for checking the UI), then exit.
        if (e.Args.Length >= 2 && e.Args[0] == "--screenshot")
        {
            var sws = FindWorkspace(e.Args.Skip(2).ToArray());
            if (sws is null) { Shutdown(2); return; }
            Hub = new Hub(sws, DataFolder);
            Hub.Start(watchFiles: false, listen: false);
            Runner = new AgentRunner(sws, DataFolder);
            var sw = new MainWindow { Left = -6000, Top = -6000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            MainWindow = sw;
            sw.Show();
            _ = sw.CaptureAllAsync(e.Args[1]).ContinueWith(_ => Dispatcher.Invoke(() => Shutdown()));
            return;
        }
        // One instance: the named pipe and the history database belong to one listener.
        _single = new Mutex(true, @"Local\MoneyTweaksAgentCenter", out var first);
        if (!first)
        {
            MessageBox.Show("The Agent Command Center is already running.", "Agent Command Center", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            try { File.AppendAllText(Path.Combine(DataFolder, "app-errors.log"), $"{DateTimeOffset.Now:O} {ex.Exception}\n"); } catch { }
            MessageBox.Show(ex.Exception.Message, "Agent Command Center error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        var ws = FindWorkspace(e.Args);
        if (ws is null)
        {
            MessageBox.Show("Couldn't find the Money Tweaks workspace (the folder with MoneyTweaks.slnx and .claude).\n\n" +
                "Start the app with the folder as argument, or set \"workspace\" in\n" + SettingsPath, "Agent Command Center", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(); return;
        }
        SaveWorkspace(ws.Root);
        Hub = new Hub(ws, DataFolder);
        Hub.Start();
        Runner = new AgentRunner(ws, DataFolder);
        _ = Runner.RefreshAsync(Settings.ClaudePath);
        var w = new MainWindow();
        MainWindow = w;
        // --offscreen <folder>: the real, listening app, but off-screen (pipeline tests), saving a frame every 4 s.
        var off = Array.IndexOf(e.Args, "--offscreen");
        if (off >= 0 && off + 1 < e.Args.Length)
        {
            w.Left = -6000; w.Top = -6000; w.ShowActivated = false; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.StartFrameCapture(e.Args[off + 1]);
        }
        w.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Hub?.Dispose(); } catch { }
        base.OnExit(e);
    }

    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");
    public static CenterSettings Settings { get; private set; } = new();
    public static AgentRunner Runner { get; private set; } = null!;

    public static void SaveSettings() { try { Settings.Save(SettingsPath); } catch { } }

    private static Workspace? FindWorkspace(string[] args)
    {
        Settings = CenterSettings.Load(SettingsPath);
        if (args.Length > 0 && Workspace.Find(args[0]) is { } fromArg) return fromArg;
        if (Workspace.Find(Settings.Workspace) is { } saved) return saved;
        return Workspace.Find(AppContext.BaseDirectory, Environment.CurrentDirectory);
    }

    private static void SaveWorkspace(string root)
    {
        Settings.Workspace = root;
        SaveSettings();
    }
}
