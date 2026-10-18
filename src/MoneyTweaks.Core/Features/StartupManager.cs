using Microsoft.Win32;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed record StartupEntry(string Name, string Command, string Location, bool Enabled, RegistryHive Hive, string ApprovedKey);

/// <summary>Lists and toggles Run-key startup apps using the same StartupApproved flags as Task Manager.</summary>
public static class StartupManager
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        void Scan(RegistryKey root, RegistryHive hive, string path, string approvedSub, string label)
        {
            using var k = root.OpenSubKey(path);
            if (k is null) return;
            using var ap = root.OpenSubKey(Approved + approvedSub);
            foreach (var n in k.GetValueNames().Where(n => n.Length > 0))
                list.Add(new StartupEntry(n, k.GetValue(n)?.ToString() ?? "", label, IsEnabledFlag(ap?.GetValue(n) as byte[]), hive, Approved + approvedSub));
        }
        // Shortcuts in the Startup folders: Task Manager lists these too, under StartupApproved\StartupFolder.
        void ScanFolder(RegistryKey root, RegistryHive hive, Environment.SpecialFolder folder, string label)
        {
            string dir;
            try { dir = Environment.GetFolderPath(folder); } catch { return; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            using var ap = root.OpenSubKey(Approved + "StartupFolder");
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var n = Path.GetFileName(f);
                list.Add(new StartupEntry(n, f, label, IsEnabledFlag(ap?.GetValue(n) as byte[]), hive, Approved + "StartupFolder"));
            }
        }
        Scan(Registry.CurrentUser, RegistryHive.CurrentUser, Run, "Run", "Current user");
        Scan(Registry.LocalMachine, RegistryHive.LocalMachine, Run, "Run", "All users");
        Scan(Registry.LocalMachine, RegistryHive.LocalMachine, Run32, "Run32", "All users (32-bit)");
        ScanFolder(Registry.CurrentUser, RegistryHive.CurrentUser, Environment.SpecialFolder.Startup, "Startup folder");
        ScanFolder(Registry.LocalMachine, RegistryHive.LocalMachine, Environment.SpecialFolder.CommonStartup, "Startup folder (all users)");
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>StartupApproved data: no value or an even first byte = enabled, odd first byte = disabled (as Task Manager writes it).</summary>
    public static bool IsEnabledFlag(byte[]? flag) => flag is null || flag.Length == 0 || (flag[0] & 1) == 0;

    /// <summary>Reads the entry's current state back from the registry.</summary>
    public static bool ReadEnabled(StartupEntry e)
    {
        var root = e.Hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
        using var k = root.OpenSubKey(e.ApprovedKey);
        return IsEnabledFlag(k?.GetValue(e.Name) as byte[]);
    }

    public static void SetEnabled(StartupEntry e, bool enabled)
    {
        var root = e.Hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
        if (e.Hive == RegistryHive.LocalMachine && !Admin.IsElevated) throw new UnauthorizedAccessException("All-users startup entries need administrator rights.");
        using var k = root.CreateSubKey(e.ApprovedKey, writable: true);
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(e.Name, data, RegistryValueKind.Binary);
        Log.Info($"Startup app '{e.Name}' {(enabled ? "enabled" : "disabled")}");
    }
}
