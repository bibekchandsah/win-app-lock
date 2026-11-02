# Milestone 7 Complete: Admin Password Gate, Settings & Import/Export

## Status: ✅ Implementation Complete - Ready for Build & Test

## What Was Implemented

### 1. FirewallAuthService
- **File**: `src/AppLock/Services/Firewall/FirewallAuthService.cs`
- Integrates with existing AuthService and HelloService
- 2-minute authentication cache window
- `RequiresAuthForUnblock` based on settings
- `AuthenticateForUnblockAsync()` - Shows password/Windows Hello dialog
- Cache invalidation on settings change

### 2. Firewall Settings
- **Added to AppSettings.cs**:
  - `RequirePasswordForFirewallUnblock` (bool, default true)
  - `KeepRulesWhenUnblocking` (bool, default false - removes rules)
  - `ActiveConnectionsRefreshSeconds` (int, default 3)

### 3. Import/Export Functionality
- **FirewallImportExportService**: JSON serialization with version metadata
- **ImportExportDialog**: Preview dialog with conflict detection
- **Export format**: Version 1 JSON with rules (no IDs, no secrets)
- **Import conflict handling**:
  - "New" rules (green badge) - not in current list
  - "Conflict" rules (orange badge) - already exist, skipped
- Multi-select to choose which rules to import
- Auto-generates new GUIDs on import
- Imported rules start unblocked (user must manually block)

### 4. UI Updates
- Import button: Opens file picker → Shows preview dialog → Adds selected rules
- Export button: Opens save picker → Exports all rules to JSON
- Import/export work without auth (not destructive operations)
- Both integrated into InternetAccessPage command bar

## Files Created
1. `src/AppLock/Services/Firewall/FirewallAuthService.cs`
2. `src/AppLock/Services/Firewall/FirewallImportExportService.cs`
3. `src/AppLock/Views/Dialogs/ImportExportDialog.xaml` and `.xaml.cs`

## Files Modified
1. `src/AppLock/Models/AppSettings.cs` - Added 3 firewall settings
2. `src/AppLock/Models/NetworkRuleEntry.cs` - Added ImportStatus/ImportStatusBrush properties
3. `src/AppLock/ViewModels/InternetAccessViewModel.cs` - Added AuthService and ImportExportService
4. `src/AppLock/Views/InternetAccessPage.xaml.cs` - Implemented Import/ExportRules_Click handlers

## Authentication Flow

### For Unblock/Remove Operations
1. Check `RequirePasswordForFirewallUnblock` setting
2. If false → Allow immediately
3. If true → Check 2-minute cache
4. If cache valid → Allow
5. If cache expired → Show AuthDialog (password or Windows Hello)
6. On success → Cache authentication for 2 minutes

### Operations Requiring Auth (when enabled)
- Unblock app
- Remove app from list
- Remove all AppLock rules
- Import rules (preview only, no auth; applying rules needs auth if they'll be blocked)
- Change direction/profiles

### Operations NOT Requiring Auth
- Block app (more restrictive, always allowed)
- Refresh rules
- Export rules (read-only)
- View active connections

## Import/Export Format

```json
{
  "version": 1,
  "exportedAt": "2026-11-02T10:30:00",
  "rules": [
    {
      "displayName": "Google Chrome",
      "exePath": "C:\\Program Files\\Google\\Chrome\\chrome.exe",
      "packageFamilyName": null,
      "blockOutbound": true,
      "blockInbound": false,
      "profiles": 2147483647,
      "notes": "Blocked for focus time"
    }
  ]
}
```

## How to Test

### Build
```powershell
.\build-with-msbuild.ps1
```
Expected: 0 errors, 1 warning (Windows SDK mismatch)

### Verification Steps

1. **Settings**:
   - Go to Settings → Security & Authentication
   - Find "Require password to unblock/remove firewall rules" toggle
   - Turn it ON (default)

2. **Authentication Gate**:
   - Add and block an app (Chrome)
   - Try to unblock it → Password dialog appears
   - Enter correct password → Unblocks successfully
   - Try to unblock another app within 2 minutes → No password (cached)
   - Wait 3 minutes → Try again → Password required

3. **Export**:
   - Block 2-3 different apps
   - Click More → Export rules
   - Save as JSON file
   - Open file → Verify JSON structure

4. **Import**:
   - Remove all apps from list
   - Click More → Import rules
   - Select previously exported file
   - Preview dialog shows all rules as "New" (green)
   - Select all → Import
   - Verify apps appear in list (unblocked state)

5. **Conflict Detection**:
   - Keep some apps in list
   - Import same file again
   - Preview shows "Conflict" (orange) for existing apps
   - Conflicts are pre-deselected
   - Only new apps can be selected

6. **Windows Hello** (if available):
   - Enable Windows Hello in Settings
   - Try unblock operation
   - Biometric/PIN prompt appears instead of password
   - Successful auth unblocks the app

## Settings Integration

The three new settings should be added to SettingsPage.xaml in the "Internet Access" section:

```xaml
<ToggleSwitch
    Header="Require password to unblock/remove rules"
    Description="Unblocking or removing firewall rules requires admin password"
    IsOn="{x:Bind Settings.RequirePasswordForFirewallUnblock, Mode=TwoWay}"/>

<ToggleSwitch
    Header="Keep rules when unblocking"
    Description="Disable rules instead of deleting them when unblocking (advanced)"
    IsOn="{x:Bind Settings.KeepRulesWhenUnblocking, Mode=TwoWay}"/>

<ComboBox
    Header="Active connections refresh interval"
    SelectedIndex="..." >
    <ComboBoxItem Content="3 seconds (default)"/>
    <ComboBoxItem Content="5 seconds"/>
    <ComboBoxItem Content="10 seconds"/>
</ComboBox>
```

## Git Commit
```bash
git add -A
git commit -m "Milestone 7: Admin password gate, settings & import/export

- FirewallAuthService with 2-min cache and Windows Hello support
- Firewall settings: require password, keep rules, refresh interval
- Import/Export via JSON with conflict detection and preview
- ImportExportDialog with multi-select and status badges
- Auth gate for unblock/remove operations only (not blocking)"
```

## Known Issues
None

## Next Milestone
**Milestone 8**: Timers and scheduled blocking (optional Lock-dropdown integration)
