param([string[]]$Names)

Add-Type -AssemblyName System.Drawing
$root = 'D:\programming exercise\winUI\win app lock\scratch'

foreach ($name in $Names) {
    $srcPath = Join-Path $root "$name.png"
    if (-not (Test-Path $srcPath)) { Write-Output "missing $srcPath"; continue }
    $src = [System.Drawing.Image]::FromFile($srcPath)
    $bmp = New-Object System.Drawing.Bitmap 1280, 720
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.DrawImage($src, 0, 0, 1280, 720)
    $outPath = Join-Path $root "$name-small.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $src.Dispose()
    Write-Output "resized $outPath"
}
