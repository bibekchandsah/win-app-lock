# Verification for island milestone 4: triggers each live card source and captures the notch.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class CardInput {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    public static void ToggleCaps() {
        keybd_event(0x14, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(0x14, 0, 2, UIntPtr.Zero);
    }
    public static void WheelAt(int x, int y, int notches) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0800, 0, 0, 120 * notches, UIntPtr.Zero);
    }
}
"@

$root = 'D:\programming exercise\winUI\win app lock'
$shots = 'D:\programming exercise\winUI\win app lock\scratch'

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

function Away() { [CardInput]::SetCursorPos(40, 900) | Out-Null; Start-Sleep -Milliseconds 120 }

# keep the pointer off the island so cards are not paused
Away

# 1. Caps Lock on / off
[CardInput]::ToggleCaps()
Start-Sleep -Milliseconds 550
Capture 'card-caps-on.png'
Start-Sleep -Milliseconds 4000   # let it collapse
Away
[CardInput]::ToggleCaps()
Start-Sleep -Milliseconds 550
Capture 'card-caps-off.png'
Start-Sleep -Milliseconds 4000
Away

# 2. Copied text
Set-Clipboard -Value 'Island milestone 4 clipboard card works'
Start-Sleep -Milliseconds 700
Capture 'card-copied-text.png'
Start-Sleep -Milliseconds 4000
Away

# 3. Image copied
$img = New-Object System.Drawing.Bitmap 64, 48
$graphics = [System.Drawing.Graphics]::FromImage($img)
$graphics.Clear([System.Drawing.Color]::DarkOrange)
$graphics.Dispose()
[System.Windows.Forms.Clipboard]::SetImage($img)
$img.Dispose()
Start-Sleep -Milliseconds 800
Capture 'card-copied-image.png'
Start-Sleep -Milliseconds 4000
Away

# 4. Screenshot saved
$dir = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'Screenshots'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$shot = New-Object System.Drawing.Bitmap 80, 60
$shot.Save((Join-Path $dir 'Island_card_test.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$shot.Dispose()
Start-Sleep -Milliseconds 900
Capture 'card-screenshot.png'
Start-Sleep -Milliseconds 4000
Away

# 5. Volume wheel over the folded pill (pointer stays on it, so the countdown pauses)
[CardInput]::WheelAt(960, 18, 2)
Start-Sleep -Milliseconds 700
Capture 'card-volume.png'

# 6. Leave and let it settle back to the plain pill
Away
Start-Sleep -Milliseconds 1200
Capture 'card-settled.png'
Write-Output 'done'
