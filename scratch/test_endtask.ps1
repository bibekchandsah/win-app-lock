Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public class EndTaskTest2 {
    public delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EndTask(IntPtr hWnd, bool fShutDown, bool fForce);
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static void CheckThread(uint tid) {
        EnumThreadWindows(tid, (hWnd, lParam) => {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            Console.WriteLine("Thread " + tid + " has window: " + hWnd + " (" + sb + ")");
            EndTask(hWnd, false, true);
            return true;
        }, IntPtr.Zero);
    }
}
'@

$p = Get-Process -Name "AppLock" -ErrorAction SilentlyContinue
if ($p) {
    foreach ($t in $p.Threads) {
        [EndTaskTest2]::CheckThread([uint32]$t.Id)
    }
}
