param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [int]$Wait = 700
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Clicker {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@

[Clicker]::Click($X, $Y)
Start-Sleep -Milliseconds $Wait
Write-Output "clicked $X,$Y"
