Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class NativeTestLive {
    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "InstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool InstallLockedThumbnail(IntPtr hwnd);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "UninstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl)]
    public static extern void UninstallLockedThumbnail(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
"@

Start-Process notepad
Start-Sleep -Milliseconds 1500

$proc = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
Write-Host "Notepad PID: $($proc.Id), HWND: 0x$($hwnd.ToString('X'))"

$installed = [NativeTestLive]::InstallLockedThumbnail($hwnd)
Write-Host "InstallLockedThumbnail returned: $installed"

# Wait 5 seconds so timer fires
Start-Sleep -Seconds 5

# Force a DWM iconic thumbnail request by sending WM_DWMSENDICONICTHUMBNAIL (0x0323)
Write-Host "Simulating DWM thumbnail request..."
# lParam: HIWORD=200, LOWORD=150 -> 0x00C80096
$lParam = [IntPtr](0x00C80096)
[NativeTestLive]::SendMessage($hwnd, 0x0323, [IntPtr]::Zero, $lParam)

Start-Sleep -Seconds 2

[NativeTestLive]::UninstallLockedThumbnail($hwnd)
$proc.Kill()
