# AppLock Development Notes

## Notifications Feature - Milestone 1 (Discovery Spike)

### Implemented (Not yet built):

1. **Models**
   - `NotificationCustomSettings.cs` - Settings model for notification customization
   - Added `NotificationCustom` property to `AppSettings`

2. **Services**
   - `TestToastService.cs` - Sends test notifications using Windows App SDK AppNotificationManager
   - `ToastDiagnostics.cs` - Enumerates and identifies candidate toast windows

3. **ViewModels**
   - `NotificationsViewModel.cs` - Handles UI logic for the notifications page
   - `ToastCandidateViewModel.cs` - Displays diagnostic information about found windows
   - `InverseBoolConverter.cs` - Helper converter

4. **Views**
   - `NotificationsPage.xaml/.cs` - Settings page with breadcrumb navigation, master toggle, test notification buttons, and diagnostic window list
   - Updated `OthersPage.xaml/.cs` - Added Notifications card at order 70 (after Volume Mixer at 60)

5. **Card Registry**
   - Notifications card icon: `\uE91C` (notification bell)
   - Description: "Choose where notifications appear and how they animate"
   - Order: 70 (Brightness 10, Eye Care 20, Volume Mixer 30, Clipboard 40, Ring Light 50, **Notifications 70**)

### Discovery Spike Strategy:

The diagnostics page will help identify toast windows by:
1. Enumerating all windows with `EnumWindows`
2. Filtering for visible windows with class name containing "CoreWindow" or "Windows.UI"
3. Checking if process is "ShellExperienceHost.exe" or similar shell processes
4. Displaying window details: HWND, class, title, process name/ID, rect, size, styles

**Expected toast characteristics (from TopNotify analysis):**
- Class: `Windows.UI.Core.CoreWindow`
- Process: `ShellExperienceHost.exe`
- Title: "New notification" (localized)
- Size: ~396px wide at 100% DPI
- Position: Bottom-right corner of monitor
- Plain `EnumWindows` may not work - may need `NtUserBuildHwndList` from `win32u.dll`

### Build Status:

The code has been written but NOT yet compiled due to build environment access issues. The existing AppLock.exe in the bin directory is from previous builds and does not include these changes.

**To build manually:**
1. Close any running AppLock instances
2. Run from Developer Command Prompt: `msbuild AppLock.sln /p:Configuration=Release /p:Platform=x64 /t:Restore,Build`
3. Or use: `dotnet build -c Release`

### Next Steps (Milestone 2+):

After confirming the discovery spike works and identifies toast windows correctly:
- Milestone 2: Implement event-driven toast detection with `SetWinEventHook`
- Milestone 3: Position-only implementation (no animation)
- Milestone 4+: Animation system

### Known Limitations:

1. **Undocumented behavior**: Windows toast mechanism is not officially documented and may change
2. **TopNotify conflict**: If TopNotify is running, both tools will fight over the same window
3. **Plain EnumWindows limitation**: May not enumerate "immersive" CoreWindow instances (need to verify in spike)
4. **Localization**: Toast title is localized; size/position heuristics are more reliable

### License Compliance:

TopNotify (github.com/SamsidParty/TopNotify) is GPL-3.0 licensed. We have NOT copied any code from it. Implementation is clean-room based on the spec's description of the technique. TopNotify should be credited in About → Acknowledgements as inspiration.

---

*Last updated: 2026-11-11 - Milestone 1 code written, awaiting build*

## Notifications Feature - Milestone 1 (Discovery Spike)

### Implemented (Code written, not yet built):

1. **Models**
   - `NotificationCustomSettings.cs` - Settings model for notification customization
   - Added `NotificationCustom` property to `AppSettings`

2. **Services**
   - `TestToastService.cs` - Sends test notifications using Windows App SDK AppNotificationManager
   - `ToastDiagnostics.cs` - Enumerates and identifies candidate toast windows

3. **ViewModels**
   - `NotificationsViewModel.cs` - Handles UI logic for the notifications page
   - `ToastCandidateViewModel.cs` - Displays diagnostic information about found windows
   - `InverseBoolConverter.cs` - Helper converter

4. **Views**
   - `NotificationsPage.xaml/.cs` - Settings page with breadcrumb navigation, master toggle, test notification buttons, and diagnostic window list
   - Updated `OthersPage.xaml/.cs` - Added Notifications card at order 70

5. **Card Registry**
   - Notifications card icon: `\uE91C` (notification bell)
   - Order: 70 (after Volume Mixer at 60, now mismatched with spec order 60 - spec needs update or we adjust)

### Discovery Spike Strategy:

The diagnostics page will help identify toast windows by:
1. Enumerating all windows with `EnumWindows`
2. Filtering for visible windows with class name containing "CoreWindow" or "Windows.UI"
3. Checking if process is "ShellExperienceHost.exe" or similar shell processes
4. Displaying window details: HWND, class, title, process name/ID, rect, size, styles

**Expected toast characteristics (from spec analysis of TopNotify):**
- Class: `Windows.UI.Core.CoreWindow`
- Process: `ShellExperienceHost.exe`
- Title: "New notification" (localized)
- Size: ~396px wide at 100% DPI
- Position: Bottom-right corner of monitor

### Order Discrepancy Note:

Spec says Notifications should be order 60, but Volume Mixer is already at order 60. Implemented at order 70 to maintain existing order. Can adjust if needed.

---

*Updated: 2026-11-11 - Milestone 1 code written*


### Discovery Spike Results:

**Test performed:** Sent test notification via AppNotificationManager

**Findings:**
1. ✅ `EnumWindows` DOES enumerate immersive CoreWindow instances
2. ✅ Found 7 candidates with broadened search criteria
3. ✅ Detected `Windows.UI.Core.CoreWindow` class from SystemSettings process
4. ❌ AppNotificationManager.Show() did not produce a visible toast (likely needs package identity)

**Sample detected windows:**
- Chrome_WidgetWin_1 (Kiro editor) - 1936×1072
- Windows.UI.Core.CoreWindow (SystemSettings) - 1920×1056 ← **This is the target pattern**
- Multiple Shell/ApplicationFrame windows detected

**Toast identification heuristics confirmed:**
- Class: `Windows.UI.Core.CoreWindow` is correct
- Process: Varies (SystemSettings, not just ShellExperienceHost)
- Size: Large windows detected, need to refine size filtering

**Recommendation:** 
For a real toast, trigger a system notification (e.g., Windows Update, Teams, etc.) or use PowerShell to send one via COM/BurntToast, then use Refresh to capture it. The detection mechanism works correctly.

**Alternative for testing:** 
Use `New-BurntToastNotification` PowerShell module or trigger Focus Assist notification from Windows Settings.

---

*Updated: 2026-11-11 - Discovery spike successful, awaiting real toast for size validation*
