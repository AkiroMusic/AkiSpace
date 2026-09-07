using System.Diagnostics;
using AkiSpace.Common;
using AkiSpace.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AkiSpace.Services;

/// <summary>Result of one environment check.</summary>
public sealed record EnvCheckResult(string Name, bool Pass, string Detail);

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
    /// Async variant: runs the synchronous checks inline (fast), then
    /// probes the listener on a worker thread (up to ~10s on a cold listener)
    /// and patches the returned list in place. Returns the fully populated list.
    /// </summary>
    public async Task<List<EnvCheckResult>> RunAllChecksAsync(CancellationToken ct = default)
    {
        var results = RunAllChecks();
        // Find the listener check (index 7) and replace with a "checking..." placeholder.
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Name.Contains("RDP 监听"))
            {
                results[i] = new EnvCheckResult(results[i].Name, false, "检查中...");
                break;
            }
        }
        var port = _sessionManager.GetConfiguredRdpPort();
        var active = await Task.Run(() => IsListenerActiveInternal(port), ct);
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Name.Contains("RDP 监听"))
            {
                results[i] = new EnvCheckResult(
                    results[i].Name, active,
                    active ? $"端口 {port} 正在监听" : "监听失败（TermService 未运行或未解锁）");
                break;
            }
        }
        return results;
    }

    private static bool IsListenerActiveInternal(int port)
    {
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
                        return true;
                }
                catch { }
            }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    public EnvCheckResult CheckRdpEnabled()
    {
        var value = ReadDword(TerminalServerKey, RegistryKeys.FDenyTSConnections);
        var pass = value == 0;
        return new("RDP 已启用 (fDenyTSConnections)", pass, pass ? "fDenyTSConnections = 0" : $"fDenyTSConnections = {value}（应为 0）");
    }

    public EnvCheckResult CheckMultiSession()
    {
        var value = ReadDword(TerminalServerKey, RegistryKeys.FSingleSessionPerUser);
        var pass = value == 0;
        return new("允许多会话 (fSingleSessionPerUser)", pass, pass ? "fSingleSessionPerUser = 0" : $"fSingleSessionPerUser = {value}（应为 0）");
    }

    public EnvCheckResult CheckRdpWrapper()
    {
        var installed = _sessionManager.IsRdpWrapperInstalled();
        return new("多会话解锁 (TermWrap/rdpwrap)", installed,
            installed ? "检测到 RDP 解锁层（TermWrap.dll 或 rdpwrap.dll）" : "未检测到 RDP 解锁层（家庭版需要）");
    }

    public EnvCheckResult CheckStartRcm()
    {
        var value = ReadDword(RdpTcpKey, RegistryKeys.StartRCM);
        var pass = value == 1;
        return new("StartRCM（家庭版修复）", pass, pass ? "StartRCM = 1" : $"StartRCM = {value}（应为 1）");
    }

    public EnvCheckResult CheckTermServiceRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("TermService");
            var running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            return new("TermService 服务", running, running ? "正在运行" : $"状态：{sc.Status}");
        }
        catch (Exception ex)
        {
            return new("TermService 服务", false, $"检查失败：{ex.Message}");
        }
    }

    public EnvCheckResult CheckFirewallLoopbackRule()
    {
        var found = FirewallRuleExists();
        return new("防火墙回环规则 (RDP 仅本机)", found,
            found ? "已存在 AkiSpace RDP Loopback 规则" : "缺少回环限制规则（建议添加，仅允许 127.0.0.1 访问 3389）");
    }

    public EnvCheckResult CheckChildSessions()
    {
        var enabled = _sessionManager.IsChildSessionsEnabled();
        var id = _sessionManager.TryGetChildSessionId();
        var detail = enabled
            ? (id.HasValue ? $"已启用，当前子会话 ID = {id.Value}" : "已启用，暂无活动子会话")
            : "未启用（需要 WTSEnableChildSessions）";
        return new("子会话 (Child Sessions)", enabled, detail);
    }

    public EnvCheckResult CheckRdpListener()
    {
        var active = _sessionManager.IsRdpListenerActive();
        return new("RDP 监听端口", active,
            active ? $"端口 {_sessionManager.GetConfiguredRdpPort()} 正在监听" : "监听失败（TermService 未运行或未解锁）");
    }

    public EnvCheckResult CheckTermsrvVersion()
    {
        var version = _sessionManager.GetTermsrvVersion();
        return new("termsrv.dll 版本", version != "unknown", version);
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
            results.Add(new("禁用 RDP Wrapper (TermWrap.dll)", disableWrapOk,
                disableWrapOk ? "TermService ServiceDll 已恢复为 %SystemRoot%\\System32\\termsrv.dll" : "无需禁用或失败"));
        }
        else
        {
            results.Add(new("保留 RDP Wrapper hook", true,
                "当前为标准 RDP 模式，TermWrap.dll 是多会话解锁层，跳过禁用"));
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
        results.Add(new("安全加固 (SecurityLayer/MinEncryptionLevel/NLA)", true, "SecurityLayer=2, MinEncryptionLevel=3, UserAuthentication=1"));

        // Firewall loopback rule
        var ruleOk = EnsureFirewallLoopbackRule();
        results.Add(new("防火墙回环规则", ruleOk, ruleOk ? "已添加 AkiSpace RDP Loopback 规则" : "添加失败（需要管理员权限）"));

        // Restart TermService so the new ServiceDll (termsrv.dll instead of TermWrap.dll)
        // is loaded and the child-session broker is reset.
        var restartOk = RestartTermService();
        results.Add(new("TermService 重启", restartOk, restartOk ? "已重启" : "重启失败"));

        // Enable child sessions (must be called after TermService is back up with
        // the un-hooked termsrv.dll).
        var childOk = _sessionManager.EnableChildSessions();
        results.Add(new("启用子会话", childOk, childOk ? "WTSEnableChildSessions(true) 成功" : "调用失败"));

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
                    "RDP Wrapper (TermWrap.dll) hook",
                    false,
                    $"⚠ TermService ServiceDll = {serviceDll}。RDP Wrapper 与 AkiSpace 子会话不兼容（BetterGI 官方文档明确警告）。请用「环境检查/修复 → 禁用 RDP Wrapper」解决。");
            }
            return new(
                "RDP Wrapper (TermWrap.dll) hook",
                true,
                serviceDll is null
                    ? "未检测到 ServiceDll"
                    : $"未检测到 RDP Wrapper hook（{serviceDll}）");
        }
        catch (Exception ex)
        {
            return new("RDP Wrapper (TermWrap.dll) hook", false, $"检查失败：{ex.Message}");
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

    private static bool FirewallRuleExists()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"AkiSpace RDP Loopback\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Contains("AkiSpace RDP Loopback", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogWarning(ex, "FirewallRuleExists failed");
            return false;
        }
    }

    private static bool EnsureFirewallLoopbackRule()
    {
        try
        {
            // Static helper can't reach the instance field; use the registry directly
            // (same logic as ChildSessionManager.GetConfiguredRdpPort).
            var port = 3389;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKeys.RdpTcp);
                var value = key?.GetValue(RegistryKeys.PortNumber) as int?;
                if (value is > 0 and <= 65535) port = value.Value;
            }
            catch { /* keep default 3389 */ }

            if (FirewallRuleExists()) return true;

            // Add the loopback allow rule (for 127.0.0.1)
            if (!RunNetsh(BuildFirewallAddRule("AkiSpace RDP Loopback", "allow", port, "127.0.0.1"), 15000))
                return false;

            // Also delete the default public allow (if any) and add a matching block rule
            // so the user's stated "loopback only" promise is actually enforced.
            RunNetsh("advfirewall firewall delete rule name=\"Remote Desktop - User Mode (TCP-In)\"", 10000);
            RunNetsh(BuildFirewallAddRule("AkiSpace RDP Block", "block", port, "any"), 15000);

            return FirewallRuleExists();
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
            p.WaitForExit(timeoutMs);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LogWarning(ex, $"netsh failed: {args}");
            return false;
        }
    }
}