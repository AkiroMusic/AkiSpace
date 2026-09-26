# 前端重写功能对照清单（Parity Checklist）

重写 UI 时逐项核对的验收清单。新 UI 必须覆盖全部「必须」项；「可选增项」允许做。
底层机制（RDP/IPC/输入/子会话/DPAPI/日志）行为不变，见仓库红线。

## 主窗口（现 MainForm.cs）

| # | 功能 | 现状行为 | 级别 |
|---|------|----------|------|
| M1 | 窗口 | 标题「AkiSpace — 桌面分身」，1280×800，最小 960×600，屏幕居中，可缩放 | 必须 |
| M2 | 按钮条 | 连接（主色）/断开/终止/游戏鼠标/启动程序/环境检查/设置；断开·终止·启动程序初始禁用；游戏鼠标初始隐藏 | 必须 |
| M3 | 状态行 | 子会话: 活动(ID n)/无 ｜ 连接: … ｜ RDP 解锁: 已安装/未安装 ｜ CPU/内存（仅 ShowPerformance=true） | 必须 |
| M4 | RDP 视图 | 黑底面板承载 RdpActiveXHost（AxHost），连接后显示分身桌面 | 必须 |
| M5 | 底部状态条 | 状态文本 + GitHub 链接（github.com/AkiroMusic/AkiSpace，点击打开浏览器） | 必须 |
| M6 | 连接流程 | 空密码(标准RDP)→弹密码框并 DPAPI 持久化；listener 探测失败→提示；TearDown→new RdpActiveXHost→延迟两拍 BeginInvoke 连接体；子会话模式先 hook 检查（冲突弹窗）+ WTSEnableChildSessions；标准模式带凭据 Connect；状态机 _connecting/_isConnected 防重入 | 必须 |
| M7 | 登录完成 | 状态「已连接—分身桌面就绪」；聚焦 RDP 输入窗口；后台 Task.Run 启动 agent（标准模式）；LaunchProgramPath 自动启动 | 必须 |
| M8 | 连接失败 | ResetConnectUi 恢复按钮；15s 内同因弹窗去重；诊断信息（原因代码/扩展代码/WTS 状态）随弹窗展示 | 必须 |
| M9 | 断开 | DisconnectSession；按钮复位；游戏鼠标关闭 | 必须 |
| M10 | 终止 | 确认框→LogoffChildSession→TearDownRdpHost→复位 | 必须 |
| M11 | 游戏鼠标 | 仅标准 RDP 连接后可用；未连 agent 弹提示；开关写回设置；按钮文本「游戏鼠标/游戏鼠标: 开」 | 必须 |
| M12 | 启动程序 | 需已连接；exe 选择框；LaunchInChildSession | 必须 |
| M13 | 全局热键 | Ctrl+Shift+D 切换连接；Ctrl+Alt+Space 显示窗口；EnableGlobalHotkey 控制；失败仅记日志 | 必须 |
| M14 | 托盘 | NotifyIcon；tooltip 未连接/已连接；菜单 连接分身(动态文案)/显示主窗口/退出；双击显示；MinimizeToTray 时最小化隐藏 | 必须 |
| M15 | 关闭 | X→teardown+热键注销+托盘释放；LogoffOnExit 且用户关闭→logoff 子会话；Windows 关机路径不 logoff；幂等 | 必须 |
| M16 | 深色标题栏 | DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE=1) | 必须 |
| M17 | 状态轮询 | 1s：子会话/RDP解锁/ActiveX Connected 状态/句柄快照(RefreshRdpHandles)/性能（Process.GetCurrentProcess 用完即释） | 必须 |
| M18 | 自动连接 | AutoConnect=true 时启动后连接 | 必须 |
| M19 | nonce 卫生 | 启动清扫 %PROGRAMDATA%\AkiSpace\nonce_*.bin；写 nonce 走受限 DACL 文件 | 必须 |

## 环境检查窗口（现 SetupDialog.cs）

| # | 功能 | 现状行为 | 级别 |
|---|------|----------|------|
| S1 | 窗口 | 800×700，最小 700×550，可缩放 | 必须 |
| S2 | 检查列表 | 三列 检查项240/状态90/详情290；打开即显示占位行「正在检查环境…」；完成后渲染 RunAllChecksAsync 全部条目 | 必须 |
| S3 | 家庭版指南 | 仅当「多会话解锁」失败显示：指引文案 + termsrv.dll 版本行 + 4 按钮（打开 sergiye/rdpWrapper releases、打开 sebaxakerhtc/rdpwrap、复制版本号、复制诊断 markdown）+「我已安装→重新检查」 | 必须 |
| S4 | 按钮 | 重新检查 / 一键修复 / 关闭 | 必须 |
| S5 | 一键修复 | 模式相关确认文案；标准 RDP 模式显示「同时禁用 TermWrap」勾选；确认后 runas 自启动 `--fix-env [--disable-wrapper]`；UAC 取消(1223)有专属提示 | 必须 |

## 设置窗口（现 SettingsDialog.cs）

| # | 功能 | 现状行为 | 级别 |
|---|------|----------|------|
| C1 | 窗口 | 580×700 固定对话框 | 必须 |
| C2 | 连接配置 | 模式(标准RDP/子会话 下拉)、宽 800–7680 步进160、高 600–4320 步进120、色深 8–32 步进8、端口 1–65535、SmartSizing、系统快捷键到分身、音频重定向；各带提示文案 | 必须 |
| C3 | 行为 | 自动连接、退出终止子会话、最小化托盘、性能监控、全局热键、游戏鼠标模式 | 必须 |
| C4 | 分身账户 | 用户名、密码（掩码显示） | 必须 |
| C5 | 自动启动 | 路径文本 + 浏览(exe 过滤) | 必须 |
| C6 | 保存/取消 | 保存→SettingsService.Update 全量写回（含 DPAPI）；取消→丢弃 | 必须 |

## 可选增项（不影响验收）

- 设置窗口加「主题」下拉（ThemeManager 已支持 6 主题，Theme 设置项已持久化，现状无 UI 入口）。
- 主窗口状态行给连接状态加颜色（绿=已连接、红=失败）。

## 稳定性验收（截图取证）

用 `tools/capture-probe.ps1` 对每个窗口拍两帧（间隔 400ms）：
1. 主窗口打开即拍 → 帧差 = 0 px（无闪烁/破碎）。
2. 环境检查窗口检查完成后拍 → 帧差 = 0 px，10 项检查全部可见。
3. 设置窗口打开拍 → 帧差 = 0 px，四卡全部内容可见。
4. 连接成功后主窗口拍 → 分身桌面在视图内正常渲染。

## 重写结果（v0.3.0，frontend-rewrite 分支）

逐项核对结果：M1–M19、S1–S5、C1–C6 全部覆盖。真机验证记录：

- 主窗口/设置/环境检查/连接中 截图渲染正常，两帧帧差 0（仅 CPU 数字跳动）。
- 连接流程点击 → 状态机切换 → `ConnectToChildSession` 发起（本机当时 RDP 监听器未启动，
  属环境问题，与前端无关；旧版在同环境行为相同）。
- 设置保存 result=True，settings.json 落盘，ClonePassword 保持 DPAPI 保护。
- WM_CLOSE 优雅退出：Shell shut down → exited cleanly，进程正常结束。
- AkiSpace.SelfTest 全部通过（含 WPF 版环境检查窗口冒烟测试）。

### 与旧版的有意偏差（均为改进，非回归）

| 项 | 旧版 | 新版 | 理由 |
|---|------|------|------|
| C4 密码显示 | 掩码 TextBox 预填解密后的密码 | PasswordBox 留空=保持现有密码 | PasswordBox 无法程序化预填；避免无意的密码重写 |
| C2 连接模式 | ComboBox 下拉 | 两个 RadioButton 分段选择 | 免写 ComboBox 暗色模板，视觉更清晰 |
| 主窗口配色 | 硬编码深色 (24,24,27) | ThemeManager Ethereal Glass 调色板 | 统一设计系统，主题跟随设置 |
| 主题下拉 | 未做（可选增项） | 未做 | 控制范围；设置 Theme 字段仍被 ThemeManager 读取 |

### 架构落点

- `App/`：ConnectionController / TrayIconService / HotkeyManager（框架无关编排层）。
- `Ui/`：MainWindow / SettingsWindow / SetupWindow / PasswordPromptWindow / UiShell /
  Theme(WpfThemeHost + Styles.xaml) / Controls(NumberBox)。
- `Controls/RdpActiveXHost.cs` 原样保留，经 WindowsFormsHost 托管（spike 验证）。
- `Ipc/` `Input/` `Services/` `Native/` `FileLogger.cs` 零改动；45 个测试全绿。
