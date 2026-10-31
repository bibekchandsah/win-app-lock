$ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\AppLock_DevShutdown")
$ev.Set()
Start-Sleep -Seconds 2
Get-Process -Name "AppLock","AppLockGuard" -ErrorAction SilentlyContinue
