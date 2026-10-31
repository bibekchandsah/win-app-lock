Remove-Item 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log' -ErrorAction SilentlyContinue

# Kill any existing AppLock or Notepad
Get-Process AppLock, notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Launching AppLock.exe..."
$appLock = Start-Process ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -PassThru
Start-Sleep -Seconds 3

Write-Host "Launching Notepad..."
$notepad = Start-Process "notepad.exe" -PassThru
Start-Sleep -Seconds 3

Write-Host "Checking hook log..."
if (Test-Path 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log') {
    Get-Content 'C:\Users\bibek\AppData\Local\Temp\applock_hook.log'
} else {
    Write-Host "No hook log generated!"
}

# Clean up
$notepad.Kill()
$appLock.Kill()
