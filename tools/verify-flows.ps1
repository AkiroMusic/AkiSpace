# verify-flows.ps1 — connect state machine + settings save flow via UIA.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Cap {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
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
[void][Cap]::SetProcessDpiAwarenessContext([IntPtr](-4))
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

$T_SETTINGS = [string][char]0x8BBE + [char]0x7F6E
$T_SAVE = [string][char]0x4FDD + [char]0x5B58
$T_CONNECT = [string][char]0x8FDE + [char]0x63A5
$T_DISCONNECT = [string][char]0x65AD + [char]0x5F00

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
if ($null -eq $main) { Write-Host "FAIL main window"; exit 1 }
$mainH = [Win]::TopByTitle([uint32]$proc.Id, $main.Current.Name)
if ($mainH -eq [IntPtr]::Zero) { Write-Host "FAIL main hwnd"; exit 1 }
$out = Join-Path $env:TEMP "p3-flows"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Invoke-Button($parent, [string]$name) {
    $btnCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $btn = $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    if ($null -eq $btn) { return $false }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

# 1. click 连接 (child-session mode → connects directly, listener may be down → pending)
if (-not (Invoke-Button $main $T_CONNECT)) { Write-Host "FAIL connect button"; exit 1 }
Start-Sleep -Seconds 5
$ok1 = Save-Shot $mainH (Join-Path $out "connecting.png")
Write-Host "shot connecting=$ok1"

# 2. click 断开
if (-not (Invoke-Button $main $T_DISCONNECT)) { Write-Host "FAIL disconnect button"; exit 1 }
Start-Sleep -Seconds 2
$ok2 = Save-Shot $mainH (Join-Path $out "disconnected.png")
Write-Host "shot disconnected=$ok2"

# 3. settings save flow
if (-not (Invoke-Button $main $T_SETTINGS)) { Write-Host "FAIL settings button"; exit 1 }
Start-Sleep -Seconds 3
$dlgH = [Win]::TopByTitle([uint32]$proc.Id, $T_SETTINGS)
if ($dlgH -eq [IntPtr]::Zero) { Write-Host "FAIL settings dialog"; exit 1 }
$dlg = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
# find the settings window element by handle among children
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$dlgEl = $null
foreach ($w in $all) { if ([IntPtr]$w.Current.NativeWindowHandle -eq $dlgH) { $dlgEl = $w; break } }
if ($null -eq $dlgEl) { Write-Host "FAIL settings element"; exit 1 }
if (-not (Invoke-Button $dlgEl $T_SAVE)) { Write-Host "FAIL save button"; exit 1 }
Start-Sleep -Seconds 2
$gone = [Win]::TopByTitle([uint32]$proc.Id, $T_SETTINGS)
Write-Host ("settings closed after save: " + ($gone -eq [IntPtr]::Zero))
Write-Host "done"
