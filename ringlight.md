# Others → Ring Light — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, and Fluent look. Treat this as a brand-new feature.

## 1. Idea

A **Ring Light** turns the edges of the screen into a bright, soft frame of light so the user's face is lit during video calls, like the ring-light / studio-light effect on a MacBook. The light is drawn by a click-through overlay on top of everything, so the user can keep working normally in the clear middle of the screen. The user can customize color/temperature, brightness, thickness, softness, which edges light up, which display is used, and when it turns on (hotkey, tray, or automatically when the camera starts).

## 2. Navigation and placement

- The feature lives in the existing **Others** navigation page, as a card named **Ring Light** (icon: Segoe Fluent `\uE781` or any light/sun-ring glyph), description "Light your face during calls using your screen edges", status text such as "On · 4500K · 70%" or "Off".
- **Card order:** the Ring Light card appears **immediately after the Clipboard card**. Implement card order through a registry with explicit order values (for example Brightness 10, Eye Care 20, Volume Mixer 30, Clipboard 40, **Ring Light 50**) so the order doesn't depend on file order. If the Clipboard card isn't in the project yet, still use order 50 so it lands after Clipboard once it exists.
- Clicking the card opens the **Ring Light settings page** inside the Others frame, with a `BreadcrumbBar` (Others › Ring Light) and a back button, using Windows 11 Settings-style cards (`SettingsCard` / `SettingsExpander` from `CommunityToolkit.WinUI.Controls.SettingsControls`), exactly like the other Others pages.
- **Theme:** the whole page, dialogs, flyouts, and the in-page preview chrome follow the app's **Light / Dark / System** setting (theme resources only, no hard-coded colors, live switching). The ring's own color is the user's choice and is not affected by the app theme.

## 3. Settings page layout (top to bottom)

### 3.1 Master control (SettingsCard)
- Large **Ring Light ToggleSwitch** (on/off) with status text, plus the **Toggle shortcut** keycaps with an Edit button (default **Ctrl+Shift+L**).
- A live **preview panel** next to or below the toggle: a small mock laptop screen (16:9) with the ring drawn exactly as configured (color, thickness, softness, edges, corner radius), updating in real time while the user changes settings. The preview works even when the real ring is off. A **"Preview on my screen for 5 seconds"** button briefly shows the real overlay.

### 3.2 Appearance (SettingsExpander, expanded)
- **Color mode** (SegmentedControl): *Temperature* (default) or *Custom color*.
  - Temperature: slider from **2700K (warm) to 6500K (cool)**, default 4500K, with a gradient track preview and the Kelvin value shown. Convert Kelvin to RGB with a standard approximation (for example Tanner Helland's algorithm) in a small tested helper.
  - Custom color: `ColorPicker` (with hex input and recent colors).
- **Brightness (intensity)**: 10–100% (default 80%). Controls the overlay opacity/luminance.
- **Ring thickness**: 2–30% of the shorter screen side (default 8%).
- **Softness (feather)**: 0–100% (default 50%): how smoothly the light fades from the edge toward the center.
- **Corner radius**: 0–200 px (default 0), for a rounded inner frame.
- **Edges** (checkboxes with a small diagram): Top, Bottom, Left, Right (default all on; many users prefer Top + Left + Right).
- **Style** (SegmentedControl): *Frame* (default) or *Full screen glow* (a whole-screen light, with intensity capped at 60% so the screen stays usable).
- **Keep taskbar clear** (toggle, default on): draw the ring within the monitor's work area (leaving the taskbar visible) or over the full monitor bounds when off.
- **Fade in/out duration** (0–1000 ms, default 300 ms). The light itself never flickers or pulses.

### 3.3 Presets (SettingsExpander)
- Built-in preset chips: **Warm**, **Neutral**, **Cool**, **Soft**, **Studio**, **Max light**. Clicking a chip applies it instantly.
- **Save current as preset** (name dialog), rename, delete custom presets. The selected preset shows as "modified" when settings change.

### 3.4 Display (SettingsCard)
- **Show on**: Primary display (default), Built-in display, All displays, or a specific display (name and resolution). Handle monitors being plugged/unplugged and DPI changes.
- Optional **brightness boost**: "Raise screen brightness while the ring light is on" with a NumberBox (default 100%) that uses the app's brightness service (`BrightnessService` from `brightness.md`) when available, and restores the previous brightness when the ring light turns off. Disabled with an explanatory tooltip if the brightness feature isn't present or the display can't be controlled. Default off.

### 3.5 Automatic (SettingsExpander)
- **Turn on automatically when the camera is in use** (default off) and **turn off when the camera stops** (with a 5-second delay, so brief camera drops don't flicker the light).
- **App filter**: *Any app* (default) or *Only these apps* with an editable list (add from running programs or browse for exe, e.g. Teams, Zoom, Chrome, Edge, Discord, Skype).
- **Auto turn off after N minutes** (0 = never, default 0), useful against burn-in on OLED screens.
- **Auto warmth** (optional): shift color temperature by time of day (for example 5500K during the day and 3500K after 20:00, with editable times). Default off.
- Camera detection (no camera access needed, see section 5.3). A line of help text: "AppLock only reads which apps are using the camera. It never opens the camera or sees video."

### 3.6 Shortcuts (SettingsExpander)
- **Toggle ring light** (default Ctrl+Shift+L), **Increase brightness** and **Decrease brightness** (optional, unassigned by default, step 10%), **Warmer** and **Cooler** (optional, step 250K). Use the shared `HotkeyService`, the hotkey capture dialog, and the validator from `brightness.md` (conflicts with other AppLock hotkeys such as Ctrl+Alt+L, Ctrl+Alt+Shift+R, and reserved system combos must be rejected with clear messages).

### 3.7 Options (SettingsCard group)
- **Show ring in screen sharing and recordings** (default **off**): by default the overlay is excluded from screen capture so people on the call don't see a white frame in your shared screen.
- **Show a small notification when turned on/off** (follows "Mute notifications"; use the OSD service if available).
- **Turn on at startup** (default off): ring light state at AppLock launch.
- **Remember state** (default on).
- Tip text under the page title: "For the best result: raise screen brightness, use a white or warm color, and increase ring thickness. A screen is a weaker light than a real ring light."

### 3.8 Tray integration
Add to the AppLock tray menu: **Ring Light** (checkable) and a **Presets** submenu. Works while the main window is hidden.

## 4. Ring rendering

- **Window:** one borderless overlay window per target display: always on top, **click-through** (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`), not in Alt+Tab, never takes focus, covers the monitor bounds (or work area), per-monitor DPI aware, transparent background.
- **Drawing:** use **Win2D** (`Microsoft.Graphics.Win2D`) or Composition effects: fill the area with the ring color, then cut out the inner rounded rectangle (thickness, edges, corner radius) and soften the transition with a Gaussian blur or gradient mask using the "Softness" value, so the inner edge fades smoothly. The mask must support enabling/disabling each edge independently. Redraw only when settings change (no continuous animation loop), and animate opacity for fade in/out.
- **Brightness:** brightness maps to alpha (and to a slightly higher luminance of the color), with 100% reaching pure, full-intensity white for white light. Pre-multiply alpha correctly to avoid dark halos.
- **Capture exclusion:** call `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` on the overlay unless the user turned on "Show ring in screen sharing and recordings". On Windows versions that don't support it, show a short note that the ring may appear in screen shares.
- **Z-order:** stays above normal windows and above borderless full-screen video call windows. It can't cover exclusive full-screen apps, UAC prompts, or the lock screen; document this in a help line.
- **Interplay with other AppLock features:**
  - The Brightness page's **blur overlay** captures the screen, so the ring (excluded from capture) is not blurred and stays crisp.
  - AppLock lock overlays and password dialogs stay readable: the ring only occupies the screen edges, so no special handling is needed, but keep the ring below the AppLock lock overlay in z-order when both are shown on the same display.
  - The ring must not interfere with the AppLock emergency lock hotkey or other hotkeys.
- **Multi-monitor/hot-plug/sleep:** re-create overlays on `WM_DISPLAYCHANGE`, resume from sleep, explorer restart, and DPI change.
- **Performance:** idle cost near zero (static content); no polling for rendering.

## 5. Services and behavior

### 5.1 State machine
`Off` → `FadingIn` → `On` → `FadingOut` → `Off`. All triggers (toggle control, hotkey, tray, preset change, automatic camera detection, auto-off timer) call the same `RingLightService.SetEnabled(bool, reason)`. Settings changes while On apply live to the overlay.

### 5.2 Presets and color math
- `ColorTemperature.ToRgb(kelvin)` (clamped 2700–6500) with unit tests for monotonic warmth (red stays high, blue increases with Kelvin) and sane output at the endpoints.
- Preset definitions (starting values, tune visually): Warm (3200K, 85%, thickness 10%), Neutral (4500K, 80%, 8%), Cool (6000K, 80%, 8%), Soft (4500K, 60%, 12%, softness 80%), Studio (5000K, 90%, 10%, edges Top/Left/Right), Max light (6500K, 100%, 18%, softness 30%).

### 5.3 Camera usage detection (for "turn on automatically")
Read the Windows privacy consent store, which tracks which apps use the camera, without touching the camera:
- Registry path: `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam`
  - Packaged apps: subkeys named by package family name.
  - Desktop apps: `NonPackaged\<path with # instead of \>`.
  - Each app key has `LastUsedTimeStart` and `LastUsedTimeStop` (QWORD FILETIME). An app is **using the camera** when `LastUsedTimeStart > LastUsedTimeStop` (typically `LastUsedTimeStop == 0`).
- Watch for changes with `RegNotifyChangeKeyValue` (recursive) on a background thread, and also re-check every 3 seconds as a fallback. Debounce: on requires 1 second of continuous use; off requires 5 seconds of no use.
- Apply the app filter by matching the executable name (desktop) or package family name (packaged). Browser-based calls show up as the browser app.
- Ignore AppLock itself. If the key is missing or unreadable, disable the automatic option and show an InfoBar explaining why.
- Optionally add a small "Test detection" button that shows which apps are currently using the camera.

### 5.4 Auto turn-off timer and auto warmth
- A single timer service handles "turn off after N minutes" (restarts when the ring turns on) and the day/evening temperature interpolation (smooth change over 15 minutes, evaluated each minute using the local clock, handling time changes/DST).

### 5.5 Brightness boost
When enabled, on turn-on: remember each controlled display's current brightness, set the target; on turn-off or app exit: restore. If the user changes brightness manually while the ring is on, don't overwrite their change on restore (restore only if the value is still the one AppLock set).

## 6. Data model and persistence

Store in the AppLock settings JSON (same integrity protection as the other sections; migrate safely when fields are missing):

```csharp
public class RingLightSettings
{
    public bool Enabled { get; set; }                  // remembered state
    public bool StartEnabledAtLaunch { get; set; }
    public string ColorMode { get; set; } = "Temperature"; // Temperature | Custom
    public int Kelvin { get; set; } = 4500;
    public string CustomColorHex { get; set; } = "#FFFFFF";
    public int BrightnessPercent { get; set; } = 80;
    public int ThicknessPercent { get; set; } = 8;
    public int SoftnessPercent { get; set; } = 50;
    public int CornerRadiusPx { get; set; } = 0;
    public bool EdgeTop { get; set; } = true;
    public bool EdgeBottom { get; set; } = true;
    public bool EdgeLeft { get; set; } = true;
    public bool EdgeRight { get; set; } = true;
    public string Style { get; set; } = "Frame";       // Frame | FullGlow
    public bool KeepTaskbarClear { get; set; } = true;
    public int FadeMs { get; set; } = 300;
    public string DisplayTarget { get; set; } = "Primary"; // Primary | BuiltIn | All | <displayId>
    public bool ShowInScreenCapture { get; set; }
    public bool BoostScreenBrightness { get; set; }
    public int BoostBrightnessPercent { get; set; } = 100;
    public AutoRingSettings Auto { get; set; } = new();
    public HotkeyBinding? ToggleHotkey { get; set; }   // default Ctrl+Shift+L
    public HotkeyBinding? IncreaseHotkey { get; set; }
    public HotkeyBinding? DecreaseHotkey { get; set; }
    public HotkeyBinding? WarmerHotkey { get; set; }
    public HotkeyBinding? CoolerHotkey { get; set; }
    public List<RingPreset> CustomPresets { get; set; } = new();
    public string? ActivePresetId { get; set; }
}

public class AutoRingSettings
{
    public bool OnWhenCameraInUse { get; set; }
    public bool OnlySelectedApps { get; set; }
    public List<string> AppFilter { get; set; } = new(); // exe names or package family names
    public int AutoOffMinutes { get; set; }               // 0 = never
    public bool AutoWarmth { get; set; }
    public int DayKelvin { get; set; } = 5500;
    public int NightKelvin { get; set; } = 3500;
    public string NightStart { get; set; } = "20:00";
    public string NightEnd { get; set; } = "07:00";
}
```

Validate all ranges on load (percentages 0–100, Kelvin 2700–6500, thickness 2–30, unique hotkeys).

## 7. Project structure additions

```
src/AppLock/
  Views/RingLightPage.xaml(.cs), Views/Controls/RingPreview, EdgeSelector, KelvinSlider
  Views/Dialogs/SavePresetDialog, AppFilterDialog
  ViewModels/RingLightViewModel.cs, PresetViewModel.cs
  Services/RingLight/
    RingLightService.cs           // state machine, triggers, settings sync
    RingOverlayManager.cs         // per-display overlay windows, hot-plug handling
    RingOverlayWindow.cs          // Win2D drawing, click-through, capture exclusion
    ColorTemperature.cs
    CameraUsageMonitor.cs         // registry consent-store watcher
    RingLightTimers.cs            // auto-off, auto warmth
    RingPresets.cs
  Models/RingLightSettings.cs
tests/AppLock.Tests/RingLight/    // Kelvin→RGB, camera consent parsing with fake registry, debounce, presets, settings migration, edge-mask geometry
```

Define `ICameraUsageSource`, `IRingOverlay`, and `IBrightnessController` interfaces so logic can be tested without hardware.

## 8. Safety, privacy, and quality

- **Privacy:** never open the camera or read video. Only read the consent-store registry values (app names and timestamps). No network calls, no telemetry, no logging of app names beyond local debug logs in `%APPDATA%\AppLock\logs`.
- **Safety:** the overlay must never block input or take focus; always provide a way to turn it off (toggle, tray, hotkey, and the existing emergency lock hotkey must not be affected). On exit or crash the overlay disappears with the process; restore brightness only on normal exit.
- Warn once, in a small InfoBar on the page, that a bright static frame for long periods can cause burn-in on OLED displays, and point to the auto turn-off timer.
- Accessibility: keyboard navigation, screen reader names, high contrast support for the page controls; the Kelvin slider and color picker have text values; status isn't conveyed by color alone.
- Localize all strings via the existing resource file; keep the UI thread free (registry watcher and timers on background threads, marshal to the dispatcher).

## 9. Milestones (build, run, verify each before moving on)

1. Card registry with explicit order, **Ring Light card placed after Clipboard** in the Others page, navigation to an empty `RingLightPage` with breadcrumb; theme-correct (Light/Dark/System).
2. Overlay window per display (click-through, topmost, transparent, capture exclusion) drawing a basic solid white frame; toggle switch works; fade in/out.
3. Appearance controls: temperature/custom color, brightness, thickness, softness, corner radius, edges, style; live preview panel; settings persistence.
4. Hotkeys (toggle, optional brightness/temperature) using the shared `HotkeyService`; tray menu entry; "Preview 5 seconds".
5. Display targeting (primary/built-in/all/specific), hot-plug and DPI handling, taskbar clearance.
6. Presets (built-in and custom), save/rename/delete.
7. Automatic mode: `CameraUsageMonitor`, app filter, debounce, auto-off timer.
8. Brightness boost integration and optional auto warmth.
9. Tests, theme/accessibility pass, performance check, manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 10. Manual test checklist

- [ ] Ring Light card appears right after Clipboard on the Others page; clicking opens the page; breadcrumb/back work; Light, Dark, and System themes look correct and switch live
- [ ] Toggle, hotkey (Ctrl+Shift+L), and tray all turn the ring on/off with a smooth fade; clicks pass through the overlay; focus is never stolen
- [ ] Temperature slider changes the color from warm to cool; custom color works; brightness, thickness, softness, corner radius, and edge checkboxes update the live preview and the real ring instantly
- [ ] Style "Full screen glow" stays usable (capped intensity)
- [ ] "Keep taskbar clear" works; multi-monitor targets (primary/built-in/all/specific) work; unplugging a monitor or changing DPI doesn't crash
- [ ] Brightness page's blur overlay doesn't blur the ring
- [ ] Opening the camera in Teams/Zoom/Camera app turns the ring on automatically; closing the camera turns it off after ~5 s; app filter works; the camera is never opened by AppLock
- [ ] Auto turn-off timer and auto warmth behave correctly across midnight and time changes
- [ ] Brightness boost raises brightness and restores the previous value afterward (and doesn't override a manual change)
- [ ] Presets apply instantly; custom presets save/rename/delete; settings persist after restart
- [ ] Hotkey conflicts with other AppLock shortcuts are rejected with clear messages
- [ ] Ring does not appear in a screen share or recording by default, and does appear when the option is enabled
