using System.ComponentModel;
using System.Globalization;
using System.Windows.Markup;
using AkiSpace.Services;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Binding = System.Windows.Data.Binding;
using BindingExpression = System.Windows.Data.BindingExpression;
using BindingMode = System.Windows.Data.BindingMode;

namespace AkiSpace.Common;

/// <summary>
/// Bilingual string catalog (English default, 中文 optional). One static table per
/// language is the single source of truth: XAML consumes it through
/// <c>{loc:Loc Key}</c> (a one-way binding that live-updates on language switch),
/// code through <see cref="T"/>/<see cref="F"/>. Keys are PascalCase with
/// underscores only — the indexer-binding path syntax in <see cref="LocExtension"/>
/// relies on the key being a single bracketed token.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Chinese = "zh";

    private static readonly object Gate = new();
    private static SettingsService? _settingsService;
    private static string _language = English;

    public static string Language
    {
        get { lock (Gate) return _language; }
    }

    /// <summary>Raised after the language changed (on the switching thread — UI).</summary>
    public static event Action? LanguageChanged;

    /// <summary>Resolves the persisted language once, before any string is shown.</summary>
    public static void Initialize(SettingsService settings)
    {
        _settingsService = settings;
        SetLanguage(settings.Current.Language, persist: false);
    }

    /// <summary>Switches the language. Unknown values fall back to English (the default).</summary>
    public static void SetLanguage(string language, bool persist = true)
    {
        var normalized = language == Chinese ? Chinese : English;
        lock (Gate)
        {
            if (normalized == _language && !persist) return;
            _language = normalized;
        }
        if (persist)
        {
            _settingsService?.Update(s => s.Language = normalized);
        }
        LocCatalog.Instance.OnLanguageChanged();
        LanguageChanged?.Invoke();
    }

    /// <summary>Looks up a key in the active language, then English, then echoes the key.</summary>
    public static string T(string key)
    {
        var lang = Language;
        if (lang == Chinese && Zh.TryGetValue(key, out var zh))
            return zh;
        return En.TryGetValue(key, out var en) ? en : key;
    }

    /// <summary>Formats a localized template (placeholders use explicit {0}/{1} indexes so
    /// translations can reorder arguments). Invariant culture — arguments are ports,
    /// session ids and file names, never user-locale quantities.</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    // ---------------------------------------------------------------- catalog

    private static readonly Dictionary<string, string> En = new()
    {
        // ---- app-wide ----
        ["App_AlreadyRunning"] = "AkiSpace is already running.",
        ["App_Fatal"] = "AkiSpace hit a fatal error:\n{0}\n\nSee the log file for details.",

        // ---- main window ----
        ["Main_Title"] = "AkiSpace — Desktop Clone",
        ["Main_Connect"] = "Connect",
        ["Main_Disconnect"] = "Disconnect",
        ["Main_Terminate"] = "Terminate",
        ["Main_GameMouse"] = "Game Mouse",
        ["Main_GameMouseOn"] = "Game Mouse: ON",
        ["Main_Launch"] = "Launch",
        ["Main_Capture"] = "Capture",
        ["Main_CaptureTip"] = "Capture the clone screen to the clipboard (Ctrl+Shift+S)",
        ["Main_EnvCheck"] = "Env Check",
        ["Main_Settings"] = "Settings",
        ["Main_ChildChecking"] = "Clone session: checking…",
        ["Main_ConnInitial"] = "Connection: not connected",
        ["Main_WrapChecking"] = "RDP unlock: checking…",
        ["Main_PerfInitial"] = "CPU: 0% | Mem: 0 MB",
        ["Main_EmptyTitle"] = "Clone desktop not connected",
        ["Main_EmptyHint"] = "Click Connect in the toolbar to start a clone session; once connected, Ctrl+Shift+S captures the screen",
        ["Main_Ready"] = "Ready",

        // ---- clone screenshot ----
        ["Shot_StatusOk"] = "Capture: clone screen → clipboard",
        ["Shot_StatusFail"] = "Capture: clone not connected or copy failed",
        ["Shot_ToastOk"] = "Clone screen copied to clipboard",
        ["Shot_ToastFail"] = "Capture failed — clone not connected",

        // ---- RDP ActiveX overlay texts ----
        ["Rdp_Connecting"] = "Creating the AkiSpace desktop clone…",
        ["Rdp_Disconnected"] = "The AkiSpace desktop clone was disconnected",

        // ---- connection status line (ConnectionController) ----
        ["Conn_NotConnected"] = "Connection: not connected",
        ["Conn_Disconnected"] = "Connection: disconnected",
        ["Conn_NoPassword"] = "Connection: no password provided",
        ["Conn_Preparing"] = "Connection: preparing…",
        ["Conn_Connecting"] = "Connection: connecting to {0} …",
        ["Conn_RdpNotReady"] = "Connection: RDP not ready",
        ["Conn_WrapperConflict"] = "Connection: RDP Wrapper conflict",
        ["Conn_Failed"] = "Connection: failed",
        ["Conn_Terminated"] = "Connection: clone session terminated",
        ["Conn_TerminateFailed"] = "Connection: terminate failed",
        ["Conn_ConnectedAgent"] = "Connection: connected — agent starting",
        ["Conn_Ready"] = "Connection: connected — clone desktop ready",
        ["Conn_Connected"] = "Connection: connected",
        ["Conn_Launching"] = "Connection: launching {0} …",
        ["Conn_Launched"] = "Connection: launched {0} in the clone (session {1})",
        ["Conn_LaunchFailed"] = "Connection: launch failed (see log)",
        ["Conn_SettingsSaved"] = "Connection: settings saved",

        // ---- status rows ----
        ["Child_Active"] = "Clone session: active (ID {0})",
        ["Child_None"] = "Clone session: none",
        ["Wrap_Installed"] = "RDP unlock: installed",
        ["Wrap_NotInstalled"] = "RDP unlock: not installed",
        ["Perf_Status"] = "CPU: {0}% | Mem: {1}",
        ["Perf_NA"] = "unavailable",

        // ---- message boxes (connection controller) ----
        ["Box_ListenerDown"] =
            "The RDP listener is not answering the connection probe yet. Possible causes:\n" +
            "1) TermService is restarting (takes 10–20 s after disabling RDP Wrapper) — wait a moment and retry\n" +
            "2) Really not listening — run Env Check → One-Click Fix\n" +
            "3) Firewall/antivirus blocking 127.0.0.1:3389",
        ["Box_WrapperConflictTitle"] = "AkiSpace — RDP Wrapper conflicts with child sessions",
        ["Box_WrapperConflict"] =
            "RDP Wrapper (TermWrap.dll) has hooked TermService.\n\n" +
            "BetterGI's official docs state clearly that RDP Wrapper is incompatible with desktop clones " +
            "(child sessions) — they cannot be used together. The Wrapper hook prevents the child-session " +
            "broker from creating a session, surfacing as \"Remote Desktop cannot connect to the remote computer (516)\".\n\n" +
            "To resolve:\n" +
            "1. Open Env Check in AkiSpace\n" +
            "2. Click One-Click Fix (it disables RDP Wrapper and restores termsrv.dll)\n" +
            "3. Wait for TermService to finish restarting\n" +
            "4. Retry the child-session connect\n\n" +
            "RDP Wrapper and AkiSpace child sessions overlap in purpose (both target multi-user scenarios) — " +
            "pick one. Using AkiSpace child sessions is recommended.",
        ["Box_ConnectFailTitle"] = "AkiSpace Connect",
        ["Box_ConnectFailed"] = "Connect failed: {0}",
        ["Box_NoChildSession"] = "There is no active clone session.",
        ["Box_TerminateConfirm"] = "Terminate clone session (ID {0})? All programs in it will be closed.",
        ["Box_GameMouseTitle"] = "AkiSpace — Game Mouse",
        ["Box_GameMouseAgent"] =
            "The replay agent is not connected. Game Mouse mode requires the replay agent (--agent mode) " +
            "running inside the clone session.\nStart the agent in the clone session first, or use the " +
            "standard RDP mouse until it is up.",
        ["Box_ConnectFirst"] = "Connect to the clone first.",
        ["Box_LaunchFail"] = "Launch failed. This can be caused by insufficient permissions or an unavailable Task Scheduler service.",

        // ---- tray ----
        ["Tray_ToggleConnect"] = "Connect clone",
        ["Tray_ToggleDisconnect"] = "Disconnect clone",
        ["Tray_Screenshot"] = "Capture clone screen",
        ["Tray_Show"] = "Show main window",
        ["Tray_Exit"] = "Exit",
        ["Tray_TipConnected"] = "AkiSpace - Connected",
        ["Tray_TipDisconnected"] = "AkiSpace - Not connected",

        // ---- settings window ----
        ["Set_Title"] = "Settings",
        ["Set_ConnCard"] = "Connection",
        ["Set_ConnSub"] = "Clone desktop connection parameters and mode",
        ["Set_Mode"] = "Connection mode",
        ["Set_ModeStandard"] = "Standard RDP",
        ["Set_ModeChild"] = "Child session",
        ["Set_ModeHint"] = "Standard RDP: separate local account · Child session: your current account (BetterGI-style)",
        ["Set_StandardWarn"] = "⚠ Standard RDP cannot connect on this machine (Home edition without the unlock layer). Open Env Check to install it with one click, or use child-session mode.",
        ["Set_Width"] = "Desktop width",
        ["Set_Height"] = "Desktop height",
        ["Set_Depth"] = "Color depth",
        ["Set_Port"] = "RDP port",
        ["Set_PxHint"] = "pixels; match your monitor resolution",
        ["Set_DepthHint"] = "bits; 32 is true color",
        ["Set_PortHint"] = "3389 by default; change only if you changed the OS-level RDP listener port",
        ["Set_ChkSmart"] = "Scale to fit window (Smart Sizing)",
        ["Set_ChkShortcuts"] = "Send system shortcuts to the clone",
        ["Set_ChkAudio"] = "Redirect audio to this machine",
        ["Set_BehaviorCard"] = "Behavior",
        ["Set_BehaviorSub"] = "Startup, tray, hotkeys and performance",
        ["Set_ChkAutoConnect"] = "Auto-connect on startup",
        ["Set_ChkLogoff"] = "Terminate child session on exit",
        ["Set_ChkTray"] = "Minimize to system tray",
        ["Set_ChkPerf"] = "Show performance in status bar (CPU/Mem)",
        ["Set_ChkHotkeys"] = "Enable global hotkeys (Ctrl+Shift+D connect · Ctrl+Alt+Space show window)",
        ["Set_ChkGameMouse"] = "Enable Game Mouse mode (standard RDP only, needs the replay agent)",
        ["Set_AccountCard"] = "Clone Account",
        ["Set_AccountSub"] = "Login credentials for standard RDP mode",
        ["Set_Username"] = "Username",
        ["Set_UsernameHint"] = "Required for standard RDP, e.g. AkiSpaceUser",
        ["Set_Password"] = "Password",
        ["Set_PasswordHint"] = "Leave empty to keep the current password; stored DPAPI-encrypted",
        ["Set_LookCard"] = "Appearance",
        ["Set_LookSub"] = "Aurora Glass color packs — two dark, two light; switching previews instantly",
        ["Set_Theme"] = "Theme",
        ["Set_ThemeDark"] = "Aurora Dusk (dark)",
        ["Set_ThemeAmber"] = "Amber Glow (dark)",
        ["Set_ThemeMint"] = "Fresh Mint (light)",
        ["Set_ThemePearl"] = "Pearl Mist (light)",
        ["Set_ThemeHint"] = "Cancel restores the original colors; Save persists the choice",
        ["Set_Language"] = "Language",
        ["Set_LanguageHint"] = "Switches the whole interface immediately",
        ["Set_AutoCard"] = "Auto Launch",
        ["Set_AutoSub"] = "Program to start inside the clone after connecting",
        ["Set_AutoPath"] = "Launch after connect",
        ["Set_Browse"] = "Browse…",
        ["Set_Cancel"] = "Cancel",
        ["Set_Save"] = "Save",
        ["Set_DlgTitle"] = "Choose the program to auto-launch",
        ["Set_DlgFilter"] = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",

        // ---- password prompt ----
        ["Pw_Title"] = "AkiSpace — Clone Account Password",
        ["Pw_Body"] = "Standard RDP mode needs the clone account's password. AkiSpace no longer ships a default password — enter the clone account's password (it is stored locally, DPAPI-encrypted):",
        ["Pw_Ok"] = "OK",
        ["Pw_Cancel"] = "Cancel",

        // ---- environment check window ----
        ["Env_Title"] = "Environment Check / Fix — Desktop Clone Prerequisites",
        ["Env_CheckCard"] = "Environment Check",
        ["Env_CheckSub"] = "Prerequisites for the desktop-clone feature",
        ["Env_VerdictCard"] = "Compatibility verdict",
        ["Env_VerdictSub"] = "Which clone modes work on this machine",
        ["Env_ChildOk"] = "Child session: ✓ available",
        ["Env_ChildNeedsFix"] = "Child session: needs One-Click Fix to enable",
        ["Env_ChildUnknown"] = "Child session: unknown (check failed)",
        ["Env_StandardOk"] = "Standard RDP: ✓ available",
        ["Env_StandardUnavailable"] = "Standard RDP: ✗ unavailable on this machine",
        ["Env_StandardReason"] = "Reason: Windows Home without the RDP unlock layer — standard RDP cannot connect; child sessions are unaffected",
        ["Env_InstallWrapper"] = "One-click install RDP unlock layer (auto-download, one UAC prompt)",
        ["Env_InstallNote"] = "Note: the unlock layer and child-session mode are mutually exclusive — after installing, child-session mode cannot connect (One-Click Fix can remove it again).",
        ["Env_ManualGuide"] = "View manual install steps ↓",
        ["Env_InstallDownloading"] = "Downloading rdpWrapper {0} (official GitHub release)…",
        ["Env_InstallVerifying"] = "Verifying SHA-256…",
        ["Env_InstallUac"] = "Waiting for administrator approval (UAC) — the installer stops/starts TermService…",
        ["Env_InstallDone"] = "Install finished — re-checking…",
        ["Env_InstallFailed"] = "Install failed: {0}",
        ["Env_InstallUacCancelled"] = "Administrator permission was declined; nothing was installed.",
        ["Env_DownloadFailed"] = "Download failed: {0}",
        ["Env_WrapperInstalledNote"] = "Unlock layer installed — standard RDP works; child-session mode will be refused while the hook is active.",
        ["Env_Placeholder"] = "Checking environment…",
        ["Env_PlaceholderDetail"] = "The RDP listener probe can take a few seconds",
        ["Env_Pass"] = "✓ Pass",
        ["Env_Fail"] = "✗ Fail",
        ["Env_Error"] = "✗ Error",
        ["Env_CheckFailed"] = "Check failed: {0}",
        ["Env_HomeTitle"] = "⚠ Home edition requires RDP Wrapper",
        ["Env_HomeSub"] = "Windows Home lacks the RDP host feature; a third-party unlock layer is required",
        ["Env_HomeBody"] =
            "Windows Home does not include the RDP host; the desktop-clone feature needs the RDP Wrapper third-party unlock layer.\n" +
            "Steps: ① download a community-maintained RDP Wrapper → ② extract to C:\\Program Files\\RDP Wrapper\\ →\n" +
            "③ run rdpWrapper.exe -install as administrator → ④ confirm rdpwrap.ini contains a section for this machine's termsrv.dll version.\n" +
            "AkiSpace never downloads or runs third-party binaries automatically — get it from a source you trust.",
        ["Env_Termsrv"] = "Local termsrv.dll version:  {0}    (rdpwrap.ini needs a [10.0.{1}.xxxx] section)",
        ["Env_OpenSergiye"] = "① Open sergiye/rdpWrapper (C# recommended)",
        ["Env_OpenSeba"] = "② Open sebaxakerhtc/rdpwrap (Delphi fork)",
        ["Env_CopyVer"] = "③ Copy version number (to search rdpwrap.ini)",
        ["Env_CopyDiag"] = "④ Copy diagnostics (for GitHub feedback)",
        ["Env_RecheckInstalled"] = "I have installed RDP Wrapper → Re-check",
        ["Env_Close"] = "Close",
        ["Env_Fix"] = "One-Click Fix",
        ["Env_Recheck"] = "Re-check",
        ["Env_FixTitle"] = "AkiSpace Environment Fix",
        ["Env_FixPromptChild"] =
            "The following will be applied (administrator rights required; a UAC prompt will appear):\n\n" +
            "  ✓ Disable RDP Wrapper (TermWrap.dll)\n" +
            "  ✓ Enable RDP (fDenyTSConnections=0)\n" +
            "  ✓ Allow multi-session (fSingleSessionPerUser=0)\n" +
            "  ✓ Set StartRCM=1 (Home-edition fix)\n" +
            "  ✓ Security hardening (TLS + high encryption + NLA)\n" +
            "  ✓ Add the firewall loopback rule (RDP reachable from 127.0.0.1 only)\n" +
            "  ✓ Block public inbound 3389 (removes the default public RDP allow rule)\n" +
            "  ✓ Restart the TermService service (drops existing RDP sessions)\n" +
            "  ✓ Enable child sessions\n\n" +
            "RDP Wrapper and child-session mode are mutually exclusive (per BetterGI's official docs);\n" +
            "this tool will disable TermWrap and restore the native termsrv.dll to enable child sessions.\n\n" +
            "Note: the RDP Wrapper itself (rdpwrap.dll) must be installed manually per the guide at the top of this dialog.\n\n" +
            "Continue?",
        ["Env_FixPromptStandard"] =
            "The following will be applied (administrator rights required; a UAC prompt will appear):\n\n" +
            "  ✗ Keep the RDP Wrapper hook (required for standard RDP mode)\n" +
            "  ✓ Enable RDP (fDenyTSConnections=0)\n" +
            "  ✓ Allow multi-session (fSingleSessionPerUser=0)\n" +
            "  ✓ Set StartRCM=1 (Home-edition fix)\n" +
            "  ✓ Security hardening (TLS + high encryption + NLA)\n" +
            "  ✓ Add the firewall loopback rule (RDP reachable from 127.0.0.1 only)\n" +
            "  ✓ Block public inbound 3389 (removes the default public RDP allow rule)\n" +
            "  ✓ Restart the TermService service (drops existing RDP sessions)\n\n" +
            "Note: the RDP Wrapper itself (rdpwrap.dll) must be installed manually per the guide at the top of this dialog.\n" +
            "To also disable TermWrap in standard RDP mode, switch Settings to Child session mode first,\n" +
            "or tick \"Also disable TermWrap\" below.\n\n" +
            "Continue?",
        ["Env_FixOverride"] = "Also disable TermWrap (override default behavior)",
        ["Env_Continue"] = "Continue",
        ["Env_NoExePath"] = "Could not determine the program path.",
        ["Env_UacCancelled"] = "Administrator permission was cancelled; no fixes were applied.",
        ["Env_LaunchFail"] = "Failed to launch the elevated process: {0}",
        ["Env_OpenUrlFail"] = "Could not open the link: {0}\nVisit manually: {1}",
        ["Env_CopiedVersion"] = "termsrv.dll version copied",
        ["Env_CopiedDiag"] = "Diagnostics copied (paste into a GitHub issue)",
        ["Env_NothingToCopy"] = "Nothing to copy.",
        ["Env_CopyFail"] = "Copy failed: {0}",
        ["Diag_Header"] = "## AkiSpace diagnostics",
        ["Diag_Time"] = "- Time: {0}",
        ["Diag_Os"] = "- OS: {0}",
        ["Diag_Proc"] = "- Process: {0} {1}",
        ["Diag_Section"] = "### Environment check results",

        // ---- environment verifier: check names & details ----
        ["Chk_Edition"] = "Windows edition",
        ["Chk_EditionUnknown"] = "Edition could not be read from the registry",
        ["Chk_EditionHome"] = "{0} (Home family — standard RDP needs the unlock layer; child sessions are NOT affected)",
        ["Chk_EditionStandard"] = "{0} (supports standard RDP natively)",
        ["Chk_RdpEnabled"] = "RDP enabled (fDenyTSConnections)",
        ["Chk_RdpEnabledBad"] = "fDenyTSConnections = {0} (should be 0)",
        ["Chk_MultiSession"] = "Multi-session allowed (fSingleSessionPerUser)",
        ["Chk_MultiSessionBad"] = "fSingleSessionPerUser = {0} (should be 0)",
        ["Chk_WrapperUnlock"] = "Multi-session unlock (TermWrap/rdpwrap)",
        ["Chk_WrapperFound"] = "RDP unlock layer detected (TermWrap.dll or rdpwrap.dll)",
        ["Chk_WrapperMissing"] = "No RDP unlock layer (required for standard RDP on Home editions)",
        ["Chk_WrapperNotNeeded"] = "No unlock layer — not needed on this edition (standard RDP works natively)",
        ["Chk_StartRcm"] = "StartRCM (Home fix)",
        ["Chk_StartRcmBad"] = "StartRCM = {0} (should be 1)",
        ["Chk_TermService"] = "TermService service",
        ["Chk_TermRunning"] = "Running",
        ["Chk_TermStatus"] = "Status: {0}",
        ["Chk_Firewall"] = "Firewall loopback rule (RDP local-only)",
        ["Chk_FirewallOk"] = "Public block rule in place, default public rule removed (loopback still reachable via the firewall bypass)",
        ["Chk_FirewallBad"] = "block={0}, default public rule left={1}",
        ["Chk_ChildSessions"] = "Child sessions",
        ["Chk_ChildEnabledActive"] = "Enabled, current clone session ID = {0}",
        ["Chk_ChildEnabledIdle"] = "Enabled, no active clone session",
        ["Chk_ChildDisabled"] = "Not enabled (needs WTSEnableChildSessions)",
        ["Chk_Listener"] = "RDP listener port",
        ["Chk_ListenerOk"] = "Port {0} is listening",
        ["Chk_ListenerBad"] = "Listener down (TermService not running or not unlocked)",
        ["Chk_TermsrvVersion"] = "termsrv.dll version",
        ["Chk_WrapperHook"] = "RDP Wrapper (TermWrap.dll) hook",
        ["Chk_HookActive"] = "⚠ TermService ServiceDll = {0}. RDP Wrapper is incompatible with AkiSpace child sessions (BetterGI's official docs warn about this). Resolve via Env Check → disable RDP Wrapper.",
        ["Chk_HookNone"] = "No RDP Wrapper hook detected ({0})",
        ["Chk_HookNoDll"] = "No ServiceDll value found",

        // ---- environment verifier: fix result names & details ----
        ["Fx_DisableWrapper"] = "Disable RDP Wrapper (TermWrap.dll)",
        ["Fx_DisableWrapperFail"] = "Nothing to disable, or the operation failed",
        ["Fx_KeepWrapper"] = "Keep RDP Wrapper hook",
        ["Fx_KeepWrapperDetail"] = "Standard RDP mode — TermWrap.dll is the multi-session unlock layer; skipping disable",
        ["Fx_Security"] = "Security hardening (SecurityLayer/MinEncryptionLevel/NLA)",
        ["Fx_Firewall"] = "Firewall loopback rule",
        ["Fx_FirewallOk"] = "AkiSpace RDP Loopback rule added",
        ["Fx_FirewallFail"] = "Failed to add (administrator rights required)",
        ["Fx_RestartTermService"] = "TermService restart",
        ["Fx_RestartOk"] = "Restarted",
        ["Fx_RestartFail"] = "Restart failed",
        ["Fx_EnableChildSessions"] = "Enable child sessions",
        ["Fx_EnableChildOk"] = "WTSEnableChildSessions(true) succeeded",
        ["Fx_EnableChildFail"] = "Call failed",

        // ---- elevated fix console (--fix-env) ----
        ["FixEnv_Title"] = "AkiSpace — Environment Fix (Administrator)",
        ["FixEnv_Banner"] = "=== AkiSpace Environment Fix (Administrator) ===",
        ["FixEnv_Applying"] = "Applying environment fixes…",
        ["FixEnv_AllOk"] = "All fixes were applied successfully!",
        ["FixEnv_SomeFailed"] = "{0} fix(es) failed — check the details above.",
        ["FixEnv_PressKey"] = "Press any key to exit...",
    };

    private static readonly Dictionary<string, string> Zh = new()
    {
        // ---- app-wide ----
        ["App_AlreadyRunning"] = "AkiSpace 已经在运行中。",
        ["App_Fatal"] = "AkiSpace 遇到致命错误：\n{0}\n\n详细信息见日志。",

        // ---- main window ----
        ["Main_Title"] = "AkiSpace — 桌面分身",
        ["Main_Connect"] = "连接",
        ["Main_Disconnect"] = "断开",
        ["Main_Terminate"] = "终止",
        ["Main_GameMouse"] = "游戏鼠标",
        ["Main_GameMouseOn"] = "游戏鼠标: 开",
        ["Main_Launch"] = "启动程序",
        ["Main_Capture"] = "截屏",
        ["Main_CaptureTip"] = "截取分身画面并复制到剪贴板 (Ctrl+Shift+S)",
        ["Main_EnvCheck"] = "环境检查",
        ["Main_Settings"] = "设置",
        ["Main_ChildChecking"] = "子会话: 检测中",
        ["Main_ConnInitial"] = "连接: 未连接",
        ["Main_WrapChecking"] = "RDP 解锁: 检测中",
        ["Main_PerfInitial"] = "CPU: 0% | 内存: 0 MB",
        ["Main_EmptyTitle"] = "分身桌面未连接",
        ["Main_EmptyHint"] = "点击工具栏「连接」建立分身会话；连接后可用 Ctrl+Shift+S 一键截屏",
        ["Main_Ready"] = "就绪",

        // ---- clone screenshot ----
        ["Shot_StatusOk"] = "已截取分身画面 → 剪贴板",
        ["Shot_StatusFail"] = "截屏: 未连接分身或复制失败",
        ["Shot_ToastOk"] = "分身画面已复制到剪贴板",
        ["Shot_ToastFail"] = "截屏失败 — 分身未连接",

        // ---- RDP ActiveX overlay texts ----
        ["Rdp_Connecting"] = "正在创建 AkiSpace 桌面分身...",
        ["Rdp_Disconnected"] = "AkiSpace 桌面分身已断开",

        // ---- connection status line (ConnectionController) ----
        ["Conn_NotConnected"] = "连接: 未连接",
        ["Conn_Disconnected"] = "连接: 已断开",
        ["Conn_NoPassword"] = "连接: 未提供密码",
        ["Conn_Preparing"] = "连接: 正在准备...",
        ["Conn_Connecting"] = "连接: 正在连接 {0} ...",
        ["Conn_RdpNotReady"] = "连接: RDP 未就绪",
        ["Conn_WrapperConflict"] = "连接: RDP Wrapper 冲突",
        ["Conn_Failed"] = "连接: 连接失败",
        ["Conn_Terminated"] = "连接: 子会话已终止",
        ["Conn_TerminateFailed"] = "连接: 终止失败",
        ["Conn_ConnectedAgent"] = "连接: 已连接 — Agent 启动中",
        ["Conn_Ready"] = "连接: 已连接 — 分身桌面就绪",
        ["Conn_Connected"] = "连接: 已连接",
        ["Conn_Launching"] = "连接: 正在启动 {0} …",
        ["Conn_Launched"] = "连接: 已在分身（会话 {1}）中启动 {0}",
        ["Conn_LaunchFailed"] = "连接: 启动失败（详见日志）",
        ["Conn_SettingsSaved"] = "连接: 设置已保存",

        // ---- status rows ----
        ["Child_Active"] = "子会话: 活动 (ID {0})",
        ["Child_None"] = "子会话: 无",
        ["Wrap_Installed"] = "RDP 解锁: 已安装",
        ["Wrap_NotInstalled"] = "RDP 解锁: 未安装",
        ["Perf_Status"] = "CPU: {0}% | 内存: {1}",
        ["Perf_NA"] = "不可用",

        // ---- message boxes (connection controller) ----
        ["Box_ListenerDown"] =
            "RDP 监听器暂时未响应连接探测。可能原因：\n" +
            "1) TermService 正在重启（禁用 RDP Wrapper 后需要 10–20 秒）— 请稍等再试\n" +
            "2) 真未监听 — 运行「环境检查/修复」→「一键修复」\n" +
            "3) 防火墙/杀软拦截 127.0.0.1:3389",
        ["Box_WrapperConflictTitle"] = "AkiSpace — RDP Wrapper 与子会话冲突",
        ["Box_WrapperConflict"] =
            "检测到 RDP Wrapper (TermWrap.dll) 已 hook TermService。\n\n" +
            "BetterGI 官方文档明确说明：RDP Wrapper 与桌面分身（子会话）功能不兼容，" +
            "两者不能同时使用。RDP Wrapper 的 hook 会导致子会话 broker 无法创建会话，" +
            "表现为「远程桌面无法连接到远程计算机 (516)」。\n\n" +
            "请按以下步骤解决：\n" +
            "1. 在 AkiSpace 中打开「环境检查/修复」\n" +
            "2. 点击「一键修复」（最新版会禁用 RDP Wrapper 并恢复 termsrv.dll）\n" +
            "3. 等待 TermService 重启完成\n" +
            "4. 重新尝试子会话连接\n\n" +
            "RDP Wrapper 和 AkiSpace 子会话功能重复（二者都是为多用户场景设计），" +
            "只能二选一。建议直接使用 AkiSpace 子会话。",
        ["Box_ConnectFailTitle"] = "AkiSpace 连接",
        ["Box_ConnectFailed"] = "连接失败：{0}",
        ["Box_NoChildSession"] = "当前没有活动的子会话。",
        ["Box_TerminateConfirm"] = "确定要终止子会话（ID {0}）吗？其中的程序将全部关闭。",
        ["Box_GameMouseTitle"] = "AkiSpace — 游戏鼠标",
        ["Box_GameMouseAgent"] =
            "回放 Agent 未连接。游戏鼠标模式需要分身侧运行回放 Agent（--agent 模式）。\n" +
            "请先在分身会话中启动 Agent，或在 Agent 落地前使用标准 RDP 鼠标。",
        ["Box_ConnectFirst"] = "请先连接分身。",
        ["Box_LaunchFail"] = "启动失败。可能是权限不足或 Task Scheduler 服务不可用。",

        // ---- tray ----
        ["Tray_ToggleConnect"] = "连接分身",
        ["Tray_ToggleDisconnect"] = "断开分身",
        ["Tray_Screenshot"] = "截屏分身画面",
        ["Tray_Show"] = "显示主窗口",
        ["Tray_Exit"] = "退出",
        ["Tray_TipConnected"] = "AkiSpace - 已连接",
        ["Tray_TipDisconnected"] = "AkiSpace - 未连接",

        // ---- settings window ----
        ["Set_Title"] = "设置",
        ["Set_ConnCard"] = "连接配置",
        ["Set_ConnSub"] = "分身桌面的连接参数与模式",
        ["Set_Mode"] = "连接模式",
        ["Set_ModeStandard"] = "标准 RDP",
        ["Set_ModeChild"] = "子会话",
        ["Set_ModeHint"] = "标准 RDP：独立本地账户 · 子会话：复用当前账户（BetterGI 同款）",
        ["Set_StandardWarn"] = "⚠ 本机标准 RDP 无法连接（家庭版且未安装解锁层）。请先打开「环境检查」一键安装解锁层，或使用子会话模式。",
        ["Set_Width"] = "分身桌面宽度",
        ["Set_Height"] = "分身桌面高度",
        ["Set_Depth"] = "颜色深度",
        ["Set_Port"] = "RDP 端口",
        ["Set_PxHint"] = "像素，建议匹配显示器分辨率",
        ["Set_DepthHint"] = "位，32位为真彩色",
        ["Set_PortHint"] = "默认 3389，仅当你在系统层面改过 RDP 监听端口时同步修改",
        ["Set_ChkSmart"] = "缩放适应窗口 (Smart Sizing)",
        ["Set_ChkShortcuts"] = "系统快捷键发送到分身",
        ["Set_ChkAudio"] = "音频重定向到本机",
        ["Set_BehaviorCard"] = "行为设置",
        ["Set_BehaviorSub"] = "启动、托盘、热键与性能监控",
        ["Set_ChkAutoConnect"] = "启动时自动连接分身",
        ["Set_ChkLogoff"] = "退出时终止子会话",
        ["Set_ChkTray"] = "最小化到系统托盘",
        ["Set_ChkPerf"] = "状态栏显示性能监控 (CPU/内存)",
        ["Set_ChkHotkeys"] = "启用全局热键 (Ctrl+Shift+D 切换连接, Ctrl+Alt+Space 显示窗口)",
        ["Set_ChkGameMouse"] = "启用游戏鼠标模式（仅标准 RDP，需回放 Agent）",
        ["Set_AccountCard"] = "分身账户",
        ["Set_AccountSub"] = "标准 RDP 模式下的登录凭据",
        ["Set_Username"] = "分身账户用户名",
        ["Set_UsernameHint"] = "标准 RDP 模式必填，如 AkiSpaceUser",
        ["Set_Password"] = "分身账户密码",
        ["Set_PasswordHint"] = "留空保持现有密码不变；密码使用 DPAPI 加密存储",
        ["Set_LookCard"] = "外观",
        ["Set_LookSub"] = "Aurora Glass 配色包 — 两套深色、两套浅色，切换即时预览",
        ["Set_Theme"] = "主题",
        ["Set_ThemeDark"] = "极光暮色 (深)",
        ["Set_ThemeAmber"] = "琥珀霞光 (深)",
        ["Set_ThemeMint"] = "薄荷清新 (浅)",
        ["Set_ThemePearl"] = "珍珠雾粉 (浅)",
        ["Set_ThemeHint"] = "取消关闭窗口时恢复原配色；保存后写入设置",
        ["Set_Language"] = "语言",
        ["Set_LanguageHint"] = "切换后整个界面立即生效",
        ["Set_AutoCard"] = "自动启动",
        ["Set_AutoSub"] = "连接成功后在分身中启动程序",
        ["Set_AutoPath"] = "连接后自动启动",
        ["Set_Browse"] = "浏览...",
        ["Set_Cancel"] = "取消",
        ["Set_Save"] = "保存",
        ["Set_DlgTitle"] = "选择连接后自动启动的程序",
        ["Set_DlgFilter"] = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",

        // ---- password prompt ----
        ["Pw_Title"] = "AkiSpace — 分身账户密码",
        ["Pw_Body"] = "标准 RDP 模式需要分身账户的密码。AkiSpace 不再内置默认密码，请输入分身账户的密码（将使用 DPAPI 加密保存在本机设置中）：",
        ["Pw_Ok"] = "确定",
        ["Pw_Cancel"] = "取消",

        // ---- environment check window ----
        ["Env_Title"] = "环境检查 / 修复 — 桌面分身前置条件",
        ["Env_CheckCard"] = "环境检查",
        ["Env_CheckSub"] = "桌面分身运行前置条件检测",
        ["Env_VerdictCard"] = "环境结论",
        ["Env_VerdictSub"] = "本机可用的分身模式",
        ["Env_ChildOk"] = "子会话：✓ 可用",
        ["Env_ChildNeedsFix"] = "子会话：需先「一键修复」启用",
        ["Env_ChildUnknown"] = "子会话：未知（检查失败）",
        ["Env_StandardOk"] = "标准 RDP：✓ 可用",
        ["Env_StandardUnavailable"] = "标准 RDP：✗ 本机不可用",
        ["Env_StandardReason"] = "原因：Windows 家庭版未安装 RDP 解锁层——标准 RDP 无法连接；子会话不受影响",
        ["Env_InstallWrapper"] = "一键安装 RDP 解锁层（自动下载，一次 UAC 授权）",
        ["Env_InstallNote"] = "注意：解锁层与子会话模式互斥——安装后子会话将无法连接（「一键修复」可随时卸载恢复）。",
        ["Env_ManualGuide"] = "查看手动安装步骤 ↓",
        ["Env_InstallDownloading"] = "正在下载 rdpWrapper {0}（官方 GitHub 发布）…",
        ["Env_InstallVerifying"] = "正在校验 SHA-256…",
        ["Env_InstallUac"] = "等待管理员授权（UAC）——安装器会重启 TermService…",
        ["Env_InstallDone"] = "安装完成——正在重新检查…",
        ["Env_InstallFailed"] = "安装失败：{0}",
        ["Env_InstallUacCancelled"] = "已取消管理员授权，未安装任何内容。",
        ["Env_DownloadFailed"] = "下载失败：{0}",
        ["Env_WrapperInstalledNote"] = "解锁层已安装——标准 RDP 可用；hook 生效期间子会话模式会被拒绝连接。",
        ["Env_Placeholder"] = "正在检查环境…",
        ["Env_PlaceholderDetail"] = "RDP 监听探测可能需要数秒，请稍候",
        ["Env_Pass"] = "✓ 通过",
        ["Env_Fail"] = "✗ 失败",
        ["Env_Error"] = "✗ 错误",
        ["Env_CheckFailed"] = "检查失败：{0}",
        ["Env_HomeTitle"] = "⚠ 家庭版需要安装 RDP Wrapper",
        ["Env_HomeSub"] = "Windows 家庭版缺少 RDP 主机功能，需第三方解锁层",
        ["Env_HomeBody"] =
            "Windows 家庭版不提供 RDP 主机，桌面分身功能需要 RDP Wrapper 第三方解锁层。\n" +
            "安装步骤：① 下载任一社区维护的 RDP Wrapper → ② 解压到 C:\\Program Files\\RDP Wrapper\\ →\n" +
            "③ 以管理员运行 rdpWrapper.exe -install → ④ 确认 rdpwrap.ini 含本机 termsrv.dll 版本段。\n" +
            "AkiSpace 不会自动下载运行第三方二进制，请按需从可信来源获取。",
        ["Env_Termsrv"] = "本机 termsrv.dll 版本:  {0}    （在 rdpwrap.ini 中需找到 [10.0.{1}.xxxx] 段落）",
        ["Env_OpenSergiye"] = "① 打开 sergiye/rdpWrapper (C# 推荐)",
        ["Env_OpenSeba"] = "② 打开 sebaxakerhtc/rdpwrap (Delphi fork)",
        ["Env_CopyVer"] = "③ 复制版本号（用于搜索 rdpwrap.ini）",
        ["Env_CopyDiag"] = "④ 复制诊断信息（用于 GitHub 反馈）",
        ["Env_RecheckInstalled"] = "我已安装 RDP Wrapper → 重新检查",
        ["Env_Close"] = "关闭",
        ["Env_Fix"] = "一键修复",
        ["Env_Recheck"] = "重新检查",
        ["Env_FixTitle"] = "AkiSpace 环境修复",
        ["Env_FixPromptChild"] =
            "将执行以下操作（需要管理员权限，会弹出 UAC 提示）：\n\n" +
            "  ✓ 禁用 RDP Wrapper (TermWrap.dll)\n" +
            "  ✓ 启用 RDP（fDenyTSConnections=0）\n" +
            "  ✓ 允许多会话（fSingleSessionPerUser=0）\n" +
            "  ✓ 设置 StartRCM=1（家庭版修复）\n" +
            "  ✓ 安全加固（TLS + 高加密 + NLA）\n" +
            "  ✓ 添加防火墙回环规则（RDP 仅允许 127.0.0.1）\n" +
            "  ✓ 防火墙阻断 3389 公网入站（删除默认 RDP 公开 allow）\n" +
            "  ✓ 重启 TermService 服务（会踢掉现有 RDP 会话）\n" +
            "  ✓ 启用子会话\n\n" +
            "RDP Wrapper 与子会话模式互斥（BetterGI 官方文档已说明），\n" +
            "本工具将禁用 TermWrap 并恢复原生 termsrv.dll 以启用子会话。\n\n" +
            "注意：RDP Wrapper 本身（rdpwrap.dll）需按本对话框顶部的指引手动安装。\n\n" +
            "是否继续？",
        ["Env_FixPromptStandard"] =
            "将执行以下操作（需要管理员权限，会弹出 UAC 提示）：\n\n" +
            "  ✗ 保留 RDP Wrapper hook（标准 RDP 模式必需）\n" +
            "  ✓ 启用 RDP（fDenyTSConnections=0）\n" +
            "  ✓ 允许多会话（fSingleSessionPerUser=0）\n" +
            "  ✓ 设置 StartRCM=1（家庭版修复）\n" +
            "  ✓ 安全加固（TLS + 高加密 + NLA）\n" +
            "  ✓ 添加防火墙回环规则（RDP 仅允许 127.0.0.1）\n" +
            "  ✓ 防火墙阻断 3389 公网入站（删除默认 RDP 公开 allow）\n" +
            "  ✓ 重启 TermService 服务（会踢掉现有 RDP 会话）\n\n" +
            "注意：RDP Wrapper 本身（rdpwrap.dll）需按本对话框顶部的指引手动安装。\n" +
            "如果你想在标准 RDP 模式下也禁用 TermWrap，请先在「设置」中切换到「子会话」模式，\n" +
            "或勾选下方的「同时禁用 TermWrap」选项。\n\n" +
            "是否继续？",
        ["Env_FixOverride"] = "同时禁用 TermWrap（覆盖默认行为）",
        ["Env_Continue"] = "继续",
        ["Env_NoExePath"] = "无法获取程序路径。",
        ["Env_UacCancelled"] = "已取消管理员权限，修复未执行。",
        ["Env_LaunchFail"] = "启动管理员进程失败：{0}",
        ["Env_OpenUrlFail"] = "无法打开链接：{0}\n请手动访问：{1}",
        ["Env_CopiedVersion"] = "已复制 termsrv.dll 版本号",
        ["Env_CopiedDiag"] = "诊断信息已复制（可粘贴到 GitHub issue）",
        ["Env_NothingToCopy"] = "无内容可复制。",
        ["Env_CopyFail"] = "复制失败：{0}",
        ["Diag_Header"] = "## AkiSpace 诊断信息",
        ["Diag_Time"] = "- 时间：{0}",
        ["Diag_Os"] = "- 系统：{0}",
        ["Diag_Proc"] = "- 进程：{0} {1}",
        ["Diag_Section"] = "### 环境检查结果",

        // ---- environment verifier: check names & details ----
        ["Chk_Edition"] = "Windows 版本",
        ["Chk_EditionUnknown"] = "无法从注册表读取系统版本",
        ["Chk_EditionHome"] = "{0}（家庭版家族——标准 RDP 需解锁层；子会话不受影响）",
        ["Chk_EditionStandard"] = "{0}（原生支持标准 RDP）",
        ["Chk_RdpEnabled"] = "RDP 已启用 (fDenyTSConnections)",
        ["Chk_RdpEnabledBad"] = "fDenyTSConnections = {0}（应为 0）",
        ["Chk_MultiSession"] = "允许多会话 (fSingleSessionPerUser)",
        ["Chk_MultiSessionBad"] = "fSingleSessionPerUser = {0}（应为 0）",
        ["Chk_WrapperUnlock"] = "多会话解锁 (TermWrap/rdpwrap)",
        ["Chk_WrapperFound"] = "检测到 RDP 解锁层（TermWrap.dll 或 rdpwrap.dll）",
        ["Chk_WrapperMissing"] = "未检测到 RDP 解锁层（家庭版使用标准 RDP 必需）",
        ["Chk_WrapperNotNeeded"] = "无需解锁层——本版本原生支持标准 RDP",
        ["Chk_StartRcm"] = "StartRCM（家庭版修复）",
        ["Chk_StartRcmBad"] = "StartRCM = {0}（应为 1）",
        ["Chk_TermService"] = "TermService 服务",
        ["Chk_TermRunning"] = "正在运行",
        ["Chk_TermStatus"] = "状态：{0}",
        ["Chk_Firewall"] = "防火墙回环规则 (RDP 仅本机)",
        ["Chk_FirewallOk"] = "公网 block 规则就位，默认公开规则已删除（回环经防火墙旁路仍可达）",
        ["Chk_FirewallBad"] = "block={0}, 公开规则残留={1}",
        ["Chk_ChildSessions"] = "子会话 (Child Sessions)",
        ["Chk_ChildEnabledActive"] = "已启用，当前子会话 ID = {0}",
        ["Chk_ChildEnabledIdle"] = "已启用，暂无活动子会话",
        ["Chk_ChildDisabled"] = "未启用（需要 WTSEnableChildSessions）",
        ["Chk_Listener"] = "RDP 监听端口",
        ["Chk_ListenerOk"] = "端口 {0} 正在监听",
        ["Chk_ListenerBad"] = "监听失败（TermService 未运行或未解锁）",
        ["Chk_TermsrvVersion"] = "termsrv.dll 版本",
        ["Chk_WrapperHook"] = "RDP Wrapper (TermWrap.dll) hook",
        ["Chk_HookActive"] = "⚠ TermService ServiceDll = {0}。RDP Wrapper 与 AkiSpace 子会话不兼容（BetterGI 官方文档明确警告）。请用「环境检查/修复 → 禁用 RDP Wrapper」解决。",
        ["Chk_HookNone"] = "未检测到 RDP Wrapper hook（{0}）",
        ["Chk_HookNoDll"] = "未检测到 ServiceDll",

        // ---- environment verifier: fix result names & details ----
        ["Fx_DisableWrapper"] = "禁用 RDP Wrapper (TermWrap.dll)",
        ["Fx_DisableWrapperFail"] = "无需禁用或失败",
        ["Fx_KeepWrapper"] = "保留 RDP Wrapper hook",
        ["Fx_KeepWrapperDetail"] = "当前为标准 RDP 模式，TermWrap.dll 是多会话解锁层，跳过禁用",
        ["Fx_Security"] = "安全加固 (SecurityLayer/MinEncryptionLevel/NLA)",
        ["Fx_Firewall"] = "防火墙回环规则",
        ["Fx_FirewallOk"] = "已添加 AkiSpace RDP Loopback 规则",
        ["Fx_FirewallFail"] = "添加失败（需要管理员权限）",
        ["Fx_RestartTermService"] = "TermService 重启",
        ["Fx_RestartOk"] = "已重启",
        ["Fx_RestartFail"] = "重启失败",
        ["Fx_EnableChildSessions"] = "启用子会话",
        ["Fx_EnableChildOk"] = "WTSEnableChildSessions(true) 成功",
        ["Fx_EnableChildFail"] = "调用失败",

        // ---- elevated fix console (--fix-env) ----
        ["FixEnv_Title"] = "AkiSpace — 环境修复（管理员）",
        ["FixEnv_Banner"] = "=== AkiSpace 环境修复（管理员权限）===",
        ["FixEnv_Applying"] = "正在应用环境修复...",
        ["FixEnv_AllOk"] = "所有修复已成功应用！",
        ["FixEnv_SomeFailed"] = "{0} 项修复失败，请检查上方详情。",
        ["FixEnv_PressKey"] = "按任意键退出...",
    };

    /// <summary>Test seam: the key sets of both catalogs (they must stay identical).</summary>
    internal static IReadOnlyDictionary<string, string> EnglishCatalog => En;
    internal static IReadOnlyDictionary<string, string> ChineseCatalog => Zh;
}

/// <summary>
/// Bindable singleton view over <see cref="Loc"/>: XAML binds through
/// <see cref="LocExtension"/>, which creates an indexer binding against this object;
/// a language switch raises one "Item[]" change so every bound string re-reads.
/// </summary>
public sealed class LocCatalog : INotifyPropertyChanged
{
    public static LocCatalog Instance { get; } = new();

    private LocCatalog() { }

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void OnLanguageChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>
/// XAML usage: <c>Text="{loc:Loc Main_Connect}"</c> — one-way, live-updating
/// localized string. Keys must match a catalog entry exactly (see LocTests).
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LocExtension : MarkupExtension
{
    private readonly string _key;

    public LocExtension(string key) => _key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{_key}]")
        {
            Source = LocCatalog.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
