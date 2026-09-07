using System.Runtime.InteropServices;

namespace AkiSpace.Native;

/// <summary>
/// Managed COM interface definitions for the MSTSC ActiveX control (mstscax.dll).
/// We use COM reflection (InvokeMember) for most properties (BetterGI's proven pattern),
/// but these interfaces give typed access to the extended settings and event sink.
/// </summary>
internal static class RdpCom
{
    /// <summary>
    /// MsRdpClient11 CLSID — BetterGI's CLSID (they label it "MsRdpClient10"
    /// but A0C63C30 actually maps to "version 11" on current systems).
    /// This is the one BetterGI successfully casts to IMsRdpExtendedSettings.
    /// </summary>
    public const string RdpClient11Clsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";

    // ---- IMsRdpExtendedSettings ----
    // Lets us set the "ConnectToChildSession" magic property that connects to
    // the WTS child session instead of a normal RDP connection.
    // NOTE: the value parameter MUST be `ref object` (matching the actual
    // type-library signature) for the RCW QueryInterface to bind correctly.
    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMsRdpExtendedSettings
    {
        void set_Property(
            [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    // ---- IMsRdpClientNonScriptable ----
    // Provides SendKeys (scan-code keyboard injection) and ClearTextPassword.
    // Method ORDER is critical: vtable dispatch uses slot position, not name.
    // This layout matches the actual mstscax type library / BetterGI exactly.
    [ComImport]
    [Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMsRdpClientNonScriptable
    {
        void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);        // slot 0
        void put_PortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);         // slot 1
        [return: MarshalAs(UnmanagedType.BStr)] string get_PortablePassword();               // slot 2
        void put_PortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);             // slot 3
        [return: MarshalAs(UnmanagedType.BStr)] string get_PortableSalt();                   // slot 4
        void put_BinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);           // slot 5
        [return: MarshalAs(UnmanagedType.BStr)] string get_BinaryPassword();                 // slot 6
        void put_BinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);               // slot 7
        [return: MarshalAs(UnmanagedType.BStr)] string get_BinarySalt();                     // slot 8
        void ResetPassword();                                                               // slot 9
        void NotifyRedirectDeviceChange(nuint wParam, nint lParam);                         // slot 10
        void SendKeys(int numKeys, [In] ref short keyUpStates, [In] ref int keyData);       // slot 11
    }

    // ---- IMsTscAxEvents ----
    // The event sink interface. DispId values come from the actual mstscax.dll
    // type library and have been cross-referenced with BetterGI's known-working
    // definition. The gaps in the DispId sequence (no 25, 27, 31) are real —
    // those slots are occupied by other events in the type library that we
    // don't handle here (their absence is fine because IDispatch just returns
    // DISP_E_MEMBERNOTFOUND for unhandled dispIds).
    //
    // The three out-parameter events are critical for the connection handshake:
    //   OnReceivedTSPublicKey  → continueLogon MUST be set to true,
    //   OnConfirmClose         → allowClose MUST be set to true,
    //   OnAutoReconnecting     → continueStatus MUST be set to Automatic (0).
    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IMsTscAxEvents
    {
        [DispId(1)] void OnConnecting();
        [DispId(2)] void OnConnected();
        [DispId(3)] void OnLoginComplete();
        [DispId(4)] void OnDisconnected([In] int disconnectReason);
        [DispId(5)] void OnEnterFullScreenMode();
        [DispId(6)] void OnLeaveFullScreenMode();
        [DispId(7)] void OnChannelReceivedData([In, MarshalAs(UnmanagedType.BStr)] string channelName, [In, MarshalAs(UnmanagedType.BStr)] string data);
        [DispId(8)] void OnRequestGoFullScreen();
        [DispId(9)] void OnRequestLeaveFullScreen();
        [DispId(10)] void OnFatalError([In] int errorCode);
        [DispId(11)] void OnWarning([In] int warningCode);
        [DispId(12)] void OnRemoteDesktopSizeChange([In] int width, [In] int height);
        [DispId(13)] void OnIdleTimeoutNotification();
        [DispId(14)] void OnRequestContainerMinimize();
        [DispId(15)] void OnConfirmClose([Out] out bool allowClose);
        [DispId(16)] void OnReceivedTSPublicKey([In, MarshalAs(UnmanagedType.BStr)] string publicKey, [Out] out bool continueLogon);
        [DispId(17)] void OnAutoReconnecting([In] int disconnectReason, [In] int attemptCount, [Out] out AutoReconnectContinueState continueStatus);
        [DispId(18)] void OnAuthenticationWarningDisplayed();
        [DispId(19)] void OnAuthenticationWarningDismissed();
        [DispId(20)] void OnRemoteProgramResult([In, MarshalAs(UnmanagedType.BStr)] string remoteProgram, [In] RemoteProgramResult error, [In] bool isExecutable);
        [DispId(21)] void OnRemoteProgramDisplayed([In] bool displayed, [In] uint displayInformation);
        [DispId(29)] void OnRemoteWindowDisplayed([In] bool displayed, [In] ref RemotableHandle windowHandle, [In] RemoteWindowDisplayedAttribute windowAttribute);
        [DispId(22)] void OnLogonError([In] int errorCode);
        [DispId(23)] void OnFocusReleased([In] int direction);
        [DispId(24)] void OnUserNameAcquired([In, MarshalAs(UnmanagedType.BStr)] string userName);
        [DispId(26)] void OnMouseInputModeChanged([In] bool isRelativeMouseMode);
        [DispId(28)] void OnServiceMessageReceived([In, MarshalAs(UnmanagedType.BStr)] string serviceMessage);
        [DispId(30)] void OnConnectionBarPullDown();
        [DispId(32)] void OnNetworkStatusChanged([In] uint qualityLevel, [In] int bandwidth, [In] int roundTripTime);
        [DispId(35)] void OnDevicesButtonPressed();
        [DispId(33)] void OnAutoReconnected();
        [DispId(34)] void OnAutoReconnecting2([In] int disconnectReason, [In] bool networkAvailable, [In] int attemptCount, [In] int maxAttemptCount);
    }

    // ---- Supporting enums / structs for IMsTscAxEvents signatures ----

    public enum AutoReconnectContinueState
    {
        Automatic,
        Stop,
        Manual,
    }

    public enum RemoteProgramResult
    {
        Ok,
        Locked,
        ProtocolError,
        NotInWhitelist,
        NetworkPathDenied,
        FileNotFound,
        Failure,
        HookNotLoaded,
    }

    public enum RemoteWindowDisplayedAttribute
    {
        None,
        WindowDisplayed,
        ShellIconDisplayed,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RemotableHandle
    {
        public int Context;
        public RemotableHandleUnion Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RemotableHandleUnion
    {
        [FieldOffset(0)] public int InProcessHandle;
        [FieldOffset(0)] public int RemoteHandle;
    }
}