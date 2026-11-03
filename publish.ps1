# Publish script for AppLock (WinUI 3 unpackaged self-contained x64)

param (
    [string]$OutputDir = "$PSScriptRoot\publish\x64"
)

# Terminate any running instances
try {
    # $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\AppLock_DevShutdown")
    # $evt.Set()
    # Start-Sleep -Milliseconds 500
    Write-Host "`Close the AppLock manually" -ForegroundColor Red
    Write-Host "`nPress Enter to continue..."
    Read-Host
} catch {}

if (Get-Process "AppLock" -ErrorAction SilentlyContinue) {
    # & taskkill.exe /F /IM "AppLock.exe" 2>$null
    # Start-Sleep -Milliseconds 500
}

$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
if (-not $vsPath) {
    Write-Host "Visual Studio not found!" -ForegroundColor Red
    exit 1
}

Import-Module "$vsPath\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation

Write-Host "1. Building Native AppLockHook.dll (Release x64)..." -ForegroundColor Cyan
msbuild src\AppLockHook\AppLockHook.vcxproj /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "AppLockHook build failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n2. Publishing AppLock..." -ForegroundColor Cyan
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
}

msbuild src\AppLock\AppLock.csproj /p:Configuration=Release /p:Platform=x64 /t:Restore,Build,Publish /p:PublishDir="$OutputDir\\" /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /v:minimal /nologo

if ($LASTEXITCODE -eq 0) {
    # Ensure AppLockHook.dll is in the publish directory
    $hookDllSrc = "x64\Release\AppLockHook.dll"
    if (-not (Test-Path $hookDllSrc)) {
        $hookDllSrc = "src\AppLockHook\x64\Release\AppLockHook.dll"
    }
    if (Test-Path $hookDllSrc) {
        Copy-Item $hookDllSrc -Destination "$OutputDir\AppLockHook.dll" -Force
        & icacls.exe "$OutputDir\AppLockHook.dll" /grant "*S-1-15-2-1:(RX)" 2>$null | Out-Null
        Write-Host "Copied AppLockHook.dll to publish directory." -ForegroundColor Green
    }

    # Ensure AppLockGuard.exe is copied
    if (Test-Path "$OutputDir\AppLock.exe") {
        Copy-Item "$OutputDir\AppLock.exe" -Destination "$OutputDir\AppLockGuard.exe" -Force
        Write-Host "Copied AppLockGuard.exe to publish directory." -ForegroundColor Green
    }

    # Ensure PRI resource file is in publish directory (WinUI 3 requires resources.pri)
    $priSrc = "src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.pri"
    if (-not (Test-Path $priSrc)) {
        $priSrc = "src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AppLock.pri"
    }
    if (Test-Path $priSrc) {
        Copy-Item $priSrc -Destination "$OutputDir\resources.pri" -Force
        Copy-Item $priSrc -Destination "$OutputDir\AppLock.pri" -Force
        Write-Host "Copied resources.pri to publish directory." -ForegroundColor Green
    }

    # Ensure RegFree WinRT manifests are in publish directory (WinUI 3 unpackaged self-contained requirement)
    $manifestSrc = "src\AppLock\obj\x64\Release\net8.0-windows10.0.19041.0\win-x64\Manifests\WindowsAppSDK.manifest"
    if (-not (Test-Path $manifestSrc)) {
        $manifestSrc = "src\AppLock\obj\x64\Release\net8.0-windows10.0.19041.0\Manifests\WindowsAppSDK.manifest"
    }
    if (Test-Path $manifestSrc) {
        Copy-Item $manifestSrc -Destination "$OutputDir\AppLock.exe.manifest" -Force
        Copy-Item $manifestSrc -Destination "$OutputDir\AppLockGuard.exe.manifest" -Force
        Write-Host "Copied RegFree WinRT manifests to publish directory." -ForegroundColor Green
    }

    # Embed merged manifest directly into executables via mt.exe
    $mtExe = "C:\Users\bibek\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.26100.1742\bin\10.0.26100.0\x64\mt.exe"
    $appManifest = "src\AppLock\app.manifest"
    if ((Test-Path $mtExe) -and (Test-Path $manifestSrc) -and (Test-Path $appManifest)) {
        & $mtExe -manifest $appManifest $manifestSrc "-outputresource:$OutputDir\AppLock.exe;#1" *>$null
        & $mtExe -manifest $appManifest $manifestSrc "-outputresource:$OutputDir\AppLockGuard.exe;#1" *>$null
        Write-Host "Embedded RegFree WinRT manifest directly into executables." -ForegroundColor Green
    }

    Write-Host "`n[SUCCESS] AppLock published successfully to: $OutputDir" -ForegroundColor Green
} else {
    Write-Host "`n[ERROR] Publishing failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
