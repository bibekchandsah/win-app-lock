# Milestone 5 Complete: Installed Apps Picker & Safety Warnings

## Status: ✅ Implementation Complete - Ready for Build & Test

## What Was Implemented

### 1. Installed Apps Dialog
- **File**: `src/AppLock/Views/Dialogs/InstalledAppsDialog.xaml` and `.xaml.cs`
- Searchable dialog listing desktop and UWP/Store apps
- Multi-select support with "Add selected" button
- Real-time search filtering
- Status text showing app count and loading state
- "Store" badge for UWP apps

### 2. InstalledAppsService
- **File**: `src/AppLock/Services/Firewall/InstalledAppsService.cs`
- **Desktop apps**: Scans Start Menu (.lnk shortcuts) from CommonStartMenu and StartMenu folders
- **UWP apps**: Uses `Windows.Management.Deployment.PackageManager` to enumerate Store apps
- **Shortcut resolution**: Uses WScript.Shell COM to resolve .lnk to .exe paths
- **Publisher extraction**: Uses FileVersionInfo to get company name
- **System critical list**: 15+ critical processes (svchost, explorer, lsass, dwm, etc.)
- **Service list**: 5+ critical services (wuauserv, mpssvc, WinDefend, etc.)

### 3. System Critical Process Warnings
- Warning dialog before blocking any system-critical executable
- Explains risks: "system instability, prevent Windows updates, interfere with security features"
- Integrated into ALL add workflows:
  - Browse for program
  - From running programs
  - Pick a window
  - **From installed apps** (new)
- User must explicitly confirm "Block Anyway"

### 4. ViewModel Updates
- **InstalledAppsDialogViewModel**: Manages dialog state, search filtering, selection
- **InstalledAppViewModel**: Wrapper for InstalledApp with UI-friendly properties
- **InternetAccessViewModel**: 
  - Added `InstalledAppsService` dependency
  - Updated `AddAppAsync` to support `packageFamilyName` parameter
  - Added `GetExistingPaths()` to filter already-added apps

### 5. UWP App Support
- `NetworkRuleEntry.PackageFamilyName` property support
- Duplicate detection by PackageFamilyName OR ExecutablePath
- Display "Package: {PackageFamilyName}" instead of file path

## Files Created
1. `src/AppLock/Views/Dialogs/InstalledAppsDialog.xaml`
2. `src/AppLock/Views/Dialogs/InstalledAppsDialog.xaml.cs`
3. `src/AppLock/ViewModels/InstalledAppsDialogViewModel.cs`
4. `src/AppLock/Services/Firewall/InstalledAppsService.cs`

## Files Modified
1. `src/AppLock/Views/InternetAccessPage.xaml.cs` - Added FromInstalledApps_Click, ShowSystemCriticalWarningAsync, safety checks in all add methods
2. `src/AppLock/ViewModels/InternetAccessViewModel.cs` - Added InstalledAppsService, packageFamilyName support, GetExistingPaths()

## Compilation Errors Fixed
1. ❌ **CS0246: AppListEntry not found** → ✅ Simplified to `package.DisplayName`
2. ❌ **CS0117: PInvoke.CoInitialize** → ✅ Replaced with WScript.Shell COM
3. ❌ **CS0246: IShellLinkW not found** → ✅ Replaced with WScript.Shell COM
4. ❌ **WMC0909: Cannot resolve DataType local:InstalledAppViewModel** → ✅ Added xmlns:vm namespace
5. ❌ **CS0111: Duplicate RepairAllDriftAsync** → ✅ Removed duplicate method (from Milestone 4)

## How to Test

### Build
```powershell
.\build-with-msbuild.ps1
```
Expected: 0 errors, 1 warning (Windows SDK version mismatch - not critical)

### Launch
```powershell
.\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe
```

### Verification Steps
1. **Installed Apps Dialog**:
   - Click "Add app" → "From installed apps"
   - Verify desktop apps from Start Menu appear
   - Verify UWP apps appear with "Store" badge
   - Search for an app (e.g., "Chrome", "Store")
   - Select multiple apps → Click "Add selected"

2. **System Critical Warning**:
   - Try adding "Windows Explorer" from running programs
   - Verify warning dialog appears with risks explanation
   - Click "Cancel" → App not added
   - Try again → Click "Block Anyway" → App added

3. **UWP App Blocking**:
   - Add a Store app (e.g., "Microsoft Store")
   - Verify it shows "Package: Microsoft.WindowsStore_..." in path
   - Toggle block switch
   - Verify firewall rule created (check wf.msc → AppLock Connection Firewall group)

4. **Duplicate Prevention**:
   - Add Chrome from "Browse"
   - Try adding Chrome again from "Installed apps"
   - Verify it's filtered out (not in list)

## Git Commit
```bash
git add -A
git commit -m "Milestone 5: Installed apps picker and system critical warnings

- Added InstalledAppsDialog for selecting desktop and UWP/Store apps
- InstalledAppsService scans Start Menu .lnk files and PackageManager
- System critical process warnings (svchost, explorer, lsass, etc.)
- Safety checks in all Add app workflows
- UWP apps tagged with Store badge and PackageFamilyName support
- Fixed XAML namespace and API compatibility issues"
```

## Known Issues
None - all compilation errors resolved

## Next Milestone
**Milestone 6**: Active connections tab with per-process Block button
