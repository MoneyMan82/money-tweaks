using System.Text.Json;
using Microsoft.Win32;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

public static partial class TweakCatalog
{
    private const string NicClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string GpuPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Full path of FortniteClient-Win64-Shipping.exe from the Epic Games Launcher's install manifests, or null.</summary>
    internal static string? FortniteExePath()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                if (!(root.TryGetProperty("AppName", out var app) && app.GetString() == "Fortnite") &&
                    !(root.TryGetProperty("DisplayName", out var dn) && dn.GetString() == "Fortnite")) continue;
                if (!root.TryGetProperty("InstallLocation", out var loc) || loc.GetString() is not { } install) continue;
                var exe = Path.Combine(install, @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { /* unreadable manifest */ }
        return null;
    }

    /// <summary>Network adapters (registry instances) whose driver exposes the given standardized keyword, e.g. *EEE.</summary>
    internal static IEnumerable<string> NicKeysWith(string keyword)
    {
        using var root = Registry.LocalMachine.OpenSubKey(NicClass);
        if (root is null) yield break;
        foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            RegistryKey? k = null;
            try { k = root.OpenSubKey(name); } catch { /* protected instance */ }
            if (k is null) continue;
            using (k)
                if (k.GetValue(keyword) is not null && k.GetValue("NetCfgInstanceId") is string) yield return $@"{NicClass}\{name}";
        }
    }

    private static IEnumerable<Tweak> Extra()
    {
        yield return T(TweakCategory.Advanced, "adv.windowed-optimizations", "Optimizations for windowed games",
            "Turns on Windows 11's 'Optimizations for windowed games' (Settings → Display → Graphics). DirectX 10/11 games in windowed or borderless mode then use the modern flip presentation model, which lowers latency and enables VRR. Only this one entry in DirectX's settings string is changed.",
            new RegStringFlagOp(Hive.HKCU, GpuPrefs, "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable", "1")) with { MinBuild = 22621, Group = "GPU" };

        yield return T(TweakCategory.GameProfiles, "gpu.fortnite-high-performance", "Fortnite: use the high-performance GPU",
            "On PCs with two graphics processors (e.g. integrated + NVIDIA/AMD), sets Windows' per-app graphics preference for Fortnite to 'High performance', exactly like Settings → Display → Graphics. Fortnite's install is found through the Epic Games Launcher. Shown as not available on single-GPU PCs.",
            new DynamicRegOp("GpuPreference=2 for FortniteClient-Win64-Shipping.exe", false, () =>
                GpuPnpIds().Count() >= 2 && FortniteExePath() is { } exe
                    ? [new RegValueOp(Hive.HKCU, GpuPrefs, exe, RegistryValueKind.String, "GpuPreference=2;")]
                    : [])) with { Group = "GPU preference" };

        yield return T(TweakCategory.Networking, "net.eee-off", "Disable Energy-Efficient Ethernet",
            "Turns off Energy-Efficient Ethernet (802.3az) on adapters whose driver supports it. EEE lets the link doze between packets, which can add small latency spikes on some adapters and switches. Wi-Fi is not affected.",
            new DynamicRegOp("*EEE=0 on supporting adapters", true, () => NicKeysWith("*EEE").Select(p => new RegValueOp(Hive.HKLM, p, "*EEE", RegistryValueKind.String, "0")))) with
        { RequiresReboot = true, Group = "Adapter" };

        yield return T(TweakCategory.General, "gen.pcie-aspm-off", "Disable PCI Express link power saving",
            "Sets 'PCI Express → Link State Power Management' to Off in the active power plan, so the GPU and NVMe links don't drop into low-power states between bursts. Desktops only; uses a little more idle power.",
            new PowerCfgOp("SUB_PCIEXPRESS", "ASPM", 0, "PCI Express link state power management")) with { Group = "Power" };

        yield return T(TweakCategory.General, "gen.disk-idle-off", "Never turn off hard disks",
            "Sets 'Turn off hard disk after' to Never in the active power plan, so a game installed on a spinning hard drive never waits for it to spin up again. No effect on SSDs.",
            new PowerCfgOp("sub_disk", "DISKIDLE", 0, "Turn off hard disk after")) with { Group = "Power" };

        yield return T(TweakCategory.General, "gen.toasts-off", "Turn off notification pop-ups",
            "Stops app and system notification banners from popping up over games. Notifications still collect in the Notification Center. Undo turns banners back on.",
            CU(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, 1)) with { Risk = Risk.Caution, Group = "Gaming", Warning = "You won't see pop-ups for messages, reminders or security alerts until you undo this. They remain in the Notification Center." };
    }
}
