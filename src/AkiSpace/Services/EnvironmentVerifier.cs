using System.Diagnostics;
using AkiSpace.Common;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AkiSpace.Services;

/// <summary>
/// Result of one environment check. <paramref name="Id"/> is a stable machine-readable
/// identifier (never localized) used by callers for logic; Name/Detail are display
/// strings resolved through <see cref="Loc"/> at check time.
/// </summary>
public sealed record EnvCheckResult(string Id, string Name, bool Pass, string Detail);

/// <summary>
/// One-click environment setup & verification for the desktop-clone feature:
/// RDP enabled, multi-session allowed, RDP Wrapper present (Home edition),
/// StartRCM (Home fix), security hardening, firewall loopback rule, child
/// sessions enabled, RDP listener active.
/// </summary>
public sealed class EnvironmentVerifier
{
    private readonly ILogger<EnvironmentVerifier> _logger;
    private readonly ChildSessionManager _sessionManager;
    private readonly SettingsService _settingsService;

    // Static logger handle for the few static helpers (ReadDword/SetDword/FirewallRuleExists).
    // Defaults to NullLogger; the first instance constructed sets the shared sink.
    private static ILogger _sharedLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentVerifier>.Instance;

    public EnvironmentVerifier(ILogger<EnvironmentVerifier> logger, ChildSessionManager sessionManager, SettingsService settingsService)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _settingsService = settingsService;
        _sharedLogger = logger;  // last-constructed wins; sufficient for this single-instance service
    }

    private static void LogWarning(string message) => _sharedLogger.LogWarning(message);
    private static void LogWarning(Exception ex, string message) => _sharedLogger.LogWarning(ex, message);
    private static void LogError(Exception ex, string message) => _sharedLogger.LogError(ex, message);

    // ---- Registry paths (now in Common/RegistryKeys.cs) ----

    private const string TerminalServerKey = RegistryKeys.TerminalServer;
    private const string RdpTcpKey = RegistryKeys.RdpTcp;
    private const string TermServiceParamsKey = RegistryKeys.TermServiceParameters;

    // ---- Checks ----

    public List<EnvCheckResult> RunAllChecks()
    {
        return new List<EnvCheckResult>
        {
            CheckWindowsEdition(),
            CheckRdpEnabled(),
            CheckMultiSession(),
            CheckRdpWrapper(),
            CheckStartRcm(),
            CheckTermServiceRunning(),
            CheckFirewallLoopbackRule(),
            CheckChildSessions(),
            // Probe the listener synchronously; the caller can avoid the
            // UI freeze by using RunAllChecksAsync which moves this one
            // call onto a worker thread.
            CheckRdpListener(),
            CheckTermsrvVersion(),
            CheckRdpWrapperHook(),
        };
    }

    /// <summary>
    /// Async variant for UI callers: runs ALL checks on a worker thread and returns
    /// the fully populated list. Everything here is thread-safe (registry reads,
    /// ServiceController, firewall rule query, WTS queries, TCP probe) — but the
    /// listener probe alone blocks ~5s when the listener is down, so the batch must
    /// never run on the UI thread.
    /// </summary>
    public async Task<List<EnvCheckResult>> RunAllChecksAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => RunAllChecks(), ct).ConfigureAwait(false);
    }

    // Listener probe is delegated to ChildSessionManager.

    /// <summary>Informational (always Pass): which Windows edition this is, since it
    /// decides whether standard RDP needs an unlock layer.</summary>
    public EnvCheckResult CheckWindowsEdition()
    {
        var edition = RdpWrapperInstaller.GetEditionId();
        var home = RdpWrapperInstaller.IsHomeEdition(edition);
        var detail = edition is null
            ? Loc.T("Chk_EditionUnknown")
            : Loc.F(home ? "Chk_EditionHome" : "Chk_EditionStandard", edition);
        return new("Edition", Loc.T("Chk_Edition"), true, detail);
    }

    public EnvCheckResult CheckRdpEnabled()
    {
        var value = ReadDword(TerminalServerKey, RegistryKeys.FDenyTSConnections);
        var pass = value == 0;
        return new("RdpEnabled", Loc.T("Chk_RdpEnabled"), pass,
            pass ? "fDenyTSConnections = 0" : Loc.F("Chk_RdpEnabledBad", value));
    }

    public EnvCheckResult CheckMultiSession()
    {
        var value = ReadDword(TerminalServerKey, RegistryKeys.FSingleSessionPerUser);
        var pass = value == 0;
        return new("MultiSession", Loc.T("Chk_MultiSession"), pass,
            pass ? "fSingleSessionPerUser = 0" : Loc.F("Chk_MultiSessionBad", value));
    }

    public EnvCheckResult CheckRdpWrapper()
    {
        var installed = _sessionManager.IsRdpWrapperInstalled();
        // On non-Home editions the unlock layer is NOT required (standard RDP is native),
        // so its absence must not read as a failure; on Home it genuinely gates standard RDP.
        var home = RdpWrapperInstaller.IsHomeEdition(RdpWrapperInstaller.GetEditionId());
        var pass = installed || !home;
        var detail = installed
            ? Loc.T("Chk_WrapperFound")
            : home ? Loc.T("Chk_WrapperMissing") : Loc.T("Chk_WrapperNotNeeded");
        return new("WrapperUnlock", Loc.T("Chk_WrapperUnlock"), pass, detail);
    }

    public EnvCheckResult CheckStartRcm()
    {
        var value = ReadDword(RdpTcpKey, RegistryKeys.StartRCM);
        var pass = value == 1;
        return new("StartRcm", Loc.T("Chk_StartRcm"), pass,
            pass ? "StartRCM = 1" : Loc.F("Chk_StartRcmBad", value));
    }

    public EnvCheckResult CheckTermServiceRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("TermService");
            var running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            return new("TermService", Loc.T("Chk_TermService"), running,
                running ? Loc.T("Chk_TermRunning") : Loc.F("Chk_TermStatus", sc.Status));
        }
        catch (Exception ex)
        {
            return new("TermService", Loc.T("Chk_TermService"), false, Loc.F("Env_CheckFailed", ex.Message));
        }
    }

    public EnvCheckResult CheckFirewallLoopbackRule()
    {
        // The one-click fix installs a SINGLE inbound block rule ("AkiSpace RDP Loopback")
        // on the RDP port (remoteip=any). Windows Firewall never inspects loopback traffic,
        // so this blocks every real remote client while loopback RDP keeps working — enforcing
        // "loopback only" without needing a (no-op) allow=127.0.0.1 rule. It also deletes the
        // built-in public allow rule so it can't shadow the intent.
        var blockFound = FirewallRuleExists("AkiSpace RDP Loopback");
        var publicRuleFound = FirewallRuleExists("Remote Desktop - User Mode (TCP-In)");
        var allOk = blockFound && !publicRuleFound;
        var detail = allOk
            ? Loc.T("Chk_FirewallOk")
            : Loc.F("Chk_FirewallBad", blockFound, publicRuleFound);
        return new("FirewallLoopback", Loc.T("Chk_Firewall"), allOk, detail);
    }

    public EnvCheckResult CheckChildSessions()
    {
        var enabled = _sessionManager.IsChildSessionsEnabled();
        var id = _sessionManager.TryGetChildSessionId();
        var detail = enabled
            ? (id.HasValue ? Loc.F("Chk_ChildEnabledActive", id.Value) : Loc.T("Chk_ChildEnabledIdle"))
            : Loc.T("Chk_ChildDisabled");
        return new("ChildSessions", Loc.T("Chk_ChildSessions"), enabled, detail);
    }

    public EnvCheckResult CheckRdpListener()
    {
        var active = _sessionManager.IsRdpListenerActive();
        return new("Listener", Loc.T("Chk_Listener"), active,
            active ? Loc.F("Chk_ListenerOk", _sessionManager.GetConfiguredRdpPort()) : Loc.T("Chk_ListenerBad"));
    }

    public EnvCheckResult CheckTermsrvVersion()
    {
        var version = _sessionManager.GetTermsrvVersion();
        return new("TermsrvVersion", Loc.T("Chk_TermsrvVersion"), version != "unknown", version);
    }

    // ---- Fixes ----

    /// <summary>Applies the registry settings + firewall rule. Requires admin.</summary>
    public List<EnvCheckResult> ApplyAllFixes(bool alsoDisableRdpWrapper = false)
    {
        var results = new List<EnvCheckResult>();

        // Disable RDP Wrapper hook (TermWrap.dll) ONLY when the user is opting
        // into child-session mode (where the native termsrv.dll must be active)
        // or has explicitly ticked the override. This avoids breaking the
        // Home-edition standard-RDP case where the TermWrap hook IS the
        // multi-session unlock (mutually exclusive with child sessions, per
        // BetterGI's docs).
        var shouldDisableWrapper = alsoDisableRdpWrapper
            || _settingsService.Current.ConnectionMode == ConnectionMode.ChildSession;
        if (shouldDisableWrapper)
        {
            var disableWrapOk = DisableRdpWrapperHook();
            results.Add(new("FixDisableWrapper", Loc.T("Fx_DisableWrapper"), disableWrapOk,
                disableWrapOk ? "TermService ServiceDll -> %SystemRoot%\\System32\\termsrv.dll" : Loc.T("Fx_DisableWrapperFail")));
        }
        else
        {
            results.Add(new("FixKeepWrapper", Loc.T("Fx_KeepWrapper"), true,
                Loc.T("Fx_KeepWrapperDetail")));
        }

        // fDenyTSConnections = 0 (enable RDP)
        SetDword(TerminalServerKey, RegistryKeys.FDenyTSConnections, 0);
        results.Add(CheckRdpEnabled());

        // fSingleSessionPerUser = 0 (multi-session)
        SetDword(TerminalServerKey, RegistryKeys.FSingleSessionPerUser, 0);
        results.Add(CheckMultiSession());

        // StartRCM = 1 (Home edition fix)
        SetDword(RdpTcpKey, RegistryKeys.StartRCM, 1);
        results.Add(CheckStartRcm());

        // Security hardening: TLS + high encryption
        SetDword(RdpTcpKey, RegistryKeys.SecurityLayer, 2);      // SSL/TLS
        SetDword(RdpTcpKey, RegistryKeys.MinEncryptionLevel, 3); // High
        SetDword(RdpTcpKey, RegistryKeys.UserAuthentication, 1); // NLA
        results.Add(new("FixSecurity", Loc.T("Fx_Security"), true, "SecurityLayer=2, MinEncryptionLevel=3, UserAuthentication=1"));

        // Firewall loopback rule
        var ruleOk = EnsureFirewallLoopbackRule();
        results.Add(new("FixFirewall", Loc.T("Fx_Firewall"), ruleOk,
            ruleOk ? Loc.T("Fx_FirewallOk") : Loc.T("Fx_FirewallFail")));

        // Restart TermService so the new ServiceDll (termsrv.dll instead of TermWrap.dll)
        // is loaded and the child-session broker is reset.
        var restartOk = RestartTermService();
        results.Add(new("FixRestartTermService", Loc.T("Fx_RestartTermService"), restartOk,
            restartOk ? Loc.T("Fx_RestartOk") : Loc.T("Fx_RestartFail")));

        // Enable child sessions (must be called after TermService is back up with
        // the un-hooked termsrv.dll).
        var childOk = _sessionManager.EnableChildSessions();
        results.Add(new("FixEnableChildSessions", Loc.T("Fx_EnableChildSessions"), childOk,
            childOk ? Loc.T("Fx_EnableChildOk") : Loc.T("Fx_EnableChildFail")));

        // Re-check the RDP Wrapper hook so the user sees confirmation.
        results.Add(CheckRdpWrapperHook());

        return results;
    }

    /// <summary>
    /// Detects whether TermService's ServiceDll has been hooked by RDP Wrapper
    /// (TermWrap.dll). When hooked, the child-session broker cannot create child
    /// sessions even though WTSIsChildSessionsEnabled returns true and the user
    /// is non-elevated — the broker returns errConnectToServer (516). Per
    /// BetterGI docs, RDP Wrapper and child sessions are mutually exclusive:
    /// use one or the other, not both.
    /// </summary>
    public EnvCheckResult CheckRdpWrapperHook()
    {
        const string key = RegistryKeys.TermServiceParameters;
        const string valueName = RegistryKeys.ServiceDll;
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var k = baseKey.OpenSubKey(key);
            var serviceDll = k?.GetValue(valueName) as string;
            var hooked = !string.IsNullOrEmpty(serviceDll)
                         && serviceDll.IndexOf("TermWrap", StringComparison.OrdinalIgnoreCase) >= 0;
            if (hooked)
            {
                return new(
                    "WrapperHook",
                    Loc.T("Chk_WrapperHook"),
                    false,
                    Loc.F("Chk_HookActive", serviceDll));
            }
            return new(
                "WrapperHook",
                Loc.T("Chk_WrapperHook"),
                true,
                serviceDll is null
                    ? Loc.T("Chk_HookNoDll")
                    : Loc.F("Chk_HookNone", serviceDll));
        }
        catch (Exception ex)
        {
            return new("WrapperHook", Loc.T("Chk_WrapperHook"), false, Loc.F("Env_CheckFailed", ex.Message));
        }
    }

    /// <summary>
    /// Disables the RDP Wrapper TermWrap.dll hook by restoring TermService's
    /// ServiceDll back to %SystemRoot%\System32\termsrv.dll. Requires admin.
    /// After this, TermService must be restarted (the caller will do so).
    /// </summary>
    public bool DisableRdpWrapperHook()
    {
        const string key = RegistryKeys.TermServiceParameters;
        const string valueName = RegistryKeys.ServiceDll;
        var originalDll = System.IO.Path.Combine(
            Environment.SystemDirectory, "termsrv.dll");
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var k = baseKey.OpenSubKey(key, writable: true);
            if (k is null)
            {
                _logger.LogError("DisableRdpWrapperHook: cannot open {Key}", key);
                return false;
            }
            var current = k.GetValue(valueName) as string;
            if (current is null || current.IndexOf("TermWrap", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _logger.LogInformation("RDP Wrapper hook not active; nothing to disable");
                return true;
            }
            k.SetValue(valueName, originalDll, Microsoft.Win32.RegistryValueKind.ExpandString);
            _logger.LogInformation("Restored TermService ServiceDll to {Path}", originalDll);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisableRdpWrapperHook failed");
            return false;
        }
    }

    public bool RestartTermService()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("TermService");
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                sc.Start();
            sc.Stop();
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            sc.Start();
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            _logger.LogInformation("TermService restarted");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TermService restart failed");
            return false;
        }
    }

    // ---- helpers ----

    private static int? ReadDword(string keyPath, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(name) as int?;
        }
        catch (Exception ex)
        {
            LogWarning(ex, $"ReadDword {keyPath}\\{name} failed");
            return null;
        }
    }

    private static void SetDword(string keyPath, string name, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(keyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            LogWarning(ex, $"SetDword {keyPath}\\{name} failed");
        }
    }

    private static bool FirewallRuleExists(string ruleName = "AkiSpace RDP Loopback")
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{ruleName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            // Drain BOTH streams concurrently to avoid a pipe-full deadlock: netsh can
            // write heavily to stderr, and a synchronous ReadToEnd on one stream while
            // the other fills would hang the process before WaitForExit returns.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            // Parameterless WaitForExit flushes any pending async output before we read.
            p.WaitForExit();
            _ = errTask.GetAwaiter().GetResult();
            var output = outTask.GetAwaiter().GetResult();
            return output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogWarning(ex, $"FirewallRuleExists('{ruleName}') failed");
            return false;
        }
    }

    private bool EnsureFirewallLoopbackRule()
    {
        try
        {
            var port = _sessionManager.GetConfiguredRdpPort();

            if (FirewallRuleExists("AkiSpace RDP Loopback")) return true;

            // Windows Firewall never inspects loopback (127.0.0.1/::1) traffic — it is
            // permitted at a higher WFP sub-layer. So an `allow remoteip=127.0.0.1` rule
            // is a no-op. The correct way to enforce "loopback only" is a single inbound
            // block rule on the RDP port with remoteip=any: it stops every real remote
            // client, while loopback keeps working untouched. Block rules also take
            // precedence over allow rules, so this reliably closes the port remotely.
            //
            // First drop the built-in public allow so it can't shadow our intent.
            if (RunNetsh("advfirewall firewall delete rule name=\"Remote Desktop - User Mode (TCP-In)\"", 10000))
                _logger.LogInformation("Deleted default 'Remote Desktop - User Mode (TCP-In)' rule");
            else
                _logger.LogWarning("Failed to delete 'Remote Desktop - User Mode (TCP-In)' (may not exist; idempotent)");

            // The single AkiSpace rule: block all non-loopback inbound TCP to the RDP port.
            if (!RunNetsh(BuildFirewallAddRule("AkiSpace RDP Loopback", "block", port, "any"), 15000))
                return false;

            return FirewallRuleExists("AkiSpace RDP Loopback");
        }
        catch (Exception ex)
        {
            LogWarning(ex, "EnsureFirewallLoopbackRule failed");
            return false;
        }
    }

    /// <summary>
    /// Builds the netsh "advfirewall firewall add rule" argument string.
    /// Public so SelfTest can assert the rule honors the configured RDP port.
    /// </summary>
    public static string BuildFirewallAddRule(string name, string action, int port, string remoteIp)
    {
        var remote = remoteIp == "any" ? string.Empty : $" remoteip={remoteIp}";
        return $"advfirewall firewall add rule name=\"{name}\" dir=in action={action} " +
               $"protocol=TCP localport={port}{remote}";
    }

    private static bool RunNetsh(string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var exited = p.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(2000); } catch { }
                LogWarning($"netsh timed out after {timeoutMs}ms: {args}");
                return false;
            }
            // Read ExitCode only after a confirmed exit.
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LogWarning(ex, $"netsh failed: {args}");
            return false;
        }
    }
}