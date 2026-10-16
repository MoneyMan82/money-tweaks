using System.Runtime.InteropServices;

namespace MoneyTweaks.Core.Features;

internal static partial class Native
{
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetSystemInformation(int infoClass, ref int info, int length);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, nint prev, nint retLen);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint h);

    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }
}

/// <summary>Kernel timer resolution control (100-ns units). Requests last only while this process runs.</summary>
public static class TimerResolution
{
    public static (double minMs, double maxMs, double currentMs) Query()
    {
        Native.NtQueryTimerResolution(out var min, out var max, out var cur);
        // NT naming is inverted: "minimum" is the coarsest (e.g. 15.6 ms), "maximum" the finest (e.g. 0.5 ms).
        return (min / 10000.0, max / 10000.0, cur / 10000.0);
    }

    public static double Set(double ms)
    {
        Native.NtSetTimerResolution((uint)Math.Round(ms * 10000), true, out var cur);
        return cur / 10000.0;
    }

    public static double Release(double ms)
    {
        Native.NtSetTimerResolution((uint)Math.Round(ms * 10000), false, out var cur);
        return cur / 10000.0;
    }
}

public static class MemoryInfo
{
    public static (ulong total, ulong available, uint loadPercent) Status()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        return (m.ullTotalPhys, m.ullAvailPhys, m.dwMemoryLoad);
    }

    /// <summary>Commit limit (RAM + page file) and how much of it is still available, in bytes.</summary>
    public static (ulong limit, ulong available) Commit()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        return (m.ullTotalPageFile, m.ullAvailPageFile);
    }

    /// <summary>Empties the standby (cached) memory list, like RAMMap's "Empty Standby List". Needs admin.</summary>
    public static (bool ok, string message) PurgeStandbyList()
    {
        if (!Sys.Admin.IsElevated) return (false, "Requires administrator rights.");
        if (!Native.EnablePrivilege("SeProfileSingleProcessPrivilege")) return (false, "Could not enable SeProfileSingleProcessPrivilege.");
        int command = 4; // MemoryPurgeStandbyList
        var status = Native.NtSetSystemInformation(80 /* SystemMemoryListInformation */, ref command, sizeof(int));
        return status == 0 ? (true, "Standby list cleared.") : (false, $"NtSetSystemInformation returned 0x{status:X8}.");
    }
}
