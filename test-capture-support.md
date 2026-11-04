# Screen Capture Diagnostics

## Issue
Live preview shows: "Screen capture not available on this system"

## Possible Causes

### 1. Windows Version Too Old
**Required:** Windows 10 version 1903 (build 18362) or later

**Check your version:**
```powershell
winver
# OR
[System.Environment]::OSVersion.Version
```

**If version < 1903:**
- Windows.Graphics.Capture API not available
- Live preview will not work
- Blur overlay still works (uses simplified acrylic)

### 2. Not Running as Administrator
**Solution:** Run AppLock as administrator
```powershell
Start-Process ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -Verb RunAs
```

### 3. Graphics Capture Permissions Blocked
**Check:**
- Windows Privacy Settings → Screen capture
- Antivirus/security software blocking capture
- Group Policy restrictions

### 4. COM Activation Failure
The RoGetActivationFactory call may be failing

**Debug:** Rebuild and check Visual Studio Output window for debug messages:
```
[ScreenCapture] GraphicsCaptureSession not supported
[ScreenCapture] Failed to get primary monitor handle
[ScreenCapture] Failed to create GraphicsCaptureItem
[ScreenCapture] StartCapture failed: [error message]
```

## Quick Test

Run this PowerShell to check Windows version:
```powershell
$version = [System.Environment]::OSVersion.Version
Write-Host "Windows Version: $version"

if ($version.Build -ge 18362) {
    Write-Host "✅ Windows version supports Graphics Capture" -ForegroundColor Green
} else {
    Write-Host "❌ Windows version too old (need build 18362+)" -ForegroundColor Red
}
```

## Workarounds

### Option A: Update Windows
Update to Windows 10 1903+ or Windows 11

### Option B: Use Without Live Preview
The blur overlay still works without live preview:
- Adjust blur level with slider (no preview)
- Press Ctrl+Shift+B to toggle blur overlay
- Blur overlay uses acrylic effect (works on all versions)

### Option C: Static Preview Image
We can show a static sample image with blur applied instead of live capture

## Next Steps

1. **Check Windows version** with `winver`
2. **Run as administrator** if not already
3. **Rebuild** with diagnostic messages
4. **Check Output window** in Visual Studio during runtime
5. **Report** which check failed

Let me know your Windows version and I'll provide specific guidance!
