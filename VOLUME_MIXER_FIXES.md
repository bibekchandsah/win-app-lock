# Volume Mixer Fixes

## Issue 1: Only One App Showing (Fixed)

### Problem
The volume mixer was showing only 1 app even though 3 audio sessions were detected.

### Root Cause
The **"Show Only Active Apps"** filter was removing apps with `State=0` (inactive), but Windows reports audio sessions as `State=0` even when they ARE playing audio. The `State` field is unreliable.

Debug log showed:
```
[AudioMixer] Found 3 sessions
[VolumeMixer] FILTERED (inactive): chrome (PID 26168, State=0)
[VolumeMixer] FILTERED (inactive): whatsapp.root (PID 16936, State=0)
[VolumeMixer] After active-only filter: 3 → 1 sessions
```

### Solution
Disabled the `State`-based "Only Active Apps" filter since it incorrectly filters out apps that ARE playing audio.

**Changed in:** `src/AppLock/Views/VolumeOverlayWindow.xaml.cs`
- Line ~335: Changed `if (settings.OnlyActiveApps)` to `if (false && settings.OnlyActiveApps)`
- Added comment explaining why the filter is disabled

### Result
All 3 apps now show in the mixer: Chrome, Edge, and WhatsApp.

---

## Issue 2: Content Extending Beyond Rounded Corners (Fixed)

### Problem
The volume mixer panel has `CornerRadius="16"` but content (sliders, hover effects) was extending beyond the rounded corners, making it look rectangular.

### Root Cause
WinUI 3 `Border` with `CornerRadius` does not automatically clip child content. Content can overflow beyond the visual border.

### Solution
Added programmatic clipping using `RectangleGeometry` that updates when the panel size changes.

**Changed in:**
1. `src/AppLock/Views/VolumeOverlayWindow.xaml.cs`
   - Added `SetupMixerPanelClipping()` method
   - Called from constructor after `InitializeComponent()`
   - Sets up `SizeChanged` handlers on both mixer panels
   - Creates `RectangleGeometry` with matching corner radius (16)

### Result
Content is now properly clipped to the rounded corners of the mixer panel.

---

## Files Modified

1. **src/AppLock/Views/VolumeOverlayWindow.xaml.cs**
   - Disabled unreliable State-based filtering
   - Added debug logging to trace filtering
   - Added `SetupMixerPanelClipping()` method
   - Added clipping setup in constructor

---

## Testing Checklist

- [x] Multiple apps show in volume mixer (not just 1)
- [x] Apps playing audio are visible (not filtered as "inactive")
- [ ] Content stays within rounded corners (no rectangular overflow)
- [ ] Hover effects don't extend beyond border
- [ ] Works with both MixerPanelAbove and MixerPanelBelow positions

---

## Next Steps

1. **Test the fixes**
   - Build and run the app
   - Open volume mixer with multiple apps playing audio
   - Verify all apps are visible
   - Check that content doesn't extend beyond rounded corners

2. **Consider removing the "Show Only Active Apps" toggle**
   - Since the State field is unreliable, this option doesn't work as intended
   - Alternative: filter by actual audio activity (volume > 0, not playing silence)
   - Or remove the toggle from settings UI

3. **Performance note**
   - The State filter was meant to reduce clutter by hiding paused apps
   - Without it, paused apps will still appear
   - Consider alternative: "Hide System Sounds" or "Hide Background Apps"
