$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public class WinSearchFixed {
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

    public static void FindAndPost(uint targetPid) {
        EnumWindows((hWnd, lParam) => {
            uint pId = 0;
            GetWindowThreadProcessId(hWnd, out pId);
            if (pId == targetPid) {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                bool vis = IsWindowVisible(hWnd);
                Console.WriteLine("Found hWnd=" + hWnd + ", Title='" + sb.ToString() + "', Visible=" + vis);
                // Send WM_CLOSE
                PostMessage(hWnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
    }
}
'@

Add-Type -TypeDefinition $code
[WinSearchFixed]::FindAndPost(13920)
