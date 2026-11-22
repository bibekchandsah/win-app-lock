$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CardInput2 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    public static void WheelFrom(int x, int y, int notches) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0800, 0, 0, 120 * notches, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
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

# 1. volume wheel over the folded pill, pointer leaves at once
[CardInput2]::WheelFrom(960, 18, 2)
Start-Sleep -Milliseconds 550
Capture 'card2-volume.png'
Start-Sleep -Milliseconds 4200

# 2. clipboard coalescing: two copies 400 ms apart show as one updated card
Set-Clipboard -Value 'first copy'
Start-Sleep -Milliseconds 400
Set-Clipboard -Value 'second copy replaces the first'
Start-Sleep -Milliseconds 600
Capture 'card2-coalesce.png'
Start-Sleep -Milliseconds 4200

# 3. screenshots folder created after the island started
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$shot = New-Object System.Drawing.Bitmap 90, 50
$shot.Save((Join-Path $dir 'Island_late_folder.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$shot.Dispose()
Start-Sleep -Milliseconds 900
Capture 'card2-screenshot.png'
Start-Sleep -Milliseconds 4200
Capture 'card2-end.png'
Write-Output 'done'
