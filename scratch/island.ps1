param(
    [string]$Action = "capture",
    [string]$Out = "scratch/island.png",
    [int]$X = 0,
    [int]$Y = 0
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$src = @'
using System;
using System.Runtime.InteropServices;
public class IslandWin {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@
Add-Type -TypeDefinition $src

if ($Action -eq "hover") {
    [IslandWin]::SetCursorPos($X, $Y) | Out-Null
    Write-Output "pointer at $X,$Y"
    exit 0
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($screen.X, $screen.Y, 0, 0, $screen.Size)
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out $($screen.Width)x$($screen.Height)"
