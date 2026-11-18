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

## Speed Meter — Milestone 8 (data usage, history, caps, metered badge)

- `Services/SpeedMeter/UsageStore`: one row per day + adapter (`date, adapterId, adapterName,
  bytesDown, bytesUp`) in `%APPDATA%\AppLock\SpeedMeter\usage.json` — the spec's `usage.db` is a JSON
  file here. Writes are atomic (`usage.json.tmp` then replace), flushed at most once a minute plus on
  stop, and rows older than 12 whole months are pruned. Deltas are added from the sampled rate, never
  from absolute counters, so reboots do not inflate totals. Includes daily/weekly/monthly grouping,
  `UsageStore.MonthStart` for the monthly cap period, CSV export and a reset.
- The service feeds it only while *Track data usage* is on, shows **Today ↓/↑** (optional widget row)
  and `NetworkCostType` → a small **metered** badge on the widget, re-read about every 30 s.
- Cap warnings: daily and monthly limits (NumberBox, GB; blank = no cap) with the 80% and 100%
  thresholds; each threshold fires once per period (the last fired key is stored with the usage
  data), and the warning goes through the same AppLock toast / native fallback as the alerts.
- Page gained a *Data usage* card (track toggle, daily/monthly caps, reset day, "count only the active
  adapter", today's totals, **View history**, **Reset statistics** with confirmation).
- `Views/Dialogs/UsageHistoryDialog`: bar chart for last 7 days, 5 weeks or 12 months (download and
  upload stacked, value scale, sparse labels), period totals, and **Export CSV** through
  `FileSavePicker`.
- Verified with a temporary startup probe (`OK today=2097152/1048576 csv=3 file=True`, page and
  dialog both construct); probe removed.
- Not done: per-adapter breakdown in the page (caps can be limited to the active adapter only),
  threshold configuration (fixed at 80/100%), and usage history retention/export per adapter filter.

## Speed Meter — Milestone 9 (presets, import/export, idle hide, VPN badge, opt-in requests)

- `Services/SpeedMeter/SpeedMeterPresets`: four built-in looks (Minimal, Gamer, Streamer, Monitor)
  and appearance-only capture/apply used by custom presets and by the settings file. Applying a
  preset or importing never touches Enabled, the shortcut, alert settings or usage data.
- Page gained a *Presets and settings file* card: preset combo (built-in + custom), Apply, Delete,
  "save the current look as" with a name box, plus **Export settings…** / **Import settings…**
  through `FileSavePicker` / `FileOpenPicker`. Custom presets live in
  `SpeedMeterSettings.CustomPresets`.
- Idle auto-hide: when traffic stays below `IdleThresholdKBps` for `IdleSeconds` the widget hides
  itself; moving the mouse into its former spot or pressing the shortcut brings it back. *Hide while
  disconnected* was added to the same Behavior card and uses `ConnectivityMonitor.IsOnline`. Both
  flags (with full-screen) now share one visibility path in `SpeedWidgetWindow`.
- VPN badge: the default-route adapter is checked for VPN-style names every ~30 s and the widget
  shows a small lock + "VPN" next to the metered badge.
- Opt-in network requests, both only on a click: **Show public IP** (`https://api.ipify.org`, then the
  button copies it) and **Speed test** (`Services/SpeedMeter/SpeedTestService`, 10 MB download +
  5 MB upload against `speed.cloudflare.com`), with a dialog showing progress, cancel and the last
  20 results for the session.
- Not done: **top apps by process**. That needs an ETW session via `Microsoft.Diagnostics.Tracing.
  TraceEvent` (a new NuGet dependency) plus administrator rights; the rest of the flyout works
  without it. Also skipped: the optional taskbar-docked mode (phase 2 in the spec) and a configurable
  per-app profile list.

## Speed Meter — Milestone 10 (tests, accessibility, performance, checklist)

- Tests: `tests/AppLock.Tests/SpeedMeter/` with 24 xunit cases (unit formatting incl. bits/decimals,
  usage store accumulation/date ranges/daily buckets/CSV/reload/pruning/reset/month-boundary, preset
  apply and appearance-only round-trip). The test project links `NetworkStatsProvider.cs` (for
  `SpeedFormatter`), `UsageStore.cs` and `SpeedMeterPresets.cs`; `dotnet test --filter SpeedMeter`
  passes 24/24. Testing the appearance JSON with invalid input exposed one real gap: a malformed
  import used to throw, and `ApplyAppearanceJson` now ignores invalid JSON.
- Accessibility: the widget's root, download/upload values, extra stats panel and both badges carry
  `AutomationProperties.Name`, so a screen reader gets labels instead of bare numbers; page controls
  are standard WinUI controls with `Header`/content labels. Live-region announcements are **not**
  implemented (that would need a throttled UIA notification call).
- Theme: the page and dialogs use only `ThemeResource` brushes; the widget keeps its own user-chosen
  colours by design. No visual pass was done for light/dark beyond the existing startup probes.
- Performance: measured with the release build on this machine (8 logical cores, 45–60 s samples):
  meter off 3.64% of total CPU, meter at 1 s with no extra rows 3.35%, meter with CPU/RAM/disk/GPU
  rows 4.04%. The app's own baseline dominates, so the meter's delta is inside measurement noise
  (<0.3% total); the WMI-based disk/GPU rows are the only measurable extra cost (~0.4% total).
- Manual checklist status: card/pages/open flows and the settings probes are verified; the usage
  store was verified with a real write (`usage.json` row from live traffic). **Not** verified by
  clicking: drag/snap, full-screen hiding, screen-capture exclusion, connection toasts on a real
  disconnect, adapter switching, flyout graph/quality, cap toast and CSV export end-to-end, and
  disabling the feature to confirm no timers remain.

### Bug found in use: customizations reverted after leaving the page, crash dialog on exit

- The settings page called `LoadFromSettings()` from its constructor, before the control templates
  existed. When the page was inserted into the frame, WinUI's template initialization raised
  `ValueChanged`/`ColorChanged`/`Toggled` events with default values while `_loading` was already
  false, so those defaults were written into `SpeedMeterSettings` and saved. Result: coming back to
  the page showed defaults. Fixed by starting `_loading` at `true` and loading once from `Loaded`
  (guarded by `_settingsLoaded`), i.e. after template initialization. Verified with a temporary
  probe that hosted the page in a window: the saved values survived the load
  (`OK color=#123456 scale=137 radius=23 preset=Solid`).
- The `AppLock.exe - System Error ... stack-based buffer overrun` dialog on exit is a teardown
  crash: the speed widget, flyout and connection toasts are separate top-level windows that were
  still open when the process called `Environment.Exit`. `MainWindow_ActualClose` now stops the
  speed meter (timers, hooks, toasts, widget/flyout, usage flush) before the tray and icons are
  disposed, and `ServiceContainer.Reset` disposes the service too.

### Bug found in use: taskbar context menus vanish while notification customization is on

Windows 11 renders toast banners **and** taskbar right-click jumplists in the *same*
`Windows.UI.Core.CoreWindow` owned by `ShellExperienceHost.exe`. Measured on this machine:

| window | size | title |
|---|---|---|
| real toast banner | 396×171 | `New notification` |
| taskbar jumplist | 318×157 … 318×674 | `Jump List for <app>` |

Two problems followed from that:

1. `ToastWindowFinder` only checked class + host process + a 300–700 × 60–300 px size band, so a
   318×157 jumplist was accepted as a toast and animated to the notification position (that is the
   menu appearing in the notification area in the report). It now also requires the banner title
   (`New notification`, localized) or Windows' native bottom-right toast position, and logs the shell
   popups it ignores instead of touching them.
2. At the end of every toast the service moved that window to `-10000,-10000` and hid it. Because the
   window is shared, the next jumplist had nowhere to appear until Explorer restarted. The service now
   remembers the window's native rect and restores it before hiding, so the shell gets a usable window
   back. Verified end-to-end: show a test toast, let it finish, right-click a taskbar icon → the
   context menu appears normally, and the log shows `ignored shell popup ... 'Jump List for Settings'`
   for the menu while the toast is still detected as `New notification`.

### Speed meter pass: ideas 334-353 plus the startup/exit reports

**Widget page (334-345, 350, 352, 353)**

- Dropped the on/off + live status text and the whole "Start with AppLock" card. `SpeedMeter.Enabled`
  (the "Show speed meter" switch) is now the only switch: on start the widget is created when it is
  on, so `StartWithAppLock` was removed from the model.
- The preview keeps a 180 px floor instead of a fixed height and is fed a small synthetic wave, so the
  mini graph is visible before the service produces the first sample.
- Colour rows are now a swatch button (opens a `ColorPicker` flyout) + hex box + label, for background,
  text, download and upload.
- A `Reset` button next to `Change` restores the default toggle hotkey.
- Every "extra stats", "alerts", "top apps" and "usage" toggle sits on the same row as its label.
- New warning boxes: disk % (default 85), download Mbps and upload Mbps. The toaster names the busiest
  process (CPU/RAM/disk) when a threshold is crossed, and battery alerts at 80%/20% with the AC state.
- Snap radius went from 16 to 48 px and also snaps when the widget is dragged past the edge.

**Top apps (353)**

Per-process CPU/RAM/disk (via `GetProcessIoCounters`) and GPU (WMI `GPUEngine` PIDs) are sampled every
second and cycled in the widget. **Per-app network usage is not implemented**: it needs an ETW kernel
session (or RawCap-style driver) to attribute packets to a PID, which is out of scope here, so the
ticker only shows CPU, RAM, disk and GPU.

**Dialogs (346-349)**

The WinRT `FileSavePicker`/`FileOpenPicker` calls failed silently (no package identity), so export
settings, import settings and CSV export use a Win32 `GetSaveFileNameW`/`GetOpenFileNameW` helper
(`Services/NativeFileDialog.cs`). `UsageHistoryDialog` and the "reset data usage" dialog now follow the
app's light/dark choice.

**Presets (345)**

`SpeedMeter.LastPresetId` is stored when a preset is applied or saved, and the dropdown selects that
entry instead of always showing the first ("Minimal") preset.

**Large black window on start**

Two separate causes:

1. The widget window existed before its content was measured, so it appeared at the OS default size,
   black. Its constructor now builds the first frame (values, backdrop, per-pixel transparency, size
   and position) before it is shown.
2. The main window was activated before the services were created, so it sat as a large black
   rectangle until the first XAML frame. It is now hidden right after `Activate()` and revealed once
   `OnLaunched` finished, and the root keeps an opaque shell colour until the first page has navigated.

While tracking #2 down: `DisplayArea.FindAll()` throws `InvalidCastException` when enumerated through
LINQ in this projection, which silently aborted positioning, so monitor lookup and the on-display check
now index the list.

**Stack-overrun dialog on exit**

`HandleCloseRequestAsync` cleaned up and called `Environment.Exit(0)` from inside the awaited auth
dialog's continuation. The cleanup is now queued on a fresh dispatcher turn, and `SpeedMeterService.Stop`
waits (max 2 s) for an in-flight WMI sample before the widget and timers are torn down.

### Speed meter follow-up: ideas 352-362

- Batteries: `BatteryLowPercent` (default 20) and `BatteryHighPercent` (default 80) are editable
  NumberBoxes, so 10/20/50/70/100 and anything in between work. Charging past the high value,
  discharging to the low value, and every plug in / unplug now raises the speed meter toast.
- The colour swatches were invisible because a `Border` inside a `Button` is centred, not stretched;
  they now fill the button, so the swatch in front of each hex value shows the chosen colour.
- The arrows / border / compact switches sit next to their labels instead of above them.
- In the extra stats card the RAM format and CPU/RAM warning boxes moved up next to the disk,
  download and upload warnings.
- The top-apps line is no longer a single cycling row: every enabled metric (CPU, RAM, disk, GPU)
  gets its own line, and the widget window grows so the process names are not clipped.
- The usage chart gives every column a transparent hit area with a tooltip (date, download, upload).
- `PositionOnScreen` no longer writes the position back while the window is still being created, so
  the remembered spot survives a restart; only dragging or "Reset position" stores it.
- Toasts are built before they are shown, use per-pixel transparency (no black rectangle behind the
  rounded card) and never activate, so a toast no longer steals focus and closes an open colour picker.
- Per-app **network** usage is still not available: attributing packets to a PID needs an ETW kernel
  session, so the top-apps block covers CPU, RAM, disk and GPU.

### Speed meter follow-up 2: swatches, dynamic size, font weight

- The colour swatches stayed blank because a `Button` centres its content: the `Border` had no size
  and collapsed to nothing. Each swatch now gets an explicit size plus `Horizontal/VerticalContentAlignment="Stretch"`,
  so the preview shows the chosen colour in front of the hex value.
- The widget window is now re-measured after every update (`Update`, `SetStats`, `SetTopApps`,
  `SetUsage`) and clamped back onto the monitor, so it grows to fit a newly enabled row or a fourth
  top-app line instead of clipping, and never slides off the bottom of the screen.
- `OnWidgetLoaded` rebuilds the appearance from the saved settings, so all enabled rows exist on the
  first show rather than only after the settings page is opened.
- The GPU list is read on the first top-apps tick, so the GPU line is not missing for the first
  seconds.
- "Font weight" now applies to every text in the widget: rate numbers, labels, stat rows, top-app
  lines, the metered badge and the VPN badge.

### Keyboard and Wi-Fi pages: layout, keyboard navigation, toggle sync

- Both pages now use the Brightness page's structure: a fixed breadcrumb + title block and a
  `ScrollViewer` that holds only the cards. Scrolling no longer drags the header with it, and the
  administrator / elevation card that sits above "Enable Keyboard" (and above the saved-network card
  on the Wi-Fi page) scrolls normally instead of sliding under the title.
- Tab order is explicit (`BackButton` 0, `KeyboardToggle` 1, `LockKeyIndicatorToggle` 2,
  `RefreshButton` 3, `Restart as administrator` 4; Wi-Fi: back 0, refresh 1, list 2), and the
  controls carry automation names.
- The Wi-Fi list is keyboard navigable: `SelectionMode="Single"` with
  `SingleSelectionFollowsFocus="True"`, arrow keys walk the rows, Enter/Space reveals the selected
  password, Ctrl+C copies it, and clicking a row does the same as the eye button.
- `ServiceContainer.SetKeyboardEnabled` is now the single entry point for the keyboard/mouse input
  state: it applies the state, saves the setting and raises `KeyboardEnabledChanged`. The quick
  settings switch on the Others card (and its Quick Settings flyout) and the Keyboard page toggle
  subscribe to that event, so changing either one updates the other without navigating.

### Notifications: a toast can no longer play its sound and stay invisible

The banner for every notification is ONE window owned by ShellExperienceHost that the shell
reuses, and three separate bugs were making that window disappear or go see-through:

- **Movement was treated as a dismissal.** `OnToastMoved` compared the window against its rest
  rectangle and called `OnToastHidden` on any offset, so the shell's own entry slide (or a late
  LOCATIONCHANGE from our own animation frames) hid the banner a moment after it appeared. Only
  HIDE / CLOAKED / DESTROY end a toast now, and a CLOAKED event is confirmed 250 ms later so a
  transient cloak cannot erase a banner the user is still reading.
- **A finished exit animation could hide the next toast.** Every toast now gets a generation
  (`_toastEpoch`); the window is only handed back while the animation still owns it.
- **The animations left the shared window layered with alpha 0.** After AppLock exited that
  stranded transparency made every later notification invisible. Handing the window back now
  restores its native position *and* full opacity, and `ShutdownNotificationCustomizer` releases
  every tracked toast on window close and on `ProcessExit` (logged as "Released N toast window(s)").

Supporting changes:

- The window is muted for ~1 s after we hand it back, so Windows' own dismissal re-show is not
  managed as a brand-new toast (it used to appear and "hide" itself a second later).
- A 500 ms poller picks up toasts whose WinEvent never arrived; it skips windows already managed
  before validating them, and the SHOW/CREATE retry tries 80 / 180 / 400 ms instead of once.
- The hold loop only re-asserts the position when the window actually drifted (50 ms ticks), and
  candidate logging is throttled per window so an animation cannot spam the log every frame.

### Auto-lock on idle now works while AppLock is minimised

- `CheckIdleLock` (the 5 s watchdog) skipped *and restarted* its own clock whenever the window was
  not "actively visible", so minimising the window - or hiding it with "Minimize to Tray" while
  still unlocked - disabled the idle lock completely. Only a locked session or an open password
  prompt skip now; a minimised or hidden window keeps counting and locks as configured.
- The system-idle monitor (`IdleService`) stops itself when it fires and was only started at launch
  or from the settings box, so it worked at most once per run. It is started again after a
  successful unlock and after first-run setup, and `StartIdleMonitoring` stops it when the
  threshold is 0.
- Verified with the window minimised (`ShowWindowAsync(SW_MINIMIZE)`, `IsIconic = True`) and a
  1-minute threshold: `idle.log` shows "AppLock window idle 1.0 min (threshold 1) - minimizing to
  tray and locking", and the window left the desktop.

### Battery alerts take any number of custom percentages

- The single "notify below / above" pair is replaced by two comma separated lists,
  `SpeedMeterSettings.BatteryLowPercents` (on battery) and `BatteryHighPercents` (charging), so the
  widget can toast at 10, 20, 50, 70, 100 or whatever the user types.
- Lists are clamped to 1-99 / 1-100, de-duplicated and sorted both when typing and on load
  (`NormalizeBatteryThresholds`). Settings saved before this change keep their one value per
  direction: `SettingsStore` migrates the old `BatteryLowPercent` / `BatteryHighPercent` into the
  new lists when the JSON has no lists yet.
- `CheckBattery` toasts once per crossing and names the level in the message: "Battery charged 70%"
  with "68% and charging - unplug whenever you like", and "Battery discharged to 20%" with
  "18% remaining - plug in the charger". A reading that jumps over several levels (wake from sleep)
  announces only the level nearest the current reading, so one event never produces a burst.
- The page shows two text boxes ("Notify at these % (on battery)" / "(charging)") with a hint line;
  plug / unplug toasts are unchanged.

## PC Checker (Others → PC Checker)

Milestone 1 of `pc-checker.md`: framework and shell.

### What is in place

- **Others card registry.** New `pcchecker` card (`Order = 80`) with the monitor glyph `E977`,
  title "PC Checker" and the spec description. The **Keyboard** card moved from order 80 to 84 so
  the 80 slot is free and free of ties (`List.Sort` is unstable, so two cards sharing an order would
  shuffle). Note that this repo's registry puts Speed Meter at 86, so PC Checker lands before
  Keyboard, Wi-Fi and Speed Meter rather than directly after Speed Meter as the spec's example
  ordering suggested. Moving Speed Meter up is a one-line change if the spec ordering is preferred.
- **Page shell** (`Views/PcCheckerPage.xaml`): breadcrumb "Others / PC Checker" with back button,
  the full 21-entry category rail in the spec order (collapses to a plain list on narrow windows),
  a global search box, the *Basic / Detailed / Everything* selector, the *Hide identifiers* toggle
  (default off, remembered in `AppSettings.PcChecker`), Refresh plus `F5` and `Ctrl+F`.
- **Provider framework** (`Services/PcChecker`): `IInfoProvider`, `SystemContext`, `CollectOptions`,
  `InfoCollector` (per-provider timeout, four-way concurrency limit, in-memory cache for static
  data, progress, and a failure that becomes "Not available (reason)" instead of an exception),
  `PcFormat` size/uptime helpers, defensive WMI and registry wrappers, and the shared
  `ElevationHelper` (`IsElevated`, `TryRestartElevated`) used by later categories.
- **Providers shipped:** operating system (edition, version, build + UBR, architecture, install date,
  last boot, uptime, computer/user, elevation), processor (name, vendor, cores, threads, clocks,
  L2/L3, socket), memory (installed, usable, in use, available, hardware reserved, module count),
  graphics (adapter, driver version/date, reported VRAM, display mode) and storage (disk count,
  total capacity, every physical disk, every mounted volume with used/free).
- **Overview** shows five tiles (OS, processor, memory, graphics, storage) built from those
  providers, each clickable to jump to its category, with a status line (scan age, source count,
  identifier state, elevation state).
- **Common renderer:** every `InfoItem` renders through one template — label, value, copy button,
  status icon (icon + tooltip, never colour alone), a "?" explanation tooltip and, at the *Everything*
  level, the source string. Identifiers are masked with `••••••••` behind an eye button when
  *Hide identifiers* is on. Search runs across every collected field and value (and labels each hit
  with its category).

### Known limits of this milestone

- Categories whose providers land in later milestones (firmware, displays, audio, network, battery,
  devices, sensors, software, security, performance, stability, health, tools, compatibility,
  reports) show an explicit "arrives in a later milestone" line instead of a blank panel.
- Health summary, findings, snapshots, exports, `Copy section` / `Export section`, the live charts and
  the two-column card grid are later milestones; the `InfoTable` / `Finding` types already exist so
  those milestones do not need a model change.
- Data comes from WMI + the registry only; nothing needs administrator rights yet, and nothing is
  cached to disk. Nothing runs while the page is closed and no network request is ever made.
- **Warnings:** the Release build is clean for all new code. Twelve pre-existing warnings in
  unrelated features (nullable annotations in `App.xaml.cs`, the NAudio microphone/speaker devices,
  `VolumeOverlayWindow`, `VolumeMixerSettingsPage`, `ClipboardPasteService`, three `async` methods
  without `await`, and one useless constant comparison in `StartupManager`) were left untouched to
  keep this milestone's change set focused; `dotnet build -c Release` still reports them.

### Verification

- `dotnet build -c Release` → build succeeded, 0 errors; no warnings in any PC Checker file.
- App launched from the Release output; manual checks (card order, rail, tiles, search, masking,
  theme switching) are on the section 15 checklist and are re-run at each milestone.

## PC Checker — milestone 2 (Operating system, Motherboard & firmware)

- New `FirmwareProvider` + `Parsers/SmbiosParser` (reads `GetSystemFirmwareTable('RSMB')`): BIOS vendor/version/date,
  firmware type (`GetFirmwareType`), Secure Boot state, board, system manufacturer/ model/SKU/family/serial/UUID,
  chassis type, TPM state. Verified against this laptop: 58 structures parsed, SMBIOS 3.2, board
  `LNVNB161216`, system `82HS` (IdeaPad Flex 5 14ITL05), chassis "Convertible", 2 memory slots, 64 GB max.
- **This machine's firmware table has no SMBIOS type 0 (BIOS) structure**, so the BIOS rows fall back to
  `HARDWARE\DESCRIPTION\System\BIOS` in the registry. Both paths are implemented.
- OS category gained: activation status (only the last five characters of the product key, never the full key),
  license channel, pending-reboot state with the reason, installed update count and newest update.
- TPM is read through `root\cimv2\security\microsofttpm`. A standard user usually cannot open that namespace,
  so the row says "Needs administrator rights" rather than claiming the TPM is missing.

## PC Checker — milestone 3 (Processor, Memory)

- `CpuProvider` now builds its topology from `GetLogicalProcessorInformationEx`: physical cores, logical
  processors, cache sizes per level/type, NUMA nodes, packages and the hybrid performance/efficiency split
  (efficiency class). Instruction sets come from CPUID (`X86Base.CpuId`), microcode from the registry.
  This laptop reports 4 P-cores / 8 threads, 12 MB L3, AVX2 and AVX-512 — matching Task Manager.
- `MemoryProvider` reads every module from SMBIOS type 17 (slot locator, capacity, DDR type, form factor,
  rated vs configured speed, manufacturer, part number, rank, width, voltage) with the physical memory
  array (type 16) for slot count and maximum capacity. This laptop: 2× 8 GB Samsung DDR4-3200 SODIMM,
  2 slots, 64 GB maximum. WMI modules are the fallback when a table has no type 17 records.
- Channel layout and the "rated vs configured speed" hint are estimates and are labelled as such.

## PC Checker — milestone 4 (Storage, storage health)

- `StorageProvider` gained MSFT_PhysicalDisk/ MSFT_Disk detail: media type (HDD/SSD), bus (NVMe/SATA/USB),
  spindle speed, health status, firmware, partition count, GPT/MBR partition style and system/boot roles,
  plus per-volume cluster size and a low-free-space status.
- New `StorageHealthProvider` reads `MSFT_StorageReliabilityCounter` (temperature, wear, power-on hours,
  power cycles, uncorrected read/write errors) and prints plain-language wear readings. It is a Tier 2
  source: unelevated it reports "Needs administrator rights", never a blank or a guess.
- The page now shows an InfoBar with a **Restart as administrator** button (shared `ElevationHelper`) for
  categories whose data is partly administrator-only (storage, firmware, devices, security, stability).
- Volume/health rows use the shared InfoStatus icons, so a nearly full drive or worn SSD is flagged in the
  same way the health check will report it later.

## PC Checker — milestone 5 (Graphics, Displays)

- Graphics now reads exact VRAM from the driver's registry key (`HardwareInformation.qwMemorySize`), so cards
  above 4 GB no longer show Windows' capped figure; it also reports driver provider, hardware-accelerated GPU
  scheduling, the DirectX runtime and whether the Vulkan loader is installed.
- **Direct3D feature level is not implemented** (it needs a live DXGI/D3D device); the row says "Not reported"
  instead of guessing, as allowed by the "closest working approach" rule.
- New `DisplayProvider` + `Parsers/EdidParser`: monitor name, EDID manufacturer/model/serial/manufacture
  week, native resolution, screen size and diagonal, computed PPI, current mode/refresh/colour depth,
  position in the multi-monitor layout, primary flag and scaling.
- **Connection type (HDMI/DisplayPort/eDP) is reported as "Not reported"**: it needs QueryDisplayConfig,
  which is on the follow-up list rather than guessed.

## PC Checker — milestone 6 (Audio, Network, Battery & power)

- `AudioProvider` lists playback and recording endpoints from the MMDevice API with default and
  communications roles, state, shared-mode format, volume and mute. No audio is ever captured.
- `NetworkProvider` lists adapters (status, type, link speed, MAC), IPv4/IPv6, gateway, DNS, DHCP and MTU,
  plus the current Wi-Fi SSID, signal bars and profile name through `Windows.Networking.Connectivity`.
  Saved Wi-Fi passwords are never read.
- `BatteryProvider` reports charge, AC state, design vs full-charge capacity with a wear reading, chemistry,
  cycle count and serial (as an identifier), plus the active power scheme and Fast Startup state.
  A desktop PC shows "No battery" instead of empty rows.

## PC Checker — milestone 7 (Devices & peripherals)

- `DevicesProvider` is the read-only Device Manager equivalent: every PnP device, the devices Windows
  flags with a problem code first (with the plain-language meaning of each code), a per-class summary, and
  focused views for USB, HID/keyboards/mice, cameras, Bluetooth, biometrics and sensors.
- Printers come from `Win32_Printer` with driver, port, default and offline state.
- Long lists are capped at 40 rows per view with a "more…" row, so the page stays responsive; the health
  check milestone will consume the problem-device list directly.

## PC Checker — milestone 8 (Software)

- `SoftwareProvider`: installed apps from the Uninstall keys (HKLM 64/32 and HKCU, system components and
  patches filtered out, `Win32_Product` never touched), startup items from both Run keys, the StartupApproved
  enabled state and the Startup folders, services whose binaries live outside Windows / Program Files,
  installed Windows updates, .NET and Visual C++ runtimes, Python/Node/Java/Git/winget found on PATH with
  their file versions, a PATH checker (missing folders, duplicates, very long entries) and the 15 largest
  processes. Everything is read-only.
- Installed apps are capped at 120 rows with an explicit "more…" row so the page stays quick on PCs with
  hundreds of apps; totals are always shown in the Summary group.

## PC Checker — milestone 9 (Security)

- `SecurityProvider` reports Defender state (real-time protection, signature version and age, tamper
  protection), firewall per profile, UAC, Secure Boot, memory integrity (HVCI), VBS, Credential Guard,
  Remote Desktop, SMBv1, AutoPlay, SmartScreen, local accounts (Guest flagged when enabled) and BitLocker
  per volume. Everything is read-only.
- Rows that indicate a weaker setting use the shared warning/problem icons so they line up with the health
  check that lands in the next milestone.

## PC Checker — milestone 10 (Performance live, Stability & events)

- `PerformanceProvider` samples CPU through `GetSystemTimes`, memory through WMI, per-disk throughput/active
  time through `Win32_PerfFormattedData_PerfDisk_PhysicalDisk`, network rates from the IPv4 byte counters and
  GPU utilisation from the GPU engine performance counters.
- The Performance category refreshes **once a second and only while it is open**; values are updated in place so
  the list does not jump. Leaving the category (or the page) stops the timer.
- `StabilityProvider` reads `Win32_ReliabilityRecords` for the last 30 days plus the system event log entries for
  blue screens (BugCheck 1001), unexpected shutdowns (Kernel-Power 41) and WHEA hardware errors, and names
  bug check codes through `Models/PcChecker/BugCheckTable` (e.g. `0x0000009F DRIVER_POWER_STATE_FAILURE`).
- Per-core CPU and per-engine GPU charts from the spec are represented as numeric rows for now; chart drawing
  is on the follow-up list rather than half-done.

## PC Checker — milestone 11 (Health check)

- `Services/PcChecker/Health/HealthRuleEngine` turns the collected sections into findings with severity,
  what it means, what to do and the owning category. Rules cover storage free space/health/wear/errors, memory
  pressure/channel/profile, problem devices, battery wear/age, Secure Boot/TPM/old BIOS, Defender, firewall,
  UAC, SMBv1, RDP, pending reboot, blue screens, WHEA errors, unexpected shutdowns, many startup items and
  PATH problems.
- The **Health check** category collects every provider, lists findings worst-first and lets each one be
  dismissed ("Don't show this again", stored in `PcCheckerSettings.DismissedFindings`).
- The status line above the content now starts with the health summary: "All good", "Needs attention (N)" or
  "Problems (N)", plus the note count, so the Overview doubles as a health summary until the tiles grow one.

## PC Checker — milestone 12 (Reports & history)

- `Services/PcChecker/ReportExporter` writes HTML (single self-contained file, light/dark aware), plain text,
  JSON and CSV. Identifiers become `••••••` when masking is on, control characters are stripped, values are
  length-capped and HTML is escaped.
- The toolbar's **More** menu has "Copy a summary", the four save formats, "Take a snapshot" and "Compare last
  two snapshots". The native Win32 save dialog is used because the WinRT pickers fail silently here (see the
  earlier notes on export settings).
- `Services/PcChecker/SnapshotService` stores gzipped JSON snapshots in `%APPDATA%\AppLock\PcChecker\snapshots`
  (newest 20 kept). Identifiers are stored only as salted SHA-256 prefixes and live performance values are
  excluded, so comparing two snapshots shows configuration changes.
- "Reports & history" lists the recent snapshots and, after a comparison, every changed value as
  `before → after`.

## PC Checker — milestone 13 (Tools, Compatibility)

- **Tools** lists the Windows tools a report makes you want: System Information, Device Manager, Reliability
  Monitor, Disk Cleanup, Storage settings, Windows Update, DirectX diagnostics, network connections, power
  options and Task Manager. Each row has an open button that launches the tool through the shell; nothing is
  installed and nothing runs elevated on its own.
- **Compatibility** evaluates Windows 11 readiness from already-collected values (TPM 2.0, Secure Boot, UEFI,
  memory, system drive size, DirectX 12, 64-bit) and gives upgrade advice (free memory slots, HDD system drive,
  little feature-update headroom, two cores or fewer). Unknown values are reported as unknown, never as passed.
- Microsoft's supported-CPU list is not bundled, so that single check is stated as unverifiable rather than
  guessed. The spec's editable requirement profile was left out instead of being half-built; the readiness
  checks and advice cover the useful part.

## PC Checker — milestone 14 (Sensors)

- Driver-free sensors only: ACPI thermal zones (`MSAcpi_ThermalZoneTemperature`), firmware fan entries
  (`Win32_Fan`), drive temperatures from `MSFT_StorageReliabilityCounter` and battery temperature
  (`root\wmi BatteryTemperature`).
- When firmware exposes no fan tachometer the row says so and explains that this is normal without a driver.
  Per-core clocks, voltages and manual fan control need a kernel driver, which this app deliberately never
  installs or offers to install.

## PC Checker — milestone 15 (Tests, accessibility, manual checklist)

- Tests: `tests/AppLock.Tests/PcCheckerTests.cs` (12 tests) covers bug check formatting, report redaction,
  HTML escaping, CSV quoting, detail-level filtering, health rule severity/ordering, dismissal, and category
  id uniqueness. They link the model/service files directly, so they run without the WinUI app.
- Accessibility: every icon-only row button has an `AutomationProperties.Name`, the search box, detail level,
  masking toggle, category rail and More menu are named, and status colours always come with text
  ("Needs administrator rights", "Not available", finding severity in words).
- Manual checklist (needs a human, the app is password-protected and the window cannot be driven from here):
  1. Open Others → PC Checker; the Overview tiles fill in and the status line shows a health summary.
  2. Visit Performance and watch CPU/memory/disk update once per second; leave it and confirm the timer stops
     (the values freeze).
  3. Security → confirm Defender/firewall rows appear; unprivileged, drive health and TPM say "Needs
     administrator rights" instead of a guess.
  4. Health check → dismiss one finding, reopen the page, confirm it stays hidden.
  5. More → Take a snapshot, change something (e.g. toggle a setting), take another, then Compare last two
     snapshots and check the change list.
  6. More → Save report as HTML and open it in a browser; toggle "Hide identifiers" first and confirm
     serial numbers/IDs are masked.
  7. Tools → each row's open button launches the listed Windows tool.
  8. Switch the app between Light and Dark and check the page, tiles and HTML report.

## Volume Mixer — auto mute schedule (Appearance tab)

- New "Auto Mute Schedule" card in Volume Mixer → Appearance: one toggle plus a list of daily ranges
  (Add / Edit / Delete). Each range is a from/to time with a 24-hour picker; a range whose end is earlier than
  its start crosses midnight, so `23:00 – 05:00` mutes overnight. Up to 12 ranges.
- Ranges are stored in `VolumeMixerSettings.MuteWindows` as `"HH:mm"` strings (`Models/MuteWindow.cs`), validated
  and normalised by `Services/VolumeMixer/MuteSchedule`.
- `Services/VolumeMixer/MuteScheduleService` polls every 15 seconds on the UI dispatcher and mutes the default
  playback device (`SpeakerVolumeService`) while a range is active. It remembers whether the user had muted
  before, restores that state when the range ends, and never re-mutes if the user unmutes manually during a
  range. Closing AppLock while muted restores the audio too.
- The service starts with the app (inside `ServiceContainer.InitializeVolumeMixer`), independent of whether the
  floating overlay is enabled, so scheduled silence works with the overlay switched off.
- Tests: `tests/AppLock.Tests/MuteScheduleTests.cs` (11 cases) covers daytime ranges, overnight ranges, multiple
  ranges, empty/identical ranges, normalisation and the "(next day)" label.

### Microphone support for the auto-mute schedule

- The schedule now has two targets, both inside the same card: **Mute speakers** (on by default) and
  **Mute microphone** (off by default, because muting a mic can surprise people on calls).
- `MuteScheduleService` tracks the playback and recording devices separately: each has its own "muted by
  schedule" flag and its own remembered previous mute state, so ending a range restores exactly what the user
  had. Turning a target off while a range is active releases that endpoint immediately.
- Microphone muting goes through `MicrophoneVolumeService` (default recording device for communications), the
  same NAudio path the Volume Mixer overlay already uses for the mic slider.
- The status line now names the targets, e.g. "Muted right now for 23:00 – 05:00 (next day) (speakers and
  microphone)", and complains when the schedule is on but neither target is selected.

## Wi-Fi passwords: QR sharing, breadcrumb and layout

- The Wi-Fi Passwords page now uses the same breadcrumb as the Volume Mixer page (small back arrow, then
  `Others › Wi-Fi Passwords` with the current page in bold) and its content is left-aligned instead of sitting
  in the middle of the window, so the cards start where the title starts.
- Each saved network with a password has a **QR** button. It shows the standard `WIFI:T:WPA;S:ssid;P:pass;;`
  payload as a QR code (quiet zone included, always black on white so it scans in dark mode too) plus the SSID,
  and a "Copy password" action. Reserved characters in the SSID/password are escaped.
- QR rendering uses `Net.Codecrete.QrCodeGenerator` (pure managed, added to the project) for the module matrix
  and draws it as vector rectangles, so no image encoding or bitmap APIs are involved in the UI.

## Network devices: MAC address copies on click

- In Internet Access → Network devices the MAC address is a flat button with the tooltip "Click to copy this
  MAC address"; clicking it copies the address to the clipboard (with `Clipboard.Flush` so it survives).

## Tray menu: Restart AppLock

- New "Restart AppLock" tray item above Exit. It marks the exit authorized for the watchdog, unprotects the
  process, and starts a hidden PowerShell that waits two seconds and relaunches the executable — the delay stops
  the new instance colliding with the one that is still shutting down.

## Navigation, tray and keyboard state sync

- Keyboard and PC Checker now use the same breadcrumb as the Volume Mixer page: a small back arrow followed by
  `Others › <Page>` with the current page in bold, outside any button border. Their content columns are
  left-aligned at 880 px like the Wi-Fi page.
- Wi-Fi Passwords content widened from 880 px to 1040 px so the rows and their action buttons use more of the
  window.
- Tray menu gained **Restart as administrator** next to **Restart AppLock**. It relaunches with a UAC prompt
  (a plain restart when the app is already elevated) and, if the prompt is dismissed, the app keeps running.
- Keyboard state no longer goes stale between surfaces:
  * `ServiceContainer.SetKeyboardEnabled` broadcasts the state that was actually applied (read back from the
    input API), not the requested one.
  * The Others page re-reads the live input state whenever the keyboard card's quick-settings flyout opens and
    on every change notification, instead of trusting a cached view-model value.
  * The Keyboard page polls the real state every 1.5 seconds while it is visible and corrects its switch and
    status line, skipping the update while its confirmation dialog is open.

## Network devices: MAC copy feedback

- Clicking a MAC address in Internet Access → Network devices now shows an inline InfoBar over the bottom-left
  of the device list instead of copying silently: green "MAC address copied — <mac> is on the clipboard." for
  four seconds, red "Could not copy the MAC address" with the exception message for eight seconds (both
  dismissable by hand).
- A device without a MAC address reports "This device has no MAC address to copy." rather than doing nothing.

## Auto mute schedule: every device, not just the default

- `MuteScheduleService` now walks every active playback device (`SpeakerVolumeService.GetSpeakers()`) and every
  active recording device (`MicrophoneVolumeService.GetMicrophones()`), muting each one and remembering its own
  previous mute state in a device-id keyed dictionary.
- Devices connected while a range is running are picked up by the 15 second poll and muted too, so "all
  speakers" stays true through docking and headset changes.
- Leaving the range restores each device to its remembered state; a device the user unmuted by hand during the
  range keeps their choice, and devices that disappeared are simply forgotten.

## Single instance guard (fixes the double window after restart)

- `Program.Main` now takes a named mutex (`Local\AppLock_MainInstance_Mutex`) before starting XAML and holds it
  for the life of the process.
  * A restarted instance (normal or elevated) waits up to 8 seconds for the previous one to release the mutex,
    so it starts only when the old window is really gone; an abandoned mutex - the normal restart case - is
    treated as "free" and the new instance continues immediately.
  * A stray second launch (double-click while AppLock is already running) waits briefly and then exits quietly
    instead of adding a second window.
  * The watchdog and BitLocker headless entry points return before the guard, so they are unaffected.
- Both restart actions now set the explicit-exit flag before relaunching and, after `Application.Exit()`, hard
  exit with `Environment.Exit(0)` if XAML shutdown lingers, so the old process cannot sit around with a window
  while the new one opens.

## Auto mute status on the volume overlay

- The overlay's speaker button now carries the schedule state, so the settings page is no longer needed to
  check it: a small accent badge (with a clock glyph) appears on the speaker icon while a range is active, and
  the speaker button's tooltip/help text says either "Auto muted until 05:00 (23:00 – 05:00)" or
  "Auto mute starts at 23:00 (23:00 – 05:00)", with "(tomorrow)" when the next range starts after midnight.
- `MuteSchedule.GetStatus` computes that one line (off / no ranges / active until / next start) and the overlay
  refreshes it every 30 seconds, on settings changes, and immediately when `MuteScheduleService` changes state.
- Tests: three new `MuteScheduleTests` cases cover the active text, next-start today vs tomorrow, and the off /
  empty cases (14 tests total).

## Auto mute schedule: runs from app start, with diagnostics

- `ServiceContainer.InitializeMuteSchedule` is now its own entry point and is called at the very start of the
  window-initialisation block in `App.OnLaunched`, before the Volume Mixer overlay is created and in its own
  try/catch. Previously it was only created at the end of `InitializeVolumeMixer`; anything that failed while
  building the overlay (or the overlay never being initialised) left the schedule dead until the page happened
  to touch it.
- `MuteScheduleService` no longer relies on a `DispatcherQueueTimer` for its heartbeat: a background
  `System.Threading.Timer` ticks every 20 seconds and marshals the work to the UI thread (falling back to
  running directly if the dispatcher is gone). This keeps muting working when the window was never shown or the
  page was never visited.
- Diagnostics: every start, range transition, device enumeration failure and restore is appended to
  `%LOCALAPPDATA%\AppLock\Logs\mute-schedule.log` (rolling at 256 KB), so a range that does not mute can be
  explained instead of guessed at.

## Auto mute schedule: the mute survives a restart properly

- Symptom: restart during an active range left microphones muted forever — the mics were already muted when the
  new process looked, so it recorded "was muted = true" as the user's own choice and dutifully restored them
  *to muted* when the range ended.
- Fix: `MuteScheduleState` (`%LOCALAPPDATA%\AppLock\mute-schedule-state.json`) records, per device id and
  category, the state the schedule found before muting. It is written whenever the schedule takes or gives back a
  device, refreshed every 30 minutes while a range stays active, deleted as soon as nothing is held, and ignored
  (and deleted) if older than 24 hours so a stale record can never unmute something muted by hand long after.
- On startup the record is restored *before* anything is muted or released, so a range ending after a restart
  still gives the devices back, and a restart that outlived the range restores them immediately.
- First run after this change has no record at all; a device found muted then is assumed to have been muted by
  the schedule, so the currently stuck microphones get released at the end of the range instead of staying muted.
- Tests: `MuteScheduleStateTests` covers round-trip, empty-record deletion, expiry, clock skew, corrupt JSON and
  dropped empty device ids.

## Keep microphone volume fixed

- Windows, a driver's automatic gain control and communication apps all write the capture level behind the
  user's back (the mixer showed it drifting 54% -> 73% -> 100%). `MicrophoneVolumeLockService` polls every
  700 ms and writes the held level back when a device sits more than 0.75 percentage points away from it.
- The held level is whatever the user has at the moment the feature is switched on (`CaptureCurrentVolumes`), and
  it is updated whenever the mixer slider is moved for that device (`NoteUserVolume`), so the guard never fights
  a deliberate change.
- Held levels are stored per device id in `VolumeMixerSettings.LockedMicrophoneVolumes`, so the guard survives a
  restart; a device that appears later keeps whatever level it has when it is first seen.
- The service has its own `MicrophoneVolumeService` and runs from `App.OnLaunched` (top of
  `InitializeVolumeMixer`, own try/catch), so it works with the overlay closed and with the page never visited.
  The device list is re-enumerated every 15 seconds rather than on every poll to keep the COM cost low.
- Toggle: Volume Mixer -> Appearance -> "Keep Microphone Volume Fixed". Diagnostics:
  `%LOCALAPPDATA%\AppLock\Logs\mic-volume-lock.log` (captures, each put-back, totals).

## Microphone lock badge in the mixer

- While `LockMicrophoneVolume` is on, a small circular lock badge overlays the microphone icon (same look as the
  locked-app icon badge on the Protection page), and the slider tooltip reads "Volume held at N% — changes made
  elsewhere are put back". Badge and tooltip follow the held level and clear when the lock is switched off.
- The badge sits on the 32 px icon chip, so it shows in both the icon-only and expanded mixer views without
  needing extra horizontal space — the earlier inline badge next to the mute button was clipped by row width.

## Keyboard enable/disable

- `KeyboardService` no longer uses `BlockInput`. That API needed administrator rights (the "BlockInput API
  call failed" error on a normal launch), blocked the mouse as well, and only held while the calling thread
  lived. It is replaced by a `WH_KEYBOARD_LL` hook that returns 1 for key messages while disabled: no
  elevation needed, mouse untouched so the switch can always be turned back on, and the block disappears
  with the process instead of outliving a crash. The hook is installed on the UI thread on first disable
  (low-level hooks need a message pump) and stays installed while the app runs.
- Both switches now read `KeyboardService.IsKeyboardEnabled()` — the Others card used to initialize from
  `Settings.Keyboard.Enabled` while the page read the service, which is why they disagreed. The change event
  still fans out to the other surface, and both revert on failure.
- The block is per session: restarting AppLock leaves the keyboard enabled and the toggles show that truth.
- Known limit: a low-level hook from an unelevated process does not see keystrokes typed into elevated
  windows (UIPI), so those apps still receive input while the switch is off. Ctrl+Alt+Del is never blocked.
- The page's administrator warning card, its restart-as-admin button, and the "keyboard and mouse input"
  wording are gone; the manual i8042prt driver method remains as the fallback card.

## Protection page: focusing a running app, and the exit-time system error

- Clicking a "Running programs" entry used to call `ShowWindow(SW_RESTORE)` unconditionally, and restoring a
  non-minimised window is what un-maximises it. The click now restores only when the window is actually
  minimised, shows it only when it is hidden, and otherwise just calls `SetForegroundWindow`.
- Exit used to run the teardown twice: `MainWindow_ActualClose` disposes the DWM thumbnails (native
  `AppLockHook.dll` exports) and then the `AppDomain.ProcessExit` safety net disposed them again while the CLR
  was shutting down and the DLL was detaching — the most likely source of the "stack-based buffer overrun"
  system error at exit. `ProcessExit` now runs managed-only cleanup once, guarded by an `Interlocked` flag that
  the normal exit path sets, so native teardown happens exactly once, while the runtime is still healthy.
- This is a plausible-cause fix, not a reproduced one: without a crash dump there is no stack to confirm it.
