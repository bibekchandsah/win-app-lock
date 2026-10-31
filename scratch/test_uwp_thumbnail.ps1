Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class NativeTestUwp {
    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "InstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool InstallLockedThumbnail(IntPtr hwnd);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "UninstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl)]
    public static extern void UninstallLockedThumbnail(IntPtr hwnd);
}
"@

# Start calculator
Start-Process "calc.exe"
Start-Sleep -Milliseconds 2000

# Find ApplicationFrameWindow
$afh = Get-Process ApplicationFrameHost | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $afh) {
    Write-Host "No ApplicationFrameHost with MainWindowHandle found!"
    exit 1
}

$hwnd = $afh.MainWindowHandle
Write-Host "Calculator AFH PID: $($afh.Id), HWND: 0x$($hwnd.ToString('X')), Title: '$($afh.MainWindowTitle)'"

$installed = [NativeTestUwp]::InstallLockedThumbnail($hwnd)
Write-Host "InstallLockedThumbnail returned: $installed"

Start-Sleep -Milliseconds 2000

$afh.Refresh()
$hookModule = $afh.Modules | Where-Object { $_.ModuleName -like "*AppLockHook*" }
if ($hookModule) {
    Write-Host "FOUND AppLockHook.dll in ApplicationFrameHost! Path: $($hookModule.FileName)"
} else {
    Write-Host "AppLockHook.dll is NOT loaded into ApplicationFrameHost!"
}

Start-Sleep -Milliseconds 3000

[NativeTestUwp]::UninstallLockedThumbnail($hwnd)
