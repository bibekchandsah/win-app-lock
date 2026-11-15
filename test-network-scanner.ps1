# Test Network Scanner - Helper script for debugging device detection
# Run this after building to test the new features

Write-Host "=== Network Scanner Test Helper ===" -ForegroundColor Cyan
Write-Host ""

# 1. Kill any running AppLock instances
Write-Host "1. Stopping any running AppLock instances..." -ForegroundColor Yellow
Get-Process AppLock -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "   Killing AppLock PID $($_.Id)..."
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# 2. Check if settings file exists and show path
$settingsPath = "$env:LOCALAPPDATA\AppLock\settings.json"
Write-Host ""
Write-Host "2. Settings file location:" -ForegroundColor Yellow
Write-Host "   $settingsPath"

if (Test-Path $settingsPath) {
    $size = (Get-Item $settingsPath).Length
    Write-Host "   File exists (Size: $size bytes)" -ForegroundColor Green
    
    # Show device count
    $content = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $deviceCount = $content.KnownNetworkDevices.Count
    Write-Host "   Known devices: $deviceCount" -ForegroundColor Green
} else {
    Write-Host "   File does not exist yet (will be created on first launch)" -ForegroundColor Gray
}

# 3. Offer to clear device database
Write-Host ""
Write-Host "3. Clear device database? (recommended to test new detection)" -ForegroundColor Yellow
Write-Host "   This will force fresh detection with new icons and names" -ForegroundColor Gray
$clear = Read-Host "   Clear database? (y/n)"

if ($clear -eq 'y' -or $clear -eq 'Y') {
    if (Test-Path $settingsPath) {
        # Backup first
        $backupPath = "$settingsPath.backup"
        Copy-Item $settingsPath $backupPath -Force
        Write-Host "   Backup created: $backupPath" -ForegroundColor Green
        
        # Clear KnownNetworkDevices
        $content = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $content.KnownNetworkDevices = @()
        $content.NetworkDeviceHistory = @()
        $content | ConvertTo-Json -Depth 100 | Set-Content $settingsPath -Encoding UTF8
        Write-Host "   Device database cleared!" -ForegroundColor Green
    } else {
        Write-Host "   Nothing to clear (file doesn't exist)" -ForegroundColor Gray
    }
}

# 4. Launch the new build
Write-Host ""
Write-Host "4. Launching AppLock..." -ForegroundColor Yellow
$exePath = "d:\programming exercise\winUI\win app lock\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe"

if (Test-Path $exePath) {
    Start-Process $exePath
    Write-Host "   AppLock launched!" -ForegroundColor Green
    Write-Host ""
    Write-Host "=== Testing Instructions ===" -ForegroundColor Cyan
    Write-Host "1. Open AppLock" -ForegroundColor White
    Write-Host "2. Go to: Internet Access > Devices tab" -ForegroundColor White
    Write-Host "3. Click 'Scan now' button" -ForegroundColor White
    Write-Host "4. Check results:" -ForegroundColor White
    Write-Host "   - 'This PC' should have a PC icon (🖥)" -ForegroundColor Gray
    Write-Host "   - Router should show 'Router / Gateway' with router icon (🌐)" -ForegroundColor Gray
    Write-Host "   - Different device types should have different icons" -ForegroundColor Gray
    Write-Host "   - Devices without hostnames should show 'Manufacturer (XX:XX:XX)'" -ForegroundColor Gray
} else {
    Write-Host "   ERROR: Build not found at:" -ForegroundColor Red
    Write-Host "   $exePath" -ForegroundColor Red
    Write-Host ""
    Write-Host "   Please build first:" -ForegroundColor Yellow
    Write-Host "   .\build-with-msbuild.ps1" -ForegroundColor Gray
}

Write-Host ""
Write-Host "=== Debug Tips ===" -ForegroundColor Cyan
Write-Host "If icons still don't show correctly:" -ForegroundColor White
Write-Host "  1. Make sure you cleared the device database (step 3 above)" -ForegroundColor Gray
Write-Host "  2. Wait a few seconds after clicking 'Scan now'" -ForegroundColor Gray
Write-Host "  3. Check the manufacturer column - should show vendor names" -ForegroundColor Gray
Write-Host "  4. Look for 'Online' status pills (green = online, gray = offline)" -ForegroundColor Gray
Write-Host ""
