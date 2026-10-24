using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LibreHardwareMonitor.Hardware;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>
/// CPU and motherboard sensors without running all of Money Tweaks as administrator. LibreHardwareMonitor reads them
/// through the PawnIO driver, which only administrators may open, so a small elevated copy of Money Tweaks
/// (<c>--sensor-helper</c>) reads just those sensors and streams the values over a private named pipe to the normal app.
/// The helper accepts no commands, sends only readings, and exits when Money Tweaks exits or the pipe closes.
/// </summary>
public static class ElevatedSensors
{
    public const string HelperArg = "--sensor-helper";
    private static readonly object Gate = new();
    private static List<(HardwareType hw, string device, SensorType type, string name, double value)> _latest = [];
    private static DateTime _latestAt = DateTime.MinValue;
    private static Process? _helper;
    private static bool _helperDriver;

    /// <summary>The helper is running and streaming.</summary>
    public static bool Connected => DateTime.UtcNow - _latestAt < TimeSpan.FromSeconds(5);
    /// <summary>The helper is connected and reports that it has driver access (PawnIO, elevated).</summary>
    public static bool HasDriver => Connected && _helperDriver;
    public static string Status { get; private set; } = "Not started.";

    /// <summary>The newest readings from the helper (empty when it isn't running or stopped streaming).</summary>
    public static List<(HardwareType hw, string device, SensorType type, string name, double value)> Latest
    {
        get { lock (Gate) return Connected ? [.. _latest] : []; }
    }

    /// <summary>
    /// Starts the helper with Windows' administrator prompt (UAC). False if the prompt was declined or it couldn't start.
    /// When Money Tweaks already runs as administrator there's nothing to start: the hub reads the sensors itself.
    /// </summary>
    public static bool Start()
    {
        if (Admin.IsElevated) { Status = "Money Tweaks runs as administrator and reads the sensors itself."; return true; }
        lock (Gate) if (_helper is { HasExited: false }) return true;
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        string pipe = "MoneyTweaks.Sensors." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, $"{HelperArg} {pipe} {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            lock (Gate) _helper = p;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            server.Dispose();
            Status = "Administrator permission was declined, so CPU sensors aren't read.";
            return false;
        }
        Status = "Starting the sensor helper…";
        _ = Task.Run(() => Receive(server));
        Log.Info("Elevated sensor helper started");
        return true;
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { if (_helper is { HasExited: false } p) p.Kill(); } catch { }
            _helper = null;
            _latest = [];
            _latestAt = DateTime.MinValue;
        }
    }

    private static async Task Receive(NamedPipeServerStream server)
    {
        try
        {
            using (server)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await server.WaitForConnectionAsync(cts.Token);
                using var reader = new StreamReader(server);
                var batch = new List<(HardwareType, string, SensorType, string, double)>();
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("#frame", StringComparison.Ordinal))
                    {
                        _helperDriver = line.Contains("driver=1", StringComparison.Ordinal);
                        lock (Gate) { _latest = batch; _latestAt = DateTime.UtcNow; }
                        Status = _helperDriver ? "Reading CPU sensors through LibreHardwareMonitor (PawnIO driver)." : "The sensor helper runs, but the PawnIO driver isn't available to it.";
                        batch = [];
                    }
                    else if (Parse(line) is { } r) batch.Add(r);
                }
            }
        }
        catch (Exception ex) { Log.Debug("sensor helper pipe: " + ex.Message); }
        Status = "The sensor helper stopped.";
        lock (Gate) { _latest = []; _latestAt = DateTime.MinValue; }
    }

    /// <summary>One reading line: hardware type, device, sensor type, sensor name and value, tab-separated (invariant culture).</summary>
    public static string Format((HardwareType hw, string device, SensorType type, string name, double value) r) =>
        string.Join('\t', r.hw, Clean(r.device), r.type, Clean(r.name), r.value.ToString("R", CultureInfo.InvariantCulture));

    public static (HardwareType hw, string device, SensorType type, string name, double value)? Parse(string line)
    {
        var f = line.Split('\t');
        if (f.Length != 5 || !Enum.TryParse<HardwareType>(f[0], out var hw) || !Enum.TryParse<SensorType>(f[2], out var st)
            || !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return null;
        // Only what the helper is for: CPU and motherboard sensors, with the same plausibility limits as the hub.
        if (hw is not (HardwareType.Cpu or HardwareType.Motherboard or HardwareType.SuperIO)) return null;
        return (hw, f[1], st, f[3], v);
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// The elevated helper itself: reads CPU and motherboard sensors once a second and writes them to the pipe. Exits when
    /// the parent process ends or the pipe breaks. Never reads anything from the pipe.
    /// </summary>
    public static int RunHelper(string pipe, int parentPid)
    {
        // Everything inside one boundary: the helper has no UI, so no error may ever reach Money Tweaks' error dialog.
        try
        {
            if (!pipe.StartsWith("MoneyTweaks.Sensors.", StringComparison.Ordinal)) return 2;
            using var parent = Process.GetProcessById(parentPid);
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(10_000);
            using var lhm = new LhmSource();
            var w = new StreamWriter(client) { AutoFlush = false, NewLine = "\n" };
            Log.Info($"Sensor helper running (admin: {Admin.IsElevated}, PawnIO: {LhmSource.PawnIoInstalled})");
            while (!parent.HasExited && client.IsConnected)
            {
                foreach (var r in lhm.Read(cpuAndBoardOnly: true)) w.WriteLine(Format(r));
                w.WriteLine($"#frame driver={(LhmSource.DriverActive ? 1 : 0)}");
                w.Flush();
                Thread.Sleep(1000);
            }
            return 0;
        }
        catch (IOException) { return 0; }   // Money Tweaks closed the pipe
        catch (Exception ex) { Log.Debug("sensor helper: " + ex.Message); return 1; }
    }

    // ---- PawnIO installer ---------------------------------------------------------------------------------------------

    /// <summary>The official PawnIO release (https://github.com/namazso/PawnIO.Setup), pinned by its published SHA-256.</summary>
    public const string PawnIoUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    public const string PawnIoSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";

    /// <summary>
    /// Downloads the official PawnIO installer and checks it: exact SHA-256 of the pinned release and a valid Authenticode
    /// signature. Returns the file path, or throws with the reason. Nothing is run.
    /// </summary>
    public static async Task<string> DownloadPawnIoAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MoneyTweaks");
        var bytes = await http.GetByteArrayAsync(PawnIoUrl, ct);
        progress?.Report(1);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(PawnIoSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The downloaded installer's checksum ({hash}) isn't the official release's; it was not used.");
        var path = Path.Combine(Path.GetTempPath(), $"MoneyTweaks-PawnIO-{Guid.NewGuid():N}", "PawnIO_setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        if (!SignatureValid(path)) { try { File.Delete(path); } catch { } throw new InvalidDataException("The installer's digital signature isn't valid; it was not used."); }
        return path;
    }

    /// <summary>Runs the verified installer with Windows' administrator prompt and waits for it. False if declined.</summary>
    public static bool RunPawnIoInstaller(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit();
            Log.Info($"PawnIO installer finished (exit {p?.ExitCode}); installed: {LhmSource.PawnIoInstalled}");
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    // WinVerifyTrust with WINTRUST_ACTION_GENERIC_VERIFY_V2: the file's Authenticode signature chains to a trusted root.
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid action, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    public static bool SignatureValid(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WinTrustData { cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */, pFile = pFile, dwStateAction = 0 };
            Marshal.StructureToPtr(data, pData, false);
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, pData) == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); Marshal.FreeHGlobal(pData); }
    }
}
