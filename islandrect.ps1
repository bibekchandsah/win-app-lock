Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class W10 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint procId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
"@

$ids = @(Get-Process AppLock | Select-Object -ExpandProperty Id)
$script:found = @()
$cb = {
    param($h, $l)
    $procId = 0
    [void][W10]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($ids -contains [int]$procId -and [W10]::IsWindowVisible($h)) {
        $r = New-Object W10+RECT
        [void][W10]::GetWindowRect($h, [ref]$r)
        $script:found += ("{0}x{1} @ {2},{3}" -f ($r.R - $r.L), ($r.B - $r.T), $r.L, $r.T)
    }
    return $true
}
[void][W10]::EnumWindows($cb, [IntPtr]::Zero)
$script:found
