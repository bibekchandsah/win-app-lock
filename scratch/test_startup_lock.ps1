$appLockExe = Resolve-Path "src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe"
Write-Host "Starting AppLock from $appLockExe..."

$proc = Start-Process -FilePath $appLockExe -PassThru
Start-Sleep -Seconds 3

Write-Host "AppLock PID: $($proc.Id)"
$guard = Get-Process -Name "AppLockGuard" -ErrorAction SilentlyContinue
Write-Host "AppLockGuard PID: $($guard.Id)"

# Check if target apps are locked or thumbnails set
$hookDll = Get-Process -Name "AppLock" | Select-Object -ExpandProperty Modules | Where-Object { $_.ModuleName -eq "AppLockHook.dll" }
Write-Host "AppLock has Hook DLL loaded: $($null -ne $hookDll)"

# Test dev shutdown signal so it cleans up nicely
Write-Host "Signaling DevShutdown..."
$ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\AppLock_DevShutdown")
$ev.Set()
Start-Sleep -Seconds 2

$remaining = Get-Process -Name "AppLock","AppLockGuard" -ErrorAction SilentlyContinue
Write-Host "Remaining processes after DevShutdown: $($remaining.Count)"
