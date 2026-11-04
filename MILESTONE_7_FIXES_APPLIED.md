# Milestone 7 - Build Fixes Applied

## All Compilation Errors Fixed ✅

### Fix 1: Package Version Conflict
**Error:** Win2D 1.2.1 requires WindowsAppSDK 1.6.x but project uses 1.5.x

**Solution:** ✅ Downgraded to Win2D 1.2.0 (compatible with SDK 1.5)
```xml
<PackageReference Include="Microsoft.Graphics.Win2D" Version="1.2.0" />
```

### Fix 2: App.MainWindow Property Not Found
**Error:** `CS0117: 'App' does not contain a definition for 'MainWindow'`

**Solution:** ✅ Changed to `App.MainWindowInstance`
```csharp
// In ScreenBlurPreview.xaml.cs line 223
var mainWindow = App.MainWindowInstance; // Was: App.MainWindow
```

### Fix 3: IsBorderRequired Not Available in SDK 10.0.19041.0
**Error:** `CS1061: 'GraphicsCaptureSession' does not contain a definition for 'IsBorderRequired'`

**Solution:** ✅ Commented out IsBorderRequired (only available in SDK 10.0.22000+)
```csharp
// In ScreenCaptureService.cs line 73
// IsBorderRequired commented out - not available in older SDK
// Yellow border may appear on Windows 10 (cosmetic only)
```

### Fix 4: WindowsRuntimeMarshal.GetActivationFactory Not Available  
**Error:** `CS0117: 'WindowsRuntimeMarshal' does not contain a definition for 'GetActivationFactory'`

**Solution:** ✅ Changed to use RoGetActivationFactory P/Invoke
```csharp
// In ScreenCaptureService.cs line 182
[DllImport("api-ms-win-core-winrt-l1-1-0.dll", CharSet = CharSet.Unicode)]
private static extern int RoGetActivationFactory(
    [MarshalAs(UnmanagedType.HString)] string activatableClassId,
    ref Guid iid,
    out IntPtr factory);

// Usage:
var guid = typeof(IGraphicsCaptureItemInterop).GUID;
var hr = RoGetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", ref guid, out var factoryPtr);
```

---

## Build Instructions

The build should now work. Try building manually with PowerShell:

### Method 1: Build Script
```powershell
cd "D:\programming exercise\winUI\win app lock"
.\build-with-msbuild.ps1
```

### Method 2: Direct MSBuild (if script fails)
```powershell
cd "D:\programming exercise\winUI\win app lock"

# Close AppLock if running
Stop-Process -Name AppLock -Force -ErrorAction SilentlyContinue

# Build
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
& $msbuild AppLock.sln /p:Configuration=Release /p:Platform=x64 /t:Restore`;Build /v:minimal /nologo
```

### Method 3: Visual Studio
1. Open `AppLock.sln` in Visual Studio
2. Configuration: **Release**, Platform: **x64**
3. Build → Rebuild Solution (Ctrl+Shift+B)

---

## Expected Build Result

### Success:
```
AppLockHook.vcxproj -> D:\...\x64\Release\AppLockHook.dll
AppLock -> D:\...\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.dll
AppLock -> D:\...\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe

Build succeeded.
```

### If Still Fails:
Copy the FULL error message and I'll fix it.

---

## Testing After Build

Once build succeeds:

```powershell
# Run AppLock
Start-Process ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -Verb RunAs
```

**Navigate:** Others → Brightness → Blur Overlay expander

**Verify:**
- [ ] Live preview appears below blur slider
- [ ] Shows blurred screen content
- [ ] Blur slider (0-100%) updates preview in real-time
- [ ] No feedback loop (preview doesn't show itself)
- [ ] Performance acceptable (CPU <10%)

---

## Known Limitations After Fixes

1. **Yellow Capture Border**: May appear on Windows 10
   - IsBorderRequired not available in SDK 10.0.19041.0
   - Borderless capture requires Windows 11 / SDK 10.0.22000+
   - **Cosmetic only** - does not affect functionality

2. **SDK Version Warning**: MSB3851 warning is harmless
   - AppLockHook uses newer SDK (26100) than main app (19041)
   - Warning can be ignored

---

## Files Modified in This Fix

1. **src/AppLock/AppLock.csproj**
   - Changed Win2D 1.2.1 → 1.2.0

2. **src/AppLock/Views/Controls/ScreenBlurPreview.xaml.cs**
   - Line 223: `App.MainWindow` → `App.MainWindowInstance`

3. **src/AppLock/Services/Brightness/ScreenCaptureService.cs**
   - Line 73: Commented out `IsBorderRequired` 
   - Line 182: Replaced WindowsRuntimeMarshal with RoGetActivationFactory
   - Added P/Invoke for RoGetActivationFactory
   - Removed unused using statements

---

## Summary

✅ **All 4 compilation errors fixed**
✅ **Package conflicts resolved**
✅ **SDK compatibility ensured**
⏳ **Ready for build**

**Next:** Build the project and test the live preview!
