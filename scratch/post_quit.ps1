Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public class WinUtil2 {
    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
}
'@

$procs = Get-Process -Name "AppLock","AppLockGuard" -ErrorAction SilentlyContinue
foreach ($p in $procs) {
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\AppLock_AuthExit_$($p.Id)")
        $ev.Set()
        Write-Host "Set auth event for $($p.Id)"
    } catch { }

    foreach ($t in $p.Threads) {
        [WinUtil2]::PostThreadMessage([uint32]$t.Id, 0x0012, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
}

Start-Sleep -Milliseconds 500
Get-Process -Name "AppLock*","AppLockGuard*" -ErrorAction SilentlyContinue
