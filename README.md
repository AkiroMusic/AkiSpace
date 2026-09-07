# AkiSpace — Desktop Clone

Create a second independent desktop session ("Desktop Clone") on a single Windows machine. Run full-screen apps/games/automation in the clone while your main desktop keeps working normally.

**Architecture**: Windows **Multi-session RDP** (unlocked via RDP Wrapper/TermWrap) + embedded MSTSC ActiveX viewer + cross-session input forwarding. Supports two connection modes:
- **Standard RDP**: Uses a separate local account (e.g., AkiSpaceUser) over 127.0.0.1 to establish a second RDP interactive session
- **Child Session**: Uses the current account to create a Windows child session, identical to BetterGI's desktop clone technique

---

## Features

- **One-click Clone**: Click "Connect" or press `Ctrl+Shift+D` to instantly connect; embedded window shows the clone desktop live
- **Dual Connection Modes**: Switch between "Standard RDP" (separate account) and "Child Session" (BetterGI-style, same user) in Settings
- **System Tray**: Minimize to tray; double-click tray icon to restore; right-click menu for connect/disconnect/quit
- **Global Hotkeys**:
  - `Ctrl+Shift+D`: Toggle connect/disconnect
  - `Ctrl+Alt+Space`: Show/restore main window
- **Game Mouse Mode**: Capture host relative mouse motion → forward to clone session; auto clip & hide cursor (ideal for FPS / relative-camera games)
- **Alt Release**: Hold Alt to temporarily release cursor back to host desktop
- **Launch in Clone**: Launch programs inside the clone session via Task Scheduler with admin rights
- **Environment Check/Repair**: One-click detection & repair for RDP status, multi-session, RDP Wrapper, StartRCM, firewall, TermService, etc.
- **Performance Monitor**: Status bar shows real-time CPU & memory usage
- **Settings Persistence**: `%APPDATA%\AkiSpace\settings.json` (resolution, port, clone account, smart scaling, hotkeys, etc.)

---

## Requirements

- Windows 10/11 (**Home edition requires RDP Wrapper** — see below)
- .NET 8 Desktop Runtime (or .NET 8 SDK installed)
- Admin rights only for "One-click Fix" (manifest is `asInvoker`; UAC elevation on demand for registry/firewall/service ops)
- **One local account with password** for clone session (RDP forbids password-less remote logon)

---

## Quick Start

1. **Build**:
   ```
   dotnet build src/AkiSpace/AkiSpace.csproj
   ```
   Output: `src/AkiSpace/bin/Debug/net8.0-windows/AkiSpace.exe`

2. **Unlock Multi-Session (Home edition required)**: See below.

3. **Create Clone Account** (Standard RDP mode needs a password-protected local account; Child Session doesn't):
   ```powershell
   net user AkiSpaceUser <your-password> /add
   net localgroup Administrators AkiSpaceUser /add
   ```
   Then enter this username/password in AkiSpace Settings.

4. **Connect**: Click "Connect" or press `Ctrl+Shift+D` → embedded window shows clone desktop → click "Game Mouse" to enable mouse forwarding → click "Launch Program" to run apps in the clone.

5. **Quick Actions**:
   - Minimize → auto-hides to tray
   - Double-click tray icon → restore window
   - Right-click tray → Connect/Disconnect/Quit
   - `Ctrl+Shift+D` → toggle connect/disconnect anywhere

---

## Unlock Multi-Session (Home Edition / RDP Disabled)

Windows Home lacks RDP host & multi-session support. AkiSpace's "One-Click Fix" handles registry/service/firewall, but **RDP Unlock Layer must be installed manually**:

### 1. Download RDP Wrapper

Original `stascorp/rdpwrap` is unmaintained. Use active community forks:

- **sergiye/rdpWrapper** (C#, recommended, doesn't patch termsrv.dll): https://github.com/sergiye/rdpWrapper
- or **sebaxakerhtc/rdpwrap** (Delphi fork + latest rdpwrap.ini): https://github.com/sebaxakerhtc/rdpwrap

> **Critical**: **rdpwrap.ini MUST match your termsrv.dll version**. AkiSpace's env check shows current version (e.g., `10.0.26100.8875` — note OS reports 26200 but termsrv.dll is 26100.x family). Find matching `[10.0.26100.xxxx]` section in the repo's `rdpwrap.ini`.

### 2. Install

Run as Admin: `rdpWrapper.exe -install` (sergiye version, uses TermWrap, Windows Update immune). Verify `TermService` `ServiceDll` points to `C:\Program Files\RDP Wrapper\TermWrap.dll`.

### 3. Apply Registry Settings

In AkiSpace: "Environment Check/Repair" → "One-Click Fix", or manually:

```powershell
# Enable RDP
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name 'fDenyTSConnections' -Value 0 -Type DWord
# Allow Multi-Session
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name 'fSingleSessionPerUser' -Value 0 -Type DWord
# Home Edition Fix: Enable RCM (else RDP listener won't create)
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'StartRCM' -Value 1 -Type DWord
# Restart Service
Restart-Service TermService -Force
```

### 4. Verify

Back in AkiSpace → "Environment Check/Repair" → "Recheck". All should show ✓:
- RDP Enabled
- Multi-Session Allowed
- Multi-Session Unlock (TermWrap/rdpwrap)
- StartRCM
- RDP Listener Port

---

## Security Notes

AkiSpace's "One-Click Fix" adds a **firewall loopback rule**: only `127.0.0.1` may reach port 3389; external networks blocked. Both Standard RDP and Child Session use this port. Recommended:

```powershell
# Ensure only loopback rule exists (remove default public RDP rule if present)
Remove-NetFirewallRule -DisplayName "Remote Desktop - User Mode (TCP-In)" -ErrorAction SilentlyContinue
```

> ⚠️ **Windows Update Risk**: Though TermWrap doesn't patch termsrv.dll directly, major updates may still affect compatibility. Re-run Environment Check after updates.

---

## Architecture

```
Main Desktop (Session 1, Your Account)                  Clone Session (Session 2, AkiSpaceUser)
┌────────────────────────────┐                ┌──────────────────────────┐
│ AkiSpace Main Window       │                │  Target App (Fullscreen/ │
│  ├─ RdpActiveXHost         │                │                          │
│  │   (MsRdpClient11 Embed)  │◄─RDP 127.0.0.1►│  Independent Desktop     │
│  ├─ RawInputMonitor (STA)  │                │                          │
│  │   └─ WM_INPUT Relative  │                │  Mouse Replay (SendInput) │
│  └─ MouseForwarder         │                │                          │
│      └─ Accumulate+10ms Batch│──Named Pipe──►│                          │
│      └─ ClipCursor+Hide    │                │                          │
│      └─ Alt Release        │                │                          │
└────────────────────────────┘                └──────────────────────────┘
```

| Component | File | Responsibility |
|-----------|------|----------------|
| Session Mgmt | `Native/WtsApi.cs` | WTS session query/listen status |
| Input Interop | `Native/User32.cs` | Raw Input, SendInput, ClipCursor, SetCursorPos, GUI thread info |
| RDP Viewer | `Controls/RdpActiveXHost.cs` | AxHost wraps MsRdpClient11, dual-mode connect (StdRDP/ChildSession), retry logic |
| IPC | `Ipc/Pipes.cs`, `Ipc/IpcProtocol.cs` | Named Pipes + Binary Frame Protocol |
| Mouse Capture | `Input/RawInputMonitor.cs` | Dedicated STA thread, RIDEV_INPUTSINK |
| Mouse Forward | `Input/MouseForwarder.cs` | Accumulate/direction-reverse flush/10ms batch/Alt release |
| Cursor Mgmt | `Input/CursorCapture.cs` | ClipCursor + ShowCursor(false) paired restore |
| Keyboard | `Input/KeyboardHandler.cs` | Input Capture Window focus + SendKeys |
| Elevated Launch | `Services/ProcessLauncher.cs` | Task Scheduler COM (TASK_RUN_USE_SESSION_ID) |
| Env Check | `Services/EnvironmentVerifier.cs` | 9 prerequisite checks/fixes |

---

## Known Limitations

- **GPU Rendering**: 3D apps in RDP use WARP software rasterization (RDP doesn't support GPU passthrough). High-end 3D games unsuitable; "force-fullscreen ordinary apps/automation" works fine.
- **First Login**: Standard RDP mode: new account first connect shows Windows OOBE — complete in clone session (keystrokes forwarded). Child Session: no such issue.
- **Relative Mouse**: Only when clone window focused & Alt not held; RDP lacks relative mouse, forwarding is custom pipe.
- **Anti-Cheat**: Some games detect multi-session or RDP environments.
- **Child Session Compatibility**: Depends on Windows version & RDP Wrapper state; some configs unsupported. Fall back to Standard RDP mode if issues.

---

## Development

```
AkiSpace.sln
src/AkiSpace/          Main App (WinForms, net8.0-windows)
  Native/              P/Invoke + COM Interfaces
  Services/            Session Mgmt/Settings/Env Check/Process Launch
  Ipc/                 Named Pipes + Binary Frame Protocol
  Input/               Mouse/Keyboard/Cursor
  Controls/            RDP ActiveX Host
  Forms/               Main Window + Dialogs
tools/AkiSpace.SelfTest/   Self-Test (Protocol/Settings/Env Check/UI Smoke/E2E Connect)
```

Run Self-Test: `dotnet run --project tools/AkiSpace.SelfTest`

---

## 中文版

---

# AkiSpace — 桌面分身

在同一台 Windows 电脑上创建第二个独立会话（桌面），在其中运行全屏程序与自动化脚本，主桌面继续正常工作。

**架构**：Windows **多会话 RDP**（经 RDP Wrapper/TermWrap 解锁家庭版限制）+ 内嵌 MSTSC ActiveX 查看器 + 跨会话输入转发。支持两种连接模式：
- **标准 RDP**：用独立本地账户（如 AkiSpaceUser）通过 127.0.0.1 建立第二个 RDP 交互会话
- **子会话（ChildSession）**：用当前账户创建 Windows 子会话，与 BetterGI 桌面分身效果相同

---

## 功能

- **一键连接分身**：点击「连接」或按 `Ctrl+Shift+D` 立即连接，内嵌窗口实时显示分身桌面
- **双连接模式**：支持「标准 RDP」（独立账户）和「子会话」（BetterGI 同款，当前账户）两种模式，在设置中切换
- **系统托盘**：最小化到系统托盘，双击托盘图标快速恢复，右键菜单支持连接/断开/退出
- **全局热键**：
  - `Ctrl+Shift+D`：切换连接/断开分身
  - `Ctrl+Alt+Space`：显示/恢复主窗口
- **游戏鼠标模式**：捕获主桌面相对鼠标移动 → 转发到分身会话，光标自动裁剪并隐藏（适合 FPS/需要相对视角的游戏）
- **Alt 键释放**：按住 Alt 临时释放光标，回到主桌面操作
- **在分身中启动**：通过 Task Scheduler 以管理员权限在分身会话中启动程序
- **环境检查/修复**：一键检测 RDP 状态、多会话、RDP Wrapper、StartRCM、防火墙、TermService 等前置条件
- **性能监控**：状态栏实时显示 CPU 和内存使用率
- **设置持久化**：`%APPDATA%\AkiSpace\settings.json`（分辨率、端口、分身账户、智能缩放、热键等）

---

## 系统要求

- Windows 10/11（**家庭版需要 RDP Wrapper 解锁**，见下文）
- .NET 8 Desktop Runtime（或已安装 .NET 8 SDK）
- 管理员权限仅在「一键修复」时需要（应用清单为 `asInvoker`，点击修复时通过 UAC 动态提权执行注册表/防火墙/服务操作）
- **一个带密码的本地账户**用于分身会话（RDP 不允许无密码账户远程登录）

---

## 快速开始

1. **构建**：
   ```
   dotnet build src/AkiSpace/AkiSpace.csproj
   ```
   产物：`src/AkiSpace/bin/Debug/net8.0-windows/AkiSpace.exe`

2. **解锁多会话（家庭版必需）**：见下节。

3. **创建分身账户**（标准 RDP 模式需要带密码的本地账户，子会话模式不需要）：
   ```powershell
   net user AkiSpaceUser <你的密码> /add
   net localgroup Administrators AkiSpaceUser /add
   ```
   然后在 AkiSpace「设置」里填入该账户的用户名/密码。

4. **连接**：点击「连接」或按 `Ctrl+Shift+D` → 内嵌窗口显示分身桌面 → 点击「游戏鼠标」开启鼠标转发 → 点击「启动程序」选择要运行的程序。

5. **快捷操作**：
   - 最小化窗口会自动隐藏到系统托盘
   - 双击托盘图标可快速恢复窗口
   - 右键托盘图标可连接/断开/退出
   - 按 `Ctrl+Shift+D` 可在任何地方快速切换连接状态

---

## 解锁多会话（家庭版 / RDP 未启用时）

Windows 家庭版不提供 RDP 主机与多会话支持。AkiSpace 的「一键修复」会处理注册表/服务/防火墙，但 **RDP 解锁层需要手动安装**：

### 1. 下载 RDP Wrapper

原版 `stascorp/rdpwrap` 已停止维护，不支持新构建。使用活跃维护的社区方案：

- **sergiye/rdpWrapper**（C# 实现，推荐，不修改 termsrv.dll）：https://github.com/sergiye/rdpWrapper
- 或 **sebaxakerhtc/rdpwrap**（Delphi fork + 最新 rdpwrap.ini）：https://github.com/sebaxakerhtc/rdpwrap

> 关键：**rdpwrap.ini 必须匹配你的 termsrv.dll 版本**。AkiSpace 的环境检查会显示当前版本（例如 `10.0.26100.8875`——注意系统报告 26200 但 termsrv.dll 版本是 26100.x 家族）。从上述仓库的 `rdpwrap.ini` 中找到对应 `[10.0.26100.xxxx]` 段落即为支持。

### 2. 安装

以管理员运行 `rdpWrapper.exe -install`（sergiye 版，使用 TermWrap，对 Windows 更新免疫），确认 `TermService` 的 `ServiceDll` 指向 `C:\Program Files\RDP Wrapper\TermWrap.dll`。

### 3. 应用注册表设置

在 AkiSpace 中点击「环境检查/修复」→「一键修复」，或手动执行：

```powershell
# 启用 RDP
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name 'fDenyTSConnections' -Value 0 -Type DWord
# 允许多会话
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name 'fSingleSessionPerUser' -Value 0 -Type DWord
# 家庭版修复：启用 RCM（否则 RDP 监听器不创建）
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -Name 'StartRCM' -Value 1 -Type DWord
# 重启服务
Restart-Service TermService -Force
```

### 4. 验证

回到 AkiSpace →「环境检查/修复」→「重新检查」。以下应为 ✓ 通过：
- RDP 已启用
- 允许多会话
- 多会话解锁 (TermWrap/rdpwrap)
- StartRCM
- RDP 监听端口

---

## 安全建议

AkiSpace 的「一键修复」会添加**防火墙回环规则**：仅允许 `127.0.0.1` 访问 3389 端口，外部网络无法连接。标准 RDP 模式和子会话模式都通过此端口连接。建议同时：

```powershell
# 确认只有回环规则（删除默认的公开 RDP 规则，若有）
Remove-NetFirewallRule -DisplayName "Remote Desktop - User Mode (TCP-In)" -ErrorAction SilentlyContinue
```

> ⚠️ **Windows 更新风险**：虽然 TermWrap 不直接修改 termsrv.dll，但重大更新仍可能影响兼容性。更新后重新运行环境检查即可发现并修复。

---

## 技术架构

```
主桌面 (Session 1, 你的账户)                  分身会话 (Session 2, AkiSpaceUser)
┌────────────────────────────┐                ┌──────────────────────────┐
│ AkiSpace 主窗口            │                │  目标程序 (全屏/自动化)   │
│  ├─ RdpActiveXHost         │                │                          │
│  │   (MsRdpClient11 内嵌)  │◄─RDP 127.0.0.1►│  独立桌面                 │
│  ├─ RawInputMonitor (STA)  │                │                          │
│  │   └─ WM_INPUT 相对增量  │                │                          │
│  └─ MouseForwarder         │                │  鼠标重放 (SendInput)     │
│      └─ 累积+10ms 批处理    │──Named Pipe──►│                          │
│      └─ ClipCursor+隐藏    │                │                          │
│      └─ Alt 释放           │                │                          │
└────────────────────────────┘                └──────────────────────────┘
```

| 组件 | 文件 | 职责 |
|------|------|------|
| 会话管理 | `Native/WtsApi.cs` | WTS 会话查询/监听状态 |
| 输入互操作 | `Native/User32.cs` | Raw Input, SendInput, ClipCursor, SetCursorPos, GUI 线程信息 |
| RDP 查看器 | `Controls/RdpActiveXHost.cs` | AxHost 封装 MsRdpClient11, 双模式连接（标准RDP/子会话）, 重试连接 |
| IPC | `Ipc/Pipes.cs`, `Ipc/IpcProtocol.cs` | 命名管道 + 二进制帧协议 |
| 鼠标捕获 | `Input/RawInputMonitor.cs` | 独立 STA 线程, RIDEV_INPUTSINK |
| 鼠标转发 | `Input/MouseForwarder.cs` | 累积/方向反转即刷/10ms 批处理/Alt 释放 |
| 光标管理 | `Input/CursorCapture.cs` | ClipCursor + ShowCursor(false) 配对恢复 |
| 键盘 | `Input/KeyboardHandler.cs` | Input Capture Window 焦点 + SendKeys |
| 提权启动 | `Services/ProcessLauncher.cs` | Task Scheduler COM (TASK_RUN_USE_SESSION_ID) |
| 环境检查 | `Services/EnvironmentVerifier.cs` | 9 项前置条件检测/修复 |

---

## 已知限制

- **GPU 渲染**：RDP 会话内的 3D 应用使用 WARP 软件渲染（RDP 协议不支持 GPU 透传）。对画面流畅度要求极高的 3D 游戏不适用；对"需要强制全屏的普通程序/自动化"完全可用。
- **首次登录**：标准 RDP 模式下新账户首次连接会显示 Windows 设置向导，需在分身会话中完成（键盘操作会转发过去）。子会话模式无此问题。
- **相对鼠标**：仅当分身窗口持有焦点且未按 Alt 时转发；RDP 本身不支持相对鼠标，转发链路为自定义管道。
- **反作弊检测**：部分游戏会检测多开或 RDP 环境。
- **子会话兼容性**：子会话模式（ConnectToChildSession）依赖 Windows 版本和 RDP Wrapper 状态，某些系统配置下可能不可用。遇到问题可切换到标准 RDP 模式。

---

## 开发

```
AkiSpace.sln
src/AkiSpace/          主程序 (WinForms, net8.0-windows)
  Native/              P/Invoke + COM 接口
  Services/            会话管理/设置/环境检查/进程启动
  Ipc/                 命名管道协议
  Input/               鼠标/键盘/光标
  Controls/            RDP ActiveX 宿主
  Forms/               主窗口 + 对话框
tools/AkiSpace.SelfTest/   自检程序（协议/设置/环境检测/UI 冒烟/E2E 连接）
```

运行自检：`dotnet run --project tools/AkiSpace.SelfTest`
EOF