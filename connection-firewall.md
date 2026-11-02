# Internet Access (Connection Firewall) Page — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, with the same MVVM structure, services, and Fluent look.

**Page name:** the sidebar item and page title are **"Internet Access"** (icon: Segoe Fluent `\uE774` Globe or `\uF4C3` with a block overlay). Internally the feature is called *Connection Firewall*. (Note: the sidebar already has a globe icon reserved for the optional Websites page. Use a different icon here, for example `\uE839` Wifi/`\uEB5E` NetworkTower, so they are not confused.)

The page lets the user **block or unblock internet access for specific applications** with a simple UI, using Windows Defender Firewall rules. It supports Light / Dark / System theme like the other pages (theme resources only, no hard-coded colors).

## 1. Reference: the user's existing batch script (behavior to reproduce, bugs to avoid)

The user has a `.bat` script using `netsh advfirewall` that asks for: an action (1 Block Outbound, 2 Block Inbound, 3 Block Both, 4 Enable Outbound, 5 Enable Inbound, 6 Enable Both), the program path (blank = all programs), and a rule name. It deletes existing rules for that program and direction, then adds a new rule with `profile=any enable=yes`. For "Both" it creates two rules named `<name> (Outbound)` and `<name> (Inbound)`.

Reproduce the capabilities (block/allow, inbound/outbound/both, per program, custom rule name) in the UI, but **fix these problems**:
1. The script runs `delete rule name=all program=... dir=...`, which **deletes every firewall rule for that program, including rules the user or Windows created**. AppLock must only create, change, and delete **its own rules** (identified by a rule group, see section 3).
2. Blank path becomes a literal `"%"` and does not mean "all programs". Do **not** offer "all programs" as a simple option (see safety rules).
3. "Enable" in the script adds an *allow* rule. In Windows Firewall a **block rule always overrides an allow rule**, so allow rules do not undo other block rules. In AppLock, "unblock/allow" means **removing or disabling AppLock's own block rule** for that app.
4. Don't parse `netsh` text. Use the Windows Firewall COM API (section 4).

## 2. Page layout (native Fluent UI)

Command bar: **Add app** (dropdown: Browse for program…, From running programs…, Pick a window…, From installed apps…), **Refresh**, **Block all selected**, **Unblock all selected**, **Remove**, and **More** (Import/Export rules, Repair rules, Remove all AppLock rules, Firewall status).

Two tabs (Pivot/SelectorBar):

### 2.1 Tab "Apps" (rules)
A searchable list (`ListView`, one row per app, multi-select supported). Each row shows:
- App icon, display name, full path (trimmed with tooltip), publisher when available
- **Internet access ToggleSwitch**: *Allowed* (off = blocked). Toggling it adds/removes the block rules instantly (UAC/elevation permitting).
- Status chip: **Blocked**, **Allowed**, **Partially blocked** (only in or only out), **Rule missing**, **App not found** (file moved/deleted)
- A **direction selector** (SegmentedControl or dropdown): *Outgoing*, *Incoming*, *Both* (default Both, matching option 3 in the script)
- An expander with advanced options: profiles (Domain / Private / Public, all on by default), custom rule name (default `AppLock – <App name>`), notes, "Block for…" timer (see 5.5), and "Open in Windows Firewall" (opens `wf.msc`)

Empty state: icon plus "No apps added. Add an app to control its internet access."

### 2.2 Tab "Active connections"
A live table (refresh every 3 s while visible) of processes that currently have network connections: icon, process name, PID, path, protocol (TCP/UDP), local → remote address:port, state, and a **Block** button per process (adds the app to the Apps tab and blocks it). Filter box and "Show only apps with internet connections" toggle. Source: IP Helper API `GetExtendedTcpTable` / `GetExtendedUdpTable` with `OWNING_PID` mapped to the process image path (resolve service host processes to a name when possible; otherwise label "Windows service (svchost)" and prevent one-click blocking, see safety rules).

### 2.3 Banners (InfoBar at top)
- Not elevated: "Administrator rights are needed to change firewall rules." with **Restart as administrator** (the page is read-only until then).
- Windows Defender Firewall is off for the active profile, or the firewall service (`mpssvc`) is stopped, or a third-party firewall is registered: "Rules are saved but won't be enforced while Windows Defender Firewall is off." with an **Open Windows Security** button.
- A group policy/MDM controls the firewall and rules can't be changed: show the error clearly.

## 3. Rule model and naming

Use one **rule group**: `AppLock Connection Firewall` (shown in `wf.msc`). Every AppLock rule:
- `Name`: `AppLock – <display name> (Outbound)` or `(Inbound)`; if the user sets a custom name, use `<custom name> (Outbound/Inbound)`
- `Description`: `AppLock-managed|id=<GUID>` (used to match the rule to the app entry and to detect drift)
- `Grouping`: `AppLock Connection Firewall`
- `Action`: Block, `Enabled`: true while blocked, `Profiles`: selected profiles (default all), `Protocol`: any, `ApplicationName`: full exe path (for desktop apps)

AppLock stores its own app entries in the settings JSON (HMAC/DPAPI protection as in `instructions.md`):

```csharp
public class NetworkRuleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; }
    public string ExePath { get; set; }           // desktop apps
    public string? PackageFamilyName { get; set; } // Store/UWP apps
    public string? ServiceName { get; set; }       // optional, service-specific rules
    public bool BlockOutbound { get; set; } = true;
    public bool BlockInbound { get; set; } = true;
    public int Profiles { get; set; } = int.MaxValue; // NET_FW_PROFILE2_ALL
    public bool Blocked { get; set; }               // current desired state
    public DateTime? BlockedUntil { get; set; }     // timer, optional
    public string? Notes { get; set; }
}
```

The **Windows Firewall is the source of truth** for whether traffic is blocked; the settings file keeps the UI metadata and desired state. On each load/refresh, compare both and show **Rule missing** (entry says blocked, rule not found or disabled) or **Modified outside AppLock**, with a **Repair** action that recreates the rules.

## 4. Implementation (Windows Firewall COM API)

Create `IFirewallService` and a `WindowsFirewallService` using COM late binding (`Type.GetTypeFromProgID("HNetCfg.FwPolicy2")`, `HNetCfg.FWRule`) or the `NetFwTypeLib` interop (`INetFwPolicy2`, `INetFwRule`, `INetFwRule3`):
- Create: `Rules.Add(rule)` with Direction `NET_FW_RULE_DIR_OUT=2` / `IN=1`, Action `NET_FW_ACTION_BLOCK=0`, `Profiles = NET_FW_PROFILE2_ALL`, `Protocol = 256` (any), `Grouping`, `Description`, `ApplicationName`
- Update: toggle `Enabled` or modify in place; **block/unblock = add rule or remove rule** (default) or enable/disable (setting: "Keep rules when unblocking", off by default)
- Delete: `Rules.Remove(name)`, but only after confirming the rule's `Grouping` equals the AppLock group
- Read: enumerate `Rules` and filter by group
- **Store/UWP apps:** use `INetFwRule3.LocalAppPackageId` with the package SID from `PackageFamilyName` (derive with `DeriveAppContainerSidFromAppContainerName`). Get installed Store apps via `Windows.Management.Deployment.PackageManager` or the `Get-AppxPackage` equivalent.
- **Service rules (optional):** `ServiceName` on the rule for blocking a specific Windows service.
- Firewall state: `FirewallEnabled(profile)`, `CurrentProfileTypes`, default outbound action, `mpssvc` service state.
- Run COM calls on a background thread; handle `UnauthorizedAccessException` (not elevated), `COMException` 0x80070005, group-policy-locked errors, and duplicate rule names with clear user messages.
- Fallback only if COM fails: `netsh advfirewall firewall add rule ... group="AppLock Connection Firewall"`, never `name=all`.

Rules persist in Windows even if AppLock is closed or crashes. No background process is required for blocking to work (only for timers and schedules, section 5.5).

## 5. Features and behavior

### 5.1 Add an app
- **Browse:** file picker for `.exe` (also accept dropping an `.exe` or a `.lnk` onto the page, resolving shortcuts to their targets)
- **From running programs:** reuse the app's running-programs list (same window/process enumerator and icons as the Protection page), show only apps not already added
- **Pick a window:** reuse the existing "Pick a window" tool; resolve the window to its process path
- **From installed apps:** list Start menu apps (desktop and Store), searchable; selecting adds it
- Duplicate paths are rejected with "Already added".
- Adding an app does not block it automatically unless the user ticks "Block immediately" (default on) in the add dialog, where they also choose direction (Outgoing / Incoming / Both) and may rename the rule.

### 5.2 Block / unblock
- Toggling the switch calls the firewall service, then re-reads the rule to confirm and shows a result in the row (success tick or error message). Use optimistic UI with rollback on failure.
- **Unblock** removes (or disables) AppLock's block rules for that app only.
- Bulk actions apply to all selected rows, with one summary message ("3 blocked, 1 failed: access denied").
- Already-running processes: blocking applies to new connections immediately; existing connections may continue until they reconnect. Offer a **Close existing connections** helper (optional): `SetTcpEntry(MIB_TCP_STATE_DELETE_TCB)` for that process's TCP connections (admin required), or offer "End process" with a confirmation. State this clearly in the UI.

### 5.3 Safety rules (important)
- Never offer an "all programs" rule in the simple UI. If an advanced "Block everything outbound" option is ever added, require typing a confirmation phrase and the AppLock admin password, and provide a visible **Panic: Remove all AppLock rules** button.
- Warn (confirm dialog) when blocking: system-critical executables (`svchost.exe`, `lsass.exe`, `services.exe`, `System`, `wininit.exe`, `csrss.exe`, `winlogon.exe`, `explorer.exe`, `MsMpEng.exe`, Windows Update / `wuauserv`), AppLock itself, or the app's own helper processes. Block these from the one-click Active-connections button entirely.
- Show a one-time explanation when the first rule is created: what AppLock changes in Windows Firewall and how to remove it.

### 5.4 Protect against bypass (consistent with AppLock security)
- Setting: **"Require AppLock admin password to unblock or remove rules"** (default on). Unblocking, removing an app, "Remove all", Import, and changing direction/profiles need the admin password or Windows Hello (cache verification max 2 minutes). Blocking never needs it.
- Show AppLock's tamper note in the page footer: standard users can't edit firewall rules without elevation, but anyone with admin rights can edit them in `wf.msc`; AppLock detects and offers to repair such changes.

### 5.5 Timers and schedules (optional but recommended)
- **Block for…** in the row expander and quick menu: 15 min, 1 hour, until tomorrow, custom, or until I unblock. When the timer ends, AppLock unblocks automatically.
- **Scheduled blocking:** reuse the `TimeBlock` model from `self-discipline.md` (start, end, days) per app: block during these periods, allow otherwise. Evaluate with the same clock service, handle midnight spanning and time changes.
- Timers and schedules need AppLock running in the tray/background (or the startup task from `bitlocker.md`, if present). If AppLock is not running when a timer expires, process the expired entries at next launch and show a notice. Never leave a "timed" block that has silently expired without unblocking when AppLock starts.

### 5.6 Integration with locked programs (optional)
Add a menu item in the Lock dropdown (after "Self-Discipline"): **Internet access…** which opens a flyout to quickly block/unblock internet for that locked app (creates the same rule entry).

### 5.7 Import / export
Export entries to a JSON file (without secrets) and import them with a preview and conflict handling. Imported entries are not applied until the user confirms.

### 5.8 Blocked attempts log (phase 2, optional)
Offer to enable firewall auditing (`auditpol /set /subcategory:"Filtering Platform Connection" /failure:enable`) and show recent blocked connections from Security event 5157 for AppLock-managed apps (app, remote IP, port, time). Make it opt-in with an explanation, because it increases log volume. Do not enable it silently.

## 6. Settings

- Require admin password to unblock/remove rules (default on)
- Keep rules when unblocking (disable instead of delete; default off)
- Show confirmation before blocking system apps (always on, not changeable)
- Refresh interval for Active connections (3 / 5 / 10 s)
- Show notifications when a timer ends or a rule needs repair (follows "Mute notifications")
- On uninstall/removal: offer to remove all AppLock firewall rules

## 7. Project structure additions

```
src/AppLock/
  Views/InternetAccessPage.xaml(.cs), Views/Dialogs/(AddAppDialog, InstalledAppsDialog, BlockTimerDialog, ImportExportDialog, FirewallStatusDialog)
  ViewModels/InternetAccessViewModel.cs, NetworkRuleViewModel.cs, ActiveConnectionViewModel.cs
  Services/Firewall/
    IFirewallService.cs / WindowsFirewallService.cs   // COM wrapper
    RuleSyncService.cs        // drift detection, repair
    ActiveConnectionService.cs // IP Helper tables
    InstalledAppsService.cs   // desktop + Store apps, icons
    BlockTimerService.cs      // timers and schedules
  Models/NetworkRuleEntry.cs, ActiveConnection.cs
tests/AppLock.Tests/Firewall/  // fake IFirewallService: rule naming, group safety, drift detection, timers, schedule evaluation
```

## 8. UX and quality rules

- Fully theme-aware (Light, Dark, System, live switching), accessible (keyboard, screen reader names, high contrast); status is not communicated by color alone.
- Localize all strings via the existing resource file.
- Keep the UI responsive: COM and IP Helper calls on background threads, with cancellation.
- Never delete or modify a firewall rule that is not in the `AppLock Connection Firewall` group.
- Log actions (add, remove, block, unblock, repair) to `%APPDATA%\AppLock\logs`, without sensitive data.
- No network calls or telemetry.

## 9. Milestones (build, run, verify each before moving on)

1. Nav item, page shell with Apps/Active connections tabs, elevation and firewall-status InfoBars, theme-correct empty state.
2. `WindowsFirewallService` (COM): create/read/remove AppLock-group rules; unit tests with a fake.
3. Add app (browse, drag-and-drop, running programs, pick a window) and Apps list with block toggle, direction selector, status chips; verify with a real app.
4. Drift detection and Repair, "App not found", bulk actions, Remove all AppLock rules.
5. Installed apps picker including Store/UWP apps; safety warnings for system executables.
6. Active connections tab with per-process Block.
7. Admin-password gate for unblock/remove; settings; import/export.
8. Timers and scheduled blocking; optional Lock-dropdown integration.
9. (Optional) Blocked attempts log; theme and accessibility pass; tests; manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors/warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 10. Manual test checklist

- [ ] Non-elevated: page is read-only with the Restart as administrator banner; elevated: all actions enabled
- [ ] Add `chrome.exe` or another browser, block Both: web pages stop loading for that browser; other apps still work
- [ ] Unblock restores access; the rule disappears from `wf.msc` (group "AppLock Connection Firewall"), and no other rules were touched
- [ ] Outgoing-only and Incoming-only produce just one rule each
- [ ] Delete an AppLock rule in `wf.msc` → page shows "Rule missing"; Repair recreates it
- [ ] Move/delete the exe → "App not found" status
- [ ] A Store app (e.g. Microsoft Store apps) can be blocked via package ID
- [ ] Active connections lists the browser's connections; Block button works; svchost/system entries can't be blocked in one click
- [ ] Blocking `svchost.exe`, `explorer.exe`, or AppLock shows the warning
- [ ] Unblock/remove asks for the admin password (when the setting is on) and blocking does not
- [ ] "Block for 15 minutes" unblocks automatically; a schedule blocks only in the set window; expired timers are handled after restarting AppLock
- [ ] Firewall turned off or third-party firewall present → warning banner
- [ ] Light, Dark, System themes look correct; closing AppLock leaves rules in force
- [ ] Remove all AppLock rules removes only the AppLock group
