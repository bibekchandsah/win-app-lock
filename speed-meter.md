# Others → Speed Meter — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, shared `HotkeyService`, and Fluent look as the other **Others** pages.

## 1. Idea

A small, always-on-top, **rounded floating widget** that shows live **download and upload speed**. The user can drag it anywhere and fully customize its look (transparent / acrylic / solid, color, opacity, size). If the user enables them, it also shows **CPU and RAM usage**. When the internet **disconnects or comes back**, AppLock shows its own **custom toast** (themed, with outage duration). Extra features: connection-quality monitor, data-usage history and caps, top apps by network use, an expandable flyout with a live graph, smart auto-hide, and more (section 6).

Light / Dark / System theme for the settings page (theme resources only). The widget has its own appearance settings, independent of the app theme.

## 2. Navigation and placement

- New card in the **Others** card registry: **Speed Meter** (icon `` or a speedometer glyph), description "Live network speed, CPU and RAM in a floating widget", status text like "On · ↓ 2.4 MB/s ↑ 120 KB/s" (live, throttled to 1 Hz) or "Off". Registry order value **70** (Brightness 10, Eye Care 20, Volume Mixer 30, Clipboard 40, Ring Light 50, Notifications 60, **Speed Meter 70**).
- Clicking the card opens the **Speed Meter settings page** inside the Others frame with a `BreadcrumbBar` (Others › Speed Meter) and back button, using `SettingsCard` / `SettingsExpander` like the other pages.
- Works with the main window closed to the tray, and independently of the AppLock master protection toggle. Restores its state at startup when "Start with AppLock" is on.

## 3. Settings page layout

### 3.1 Master (SettingsCard)
- **Show speed meter** ToggleSwitch + **Toggle shortcut** keycaps with Edit (default **Ctrl+Alt+N**, via the shared hotkey capture dialog and validator).
- Live status: adapter in use, link speed, and current ↓/↑.
- Buttons: **Reset position**, **Reset all appearance**.

### 3.2 Live preview (SettingsCard)
A themed mock desktop strip showing the widget exactly as configured (it is the real widget control hosted in the page, fed with live data), so every appearance change is visible instantly even before the floating widget is enabled.

### 3.3 Appearance (SettingsExpander, expanded)
- **Theme preset** (SegmentedControl / chips): *Transparent* (text only, no background), *Glass (Acrylic)*, *Solid*, *Follow Windows theme (Light/Dark)*, *Custom*.
- **Background color** (ColorPicker + hex) and **Opacity** 0–100% (default 70%). With *Transparent* the background opacity is 0 but the text keeps its own opacity setting.
- **Text color**, **Download color** and **Upload color** (defaults: green-ish ↓, blue-ish ↑; accessible contrast warning if the contrast ratio against the background is below 3:1).
- **Size / scale** 50–250% (default 100%) and **Font** (Segoe UI Variable default; list of installed fonts), font weight, and **Show arrows/icons** (↓ ↑ glyphs or text "DL / UL").
- **Corner radius** 0–40 px (default 14), **Border** (none / thin, color), **Shadow** on/off.
- **Layout**: *Horizontal* (one row) or *Vertical* (stacked); **Compact mode** (smaller padding).
- **Units**: *Auto (KB/s, MB/s)*, *Bytes (B/s…)*, *Bits (Kbps, Mbps)*; decimals 0–2; fixed-width numbers so the widget doesn't jitter in size.
- **Update interval**: 0.5 s, 1 s (default), 2 s.
- **Mini sparkline graph** inside the widget (off by default): last 30–120 seconds of ↓/↑ as a tiny area chart.

### 3.4 Extra stats (SettingsExpander)
Each is an independent toggle (default off, except as noted), shown as additional rows/segments in the widget:
- **CPU usage** (%), optional per-metric color and **warning color above a threshold** (default 85%).
- **RAM usage** (% or used/total GB, selectable).
- **Disk activity** (% busy) — optional.
- **GPU usage** (%) — optional, when the performance counters are available.
- **Ping / latency** (ms) — optional, uses the connection-quality monitor (section 5.5).
- **Battery** (%) — optional, shown only on laptops.
- **Order** of the rows via drag-and-drop in a list, and per-row show/hide.
- **Total data used today** (↓+↑) — optional.

### 3.5 Behavior (SettingsExpander)
- **Always on top** (default on), **Show on**: primary monitor (default) / a specific monitor / follow the active monitor.
- **Draggable** (default on) and **Lock position** (disables dragging). **Snap to screen edges/corners** (default on, with margin 8 px, snap distance 16 px). **Click-through** (default off; when on, hold **Alt** to temporarily interact; also a hotkey toggle).
- **Hide when a full-screen app or game is running** (default on), **Hide when idle** (speed below a threshold for N seconds, default off), **Hide when disconnected** (default off).
- **Hide from screen sharing/recording** (default **on**; uses `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`).
- **Auto-fade when the mouse is over it** (default on, so it doesn't block content underneath), **Opacity on hover**.
- **Adapter selection**: *Automatic (the adapter with the default route)* (default), *All physical adapters (sum)*, or a specific adapter. **Include VPN/virtual adapters** (default off, avoids double-counting).
- **Start with AppLock** (default on if enabled).
- **Mini mode**: double-click the widget to switch between compact and expanded display (setting for what double-click does).

### 3.6 Connection alerts (SettingsExpander)
- **Notify when the connection is lost / restored** (default on once the meter is on; each independently selectable).
- **Style**: *AppLock toast* (default, custom themed window, see 5.4) or *Windows notification* (native toast).
- **Position** of the AppLock toast: 9 anchors (default top-center), margin, monitor; **Duration** 2–15 s (default 5 s); **Sound** (none / system default / choose .wav) per event, volume; **Do not show while a full-screen app is running** (default on; queue and show a short summary afterwards).
- **Appearance**: toast theme (Follow system / Dark / Light / Custom accent), size, opacity, animation (slide from top / fade / none; reduce-motion respected).
- **Content options**: show adapter name/SSID, show outage duration on restore, show current ping after restore.
- **Test buttons**: *Preview "Disconnected"* and *Preview "Restored"*.
- **Check for real internet access, not just a network link** (default on): see 5.3. **Active internet probe** (default **off**, opt-in): see 5.3.
- **Debounce** (seconds before declaring lost, default 3 s) to avoid flapping alerts; **Minimum outage to announce restore** (default 5 s).

### 3.7 Data usage (SettingsExpander)
- **Track data usage** (default on, local only): daily totals per adapter kept for 12 months.
- **Data cap warning**: daily / monthly limit (NumberBox, GB) with a threshold (80%, 100%) → custom toast; monthly reset day (1–28); **Count only this adapter** (e.g., mobile hotspot).
- **View history** button → dialog with a bar chart by day/week/month, totals, and **Export CSV**.
- **Reset statistics** (with confirmation).

### 3.8 Details flyout and tray
- Clicking the widget (or the tray menu item **Network details**) opens an **expanded flyout** attached to the widget: live ↓/↑ graph (last 5 min), adapter, SSID and signal strength (Wi-Fi), link speed, local IP, gateway, DNS, public IP (opt-in button, see section 6), ping/jitter/packet loss, session and today totals, **Top apps** list (section 5.6), and quick actions: *Copy IP*, *Run speed test* (opt-in), *Open Network settings*, *Pause tracking*.
- Tray menu: **Speed Meter** (checkable), **Lock position**, **Click-through**, **Reset position**, **Network details**.

## 4. Widget window

- Implement as a separate borderless window (WinUI 3 `Window` with `AppWindow` and `OverlappedPresenter` borderless, no title bar, no border, `IsAlwaysOnTop`, `IsResizable=false`, `IsMinimizable=false`) or a Win32 layered window hosting WinUI content. It must be: topmost (re-assert topmost periodically/on shell events), `WS_EX_TOOLWINDOW` (not in Alt+Tab or taskbar), `WS_EX_NOACTIVATE` (never steals focus), and optionally `WS_EX_TRANSPARENT` (click-through).
- **Backgrounds**: *Transparent* via a transparent backdrop (`TransparentTintBackdrop` on Windows App SDK 1.6+, or a layered window with per-pixel alpha); *Glass* via `DesktopAcrylicBackdrop` with tint/opacity; *Solid* via a filled rounded rectangle. If a backdrop isn't available on the Windows build, fall back to Solid with the same opacity and tell the user in an InfoBar.
- **Rounded corners**: use a rounded rectangle in XAML with transparent window background; also apply `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)` where it helps. No visible square corners or white flashes at any opacity.
- **Dragging**: press-and-drag anywhere on the widget (pointer capture) updates the window position with `SetWindowPos`; if click-through is on, dragging is only possible while Alt is held or Lock is off and click-through is temporarily bypassed by the hotkey. Show a subtle drag hint (cursor change, slight scale) and a thin snap guide when near an edge. Double-click action is configurable. Keyboard: when focused via the tray/hotkey "Move widget" mode, arrow keys nudge 1 px and Shift+arrows 10 px; Esc/Enter ends the mode.
- **Position storage**: store as the monitor id plus X/Y as percentages of the monitor work area (survives resolution/DPI changes); clamp into the visible area when monitors change; handle monitor hot-plug and DPI changes (per-monitor DPI v2) and re-create the window on `WM_DISPLAYCHANGE`, resume from sleep, and Explorer restart.
- **Size**: auto-sizes to content at the chosen scale; the content never reflows while numbers change (fixed-width/tabular numerals, reserve max digits).
- **Z-order with other AppLock overlays**: the widget stays below AppLock lock overlays and password dialogs. The Brightness page's blur overlay and the Ring Light overlay must not hide or blur the widget unexpectedly: the widget is excluded from capture by default, so the blur doesn't include it, and it stays above the blur overlay.
- **Full-screen detection**: use `SHQueryUserNotificationState` (hide for `QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE`) plus a foreground-window-covers-monitor check; re-show automatically afterwards.
- **Performance**: idle cost near zero; the widget updates only once per interval, the UI thread is never blocked by counters, and the app is paused while the screen is locked/off (`WTS` session events, `PowerSettingRegisterNotification`).

## 5. Engine

### 5.1 Network speed
- Source: IP Helper `GetIfTable2` (`MIB_IF_ROW2`: `InOctets`, `OutOctets`, `OperStatus`, `Type`, `InterfaceAndOperStatusFlags` for hardware/filter/virtual flags) or `NetworkInterface.GetAllNetworkInterfaces()` with `GetIPv4Statistics()` as a fallback. Use 64-bit counters.
- Speed per interval = (counter delta) / (elapsed time measured with `Stopwatch`, not the nominal interval). Handle counter reset/wrap and interface appearing/disappearing (treat as delta 0 for that tick). Smooth with a short moving average (1–3 samples; setting "Smoothing") so numbers don't flicker.
- **Adapter logic**: *Automatic* = the adapter holding the default route (lowest metric, `GetBestInterfaceEx` to a public IP address without sending traffic); re-evaluate every few seconds and on `NetworkChange.NetworkAddressChanged`. Exclude loopback, tunnel, Hyper-V/vEthernet, VMware, VirtualBox, WSL and Bluetooth PAN by default (detect via `IfType`, hardware flag and description patterns). VPN adapters are optional.
- Report: current ↓/↑, peak since start, average, session totals, today's totals.

### 5.2 CPU, RAM, and other stats
- **CPU**: `GetSystemTimes` deltas (cheap, no counters) for total %; optionally Performance Counters "Processor Information → % Processor Utility" for a Task-Manager-like value. **RAM**: `GlobalMemoryStatusEx` (used %, used/total). **Disk**: PDH "PhysicalDisk(_Total)\% Disk Time". **GPU**: PDH "GPU Engine(*)\Utilization Percentage" summed by engine type (3D/compute), hidden if unavailable. **Battery**: `SystemInformation.PowerStatus`/`GetSystemPowerStatus`. Create counters lazily only for enabled metrics and dispose them when disabled.
- Do not read CPU temperature or other WMI hardware sensors in the core feature (unreliable, needs drivers/admin). Mark as a possible phase-2 item.

### 5.3 Connection state detection (for lost/restored alerts)
- Primary: **`INetworkListManager`** (COM, `NLM_CONNECTIVITY` flags: `IPV4_INTERNET`, `IPV6_INTERNET`, `IPV4_LOCALNETWORK`…) with `INetworkListManagerEvents.ConnectivityChanged`. It distinguishes "connected to a router but **no internet**" from "no network at all", using Windows' own network connectivity status indicator, and needs no network calls from AppLock.
- Secondary: `NetworkChange.NetworkAvailabilityChanged` and adapter `OperStatus` changes.
- Optional, **opt-in** active probe (default **off**): every 10–15 s send one tiny request to a user-selectable endpoint (default: the gateway ping first, then an HTTPS HEAD to a public connectivity-check URL, with the user able to enter their own) to detect "link is up but nothing works" cases that NLM reports late. Show a clear note that this makes a small outbound request. No probe runs when the setting is off.
- **State machine**: `Online → Degraded/Checking → Offline → Online`. A change must persist for the debounce time (default 3 s) before alerting. Keep a log of outages (start, end, duration, adapter, reason if known), shown in the details flyout and exportable. If the adapter itself changes (Wi-Fi → Ethernet) without a gap, show an optional "Switched to <adapter>" toast instead of lost/restored.
- Suppress duplicate alerts after resume from sleep (give the network 5–10 s to reconnect before announcing a loss). Do not alert while the machine is locked if the user chose that option.

### 5.4 Custom toast (AppLock toast window)
- A themed, rounded, borderless, topmost, **non-activating** window (`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`), not part of Windows' notification system, so it works with Do-Not-Disturb off or on and is unaffected by the Notifications page position settings (it has its own anchor and margin settings).
- Content: **icon** (red "no connection" / green "connected" glyph), **title** ("Connection lost" / "Back online"), **subtitle** (adapter/SSID, and for restore "Offline for 2 min 14 s", optional ping), a **close (×)** button, and optionally **"Open details"**. Slide/fade animation (direction configurable; reduces to fade/none when Windows animation effects are off), auto-dismiss after the chosen duration, pause the timer while hovered, and a stack with a maximum of 3 simultaneous toasts.
- Sound: optional, per event; respects a "Do not disturb while full-screen" option and system volume.
- Accessibility: raise a UI Automation notification event (`RaiseNotificationEvent`) so screen readers announce the text; keyboard dismiss with Esc when focused via a hotkey.
- If the user chooses **Windows notification** style, post a native toast with `AppNotificationManager` with the same text.

### 5.5 Connection-quality monitor
- Ping the **default gateway** (and optionally one user-defined host) every 2 s with `Ping` (ICMP), only while the widget, flyout, or alerts need it (the latency row is on, the flyout is open, or the opt-in probe is enabled). Compute rolling latency, jitter, and packet loss over the last 60 samples and classify as *Excellent / Good / Fair / Poor* (thresholds configurable). Show in the flyout and as an optional widget row. Gateway pings stay on the local network; any internet host is opt-in.

### 5.6 Top apps by network usage (flyout)
- Per-process bytes need ETW or per-connection stats, which require **administrator rights**. Implement as an opt-in feature: use an ETW session for **Microsoft-Windows-Kernel-Network** (via `Microsoft.Diagnostics.Tracing.TraceEvent`) summing sent/received bytes by PID, mapping PID → process name/icon, shown as a top-5 list refreshed every 2 s while the flyout is open. When not elevated, show an InfoBar with **Restart as administrator** (same elevation helper as the BitLocker and Internet Access pages) and keep the rest of the feature working.
- Each row has **Block internet** which opens the Internet Access page's add-app flow (see `connection-firewall.md`) if that feature exists. Never end a process from here.

### 5.7 Data usage store
- Local JSON or SQLite (`%APPDATA%\AppLock\SpeedMeter\usage.db`) with daily rows `(date, adapterId, bytesDown, bytesUp)`; flush every 60 s and on exit; recover from crashes (atomic writes). 12-month retention; **no data leaves the PC**. Handle counters across reboots by tracking deltas, not absolute values.
- Cap alerts evaluate on each flush and fire once per threshold per period. Optionally flag the active connection as **metered** (`NetworkCostType` from `NetworkInformation.GetInternetConnectionProfile().GetConnectionCost()`) and show a small "metered" badge on the widget.

## 6. More features (included by design)

1. **Speed test** (opt-in, button in the flyout): measure download/upload using a user-selectable public speed-test endpoint (default a well-known CDN test file). Show a clear note that this uses data and makes network requests; progress ring; results history (last 20) with a chart; cancel button; nothing is sent besides the test traffic itself.
2. **Public IP / location lookup** (opt-in button, off by default): fetch once on click from a user-selectable endpoint; show and let the user copy; never automatic.
3. **Smart color thresholds**: optionally change the ↓/↑ color when speeds exceed N MB/s, and CPU/RAM color above thresholds.
4. **Wi-Fi details** (Windows.Devices.WiFi / WLAN API): SSID, signal %, band, link speed, with a "weak signal" warning toast option.
5. **VPN detection badge** (small lock glyph when a VPN adapter holds the default route).
6. **Idle auto-hide and "peek"**: when hidden by idle, moving the mouse into its former spot or using the hotkey reveals it.
7. **Per-game/app profiles (phase 2)**: hide or reposition the widget when specific apps are in the foreground.
8. **Taskbar-docked mode (phase 2, optional)**: a mode that docks the widget next to the system tray; only if it can be done reliably with documented APIs, otherwise skip it.
9. **Export/import settings** (JSON) and **Presets**: Minimal, Gamer (compact with ping), Streamer (hidden from capture, transparent), Monitor (CPU/RAM/Disk/GPU), and custom presets.
10. **Accessibility**: high-contrast mode uses system colors; the page and the flyout are fully keyboard- and screen-reader accessible; widget text is exposed to UI Automation (live region, throttled to once every 5 s to avoid noisy announcements).

## 7. Data model

```csharp
public class SpeedMeterSettings
{
    public bool Enabled { get; set; }
    public bool StartWithAppLock { get; set; } = true;
    public HotkeyBinding? ToggleHotkey { get; set; }        // default Ctrl+Alt+N

    // Appearance
    public string ThemePreset { get; set; } = "Glass";       // Transparent|Glass|Solid|FollowWindows|Custom
    public string BackgroundColor { get; set; } = "#202020";
    public int BackgroundOpacityPercent { get; set; } = 70;
    public string TextColor { get; set; } = "#FFFFFF";
    public string DownloadColor { get; set; } = "#4ADE80";
    public string UploadColor { get; set; } = "#60A5FA";
    public int ScalePercent { get; set; } = 100;
    public string FontFamily { get; set; } = "Segoe UI Variable";
    public int CornerRadiusPx { get; set; } = 14;
    public bool ShowBorder { get; set; }
    public bool ShowShadow { get; set; } = true;
    public string Layout { get; set; } = "Horizontal";       // Horizontal|Vertical
    public bool Compact { get; set; }
    public string Units { get; set; } = "Auto";              // Auto|Bytes|Bits
    public int Decimals { get; set; } = 1;
    public double IntervalSeconds { get; set; } = 1.0;
    public bool ShowSparkline { get; set; }
    public bool UseIcons { get; set; } = true;

    // Extra stats
    public bool ShowCpu { get; set; }
    public bool ShowRam { get; set; }
    public string RamFormat { get; set; } = "Percent";       // Percent|UsedTotal
    public bool ShowDisk { get; set; }
    public bool ShowGpu { get; set; }
    public bool ShowPing { get; set; }
    public bool ShowBattery { get; set; }
    public bool ShowTodayTotal { get; set; }
    public int WarnCpuPercent { get; set; } = 85;
    public int WarnRamPercent { get; set; } = 85;
    public List<string> RowOrder { get; set; } = new();

    // Behavior
    public bool AlwaysOnTop { get; set; } = true;
    public string MonitorMode { get; set; } = "Primary";     // Primary|Specific|ActiveMonitor
    public string? MonitorId { get; set; }
    public float PositionPercentX { get; set; } = 98;
    public float PositionPercentY { get; set; } = 90;
    public bool Draggable { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool SnapToEdges { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public double IdleThresholdKBps { get; set; } = 1;
    public int IdleSeconds { get; set; } = 30;
    public bool HideWhenDisconnected { get; set; }
    public bool HideFromCapture { get; set; } = true;
    public bool FadeOnHover { get; set; } = true;
    public string AdapterMode { get; set; } = "Auto";        // Auto|AllPhysical|Specific
    public string? AdapterId { get; set; }
    public bool IncludeVirtualAdapters { get; set; }
    public string DoubleClickAction { get; set; } = "Flyout"; // Flyout|Compact|None

    // Alerts
    public ConnectionAlertSettings Alerts { get; set; } = new();

    // Data usage
    public bool TrackUsage { get; set; } = true;
    public double? DailyCapGb { get; set; }
    public double? MonthlyCapGb { get; set; }
    public int MonthlyResetDay { get; set; } = 1;
    public string? CapAdapterId { get; set; }

    public List<SpeedMeterPreset> CustomPresets { get; set; } = new();
}

public class ConnectionAlertSettings
{
    public bool NotifyLost { get; set; } = true;
    public bool NotifyRestored { get; set; } = true;
    public string Style { get; set; } = "AppLockToast";      // AppLockToast|Windows
    public string Anchor { get; set; } = "TopCenter";
    public int MarginPx { get; set; } = 16;
    public int DurationSeconds { get; set; } = 5;
    public string? LostSoundPath { get; set; }
    public string? RestoredSoundPath { get; set; }
    public bool SoundEnabled { get; set; } = true;
    public int VolumePercent { get; set; } = 70;
    public bool SuppressInFullscreen { get; set; } = true;
    public string ToastTheme { get; set; } = "FollowSystem"; // FollowSystem|Dark|Light|Custom
    public int ToastOpacityPercent { get; set; } = 95;
    public string Animation { get; set; } = "SlideTop";      // SlideTop|Fade|None
    public bool ShowAdapterName { get; set; } = true;
    public bool ShowOutageDuration { get; set; } = true;
    public bool ShowPingOnRestore { get; set; }
    public int DebounceSeconds { get; set; } = 3;
    public int MinOutageToAnnounceSeconds { get; set; } = 5;
    public bool CheckRealInternet { get; set; } = true;
    public bool ActiveProbe { get; set; }                    // opt-in network request
    public string? ProbeUrl { get; set; }
}
```

Validate all ranges on load (opacity 0–100, scale 50–250, interval 0.5–2 s, duration 2–15 s) and migrate missing fields safely. Store in the AppLock settings JSON with the same integrity protection as other sections.

## 8. Project structure additions

```
src/AppLock/
  Views/SpeedMeterPage.xaml(.cs), Views/SpeedWidgetWindow.xaml(.cs), Views/Controls/SpeedWidget, SparklineControl
  Views/NetworkFlyout.xaml(.cs), Views/ConnectionToastWindow.xaml(.cs), Views/Dialogs/UsageHistoryDialog, OutageLogDialog, SpeedTestDialog
  ViewModels/SpeedMeterViewModel.cs, NetworkFlyoutViewModel.cs
  Services/SpeedMeter/
    SpeedMeterService.cs           // lifecycle, timers, settings sync, pausing on lock/sleep
    NetworkStatsProvider.cs        // GetIfTable2, adapter selection, deltas, smoothing
    SystemStatsProvider.cs         // CPU/RAM/Disk/GPU/Battery
    ConnectivityMonitor.cs         // INetworkListManager, state machine, outage log, probe
    ConnectionToastService.cs      // AppLock toast stack + native fallback
    QualityMonitor.cs              // ping/jitter/loss
    UsageStore.cs                  // daily totals, caps, CSV export
    ProcessNetworkMonitor.cs       // optional ETW top apps (admin)
    SpeedTestService.cs            // opt-in
    FullscreenDetector.cs
    WidgetPositioner.cs            // snapping, monitor percent, clamping
  Models/SpeedMeterSettings.cs, ConnectionAlertSettings.cs, NetSample.cs, OutageRecord.cs
tests/AppLock.Tests/SpeedMeter/   // delta/wrap math, unit formatting, adapter choice with fake interfaces, state machine/debounce, usage store, cap logic, position clamping, settings migration
```

Define `INetworkInterfaceSource`, `IConnectivitySource`, `IClock`, and `ISystemStatsSource` so logic is testable without real hardware or network.

## 9. Safety, privacy, quality

- **Privacy:** all statistics stay on the PC. The core feature makes **no network requests**. Only these opt-in, user-initiated features make any: active probe, speed test, public IP lookup. Each shows what it contacts. No telemetry.
- The widget and toasts must never steal focus, block input (except the widget's own area when not click-through), or cover AppLock lock overlays and password prompts.
- Always provide an escape: the hotkey, the tray menu, and "Reset position" work even when the widget is off-screen or hidden.
- Localize all strings; keyboard accessible; status never conveyed by color alone (arrows, text); high-contrast friendly.
- Log events (adapter chosen, state changes, errors) to `%APPDATA%\AppLock\logs`, without IP addresses or SSIDs unless the user enables detailed logs.
- UI thread stays free; counters, ping and ETW run on background threads. Performance target: ≤ 1% CPU with the widget at 1 s interval, near zero when hidden or paused.

## 10. Milestones (build, run, verify each before moving on)

1. **Speed Meter card** (order 70) in the Others registry, page shell with breadcrumb, master toggle, themed preview area; theme-correct (Light/Dark/System).
2. `NetworkStatsProvider` + `SpeedWidgetWindow`: borderless, rounded, topmost, shows live ↓/↑ with units and the Auto adapter logic.
3. Appearance: theme presets (Transparent/Glass/Solid/Follow), colors, opacity, size, radius, layout, font, decimals; live preview; persistence.
4. Dragging, snap to edges, lock position, click-through with Alt bypass, hotkey, multi-monitor/DPI/hot-plug handling, reset position, hide in full-screen, hide from capture.
5. CPU and RAM (then Disk/GPU/Battery), row order, warning colors, optional sparkline.
6. `ConnectivityMonitor` (NLM + debounce + outage log) and **custom toast** (lost/restored), sounds, position/animation/theme options, previews, native-toast fallback.
7. Details flyout: graph, adapter/Wi-Fi/IP info, quality monitor (ping/jitter/loss), tray menu items.
8. Data usage store, history dialog, CSV export, cap warnings, metered badge.
9. Presets, import/export, idle auto-hide, VPN badge, optional speed test and public IP (opt-in), optional top apps by process (ETW, admin).
10. Tests, accessibility and theme pass, performance check, manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 11. Manual test checklist

- [ ] The Speed Meter card appears in Others after Notifications; page opens; breadcrumb/back work; Light, Dark, System themes look right
- [ ] Widget appears with live ↓/↑; start a large download and an upload and confirm values are plausible against Task Manager; units switch between Auto/Bytes/Bits
- [ ] Transparent, Glass, Solid and Follow-Windows presets look right on light and dark wallpapers; opacity, size, color, radius and layout changes apply live
- [ ] Dragging works, snaps to edges/corners, position persists after restart, and survives a resolution/DPI change and a monitor unplug; Lock and click-through (Alt bypass) work; the widget never steals focus
- [ ] CPU and RAM rows appear when enabled and match Task Manager; warning colors trigger above thresholds
- [ ] Hides in a full-screen game/video and returns afterwards; hidden from screen sharing by default
- [ ] Turning Wi-Fi off shows "Connection lost" toast after the debounce; turning it on shows "Back online · Offline for X" toast; a router with no internet is reported as "No internet"; no duplicate toasts after sleep/resume
- [ ] Toast style, position, duration, sound, theme and animation options work; test buttons work; full-screen suppression works
- [ ] Adapter switching (Wi-Fi ↔ Ethernet) and VPN on/off don't produce wrong speeds or double counting
- [ ] Flyout shows graph, adapter/SSID/IP info, ping/jitter/loss; Top apps list works when elevated and shows the elevation prompt when not
- [ ] Data usage totals persist across reboots; cap warning fires once per threshold; CSV export works
- [ ] Speed test, public IP and active probe do nothing until the user enables or clicks them
- [ ] Disabling the feature removes the widget, timers, counters and hooks (check CPU in Task Manager); no leftover windows
- [ ] No network requests from the core feature (verify with a firewall log or Resource Monitor)
