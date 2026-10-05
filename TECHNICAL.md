<div align="center">

# AkiSpace — Technical Notes

**技术文档**

Version 0.8.0

</div>

---

## 1　System Overview

AkiSpace runs as **one GUI process plus two optional satellite processes**:

```
┌─ Primary session (your desktop) ──────────────┐   ┌─ Child/clone session ─────────┐
│ AkiSpace.exe (WPF + WinForms interop, STA)    │   │ AkiSpace.exe --agent          │
│  ├─ MainWindow ─ WindowsFormsHost ─ AxHost    │   │  ├─ PipeClient (named pipe)   │
│  │    (MSTSC MsRdpClient11 ActiveX)           │◄──┤  ├─ AgentRunner (SendInput)   │
│  ├─ ConnectionController (state machine)      │RDP│  └─ reads nonce from file     │
│  ├─ MouseForwarder ─ RawInputMonitor (WM_INPUT)│  └───────────────────────────────┘
│  ├─ PipeServer (nonce handshake, DACL)        │        ▲
│  └─ Tray / Hotkeys / EnvCheck / Installer     │        │ launched via Task
└───────────────────────────────────────────────┘        │ Scheduler COM (session id)
AkiSpace.exe --fix-env (elevated, console) ──────────────┘ one UAC prompt on demand
```

- **Connection modes** — *Standard RDP*: a separate local account over `127.0.0.1`, requires the RDP listener (on Home editions, the RDP unlock layer). *Child session*: the same user's second session via the termsrv child-session broker — works on Home editions with no listener, no unlock layer.
- The viewer is the **MSTSC ActiveX** wrapped in a WinForms `AxHost` inside a WPF `WindowsFormsHost`; all COM property access is late-bound via reflection, so there is no compile-time dependency on `MSTSCLib`.
- Everything user-facing is **bilingual** (English default / 中文) through a static catalog + XAML markup extension; every theme color flows through the Aurora Glass token system (four color packs).

## 2　RDP Viewer Host (`Controls/RdpActiveXHost.cs`)

- The control is created per connect and disposed per teardown; the MSTSC OCX is created by forcing `Handle` (BetterGI's `GetRequiredOcx` pattern) so the underlying window exists before any property write.
- **Connection is a retry loop on a worker thread** (3 attempts, exponential backoff 1 s / 2 s / 4 s), with each attempt marshalled to the UI (STA) thread through `BeginInvoke`. Every attempt re-checks the cancellation token first, so a Disconnect landing between attempts truly stops the connect.
- `ConnectToChildSession` is set through the **extended-properties interface** (`IMsRdpExtendedSettings`), with a read-back log line confirming the control accepted it. A plain-TCP fallback that ignores the property produces the (516) error class of failures; the read-back exists to expose exactly that.
- Events are consumed from a single sink: `OnLoginComplete` (dispId 3) → connected; `OnDisconnected` (4) → surfaced as failure **unless it follows a user-initiated Disconnect** (5 s suppression window); `OnLogonError` (22) — codes −5/−4/−2/3 are non-terminal prompts and leave state untouched; `OnAutoReconnected` (33) → treated as login.
- `AuthenticationLevel = 2`: localhost certificate mismatches are logged, never silently skipped.

## 3　Connection Orchestration (`App/ConnectionController.cs`)

The controller owns the connect state machine and emits immutable `ConnectUiState` snapshots; the window only renders them.

- Status lines are stored as **localization keys + format args + a semantic level** (`Idle / InFlight / Good`), so a language switch re-renders the current state and the status dots never depend on display text.
- The 1 Hz status poll re-asserts machine truth (`GetConnectedState`, WTS session id, unlock layer) but is **suppressed while connecting and for 5 s after an explicit disconnect** — MSTSC reports `Connected`/`OnDisconnected` asynchronously and the poll would otherwise overwrite the in-flight/terminal status.
- A **3 s exit watchdog is armed before any teardown work**; the ActiveX can block window destruction during a connect unwind, and the watchdog guarantees process exit on that path. The window also self-heals from the ActiveX owner-disable (it re-enables itself only when no other window of the thread is open, so modal dialogs keep their modality).
- Agent launch and manual "launch program" both run through Task Scheduler COM (`TASK_RUN_USE_SESSION_ID`); the temp task is deleted in a `finally` with a failure-path sweep, and the launch happens off the UI thread.

## 4　Input Pipeline (Game Mouse)

```
WM_INPUT (RawInputMonitor, dedicated STA thread, RIDEV_INPUTSINK)
  → relative deltas → MouseForwarder (accumulate 10 ms, flush on direction reversal)
    → Named pipe (≤1 MB frames) → AgentRunner (child session, bounded channel 1024, DropOldest)
      → SendInput replay
Cursor: ClipCursor(viewer bounds) + ShowCursor(hidden), Alt = temporary release
```

- The raw-input window lives on its own thread; the class registration is Unicode-exact (`RegisterClassW` ↔ `CreateWindowExW`) — an ANSI/W mismatch here silently kills the whole pipeline.
- The forwarder's 5 ms poll probes Alt state and viewer focus; probing happens outside the lock, the state transition **and** the cursor capture happen inside it, so a mode-disable cannot race an in-flight capture into a stuck clip+hide.
- `CursorCapture` runs **all** Win32 cursor work on one dedicated owner thread: `ShowCursor`'s display counter is per-thread, and Capture/Release arrive from arbitrary threadpool timer threads — cross-thread hide/show would leave the cursor permanently invisible.

## 5　IPC Protocol & Security

- **Framing**: 4-byte little-endian length + 1-byte payload type + payload; payloads capped at 1 MB; batches are an 18-byte header (sequence, base ticks, count) plus 16-byte samples (`dx`, `dy`, timestamp). Malformed payloads are logged and dropped — a bad frame never kills the read loop.
- **Handshake**: the agent presents a 32-byte nonce; the server compares in constant time and answers a 1-byte ACK. No nonce configured, wrong nonce, or **no verdict at all** → rejected/retried (a dropped connection is a retry, never a "success"). Handshake reads are bounded by a 5 s timeout so a silent client cannot wedge the single pipe instance.
- **Nonce delivery** is out-of-band: written to `%PROGRAMDATA%\AkiSpace\nonce_*.bin` with a DACL built **before** the bytes exist (owner + SYSTEM + clone account read/delete), consumed (read + delete) by the agent, swept at startup and at shutdown. The nonce never appears on the command line (argv leaks into Task Scheduler task XML and the PEB).
- **Pipe DACL**: primary user + SYSTEM; standard-RDP mode adds the clone account's SID so the cross-user agent can connect. ACL build failure falls back to `PipeOptions.CurrentUserOnly` (fail-closed), never to an open DACL.

## 6　Environment Check & One-Click Unlock (`Services/`)

- Eleven checks (edition, fDenyTSConnections, fSingleSessionPerUser, unlock layer, StartRCM, TermService, firewall, child sessions, listener probe, termsrv version, wrapper hook) run **on a worker thread**; each result carries a stable machine-readable `Id` (never localized) plus localized name/detail.
- A **verdict card** translates the raw checks into the user's real question — which clone modes work on this machine. On a Home edition without the unlock layer it states that standard RDP is unavailable (child sessions unaffected) and offers the one-click install.
- **One-click install**: downloads the pinned `sergiye/rdpWrapper` build (v2.15 x64) from its official GitHub release, verifies the SHA-256 recorded at pin time (mismatch aborts before anything executes), then launches it elevated with `-install -offline` — upstream stops/starts TermService and adds the Defender folder exclusion itself. The unlock layer is mutually exclusive with child sessions, and the UI says so before installing.
- The firewall posture is a **single inbound block rule** on the RDP port with `remoteip=any`: Windows Firewall never inspects loopback traffic, so loopback RDP keeps working while every real remote client is blocked; the default public allow rule is removed so it cannot shadow the block.

## 7　Settings, Themes, Language

- `%APPDATA%\AkiSpace\settings.json` — atomic write (temp + move), loaded with defaults on corruption **and healed back to disk immediately**. The clone password is DPAPI-protected (`CurrentUser`) with a versioned prefix; plaintext from older formats migrates on the next save; the in-memory snapshot never holds the wrapped blob.
- **Aurora Glass**: four self-contained color packs (two dark, two light) defined as token tables — base/text/accent trios, one three-color gradient ramp, per-pack aurora intensities, glass/liquid material tokens. The backdrop is four breathing glow layers + an orbiting sweep + star-dust + noise, all honoring the system "client area animation" setting.
- **Localization** is a static two-catalog table (200+ keys, EN default); XAML binds through `{loc:Loc Key}` to an indexer view, and a language switch raises one `Item[]` change that re-renders every open window live. Both catalogs are asserted key-identical (and placeholder-identical) by tests.
- WPF and WinForms interop coexist under one STA thread; the airspace rule is respected by design — hwnd-hosted content (the RDP viewer) always draws over WPF, so status messages intentionally live in the status bar while a session is up.

## 8　Process Lifecycle

- **Single instance** via a named `Global\` mutex (double-open would race settings.json and the pipe name).
- Modes: GUI (default), `--fix-env [--disable-wrapper]` (elevated console, one UAC prompt, localized output), `--agent --nonce-file <path>` (headless replay).
- File logging to `%LOCALAPPDATA%\AkiSpace\logs\akspace-YYYYMMDD.log`: one persistent stream writer flushed per line, stale daily logs pruned past a 14-day retention window, dispose-race safe.
- Shutdown ordering: status timer → controller teardown (game mouse off, host dispose, user-initiated logoff of the child session, nonce sweep) → hotkeys → tray → dispatcher shutdown, all under the 3 s watchdog.

## 9　Testing

- **74 unit/integration tests** (`tests/AkiSpace.Tests`): IPC frame edge cases (truncation, oversize, negative lengths, nonce validation), settings concurrency + corrupt-file healing + DPAPI round-trips + plaintext migration, bilingual catalog key **and placeholder parity**, every XAML/code-referenced key existence (a typo fails the build, not the UI), log rotation, theme-pack completeness, pinned-download constants.
- **SelfTest** (`tools/AkiSpace.SelfTest`): pure-logic assertions plus an interactive UI smoke (env-check window renders with all checks) and an optional E2E connect, auto-skipped on CI/headless.
- **End-to-end UIA scenarios** drive the real app on a real desktop: fresh start, multi-instance guard, settings save/reload round-trip, standard-RDP password gate, child-session connect → **real capture-to-clipboard bitmap verification** → disconnect without spurious dialogs, corrupt-settings recovery, maximize/restore layout, and the raw-input registration regression.

---

<div align="center">

# AkiSpace — 技术文档

版本 0.8.0

</div>

---

## 1　系统总览

AkiSpace 由**一个 GUI 进程加两个可选卫星进程**组成：

```
┌─ 主会话（你的桌面）───────────────────────────┐   ┌─ 子会话/分身 ──────────────────┐
│ AkiSpace.exe（WPF + WinForms interop, STA）   │   │ AkiSpace.exe --agent          │
│  ├─ MainWindow ─ WindowsFormsHost ─ AxHost    │   │  ├─ PipeClient（命名管道）    │
│  │    （MSTSC MsRdpClient11 ActiveX）          │◄──┤  ├─ AgentRunner（SendInput）  │
│  ├─ ConnectionController（连接状态机）         │RDP│  └─ 从文件读取 nonce          │
│  ├─ MouseForwarder ─ RawInputMonitor(WM_INPUT) │   └───────────────────────────────┘
│  ├─ PipeServer（nonce 握手、DACL）             │        ▲
│  └─ 托盘 / 热键 / 环境检查 / 解锁层安装器       │        │ Task Scheduler COM 按
└───────────────────────────────────────────────┘        │ 会话 ID 启动
AkiSpace.exe --fix-env（提权控制台）──────────────────────┘ 按需一次 UAC
```

- **两种连接模式**——*标准 RDP*：独立本地账户走 `127.0.0.1`，依赖 RDP 监听（家庭版需解锁层）；*子会话*：同一用户的第二个会话，走 termsrv 子会话 broker——家庭版无需监听、无需解锁层即可用。
- 查看器是 **MSTSC ActiveX**：WinForms `AxHost` 内嵌于 WPF `WindowsFormsHost`；全部 COM 属性走反射晚期绑定，编译期不依赖 `MSTSCLib`。
- 所有用户可见文字均为**双语**（默认英文 / 中文）：静态目录表 + XAML 标记扩展；全部主题颜色走 Aurora Glass 令牌系统（四套配色包）。

## 2　RDP 查看器宿主（`Controls/RdpActiveXHost.cs`）

- 每次连接创建、每次拆除销毁；通过强制读取 `Handle` 创建 OCX（BetterGI 的 `GetRequiredOcx` 模式），保证任何属性写入前底层窗口已存在。
- **连接是工作线程上的重试循环**（3 次，指数退避 1/2/4 秒），每次尝试经 `BeginInvoke` 封送回 UI（STA）线程；每次尝试先复查取消令牌——重试间隙点「断开」能真正终止连接。
- `ConnectToChildSession` 经**扩展属性接口**（`IMsRdpExtendedSettings`）设置，并回读打日志确认控件接受。若控件静默回退成普通 TCP 连接，会产生 (516) 一类错误——回读日志就是为暴露这种情况而存在。
- 事件经单一接收器消费：`OnLoginComplete`(dispId 3) → 已连接；`OnDisconnected`(4) → 上报为失败，**但用户主动断开后的 5 秒内除外**（抑制窗口）；`OnLogonError`(22) 中 −5/−4/−2/3 为非终止提示，不动状态；`OnAutoReconnected`(33) → 视为登录完成。
- `AuthenticationLevel = 2`：本地回环证书不匹配会记录告警，绝不静默跳过。

## 3　连接编排（`App/ConnectionController.cs`）

控制器拥有连接状态机，只向窗口发出不可变的 `ConnectUiState` 快照；窗口只负责渲染。

- 状态行以**本地化键 + 格式化参数 + 语义级别**（`Idle / InFlight / Good`）存储：切换语言可即时重绘当前状态，状态指示点颜色永远不依赖显示文本。
- 1Hz 状态轮询负责对齐机器真值（`GetConnectedState`、WTS 会话、解锁层），但在**连接期间与主动断开后 5 秒内被抑制**——MSTSC 的 `Connected`/`OnDisconnected` 是异步的，轮询否则会把进行中/终态覆盖掉。
- **3 秒退出看门狗在一切清理之前武装**：ActiveX 可能在连接未决时阻塞窗口销毁，看门狗保证该路径下进程必然退出。窗口还会自愈 ActiveX 的 owner-disable（仅当本线程没有其他窗口时才恢复启用，模态对话框的模态性因此保持）。
- Agent 启动与手动「启动程序」都走 Task Scheduler COM（`TASK_RUN_USE_SESSION_ID`）；临时任务在 `finally` 中删除并有失败兜底清扫，且启动过程不在 UI 线程。

## 4　输入管线（游戏鼠标）

```
WM_INPUT（RawInputMonitor，独立 STA 线程，RIDEV_INPUTSINK）
  → 相对增量 → MouseForwarder（累积 10ms，方向反转立即刷出）
    → 命名管道（帧 ≤1MB）→ AgentRunner（子会话，有界通道 1024，DropOldest）
      → SendInput 重放
光标：ClipCursor(查看器边界) + ShowCursor(隐藏)，按 Alt = 临时释放
```

- 原始输入窗口在独立线程上；类注册是精确的 Unicode 配对（`RegisterClassW` ↔ `CreateWindowExW`）——此处一旦出现 ANSI/W 错配，整条管线会静默失效。
- 转发器的 5ms 轮询探测 Alt 与查看器焦点：探测在锁外，**状态迁移与光标捕获在锁内**，关闭模式不可能与进行中的捕获竞态出「裁剪+隐藏卡死」。
- `CursorCapture` 把**所有** Win32 光标操作放在专用 owner 线程上串行执行：`ShowCursor` 的显示计数按线程维护，而捕获/释放来自任意线程池定时器线程——跨线程一隐一显会让光标永久不可见。

## 5　IPC 协议与安全

- **帧格式**：4 字节小端长度 + 1 字节载荷类型 + 载荷；载荷上限 1MB；批帧为 18 字节头（序号、基准刻度、数量）加 16 字节样本（`dx`、`dy`、时间戳）。坏载荷记日志后丢弃——一个坏帧不会杀死读循环。
- **握手**：Agent 出示 32 字节 nonce；服务端常数时间比较并以 1 字节 ACK 应答。未配置 nonce、nonce 错误、**以及完全无应答** → 拒绝/重试（对端断线是重试，绝不是"成功"）。握手读取有 5 秒超时，静默客户端不可能卡死单实例管道。
- **nonce 投递走带外**：写入 `%PROGRAMDATA%\AkiSpace\nonce_*.bin`，DACL 在字节落盘**之前**构建（owner + SYSTEM + 分身账户的读/删权限），由 Agent 读取后删除，启动与关机各清扫一次。nonce 绝不出现在命令行（argv 会泄漏进计划任务 XML 与进程 PEB）。
- **管道 DACL**：主用户 + SYSTEM；标准 RDP 模式追加分身账户 SID 以便跨用户 Agent 连接。ACL 构建失败回退 `PipeOptions.CurrentUserOnly`（fail-closed），绝不回退开放 DACL。

## 6　环境检查与一键解锁（`Services/`）

- 11 项检查（系统版本、fDenyTSConnections、fSingleSessionPerUser、解锁层、StartRCM、TermService、防火墙、子会话、监听探测、termsrv 版本、Wrapper hook）**在工作线程执行**；每项结果带稳定的机器可读 `Id`（不参与本地化）与本地化的名称/详情。
- **结论卡片**把原始检查翻译成用户真正关心的问题——本机哪些分身模式可用。家庭版未装解锁层时，明确告知标准 RDP 不可用（子会话不受影响）并提供一键安装。
- **一键安装**：从 sergiye/rdpWrapper 官方 GitHub 发布下载钉定版本（v2.15 x64），按发布时记录的 SHA-256 逐字节校验（不符即中止，不执行任何内容），随后提权运行 `-install -offline`——上游安装器自行完成 TermWrap 安装与 TermService 重启及 Defender 目录排除。解锁层与子会话互斥，安装前界面会明确提示。
- 防火墙策略是 RDP 端口上**一条** `remoteip=any` 的入站阻断规则：Windows 防火墙不检查回环流量，因此回环 RDP 照常工作而所有真实远端被拦；默认公开允许规则会被删除以免干扰。

## 7　设置、主题与语言

- `%APPDATA%\AkiSpace\settings.json`——原子写入（临时文件 + 改名），损坏时以默认值加载**并立即自愈重写**。分身账户密码用 DPAPI（`CurrentUser`）加密并带版本前缀；旧格式的明文密码在下次保存时迁移；内存快照永远不会持有加密后的团块。
- **Aurora Glass**：四套自包含配色包（两深两浅），定义为令牌表——底色/文字/强调三色组、一条三色渐变坡、每包独立的极光强度、玻璃/液态材质令牌。背景为四层呼吸光晕 + 公转扫光 + 星尘 + 噪点，全部尊重系统「动画控件」设置。
- **本地化**是静态双目录表（200+ 键，默认英文）；XAML 通过 `{loc:Loc Key}` 绑定到索引器视图，切换语言只触发一次 `Item[]` 变更即可实时重绘所有已打开窗口。两份目录的键位一致性与占位符一致性均有测试断言。
- WPF 与 WinForms interop 共存于同一 STA 线程；空域规则（airspace）按设计规避——hwnd 承载的内容（RDP 查看器）永远盖在 WPF 之上，因此会话建立期间的状态提示有意放在状态栏。

## 8　进程生命周期

- **单实例**：命名 `Global\` 互斥体（双开会竞争 settings.json 与管道名）。
- 运行模式：GUI（默认）、`--fix-env [--disable-wrapper]`（提权控制台，一次 UAC，输出本地化）、`--agent --nonce-file <path>`（无界面回放）。
- 文件日志写入 `%LOCALAPPDATA%\AkiSpace\logs\akspace-YYYYMMDD.log`：单一流式写入器逐行落盘、过期日志按 14 天保留期清理、释放竞争安全。
- 关闭顺序：状态计时器 → 控制器拆除（游戏鼠标关闭、宿主释放、用户主动关闭时注销子会话、nonce 清扫）→ 热键 → 托盘 → Dispatcher 关闭，全程在 3 秒看门狗之下。

## 9　测试

- **74 项单元/集成测试**（`tests/AkiSpace.Tests`）：IPC 帧边界（截断、超长、负长度、nonce 校验）、设置并发 + 损坏自愈 + DPAPI 往返 + 明文迁移、双语目录键位**与占位符对齐**、全部 XAML/代码引用键存在性（拼错键名是构建失败而不是界面显示键名）、日志滚动、调色板完整性、钉定下载常量。
- **SelfTest**（`tools/AkiSpace.SelfTest`）：纯逻辑断言 + 交互式 UI 冒烟（环境检查窗口完整渲染）+ 可选 E2E 连接，CI/无头环境自动跳过。
- **端到端 UIA 场景**在真实桌面驱动真实应用：全新启动、多实例守护、设置保存/重载往返、标准 RDP 密码门、子会话连接 → **真实截屏→剪贴板位图验证** → 断开无误弹、损坏设置恢复、最大化/还原布局、原始输入注册回归。
