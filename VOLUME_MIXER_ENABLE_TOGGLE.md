# Volume Mixer Enable/Disable Toggle Implementation

## Features to Implement

### 1. Toggle Card in Appearance Tab
- Add an "Enable Volume Mixer" card above the Icon Position card
- Show current status (Enabled/Disabled)
- Toggle switch to enable/disable
- Description text explaining what it does

### 2. Hotkey Support
- Add hotkey field in settings (default: Win+V)
- Allow user to customize the hotkey
- Register/unregister hotkey based on enabled state
- Show current hotkey in UI

### 3. System Tray Menu
- Add menu item "Enable Volume Mixer" or "Disable Volume Mixer" (dynamic text)
- Checkmark or indicator showing current state
- Sync with all UIs when toggled

### 4. Synchronization
All three sources (Settings UI, Hotkey, System Tray) must stay in sync:
- Toggling in Settings → updates tray menu + registers/unregisters hotkey
- Using hotkey → shows/hides overlay + updates Settings UI + updates tray menu
- Clicking tray menu → enables/disables + updates Settings UI + registers/unregisters hotkey

## Files to Modify

1. **VolumeMixerSettings.cs** - Add Hotkey property
2. **VolumeMixerSettingsPage.xaml** - Add toggle card and hotkey field
3. **VolumeMixerSettingsPage.xaml.cs** - Handle toggle and hotkey changes
4. **ServiceContainer.cs** - Already has SetVolumeMixerEnabled, may need hotkey service
5. **System tray** - Add menu item (find tray icon implementation)
6. **HotkeyService** - Register Volume Mixer hotkey

## Implementation Plan

### Step 1: Add Hotkey to Settings Model
- Add `Hotkey` property to `VolumeMixerSettings`
- Default: Win+V (VK_LWIN + VK_V)

### Step 2: Add Toggle Card to Settings UI
- Create card above Icon Position
- ToggleSwitch bound to `Enabled`
- Display current status

### Step 3: Add Hotkey Field to Settings UI
- TextBox showing current hotkey
- Button to capture new hotkey
- Validation (no conflicts with existing hotkeys)

### Step 4: Register Hotkey
- Use existing HotkeyService pattern (Brightness uses Ctrl+Alt+B)
- Register when enabled, unregister when disabled
- Toggle action: call `ServiceContainer.SetVolumeMixerEnabled(!current)`

### Step 5: Add System Tray Menu Item
- Find existing tray icon code
- Add menu item with checkmark
- Update text and checkmark based on state
- Subscribe to `ServiceContainer.VolumeMixerEnabledChanged` event

### Step 6: Sync All Sources
- Settings toggle → saves + fires event
- Hotkey → toggles state + fires event
- Tray menu → toggles state + fires event
- All listeners update their UI

## Testing Checklist

- [ ] Toggle in Settings enables/disables overlay
- [ ] Hotkey toggles overlay on/off
- [ ] Tray menu item toggles overlay
- [ ] All three stay in sync
- [ ] Custom hotkey can be set
- [ ] Hotkey conflicts are detected
- [ ] Settings persist across restarts
- [ ] Overlay appears/disappears correctly
- [ ] Status shows correctly everywhere
