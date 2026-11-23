param([int]$CropX = 640, [int]$CropY = 0, [int]$CropW = 720, [int]$CropH = 340)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class P9 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

# Hover the notch with a real move so the island opens.
[void][P9]::SetCursorPos(600, 500)
Start-Sleep -Milliseconds 600
[void][P9]::SetCursorPos(900, 8)
Start-Sleep -Milliseconds 400
[void][P9]::SetCursorPos(946, 12)
Start-Sleep -Seconds 3

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)

$crop = New-Object System.Drawing.Bitmap ($CropW * 2), ($CropH * 2)
$cg = [System.Drawing.Graphics]::FromImage($crop)
$cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$cg.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, ($CropW * 2), ($CropH * 2)), (New-Object System.Drawing.Rectangle $CropX, $CropY, $CropW, $CropH), [System.Drawing.GraphicsUnit]::Pixel)
$crop.Save("D:\programming exercise\winUI\win app lock\island-crop.png", [System.Drawing.Imaging.ImageFormat]::Png)
