Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class FindUwpHelper {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "InstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool InstallLockedThumbnail(IntPtr hwnd);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "UninstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl)]
    public static extern void UninstallLockedThumbnail(IntPtr hwnd);
}
"@

Start-Process "calc.exe"
Start-Sleep -Milliseconds 2000

$targetHwnd = [IntPtr]::Zero
$targetTitle = ""
$targetPid = 0

[FindUwpHelper]::EnumWindows({
    param($hwnd, $lParam)
    if (-not [FindUwpHelper]::IsWindowVisible($hwnd)) { return $true }
    $sbCls = New-Object System.Text.StringBuilder 256
    [FindUwpHelper]::GetClassName($hwnd, $sbCls, 256) | Out-Null
    $cls = $sbCls.ToString()

    if ($cls -eq "ApplicationFrameWindow") {
        $sbTitle = New-Object System.Text.StringBuilder 256
        [FindUwpHelper]::GetWindowText($hwnd, $sbTitle, 256) | Out-Null
        $t = $sbTitle.ToString()
        $pidVal = 0
        [FindUwpHelper]::GetWindowThreadProcessId($hwnd, [ref]$pidVal) | Out-Null
        Write-Host "Found ApplicationFrameWindow: 0x$($hwnd.ToString('X')), Title: '$t', PID: $pidVal"
        if ($t -like "*Calculator*") {
            $script:targetHwnd = $hwnd
            $script:targetTitle = $t
            $script:targetPid = $pidVal
            return $false
        }
    }
    return $true
}, [IntPtr]::Zero)

if ($targetHwnd -ne [IntPtr]::Zero) {
    Write-Host "Installing locked thumbnail on Calculator 0x$($targetHwnd.ToString('X'))..."
    $ok = [FindUwpHelper]::InstallLockedThumbnail($targetHwnd)
    Write-Host "InstallLockedThumbnail returned: $ok"
    Start-Sleep -Milliseconds 1500
    [FindUwpHelper]::UninstallLockedThumbnail($targetHwnd)
} else {
    Write-Host "Calculator ApplicationFrameWindow NOT found!"
}
