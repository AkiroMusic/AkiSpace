# click-dialogs-wpf.ps1 — verify the WPF shell's dialog wiring via UI Automation.
# Invokes the 设置 / 环境检查 buttons on the main window, screenshots the modal
# dialogs, closes them, and reports PASS/FAIL.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Cap {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[void][Cap]::SetProcessDpiAwarenessContext([IntPtr](-4))

$T_SETTINGS = [string][char]0x8BBE + [char]0x7F6E
$T_SETUP = [string][char]0x73AF + [char]0x5883 + [char]0x68C0 + [char]0x67E5
$T_CLOSE = [string][char]0x5173 + [char]0x95ED
$T_CANCEL = [string][char]0x53D6 + [char]0x6D88

function Save-Shot([IntPtr]$hwnd, [string]$path) {
    $r = New-Object Cap+RECT
    [void][Cap]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][Cap]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $true
}

$proc = Get-Process AkiSpace -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $main) { Write-Host "FAIL main window not found (pid $($proc.Id))"; exit 1 }
Write-Host ("main: " + $main.Current.Name)

$out = Join-Path $env:TEMP "p3-dialogs"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Invoke-Button($parent, [string]$name) {
    $btnCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $btn = $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    if ($null -eq $btn) { return $false }
    $invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    return $true
}

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static IntPtr TopByTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.ToString() == title) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
function Find-TopWindowByTitle([string]$title) {
    $h = [Win]::TopByTitle([uint32]$proc.Id, $title)
    if ($h -eq [IntPtr]::Zero) { return $null }
    return @{ Handle = $h }
}

# ---- settings ----
if (-not (Invoke-Button $main $T_SETTINGS)) { Write-Host "FAIL settings button not found"; exit 1 }
Start-Sleep -Seconds 3
$dlg = Find-TopWindowByTitle $T_SETTINGS
if ($null -eq $dlg) { Write-Host "FAIL settings dialog did not open"; exit 1 }
$h = [IntPtr]$dlg.Handle
$ok = Save-Shot $h (Join-Path $out "settings.png")
Write-Host "PASS settings dialog open (shot=$ok)"
[void][Cap]::PostMessage($h, 0x0100, [IntPtr]27, [IntPtr]0)  # Esc (IsCancel closes)
Start-Sleep -Seconds 2

# ---- setup (env check) ----
if (-not (Invoke-Button $main $T_SETUP)) { Write-Host "FAIL setup button not found"; exit 1 }
Start-Sleep -Seconds 9
$T_SETUP_TITLE = $T_SETUP + " / " + [string][char]0x4FEE + [char]0x590D + " — " + [string][char]0x684C + [string][char]0x9762 + [string][char]0x5206 + [string][char]0x8EAB + [string][char]0x524D + [string][char]0x7F6E + [string][char]0x6761 + [string][char]0x4EF6
$dlg2 = Find-TopWindowByTitle $T_SETUP_TITLE
if ($null -eq $dlg2) { Write-Host "FAIL setup dialog did not open"; exit 1 }
$h2 = [IntPtr]$dlg2.Handle
$ok2 = Save-Shot $h2 (Join-Path $out "setup.png")
Write-Host "PASS setup dialog open (shot=$ok2)"
[void][Cap]::PostMessage($h2, 0x0100, [IntPtr]27, [IntPtr]0)
Start-Sleep -Seconds 1
Write-Host "done"
