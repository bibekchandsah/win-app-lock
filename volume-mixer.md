# Volume Mixer: Implementation Spec (WinUI 3)

A floating volume overlay with a per-application **Volume Mixer**. The user hovers a small speaker icon, adjusts the master volume with a slider, and opens a mixer panel to raise, lower or mute the volume of each app (WhatsApp, Chrome, Spotify, etc.) separately.

Stack: C# / .NET 8 / WinUI 3 (Windows App SDK). Audio via Windows Core Audio (WASAPI), using the **NAudio** package (`NAudio.CoreAudioApi`) as the wrapper.
Theme: **Light / Dark / System** must work in both the settings UI and the floating overlay.

The reference screenshots show the behavior and layout to reproduce. Use native WinUI 3 / Fluent controls and theme resources; do not copy the screenshot colors or hard-code them.

Placement in the app: a **Volume Mixer** card in the **Others** page (enable toggle + "Settings" button that opens the settings dialog/page with the three tabs below). If AppLock already has a different place for add-ons, follow that.

---

## 1. Features

1. **Floating overlay icon**: a small speaker icon pinned to a screen position. It is dim when idle and expands on hover.
2. **Master volume slider** (expanded state): speaker icon (click = mute/unmute master), slider 0 to 100, percentage text, and a small round button that opens the mixer.
3. **Volume Mixer panel**: a list of apps that currently own an audio session. Each row has app icon, app name (tooltip), slider 0 to 100, percentage text, and a per-app mute button.
4. **Settings** with 3 tabs: Appearance, Behavior, Blacklist (section 3).
5. **Theme support**: Light, Dark, System. Changing the app theme updates the overlay live.
6. **Blacklist**: process names that must never appear in the mixer.
7. **Drag mode**: lets the user drag the icon to any position (saved).
8. **Start with Windows** option.

---

## 2. Overlay behavior (state machine)

States: `Idle` -> `Expanded` -> `MixerOpen`.

| State | Look | Trigger in | Trigger out |
|---|---|---|---|
| Idle | Speaker icon only inside a rounded chip, opacity = Idle Opacity | App start; or hide timers expire | Pointer enters the icon |
| Expanded | Chip grows to icon + slider (width = Slider Width) + `%` + mixer button; opacity = Hover Opacity | Pointer stays over the icon for **Hover Delay** ms | Pointer leaves for longer than **Mixer Hide Delay** ms -> Idle |
| MixerOpen | Panel opens under the bar (or above it for `bottom-*` positions) with the app rows | Click the round mixer button (toggle) | Click the button again, or pointer leaves bar and panel for **Mixer Hide Delay** ms |

Rules:
- Animate width/opacity/panel with **Animation Speed** ms (use Composition animations or `Storyboard` with easing; no jank on the UI thread).
- The pointer being over the bar **or** the mixer panel counts as "inside"; leaving both starts the hide timer. Re-entering cancels it.
- While the user is dragging any slider, never collapse.
- Mouse wheel on the icon/bar changes master volume by 2 percent per notch (Shift = 1 percent).
- Click on the speaker icon toggles master mute; the icon changes by level: muted, low, medium, high.
- The overlay window must **not take focus** from the app the user is working in.

### Overlay window requirements
- Borderless, transparent, topmost `Window`; hidden from taskbar and Alt+Tab (`AppWindow.IsShownInSwitchers = false`, extended style `WS_EX_TOOLWINDOW`).
- `WS_EX_NOACTIVATE` so clicks do not steal focus.
- Background: rounded card with `DesktopAcrylicBackdrop` or a theme brush; border 1 px using `CardStrokeColorDefaultBrush`.
- Size the window to its content (collapsed chip size when Idle) so it does not block clicks on other windows. Resize the window as it expands (`AppWindow.MoveAndResize`), and keep the anchor edge fixed (e.g. `top-center` stays centered, `bottom-right` stays right-aligned).
- Position uses the work area of the primary monitor (`DisplayArea.Primary.WorkArea`), 12 px margin. Re-position on display change/DPI change.
- Support per-monitor DPI.

---

## 3. Settings

Use a `TabView` (tabs not closable, no add button) or `SelectorBar` + content `Frame`. All values apply live (no Save button) and persist.

### Tab 1: Appearance
| Setting | Control | Default | Range |
|---|---|---|---|
| Icon Position | `ComboBox` | `top-center` | `top-left, top-right, top-center, bottom-left, bottom-right, bottom-center` |
| Icon Size | `NumberBox` (suffix px) | 32 | 16 to 64 |
| Idle Opacity | `Slider` + % label | 50 | 10 to 100 |
| Hover Opacity | `Slider` + % label | 100 | 10 to 100 |
| Slider Width | `NumberBox` (suffix px) | 200 | 100 to 400 |
| Theme | `RadioButtons`: Light / Dark / System | System | |

### Tab 2: Behavior
| Setting | Control | Default | Range |
|---|---|---|---|
| Hover Delay | `NumberBox` (ms) | 500 | 0 to 3000 |
| Mixer Hide Delay | `NumberBox` (ms) | 1000 | 200 to 5000 |
| Animation Speed | `NumberBox` (ms) | 250 | 0 to 1000 (0 = no animation) |
| Polling Interval | `NumberBox` (ms) | 150 | 50 to 1000 |
| Enable Drag Mode | `CheckBox` | off | |
| Show only active audio apps | `CheckBox` | off | |
| Start with Windows | `CheckBox` | off | |

### Tab 3: Blacklist
- Label: "Blocked applications (process names):"
- `ListView` of blocked process names (single selection), an input `TextBox` with placeholder `e.g. chrome.exe`, an **Add** button (accent) and a **Remove** button (removes the selected item).
- Normalize on add: trim, lower-case, add `.exe` if missing, reject empty or duplicates (show inline hint).
- Blocked apps are excluded from the mixer immediately.

---

## 4. Audio engine (`AudioMixerService`)

Use NAudio Core Audio:
- `MMDeviceEnumerator` -> default render endpoint (`GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)`).
- Master volume: `device.AudioEndpointVolume` (`MasterVolumeLevelScalar`, `Mute`, `OnVolumeNotification`).
- Sessions: `device.AudioSessionManager.Sessions`; each `AudioSessionControl` provides `GetProcessID`, `DisplayName`, `IconPath`, `State`, `SimpleAudioVolume` (`Volume` 0 to 1, `Mute`), and `AudioMeterInformation`.

Design:
- All COM calls run on a dedicated background thread/queue (not the UI thread). Marshal results to the UI with `DispatcherQueue.TryEnqueue`.
- **Group sessions by process (exe name)**: one app may have several sessions (e.g. Chrome tabs). Show one row per app; the slider sets `SimpleAudioVolume.Volume` on **all** sessions of that app; the displayed value is the max (or average) of them.
- **Events first, polling second**: subscribe to `AudioSessionManager.OnSessionCreated`, and per-session `RegisterEventClient` (`OnSimpleVolumeChanged`, `OnStateChanged`, `OnSessionDisconnected`). Use the **Polling Interval** timer only while the overlay is Expanded/MixerOpen to refresh peak meters (optional) and catch missed changes. Stop polling when Idle.
- **Default device changes**: implement `IMMNotificationClient` (`OnDefaultDeviceChanged`) and rebuild sessions on the new device. Also handle device removal.
- **Filtering**:
  - Remove sessions of the app's own process and system-sound session unless the user expects it (show "System sounds" as a row, with the pid 0 session mapped to that name and a system icon).
  - Remove blacklisted process names.
  - If **Show only active audio apps** is on, show only sessions with `AudioSessionState.AudioSessionStateActive`; otherwise show active and inactive sessions (but not expired ones).
- Sort rows by name (stable order, so rows don't jump around when apps start or stop playing).
- Writes: set volume on slider `ValueChanged` only when the change comes from the user. Ignore updates caused by events/polling to avoid feedback loops. While a slider is being dragged, ignore incoming external updates for that row.
- Dispose all NAudio/COM objects when sessions or devices go away (memory leak prevention).

### App name and icon resolution (important: Store apps like WhatsApp)
Name, in order: `session.DisplayName` (if not empty and not a resource string like `@%SystemRoot%...`) -> process `FileVersionInfo.FileDescription` -> `ProductName` -> exe file name without extension.

Icon, in order:
1. If `session.IconPath` is a valid file or resource, extract it (`SHDefExtractIcon` / `ExtractIconEx`).
2. Process exe icon via `SHGetFileInfo` or `IShellItemImageFactory.GetImage` (use 32x32 or larger for sharp rendering on high DPI).
3. For packaged (UWP/MSIX) apps where the exe icon is generic, resolve the package logo via the package manifest (`Windows.ApplicationModel.Package` / `PackageManager.FindPackageForUser`, `Logo`).
4. Fallback: generic app glyph (`\uE71D`).

Convert to `BitmapImage`/`SoftwareBitmapSource` on the UI thread. **Cache icons** by exe path for the lifetime of the app.

Reading another process's path may throw (access denied); wrap in try/catch and fall back.

---

## 5. Mixer panel UI

Row layout (Grid columns): `Auto` icon (24 px) | `*` slider | `Auto` percent (min width 44 px) | `Auto` mute button (32 px).
- Percent text is right aligned. Use a `ToolTip` on the row with the full app name and process name.
- Mute button toggles `SimpleAudioVolume.Mute`; icon changes between volume and muted glyphs; the slider is dimmed when muted.
- Panel header: mixer glyph + "Volume Mixer" title with a divider line.
- Max panel height about 360 px, then vertical scrolling (`ScrollViewer`).
- Empty state: "No apps are playing audio" (centered, secondary text brush).
- Keyboard accessibility: Tab order icon -> slider -> mixer button -> rows; arrow keys adjust sliders; add `AutomationProperties.Name` to every control.

---

## 6. Theming (Light / Dark / System)

- Store `ThemeMode` = `Light | Dark | System`.
- Apply by setting `RequestedTheme` (`Light`, `Dark`, `Default`) on the root `FrameworkElement` of the settings UI **and** of the overlay window content. Do not rely on `Application.RequestedTheme` (it is fixed at startup).
- For `System`, listen to `UISettings.ColorValuesChanged` (and `ActualThemeChanged`) and refresh the overlay, including backdrop tint.
- Use only theme resources, never fixed hex colors:
  - Backgrounds: `CardBackgroundFillColorDefaultBrush`, `LayerFillColorDefaultBrush`, or acrylic.
  - Text: `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`.
  - Borders: `CardStrokeColorDefaultBrush`, `DividerStrokeColorDefaultBrush`.
  - Accent (slider fill, Add button): `AccentFillColorDefaultBrush` / `AccentButtonStyle`.
- Check contrast in both themes (idle chip at 50 percent opacity must stay visible on light and dark wallpapers; give the chip a subtle border for this).
- Icons: use `FontIcon` with Segoe Fluent Icons (volume `\uE767`, mute `\uE74F`, mixer `\uE9E9`, chevron `\uE70D`) so they follow the foreground color.

---

## 7. Persistence and startup

- Save settings in the same settings store the app already uses (do not create a new mechanism).
- Settings model:

```csharp
public sealed class VolumeMixerSettings
{
    public bool Enabled { get; set; } = false;
    public string IconPosition { get; set; } = "top-center";
    public int IconSize { get; set; } = 32;
    public int IdleOpacity { get; set; } = 50;
    public int HoverOpacity { get; set; } = 100;
    public int SliderWidth { get; set; } = 200;
    public int HoverDelayMs { get; set; } = 500;
    public int MixerHideDelayMs { get; set; } = 1000;
    public int AnimationMs { get; set; } = 250;
    public int PollingMs { get; set; } = 150;
    public bool DragMode { get; set; } = false;
    public bool OnlyActiveApps { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string ThemeMode { get; set; } = "System";
    public List<string> Blacklist { get; set; } = new();
    public double? CustomX { get; set; }   // used when dragged
    public double? CustomY { get; set; }
}
```

- Clamp values on load.
- **Start with Windows**: use the same startup mechanism the app already uses (MSIX `StartupTask` if packaged, otherwise `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`). Start minimized/in tray, with the overlay running.
- The overlay and the audio engine must keep running when the main window is closed (tray/background), and stop fully when **Enabled** is off.

### Drag mode
- When on: show a subtle drag outline on the chip; press-and-drag moves the window (use `AppWindow.Move`, clamp to the visible work area). On release, save `CustomX/CustomY` and set position to custom.
- When off: position comes from **Icon Position**; changing Icon Position clears `CustomX/CustomY`.

---

## 8. Files

```
/Services/AudioMixerService.cs            <- sessions, grouping, events, master volume
/Services/AppIconResolver.cs              <- name + icon resolution and cache
/Services/VolumeMixerSettings.cs
/Views/VolumeOverlayWindow.xaml(.cs)      <- floating overlay (chip, bar, mixer panel)
/Views/Controls/MixerRow.xaml(.cs)
/Views/VolumeMixerSettingsPage.xaml(.cs)  <- 3 tabs
/Views/Controls/VolumeMixerCard.xaml(.cs) <- card in Others page
/ViewModels/VolumeMixerViewModel.cs
/ViewModels/MixerSessionViewModel.cs      <- Name, Icon, Volume, IsMuted, ProcessName
```

NuGet: `NAudio` (use the Core Audio parts; `NAudio.Wasapi` / `NAudio.CoreAudioApi`).

---

## 9. Edge cases

- **No audio device or device disabled**: show the idle chip with a muted/disabled glyph; mixer panel shows "No audio device found". Recover automatically when a device appears.
- **App closes while the mixer is open**: remove its row with a short fade; no exceptions.
- **Many sessions** (10 or more): panel scrolls; no UI stall (build rows incrementally, update with diffs, not full rebuilds).
- **Admin / protected processes** whose info can't be read: still show them with the session display name and a generic icon.
- **Remote desktop / Bluetooth devices** with no per-session volume support: handle `COMException` and hide the row.
- **Fullscreen exclusive apps (games)**: overlay may be hidden by the OS; do not crash. Optional setting "Hide over fullscreen apps" using `SHQueryUserNotificationState`.
- **Multi-monitor and DPI changes**: reposition on `DisplayArea` changes.
- **Volume changed outside the app** (keyboard keys, Windows mixer): UI must update within about the polling interval or immediately through events.
- Never block the UI thread with COM calls. Guard against re-entrancy from event callbacks.
- Blacklist input `chrome` becomes `chrome.exe`; matching is case-insensitive.

---

## 10. Acceptance checklist

- [ ] Overlay icon appears at the chosen position, at Idle Opacity, and does not steal focus.
- [ ] Hovering for Hover Delay expands to the master slider; leaving collapses after Mixer Hide Delay.
- [ ] Master slider, scroll wheel and mute click all work, and follow volume changes made outside the app.
- [ ] Mixer button opens the panel with one row per app (icon, name, slider, percent, mute).
- [ ] Moving an app's slider changes only that app's volume (including multi-session apps like browsers).
- [ ] Per-app mute works and is reflected in the UI.
- [ ] Store apps (e.g. WhatsApp) show correct names and icons; failures fall back to a generic icon.
- [ ] "Show only active audio apps" and the blacklist filter rows correctly and instantly.
- [ ] All settings in the 3 tabs apply live, persist after restart, and respect the ranges.
- [ ] Light, Dark and System themes render correctly in settings and overlay; System follows OS theme changes live.
- [ ] Drag mode moves and saves the position; changing Icon Position resets it.
- [ ] Start with Windows works; the overlay runs when the main window is closed.
- [ ] Changing the default audio device, unplugging a device and closing apps cause no crash or stale rows.
- [ ] No memory growth or high CPU in Idle (polling is off when collapsed).
