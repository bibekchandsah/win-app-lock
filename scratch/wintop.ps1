param([long]$Hwnd, [int]$X = 960, [int]$Y = 18)

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public class WinProbe2
{
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X; public int Y; }

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}

public class PointProbe
{
    public static string Describe(int x, int y)
    {
        var p = new WinProbe2.PT();
        p.X = x; p.Y = y;
        IntPtr h = WinProbe2.WindowFromPoint(p);
        uint pid;
        WinProbe2.GetWindowThreadProcessId(h, out pid);
        var cls = new StringBuilder(256);
        WinProbe2.GetClassName(h, cls, 256);
        var title = new StringBuilder(256);
        WinProbe2.GetWindowText(h, title, 256);
        return string.Format("hwnd={0} pid={1} class={2} title='{3}'", h, pid, cls, title);
    }
}
'@

if ($Hwnd -ne 0) {
    [WinProbe2]::SetWindowPos([IntPtr]$Hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
    Start-Sleep -Milliseconds 300
    Write-Output "raised $Hwnd to top of topmost band"
}

Write-Output ("before/after at {0},{1}: {2}" -f $X, $Y, [PointProbe]::Describe($X, $Y))
