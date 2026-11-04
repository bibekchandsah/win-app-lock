# Simple build test script
$ErrorActionPreference = "Continue"

Write-Host "Building AppLock..." -ForegroundColor Cyan

# Find MSBuild
$msbuildPath = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"

if (-not (Test-Path $msbuildPath)) {
    Write-Host "MSBuild not found at $msbuildPath" -ForegroundColor Red
    exit 1
}

# Build
& $msbuildPath AppLock.sln /p:Configuration=Release /p:Platform=x64 /t:Restore`;Build /v:normal /nologo

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nBuild SUCCESS!" -ForegroundColor Green
    $exePath = ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe"
    if (Test-Path $exePath) {
        Write-Host "Executable created: $exePath" -ForegroundColor Green
    }
} else {
    Write-Host "`nBuild FAILED with exit code $LASTEXITCODE" -ForegroundColor Red
}
