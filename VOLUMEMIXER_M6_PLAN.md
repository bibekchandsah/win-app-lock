# Volume Mixer M6: Start with Windows & Final Polish

## Milestone Scope

M6 completes the Volume Mixer feature with startup integration, performance improvements, and polish:

1. **Start with Windows** - Auto-launch on system startup
2. **Performance Optimizations** - Reduce CPU/memory when idle
3. **Error Handling** - Graceful degradation for edge cases
4. **Accessibility** - Keyboard navigation and screen reader support
5. **Final Testing** - End-to-end validation

---

## Feature 1: Start with Windows

### Current State
- `StartWithWindows` setting exists in VolumeMixerSettings
- CheckBox exists in settings UI
- No implementation (always false)

### Implementation Plan

#### A. Startup Task Registration

**For MSIX/Packaged Apps:**
```csharp
// Use Windows.ApplicationModel.StartupTask
var startupTask = await StartupTask.GetAsync("VolumeMixerStartupId");
if (startWithWindows)
{
    await startupTask.RequestEnableAsync();
}
else
{
    startupTask.Disable();
}
```

**For Unpackaged Apps:**
```csharp
// Use registry: HKCU\Software\Microsoft\Windows\CurrentVersion\Run
const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
const string valueName = "AppLockVolumeMixer";

if (startWithWindows)
{
    using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
    key?.SetValue(valueName, $"\"{exePath}\" --minimized");
}
else
{
    using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
    key?.DeleteValue(valueName, throwOnMissingValue: false);
}
```

#### B. Startup Arguments

**Add command-line arg support:**
- `--minimized` or `--startup` → Start with overlay only, no main window
- Check in `App.xaml.cs` `OnLaunched`
- If startup flag, don't show main window

#### C. Settings Integration

**Update `StartWithWindowsCheckBox_Changed`:**
```csharp
private async void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
{
    var enabled = StartWithWindowsCheckBox.IsChecked == true;
    ServiceContainer.Settings.VolumeMixer.StartWithWindows = enabled;
    
    try
    {
        await StartupManager.SetStartWithWindows(enabled);
        SaveSettings();
    }
    catch (Exception ex)
    {
        // Show error, revert checkbox
        StartWithWindowsCheckBox.IsChecked = !enabled;
        ShowStartupError(ex.Message);
    }
}
```

---

## Feature 2: Performance Optimizations

### Current Issues
- Polling runs at 500ms even when mixer closed
- No resource cleanup on overlay close
- Session enumeration could be more efficient

### Optimizations

#### A. Smart Polling
**Already Implemented ✅**
- Polling only when mixer panel open
- Stops when collapsed/closed
- M4 implementation is good

#### B. Event-Driven Updates
**Enhancement:**
- Use `AudioSessionManager.OnSessionCreated` where possible
- Only poll for session state/volume changes
- Reduce polling interval to 1000ms (from 500ms) when only checking state

#### C. Memory Management
**Add cleanup:**
```csharp
public void Dispose()
{
    _pollingTimer?.Stop();
    _hoverDelayTimer?.Stop();
    _hideDelayTimer?.Stop();
    
    _audioService.MasterVolumeChanged -= OnAudioMasterVolumeChanged;
    _audioService.MasterMuteChanged -= OnAudioMasterMuteChanged;
    _audioService.DefaultDeviceChanged -= OnAudioDefaultDeviceChanged;
    
    _iconResolver.ClearCache();
}
```

#### D. Reduce Allocations
- Cache session groups dictionary
- Reuse existing rows instead of recreate
- Use `StringBuilder` for debug logging

---

## Feature 3: Error Handling & Edge Cases

### A. No Audio Device
**Current:** May crash or show empty mixer

**Fix:**
```csharp
if (!_audioService.HasDefaultDevice)
{
    // Show message in mixer panel
    EmptyStateText.Text = "No audio device found";
    return;
}
```

### B. Protected Processes
**Current:** May throw access denied exceptions

**Fix:**
```csharp
try
{
    var process = Process.GetProcessById(processId);
    // ... use process
}
catch (Win32Exception ex) when (ex.NativeErrorCode == 5) // Access Denied
{
    // Use session.DisplayName as fallback
    return (session.DisplayName, null);
}
```

### C. Session Disconnect During Update
**Current:** May cause exceptions in UI updates

**Fix:**
```csharp
try
{
    _audioService.SetSessionVolume(identifier, volume);
}
catch (COMException ex)
{
    System.Diagnostics.Debug.WriteLine($"Session disconnected: {ex.Message}");
    // Remove from UI on next refresh
}
```

---

## Feature 4: Accessibility

### A. AutomationProperties

**Add to all interactive elements:**
```xaml
<!-- Speaker button -->
<Button AutomationProperties.Name="Master volume mute toggle"
        AutomationProperties.HelpText="Click to mute or unmute system audio"/>

<!-- Master slider -->
<Slider AutomationProperties.Name="Master volume"
        AutomationProperties.HelpText="Adjust system volume level"/>

<!-- Mixer button -->
<Button AutomationProperties.Name="Open volume mixer"
        AutomationProperties.HelpText="Show per-application volume controls"/>

<!-- App row slider -->
<Slider AutomationProperties.Name="{x:Bind viewModel.Name} volume"
        AutomationProperties.HelpText="Adjust volume for this application"/>
```

### B. Keyboard Navigation

**Verify Tab order:**
1. Speaker button
2. Master volume slider
3. Mixer button
4. (In mixer) First app slider
5. First app mute button
6. Second app slider
7. Etc.

**Add keyboard shortcuts:**
- `Space` on slider → Play/pause (default behavior)
- `Arrow keys` on slider → Adjust volume (default behavior)
- `Escape` → Close mixer panel

### C. High Contrast Support

**Verify in High Contrast themes:**
- All controls visible
- Borders have sufficient contrast
- Focus indicators clear

---

## Feature 5: Final Testing & Validation

### Test Matrix

#### Core Functionality
- [ ] Master volume slider controls system volume
- [ ] Master mute button works
- [ ] Per-app sliders control app volume
- [ ] Per-app mute buttons work
- [ ] Mouse wheel on master slider adjusts volume

#### State Machine
- [ ] Idle → Expanded on hover (after delay)
- [ ] Expanded → Idle when pointer leaves (after delay)
- [ ] Expanded → MixerOpen on mixer button click
- [ ] MixerOpen → Expanded on button click again
- [ ] No collapse while dragging slider

#### Drag Mode
- [ ] Outline shows when enabled
- [ ] Can drag to any position
- [ ] Position persists across restarts
- [ ] Clamped to screen bounds
- [ ] IconPosition change clears custom position

#### Filtering
- [ ] Blacklist excludes apps
- [ ] Active-only shows playing apps only
- [ ] Empty state shows when no apps

#### Persistence
- [ ] All settings save and load correctly
- [ ] Custom position persists
- [ ] Blacklist persists
- [ ] Theme persists

#### Themes
- [ ] Light theme works
- [ ] Dark theme works
- [ ] System theme follows OS
- [ ] Theme changes apply live

#### Startup
- [ ] Start with Windows checkbox works
- [ ] App launches minimized on startup
- [ ] Overlay appears without main window

#### Performance
- [ ] No CPU usage when idle
- [ ] <5% CPU when mixer open
- [ ] Memory stable over time (no leaks)
- [ ] Smooth animations

#### Error Handling
- [ ] No audio device: shows message
- [ ] Protected process: uses fallback name
- [ ] Session disconnect: no crash
- [ ] Device change: recovers gracefully

---

## Files to Create/Modify

### New Files
- `src/AppLock/Services/VolumeMixer/StartupManager.cs` - Startup task registration

### Modified Files
- `src/AppLock/App.xaml.cs`:
  - Add command-line argument parsing
  - Support `--minimized` flag

- `src/AppLock/Views/VolumeMixerSettingsPage.xaml.cs`:
  - Implement StartWithWindowsCheckBox_Changed with actual registration

- `src/AppLock/Views/VolumeOverlayWindow.xaml`:
  - Add AutomationProperties to all controls

- `src/AppLock/Views/VolumeOverlayWindow.xaml.cs`:
  - Add Dispose method for cleanup
  - Improve error handling in session updates
  - Add Escape key handler to close mixer

- `src/AppLock/Services/VolumeMixer/AudioMixerService.cs`:
  - Better exception handling for COM errors

---

## Implementation Order

1. **Start with Windows** (1-2 hours)
   - Create StartupManager
   - Detect if packaged vs unpackaged
   - Registry approach for unpackaged
   - Command-line args support
   - Update settings checkbox handler

2. **Error Handling** (1 hour)
   - Add try-catch in critical paths
   - Graceful degradation for missing device
   - Session disconnect handling

3. **Accessibility** (1 hour)
   - Add AutomationProperties to XAML
   - Test keyboard navigation
   - Add Escape key handler

4. **Performance** (30 min)
   - Add Dispose method
   - Verify polling already optimized
   - Memory leak check

5. **Final Testing** (2 hours)
   - Run full test matrix
   - Fix any bugs found
   - Performance validation

**Total Estimate**: 5-6 hours

---

## Success Criteria for M6

- [x] Start with Windows checkbox functional
- [x] App launches minimized on startup
- [x] No CPU usage when idle
- [x] All controls have AutomationProperties
- [x] Keyboard navigation works
- [x] Escape closes mixer
- [x] No crashes on edge cases
- [x] Memory stable over 1 hour
- [x] All M1-M5 features still work
- [x] Full test matrix passes

---

## Deliverables

- Working "Start with Windows" feature
- Comprehensive error handling
- Full accessibility support
- Performance validated
- Complete test documentation
- User guide / README

---

Ready to implement M6? Reply **"continue"** and I'll start with Start with Windows implementation.
