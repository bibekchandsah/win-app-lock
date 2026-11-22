param(
    [Parameter(Mandatory = $true)][string]$Image,
    [int]$X0 = 700,
    [int]$X1 = 1220,
    [int]$Y0 = 0,
    [int]$Y1 = 200,
    [int]$Step = 20
)

Add-Type -AssemblyName System.Drawing

$path = if ([System.IO.Path]::IsPathRooted($Image)) { $Image } else { Join-Path 'D:\programming exercise\winUI\win app lock\scratch' $Image }
$bmp = New-Object System.Drawing.Bitmap $path

Write-Output ("image {0} {1}x{2}" -f [System.IO.Path]::GetFileName($path), $bmp.Width, $bmp.Height)

$header = '      '
for ($x = $X0; $x -le $X1; $x += $Step * 4) { $header += ('{0,9}' -f $x) }
Write-Output $header

for ($y = $Y0; $y -le $Y1; $y += $Step) {
    $row = ('{0,4}: ' -f $y)
    for ($x = $X0; $x -le $X1; $x += $Step * 4) {
        $c = $bmp.GetPixel($x, $y)
        $row += ('{0,9}' -f ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B))
    }
    Write-Output $row
}

$bmp.Dispose()
