# Build script for AppLock
Write-Host "Building AppLock..." -ForegroundColor Cyan

# Clean previous build
if (Test-Path "src\AppLock\bin") {
    Remove-Item "src\AppLock\bin" -Recurse -Force
}
if (Test-Path "src\AppLock\obj") {
    Remove-Item "src\AppLock\obj" -Recurse -Force
}

# Restore packages
Write-Host "`nRestoring packages..." -ForegroundColor Yellow
dotnet restore

# Build in Release mode
Write-Host "`nBuilding in Release mode..." -ForegroundColor Yellow
dotnet build -c Release -v minimal

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n Build succeeded!" -ForegroundColor Green
    Write-Host "`nTo run the app:" -ForegroundColor Cyan
    Write-Host "  dotnet run --project src\AppLock\AppLock.csproj" -ForegroundColor White
} else {
    Write-Host "`n Build failed!" -ForegroundColor Red
    Write-Host "`nTrying to get more details..." -ForegroundColor Yellow
    dotnet build -c Release -v detailed > build-log.txt 2>&1
    Write-Host "Detailed build log saved to build-log.txt" -ForegroundColor Yellow
}
