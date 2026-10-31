$code = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class Privileges {
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TOKEN_PRIVILEGES {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, [In] ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    public static void EnableDebugPrivilege() {
        IntPtr hToken;
        if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028 /* TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY */, out hToken)) {
            TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Attributes = 0x00000002; /* SE_PRIVILEGE_ENABLED */
            LookupPrivilegeValue(null, "SeDebugPrivilege", out tp.Luid);
            AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static bool KillWithDebug(int pid) {
        EnableDebugPrivilege();
        IntPtr hProc = OpenProcess(0x0001 /* PROCESS_TERMINATE */, false, pid);
        if (hProc != IntPtr.Zero) {
            bool res = TerminateProcess(hProc, 0);
            return res;
        }
        return false;
    }
}
'@

Add-Type -TypeDefinition $code

$procs = Get-Process -Name "AppLock","AppLockGuard" -ErrorAction SilentlyContinue
foreach ($p in $procs) {
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting("Local\AppLock_AuthExit_$($p.Id)")
        $ev.Set()
    } catch { }
}

foreach ($p in $procs) {
    $res = [Privileges]::KillWithDebug($p.Id)
    Write-Host "Kill PID $($p.Id): $res"
}

Start-Sleep -Milliseconds 500
Get-Process -Name "AppLock*","AppLockGuard*" -ErrorAction SilentlyContinue
