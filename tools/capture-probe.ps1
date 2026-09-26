# capture-probe.ps1 — UI rendering evidence tool for the AkiSpace frontend rewrite.
#
# Captures N composed-screen frames of a target window (by PID or title substring),
# saves them as PNGs and reports a pixel diff between consecutive frames:
#   - 0 changed pixels across two frames => static render, no flicker.
#   - A large changed area => garbling / flicker / repaint storm.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\capture-probe.ps1 -ProcId 12345
#   powershell -ExecutionPolicy Bypass -File tools\capture-probe.ps1 -Title "AkiSpace" -Frames 2 -IntervalMs 400
#   powershell -ExecutionPolicy Bypass -File tools\capture-probe.ps1 -Title "AkiSpace" -OutDir C:\temp\probe
param(
    [int]$ProcId = 0,
    [string]$Title = "",
    [int]$Frames = 2,
    [int]$IntervalMs = 400,
    [string]$OutDir = "",
    [ValidateSet("printwindow", "screen")]
    [string]$Mode = "printwindow"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
# Make THIS process PerMonitorV2-aware so GetWindowRect returns physical pixels
# (a virtualized view would crop PrintWindow output on scaled displays).
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiFix {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
"@
[void][DpiFix]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ProbeWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    public static IntPtr FindByPid(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.Length > 0) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr FindByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (IsWindowVisible(h)) {
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().Contains(title)) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP "akiprobe" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$hwnd = [IntPtr]::Zero
if ($ProcId -gt 0) { $hwnd = [ProbeWin]::FindByPid([uint32]$ProcId) }
elseif ($Title -ne "") { $hwnd = [ProbeWin]::FindByTitle($Title) }
if ($hwnd -eq [IntPtr]::Zero) { throw "Window not found (ProcId=$ProcId Title='$Title')" }

[ProbeWin]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
[ProbeWin]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 300

$rect = New-Object ProbeWin+RECT
if (-not [ProbeWin]::GetWindowRect($hwnd, [ref]$rect)) { throw "GetWindowRect failed" }
$w = $rect.R - $rect.L; $h = $rect.B - $rect.T
if ($w -le 0 -or $h -le 0) { throw "Invalid window rect ${w}x${h}" }
Write-Host ("Window rect: {0},{1} {2}x{3}" -f $rect.L, $rect.T, $w, $h)

$paths = @()
for ($i = 0; $i -lt $Frames; $i++) {
    if ($i -gt 0) { Start-Sleep -Milliseconds $IntervalMs; [ProbeWin]::SetForegroundWindow($hwnd) | Out-Null }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    if ($Mode -eq "screen") {
        # Composed screen: shows exactly what a user sees (requires the window on top).
        $g.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    } else {
        # PrintWindow + PW_RENDERFULLCONTENT: captures the window's own surface even
        # when occluded; works for GDI and DirectX-composited content.
        $hdc = $g.GetHdc()
        [void][ProbeWin]::PrintWindow($hwnd, $hdc, 2)
        $g.ReleaseHdc($hdc)
    }
    $g.Dispose()
    $path = Join-Path $OutDir ("frame{0}.png" -f $i)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $paths += $path
    Write-Host "Saved $path"
}

# Pixel diff between consecutive frames (32bppArgb LockBits, byte compare).
for ($i = 1; $i -lt $paths.Count; $i++) {
    $a = New-Object System.Drawing.Bitmap($paths[$i - 1])
    $b = New-Object System.Drawing.Bitmap($paths[$i])
    if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { Write-Host "Frame $i size differs"; $a.Dispose(); $b.Dispose(); continue }
    $r = New-Object System.Drawing.Rectangle(0, 0, $a.Width, $a.Height)
    $d = $a.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $s = $b.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = $d.Stride * $a.Height
    $da = New-Object byte[] $bytes; $db = New-Object byte[] $bytes
    [System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $da, 0, $bytes)
    [System.Runtime.InteropServices.Marshal]::Copy($s.Scan0, $db, 0, $bytes)
    $a.UnlockBits($d); $b.UnlockBits($s)
    $changed = 0; $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $a.Height; $y++) {
        $row = $y * $d.Stride
        for ($x = 0; $x -lt $a.Width; $x++) {
            $o = $row + $x * 4
            # Compare B,G,R (skip alpha — screen capture alpha is constant 255).
            if ($da[$o] -ne $db[$o] -or $da[$o+1] -ne $db[$o+1] -or $da[$o+2] -ne $db[$o+2]) {
                $changed++
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $total = $a.Width * $a.Height
    $pct = [math]::Round(100.0 * $changed / $total, 3)
    Write-Host ("Diff frame{0}->frame{1}: {2}/{3} px changed ({4}%), bbox=({5},{6})-({7},{8})" -f ($i-1), $i, $changed, $total, $pct, $minX, $minY, $maxX, $maxY)
    $a.Dispose(); $b.Dispose()
}
Write-Host "Done."
