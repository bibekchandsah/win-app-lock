# Milestone 4: Drift Detection and Repair - COMPLETE

## Implementation Summary

### Files Created:

1. **Services/Firewall/RuleSyncService.cs** - Core drift detection and repair logic:
   - `DetectDriftAsync()` - Checks all entries against actual firewall rules
   - `CheckEntryDriftAsync()` - Checks single entry for drift
   - `RepairDriftAsync()` - Repairs a single drift
   - `RepairAllDriftAsync()` - Repairs all detected drift
   - `DriftType` enum with flags (OutboundMissing, InboundMissing, OutboundExtra, InboundExtra)
   - `DriftInfo` class with detailed drift information
   - `RepairResult` class with repair statistics

### Files Modified:

1. **ViewModels/InternetAccessViewModel.cs**:
   - Added `RuleSyncService` instance
   - Added `DriftCount` observable property
   - Updated `SyncWithFirewallAsync()` to use RuleSyncService
   - Added `RepairAllDriftAsync()` - returns RepairResult
   - Added `RepairSingleAsync()` - repairs individual app drift

2. **ViewModels/NetworkRuleViewModel.cs**:
   - Added `HasDrift` observable property
   - Added `RepairButtonVisibility` computed property
   - Added `OnHasDriftChanged` partial method to update visibility

3. **Views/InternetAccessPage.xaml**:
   - Added 6th column to ListView template for Repair button
   - Repair button with custom template (theme-aware hover states)
   - Visibility bound to `RepairButtonVisibility`
   - Tag binding for identifying which app to repair

4. **Views/InternetAccessPage.xaml.cs**:
   - Implemented `RepairRules_Click()` - repairs all drift
   - Added `RepairSingle_Click()` - repairs single app
   - Added `RepairAllDriftAsync()` helper with result dialog

5. **tests/AppLock.Tests/AppLock.Tests.csproj**:
   - Added RuleSyncService.cs link

## Features Implemented:

### Drift Detection ✅
- **Rule Missing**: App is marked as blocked, but firewall rule doesn't exist or is disabled
- **Modified Outside AppLock**: App is marked as allowed, but firewall rule exists
- **Partial Missing**: Only one direction rule missing (e.g., Outbound present but Inbound missing)
- **Status Display**: Yellow badge with "Rule missing" or "Modified outside AppLock"

### Drift Types (Flags Enum):
```csharp
OutboundMissing = 1    // Expected outbound rule missing
InboundMissing = 2     // Expected inbound rule missing
OutboundExtra = 4      // Unexpected outbound rule exists
InboundExtra = 8       // Unexpected inbound rule exists
```

### Repair Functionality ✅
- **Repair Single**: Button appears next to apps with drift, click to repair that app only
- **Repair All**: Click "More" → "Repair rules" to repair all detected drift
- **Repair Result Dialog**: Shows count of repaired and failed items
- **Automatic Refresh**: UI updates after repair to reflect current state

### Sync Behavior:
1. On page load: `LoadAppsAsync()` calls `SyncWithFirewallAsync()`
2. On refresh: User clicks Refresh → sync runs
3. Drift detected: `HasDrift` set to true, Repair button appears
4. After repair: Drift cleared, Repair button hidden, status updated

## How It Works:

### Detection Flow:
1. Get all NetworkRuleEntry items from settings
2. Get all actual firewall rules from Windows Firewall
3. For each entry, compare expected vs actual state:
   - If Blocked=true and BlockOutbound=true: expect "(Outbound)" rule
   - If Blocked=true and BlockInbound=true: expect "(Inbound)" rule
   - If Blocked=false: expect no rules
4. Flag mismatches as drift with specific type
5. Update ViewModel with drift info

### Repair Flow:
1. For missing rules (OutboundMissing/InboundMissing):
   - Call `CreateRuleAsync()` to recreate the rule
2. For extra rules (OutboundExtra/InboundExtra):
   - Call `RemoveRuleAsync()` to delete the unexpected rule
3. Return success/failure
4. Refresh UI to show updated status

## UI Updates:

### ListView Item Template (6 columns):
1. Icon
2. Name/Path
3. Direction selector
4. Status chip (shows "Rule missing" in yellow when drift)
5. Block/Allow toggle
6. **Repair button** (only visible when HasDrift=true)

### Repair Button Style:
- Transparent background
- Hover: Subtle secondary fill
- Pressed: Subtle tertiary fill
- Accent text color
- Compact padding

## Testing Scenarios:

### Test 1: Rule Missing
```
1. Add Chrome, block it
2. Verify rules created in wf.msc
3. Close AppLock
4. Open wf.msc → manually delete "AppLock – Chrome (Outbound)" rule
5. Reopen AppLock → Internet Access page
6. See Chrome with yellow "Rule missing" badge
7. Repair button visible next to Chrome
8. Click Repair
9. Rule recreated, badge changes to "Blocked" (red)
10. Repair button disappears
```

### Test 2: Modified Outside AppLock
```
1. Add Edge, keep it allowed (unblocked)
2. Close AppLock
3. Open wf.msc → manually create rule "AppLock – Edge (Outbound)"
4. Reopen AppLock → Internet Access page
5. See Edge with yellow "Modified outside AppLock" badge
6. Click Repair
7. Extra rule removed
8. Badge changes to "Allowed" (green)
```

### Test 3: Bulk Repair
```
1. Add multiple apps, block them
2. Close AppLock
3. Delete several rules in wf.msc
4. Reopen AppLock → see multiple apps with "Rule missing"
5. Click "More" → "Repair rules"
6. Dialog shows "Repaired: X, Failed: 0"
7. All apps show correct status
8. All Repair buttons disappear
```

### Test 4: App Not Found
```
1. Add an app, block it
2. Delete the .exe file
3. Refresh page
4. Status shows "App not found" (gray badge)
5. No Repair button (different issue type)
```

## Code Quality:

- ✅ Async/await throughout
- ✅ Proper error handling with Debug logging
- ✅ Observable properties with change notifications
- ✅ Flags enum for drift types (supports combinations)
- ✅ MVVM pattern maintained
- ✅ Reuses existing IFirewallService
- ✅ Comprehensive drift scenarios covered
- ✅ Theme-aware button template

## Build & Test:

### Build Command:
```powershell
.\build-with-msbuild.ps1
```

### Launch:
```powershell
.\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe
```

### Verify Drift Detection:
1. Add Chrome, block it
2. Manually delete rule in wf.msc
3. Refresh AppLock page
4. See "Rule missing" with Repair button
5. Click Repair
6. Rule recreated

## Ready for Milestone 5:
Foundation complete for detecting and repairing drift. Next: Installed apps picker (Desktop + UWP).
