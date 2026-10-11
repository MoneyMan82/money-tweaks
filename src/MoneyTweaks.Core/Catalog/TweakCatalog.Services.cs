using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Ops;

namespace MoneyTweaks.Core.Catalog;

public static partial class TweakCatalog
{
    private const int Manual = 3, Disabled = 4;

    /// <summary>(service name, display name, target start type, risk, group, what it does / who needs it)</summary>
    internal static readonly (string svc, string name, int start, Risk risk, string group, string desc)[] ServiceTable =
    [
        // Telemetry & diagnostics
        ("DiagTrack", "Connected User Experiences and Telemetry", Disabled, Risk.Safe, "Telemetry", "The main Windows telemetry uploader."),
        ("dmwappushservice", "Device Management WAP Push", Disabled, Risk.Safe, "Telemetry", "Routes WAP push messages for device management and telemetry. Only needed on MDM-managed work PCs."),
        ("diagnosticshub.standardcollector.service", "Diagnostics Hub Standard Collector", Manual, Risk.Safe, "Telemetry", "Collects ETW events for Visual Studio diagnostics."),
        ("diagsvc", "Diagnostic Execution Service", Manual, Risk.Safe, "Telemetry", "Runs troubleshooting diagnostics on demand."),
        ("DPS", "Diagnostic Policy Service", Manual, Risk.Caution, "Telemetry", "Detects and troubleshoots problems. The built-in troubleshooters stop working while it's disabled."),
        ("WdiServiceHost", "Diagnostic Service Host", Manual, Risk.Safe, "Telemetry", "Hosts diagnostics for the Diagnostic Policy Service."),
        ("WdiSystemHost", "Diagnostic System Host", Manual, Risk.Safe, "Telemetry", "Hosts system-level diagnostics."),
        ("WerSvc", "Windows Error Reporting", Manual, Risk.Safe, "Telemetry", "Reports crashes to Microsoft. On Manual it only starts when needed."),
        ("wercplsupport", "Problem Reports Control Panel Support", Manual, Risk.Safe, "Telemetry", "Backs the Problem Reports control panel."),
        ("PcaSvc", "Program Compatibility Assistant", Manual, Risk.Safe, "Telemetry", "Watches programs for known compatibility issues and shows prompts."),
        ("TroubleshootingSvc", "Recommended Troubleshooting", Manual, Risk.Safe, "Telemetry", "Applies Microsoft-recommended troubleshooters automatically."),
        ("wisvc", "Windows Insider Service", Disabled, Risk.Safe, "Telemetry", "Only needed if you're in the Windows Insider Program."),
        ("RetailDemo", "Retail Demo", Disabled, Risk.Safe, "Telemetry", "Controls the store-display retail demo mode."),
        ("InventorySvc", "Inventory and Compatibility Appraisal", Manual, Risk.Safe, "Telemetry", "Inventories apps for upgrade-compatibility checks."),

        // Xbox
        ("XblAuthManager", "Xbox Live Auth Manager", Manual, Risk.Safe, "Xbox", "Signs in to Xbox Live. Starts on demand for Game Pass and Xbox titles."),
        ("XblGameSave", "Xbox Live Game Save", Manual, Risk.Safe, "Xbox", "Syncs cloud saves for Xbox-enabled games. Starts on demand."),
        ("XboxNetApiSvc", "Xbox Live Networking", Manual, Risk.Safe, "Xbox", "Xbox Live multiplayer networking. Starts on demand."),
        ("XboxGipSvc", "Xbox Accessory Management", Manual, Risk.Safe, "Xbox", "Manages Xbox controller firmware/accessories. Starts on demand."),

        // Printing & scanning
        ("Spooler", "Print Spooler", Disabled, Risk.Caution, "Printing", "Required for any printing, including 'Print to PDF'. Disable it only if you never print. That also closes the PrintNightmare attack surface."),
        ("PrintNotify", "Printer Extensions and Notifications", Manual, Risk.Safe, "Printing", "Shows printer driver dialogs and notifications."),
        ("Fax", "Fax", Disabled, Risk.Safe, "Printing", "Sends and receives faxes through a fax modem."),
        ("stisvc", "Windows Image Acquisition", Manual, Risk.Caution, "Printing", "Needed for scanners and some webcams. On Manual it starts when a scanner is used."),
        ("WiaRpc", "Still Image Acquisition Events", Manual, Risk.Safe, "Printing", "Launches apps when a scanner/camera button is pressed."),

        // Remote access & sharing
        ("RemoteRegistry", "Remote Registry", Disabled, Risk.Safe, "Remote access", "Lets remote users edit this PC's registry. Disabling it is a security win."),
        ("RemoteAccess", "Routing and Remote Access", Disabled, Risk.Safe, "Remote access", "Turns the PC into a router/VPN server. Not needed on gaming PCs."),
        ("TermService", "Remote Desktop Services", Manual, Risk.Caution, "Remote access", "Hosts incoming Remote Desktop connections. Keep it if you remote into this PC."),
        ("SessionEnv", "Remote Desktop Configuration", Manual, Risk.Safe, "Remote access", "Configures Remote Desktop sessions."),
        ("UmRdpService", "Remote Desktop UserMode Port Redirector", Manual, Risk.Safe, "Remote access", "Redirects printers/drives in Remote Desktop sessions."),
        ("SharedAccess", "Internet Connection Sharing", Manual, Risk.Safe, "Remote access", "Shares this PC's internet with other devices."),
        ("icssvc", "Windows Mobile Hotspot", Manual, Risk.Safe, "Remote access", "Turns the PC into a Wi-Fi hotspot. Starts on demand."),
        ("lmhosts", "TCP/IP NetBIOS Helper", Manual, Risk.Safe, "Remote access", "Legacy NetBIOS name resolution on old LANs."),
        ("NetTcpPortSharing", "Net.Tcp Port Sharing", Disabled, Risk.Safe, "Remote access", "WCF port sharing for .NET server apps. Disabled by default on most PCs."),
        ("WMPNetworkSvc", "Windows Media Player Network Sharing", Disabled, Risk.Safe, "Remote access", "Shares your Windows Media Player library on the network."),
        ("SSDPSRV", "SSDP Discovery", Manual, Risk.Caution, "Remote access", "Discovers UPnP devices (smart TVs, media servers, some consoles)."),
        ("upnphost", "UPnP Device Host", Manual, Risk.Safe, "Remote access", "Hosts UPnP devices on this PC."),
        ("fdPHost", "Function Discovery Provider Host", Manual, Risk.Safe, "Remote access", "Network device discovery provider."),
        ("FDResPub", "Function Discovery Resource Publication", Manual, Risk.Caution, "Remote access", "Lets other PCs see this one in the Network folder."),
        ("lltdsvc", "Link-Layer Topology Discovery Mapper", Manual, Risk.Safe, "Remote access", "Draws the legacy network map."),
        ("p2psvc", "Peer Networking Grouping", Manual, Risk.Safe, "Remote access", "Legacy peer-to-peer grouping."),
        ("p2pimsvc", "Peer Networking Identity Manager", Manual, Risk.Safe, "Remote access", "Identity service for peer networking."),
        ("PNRPsvc", "Peer Name Resolution Protocol", Manual, Risk.Safe, "Remote access", "Serverless peer name resolution."),
        ("PNRPAutoReg", "PNRP Machine Name Publication", Manual, Risk.Safe, "Remote access", "Publishes the PC name over PNRP."),
        ("MSiSCSI", "Microsoft iSCSI Initiator", Manual, Risk.Safe, "Remote access", "Connects to iSCSI network storage."),
        ("SNMPTRAP", "SNMP Trap", Manual, Risk.Safe, "Remote access", "Receives SNMP trap messages."),
        ("ALG", "Application Layer Gateway", Manual, Risk.Safe, "Remote access", "Protocol plugins for Internet Connection Sharing."),
        ("CscService", "Offline Files", Disabled, Risk.Safe, "Remote access", "Caches network files for offline use (mostly corporate shares)."),
        ("WebClient", "WebClient (WebDAV)", Manual, Risk.Safe, "Remote access", "Maps WebDAV/SharePoint folders as drives."),
        ("NcdAutoSetup", "Network Connected Devices Auto-Setup", Manual, Risk.Safe, "Remote access", "Automatically installs network devices such as printers."),
        ("QWAVE", "Quality Windows Audio Video Experience", Manual, Risk.Safe, "Remote access", "QoS for home-network AV streaming."),

        // Location, maps, sensors
        ("MapsBroker", "Downloaded Maps Manager", Disabled, Risk.Safe, "Location & sensors", "Manages offline maps for the Maps app."),
        ("lfsvc", "Geolocation Service", Manual, Risk.Caution, "Location & sensors", "Provides location to apps. Find My Device and weather need it."),
        ("tzautoupdate", "Auto Time Zone Updater", Disabled, Risk.Safe, "Location & sensors", "Changes the time zone automatically from your location."),
        ("SensorDataService", "Sensor Data Service", Manual, Risk.Safe, "Location & sensors", "Delivers data from hardware sensors."),
        ("SensrSvc", "Sensor Monitoring Service", Manual, Risk.Safe, "Location & sensors", "Adjusts the display from ambient light sensors (laptops)."),
        ("SensorService", "Sensor Service", Manual, Risk.Safe, "Location & sensors", "Manages device sensors such as screen rotation."),

        // Phone, wallet, biometrics, smart card
        ("PhoneSvc", "Phone Service", Manual, Risk.Safe, "Devices", "Manages telephony state for Phone Link calls."),
        ("SmsRouter", "Microsoft Windows SMS Router", Manual, Risk.Safe, "Devices", "Routes SMS messages for Phone Link."),
        ("WalletService", "WalletService", Manual, Risk.Safe, "Devices", "Hosts the legacy Wallet clients."),
        ("SEMgrSvc", "Payments and NFC/SE Manager", Manual, Risk.Safe, "Devices", "NFC tap-to-pay support."),
        ("WbioSrvc", "Windows Biometric Service", Manual, Risk.Caution, "Devices", "Needed for Windows Hello fingerprint and face sign-in."),
        ("SCardSvr", "Smart Card", Manual, Risk.Safe, "Devices", "Reads smart cards (corporate badges, ID cards)."),
        ("ScDeviceEnum", "Smart Card Device Enumeration", Manual, Risk.Safe, "Devices", "Creates device nodes for smart card readers."),
        ("SCPolicySvc", "Smart Card Removal Policy", Manual, Risk.Safe, "Devices", "Locks the PC when a smart card is removed."),
        ("TabletInputService", "Touch Keyboard and Handwriting Panel", Manual, Risk.Caution, "Devices", "Needed for the touch keyboard, pen input and emoji panel (Win+.)."),
        ("FrameServer", "Windows Camera Frame Server", Manual, Risk.Safe, "Devices", "Lets multiple apps share the webcam. Starts on demand."),
        ("BthAvctpSvc", "AVCTP Service", Manual, Risk.Caution, "Devices", "Bluetooth audio remote control. Needed for Bluetooth headphone media buttons."),
        ("BTAGService", "Bluetooth Audio Gateway", Manual, Risk.Caution, "Devices", "Hands-free profile for Bluetooth headsets with microphones."),
        ("bthserv", "Bluetooth Support Service", Manual, Risk.Caution, "Devices", "Required for Bluetooth. Don't disable it if you use any Bluetooth device."),
        ("wcncsvc", "Windows Connect Now", Manual, Risk.Safe, "Devices", "WPS configuration for wireless devices."),
        ("WFDSConMgrSvc", "Wi-Fi Direct Services Connection Manager", Manual, Risk.Safe, "Devices", "Wi-Fi Direct and Miracast connections."),
        ("MsKeyboardFilter", "Microsoft Keyboard Filter", Disabled, Risk.Safe, "Devices", "Kiosk keystroke filtering. Enterprise only."),
        ("DisplayEnhancementService", "Display Enhancement Service", Manual, Risk.Safe, "Devices", "Manages display brightness enhancement on laptops."),

        // Search, indexing, performance
        ("SysMain", "SysMain (Superfetch)", Disabled, Risk.Safe, "Performance", "Preloads apps into RAM. Of little use on SSDs, and it can cause disk churn."),
        ("WSearch", "Windows Search (indexer)", Manual, Risk.Caution, "Performance", "Indexes files for instant search. On Manual, search still works but is slower and Outlook search can degrade."),
        ("TrkWks", "Distributed Link Tracking Client", Disabled, Risk.Safe, "Performance", "Keeps NTFS shortcuts working when files move between networked PCs."),
        ("BITS", "Background Intelligent Transfer Service", Manual, Risk.Caution, "Performance", "Background downloads for Windows Update and Store. Keep it on Manual, never Disabled."),
        ("GraphicsPerfSvc", "GraphicsPerfSvc", Manual, Risk.Safe, "Performance", "Graphics performance monitoring for Task Manager's GPU view."),
        ("svsvc", "Spot Verifier", Manual, Risk.Safe, "Performance", "Verifies suspected file-system corruption."),
        ("AppReadiness", "App Readiness", Manual, Risk.Safe, "Performance", "Prepares Store apps at first sign-in."),

        // Hyper-V guest integration
        ("HvHost", "HV Host Service", Manual, Risk.Safe, "Hyper-V guest", "Hyper-V performance enlightenments. Only matters inside VMs or with Hyper-V."),
        ("vmickvpexchange", "Hyper-V Data Exchange", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmicguestinterface", "Hyper-V Guest Service Interface", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmicshutdown", "Hyper-V Guest Shutdown", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmicheartbeat", "Hyper-V Heartbeat", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmicvmsession", "Hyper-V PowerShell Direct", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmicrdv", "Hyper-V Remote Desktop Virtualization", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmictimesync", "Hyper-V Time Synchronization", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),
        ("vmicvss", "Hyper-V Volume Shadow Copy Requestor", Manual, Risk.Safe, "Hyper-V guest", "Only used when this PC runs as a Hyper-V virtual machine."),

        // Enterprise / rarely used
        ("AppVClient", "Microsoft App-V Client", Disabled, Risk.Safe, "Enterprise", "Runs virtualized App-V apps (enterprise)."),
        ("UevAgentService", "User Experience Virtualization", Disabled, Risk.Safe, "Enterprise", "UE-V settings roaming (enterprise)."),
        ("shpamsvc", "Shared PC Account Manager", Disabled, Risk.Safe, "Enterprise", "Manages accounts on shared kiosk PCs."),
        ("AssignedAccessManagerSvc", "Assigned Access Manager", Manual, Risk.Safe, "Enterprise", "Kiosk mode."),
        ("workfolderssvc", "Work Folders", Manual, Risk.Safe, "Enterprise", "Syncs corporate Work Folders."),
        ("EntAppSvc", "Enterprise App Management", Manual, Risk.Safe, "Enterprise", "Enterprise app deployment."),
        ("embeddedmode", "Embedded Mode", Manual, Risk.Safe, "Enterprise", "Windows IoT background apps."),
        ("Wecsvc", "Windows Event Collector", Manual, Risk.Safe, "Enterprise", "Collects events from remote PCs."),
        ("AJRouter", "AllJoyn Router", Disabled, Risk.Safe, "Enterprise", "Legacy AllJoyn IoT messaging."),
        ("TapiSrv", "Telephony", Manual, Risk.Safe, "Enterprise", "Telephony API for dial-up and VoIP hardware."),
        ("PushToInstall", "Windows PushToInstall", Manual, Risk.Safe, "Enterprise", "Installs Store apps pushed from the web Store."),

        // Mixed reality, AI & misc
        ("MixedRealityOpenXRSvc", "Mixed Reality OpenXR", Manual, Risk.Safe, "Mixed reality", "Windows Mixed Reality headsets."),
        ("perceptionsimulation", "Windows Perception Simulation", Manual, Risk.Safe, "Mixed reality", "Holographic simulation for developers."),
        ("spectrum", "Windows Perception Service", Manual, Risk.Safe, "Mixed reality", "Spatial perception for mixed-reality headsets."),
        ("NaturalAuthentication", "Natural Authentication", Manual, Risk.Safe, "Mixed reality", "Dynamic Lock and other signal-based sign-in policies."),
        ("WpcMonSvc", "Parental Controls", Manual, Risk.Safe, "Accounts", "Microsoft Family Safety enforcement. Keep it if this is a child account."),
        ("wlidsvc", "Microsoft Account Sign-in Assistant", Manual, Risk.Caution, "Accounts", "Needed to sign in with a Microsoft account, use the Store and Xbox. Keep it on Manual."),

        // Updaters
        ("edgeupdate", "Microsoft Edge Update", Manual, Risk.Safe, "Updaters", "Keeps Edge updated. On Manual it runs when Edge checks for updates."),
        ("edgeupdatem", "Microsoft Edge Update (on-demand)", Manual, Risk.Safe, "Updaters", "On-demand Edge updater."),
        ("MicrosoftEdgeElevationService", "Microsoft Edge Elevation", Manual, Risk.Safe, "Updaters", "Elevation helper for Edge."),
        ("gupdate", "Google Update", Manual, Risk.Safe, "Updaters", "Google Chrome's updater (only present if Chrome is installed)."),
        ("gupdatem", "Google Update (on-demand)", Manual, Risk.Safe, "Updaters", "Google Chrome's on-demand updater."),
    ];

    private static IEnumerable<Tweak> Services()
    {
        foreach (var (svc, name, start, risk, group, desc) in ServiceTable)
        {
            var verb = start == Disabled ? "Disable" : "Set to Manual:";
            yield return new Tweak
            {
                Category = TweakCategory.Services,
                Id = "svc." + svc.ToLowerInvariant(),
                Name = $"{verb} {name}",
                Description = $"{desc} [{svc} → {ServiceStartOp.StartName(start)}]",
                Risk = risk,
                Group = group,
                Ops = [new ServiceStartOp(svc, start)],
                Recommended = risk == Risk.Safe && group is "Telemetry" && start == Disabled,
            };
        }
    }
}
