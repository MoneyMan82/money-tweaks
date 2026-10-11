using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace MoneyTweaks.Core.Sys;

public static class AppPaths
{
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoneyTweaks");
    public static string Backups => Ensure(Path.Combine(Root, "backups"));
    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string FileBackups => Ensure(Path.Combine(Root, "file-backups"));
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    /// <summary>Used by tests to isolate state.</summary>
    public static void OverrideRoot(string root) => Root = root;

    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

public static class Admin
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Relaunch the current executable elevated. Returns false if the user declined UAC.</summary>
    public static bool RestartElevated(string? args = null)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

public static class OsInfo
{
    public static int Build { get; } = Environment.OSVersion.Version.Build;
    public static bool IsWindows11 => Build >= 22000;

    public static string ProductName
    {
        get
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            // ProductName still says "Windows 10" on Windows 11.
            if (IsWindows11) name = name.Replace("Windows 10", "Windows 11");
            var disp = k?.GetValue("DisplayVersion") as string;
            return disp is null ? name : $"{name} {disp} (build {Build})";
        }
    }
}
