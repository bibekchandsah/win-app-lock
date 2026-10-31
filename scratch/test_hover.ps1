Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class NativeTestHover {
    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "InstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool InstallLockedThumbnail(IntPtr hwnd);

    [DllImport(@"src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll", EntryPoint = "UninstallLockedThumbnail", CallingConvention = CallingConvention.Cdecl)]
    public static extern void UninstallLockedThumbnail(IntPtr hwnd);
}
"@

Remove-Item 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log' -ErrorAction SilentlyContinue

Start-Process notepad
Start-Sleep -Milliseconds 1500

$proc = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
Write-Host "Notepad PID: $($proc.Id), HWND: 0x$($hwnd.ToString('X'))"

$installed = [NativeTestHover]::InstallLockedThumbnail($hwnd)
Write-Host "InstallLockedThumbnail returned: $installed"

# Wait a moment for subclass
Start-Sleep -Milliseconds 1000

# Get screen bounds and move cursor to taskbar
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "Screen bounds: $($screen.Width) x $($screen.Height)"

# Move cursor across taskbar bottom to trigger thumbnail hover
for ($x = 200; $x -lt ($screen.Width - 200); $x += 100) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, ($screen.Height - 15))
    Start-Sleep -Milliseconds 400
    if (Test-Path 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log') {
        $content = Get-Content 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log'
        if ($content -match "WM_DWMSENDICONICTHUMBNAIL") {
            Write-Host "DWM THUMBNAIL EVENT TRIGGERED at x=$x!"
            break
        }
    }
}

Start-Sleep -Milliseconds 1000

if (Test-Path 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log') {
    Write-Host "`n--- HOOK LOG ---"
    Get-Content 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log'
} else {
    Write-Host "No hook log found!"
}

[NativeTestHover]::UninstallLockedThumbnail($hwnd)
$proc.Kill()
