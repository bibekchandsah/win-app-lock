# Caps Lock OSD Fixes

## Issues Identified

1. **Black Screen Flash on First Show**: The window would show a black rectangle initially before the content appeared
2. **Unreliable Detection**: Key state detection was sometimes inconsistent
3. **Acrylic Background Issues**: Using AcrylicBackgroundFillColorDefaultBrush can cause rendering problems

## Changes Made

### 1. LockKeyOverlayWindow.xaml
**Problem**: Acrylic brush causing black flash and rendering issues
**Solution**: 
- Changed from `AcrylicBackgroundFillColorDefaultBrush` to `CardBackgroundFillColorDefaultBrush` for more reliable rendering
- Added `Translation="0,0,32"` to enable proper layering and shadow effects
- Added `BackgroundTransition` for smooth visual updates

```xaml
Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
Translation="0,0,32"
```

### 2. LockKeyOverlayWindow.xaml.cs
**Problem**: Window not pre-warmed, causing black flash on first display; black background due to incorrect window style application order
**Solution**: 
- Added window "pre-warming" by positioning OFF-SCREEN, activating, then hiding/repositioning
- Position off-screen during pre-warm so user never sees it
- **CRITICAL**: Apply `Activate()` BEFORE `SetWindowExTransparent()` - WinUI3 compositor needs first activation to render
- Use 64-bit window long functions (`GetWindowLongPtrW`/`SetWindowLongPtrW`) instead of 32-bit versions
- Added `TransparentBackdrop` SystemBackdrop for proper transparent background

```csharp
// Position OFF-SCREEN for invisible pre-warming
PositionWindowOffscreen();

// Set transparent background
this.SystemBackdrop = new TransparentBackdrop();
RootGrid.Background = new SolidColorBrush(Colors.Transparent);

// IMPORTANT: Activate FIRST to let compositor render initial frame
this.Activate();

// THEN apply transparent/click-through styles AFTER activation
SetWindowExTransparent(hwnd);

// After brief moment, hide and reposition to correct location
DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
{
    _appWindow.Hide();
    PositionWindow(); // Move to correct position for actual use
});
```

### 3. LockKeyIndicatorService.cs
**Problem**: Using Low priority dispatcher queue added unnecessary delay
**Solution**: 
- Removed `DispatcherQueuePriority.Low` to use normal priority for more responsive detection
- Added try-catch for better error handling
- Improved debugging output

```csharp
// Use normal priority for more responsive detection
_dispatcherQueue.TryEnqueue(() =>
{
    try
    {
        // ... detection logic
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[LockKeyIndicator] Error showing indicator: {ex.Message}");
    }
});
```

### 4. LockKeyHookService.cs
**Problem**: Detecting on key DOWN meant reading state before it changed
**Solution**: 
- Changed detection from WM_KEYDOWN to WM_KEYUP for more reliable state detection
- Added 10ms delay after key up to ensure Windows has updated the key state
- Added WM_KEYUP constant for proper key release detection

```csharp
// Trigger on key UP for more reliable state detection
if ((vkCode == VK_CAPITAL || vkCode == VK_NUMLOCK || vkCode == VK_SCROLL) && isKeyUp)
{
    // Small delay to ensure key state has changed
    Task.Delay(10).ContinueWith(_ =>
    {
        LockKeyPressed?.Invoke(this, vkCode);
    });
}
```

## How These Fixes Work Together

1. **Off-screen Pre-warming** (xaml.cs): Window positioned 1000px off-screen during activation, so pre-warming is invisible to user
2. **Activate-then-style Order** (xaml.cs): Calling `Activate()` before `SetWindowExTransparent()` allows WinUI3 compositor to render initial frame, preventing black screen
3. **64-bit Window Functions** (xaml.cs): Using `GetWindowLongPtrW`/`SetWindowLongPtrW` prevents handle truncation on 64-bit systems
4. **TransparentBackdrop** (xaml.cs): Custom SystemBackdrop ensures transparent window background
5. **Solid Background** (xaml): Provides reliable, consistent rendering across all systems
6. **Key UP Detection** (LockKeyHookService): Ensures we read the key state AFTER it changes, not before
7. **Delay After Key Up**: 10ms gives Windows time to update GetKeyState() internally
8. **Normal Priority Dispatch**: Removes artificial delay for faster, more responsive UI

## Testing Checklist

After building these changes, test:
- [ ] First Caps Lock press shows the OSD without black flash
- [ ] Rapid Caps Lock presses all show correct ON/OFF state
- [ ] OSD appears quickly (< 100ms after keypress)
- [ ] OSD fades in/out smoothly
- [ ] No ghost windows or rendering artifacts
- [ ] Works on both light and dark themes
- [ ] Num Lock and Scroll Lock also work correctly

## Build Instructions

⚠️ **IMPORTANT**: Before building, you need to fix the .dotnet permissions issue.

Run these commands in an **elevated PowerShell** (Run as Administrator):

```powershell
# Take ownership of the .dotnet folder
takeown /F "$env:USERPROFILE\.dotnet" /R /D Y

# Grant yourself full control
icacls "$env:USERPROFILE\.dotnet" /grant "${env:USERNAME}:(OI)(CI)F" /T

# Then build the project
cd "d:\programming exercise\winUI\win app lock\src\AppLock"
dotnet build -c Debug
```

## Additional Improvements to Consider

1. **Settings Integration**: Add OSD position options (top/bottom, left/center/right)
2. **Customization**: Allow users to change display duration, animation speed
3. **Multiple Monitors**: Detect which monitor has focus and show OSD there
4. **Accessibility**: Add screen reader announcements for key state changes
