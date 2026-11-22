param([string]$Filter = "")

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class WinProbe
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static List<string> Rows = new List<string>();

    public static void Collect()
    {
        Rows.Clear();
        EnumWindows(delegate (IntPtr h, IntPtr p)
        {
            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            RECT r;
            GetWindowRect(h, out r);
            long ex = GetWindowLongPtr(h, -20).ToInt64();
            Rows.Add(string.Format("{0}|{1}|{2}|{3}|{4},{5} {6}x{7}|vis={8}|topmost={9}",
                h, pid, cls, title, r.L, r.T, r.R - r.L, r.B - r.T, IsWindowVisible(h), (ex & 0x8) != 0));
            return true;
        }, IntPtr.Zero);
    }
}
'@

[WinProbe]::Collect()
foreach ($row in [WinProbe]::Rows) {
    $parts = $row.Split('|')
    $procName = (Get-Process -Id ([int]$parts[1]) -ErrorAction SilentlyContinue).ProcessName
    if ($Filter -eq "" -or $procName -like "*$Filter*" -or $parts[2] -like "*$Filter*") {
        Write-Output ("pid={0} proc={1} class={2} title='{3}' rect={4} {5}" -f $parts[1], $procName, $parts[2], $parts[3], $parts[4], $parts[5] + " " + $parts[6])
    }
}
