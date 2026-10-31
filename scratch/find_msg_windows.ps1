$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinMsgSearch {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
'@

Add-Type -TypeDefinition $code

$parent = [IntPtr](-3) # HWND_MESSAGE
$child = [IntPtr]::Zero
while ($true) {
    $child = [WinMsgSearch]::FindWindowEx($parent, $child, $null, $null)
    if ($child -eq [IntPtr]::Zero) { break }
    $pid = 0
    [WinMsgSearch]::GetWindowThreadProcessId($child, [ref]$pid) | Out-Null
    if ($pid -eq 13920) {
        $sb = New-Object System.Text.StringBuilder 256
        [WinMsgSearch]::GetClassName($child, $sb, 256) | Out-Null
        Write-Host "Message window found: hWnd=$child, Class=$($sb.ToString())"
    }
}
