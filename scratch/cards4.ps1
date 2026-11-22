$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CardInput4 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    public static void WheelFrom(int x, int y, int notches) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0800, 0, 0, 120 * notches, UIntPtr.Zero);
        System.Threading.Thread.Sleep(20);
        SetCursorPos(40, 900);
    }
}
"@

$shots = 'D:\programming exercise\winUI\win app lock\scratch'

# Start from a known level: four notches down, then three more down while watching.
[CardInput4]::WheelFrom(960, 18, -4)
Start-Sleep -Milliseconds 1200
[CardInput4]::WheelFrom(960, 18, -3)
Start-Sleep -Milliseconds 1100

$bmp = New-Object System.Drawing.Bitmap 1920, 1080
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$g.Dispose()
$rect = New-Object System.Drawing.Rectangle 620, 0, 700, 140
$crop = $bmp.Clone($rect, $bmp.PixelFormat)
$crop.Save((Join-Path $shots 'card4-volume-down.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose()
$bmp.Dispose()

Start-Sleep -Milliseconds 4200
$bmp2 = New-Object System.Drawing.Bitmap 1920, 1080
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.CopyFromScreen(0, 0, 0, 0, $bmp2.Size)
$g2.Dispose()
$crop2 = $bmp2.Clone($rect, $bmp2.PixelFormat)
$crop2.Save((Join-Path $shots 'card4-expired.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$crop2.Dispose()
$bmp2.Dispose()
Write-Output 'done'
