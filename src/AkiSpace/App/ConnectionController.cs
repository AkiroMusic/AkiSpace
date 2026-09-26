using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using AkiSpace.Controls;
using AkiSpace.Input;
using AkiSpace.Ipc;
using AkiSpace.Native;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.App;

/// <summary>
/// Complete UI-facing connect state. The shell (WinForms or WPF) renders this
/// record verbatim; the controller never touches controls directly.
/// </summary>
public sealed record ConnectUiState(
    bool ConnectEnabled,
    bool DisconnectEnabled,
    bool TerminateEnabled,
    bool GameMouseVisible,
    bool GameMouseEnabled,
    string GameMouseText,
    bool LaunchEnabled,
    string ConnectionStatusText);

/// <summary>
/// Framework-agnostic owner of the connect orchestration, the RDP host lifecycle,
/// agent launch/nonce management and status polling. Extracted 1:1 from the
/// WinForms MainForm during the frontend rewrite; logic is unchanged — only the
/// control mutations became <see cref="ConnectUiState"/> emissions.
/// </summary>
public sealed class ConnectionController
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConnectionController> _logger;
    private readonly SettingsService _settingsService;
    private readonly ChildSessionManager _sessionManager;
    private readonly EnvironmentVerifier _environmentVerifier;
    private readonly ProcessLauncher _processLauncher;
    private readonly MouseForwarder _mouseForwarder;
    private readonly PipeServer _pipeServer;

    /// <summary>UI-thread deferral (WinForms BeginInvoke / WPF Dispatcher.BeginInvoke).</summary>
    private readonly Action<Action> _defer;
    /// <summary>Modal clone-password prompt for first Standard-RDP connect (shell-owned UI).</summary>
    private readonly Func<string?> _promptForClonePassword;
    /// <summary>Owner window for modal file dialogs (shell-owned UI).</summary>
    private readonly Func<System.Windows.Forms.IWin32Window?> _dialogOwner;

    // ---- shell bindings ----
    public event Action<ConnectUiState>? ConnectStateChanged;
    public event Action<string>? ChildSessionStatusChanged;
    public event Action<string>? WrapperStatusChanged;
    public event Action<string>? PerformanceStatusChanged;
    public event Action<bool>? TrayConnectedChanged;
    /// <summary>Raised with the new host when one is created and with null when torn down.</summary>
    public event Action<RdpActiveXHost?>? RdpHostChanged;
    public event Action? FullScreenRequested;
    public event Action? LeaveFullScreenRequested;

    private RdpActiveXHost? _rdpHost;
    private bool _closing;
    private bool _isConnected;

    // True between a ConnectAsync call and its terminal state (login complete /
    // connection failed / early return). Gates re-entrant connects and hotkey toggles.
    private bool _connecting;

    // Collapse the ActiveX retry loop's repeated failure events (one per attempt,
    // ~1-2s apart) into a single dialog; distinct or later failures still notify.
    private string? _lastFailureReason;
    private long _lastFailureDialogAt;

    // RDP window handles snapshotted ON the UI thread (RefreshRdpHandles) so the
    // mouse-forwarder's 200 Hz poller can do pure-Win32 bounds/focus checks without
    // touching Control.Handle (a cross-thread handle access that only survives
    // because WinForms' check is debugger-only).
    private IntPtr _rdpHostHandle;
    private IntPtr _rdpInputWindowHandle;

    // Button-state mirror so every mutation can emit a complete ConnectUiState
    // (the game-mouse button's visibility is only assigned in ConnectAsync — the
    // other paths leave it untouched — so it must be tracked here).
    private bool _connectEnabled = true;
    private bool _disconnectEnabled;
    private bool _terminateEnabled;
    private bool _gameMouseVisible;
    private bool _gameMouseEnabled;
    private string _gameMouseText = "游戏鼠标";
    private bool _launchEnabled;
    private string _statusText = "连接: 未连接";

    private string _cloneUsername => _settingsService.Current.CloneUsername;
    private string _clonePassword => _settingsService.Current.ClonePassword;

    public ConnectionController(
        ILoggerFactory loggerFactory,
        SettingsService settingsService,
        ChildSessionManager sessionManager,
        EnvironmentVerifier environmentVerifier,
        ProcessLauncher processLauncher,
        PipeServer pipeServer,
        MouseForwarder mouseForwarder,
        Action<Action> defer,
        Func<string?> promptForClonePassword,
        Func<System.Windows.Forms.IWin32Window?> dialogOwner)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ConnectionController>();
        _settingsService = settingsService;
        _sessionManager = sessionManager;
        _environmentVerifier = environmentVerifier;
        _processLauncher = processLauncher;
        _pipeServer = pipeServer;
        _mouseForwarder = mouseForwarder;
        _defer = defer;
        _promptForClonePassword = promptForClonePassword;
        _dialogOwner = dialogOwner;
    }

    /// <summary>Mark the app as closing: stops status mutations and re-entrancy.</summary>
    public void MarkClosing() => _closing = true;

    // ---------------------------------------------------------------- Lifecycle

    /// <summary>
    /// Startup sequence that used to live in MainForm.OnLoad (after the shell has
    /// created its timer and applied window styling): preset the pipe DACL for the
    /// clone account BEFORE game-mouse restore, initialize the forwarder, sweep
    /// stale nonces, and run the first status refresh.
    /// </summary>
    public void OnShellLoaded()
    {
        // Resolve the clone-account pipe DACL grant BEFORE restoring game-mouse mode:
        // SetGameMouseModeEnabled(true) starts the pipe listener, and CreateServerStream
        // bakes the pipe DACL at stream-creation time. The clone-agent launch
        // (OnRdpLoginComplete → LaunchAgentInChildSession) happens LATER, so without this
        // pre-set, a persisted GameMouseModeEnabled=true would start the very first
        // listening stream with a primary+SYSTEM-only DACL and the cross-user clone agent
        // would be denied connect at the pipe level. (The nonce needs no such ordering —
        // VerifyHandshakeAsync reads _expectedNonce per connection, not at stream create.)
        _pipeServer.SetAllowedClientSid(TryResolveCloneSid());

        _mouseForwarder.Initialize(GetRdpViewerBounds, IsRdpInputFocused);
        _mouseForwarder.SetGameMouseModeEnabled(_settingsService.Current.GameMouseModeEnabled);

        SweepStaleNonceFiles();
        RefreshStatus();
    }

    /// <summary>
    /// Shutdown sequence that used to live in MainForm.OnFormClosing: disable game
    /// mouse, tear down the RDP host, and — only for explicit user closes, never the
    /// OS-shutdown path — optionally log off the child session.
    /// </summary>
    public void OnAppClosing(bool userInitiated)
    {
        _closing = true;
        _mouseForwarder.SetGameMouseModeEnabled(false);
        TearDownRdpHost();

        // A synchronous WTS logoff must not run on the OS-shutdown or task-manager
        // kill path — only log off when the user explicitly closed/exited the app.
        if (userInitiated
            && _settingsService.Current.LogoffOnExit
            && _sessionManager.TryGetChildSessionId() is uint sid)
        {
            _sessionManager.LogoffChildSession(sid);
        }
        _logger.LogInformation("Connection controller shut down");
    }

    // ---------------------------------------------------------------- Connect State Machine

    public void ToggleConnect()
    {
        if (_connecting) return;
        if (_isConnected)
        {
            Disconnect();
        }
        else
        {
            _defer(() =>
            {
                try { _ = ConnectAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Connect from hotkey/tray failed"); }
            });
        }
    }

    public async Task ConnectAsync()
    {
        if (_connecting || _isConnected) return;
        try
        {
            var settings = _settingsService.Current;

            // Standard-RDP mode needs the clone account's password. There is no
            // built-in default anymore; prompt on first use and persist it DPAPI-
            // protected through the settings service.
            if (settings.ConnectionMode != ConnectionMode.ChildSession
                && string.IsNullOrEmpty(settings.ClonePassword))
            {
                var password = _promptForClonePassword();
                if (string.IsNullOrEmpty(password))
                {
                    SetStatusText("连接: 未提供密码");
                    return;
                }
                _settingsService.Update(s => s.ClonePassword = password);
                settings = _settingsService.Current;
            }

            _connecting = true;
            SetStatusText("连接: 正在准备...");

            if (settings.ConnectionMode != ConnectionMode.ChildSession
                && !await _sessionManager.IsRdpListenerActiveAsync())
            {
                System.Windows.Forms.MessageBox.Show(
                    "RDP 监听器暂时未响应连接探测。可能原因：\n" +
                    "1) TermService 正在重启（禁用 RDP Wrapper 后需要 10–20 秒）— 请稍等再试\n" +
                    "2) 真未监听 — 运行「环境检查/修复」→「一键修复」\n" +
                    "3) 防火墙/杀软拦截 127.0.0.1:3389",
                    "AkiSpace", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                ResetConnectUi("连接: RDP 未就绪");
                return;
            }

            TearDownRdpHost();
            _rdpHost = new RdpActiveXHost(_loggerFactory.CreateLogger<RdpActiveXHost>());
            _rdpHost.LoginCompleted += OnRdpLoginComplete;
            _rdpHost.ConnectionFailed += OnRdpConnectionFailed;
            _rdpHost.RequestedGoFullScreen += OnRdpRequestFullScreen;
            _rdpHost.RequestedLeaveFullScreen += OnRdpRequestLeaveFullScreen;
            RdpHostChanged?.Invoke(_rdpHost);

            _rdpHost.CreateControl();

            var port = settings.RdpPort is > 0 and not 3389
                ? settings.RdpPort
                : _sessionManager.GetConfiguredRdpPort();

            SetStatusText($"连接: 正在连接 127.0.0.1:{port} ...");

            _defer(() => _defer(() =>
            {
                try
                {
                    if (_rdpHost is null || _rdpHost.IsDisposed) return;

                    if (settings.ConnectionMode == ConnectionMode.ChildSession)
                    {
                        var hookCheck = _environmentVerifier.CheckRdpWrapperHook();
                        if (!hookCheck.Pass)
                        {
                            _logger.LogWarning("RDP Wrapper hook detected; child session will fail: {Detail}", hookCheck.Detail);
                            System.Windows.Forms.MessageBox.Show(
                                "检测到 RDP Wrapper (TermWrap.dll) 已 hook TermService。\n\n"
                                + "BetterGI 官方文档明确说明：RDP Wrapper 与桌面分身（子会话）功能不兼容，"
                                + "两者不能同时使用。RDP Wrapper 的 hook 会导致子会话 broker 无法创建会话，"
                                + "表现为「远程桌面无法连接到远程计算机 (516)」。\n\n"
                                + "请按以下步骤解决：\n"
                                + "1. 在 AkiSpace 中打开「环境检查/修复」\n"
                                + "2. 点击「一键修复」（最新版会禁用 RDP Wrapper 并恢复 termsrv.dll）\n"
                                + "3. 等待 TermService 重启完成\n"
                                + "4. 重新尝试子会话连接\n\n"
                                + "RDP Wrapper 和 AkiSpace 子会话功能重复（二者都是为多用户场景设计），"
                                + "只能二选一。建议直接使用 AkiSpace 子会话。",
                                "AkiSpace — RDP Wrapper 与子会话冲突",
                                System.Windows.Forms.MessageBoxButtons.OK,
                                System.Windows.Forms.MessageBoxIcon.Warning);
                            ResetConnectUi("连接: RDP Wrapper 冲突");
                            return;
                        }

                        var enableResult = _sessionManager.EnableChildSessions();
                        _logger.LogInformation("WTSEnableChildSessions(true) before connect returned {Result}", enableResult);

                        _rdpHost.ConnectToChildSession(
                            settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                            port, settings.SmartSizing,
                            settings.SendSystemShortcutsToRemote, settings.AudioRedirected);
                    }
                    else
                    {
                        _rdpHost.Connect(
                            settings.DesktopWidth, settings.DesktopHeight, settings.ColorDepth,
                            port, settings.SmartSizing,
                            settings.SendSystemShortcutsToRemote, settings.AudioRedirected,
                            userName: _cloneUsername, password: _clonePassword,
                            useChildSession: false);
                    }
                }
                catch (Exception ex)
                {
                    // A throw here used to escape into Application.ThreadException and
                    // leave the UI in a half-connected state; route it through the
                    // normal reset instead.
                    _logger.LogError(ex, "Deferred connect body failed");
                    ResetConnectUi("连接: 连接失败");
                }
            }));

            _connectEnabled = false;
            _disconnectEnabled = true;
            _terminateEnabled = true;
            var enableGameMouse = settings.ConnectionMode != ConnectionMode.ChildSession;
            _gameMouseEnabled = enableGameMouse;
            _gameMouseVisible = enableGameMouse;
            _launchEnabled = true;
            ApplyState();
            // Deliberately NO optimistic _isConnected=true here: the connect actually
            // happens in the deferred BeginInvoke above (or its early-return paths).
            // The real state lands via OnRdpLoginComplete and the RefreshStatus poll.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connect failed");
            ResetConnectUi("连接: 连接失败");
            System.Windows.Forms.MessageBox.Show($"连接失败：{ex.Message}", "AkiSpace",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Restores the buttons/status after a connect attempt ends without a session
    /// (early return, connect exception, or reported connection failure).
    /// </summary>
    private void ResetConnectUi(string statusText)
    {
        _connecting = false;
        _isConnected = false;
        _connectEnabled = true;
        _disconnectEnabled = false;
        _terminateEnabled = false;
        _gameMouseEnabled = false;
        _gameMouseText = "游戏鼠标";
        _launchEnabled = false;
        SetStatusText(statusText);
        TrayConnectedChanged?.Invoke(false);
    }

    /// <summary>
    /// Removes and disposes the current RDP ActiveX host. Controls.Clear() does NOT
    /// dispose children, so every reconnect previously leaked the AxHost, the MSTSC
    /// COM object behind it and its event sink until process exit.
    /// </summary>
    private void TearDownRdpHost()
    {
        _rdpHostHandle = IntPtr.Zero;
        _rdpInputWindowHandle = IntPtr.Zero;
        if (_rdpHost is null) return;
        var host = _rdpHost;
        _rdpHost = null;
        host.LoginCompleted -= OnRdpLoginComplete;
        host.ConnectionFailed -= OnRdpConnectionFailed;
        host.RequestedGoFullScreen -= OnRdpRequestFullScreen;
        host.RequestedLeaveFullScreen -= OnRdpRequestLeaveFullScreen;
        RdpHostChanged?.Invoke(null);
        try
        {
            host.DisconnectSession();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RDP host disconnect during teardown failed (continuing to dispose)");
        }
        host.Dispose();
    }

    public void Disconnect()
    {
        _rdpHost?.DisconnectSession();
        _connectEnabled = true;
        _disconnectEnabled = false;
        _terminateEnabled = false;
        _gameMouseEnabled = false;
        _gameMouseText = "游戏鼠标";
        _launchEnabled = false;
        _mouseForwarder.SetGameMouseModeEnabled(false);
        SetStatusText("连接: 已断开");
        _connecting = false;
        _isConnected = false;
        TrayConnectedChanged?.Invoke(false);
    }

    public void TerminateChildSession()
    {
        var sid = _sessionManager.TryGetChildSessionId();
        if (sid is null)
        {
            System.Windows.Forms.MessageBox.Show("当前没有活动的子会话。", "AkiSpace",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }
        var confirm = System.Windows.Forms.MessageBox.Show(
            $"确定要终止子会话（ID {sid.Value}）吗？其中的程序将全部关闭。",
            "AkiSpace", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question);
        if (confirm != System.Windows.Forms.DialogResult.Yes) return;

        if (_sessionManager.LogoffChildSession(sid.Value))
        {
            TearDownRdpHost();
            _connectEnabled = true;
            _disconnectEnabled = false;
            _terminateEnabled = false;
            _gameMouseEnabled = false;
            _gameMouseText = "游戏鼠标";
            _launchEnabled = false;
            _mouseForwarder.SetGameMouseModeEnabled(false);
            SetStatusText("连接: 子会话已终止");
            _connecting = false;
            _isConnected = false;
            TrayConnectedChanged?.Invoke(false);
        }
        else
        {
            SetStatusText("连接: 终止失败");
        }
    }

    public void ToggleGameMouse()
    {
        var enabled = _mouseForwarder.IsGameMouseModeEnabled;
        if (!enabled && !_mouseForwarder.IsAgentConnected)
        {
            System.Windows.Forms.MessageBox.Show(
                "回放 Agent 未连接。游戏鼠标模式需要分身侧运行回放 Agent（--agent 模式）。\n" +
                "请先在分身会话中启动 Agent，或在 Agent 落地前使用标准 RDP 鼠标。",
                "AkiSpace — 游戏鼠标", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            return;
        }
        _mouseForwarder.SetGameMouseModeEnabled(!enabled);
        _gameMouseText = enabled ? "游戏鼠标" : "游戏鼠标: 开";
        _settingsService.Update(s => s.GameMouseModeEnabled = !enabled);
        ApplyState();
    }

    /// <summary>Status text set by the shell after dialogs close (e.g. 设置已保存).</summary>
    public void SetStatusText(string text)
    {
        _statusText = text;
        ApplyState();
    }

    private void ApplyState()
    {
        ConnectStateChanged?.Invoke(new ConnectUiState(
            _connectEnabled, _disconnectEnabled, _terminateEnabled,
            _gameMouseVisible, _gameMouseEnabled, _gameMouseText,
            _launchEnabled, _statusText));
    }

    // ---------------------------------------------------------------- Agent Launch

    private void LaunchAgentInChildSession()
    {
        try
        {
            // Server-scoped only: the pipe server holds the expected nonce for the
            // handshake; keeping a second copy of the secret in this field for the
            // process lifetime serves nothing.
            var nonce = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

            _pipeServer.SetNonce(nonce);

            // Standard-RDP mode: the agent runs under a DIFFERENT account, so the pipe DACL
            // must also admit the clone account SID (else its connect is denied before the
            // handshake). Null for child-session mode (same user) → server keeps the tight
            // primary+SYSTEM-only DACL.
            _pipeServer.SetAllowedClientSid(TryResolveCloneSid());

            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is null)
            {
                _logger.LogWarning("Cannot determine exe path for agent launch");
                return;
            }

            var sid = _sessionManager.TryGetChildSessionId();
            if (sid is null)
            {
                _logger.LogWarning("No child session id — agent not launched");
                return;
            }

            // Deliver the nonce out-of-band via a DACL-protected file instead of the command
            // line — argv leaks via the Task Scheduler task XML under %WINDIR%\System32\Tasks
            // and the child process PEB.
            var nonceFilePath = WriteNonceToProtectedTempFile(nonce);
            if (nonceFilePath is null)
            {
                _logger.LogWarning("Failed to write nonce file; agent not launched");
                return;
            }

            var args = $"--agent --nonce-file \"{nonceFilePath}\"";
            if (_processLauncher.LaunchInChildSession(exePath, sid.Value, args))
            {
                _logger.LogInformation("Agent launched in child session {Sid} (nonce delivered via file)", sid.Value);
                SetStatusText("连接: 已连接 — Agent 启动中");
            }
            else
            {
                _logger.LogWarning("Agent launch failed in child session {Sid}", sid.Value);
                // Launch failed — the agent will never consume the nonce, so delete it here
                // to avoid orphaning a (DACL-restricted) nonce file on disk.
                try { File.Delete(nonceFilePath); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch agent in child session");
        }
    }

    /// <summary>
    /// Writes the handshake nonce to a uniquely-named file whose DACL grants Read to the
    /// current identity + SYSTEM + (for standard-RDP mode) the clone account, so the
    /// separately-launched agent — which may run under a DIFFERENT user — can read it, while
    /// unrelated local users cannot. The nonce is NEVER placed on the command line (argv leaks
    /// into the Task Scheduler task XML and the PEB). The agent reads+deletes it via
    /// <see cref="PipeClient.ReadNonceFromFile"/>.
    ///
    /// The file lives under %PROGRAMDATA%\AkiSpace because that tree grants the Users group
    /// traverse+read by inheritance, letting the clone account (a Users member) reach the
    /// file; %TEMP% would be per-user and unreadable cross-account.
    /// </summary>
    private string? WriteNonceToProtectedTempFile(byte[] nonce)
    {
        var path = Path.Combine(NonceDirectory(), $"nonce_{Guid.NewGuid():N}.bin");
        try
        {
            // Build the restricted DACL FIRST and create the file WITH it in one atomic
            // step (FileStream's FileSecurity overload → CreateFile with SECURITY_ATTRIBUTES).
            // This avoids the write-then-tighten TOCTOU window: the file never exists with
            // content under %PROGRAMDATA%'s inherited Users:Read ACE. Owner gets Write so it
            // can write through the handle, Delete so the launch-failure path can remove it.
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var identity = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!, // non-null for an interactive logon token
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.Read, InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
            // Clone account (standard-RDP / cross-user mode) — Read so the agent running as
            // AkiSpaceUser can consume the nonce, + Delete so it can remove the file after
            // reading (the owner's delete right does not transfer to a different identity).
            // No-op for same-user child-session mode.
            foreach (var sid in ResolveNonceFileSids())
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid, FileSystemRights.Read | FileSystemRights.Delete, InheritanceFlags.None,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            // .NET 8 dropped the FileStream(FileSecurity) ctor, so we close the TOCTOU window
            // like this: open with FileShare.None (an exclusive handle — no other process can
            // open the file at all while we hold it), tighten the DACL via
            // FileStream.SetAccessControl ON THE ALREADY-OPEN HANDLE (FileInfo.SetAccessControl
            // would try a second path-open and hit a sharing violation against our exclusive
            // handle), and only THEN write the nonce bytes. The handle's WriteData access is
            // fixed at open time and survives the ACL change; the owner has implicit WRITE_DAC
            // to set it. Net effect: bytes that exist on disk are only ever present under the
            // restricted DACL, and during the pre-tighten window the file is empty AND locked.
            using (var fs = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
            {
                fs.SetAccessControl(security);
                fs.Write(nonce, 0, nonce.Length);
                fs.Flush(flushToDisk: true);
            }
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to secure nonce file {Path}", path);
            try { File.Delete(path); } catch { /* best-effort */ }
            return null;
        }
    }

    /// <summary>%PROGRAMDATA%\AkiSpace, created if absent. Traversable+readable by the Users group by inheritance.</summary>
    private static string NonceDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AkiSpace");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Deletes leftover nonce files from previous runs at startup. Normally the agent
    /// consumes (read+delete) the file, but a launch that never got that far (crashed
    /// or silently-skipped RunEx) would otherwise orphan a DACL-protected secret on
    /// disk forever. At shell load no handshake can be in flight, so everything
    /// matching the pattern is stale.
    /// </summary>
    private void SweepStaleNonceFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(NonceDirectory(), "nonce_*.bin"))
            {
                try
                {
                    File.Delete(file);
                    _logger.LogInformation("Deleted stale nonce file {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not delete stale nonce file {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nonce directory sweep failed");
        }
    }

    /// <summary>
    /// Extra SIDs the nonce file must be readable by — currently the configured clone account
    /// (standard-RDP mode). Returns empty for child-session mode (same user) or when the
    /// account cannot be translated to a SID.
    /// </summary>
    private IEnumerable<SecurityIdentifier> ResolveNonceFileSids()
    {
        var sid = TryResolveCloneSid();
        if (sid is not null) yield return sid;
    }

    /// <summary>Resolves the configured clone account to a SID, or null (same-user/unresolvable).</summary>
    private SecurityIdentifier? TryResolveCloneSid()
    {
        try
        {
            if (_settingsService.Current.ConnectionMode == ConnectionMode.ChildSession)
                return null; // agent runs as the current user — no extra grant needed

            var user = _settingsService.Current.CloneUsername;
            if (string.IsNullOrWhiteSpace(user)) return null;

            // Try the qualified form first (MACHINE\user for plain names, or the
            // DOMAIN\user / UPN exactly as given), then fall back to an unqualified
            // lookup which succeeds for domain-joined / MSA identities the local
            // SAM cannot resolve. Fail-closed (null + warning) if neither works.
            var candidates = user.Contains('\\') || user.Contains('@')
                ? new[] { new NTAccount(user) }
                : new[] { new NTAccount(Environment.MachineName, user), new NTAccount(user) };

            foreach (var account in candidates)
            {
                try
                {
                    if (account.Translate(typeof(SecurityIdentifier)) is SecurityIdentifier sid)
                        return sid;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SID lookup failed for account form '{Account}'", account.Value);
                }
            }
            // Every candidate form failed to translate — surface it at Warning level so the
            // fail-closed degradation is never silent (per-candidate Debug logs alone are not
            // enough to diagnose a broken agent connection later).
            _logger.LogWarning("Could not resolve clone account '{User}' to a SID; agent may fail to read the nonce file in standard-RDP mode", _cloneUsername);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve clone account '{User}' to a SID; agent may fail to read the nonce file in standard-RDP mode", _cloneUsername);
            return null;
        }
    }

    public void LaunchProgramInChildSession()
    {
        var sid = _sessionManager.TryGetChildSessionId();
        if (sid is null)
        {
            System.Windows.Forms.MessageBox.Show("请先连接分身。", "AkiSpace",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }
        using var ofd = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择要在分身会话中启动的程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (ofd.ShowDialog(_dialogOwner()) != System.Windows.Forms.DialogResult.OK) return;

        if (_processLauncher.LaunchInChildSession(ofd.FileName, sid.Value))
        {
            SetStatusText($"连接: 已在分身（会话 {sid.Value}）中启动 {Path.GetFileName(ofd.FileName)}");
        }
        else
        {
            SetStatusText("连接: 启动失败（详见日志）");
            System.Windows.Forms.MessageBox.Show("启动失败。可能是权限不足或 Task Scheduler 服务不可用。", "AkiSpace",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    // ---------------------------------------------------------------- RDP Events

    private void OnRdpLoginComplete()
    {
        if (_closing) return;
        _logger.LogInformation("RDP login complete event");
        _statusText = "连接: 已连接 — 分身桌面就绪";
        _gameMouseEnabled = true;
        _connecting = false;
        _isConnected = true;
        ApplyState();
        TrayConnectedChanged?.Invoke(true);

        _defer(() => _rdpHost?.TryFocusRdpInputWindow());

        // Agent launch resolves the clone-account SID (potentially a network lookup)
        // and performs several Task Scheduler COM calls — keep both off the UI thread.
        if (_settingsService.Current.ConnectionMode != ConnectionMode.ChildSession)
        {
            _ = Task.Run(() =>
            {
                try { LaunchAgentInChildSession(); }
                catch (Exception ex) { _logger.LogError(ex, "Agent launch failed"); }
            });
        }

        var launchPath = _settingsService.Current.LaunchProgramPath;
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            var sid = _sessionManager.TryGetChildSessionId();
            if (sid.HasValue)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (_processLauncher.LaunchInChildSession(launchPath, sid.Value))
                            _logger.LogInformation("Auto-launched {Exe} in child session {Sid}", launchPath, sid.Value);
                        else
                            _logger.LogWarning("Auto-launch failed for {Exe} in child session {Sid}", launchPath, sid.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-launch threw for {Exe}", launchPath);
                    }
                });
            }
            else
            {
                _logger.LogInformation("Auto-launch skipped: no child session id available");
            }
        }
    }

    private void OnRdpConnectionFailed(string reason)
    {
        if (_closing) return;
        _logger.LogWarning("RDP connection failed: {Reason}", reason);
        ResetConnectUi("连接: 连接失败");

        // The ActiveX connect helper fires ConnectionFailed once per retry attempt
        // (typically 3, a few seconds apart). Collapse them into one dialog so the
        // user doesn't dismiss a stack of identical message boxes.
        var now = Environment.TickCount64;
        if (reason == _lastFailureReason && now - _lastFailureDialogAt < 15000) return;
        _lastFailureReason = reason;
        _lastFailureDialogAt = now;
        System.Windows.Forms.MessageBox.Show(reason, "AkiSpace 连接",
            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
    }

    private void OnRdpRequestFullScreen()
    {
        if (_closing) return;
        _logger.LogInformation("Remote requested fullscreen");
        FullScreenRequested?.Invoke();
    }

    private void OnRdpRequestLeaveFullScreen()
    {
        if (_closing) return;
        _logger.LogInformation("Remote requested leave fullscreen");
        LeaveFullScreenRequested?.Invoke();
    }

    // ---------------------------------------------------------------- Status Refresh

    /// <summary>Called once per second by the shell's UI-thread timer.</summary>
    public void RefreshStatus()
    {
        if (_closing) return;

        try
        {
            var sid = _sessionManager.TryGetChildSessionId();
            ChildSessionStatusChanged?.Invoke(sid.HasValue
                ? $"子会话: 活动 (ID {sid.Value})"
                : "子会话: 无");

            WrapperStatusChanged?.Invoke(_sessionManager.IsRdpWrapperInstalled()
                ? "RDP 解锁: 已安装"
                : "RDP 解锁: 未安装");

            if (_rdpHost != null)
            {
                RefreshRdpHandles();
                var connected = _rdpHost.GetConnectedState();
                _statusText = connected ? "连接: 已连接" : "连接: 未连接";
                _isConnected = connected;
                ApplyState();
                TrayConnectedChanged?.Invoke(connected);
            }

            if (_settingsService.Current.ShowPerformance)
            {
                // GetCurrentProcess() allocates an OS handle per call; without dispose
                // the 1 Hz poll leaks handles until GC finalizers run. `using` releases
                // it deterministically each tick.
                using var process = Process.GetCurrentProcess();
                var elapsed = DateTime.Now - process.StartTime;
                var cpuUsage = elapsed.TotalMilliseconds > 0
                    ? process.TotalProcessorTime.TotalMilliseconds / elapsed.TotalMilliseconds * 100
                    : 0;
                var memoryMB = process.WorkingSet64 / 1024 / 1024;
                PerformanceStatusChanged?.Invoke($"CPU: {cpuUsage:F1}% | 内存: {memoryMB} MB");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshStatus error");
        }
    }

    // ---------------------------------------------------------------- Forwarder Callbacks

    /// <summary>
    /// Refreshes the forwarder-facing window handles. MUST run on the UI thread
    /// (RefreshStatus calls it once per second): reading Control.Handle and discovering
    /// the RDP input capture window are only safe here. The poller thread consumes the
    /// published values with a Win32 liveness check, so a ≤1 s stale handle after a
    /// teardown degrades to a missed frame, never a crash.
    /// </summary>
    private void RefreshRdpHandles()
    {
        if (_rdpHost is null || _rdpHost.IsDisposed)
        {
            _rdpHostHandle = IntPtr.Zero;
            _rdpInputWindowHandle = IntPtr.Zero;
            return;
        }
        _rdpHostHandle = _rdpHost.Handle;
        _rdpInputWindowHandle = _rdpHost.GetInputCaptureWindowHandle();
    }

    private User32.RECT GetRdpViewerBounds()
    {
        var hwnd = Volatile.Read(ref _rdpHostHandle);
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd))
            return default;
        User32.GetWindowRect(hwnd, out var rect);
        return rect;
    }

    private bool IsRdpInputFocused()
    {
        var hwnd = Volatile.Read(ref _rdpInputWindowHandle);
        return RdpActiveXHost.IsWindowFocused(hwnd);
    }
}
