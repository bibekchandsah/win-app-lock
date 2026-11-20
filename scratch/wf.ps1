param(
    [string]$Action = "capture",
    [string]$Out = "scratch/wf.png",
    [int]$X = 0,
    [int]$Y = 0
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$src = @'
using System;
using System.Runtime.InteropServices;
public class WfWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, int extra);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    public static IntPtr Find(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 2);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
Add-Type -TypeDefinition $src

$hwnd = [WfWin]::Find("AppLock")
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "window not found"; exit 1 }

if ($Action -eq "maximize") {
    [WfWin]::ShowWindow($hwnd, 3) | Out-Null
    [WfWin]::SetForegroundWindow($hwnd) | Out-Null
    Write-Output "maximized"
    exit 0
}

[WfWin]::ShowWindow($hwnd, 3) | Out-Null
[WfWin]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700

if ($Action -eq "click") {
    [WfWin]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 150
    [WfWin]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 40
    [WfWin]::mouse_event(0x0004, 0, 0, 0, 0)
    Write-Output "clicked $X,$Y"
    exit 0
}

# Full primary screen: the window is maximized, so this shows everything including flyouts.
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($screen.X, $screen.Y, 0, 0, $screen.Size)
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out $($screen.Width)x$($screen.Height)"
