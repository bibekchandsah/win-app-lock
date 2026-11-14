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

## Speed Meter — Milestone 1 (card, page shell, preview)

- Card in the Others registry: id `speedmeter`, icon `\uEC4A` (SpeedHigh), title "Speed Meter",
  description "Live download and upload speed in a floating widget".
- **Order 86, not 70**: the spec asks for 70, but Notifications already uses 70 (see the note above).
  The user also asked for the card "next to the wifi card", so it sits directly after Wi-Fi (85).
  Nothing else was renumbered.
- `Models/SpeedMeterSettings.cs` holds the full section-7 model (settings, `ConnectionAlertSettings`,
  `SpeedMeterPreset`) and is persisted as `AppSettings.SpeedMeter`. Only `Enabled`/`StartWithAppLock`
  are used so far. `ToggleHotkey` is stored as the same "Ctrl+Alt+N" string the other hotkey sections
  use; there is no `HotkeyBinding` type in this codebase, so one was not invented.
- `Views/SpeedMeterPage.xaml(.cs)`: breadcrumb (Others › Speed Meter) + back button, master
  "Show speed meter" toggle, "Start with AppLock" toggle, and a themed preview strip with a mock
  widget. Everything uses theme resources (`CardBackgroundFillColorDefaultBrush`,
  `LayerFillColorDefaultBrush`, `TextFillColor*`, `AccentFillColorDefaultBrush`) so Light/Dark/System
  follow the app theme with the same accent as the other pages.
- The preview is still a static mock: it becomes the real widget control fed with live data once
  `NetworkStatsProvider` and the widget window exist (milestone 2/3).
- No network requests, no timers, no windows created yet.
- Tests project links the new model file (`tests/AppLock.Tests/AppLock.Tests.csproj`), otherwise the
  solution build fails with CS0246.
- Pre-existing build warnings (mostly CS8618/CS1998 in Audio, VolumeMixer, App.xaml.cs) were left
  untouched; this milestone added none.

## Speed Meter — Milestone 2 (engine + widget window)

- `Services/SpeedMeter/NetworkStatsProvider.cs`: adapter selection plus byte-counter deltas
  measured with `Stopwatch`, a 3-sample average, counter-reset handling (negative delta → 0) and
  per-adapter baselines. Uses the managed `NetworkInterface.GetIPStatistics()` (same 64-bit octets
  as `GetIfTable2`) instead of hand-marshalling `MIB_IF_ROW2`; the spec lists this API as the
  fallback, and it avoids a 1.3 KB struct definition with no behavioural loss.
- Adapter logic: *Auto* = an adapter that carries a default gateway (`GatewayAddresses`), fastest
  link first. .NET does not expose the IPv4 route metric (`IPv4InterfaceProperties` has no
  `InterfaceMetric`), so metric ordering was not possible without IP Helper; the gateway + speed
  heuristic selects the same adapter in practice. *AllPhysical* and *Specific* also work.
  Virtual adapters (Hyper-V, vEthernet, VMware, VirtualBox, WSL, TAP, Tailscale, …) are excluded
  unless `IncludeVirtualAdapters` is on.
- `Views/SpeedWidgetWindow.xaml(.cs)`: borderless `OverlappedPresenter`, always-on-top, not
  resizable/minimizable, hidden from switchers, DWM glass (`DwmExtendFrameIntoClientArea(-1)` +
  NC rendering disabled, same technique as the volume overlay) so the rounded card floats with
  per-pixel transparency. `WS_EX_NOACTIVATE` is applied after the first frame. `WS_EX_TOOLWINDOW`
  is deliberately **not** applied: the volume overlay had to remove it because it made the window
  invisible in this app — `AppWindow.IsShownInSwitchers = false` already keeps it out of Alt+Tab and
  the taskbar.
- Size is measured from the card (`DesiredSize × RasterizationScale`) and the window is placed at
  `PositionPercentX/Y` (98/90) of the monitor work area. Dragging, snapping, multi-monitor and DPI
  change handling are milestone 4; monitor choice is still always the primary one.
- `SpeedMeterService`: 1 s `DispatcherQueueTimer` (interval from settings, 0.5–2 s), sampling on a
  background thread with one-at-a-time guard, UI update marshalled back; owns the widget window and
  stops/closes it when the feature is turned off. `ServiceContainer.InitializeSpeedMeter` restores
  the widget at startup when *Enabled* and *Start with AppLock* are both on.
- Card subtitle now shows the live rates (`On · ↓ … ↑ …`), and the page's status line shows the
  adapter, link speed and current rates.
- Still no network requests. Nothing is drawn while the widget is disabled (no timer, no window).

## Speed Meter — Milestone 3 (appearance, presets, live preview)

- `Views/Controls/SpeedWidgetControl.xaml(.cs)` is now the single widget surface. The floating
  window and the settings preview both host it, so the preview is the real control and every change
  is visible before the widget is enabled.
- Presets: Transparent (no background, text only), Glass (background color at the opacity setting),
  Solid (fully opaque background color), Follow Windows (background/text swap with the app's
  light/dark theme) and Custom. Color, opacity, scale (50–250 %), corner radius (0–40), font
  family, font weight, arrows vs `DL/UL`, layout (horizontal/vertical) and compact padding all
  apply live; the window re-measures and re-anchors after each change.
- Installed fonts come from `CanvasTextFormat.GetSystemFontFamilies()` (Win2D is already a
  dependency), with a curated fallback list if enumeration fails.
- Settings are saved on every change and pushed to the widget through
  `SpeedMeterService.ApplySettings`. `Reset appearance` restores the model defaults.
- Added `SpeedMeterSettings.FontWeight` to the model (the spec listed font weight but the data model
  in section 7 had no field for it).
- Deliberately simplified, still to do: drop shadow toggle, per-border color (the border is tinted
  from the text color), accessibility contrast warning for the download/upload colors, and a
  "Glass" acrylic blur (Glass currently uses the same translucent fill as the volume overlay
  rather than a real backdrop, because a backdrop cannot be per-widget).
- Range validation happens where values are applied (`Math.Clamp` on opacity, scale, radius,
  decimals, interval) instead of on deserialization.

## Speed Meter — Milestone 4 (drag, snap, click-through, full-screen, capture)

- Dragging is pointer-based: press anywhere on the widget already moves with the cursor, release
  snaps to a nearby edge (16 px detection, 8 px margin) and the position is stored as percentages of
  the monitor work area. `Lock position` disables dragging.
- Click-through sets `WS_EX_TRANSPARENT`; a 150 ms timer watches `GetAsyncKeyState(VK_MENU)` so
  holding Alt temporarily makes the widget clickable again for dragging.
- `Reset position` (page button) restores 98 %/90 % on the primary monitor.
- `Hide in full-screen apps` uses `SHQueryUserNotificationState` (busy / D3D full-screen /
  presentation) checked on every sampling tick; the window is hidden with `AppWindow.Hide()` and
  shown again with a topmost re-assert.
- `Hide from screen sharing` uses `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- The show/hide shortcut (default `Ctrl+Alt+N`, stored as a `HotkeyBinding`) is registered through
  the shared `HotkeyService` and stays registered while the widget is off so it can turn it back on.
  It is rebindable on the page with the shared hotkey capture dialog. Changing `ToggleHotkey` from a
  string to `HotkeyBinding` matches the other hotkey sections.
- Monitor handling: the monitor is stored by its work-area rect and re-used on restart; the window is
  clamped into the work area after a resize, and `DisplayArea.FindAll()` is available for the
  off-screen check. A *specific* monitor setting and live per-monitor DPI re-layout on hot-plug are
  not implemented — moving to another monitor and a restart both work, and the next drag saves it.

### Bug found while verifying milestone 3/4

`SpeedMeterPage` failed to load at runtime with
`XamlParseException: Failed to assign to property 'Microsoft.UI.Xaml.Controls.Primitives.RangeBase.Minimum'`
even though the same attributes work on other pages. The compiled XBF assignment was the cause;
setting `Minimum`/`Maximum`/`StepFrequency` on the three sliders and `Minimum`/`Maximum` on the
numbers box from code in the page constructor fixes it. Verified by instantiating the page at startup
with a temporary probe (removed again) — `OK` in `%LOCALAPPDATA%\AppLock\Logs\speedpage_probe.log`.
NOTE: adding new `Slider Minimum=...` attributes in XAML may hit the same problem.

## Speed Meter — Milestone 5 (CPU, RAM, disk, GPU, battery, rows, sparkline)

- `Services/SpeedMeter/SystemStatsProvider.cs`: CPU from `GetSystemTimes` deltas (kernel includes
  idle, so busy = kernel+user-idle), RAM from `GlobalMemoryStatusEx`, battery from
  `GetSystemPowerStatus` (hidden when Windows reports no battery). Disk and GPU use the WMI
  formatted performance classes (`Win32_PerfFormattedData_PerfDisk_PhysicalDisk`,
  `..._GPUPerformanceCounters_GPUEngine` filtered to the 3D/Compute engines) and are cached for
  three ticks because those queries are the expensive part. Each metric is only read while its
  toggle is on; GPU/disk simply stay hidden if the class is unavailable.
- Widget rows: the control builds one row per enabled metric in `RowOrder` (defaults CPU, RAM, Disk,
  GPU, Battery), formats RAM as percent or used/total, and paints CPU/RAM in a warning color at or
  above `WarnCpuPercent` / `WarnRamPercent`.
- Sparkline: `ShowSparkline` draws two polylines (download/upload) over the last 60 samples inside
  the widget, scaled to the current peak. Off by default.
- Page gained an *Extra stats* card: five metric toggles, mini-graph toggle, RAM format, both warning
  thresholds and a row-order list with up/down buttons. Row order is **reordered with buttons, not
  drag-and-drop** — the list is plain XAML rows so it needs no drag infrastructure and stays
  keyboard accessible.
- `StatsUpdated` is raised on the UI thread alongside `SampleUpdated`, so the page preview shows the
  same rows as the widget.
- Row-order values live in `SpeedMeterSettings.RowOrder`; anything enabled but missing from the list
  is appended, so new metrics never get lost.

## Speed Meter — Milestone 6 (connection alerts)

- `Services/SpeedMeter/ConnectivityMonitor.cs` polls Windows' own connectivity state
  (`NetworkInformation.GetInternetConnectionProfile().GetNetworkConnectivityLevel()`) once a second,
  so it makes **no network requests**. A change only counts after `DebounceSeconds` (default 3 s);
  `CheckRealInternet` requires `InternetAccess`, otherwise any link is enough. Outages are stored as
  `OutageRecord` (start, end, adapter/profile name), newest first, max 50.
- `ConnectionToastService` stacks up to three non-activating AppLock toasts
  (`Views/ConnectionToastWindow`), positions them at the configured 9-way anchor + margin with a
  slide/fade/none entrance, and closes them after the duration (timer pauses while hovered). With
  style *Windows notification* it posts through the existing `AppNotificationManager` instead. Sounds
  use `MessageBeep` (system default) — there is **no .wav picker** in this milestone.
- `SpeedMeterService` wires the monitor into the toast service, formats "Offline for 2 min 14 s" on
  restore and honours `MinOutageToAnnounceSeconds`. `ShowPreview(lost)` backs the two preview buttons.
- Page gained a *Connection alerts* card: lost/restored toggles, sound, full-screen quiet, real
  internet check, opt-in active probe, style/position/theme/animation combos, duration/margin/
  debounce/min-outage number boxes and both previews. All values save immediately.
- Not done in this milestone: the active-probe request itself (setting is stored only), toast size/
  opacity/content options, "Open details" button, and the queued summary after full-screen
  suppression (alerts are simply dropped while a full-screen app is in front).

## Speed Meter — Milestone 7 (details flyout, quality monitor, tray menu)

- `Views/NetworkFlyoutWindow`: borderless topmost card that opens beside the widget (flips to the
  other side / clamps when it would leave the monitor). It shows live ↓/↑, a graph of the last
  300 samples (5 minutes at the default interval, each series scaled to its own peak), adapter,
  Wi-Fi SSID + signal bars, link speed, local IP, gateway, DNS, ping/jitter/loss with a grade, the
  three most recent outages and the session totals. Actions: **Copy IP**, **Network settings**
  (`ms-settings:network-status`), **Pause tracking** (stops sampling and pings until resumed).
- `Services/SpeedMeter/NetworkDetailsProvider`: reads adapter/IP/Wi-Fi state from local APIs only
  (`NetworkInterface` + `NetworkInformation`), so opening the flyout sends no traffic.
- `Services/SpeedMeter/QualityMonitor`: pings the **default gateway** every 2 s on a background
  thread (never the internet), keeps the last 60 results and reports average latency, jitter and
  packet loss, graded Excellent/Good/Fair/Poor. It only runs while the flyout is open.
- Widget click opens/toggles the flyout; double-click follows `DoubleClickAction` (opens the flyout
  or toggles compact mode). A 250 ms delay keeps a double-click from also firing the single click.
- Tray menu gained *Network details*, *Speed Meter* (checkable), *Lock speed widget position*,
  *Speed widget click-through* and *Reset speed widget position*, all driving the shared service.
- Not done in this milestone: Top apps (needs the ETW/admin path, milestone 9), the public IP
  button, data-usage "today" totals (milestone 8), and adapter switching toasts.
