# Milestone 6 Complete: Active Connections Tab

## Status: ✅ Implementation Complete - Ready for Build & Test

## What Was Implemented

### 1. ActiveConnectionService
- **File**: `src/AppLock/Services/Firewall/ActiveConnectionService.cs`
- Uses Windows IP Helper API via CsWin32 PInvoke
- **TCP Connections**: `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_ALL`
- **UDP Connections**: `GetExtendedUdpTable` with `UDP_TABLE_OWNER_PID`
- Resolves PID to process name and path
- Port number byte-order conversion for correct display
- TCP state mapping (ESTABLISHED, LISTEN, SYN_SENT, CLOSE_WAIT, etc.)
- System process detection via `InstalledAppsService.IsSystemCritical()`

### 2. Active Connections Tab UI
- **File**: `src/AppLock/Views/InternetAccessPage.xaml`
- 6-column ListView: Process (name + path), PID, Protocol badge, Connection (local → remote), System badge, Block button
- Filter TextBox: Search by process, address, or protocol
- Toggle: "Show only connected" (filters to ESTABLISHED state with remote address)
- Auto-refresh status: "Auto-refreshing every 3 seconds"
- ProgressRing while loading

### 3. ActiveConnectionViewModel
- **File**: `src/AppLock/ViewModels/ActiveConnectionViewModel.cs`
- `LoadConnectionsAsync()`: Gets all TCP + UDP connections
- `StartAutoRefresh(intervalSeconds)`: Background refresh loop
- `StopAutoRefresh()`: Cancels refresh when tab hidden
- `FilterConnections()`: Client-side filtering by query and toggle
- Groups connections by process, shows counts in status

### 4. Block Button Integration
- Per-row Block button (disabled for system processes)
- System critical warning dialog before blocking
- Adds app to Apps tab with `blockImmediately: true`
- Success message shows app was added

### 5. Tab Lifecycle Management
- Auto-refresh starts when Active connections tab clicked
- Auto-refresh stops when switching to Apps tab
- Cleanup on page unload (`InternetAccessPage_Unloaded`)

## API Structures Used
```csharp
MIB_TCPTABLE_OWNER_PID    // TCP table header
MIB_TCPROW_OWNER_PID      // TCP connection row
MIB_UDPTABLE_OWNER_PID    // UDP table header
MIB_UDPROW_OWNER_PID      // UDP connection row
MIB_TCP_STATE             // TCP state enum
```

## Files Created
1. `src/AppLock/Services/Firewall/ActiveConnectionService.cs`
2. `src/AppLock/Models/ActiveConnection.cs`
3. `src/AppLock/ViewModels/ActiveConnectionViewModel.cs`

## Files Modified
1. `src/AppLock/Views/InternetAccessPage.xaml` - Replaced placeholder with full UI
2. `src/AppLock/Views/InternetAccessPage.xaml.cs` - Added ViewModel, auto-refresh, Block handler

## How to Test

### Build
```powershell
.\build-with-msbuild.ps1
```
Expected: 0 errors, 1 warning (Windows SDK mismatch)

### Launch & Verify
1. **Navigate to Active connections tab**
   - Should immediately start loading connections
   - Status shows "X processes with connections (Y total)"

2. **Check live connections**
   - Browser connections (Chrome, Edge, Firefox) should show ESTABLISHED to various IPs
   - System processes (svchost) should show "System Process" badge and Block button disabled
   - User processes should have enabled Block buttons

3. **Test filter**
   - Type "chrome" → Only Chrome connections visible
   - Type "443" → Only HTTPS connections visible
   - Clear filter → All connections back

4. **Test "Show only connected" toggle**
   - ON (default): Only ESTABLISHED connections with remote addresses
   - OFF: All connections including LISTEN, UDP listeners, etc.

5. **Block a process**
   - Find a non-system process (e.g., Discord, Spotify)
   - Click Block → See confirmation message
   - Switch to Apps tab → Verify app added and blocked

6. **System process protection**
   - Find svchost.exe in list
   - Block button should be disabled
   - Hover tooltip explains it's a system process

7. **Auto-refresh**
   - Watch connections list update every 3 seconds
   - Open a new browser tab → New connection appears within 3 seconds
   - Switch to Apps tab → Auto-refresh stops
   - Switch back → Auto-refresh resumes

## Git Commit
```bash
git add -A
git commit -m "Milestone 6: Active connections tab with IP Helper API

- ActiveConnectionService using GetExtendedTcpTable/GetExtendedUdpTable
- Live monitoring of TCP/UDP connections with process resolution
- Per-process Block button with system process safety
- Auto-refresh every 3 seconds while tab visible
- Filter by process/address/protocol, toggle for connected only
- System badge for critical processes with disabled Block button"
```

## Known Issues
None

## Next Milestone
**Milestone 7**: Admin password gate for unblock/remove, settings, import/export
