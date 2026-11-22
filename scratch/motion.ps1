$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class MotionInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

$shots = 'D:\programming exercise\winUI\win app lock\scratch'

function Capture([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap 1920, 1080
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $g.Dispose()
    $rect = New-Object System.Drawing.Rectangle 600, 0, 720, 300
    $crop = $bmp.Clone($rect, $bmp.PixelFormat)
    $crop.Save((Join-Path $shots $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    $bmp.Dispose()
    Write-Output "captured $name"
}

# mid-open: hover starts the 150 ms delay, the spring runs 320 ms
[MotionInput]::SetCursorPos(960, 18) | Out-Null
Start-Sleep -Milliseconds 260
Capture 'motion-open-mid.png'
Start-Sleep -Milliseconds 1500

# mid-close: 350 ms close delay then 260 ms spring
[MotionInput]::SetCursorPos(40, 900) | Out-Null
Start-Sleep -Milliseconds 470
Capture 'motion-close-mid.png'
Start-Sleep -Milliseconds 900
Capture 'motion-rest.png'
Write-Output 'done'
