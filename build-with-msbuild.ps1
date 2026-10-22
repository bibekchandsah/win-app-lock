# Build script using Visual Studio's MSBuild
# This imports the VS Developer environment which includes all required build tools

# --- Kill any running AppLock instance so the EXE isn't locked during build ---
# --- Kill any running AppLock instance so the EXE isn't locked during build ---
& taskkill.exe /F /IM "AppLock.exe" 2>$null
Start-Sleep -Milliseconds 600

# Import VS Developer PowerShell environment
$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
if ($vsPath) {
    Import-Module "$vsPath\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation

    Write-Host "Building with MSBuild..." -ForegroundColor Green
    msbuild AppLock.sln /p:Configuration=Release /p:Platform=x64 /t:Restore,Build /v:minimal /nologo $args

    if ($LASTEXITCODE -eq 0) {
        Write-Host "`nBuild succeeded!" -ForegroundColor Green
        Write-Host "`nTo run the app, execute:" -ForegroundColor Cyan
        Write-Host ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -ForegroundColor Yellow
    } else {
        Write-Host "`nBuild failed with exit code $LASTEXITCODE" -ForegroundColor Red
    }
} else {
    Write-Host "Visual Studio not found. Please build from Developer Command Prompt." -ForegroundColor Red
    Write-Host "Or run: dotnet build -c Release" -ForegroundColor Yellow
}

