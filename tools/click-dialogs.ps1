# click-dialogs.ps1 — smoke-test the refactored WinForms shell's dialog wiring.
# Finds the main window by PID, clicks the dialog buttons via BM_CLICK,
# screenshots the modal dialog, closes it, and reports PASS/FAIL per dialog.
param(
    [int]$PidToUse = 0
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    public static IntPtr MainOf(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.Length > 0) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr ChildByText(IntPtr parent, string clsPrefix, string text) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var cn = new StringBuilder(256); GetClassName(h, cn, 256);
            var tx = new StringBuilder(256); GetWindowText(h, tx, 256);
            if (cn.ToString().StartsWith(clsPrefix) && tx.ToString() == text) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr TopByTitle(string t) { return FindWindow(null, t); }
}
'@
[void][W]::SetProcessDpiAwarenessContext([IntPtr](-4))

# Chinese strings built from code points to survive shell encoding
$T_SETTINGS = [string][char]0x8BBE + [char]0x7F6E                  # 设置
$T_SETUP_PREFIX = [string][char]0x73AF + [char]0x5883 + [char]0x68C0 + [char]0x67E5  # 环境检查...
$T_BTN_SETTINGS = $T_SETTINGS
$T_BTN_SETUP = [string][char]0x73AF + [char]0x5883 + [char]0x68C0 + [char]0x67E5      # 环境检查
$T_CLOSE = [string][char]0x5173 + [char]0x95ED                        # 关闭

function Save-Shot([IntPtr]$hwnd, [string]$path) {
    $r = New-Object W+RECT
    [void][W]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][W]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $true
}

$proc = if ($PidToUse -gt 0) { Get-Process -Id $PidToUse -ErrorAction Stop } else { Get-Process AkiSpace -ErrorAction Stop | Select-Object -First 1 }
$main = [W]::MainOf([uint32]$proc.Id)
if ($main -eq [IntPtr]::Zero) { Write-Host "FAIL main window not found"; exit 1 }
Write-Host "main hwnd: $main"
$out = Join-Path $env:TEMP "p2-dialogs"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---- Settings dialog ----
$btn = [W]::ChildByText($main, "WindowsForms10.Button", $T_BTN_SETTINGS)
if ($btn -eq [IntPtr]::Zero) { Write-Host "FAIL settings button not found"; exit 1 }
[void][W]::PostMessage($btn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Seconds 3
$dlg = [W]::TopByTitle($T_SETTINGS)
if ($dlg -eq [IntPtr]::Zero) { Write-Host "FAIL settings dialog did not open"; exit 1 }
$ok = Save-Shot $dlg (Join-Path $out "settings.png")
Write-Host "PASS settings dialog open (shot=$ok)"
# close via Cancel button (取消)
$T_CANCEL = [string][char]0x53D6 + [char]0x6D88
$cancel = [W]::ChildByText($dlg, "WindowsForms10.Button", $T_CANCEL)
if ($cancel -ne [IntPtr]::Zero) { [void][W]::PostMessage($cancel, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
Start-Sleep -Seconds 2

# ---- Setup (env check) dialog ----
$btn2 = [W]::ChildByText($main, "WindowsForms10.Button", $T_BTN_SETUP)
if ($btn2 -eq [IntPtr]::Zero) { Write-Host "FAIL setup button not found"; exit 1 }
[void][W]::PostMessage($btn2, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Seconds 6
# title is 环境检查 / 修复 — 桌面分身前置条件; find any visible top window of same process that is not main
$setupHwnd = [IntPtr]::Zero
$cb = [W+EnumProc]{
    param($h, $l)
    $p = [uint32]0
    [void][W]::GetWindowThreadProcessId($h, [ref]$p)
    if ($p -eq [uint32]$proc.Id -and $h -ne $main -and [W]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][W]::GetWindowText($h, $sb, 256)
        if ($sb.ToString().StartsWith($T_SETUP_PREFIX)) { $script:setupHwnd = $h; return $false }
    }
    return $true
}
[void][W]::EnumWindows($cb, [IntPtr]::Zero)
if ($setupHwnd -eq [IntPtr]::Zero) { Write-Host "FAIL setup dialog did not open"; exit 1 }
Start-Sleep -Seconds 4  # let checks finish
$ok2 = Save-Shot $setupHwnd (Join-Path $out "setup.png")
Write-Host "PASS setup dialog open (shot=$ok2)"
# close via 关闭 button
$closeBtn = [W]::ChildByText($setupHwnd, "WindowsForms10.Button", $T_CLOSE)
if ($closeBtn -ne [IntPtr]::Zero) { [void][W]::PostMessage($closeBtn, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
Start-Sleep -Seconds 2
Write-Host "done"
