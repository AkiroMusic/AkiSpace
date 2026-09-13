using System.Diagnostics;
using System.Runtime.InteropServices;
using AkiSpace.Common;
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
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKeys.RdpTcp);
            var value = key?.GetValue(RegistryKeys.PortNumber) as int?;
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
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKeys.TermServiceParameters);
            var serviceDll = key?.GetValue(RegistryKeys.ServiceDll) as string;
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
    /// Blocking variant for console tools and synchronous check surfaces (SelfTest,
    /// EnvironmentVerifier.CheckRdpListener). Blocks up to ~5 s while the probe's
    /// retry loop runs; UI paths should use <see cref="IsRdpListenerActiveAsync"/>.
    /// </summary>
    public bool IsRdpListenerActive() => IsRdpListenerActiveAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Checks whether the RDP listener is active by probing the loopback TCP port.
    /// Uses a small retry loop with backoff, because right after a TermService
    /// restart (e.g. after disabling the RDP Wrapper hook) the listener can take a
    /// few seconds to start accepting connections on loopback, even though the
    /// LISTENING socket is already registered.
    /// Fully asynchronous and cancellable: each connect is bounded by a per-attempt
    /// token, so no task outlives its TcpClient (no orphaned unobserved faults).
    /// </summary>
    public async Task<bool> IsRdpListenerActiveAsync(CancellationToken ct = default)
    {
        var port = GetConfiguredRdpPort();
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var client = new System.Net.Sockets.TcpClient();
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(1500);
                await client.ConnectAsync("127.0.0.1", port, attemptCts.Token).ConfigureAwait(false);
                if (client.Connected)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // per-attempt timeout — fall through to the next attempt
            }
            catch (OperationCanceledException)
            {
                throw; // caller cancellation must propagate
            }
            catch
            {
                // refused/unreachable — fall through to the next attempt
            }
            finally
            {
                client.Dispose();
            }

            if (attempt < maxAttempts)
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }
        return false;
    }
}