$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CardInput3 {
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
$dir = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'Screenshots'

function Capture([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap 1920, 1080
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $g.Dispose()
    $rect = New-Object System.Drawing.Rectangle 620, 0, 700, 140
    $crop = $bmp.Clone($rect, $bmp.PixelFormat)
    $crop.Save((Join-Path $shots $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    $bmp.Dispose()
    Write-Output "captured $name"
}

# 1. volume wheel over the folded pill; capture once the shell has closed again
[CardInput3]::WheelFrom(960, 18, 3)
Start-Sleep -Milliseconds 1100
Capture 'card3-volume.png'
Start-Sleep -Milliseconds 4200

# 2. screenshots folder missing at start, created now, file written inside it
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
Start-Sleep -Milliseconds 250
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Start-Sleep -Milliseconds 400
$shot = New-Object System.Drawing.Bitmap 90, 50
$shot.Save((Join-Path $dir 'Island_late_folder2.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$shot.Dispose()
Start-Sleep -Milliseconds 1100
Capture 'card3-screenshot.png'
Start-Sleep -Milliseconds 4000
Capture 'card3-end.png'
Write-Output 'done'
