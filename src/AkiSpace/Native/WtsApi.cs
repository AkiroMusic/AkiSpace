using System.Runtime.InteropServices;

namespace AkiSpace.Native;

/// <summary>
/// P/Invoke wrappers for wtsapi32.dll — Windows Terminal Services / Child Sessions API.
/// Child sessions are the "second desktop" mechanism (same technique as BetterGI).
/// </summary>
public static class WtsApi
{
    /// <summary>Use the local machine as the server handle.</summary>
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    /// <summary>No child session is active.</summary>
    public const uint NoChildSessionId = uint.MaxValue;

    /// <summary>Enables or disables Windows child sessions.</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSEnableChildSessions(
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    /// <summary>Queries whether child sessions are enabled.</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSIsChildSessionsEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    /// <summary>Gets the current child session id, or NoChildSessionId if none.</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSGetChildSessionId(out uint sessionId);

    /// <summary>Logs off a session (used to terminate the child session).</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSLogoffSession(
        IntPtr hServer,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    /// <summary>Enumerates sessions on the server.</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        [MarshalAs(UnmanagedType.U4)] uint reserved,
        [MarshalAs(UnmanagedType.U4)] uint version,
        out IntPtr ppSessionInfo,
        [MarshalAs(UnmanagedType.U4)] out uint pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    /// <summary>WTS_SESSION_INFO structure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;  // LPWSTR
        public WTS_CONNECTSTATE_CLASS State;
    }

    public enum WTS_CONNECTSTATE_CLASS
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
        WTSInit,
    }
}