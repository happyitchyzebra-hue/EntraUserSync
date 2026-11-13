using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EntraUserSync.Core.Interop;

/// <summary>
/// All Win32 interop for EntraUserSync lives here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const int WTS_CURRENT_SERVER_HANDLE = 0;
    internal const int TokenUser = 1;

    internal enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_USER
    {
        public SID_AND_ATTRIBUTES User;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateSessionsW(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(IntPtr pMemory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        IntPtr TokenInformation,
        int TokenInformationLength,
        out int ReturnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr LocalFree(IntPtr hMem);
}
