# Signal authorized exit so watchdog and AppLock can close cleanly
$procs = Get-Process -Name "AppLock","AppLockGuard" -ErrorAction SilentlyContinue
foreach ($p in $procs) {
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\AppLock_AuthExit_$($p.Id)")
        $ev.Set()
        Write-Host "Signaled authorized exit to PID $($p.Id)"
    } catch {
        # ignore
    }
}

Start-Sleep -Milliseconds 300

# Now stop any remaining instances
foreach ($p in $procs) {
    try {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    } catch { }
}
Start-Sleep -Milliseconds 500
Get-Process -Name "AppLock*","AppLockGuard*" -ErrorAction SilentlyContinue
