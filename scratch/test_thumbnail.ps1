Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class NativeTest {
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "InstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool InstallLockedThumbnail(IntPtr hwnd);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "UninstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl)]
    public static extern void UninstallLockedThumbnail(IntPtr hwnd);
}
"@

Start-Process notepad
Start-Sleep -Milliseconds 1500

$proc = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
Write-Host "Notepad PID: $($proc.Id), HWND: 0x$($hwnd.ToString('X'))"

$installed = [NativeTest]::InstallLockedThumbnail($hwnd)
Write-Host "InstallLockedThumbnail returned: $installed"

Start-Sleep -Milliseconds 1000

# Check if AppLockHook.dll is in Notepad process modules
$proc.Refresh()
$hookModule = $proc.Modules | Where-Object { $_.ModuleName -like "*AppLockHook*" }
if ($hookModule) {
    Write-Host "FOUND AppLockHook.dll in Notepad process! Path: $($hookModule.FileName)"
} else {
    Write-Host "AppLockHook.dll is NOT loaded into Notepad process!"
}

[NativeTest]::UninstallLockedThumbnail($hwnd)
$proc.Kill()
