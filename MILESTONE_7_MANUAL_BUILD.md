# Milestone 7 - Manual Build & Fix Guide

## Current Status
✅ All code written and integrated
⚠️ Build environment has permission/sandbox issues
🔧 Manual build required

## Recent Fixes Applied

### Fix 1: Removed `ClipToBounds` from XAML
**Error:** `XamlCompiler error WMC0011: Unknown member 'ClipToBounds' on element 'Border'`

**Fix:** Removed `ClipToBounds="True"` from ScreenBlurPreview.xaml
- WinUI 3 Border doesn't support ClipToBounds property
- Removed entirely as it's cosmetic

**File:** `src/AppLock/Views/Controls/ScreenBlurPreview.xaml` (Line 31)

### Fix 2: Updated COM Interop for GraphicsCaptureItem
**Issue:** WinRT interop method needed proper implementation

**Fix:** Updated `CreateCaptureItemForMonitor` method to use:
```csharp
var factoryPtr = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
var interop = Marshal.GetObjectForIUnknown(factoryPtr) as IGraphicsCaptureItemInterop;
var itemPointer = interop.CreateForMonitor(hMonitor, ref itemGuid);
var captureItem = Marshal.GetObjectForIUnknown(itemPointer) as GraphicsCaptureItem;
```

**File:** `src/AppLock/Services/Brightness/ScreenCaptureService.cs`

### Fix 3: Updated Win2D Package Version
**Change:** Microsoft.Graphics.Win2D from 1.2.0 → 1.2.1

**File:** `src/AppLock/AppLock.csproj`

## Manual Build Instructions

### Method 1: PowerShell Build Script (Recommended)
```powershell
# Run PowerShell as Administrator
cd "D:\programming exercise\winUI\win app lock"

# Close AppLock if running
Stop-Process -Name AppLock -Force -ErrorAction SilentlyContinue

# Run build
.\build-test.ps1
```

### Method 2: Direct MSBuild
```powershell
# Run PowerShell as Administrator
cd "D:\programming exercise\winUI\win app lock"

# Set path to MSBuild
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"

# Build solution
& $msbuild AppLock.sln /p:Configuration=Release /p:Platform=x64 /t:Restore`;Build /v:minimal /nologo
```

### Method 3: Visual Studio IDE
1. Open `AppLock.sln` in Visual Studio 2026
2. Select **Release** configuration
3. Select **x64** platform
4. Build → Rebuild Solution (Ctrl+Shift+B)
5. Check Output window for errors

## Expected Build Output

### Success:
```
AppLockHook.vcxproj -> D:\...\x64\Release\AppLockHook.dll
AppLock -> D:\...\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.dll
AppLock -> D:\...\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Common Errors & Fixes

#### Error 1: Win2D Package Not Restored
**Symptoms:**
```
error CS0246: The type or namespace name 'CanvasControl' could not be found
```

**Fix:**
```powershell
# Restore NuGet packages
dotnet restore
# Or in Visual Studio: Tools → NuGet Package Manager → Restore NuGet Packages
```

#### Error 2: Windows SDK Mismatch
**Symptoms:**
```
warning MSB3851: This project targets "Windows, Version=10.0.19041.0", 
but it is attempting to reference "...Version=10.0.26100.0"
```

**Fix:** This is just a warning, ignore it. Or update `Directory.Build.props`:
```xml
<TargetFramework>net8.0-windows10.0.26100.0</TargetFramework>
```

#### Error 3: COM Interop Interface Issue
**Symptoms:**
```
error CS1503: Argument 2: cannot convert from 'out System.Guid' to 'ref System.Guid'
```

**Fix:** Already applied - interface uses `ref Guid` parameter

#### Error 4: CanvasDevice Constructor
**Symptoms:**
```
error CS7036: There is no argument given that corresponds to the required parameter
```

**Fix:** Change in ScreenCaptureService.cs line 54:
```csharp
// From:
_canvasDevice = new CanvasDevice();

// To:
_canvasDevice = CanvasDevice.GetSharedDevice();
```

## Testing After Build

### Step 1: Launch Application
```powershell
cd "D:\programming exercise\winUI\win app lock"
Start-Process ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -Verb RunAs
```

### Step 2: Navigate to Brightness Page
1. Click **"Others"** in left navigation
2. Click **"Brightness"** card
3. Scroll to **"Blur Overlay"** expander (should be expanded)

### Step 3: Verify Live Preview
**Expected:**
- ✅ Preview panel visible below "Blur Level" slider
- ✅ Loading spinner appears briefly
- ✅ Live blurred screen content displays
- ✅ Preview updates smoothly (~30 fps)

**If fallback message appears:**
- Check Windows version (needs 10 version 1903+)
- Run as administrator
- Check Event Viewer for security/capture errors

### Step 4: Test Real-Time Blur
1. Move **"Blur Level"** slider from 0% to 100%
2. **Expected:** Preview blur intensity changes in real-time
   - 0% = clear/sharp image
   - 50% = moderate blur
   - 100% = heavy blur (48px)

### Step 5: Verify No Feedback Loop
**Check:** Preview should NOT show itself recursively
- ✅ Preview shows desktop/windows behind AppLock
- ❌ Should NOT see infinite mirroring effect
- ❌ Should NOT see the preview inside itself

**If recursion occurs:**
- SetWindowDisplayAffinity may not be working
- Windows version may be < 2004 (build 19041)

### Step 6: Test Performance
1. Open Task Manager → Performance tab
2. Note CPU/GPU usage with preview active
3. Navigate away from Brightness page
4. **Expected:** CPU/GPU usage drops
5. Return to Brightness page  
6. **Expected:** Preview restarts, usage increases

**Target Performance:**
- CPU: 5-10% while preview active
- GPU: Moderate (depends on hardware)
- Memory: Stable (no leaks)

### Step 7: Test Theme Switching
1. Go to Settings → Appearance
2. Switch between Light / Dark / System
3. **Expected:** Preview control follows theme
   - Border color changes
   - Text color adjusts
   - Background adapts

## Files Modified in Milestone 7

### New Files:
1. `src/AppLock/Services/Brightness/ScreenCaptureService.cs` - 230 lines
2. `src/AppLock/Views/Controls/ScreenBlurPreview.xaml` - 80 lines
3. `src/AppLock/Views/Controls/ScreenBlurPreview.xaml.cs` - 215 lines
4. `build-test.ps1` - Simple build script
5. `MILESTONE_7_COMPLETE.md` - Full documentation
6. `MILESTONE_7_BUILD_INSTRUCTIONS.md` - Build guide
7. `MILESTONE_7_MANUAL_BUILD.md` - This file

### Modified Files:
1. `src/AppLock/AppLock.csproj` - Added Win2D package
2. `src/AppLock/Views/BrightnessPage.xaml` - Added preview control + namespace
3. `src/AppLock/Views/BrightnessPage.xaml.cs` - Connected blur slider
4. `src/AppLock/NativeMethods.txt` - Added SetWindowDisplayAffinity

## Verification Checklist

After successful build:

- [ ] Application launches without errors
- [ ] Others page accessible
- [ ] Brightness card navigates to settings
- [ ] Live preview panel appears
- [ ] Preview shows blurred screen content
- [ ] Blur slider updates preview in real-time
- [ ] No recursive preview (feedback loop prevented)
- [ ] Performance acceptable (CPU < 10%)
- [ ] Preview stops when page hidden
- [ ] Preview restarts when page shown
- [ ] Theme switching works correctly
- [ ] No memory leaks over time

## Known Limitations

1. **Primary Monitor Only**: Currently captures primary monitor
   - Could be extended to support monitor selection

2. **Windows 10 2004+**: SetWindowDisplayAffinity requires build 19041+
   - Older versions may show yellow capture border

3. **Performance**: Real-time blur is GPU-intensive
   - Preview auto-stops when not visible to conserve resources

4. **Capture Border**: Yellow border may appear on Windows 10
   - Windows 11 supports borderless capture

## Success Criteria

Milestone 7 is complete when:

✅ **Code Complete:**
- All files created/modified
- No compilation errors
- No XAML errors
- Win2D properly integrated

✅ **Functional Complete:**
- Preview displays live screen content
- Gaussian blur effect works
- Real-time slider updates
- Feedback loop prevention active
- Performance optimized

✅ **Quality Complete:**
- Theme support working
- Error handling in place
- Graceful fallbacks implemented
- Documentation complete

## If Build Still Fails

### Last Resort Option: Simplified Fallback

If Win2D or GraphicsCapture absolutely won't work, we can implement a simpler fallback:

**Option A: Static Sample Preview**
- Show a static sample image (bundled resource)
- Apply blur effect to static image
- Note in UI: "Live preview unavailable"

**Option B: Remove Preview**
- Keep blur overlay functional
- Remove live preview panel
- Note: "Preview on primary screen when active"

**To implement Option A:**
1. Comment out ScreenCaptureService usage in ScreenBlurPreview.xaml.cs
2. Load a bundled image resource
3. Apply GaussianBlurEffect to static image
4. Update info text

## Git Commit (After Success)

```powershell
git add -A
git commit -m "Milestone 7: Live Preview with Windows.Graphics.Capture + Win2D

IMPLEMENTATION COMPLETE:
- Windows.Graphics.Capture screen capture engine with COM interop
- Win2D CanvasControl with real-time GaussianBlurEffect
- 30fps live preview with 0-48px blur mapping
- SetWindowDisplayAffinity feedback loop prevention  
- Performance optimization (stops when not visible)
- Graceful fallback UI for unsupported systems
- Privacy notice about local-only capture

FILES ADDED:
- Services/Brightness/ScreenCaptureService.cs (230 lines)
- Views/Controls/ScreenBlurPreview.xaml/.cs (295 lines total)
- build-test.ps1, documentation files

FILES MODIFIED:
- AppLock.csproj (added Microsoft.Graphics.Win2D 1.2.1)
- Views/BrightnessPage.xaml/.cs (integrated preview control)
- NativeMethods.txt (added SetWindowDisplayAffinity)

TECHNICAL DETAILS:
- COM IGraphicsCaptureItemInterop for monitor capture
- Direct3D11CaptureFramePool free-threaded capture
- CanvasBitmap from Direct3D11 surface conversion
- GaussianBlurEffect with BorderMode.Hard
- Aspect ratio preservation with Matrix3x2 scaling
- Thread-safe with proper disposal patterns

FIXES APPLIED:
- Removed ClipToBounds (not supported in WinUI 3)
- Updated COM interop to use WindowsRuntimeMarshal
- Win2D package version 1.2.1

Milestone 7 complete per brightness.md specification."
```

---

## Summary

**Status:** ✅ Implementation 100% complete, ⏳ Build pending

**What's Done:**
- Full Windows.Graphics.Capture + Win2D implementation
- Live preview control with Gaussian blur
- Feedback loop prevention
- Performance optimization
- All code fixes applied

**What's Needed:**
- Manual build with admin permissions
- Test all features per checklist
- Verify no feedback loop
- Check performance
- Git commit after success

**Next Milestone:** Milestone 8 - Overlay options (dim, tint, exclude active window)
