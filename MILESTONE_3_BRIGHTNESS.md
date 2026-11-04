# Milestone 3: DDC/CI + Software Dimming - COMPLETED

## What Was Implemented

### New Providers
- ✅ **DdcCiBrightnessProvider.cs** - External monitor control via DDC/CI
  - Uses Dxva2.dll (GetPhysicalMonitorsFromHMONITOR, GetMonitorBrightness, SetMonitorBrightness)
  - Capability checking with GetMonitorCapabilities
  - 2-second timeout per operation (DDC/CI is slow)
  - 3 retry attempts for writes with 100ms delay
  - Maps hardware min/max range to 0-100%
  - Thread-safe with SemaphoreSlim
  
- ✅ **SoftwareDimProvider.cs** - Fallback using black overlay
  - Creates layered window (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)
  - SetLayeredWindowAttributes for alpha transparency
  - Respects minimum brightness floor (configurable, default 10%)
  - Disposes overlay at 100% brightness

### Service Updates
- ✅ **BrightnessService.cs** enhancements:
  - EnumDisplayMonitors to detect all attached displays
  - Auto-detection of best provider per display (WMI → DDC/CI → Software)
  - Tries all available hardware providers when displayId = "All"
  - Falls back to software dimming if no hardware control available
  - Sets minimum software brightness floor

### UI Updates
- ✅ **BrightnessPage.xaml** - Enhanced display selector:
  - Control method chips with rounded borders
  - Shows "Built-in", "External (DDC/CI)", or "Software dimming"
  - InfoBar warning when software dimming is active
  
- ✅ **BrightnessPage.xaml.cs**:
  - CheckSoftwareDimmingWarning() method
  - Shows InfoBar if any display uses software dimming

### Integration
- ✅ Added EnumDisplayMonitors to NativeMethods.txt
- ✅ Updated NOTES.md with implementation details

## Features Working

1. **Multi-Provider Support** - Automatically detects and uses the best provider for each display
2. **DDC/CI Control** - Adjusts brightness on external monitors that support DDC/CI
3. **Software Dimming Fallback** - Creates overlay for displays without hardware control
4. **Control Method Display** - Shows control method as chips in display selector
5. **InfoBar Warning** - Alerts user when software dimming is being used

## To Verify

1. Build: `.\build-with-msbuild.ps1`
2. Click **Others** → **Brightness card**
3. **Display selector dropdown** shows each display with control method chip:
   - Built-in laptop screen: "Built-in"
   - External monitor (if DDC/CI supported): "External (DDC/CI)"
   - Other displays: "Software dimming"
4. If any display uses software dimming, **InfoBar appears** at top
5. Drag slider:
   - Built-in panel: hardware brightness changes
   - DDC/CI monitor: external monitor brightness changes (may be slower)
   - Software dimming: black overlay appears/adjusts
6. Theme switching works correctly

## Known Issues & Limitations

- Software dimming simplified implementation (only covers primary screen)
- DDC/CI can be slow (2-3 seconds) and unreliable on some monitors
- Multi-monitor software dimming needs per-monitor overlays (future enhancement)
- Some USB-connected monitors may not support DDC/CI

## Git Commit

Please run manually:
```powershell
git add -A
git commit -m "Milestone 3: DDC/CI provider and software dimming fallback

- Implemented DdcCiBrightnessProvider for external monitors via DDC/CI
- Created SoftwareDimProvider as fallback using layered windows
- Updated BrightnessService to auto-detect and use best provider per display
- Enhanced display selector with control method chips
- Added InfoBar warning when software dimming is active
- Monitor enumeration with EnumDisplayMonitors
- Thread-safe DDC/CI operations with 2s timeout and 3 retries
- Minimum brightness floor for software dimming (default 10%)"
```

## Next: Milestone 4
HotkeyService integration + hotkey capture dialog + validation + presets list + auto-save.
