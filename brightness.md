# Others → Brightness — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project with the same MVVM structure, services, and Fluent look. Treat this as a brand-new feature (no other brightness tool is involved).

The feature has two parts:
1. **Brightness control** with global keyboard shortcuts (set a percentage, assign a hotkey to it).
2. **Blur overlay**: blur the whole screen with an adjustable intensity and a **live preview**, toggled by a hotkey.

## 1. Navigation and page structure

- Add a new item to the left navigation rail named **Others** (icon: Segoe Fluent `\uE712` More or `\uF0E2` Apps), placed above Settings.
- **Others page** (`OthersPage`): a responsive grid of **cards** (this page will hold more utilities later, so make the card component reusable: icon, title, one-line description, optional status text). First card: **Brightness** (icon `\uE706`), description "Control screen brightness with shortcuts and a blur overlay", live status text such as "60% · Blur off".
- Clicking the **Brightness card** navigates inside the Others frame to the **Brightness settings page** (`BrightnessPage`) with a `BreadcrumbBar` (Others › Brightness) and a back button. The page uses native Windows 11 Settings-style cards (`SettingsCard` / `SettingsExpander` from `CommunityToolkit.WinUI.Controls.SettingsControls`).
- Theme: **Light / Dark / System**, like all other pages. Use `ThemeResource` brushes only, no hard-coded colors; verify live theme switching.
- This feature works independently of the AppLock master protection toggle. It keeps working (hotkeys, overlay) while the app is minimized to the tray. Do not unregister hotkeys when the window closes to the tray.

## 2. Brightness page layout (top to bottom)

### 2.1 Current brightness (SettingsCard)
- Title "Brightness", big current value (e.g. **60%**), a **Slider** (0–100, step 1, tick marks every 10, `TickPlacement=Outside`) for manual control. Changes apply live while dragging (debounce hardware writes to ~80 ms, always send the final value).
- **Display selector** (ComboBox): *All displays* (default) or a single monitor (name + resolution, e.g. "DELL U2720Q · DDC/CI"). Each display shows its control method as a small chip: **Built-in**, **External (DDC/CI)**, or **Software dimming**.
- The slider reflects changes made outside AppLock (laptop Fn keys, Windows quick settings) by listening for brightness change events (see section 4) and polling every 2 s while the page is visible.

### 2.2 Keyboard shortcuts (SettingsExpander, expanded by default)
A list of **brightness presets**, each row containing:
- **Brightness %** (NumberBox 0–100 with spin buttons)
- **Shortcut** displayed as keycaps (e.g. `Ctrl` `Shift` `0`)
- **Edit** button that opens the hotkey capture dialog (section 3)
- **Delete** (trash) button

Default presets (same as the old tool): 0% = Ctrl+Shift+0, 10% = Ctrl+Shift+1, 20% = Ctrl+Shift+2 … 90% = Ctrl+Shift+9, and 100% = Ctrl+Shift+F.

Footer of the expander: **Add preset** (new row with percentage and no shortcut until the user records one), **Reset to defaults** (confirmation dialog). Changes **save automatically**; show an `InfoBar`-style "Saved" confirmation briefly. (No separate Save button is needed.)

Optional step hotkeys in the same expander: **Increase brightness** and **Decrease brightness** (step size NumberBox, default 10%), unassigned by default.

### 2.3 Blur overlay (SettingsExpander, expanded by default)
- **Toggle Blur Overlay** switch + **Toggle shortcut** keycaps with Edit (default **Ctrl+Shift+B**).
- **Blur level** slider 0–100% with tick marks (default 50%), shows the value on the right. Moving the slider updates the live preview and, if the overlay is on, the real overlay immediately.
- **Live preview**: a rounded preview panel (16:9, scaled to the card width) showing the current screen content (the selected display) blurred at the chosen intensity, updating in real time (~15–30 fps) while the page is visible. It starts when the page opens/expander is expanded and stops when the page is left or the window is hidden, to save CPU/GPU. If screen capture is unavailable, show a static sample image blurred with the same effect and an explanatory note.
- **Overlay options**:
  - Apply to: all displays / selected display
  - Dim amount under the blur (0–60%, default 0) and optional tint (None / Dark / Light)
  - "Exclude the active window from the blur" (cut-out so the focused window stays sharp; optional, milestone 8)
  - "Keep overlay on after restarting AppLock" (default off)
- A **Toggle Blur Overlay** button (full-width, same action as the shortcut).

### 2.4 General options (SettingsCard group)
- "Show on-screen display when brightness changes" (default on): small, themed pill at the bottom-center of the screen showing a sun icon and the percentage for ~1.2 s (click-through, not activating, not captured).
- "Pressing a preset's shortcut again returns to the previous brightness" (default off) — this is the "toggle" behavior.
- "Restore the original brightness when AppLock exits" (default off)
- "Remember brightness and apply at startup" (default off)
- "Minimum software dimming brightness" (default 10%; prevents the screen from becoming unreadable, see safety)

### 2.5 Hotkey diagnostics (Expander, collapsed by default)
Replaces the old "Hotkey Debugger". Shows (a) a table of every hotkey with its registration status (Registered / Failed: reason such as "already in use by another app"), and (b) a live log of the last 200 events (time, key combination, action, result). Buttons: **Clear log**, **Copy**, **Re-register all hotkeys**. Redact nothing sensitive: no key logging other than registered AppLock combinations.

## 3. Hotkey capture and validation

- Edit opens a `ContentDialog`: "Press the new shortcut" with a live keycap display, **Save**, **Clear**, **Cancel**. Capture with a low-level approach limited to the dialog (key-down handling in the dialog while it has focus; do **not** install a global keyboard hook).
- Rules: at least one modifier (Ctrl/Alt/Shift/Win) plus one non-modifier key; allow F-keys alone only with a modifier. Keys: letters, digits (top row and numpad), F1–F24, arrows, Home/End/PgUp/PgDn, Insert/Delete, punctuation.
- Reject with a clear message: combinations already used by another AppLock hotkey (including the emergency lock hotkey Ctrl+Alt+L and any BitLocker/other shortcuts), well-known system-reserved combos (Ctrl+Alt+Del, Win+L, Alt+Tab, Alt+F4, Win+D, etc.), and combos that `RegisterHotKey` rejects (already registered by another app → "This shortcut is already used by another program").
- Display names are localized and consistent (`Ctrl + Shift + 0`). Store as modifiers + virtual key code, not as a string.
- Use `RegisterHotKey` with `MOD_NOREPEAT` on one hidden message-only window owned by a shared `HotkeyService` (create it if it doesn't exist yet and reuse the same service as the emergency lock hotkey). Handle `WM_HOTKEY` and dispatch to actions. Unregister/re-register when settings change. Re-register after resume from sleep and after explorer restarts.

## 4. Brightness engine

Create `IBrightnessProvider` with implementations and a `BrightnessService` that picks the best provider for each display (in this order):

1. **Built-in panel (laptops):** WMI in `root\WMI`: `WmiMonitorBrightness` (read `CurrentBrightness`, `Level[]`) and `WmiMonitorBrightnessMethods.WmiSetBrightness(timeout=1, brightness)`. Subscribe to `WmiMonitorBrightnessEvent` to detect external changes. Map the 0–100 percentage to the supported levels (round to the nearest supported level).
2. **External monitors (DDC/CI):** Dxva2 high-level monitor API: `GetNumberOfPhysicalMonitorsFromHMONITOR`, `GetPhysicalMonitorsFromHMONITOR`, `GetMonitorBrightness`, `SetMonitorBrightness`, `GetMonitorCapabilities`; always `DestroyPhysicalMonitors`. Map min/max reported by the monitor to 0–100. DDC/CI is slow and sometimes fails: run on a background worker per monitor, queue and coalesce writes (latest value wins), retry up to 3 times, and cache capabilities. Match each physical monitor to its display name using `EnumDisplayDevices`/`DisplayConfigGetDeviceInfo`.
3. **Software dimming fallback** (for displays without hardware control): a per-monitor, borderless, topmost, click-through, non-activating, tool-window overlay (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`) filled with black at opacity = `1 - brightness/100`, clamped by the "Minimum software dimming brightness" setting (never fully black). Do **not** use gamma ramps (they persist unpredictably and can be reset by other apps).

Rules:
- Percent values are 0–100 integers. For hardware, 0% means the panel's minimum, which on many laptops is still readable; keep it as the hardware reports.
- Handle display changes (`WM_DISPLAYCHANGE`, `SystemEvents.DisplaySettingsChanged`), monitors added/removed, and resume from sleep: re-detect displays and re-apply the target brightness if "Remember brightness" is on.
- Never block the UI thread; all WMI/DDC calls are async with timeouts (WMI 3 s, DDC 2 s) and errors surface as a non-blocking InfoBar ("Couldn't change brightness on <monitor>: <reason>").
- If no display supports hardware control, show an InfoBar explaining that software dimming is being used.

## 5. Blur overlay engine

Goal: a click-through overlay that shows the screen content behind it blurred with an adjustable Gaussian blur, updated live.

Recommended implementation:
- **Capture:** `Windows.Graphics.Capture` (`GraphicsCaptureItem` from the monitor via `IGraphicsCaptureItemInterop.CreateForMonitor`, `Direct3D11CaptureFramePool.CreateFreeThreaded`). Try `GraphicsCaptureSession.IsBorderRequired = false` (request borderless access if available); if the yellow capture border can't be disabled, fall back to **DXGI Desktop Duplication** (`IDXGIOutputDuplication`) for the overlay path.
- **Blur:** Win2D (`Microsoft.Graphics.Win2D`) `GaussianBlurEffect` with `BlurAmount` mapped from the 0–100% slider to roughly 0–48 px (scaled for DPI), `BorderMode = Hard`, rendered into a `CanvasSwapChain`/`CanvasSwapChainPanel` hosted in a full-screen borderless window per display.
- **Overlay window:** topmost, borderless, click-through (`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`), covers the monitor bounds, not shown in Alt+Tab, per-monitor DPI aware.
- **Avoid feedback loops:** call `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` on the overlay (Windows 10 2004+) so the capture shows the real screen beneath it. Fallback when unsupported: capture is paused and the overlay uses `DesktopAcrylicBackdrop` (fixed blur, with tint/dim adjustments) and the UI says "Intensity is limited on this Windows version".
- **Preview:** the same capture + Win2D pipeline renders into a small `CanvasControl`/`SwapChainPanel` in the settings card using the selected display, at a reduced resolution (e.g. 480 px wide) and the current slider value. While the preview is running, exclude the AppLock main window from capture (`WDA_EXCLUDEFROMCAPTURE`) so the preview doesn't show itself recursively, and restore the affinity when the preview stops.
- **Performance:** target ≤ 5% CPU/GPU on a typical machine: capture at ≤ 30 fps, use reduced-resolution blur (downscale → blur → upscale) for high intensities, skip frames when nothing changed, stop capture entirely when the blur level is 0 or the overlay is off and the preview isn't visible.
- **Interplay with AppLock locking:** while any AppLock **lock overlay or password/auth dialog** is visible, temporarily hide the blur overlay (restore it afterwards) so the password prompt stays readable. The blur overlay must also never block the emergency lock hotkey.
- The overlay does not appear above the secure desktop (UAC prompts, Ctrl+Alt+Del) or exclusive full-screen games; document that limitation in the UI help text.
- Multi-monitor: one overlay per selected display; handle hot-plug and DPI changes.

## 6. Data model and persistence

Store in the existing AppLock settings JSON (same integrity protection as other sections):

```csharp
public class BrightnessSettings
{
    public List<BrightnessPreset> Presets { get; set; } = DefaultPresets();
    public HotkeyBinding? IncreaseHotkey { get; set; }
    public HotkeyBinding? DecreaseHotkey { get; set; }
    public int StepPercent { get; set; } = 10;
    public bool ShowOsd { get; set; } = true;
    public bool PressAgainRestoresPrevious { get; set; }
    public bool RestoreOnExit { get; set; }
    public bool RememberAndApplyAtStartup { get; set; }
    public int LastBrightness { get; set; } = 100;
    public int MinSoftwareBrightness { get; set; } = 10;
    public string? SelectedDisplayId { get; set; } // null = all displays
    public BlurSettings Blur { get; set; } = new();
}

public class BrightnessPreset { public Guid Id = Guid.NewGuid(); public int Percent; public HotkeyBinding? Hotkey; }

public class HotkeyBinding { public uint Modifiers; public uint VirtualKey; }

public class BlurSettings
{
    public int LevelPercent { get; set; } = 50;
    public HotkeyBinding? ToggleHotkey { get; set; }   // default Ctrl+Shift+B
    public int DimPercent { get; set; }
    public string Tint { get; set; } = "None";        // None, Dark, Light
    public bool ExcludeActiveWindow { get; set; }
    public bool KeepOnAfterRestart { get; set; }
    public string? SelectedDisplayId { get; set; }
}
```

Validate on load (percent 0–100, unique hotkeys, level 0–100) and migrate missing fields safely.

## 7. Services and structure

```
src/AppLock/
  Views/OthersPage.xaml(.cs), Views/BrightnessPage.xaml(.cs), Views/Dialogs/HotkeyCaptureDialog, Views/Controls/OtherCard, KeyCapChips, ScreenBlurPreview
  ViewModels/OthersViewModel, BrightnessViewModel, PresetViewModel, DisplayViewModel
  Services/Brightness/
    BrightnessService.cs            // picks providers, queue/debounce, events
    WmiBrightnessProvider.cs
    DdcCiBrightnessProvider.cs
    SoftwareDimProvider.cs          // per-monitor dim overlays
    DisplayEnumerator.cs
    OsdService.cs                   // on-screen display
    BlurOverlayService.cs           // capture + blur + overlays
    ScreenCaptureService.cs         // Graphics Capture / DXGI duplication
    HotkeyService.cs                // shared (RegisterHotKey / WM_HOTKEY)
    HotkeyValidator.cs
  Models/BrightnessSettings.cs, HotkeyBinding.cs
tests/AppLock.Tests/Brightness/     // fake providers: preset mapping, debounce, hotkey validation/conflicts, settings migration
```

Add `IBrightnessProvider`, `IScreenCapture`, and `IHotkeyRegistrar` interfaces so logic is testable without hardware.

## 8. Safety, security, and privacy

- Never leave the user with an unreadable screen: enforce the minimum software dimming floor; keep a built-in **"Reset display" hotkey** (default **Ctrl+Alt+Shift+R**, rebindable, cannot be cleared) that sets 100% brightness, removes all dim/blur overlays, and shows the OSD. Also expose "Reset display" in the tray menu and as a button on the page.
- On normal exit: remove overlays, restore brightness if configured. If AppLock crashes, overlays disappear with the process; on next start, if "Remember brightness" is off, do nothing to hardware brightness.
- Screen capture is used only locally for the blur and preview. **No frames are saved, logged, or transmitted.** Do not write captured images to disk. Show a short note in the UI: "AppLock reads the screen only to blur it. Nothing is saved or sent."
- No global keyboard hook, no logging of keystrokes other than matched AppLock hotkeys.
- OSD and overlays must not steal focus or intercept clicks.

## 9. UX and quality rules

- Light/Dark/System support, keyboard navigation, screen reader names (`AutomationProperties.Name`), high-contrast support; status not by color alone.
- Localize all strings through the existing resource file.
- The Others page cards and the settings page follow the same spacing, corner radius, and elevation as the existing pages; use Mica/Acrylic consistently with the app.
- Disabled controls have tooltips that explain why (e.g. "This monitor doesn't support hardware brightness; using software dimming").
- Log events (hotkey registered/failed, provider chosen, errors) to `%APPDATA%\AppLock\logs`, with no screen content.

## 10. Milestones (build, run, verify each before moving on)

1. **Others** nav item, `OthersPage` card grid with the Brightness card, navigation to `BrightnessPage` with breadcrumb and an empty settings skeleton; theme-correct.
2. Display enumeration + brightness engine for the built-in panel (WMI) and the manual slider with current value sync.
3. DDC/CI provider for external monitors and the software-dimming fallback; display selector and method chips.
4. `HotkeyService` + hotkey capture dialog + validation; presets list (edit %, edit hotkey, add, delete, reset defaults), auto-save; hotkeys work while the app is in the tray.
5. OSD, "press again to restore previous", step hotkeys, startup/exit options, "Reset display" hotkey.
6. Blur overlay (capture + Win2D blur + click-through overlays, toggle hotkey and button, live level changes, multi-monitor).
7. Live preview panel in the settings card with feedback-loop prevention; performance tuning.
8. Overlay options (dim, tint, exclude active window), interplay with AppLock lock overlays/dialogs, fallbacks (acrylic) when capture isn't available.
9. Hotkey diagnostics expander, tests, theme/accessibility pass, manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, show git message to commit if user need to do manually then STOP and wait for "continue".

## 11. Manual test checklist

- [ ] "Others" appears in the sidebar; the Brightness card opens the settings page; back/breadcrumb work; light, dark, and system themes look right
- [ ] Manual slider changes the laptop panel brightness live; changes made with Fn keys/Windows quick settings update the slider
- [ ] An external monitor (DDC/CI) changes brightness; a monitor without DDC/CI uses software dimming and never goes fully black
- [ ] Default shortcuts Ctrl+Shift+0…9 and Ctrl+Shift+F set 0–100%; works with the window closed to the tray
- [ ] Editing a preset's % and its shortcut works; duplicates and reserved or in-use shortcuts are rejected with clear messages; add/delete/reset defaults work; settings persist after restart
- [ ] "Press again returns to previous brightness" behaves as described when enabled
- [ ] OSD shows the percentage and doesn't steal focus
- [ ] Blur toggle hotkey (Ctrl+Shift+B) and button turn the overlay on/off on all or the selected display; clicks pass through; slider changes the blur live
- [ ] Preview shows the current screen blurred with the same intensity, updates live, doesn't show itself recursively, and stops when leaving the page (CPU/GPU drops)
- [ ] A lock overlay or password dialog from AppLock stays readable while the blur is on
- [ ] Reset display hotkey/tray/button restores 100% and removes overlays
- [ ] Unplugging/plugging a monitor, sleep/resume, and DPI changes are handled without crashes
- [ ] Diagnostics lists hotkey statuses (including a deliberately conflicting one) and the event log; nothing about screen content is stored on disk
