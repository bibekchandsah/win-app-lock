$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinSearch {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
'@

Add-Type -TypeDefinition $code

[WinSearch]::EnumWindows({
    param([IntPtr]$h, [IntPtr]$l)
    $pid = 0
    [WinSearch]::GetWindowThreadProcessId($h, [ref]$pid) | Out-Null
    if ($pid -eq 13920) {
        $sb = New-Object System.Text.StringBuilder 256
        [WinSearch]::GetWindowText($h, $sb, 256) | Out-Null
        $vis = [WinSearch]::IsWindowVisible($h)
        Write-Host "hWnd: $h, Title: '$($sb.ToString())', Visible: $vis"
    }
    return $true
}, [IntPtr]::Zero) | Out-Null
