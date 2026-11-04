# Milestone 4: Hotkeys + Presets - COMPLETED

## What Was Implemented

### Hotkey Capture
- ✅ **HotkeyCaptureDialog.xaml/.cs** - Modal dialog for capturing hotkeys
  - Live keycap display (visual keyboard chips)
  - OnKeyDown handler captures key + modifiers
  - Real-time validation with error display
  - Save / Clear / Cancel buttons
  - Clear button resets without closing dialog

### Validation
- ✅ **HotkeyValidator.cs** - Comprehensive hotkey validation
  - Requires at least one modifier (Ctrl/Alt/Shift/Win)
  - Rejects system-reserved combos (Ctrl+Alt+Del, Win+L, Alt+Tab, Alt+F4, Win+D, etc.)
  - Checks against emergency lock hotkey
  - Checks against other brightness presets
  - Checks against increase/decrease/blur toggle hotkeys
  - Formats hotkeys as readable strings (e.g., "Ctrl+Shift+0")
  - Parses hotkey strings to HotkeyBinding objects

### Presets UI
- ✅ **BrightnessPage.xaml** - Added "Keyboard Shortcuts" expander
  - List of presets with NumberBox for percent
  - Hotkey display as formatted string
  - Edit button opens HotkeyCaptureDialog
  - Delete button (trash icon)
  - Add Preset button
  - Reset to Defaults button (with confirmation)

### View Models
- ✅ **BrightnessViewModel** - Enhanced with preset management
  - `Presets` ObservableCollection
  - `LoadPresets()` - Loads from settings
  - `AddPreset()` - Adds new 50% preset
  - `RemovePreset()` - Removes and saves
  - `ResetToDefaults()` - Restores default presets
  - `SaveSettings()` - Auto-saves to SettingsStore

- ✅ **BrightnessPresetViewModel** - Individual preset view model
  - Two-way binding for Percent
  - HotkeyDisplay computed property
  - UpdateHotkey() method
  - Auto-saves on any change

### Features Working

1. **Preset List** - Shows all brightness presets with percent and hotkey
2. **Edit Hotkey** - Opens capture dialog, validates, saves automatically
3. **Add Preset** - Creates new preset at 50% with no hotkey
4. **Delete Preset** - Removes preset and saves
5. **Reset Defaults** - Restores Ctrl+Shift+0-9, Ctrl+Shift+F presets
6. **Auto-Save** - Changes save immediately (no manual Save button)
7. **Validation** - Prevents duplicates, system hotkeys, conflicts
8. **Live Keycap Display** - Shows keys as you press them

## To Verify

1. Build: `.\build-with-msbuild.ps1`
2. Click **Others** → **Brightness**
3. Expand **Keyboard Shortcuts**
4. See default presets (0-100%)
5. Click **Edit** on any preset → **HotkeyCaptureDialog opens**
6. Press a valid combo (e.g., Ctrl+Alt+B) → Keycaps appear
7. Press invalid combo (e.g., just "A") → Error: "must include at least one modifier"
8. Try duplicate hotkey → Error: "already used for X% brightness"
9. Click **Save** → Hotkey updates, dialog closes
10. Change **Percent** in NumberBox → Saves automatically
11. Click **Delete** → Preset removed
12. Click **Add Preset** → New 50% preset appears
13. Click **Reset to Defaults** → Confirmation dialog → Resets all presets
14. Close app, reopen → Presets persist

## Known Issues

- Hotkeys not yet registered with HotkeyService (will be done in Milestone 5)
- No OSD display yet
- No actual brightness changes from hotkeys yet (just UI/settings)

## Git Commit

Please run manually:
```powershell
git add -A
git commit -m "Milestone 4: Hotkey capture dialog and presets management

- Implemented HotkeyCaptureDialog with live keycap display and validation
- Created HotkeyValidator with comprehensive validation rules
- Added presets management UI (add, edit, delete, reset defaults)
- Enhanced BrightnessViewModel with preset CRUD operations
- Created BrightnessPresetViewModel for individual presets
- Auto-save on all changes (no manual Save button)
- System-reserved hotkey detection
- Duplicate and conflict detection
- Visual keycap display in capture dialog"
```

## Next: Milestone 5
Register hotkeys with HotkeyService, implement preset actions, OSD, startup/exit options, "Reset display" hotkey.
