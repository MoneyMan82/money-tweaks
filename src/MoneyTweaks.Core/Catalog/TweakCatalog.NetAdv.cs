using System.Management;
using Microsoft.Win32;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

public static partial class TweakCatalog
{
    private const string TcpIfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    /// <summary>bcdedit line for disabledynamictick set to Yes, in the languages bcdedit translates it to.</summary>
    internal const string DynamicTickOnPattern = @"^\s*disabledynamictick\s+(Yes|Ja|Oui|Sí|Si|Sim|Kyllä|Tak|Да|Так|Evet|Igen|Ano|Áno|Da|Jah|Taip|Jā|Ναι|是|はい|예)\s*$";
    private const string TcpParams = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    private const string SysProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MemMgmt = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    /// <summary>Every TCP/IP interface that currently has (or had) an IPv4 address.</summary>
    internal static IEnumerable<string> ActiveTcpInterfaces()
    {
        using var root = Registry.LocalMachine.OpenSubKey(TcpIfaces);
        if (root is null) yield break;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k is null) continue;
            var dhcp = k.GetValue("DhcpIPAddress") as string;
            var ips = k.GetValue("IPAddress") as string[];
            if ((!string.IsNullOrEmpty(dhcp) && dhcp != "0.0.0.0") || (ips?.Any(i => i != "0.0.0.0" && i.Length > 0) ?? false))
                yield return $@"{TcpIfaces}\{name}";
        }
    }

    /// <summary>PCI device instance paths of all display adapters.</summary>
    internal static IEnumerable<string> GpuPnpIds()
    {
        var ids = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController");
            foreach (var o in s.Get())
                if (o["PNPDeviceID"] is string id && id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) ids.Add(id);
        }
        catch { /* WMI unavailable */ }
        return ids;
    }

    private const string DnsCheck = "Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $_.ServerAddresses.Count -gt 0 } | ForEach-Object { $_.ServerAddresses -join ',' }";
    private const string DnsReset = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses }; Clear-DnsClientCache";
    private static string DnsSet(string a, string b) =>
        $"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object {{ Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses ('{a}','{b}') }}; Clear-DnsClientCache";

    private static IEnumerable<Tweak> Networking()
    {
        const TweakCategory N = TweakCategory.Networking;

        yield return T(N, "net.nagle", "Disable Nagle's algorithm",
            "Sets TcpAckFrequency=1 and TCPNoDelay=1 on every active network interface, so small TCP packets are acknowledged immediately instead of being batched. TCP only: Fortnite and most shooters send gameplay over UDP, so in-match ping is not affected.",
            new DynamicRegOp("TcpAckFrequency=1 on active interfaces", true, () => ActiveTcpInterfaces().Select(p => new RegValueOp(Hive.HKLM, p, "TcpAckFrequency", RegistryValueKind.DWord, 1))),
            new DynamicRegOp("TCPNoDelay=1 on active interfaces", true, () => ActiveTcpInterfaces().Select(p => new RegValueOp(Hive.HKLM, p, "TCPNoDelay", RegistryValueKind.DWord, 1)))) with { RequiresReboot = true };
        yield return T(N, "net.throttling-index", "Disable network throttling",
            "Sets NetworkThrottlingIndex to off. Windows normally limits non-multimedia network packet processing while media plays.",
            LMu(SysProfile, "NetworkThrottlingIndex", 0xFFFFFFFF, 10)) with { Recommended = true, RequiresReboot = true };
        yield return T(N, "net.qos-reserve", "Remove QoS bandwidth reserve",
            "Sets the QoS Packet Scheduler's reservable bandwidth limit to 0%. Windows only reserves bandwidth for apps that explicitly request QoS, so on most PCs this changes nothing measurable.",
            LM(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0));
        yield return T(N, "net.autotuning", "TCP receive window auto-tuning: normal",
            "Makes sure TCP receive-window auto-tuning is at 'normal', its best setting for fast connections. Some 'optimizers' set it to disabled, which caps download speed.",
            Cmd("netsh int tcp set global autotuninglevel=normal", "netsh int tcp set global autotuninglevel=normal",
                "(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal", @"^Normal", label: "netsh autotuninglevel=normal", checkShell: Shell.PowerShell));
        yield return T(N, "net.ecn-off", "Disable ECN capability",
            "Turns off Explicit Congestion Notification. Some routers mishandle ECN-marked packets. Undo restores the Windows default.",
            Cmd("netsh int tcp set global ecncapability=disabled", "netsh int tcp set global ecncapability=default",
                "(Get-NetTCPSetting -SettingName Internet).EcnCapability", @"^Disabled", label: "netsh ecncapability=disabled", checkShell: Shell.PowerShell));
        yield return T(N, "net.timestamps-off", "Disable TCP timestamps",
            "Removes the 12-byte RFC 1323 timestamp option from TCP packets.",
            Cmd("netsh int tcp set global timestamps=disabled", "netsh int tcp set global timestamps=default",
                "(Get-NetTCPSetting -SettingName Internet).Timestamps", @"^Disabled", label: "netsh timestamps=disabled", checkShell: Shell.PowerShell));
        yield return T(N, "net.rss-on", "Enable Receive-Side Scaling",
            "Lets network processing spread across multiple CPU cores (Windows default, which some tools turn off).",
            Cmd("netsh int tcp set global rss=enabled", "netsh int tcp set global rss=enabled",
                "(Get-NetOffloadGlobalSetting).ReceiveSideScaling", @"^Enabled", label: "netsh rss=enabled", checkShell: Shell.PowerShell));
        yield return T(N, "net.dns-cloudflare", "DNS: Cloudflare (1.1.1.1)",
            "Points every connected physical adapter to Cloudflare DNS (1.1.1.1 / 1.0.0.1), which is usually among the fastest resolvers. Undo returns DNS to automatic (DHCP).",
            Ps(DnsSet("1.1.1.1", "1.0.0.1"), DnsReset, DnsCheck, @"^1\.1\.1\.1,1\.0\.0\.1", label: "Set DNS 1.1.1.1 / 1.0.0.1")) with { Group = "DNS" };
        yield return T(N, "net.dns-google", "DNS: Google (8.8.8.8)",
            "Points every connected physical adapter to Google Public DNS (8.8.8.8 / 8.8.4.4). Undo returns DNS to automatic (DHCP).",
            Ps(DnsSet("8.8.8.8", "8.8.4.4"), DnsReset, DnsCheck, @"^8\.8\.8\.8,8\.8\.4\.4", label: "Set DNS 8.8.8.8 / 8.8.4.4")) with { Group = "DNS" };
        yield return T(N, "net.dns-quad9", "DNS: Quad9 (9.9.9.9)",
            "Points every connected physical adapter to Quad9 (9.9.9.9 / 149.112.112.112), which also blocks known malicious domains. Undo returns DNS to automatic (DHCP).",
            Ps(DnsSet("9.9.9.9", "149.112.112.112"), DnsReset, DnsCheck, @"^9\.9\.9\.9,149\.112\.112\.112", label: "Set DNS 9.9.9.9")) with { Group = "DNS" };
        yield return T(N, "net.adapter-power", "Disable network adapter power saving",
            "Stops Windows putting your Ethernet/Wi-Fi adapter into low-power states, which can cause lag spikes and disconnects.",
            Ps("Get-NetAdapter -Physical | Disable-NetAdapterPowerManagement -NoRestart",
               "Get-NetAdapter -Physical | Enable-NetAdapterPowerManagement -NoRestart",
               "Get-NetAdapter -Physical | Get-NetAdapterPowerManagement | ForEach-Object { $_.AllowComputerToTurnOffDevice }", @"^Disabled", label: "Disable-NetAdapterPowerManagement")) with { Recommended = true };
        yield return T(N, "net.lso-off", "Disable Large Send Offload",
            "Turns off LSO on physical adapters. LSO can add latency or cause stalls with some drivers. Undo turns it back on.",
            Ps("Get-NetAdapter -Physical | Disable-NetAdapterLso -NoRestart", "Get-NetAdapter -Physical | Enable-NetAdapterLso -NoRestart",
               "Get-NetAdapter -Physical | Get-NetAdapterLso | ForEach-Object { $_.IPv4Enabled }", @"^False", label: "Disable-NetAdapterLso")) with { Risk = Risk.Caution };
        yield return T(N, "net.teredo-off", "Disable Teredo tunneling",
            "Turns off the Teredo IPv6-over-IPv4 tunnel. Rarely needed on modern networks.",
            Cmd("netsh interface teredo set state disabled", "netsh interface teredo set state default",
                "(Get-NetTeredoConfiguration).Type", @"^Disabled", label: "netsh teredo disabled", checkShell: Shell.PowerShell)) with { Risk = Risk.Caution, Warning = "Xbox app party chat may report 'Teredo unable to qualify' while this is applied." };
        yield return T(N, "net.llmnr-off", "Disable LLMNR",
            "Disables Link-Local Multicast Name Resolution, a legacy name-lookup protocol that can be abused on public Wi-Fi.",
            LM(@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0));
        yield return T(N, "net.prefer-ipv4", "Prefer IPv4 over IPv6",
            "Keeps IPv6 enabled but makes Windows prefer IPv4 when both are available.",
            LM(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0x20)) with { Risk = Risk.Caution, RequiresReboot = true };
        yield return T(N, "net.port-range", "Faster TCP port reuse",
            "Shortens TIME_WAIT to 30 seconds, which helps when many TCP connections open and close quickly. MaxUserPort is also written for older tools, but Windows Vista and later ignore it (the dynamic port range is already 49152-65535).",
            LM(TcpParams, "TcpTimedWaitDelay", 30), LM(TcpParams, "MaxUserPort", 65534)) with { RequiresReboot = true };
        yield return T(N, "net.default-ttl", "Set default TTL to 64",
            "Sets the IP time-to-live for outgoing packets to 64, the common value used by Linux and most consoles. TTL only limits how many hops a packet may take; it does not change speed or ping.",
            LM(TcpParams, "DefaultTTL", 64)) with { RequiresReboot = true };
    }

    private static IEnumerable<Tweak> Advanced()
    {
        const TweakCategory A = TweakCategory.Advanced;
        const string Gfx = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        const string GamesTask = SysProfile + @"\Tasks\Games";

        yield return T(A, "adv.memory-compression", "Disable memory compression",
            "Turns off the memory-compression store. That saves CPU time on PCs with plenty of RAM (16 GB+), but uses more RAM and page file.",
            Ps("Disable-MMAgent -MemoryCompression", "Enable-MMAgent -MemoryCompression",
               "(Get-MMAgent).MemoryCompression", @"^False", label: "Disable-MMAgent -MemoryCompression")) with { Risk = Risk.Caution, RequiresReboot = true };
        yield return T(A, "adv.large-system-cache", "Large system cache",
            "Lets the file-system cache use more RAM. Can help disk-heavy workloads, but can hurt games on PCs with 8 GB or less.",
            LM(MemMgmt, "LargeSystemCache", 1, 0)) with { Risk = Risk.Caution, RequiresReboot = true };
        yield return T(A, "adv.prefetch", "Disable Prefetch & Superfetch",
            "Turns off boot/app prefetching. Little benefit on NVMe SSDs, and it cuts background disk activity.",
            LM(MemMgmt + @"\PrefetchParameters", "EnablePrefetcher", 0, 3),
            LM(MemMgmt + @"\PrefetchParameters", "EnableSuperfetch", 0, 3)) with { RequiresReboot = true };
        yield return T(A, "adv.paging-executive", "Keep kernel in RAM",
            "Stops Windows paging kernel-mode drivers and system code to disk (DisablePagingExecutive). Best with 16 GB+ RAM.",
            LM(MemMgmt, "DisablePagingExecutive", 1, 0)) with { Risk = Risk.Caution, RequiresReboot = true };
        yield return T(A, "adv.timer-dynamic-tick", "Disable dynamic tick",
            "Stops the kernel skipping timer ticks while idle. That can reduce timer jitter, but uses slightly more power. Changes the boot configuration (bcdedit).",
            Cmd("bcdedit /set disabledynamictick yes", "bcdedit /deletevalue disabledynamictick",
                // "disabledynamictick No" must not count as applied: only "Yes" (bcdedit localizes it) counts.
                "bcdedit /enum {current}", DynamicTickOnPattern, label: "bcdedit disabledynamictick yes")) with { Risk = Risk.Caution, RequiresReboot = true, Group = "Timers" };
        yield return T(A, "adv.timer-hpet", "Remove forced HPET (platform clock)",
            "Deletes a 'useplatformclock' override if a previous tool set one, so Windows can use its default (faster) timer source.",
            Cmd("bcdedit /deletevalue useplatformclock & exit /b 0", "exit /b 0",
                "bcdedit /enum {current}", @"\A(?![\s\S]*useplatformclock)(?=[\s\S]*\{current\})", label: "bcdedit /deletevalue useplatformclock")) with { RequiresReboot = true, Group = "Timers" };
        yield return T(A, "adv.global-timer-res", "Global timer resolution requests",
            "On Windows 11, a program's timer-resolution request normally only affects that program. This restores the old system-wide behavior, which the Input Delay Reducer relies on.",
            LM(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)) with { MinBuild = 22000, Risk = Risk.Caution, RequiresReboot = true, Group = "Timers" };
        yield return T(A, "adv.hags", "Hardware-accelerated GPU scheduling",
            "Turns on HAGS so the GPU manages its own video memory scheduling. Needs a recent GPU and driver (and is required for DLSS Frame Generation).",
            LM(Gfx, "HwSchMode", 2, 1)) with { RequiresReboot = true, Group = "GPU" };
        yield return T(A, "adv.fso", "Disable fullscreen optimizations (global)",
            "Makes games that request exclusive fullscreen get true exclusive fullscreen instead of Windows' borderless 'optimized' mode.",
            CU(@"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0),
            CU(@"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, 0),
            CU(@"System\GameConfigStore", "GameDVR_FSEBehavior", 2, 0),
            CU(@"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, 0)) with { Group = "GPU" };
        yield return T(A, "adv.gpu-msi", "MSI mode for GPU",
            "Turns on Message Signaled Interrupts for your graphics card(s), which can lower interrupt latency. Most modern drivers already use MSI.",
            new DynamicRegOp("MSISupported=1 for display adapters", true, () => GpuPnpIds().Select(id => new RegValueOp(Hive.HKLM,
                $@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties", "MSISupported", RegistryValueKind.DWord, 1)))) with
        {
            Risk = Risk.Caution, RequiresReboot = true, Group = "GPU",
            Warning = "If your display stays black after rebooting, boot into Safe Mode and undo this tweak (or roll back with the restore point)."
        };
        yield return T(A, "adv.system-responsiveness", "Prioritize foreground games (MMCSS)",
            "Sets SystemResponsiveness to 10%, the lowest Windows honours, so the multimedia scheduler reserves less CPU for background tasks.",
            LM(SysProfile, "SystemResponsiveness", 10, 20)) with { Recommended = true, Group = "Scheduler" };
        yield return T(A, "adv.games-task", "Raise MMCSS 'Games' task priority",
            "Raises the GPU priority, CPU priority and scheduling category for apps registered as games with the Multimedia Class Scheduler.",
            LM(GamesTask, "GPU Priority", 8, 8), LM(GamesTask, "Priority", 6, 2),
            LMs(GamesTask, "Scheduling Category", "High", "Medium"), LMs(GamesTask, "SFIO Priority", "High", "Normal")) with { Group = "Scheduler" };
        yield return T(A, "adv.priority-separation", "Foreground priority boost (Win32PrioritySeparation)",
            "Sets Win32PrioritySeparation to 0x26 (short, variable quanta with maximum foreground boost) so the window you're using gets more CPU time.",
            LM(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, 2)) with { Group = "Scheduler" };
        yield return T(A, "adv.power-throttling", "Disable power throttling",
            "Turns off Windows power throttling, which slows down background apps to save battery. Useful when streaming or recording while gaming.",
            LM(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)) with { Group = "Scheduler" };
        yield return T(A, "adv.ntfs-8dot3", "Disable NTFS 8.3 short names",
            "Stops NTFS creating legacy DOS 8.3 file names for new files, which speeds up folders with many files.",
            LM(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisable8dot3NameCreation", 1, 2)) with { Group = "File system" };
        yield return T(A, "adv.ntfs-lastaccess", "Disable NTFS last-access timestamps",
            "Stops NTFS writing a 'last accessed' time each time a file is read, cutting needless disk writes.",
            LMu(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 0x80000001, 0x80000002u)) with { Group = "File system" };
        yield return T(A, "adv.ntfs-memory", "Increase NTFS paged-pool memory",
            "Lets NTFS use more paged-pool memory for its metadata cache, which helps when opening many files (big game installs).",
            LM(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsMemoryUsage", 2)) with { Group = "File system", RequiresReboot = true };
        yield return T(A, "adv.vbs-hvci", "Disable Memory Integrity (HVCI)",
            "Turns off Core Isolation Memory Integrity. Microsoft documents a measurable gaming performance cost for it on some CPUs.",
            LM(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, 1)) with
        {
            Risk = Risk.Caution, RequiresReboot = true, Group = "Security trade-off",
            Warning = "This lowers protection against kernel-level malware. Some anti-cheats (and Windows Security) may recommend turning it back on."
        };
        yield return T(A, "adv.svchost-split", "Group svchost processes (16+ GB RAM)",
            "Raises SvcHostSplitThresholdInKB to your installed RAM, so services share fewer svchost.exe processes. That cuts the process count and some memory overhead.",
            new DynamicRegOp("SvcHostSplitThresholdInKB = installed RAM", true, () =>
            {
                var kb = (int)Math.Min(int.MaxValue, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024);
                return [new RegValueOp(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", RegistryValueKind.DWord, kb)];
            })) with { Risk = Risk.Caution, RequiresReboot = true, Group = "Scheduler" };
        yield return T(A, "adv.background-drivers", "Disable automatic driver updates via Windows Update",
            "Stops Windows Update installing drivers automatically, so it won't replace the GPU driver you chose. You'll need to update drivers yourself.",
            LM(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1)) with { Risk = Risk.Caution, Group = "Drivers" };
    }
}
