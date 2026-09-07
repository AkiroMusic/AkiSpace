using System.Diagnostics;
using System.Runtime.InteropServices;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Services;

/// <summary>
/// Manages the Windows child session lifecycle: enable/disable, query id,
/// logoff, RDP port discovery, and RDP Wrapper detection.
/// </summary>
public sealed class ChildSessionManager
{
    private readonly ILogger<ChildSessionManager> _logger;

    public ChildSessionManager(ILogger<ChildSessionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Enables Windows child sessions (the "second desktop" mechanism).</summary>
    public bool EnableChildSessions()
    {
        if (!WtsApi.WTSEnableChildSessions(true))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("WTSEnableChildSessions(true) failed, Win32 error {Error}", err);
            return false;
        }
        _logger.LogInformation("Child sessions enabled");
        return true;
    }

    /// <summary>Disables Windows child sessions.</summary>
    public bool DisableChildSessions()
    {
        if (!WtsApi.WTSEnableChildSessions(false))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("WTSEnableChildSessions(false) failed, Win32 error {Error}", err);
            return false;
        }
        _logger.LogInformation("Child sessions disabled");
        return true;
    }

    /// <summary>Whether child sessions are currently enabled.</summary>
    public bool IsChildSessionsEnabled()
    {
        if (!WtsApi.WTSIsChildSessionsEnabled(out var enabled))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("WTSIsChildSessionsEnabled failed, Win32 error {Error}", err);
            return false;
        }
        return enabled;
    }

    /// <summary>Gets the current child session id, or null if no child session exists.</summary>
    public uint? TryGetChildSessionId()
    {
        if (!WtsApi.WTSGetChildSessionId(out var sessionId))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogDebug("WTSGetChildSessionId failed (no child session yet), Win32 error {Error}", err);
            return null;
        }
        if (sessionId == WtsApi.NoChildSessionId)
            return null;
        return sessionId;
    }

    /// <summary>Logs off (terminates) the child session.</summary>
    public bool LogoffChildSession(uint sessionId)
    {
        if (!WtsApi.WTSLogoffSession(WtsApi.WTS_CURRENT_SERVER_HANDLE, sessionId, bWait: false))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("WTSLogoffSession({SessionId}) failed, Win32 error {Error}", sessionId, err);
            return false;
        }
        _logger.LogInformation("Child session {SessionId} logged off", sessionId);
        return true;
    }

    /// <summary>
    /// Reads the configured RDP port from the registry (RDP-Tcp\PortNumber),
    /// defaulting to 3389.
    /// </summary>
    public int GetConfiguredRdpPort()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            var value = key?.GetValue("PortNumber") as int?;
            return value is > 0 and <= 65535 ? value.Value : 3389;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read RDP port from registry, defaulting to 3389");
            return 3389;
        }
    }

    /// <summary>
    /// Detects whether RDP multi-session unlock is installed by checking
    /// TermService's ServiceDll registry value. Supports both:
    ///  - sergiye/rdpWrapper (TermWrap.dll — modern, doesn't patch termsrv.dll)
    ///  - classic stascorp/rdpwrap (rdpwrap.dll)
    /// </summary>
    public bool IsRdpWrapperInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            var serviceDll = key?.GetValue("ServiceDll") as string;
            var found = !string.IsNullOrEmpty(serviceDll) && (
                serviceDll.Contains("rdpwrap.dll", StringComparison.OrdinalIgnoreCase) ||
                serviceDll.Contains("TermWrap.dll", StringComparison.OrdinalIgnoreCase));
            _logger.LogDebug("RDP unlock layer installed: {Found} (ServiceDll={ServiceDll})", found, serviceDll);
            return found;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check RDP unlock layer status");
            return false;
        }
    }

    /// <summary>Gets the termsrv.dll file version (drives rdpwrap.ini matching).
    /// Returns the string form, e.g. "10.0.26100.8972" — the same format the
    /// rdpwrap.ini section headers use. Strips any "(WinBuild...)" suffix.</summary>
    public string GetTermsrvVersion()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "termsrv.dll");
            var info = FileVersionInfo.GetVersionInfo(path);
            var raw = info.FileVersion ?? info.ProductVersion ?? string.Empty;
            var space = raw.IndexOf(' ');
            if (space > 0) raw = raw[..space];
            return string.IsNullOrWhiteSpace(raw) ? "unknown" : raw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read termsrv.dll version");
            return "unknown";
        }
    }

    /// <summary>
    /// Enumerates terminal sessions via WTS and returns (id, name, state) tuples.
    /// Used for verification ("query user" equivalent) and diagnostics.
    /// </summary>
    public List<(uint Id, string Name, WtsApi.WTS_CONNECTSTATE_CLASS State)> EnumerateSessions()
    {
        var result = new List<(uint, string, WtsApi.WTS_CONNECTSTATE_CLASS)>();
        if (!WtsApi.WTSEnumerateSessions(
                WtsApi.WTS_CURRENT_SERVER_HANDLE, 0, 1, out var pSessionInfo, out var count))
        {
            _logger.LogWarning("WTSEnumerateSessions failed, Win32 error {Error}", Marshal.GetLastWin32Error());
            return result;
        }

        try
        {
            var ptr = pSessionInfo;
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsApi.WTS_SESSION_INFO>(ptr);
                var name = Marshal.PtrToStringUni(info.pWinStationName) ?? string.Empty;
                result.Add((info.SessionId, name, info.State));
                ptr = IntPtr.Add(ptr, Marshal.SizeOf<WtsApi.WTS_SESSION_INFO>());
            }
        }
        finally
        {
            WtsApi.WTSFreeMemory(pSessionInfo);
        }

        return result;
    }

    /// <summary>
    /// Checks whether the RDP listener is active by probing the TCP port.
    /// Probes both 127.0.0.1 and 0.0.0.0 with a generous timeout and a small retry
    /// loop, because right after a TermService restart (e.g. after disabling the
    /// RDP Wrapper hook) the listener can take a few seconds to start accepting
    /// connections on loopback, even though the LISTENING socket is already
    /// registered.
    /// </summary>
    public bool IsRdpListenerActive()
    {
        var port = GetConfiguredRdpPort();
        var endpoints = new[] { "127.0.0.1", "0.0.0.0" };
        var perAttemptTimeoutMs = 1500;
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            foreach (var host in endpoints)
            {
                using var client = new System.Net.Sockets.TcpClient();
                try
                {
                    var task = client.ConnectAsync(host, port);
                    if (task.Wait(perAttemptTimeoutMs) && client.Connected)
                    {
                        return true;
                    }
                }
                catch
                {
                    // fall through to next attempt
                }
            }
            // small backoff between retry rounds
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }
}