$p = Start-Process -FilePath "d:\programming exercise\winUI\win app lock\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AppLock.exe" -PassThru
Start-Sleep -Seconds 2
Write-Output "PID: $($p.Id), HasExited: $($p.HasExited)"
if ($p.HasExited) {
    Write-Output "ExitCode: $($p.ExitCode)"
}
