using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed class NvApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Minimal NVAPI driver-settings (DRS) access: the same per-application profile database NVIDIA Control Panel and NVIDIA
/// Profile Inspector edit. Functions are resolved through nvapi_QueryInterface; a missing function or driver turns into an
/// NvApiException, never a crash. Structures are built as raw byte buffers at the documented offsets, and the driver rejects
/// any structure whose version (size) doesn't match, so a mismatch is an error code rather than memory corruption.
/// </summary>
public static unsafe class NvApi
{
    public const int Ok = 0;
    public const int SettingNotFound = -160;
    public const int ProfileNotFound = -163;
    public const int ExecutableNotFound = -166;
    public const int InvalidUserPrivilege = -137;

    private const int UnicodeBytes = 2048 * 2;                    // NvAPI_UnicodeString = NvU16[2048]
    private const int UnionBytes = 4 + 4096;                     // largest member: NVDRS_BINARY_SETTING
    internal const int SettingSize = 4 + UnicodeBytes + 5 * 4 + 2 * UnionBytes; // NVDRS_SETTING_V1 = 12320
    internal const int SettingIdOff = 4 + UnicodeBytes, SettingTypeOff = SettingIdOff + 4, LocationOff = SettingIdOff + 8,
        IsCurrentPredefinedOff = SettingIdOff + 12, IsPredefinedValidOff = SettingIdOff + 16, PredefinedOff = SettingIdOff + 20, CurrentOff = PredefinedOff + UnionBytes;
    private const int ProfileSize = 4 + UnicodeBytes + 4 * 4;   // NVDRS_PROFILE_V1
    private const int AppSizeV3 = 4 + 4 + 4 * UnicodeBytes + 4;  // NVDRS_APPLICATION_V3
    private const int MaxValues = 100;
    private const int ValuesSize = 12 + UnionBytes + MaxValues * UnionBytes; // NVDRS_SETTING_VALUES_V1
    private static uint Ver(int size, int v) => (uint)size | ((uint)v << 16);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    private static bool _initialized;
    private static readonly object Gate = new();

    public static bool DriverPresent => NativeLibrary.TryLoad("nvapi64.dll", out _);

    private static IntPtr F(uint id)
    {
        var p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new NvApiException(-3, $"This NVIDIA driver doesn't provide NVAPI function 0x{id:X8}.");
        return p;
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            if (!DriverPresent) throw new NvApiException(-2, "No NVIDIA driver found (nvapi64.dll is missing).");
            Check(((delegate* unmanaged[Cdecl]<int>)F(0x0150E828))(), "NvAPI_Initialize");
            _initialized = true;
        }
    }

    public static string ErrorText(int status)
    {
        try
        {
            var buf = stackalloc byte[64];
            if (((delegate* unmanaged[Cdecl]<int, byte*, int>)F(0x6C2D048C))(status, buf) == Ok)
                return Marshal.PtrToStringAnsi((IntPtr)buf) ?? status.ToString();
        }
        catch { }
        return "NVAPI error " + status;
    }

    internal static void Check(int status, string what)
    {
        if (status == Ok) return;
        throw new NvApiException(status, status == InvalidUserPrivilege
            ? "Changing NVIDIA driver settings needs administrator rights."
            : $"{what} failed: {ErrorText(status)} ({status})");
    }

    private static void PutString(byte* dst, string s)
    {
        var chars = s.AsSpan(0, Math.Min(s.Length, 2047));
        fixed (char* c = chars) Buffer.MemoryCopy(c, dst, UnicodeBytes, chars.Length * 2);
        ((char*)dst)[chars.Length] = '\0';
    }

    private static string GetString(byte* src) => new((char*)src);

    /// <summary>Driver's own name for a setting ID ("Texture filtering - Quality"), or null if the driver doesn't know it.</summary>
    public static string? SettingName(uint id)
    {
        Initialize();
        var buf = (byte*)NativeMemory.AllocZeroed(UnicodeBytes);
        try { return ((delegate* unmanaged[Cdecl]<uint, byte*, int>)F(0xD61CBE6E))(id, buf) == Ok ? GetString(buf) : null; }
        finally { NativeMemory.Free(buf); }
    }

    /// <summary>The values the driver lists for a setting (empty if it lists none) and its default.</summary>
    public static (uint? defaultValue, List<uint> values) SettingValues(uint id)
    {
        Initialize();
        var buf = (byte*)NativeMemory.AllocZeroed(ValuesSize);
        try
        {
            *(uint*)buf = Ver(ValuesSize, 1);
            uint max = MaxValues;
            var st = ((delegate* unmanaged[Cdecl]<uint, uint*, byte*, int>)F(0x2EC39F90))(id, &max, buf);
            if (st != Ok) return (null, []);
            var n = Math.Min(*(uint*)(buf + 4), (uint)MaxValues);
            var list = new List<uint>();
            for (int i = 0; i < n; i++) list.Add(*(uint*)(buf + 12 + UnionBytes + i * UnionBytes));
            return (*(uint*)(buf + 12), list);
        }
        finally { NativeMemory.Free(buf); }
    }

    /// <summary>A DRS session. Load happens on open; Save must be called explicitly to write the database.</summary>
    public sealed class Session : IDisposable
    {
        private IntPtr _h;
        public IntPtr Handle => _h;

        public Session()
        {
            Initialize();
            IntPtr h;
            Check(((delegate* unmanaged[Cdecl]<IntPtr*, int>)F(0x0694D52E))(&h), "Create driver settings session");
            _h = h;
            Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)F(0x375DBD6B))(_h), "Load driver settings");
        }

        public void Save() => Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)F(0xFCBC7E14))(_h), "Save driver settings");

        /// <summary>Profile that owns this executable, or IntPtr.Zero if no profile lists it.</summary>
        public IntPtr FindProfileForApp(string exe)
        {
            var name = (byte*)NativeMemory.AllocZeroed(UnicodeBytes);
            var app = (byte*)NativeMemory.AllocZeroed(AppSizeV3);
            try
            {
                PutString(name, exe);
                *(uint*)app = Ver(AppSizeV3, 3);
                IntPtr p;
                var st = ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, byte*, int>)F(0xEEE566B2))(_h, name, &p, app);
                if (st is ExecutableNotFound or ProfileNotFound) return IntPtr.Zero;
                Check(st, "Find application profile");
                return p;
            }
            finally { NativeMemory.Free(name); NativeMemory.Free(app); }
        }

        public IntPtr FindProfile(string profileName)
        {
            var name = (byte*)NativeMemory.AllocZeroed(UnicodeBytes);
            try
            {
                PutString(name, profileName);
                IntPtr p;
                var st = ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)F(0x7E4A9A0B))(_h, name, &p);
                if (st == ProfileNotFound) return IntPtr.Zero;
                Check(st, "Find profile");
                return p;
            }
            finally { NativeMemory.Free(name); }
        }

        public string ProfileName(IntPtr profile)
        {
            var buf = (byte*)NativeMemory.AllocZeroed(ProfileSize);
            try
            {
                *(uint*)buf = Ver(ProfileSize, 1);
                Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)F(0x61CD6FD6))(_h, profile, buf), "Read profile info");
                return GetString(buf + 4);
            }
            finally { NativeMemory.Free(buf); }
        }

        /// <summary>Creates a user profile containing one application.</summary>
        public IntPtr CreateProfileWithApp(string profileName, string exe, string friendly)
        {
            var prof = (byte*)NativeMemory.AllocZeroed(ProfileSize);
            var app = (byte*)NativeMemory.AllocZeroed(AppSizeV3);
            try
            {
                *(uint*)prof = Ver(ProfileSize, 1);
                PutString(prof + 4, profileName);
                IntPtr p;
                Check(((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)F(0xCC176068))(_h, prof, &p), "Create profile");
                *(uint*)app = Ver(AppSizeV3, 3);
                PutString(app + 8, exe);
                PutString(app + 8 + UnicodeBytes, friendly);
                Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)F(0x4347A9DE))(_h, p, app), "Add application to profile");
                return p;
            }
            finally { NativeMemory.Free(prof); NativeMemory.Free(app); }
        }

        public void DeleteProfile(IntPtr profile) => Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F(0x17093206))(_h, profile), "Delete profile");

        /// <summary>The setting as this profile resolves it, or null if neither the profile nor its parents define it.</summary>
        public NvSettingState? Get(IntPtr profile, uint id)
        {
            var buf = (byte*)NativeMemory.AllocZeroed(SettingSize);
            try
            {
                *(uint*)buf = Ver(SettingSize, 1);
                var st = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, byte*, int>)F(0x73BF8338))(_h, profile, id, buf);
                if (st == SettingNotFound) return null;
                Check(st, $"Read setting 0x{id:X8}");
                return new NvSettingState(id, *(uint*)(buf + SettingTypeOff), *(uint*)(buf + LocationOff),
                    *(uint*)(buf + IsCurrentPredefinedOff) != 0, *(uint*)(buf + IsPredefinedValidOff) != 0,
                    *(uint*)(buf + PredefinedOff), *(uint*)(buf + CurrentOff));
            }
            finally { NativeMemory.Free(buf); }
        }

        public void Set(IntPtr profile, uint id, uint value)
        {
            var buf = (byte*)NativeMemory.AllocZeroed(SettingSize);
            try
            {
                *(uint*)buf = Ver(SettingSize, 1);
                *(uint*)(buf + SettingIdOff) = id;
                *(uint*)(buf + SettingTypeOff) = 0; // NVDRS_DWORD_TYPE
                *(uint*)(buf + CurrentOff) = value;
                Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)F(0x577DD202))(_h, profile, buf), $"Set setting 0x{id:X8}");
            }
            finally { NativeMemory.Free(buf); }
        }

        /// <summary>Removes the profile's own value: NVIDIA's predefined value (if any) or the global value applies again.</summary>
        public void Reset(IntPtr profile, uint id)
        {
            var st = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)F(0x53F0381E))(_h, profile, id);
            if (st == Ok) return;
            st = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)F(0xE4A26362))(_h, profile, id);
            if (st is Ok or SettingNotFound) return;
            Check(st, $"Reset setting 0x{id:X8}");
        }

        public void Dispose()
        {
            if (_h == IntPtr.Zero) return;
            try { ((delegate* unmanaged[Cdecl]<IntPtr, int>)F(0xDAD9CFF8))(_h); } catch { }
            _h = IntPtr.Zero;
        }
    }
}

/// <summary>Location: 0 = this profile, 1 = global profile, 2 = base profile, 3 = driver default.</summary>
public sealed record NvSettingState(uint Id, uint Type, uint Location, bool IsCurrentPredefined, bool IsPredefinedValid, uint Predefined, uint Current)
{
    public bool InProfile => Location == 0;
}
