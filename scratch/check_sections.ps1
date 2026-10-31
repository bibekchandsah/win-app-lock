$b = [System.IO.File]::ReadAllBytes('src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLockHook.dll')
$pe = [BitConverter]::ToInt32($b, 0x3C)
$secCount = [BitConverter]::ToInt16($b, $pe + 6)
$optSize = [BitConverter]::ToInt16($b, $pe + 20)
$secBase = $pe + 24 + $optSize
for ($i=0; $i -lt $secCount; $i++) {
    $ptr = $secBase + ($i * 40)
    $n = [System.Text.Encoding]::ASCII.GetString($b, $ptr, 8).Trim([char]0)
    $c = [BitConverter]::ToUInt32($b, $ptr + 36)
    Write-Host ("{0,-10} 0x{1:X8}" -f $n, $c)
}
