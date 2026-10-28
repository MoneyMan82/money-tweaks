using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Engine;

/// <summary>One service or driver as the Service Control Manager reports it right now.</summary>
public sealed record ServiceNode(string Name, string DisplayName, bool IsDriver, string State, int? Start, bool Delayed, bool TriggerStart,
    IReadOnlyList<string> DependsOn, IReadOnlyList<string> Groups, string? Description, string? Image)
{
    /// <summary>Filled in by the snapshot: services that list this one as a dependency.</summary>
    public List<string> Dependents { get; } = [];
    public bool Running => State == "Running";
    public bool Disabled => Start == 4;
    public string StartText => Start is null ? "unknown" : ServiceStartOp.StartName(Start.Value) + (Delayed && Start == 2 ? " (delayed)" : "") + (TriggerStart ? " (trigger start)" : "");
}

/// <summary>
/// What the Service Control Manager says about every service and driver on this PC, read once (one enumeration plus each
/// service's configuration). Dependents are worked out by reversing every service's dependency list, so both directions
/// come from the same configuration Windows itself uses when it starts services.
/// </summary>
public sealed class ServiceSnapshot
{
    public IReadOnlyDictionary<string, ServiceNode> Nodes { get; }
    public DateTime TakenAt { get; } = DateTime.Now;

    public ServiceSnapshot(IEnumerable<ServiceNode> nodes)
    {
        var map = new Dictionary<string, ServiceNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) map.TryAdd(n.Name, n);
        foreach (var n in map.Values)
            foreach (var d in n.DependsOn)
                if (map.TryGetValue(d, out var target) && !target.Dependents.Contains(n.Name, StringComparer.OrdinalIgnoreCase)) target.Dependents.Add(n.Name);
        foreach (var n in map.Values) n.Dependents.Sort(StringComparer.OrdinalIgnoreCase);
        Nodes = map;
    }

    public ServiceNode? Get(string name) => Nodes.GetValueOrDefault(name);

    /// <summary>Reads the live system. Services that can't be queried are still listed, with what could be read.</summary>
    public static ServiceSnapshot Read()
    {
        using var perf = Perf.Measure("service snapshot", 100);
        var nodes = new List<ServiceNode>();
        void Add(ServiceController sc, bool driver)
        {
            try
            {
                string name = sc.ServiceName;
                string state = Safe(() => sc.Status.ToString(), "Unknown");
                List<string> deps = Safe(() => sc.ServicesDependedOn.Select(x => x.ServiceName).ToList(), []);
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                int? start = k?.GetValue("Start") is int s ? s : null;
                bool delayed = k?.GetValue("DelayedAutostart") is int dl && dl == 1;
                bool trigger = k?.OpenSubKey("TriggerInfo") is { } ti && Dispose(ti);
                var groups = (k?.GetValue("DependOnGroup") as string[] ?? []).Where(g => g.Length > 0).ToList();
                string? desc = driver ? null : Indirect(k?.GetValue("Description") as string);
                string? image = k?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                nodes.Add(new(name, Safe(() => sc.DisplayName, name), driver, state, start, delayed, trigger, deps, groups, desc, image));
            }
            catch (Exception ex) { Log.Debug($"service snapshot {sc.ServiceName}: {ex.Message}"); }
            finally { sc.Dispose(); }
        }
        foreach (var sc in ServiceController.GetServices()) Add(sc, false);
        foreach (var sc in ServiceController.GetDevices()) Add(sc, true);
        return new ServiceSnapshot(nodes);
    }

    private static bool Dispose(RegistryKey k) { k.Dispose(); return true; }
    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int outBufSize, IntPtr reserved);

    /// <summary>Descriptions are often "@dll,-id" resource references; this resolves them the way Services.msc does.</summary>
    private static string? Indirect(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || !s.StartsWith('@')) return s;
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }
}

public enum ImpactLevel { None, Info, Possible, Likely }

/// <summary>Expected effect on one service, with the evidence it's based on.</summary>
public sealed record ServiceEffect(string Name, string DisplayName, int Depth, string Now, string After, ImpactLevel Level, string Evidence, string? CriticalFunction);

/// <summary>Before/after preview of changing one service's start type.</summary>
public sealed record ServiceImpact(ServiceNode Target, int ProposedStart, string Before, string After, IReadOnlyList<ServiceEffect> Effects,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Uncertainty)
{
    public bool TouchesCritical => Effects.Any(e => e.CriticalFunction is not null && e.Level >= ImpactLevel.Possible) || ServiceNexus.Critical(Target.Name) is not null;
    public int Affected => Effects.Count(e => e.Level >= ImpactLevel.Possible);
}

/// <summary>
/// Service Nexus: the dependency graph behind the Services tweaks, and what a proposed start-type change could do to the
/// services that depend on it. The reasoning follows how the Service Control Manager starts services: before starting a
/// service it starts everything that service depends on, on demand even when that is set to Manual, and refuses (error
/// 1068) when a dependency is Disabled. A declared dependency is evidence of a start-order requirement, not proof that
/// anything you use will break, and undeclared dependencies (RPC, COM, files) are invisible to it. The analysis says so.
/// </summary>
public static class ServiceNexus
{
    /// <summary>
    /// Core Windows functions behind some services (Money Tweaks' own list, not from Windows). Used only to raise the
    /// warning level when a change could reach them.
    /// </summary>
    private static readonly Dictionary<string, string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RpcSs"] = "Remote Procedure Call: almost every Windows component talks through it",
        ["RpcEptMapper"] = "RPC endpoint mapping",
        ["DcomLaunch"] = "COM server launching and device power",
        ["LSM"] = "user sign-in sessions",
        ["SamSs"] = "local accounts and passwords",
        ["EventLog"] = "Windows event logging",
        ["PlugPlay"] = "detecting and configuring hardware",
        ["Power"] = "power management",
        ["Winmgmt"] = "WMI, used by Settings, Task Manager and many apps",
        ["BFE"] = "the firewall and IPsec engine",
        ["mpssvc"] = "Windows Firewall",
        ["Dhcp"] = "getting an IP address from your router",
        ["Dnscache"] = "DNS name resolution",
        ["nsi"] = "network connectivity information",
        ["NlaSvc"] = "network location and connectivity status",
        ["netprofm"] = "network profiles (the internet status icon)",
        ["Audiosrv"] = "sound",
        ["AudioEndpointBuilder"] = "audio devices",
        ["CryptSvc"] = "certificates and signature checks",
        ["ProfSvc"] = "loading user profiles at sign-in",
        ["Schedule"] = "Task Scheduler",
        ["SystemEventsBroker"] = "system event triggers",
        ["BrokerInfrastructure"] = "background tasks for apps",
        ["CoreMessagingRegistrar"] = "UI messaging between system components",
        ["StateRepository"] = "Store apps and the Start menu",
        ["gpsvc"] = "Group Policy",
        ["UserManager"] = "user sign-in and the shell",
        ["WinDefend"] = "Microsoft Defender Antivirus",
        ["SecurityHealthService"] = "Windows Security",
        ["wuauserv"] = "Windows Update",
        ["TrustedInstaller"] = "installing Windows updates and features",
        ["Themes"] = "visual themes",
        ["FontCache"] = "font rendering performance",
        ["ShellHWDetection"] = "AutoPlay and the shell's hardware events",
        ["LanmanWorkstation"] = "connecting to network shares",
        ["WlanSvc"] = "Wi-Fi",
        ["Netman"] = "network connections",
        ["EventSystem"] = "COM+ events used by many system services",
        ["SENS"] = "system event notifications",
        ["camsvc"] = "app permissions (camera, microphone, location)",
        ["TokenBroker"] = "Microsoft account sign-in in apps",
        ["AppXSvc"] = "installing and updating Store apps",
        ["ClipSVC"] = "Store app licences",
        ["WinHttpAutoProxySvc"] = "proxy detection used by networking",
        ["Tcpip"] = "TCP/IP networking (driver)",
        ["AFD"] = "Windows sockets (driver)",
        ["NetBT"] = "NetBIOS over TCP/IP (driver)",
    };

    public static string? Critical(string name) => CriticalServices.GetValueOrDefault(name);

    /// <summary>Services the Services tweaks change (their start type), from the catalog.</summary>
    public static IReadOnlySet<string> Tweakable { get; } = TweakCatalog.InCategory(TweakCategory.Services)
        .SelectMany(t => t.Ops.OfType<ServiceStartOp>()).Select(o => o.Service).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Money Tweaks service tweak for this service, if the catalog has one.</summary>
    public static Tweak? TweakFor(string service) => TweakCatalog.Find("svc." + service.ToLowerInvariant());

    /// <summary>Services that list <paramref name="name"/> as a dependency, directly or through others, nearest first.</summary>
    public static List<(ServiceNode node, int depth, string via)> Dependents(ServiceSnapshot s, string name, int maxDepth = 6)
    {
        var result = new List<(ServiceNode, int, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var queue = new Queue<(string name, int depth)>();
        queue.Enqueue((name, 0));
        while (queue.Count > 0)
        {
            var (cur, depth) = queue.Dequeue();
            if (depth >= maxDepth || s.Get(cur) is not { } node) continue;
            foreach (var d in node.Dependents)
                if (seen.Add(d) && s.Get(d) is { } dn)
                {
                    result.Add((dn, depth + 1, cur));
                    queue.Enqueue((d, depth + 1));
                }
        }
        return result;
    }

    /// <summary>
    /// Before/after preview of setting <paramref name="name"/> to <paramref name="proposedStart"/> (3 Manual, 4 Disabled).
    /// Read-only: works on the snapshot, changes nothing.
    /// </summary>
    public static ServiceImpact Impact(ServiceSnapshot s, string name, int proposedStart)
    {
        var target = s.Get(name) ?? throw new ArgumentException($"Service {name} isn't on this PC.", nameof(name));
        var effects = new List<ServiceEffect>();
        var warnings = new List<string>();
        var uncertainty = new List<string>();
        bool disabling = proposedStart == 4 && !target.Disabled;
        string after = ServiceStartOp.StartName(proposedStart) + (proposedStart == 4 && target.Running ? ", stopped now" : "");

        if (Critical(target.Name) is { } own)
            warnings.Add($"{target.DisplayName} is a core Windows service ({own}). Changing it can affect Windows itself, not just optional features.");
        if (target.Start is { } cur && proposedStart > cur && proposedStart == 3 && cur == 2)
            uncertainty.Add($"From Automatic to Manual: Windows no longer starts it at boot, but starts it on demand when a dependent or a trigger needs it. Features that expect it to be running already can start a little later.");

        foreach (var (node, depth, via) in Dependents(s, target.Name))
        {
            var critical = Critical(node.Name);
            var viaText = depth == 1 ? $"{node.Name} lists {target.Name} as a dependency (SCM configuration)." : $"{node.Name} depends on {via}, which depends (directly or further down) on {target.Name}.";
            string now = $"{(node.Running ? "running" : node.State.ToLowerInvariant())}, {node.StartText}";
            if (proposedStart != 4)
            {
                effects.Add(new(node.Name, node.DisplayName, depth, now, "unchanged: Windows starts a Manual dependency on demand",
                    ImpactLevel.None, viaText + " The Service Control Manager starts dependencies set to Manual automatically before starting the dependent.", critical));
                continue;
            }
            if (!disabling)
            {
                effects.Add(new(node.Name, node.DisplayName, depth, now, "no change (already disabled)", ImpactLevel.None, viaText, critical));
                continue;
            }
            if (node.Disabled)
            {
                effects.Add(new(node.Name, node.DisplayName, depth, now, "no change: it's disabled itself", ImpactLevel.None, viaText, critical));
                continue;
            }
            ImpactLevel level;
            string afterText;
            if (node.Running && target.Running)
            {
                // Stopping a service first stops the running services that depend on it (ServiceController.Stop).
                level = ImpactLevel.Likely;
                afterText = "stopped now, and can't start again";
            }
            else if (node.Running)
            {
                level = ImpactLevel.Likely;
                afterText = "keeps running until it stops; can't start again";
            }
            else if (node.Start == 2)
            {
                level = ImpactLevel.Likely;
                afterText = "fails to start at boot (error 1068)";
            }
            else
            {
                level = ImpactLevel.Possible;
                afterText = node.TriggerStart ? "fails if a trigger starts it (error 1068)" : "fails if something starts it (error 1068)";
            }
            effects.Add(new(node.Name, node.DisplayName, depth, now, afterText, level,
                viaText + " Windows refuses to start a service while a service it depends on is disabled (error 1068, logged as event 7001).", critical));
        }

        if (disabling && target.Running)
        {
            var runningDeps = effects.Where(e => e.Level == ImpactLevel.Likely && s.Get(e.Name)?.Running == true).Select(e => e.DisplayName).ToList();
            warnings.Add(runningDeps.Count == 0
                ? $"{target.DisplayName} is running: applying stops it right away (Money Tweaks stops a service it disables)."
                : $"{target.DisplayName} is running: applying stops it right away, and Windows stops the running services that depend on it first: {string.Join(", ", runningDeps)}.");
        }
        var crit = effects.Where(e => e.CriticalFunction is not null && e.Level >= ImpactLevel.Possible).ToList();
        if (crit.Count > 0)
            warnings.Add("Reaches core Windows functions: " + string.Join("; ", crit.Select(e => $"{e.DisplayName} ({e.CriticalFunction})")) + ".");
        if (disabling && effects.Count(e => e.Level >= ImpactLevel.Possible) is var n and > 0)
            warnings.Add($"{n} service(s) that depend on it could stop working while it's disabled.");

        uncertainty.Add("Dependencies here are the ones services declare to Windows. They prove a start-order requirement, not that a feature you use will break: a dependent may never be needed on this PC.");
        uncertainty.Add("The reverse also holds: services often rely on each other through RPC, COM or files without declaring it, so “no dependents” doesn't guarantee nothing else uses it.");
        if (target.Groups.Count > 0)
            uncertainty.Add($"{target.Name} also waits for load-order group(s) {string.Join(", ", target.Groups)}; group membership isn't a service dependency and isn't drawn.");
        if (target.TriggerStart)
            uncertainty.Add($"{target.Name} is trigger-started (by a device, network or policy event). Disabled, those triggers can no longer start it.");

        return new(target, proposedStart, $"{(target.Running ? "running" : target.State.ToLowerInvariant())}, {target.StartText}", after, effects, warnings, uncertainty);
    }
}
