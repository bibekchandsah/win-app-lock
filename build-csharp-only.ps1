# Build only the C# AppLock project (skip C++ hook)
# Run this in a regular PowerShell (not Developer PowerShell)

Write-Host "Building AppLock C# project only..." -ForegroundColor Cyan

# Use dotnet build which doesn't require full VS environment
$projectPath = ".\src\AppLock\AppLock.csproj"

try {
    # Restore packages
    Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
    dotnet restore $projectPath
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Restore failed!" -ForegroundColor Red
        exit 1
    }

    # Build
    Write-Host "`nBuilding project..." -ForegroundColor Yellow
    dotnet build $projectPath -c Release --no-restore
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nBuild FAILED!" -ForegroundColor Red
        exit 1
    }

    Write-Host "`n✅ Build SUCCEEDED!" -ForegroundColor Green
    Write-Host "`nOutput: .\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AppLock.exe" -ForegroundColor Cyan
    
} catch {
    Write-Host "`nBuild error: $_" -ForegroundColor Red
    exit 1
}
