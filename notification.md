# Others → Notifications (Position & Animation) — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, hotkey/OSD helpers and Fluent look as the other **Others** pages.

## 1. Goal

Windows 11 shows toast notifications in the bottom-right corner and always slides them in from the right. This feature lets the user:

1. Choose **where** notifications appear: any of 9 anchor positions, a custom spot picked visually, on any monitor, with an offset.
2. Choose **how** they animate in and out: from the top, bottom, left, right, fade only, or none, with duration and easing.
3. Optionally make them semi-transparent and click-through.
4. See a **live preview** and send a **test notification**.

Light / Dark / System theme like every other page (theme resources only).

## 2. How Windows toasts work (findings from reading the open-source TopNotify project)

TopNotify (github.com/SamsidParty/TopNotify, C#, **GPL-3.0**) already moves the toast. What it does, and what it does not do:

- A toast is a normal top-level window of class **`Windows.UI.Core.CoreWindow`**, titled "New notification" (the title is **localized**; TopNotify ships a language→title CSV and falls back to size matching: the toast window is about **396 px wide at 100% scale**, flush against the right edge of the monitor).
- Plain `EnumWindows` does not return these windows. TopNotify's small native DLL calls the undocumented `NtUserBuildHwndList` (from `win32u.dll`) to enumerate "immersive" windows, with a `FindWindowEx` fallback.
- It then calls **`SetWindowPos(hwnd, ..., x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW)`** from a loop that runs every ~10 ms (and re-discovers the window about every 500 ms). Position is computed from the monitor rect plus the window size (corner presets) or from a stored X/Y percentage (custom).
- Opacity and click-through use `SetWindowLongPtr(GWL_EXSTYLE, WS_EX_LAYERED | WS_EX_TRANSPARENT)` plus `SetLayeredWindowAttributes`. Holding **Alt** temporarily restores normal clicking.
- The custom position is chosen by a "drag mode": a small draggable window follows the cursor and its coordinates are saved as a percentage of the monitor.
- It uses the `UserNotificationListener` API only for sounds and read-aloud (needs package identity), not for placement.
- **It contains no animation code.** The slide-in you see is Windows' own, from the right. After TopNotify repositions the window, the native slide can still play at the new location, which looks wrong for non-right-side positions.

**Licensing:** TopNotify is GPL-3.0. Do **not copy its code** into AppLock. Implement the technique independently from this spec (clean-room). Credit the project in About → Acknowledgements as inspiration. If both tools are running they fight over the same window: detect a running `TopNotify` process and show a warning InfoBar.

**Honest limits** (show in the page's help text and put in `NOTES.md`):
- This relies on undocumented behavior of the Windows shell. A Windows update can change the window class, size, or animation. The page must degrade safely (feature stops moving toasts, shows a "Not detected" status), never crash or affect other windows.
- Only classic toast banners can be moved. Notification Center and the Quick Settings flyout are not affected.
- Windows' "Do not disturb" / Focus settings still decide whether a toast appears at all.

## 3. Navigation and placement

- New card in the **Others** card registry named **Notifications** (icon `` or ``), description "Choose where notifications appear and how they animate", status text like "Top right · Slide from top" or "Off". Registry order value **60** (Brightness 10, Eye Care 20, Volume Mixer 30, Clipboard 40, Ring Light 50, **Notifications 60**), so the position doesn't depend on build order.
- Clicking the card opens the **Notifications settings page** inside the Others frame with a `BreadcrumbBar` (Others › Notifications) and back button, using `SettingsCard` / `SettingsExpander` (CommunityToolkit) like the other pages.
- Works with the main window closed to the tray. Independent of the AppLock master protection toggle.

## 4. Settings page layout

### 4.1 Master (SettingsCard)
- **Customize notification position and animation** ToggleSwitch (default off). When off, AppLock never touches notification windows and releases all hooks/threads.
- Status line: **Detected / Waiting for a notification / Not detected on this Windows build**, plus **Send test notification** button (see 6.4).
- InfoBars when relevant: TopNotify is running; unsupported Windows build; Windows Focus/Do-not-disturb is on (test notification won't show).

### 4.2 Live preview (SettingsCard)
A 16:9 mock monitor (themed) with a mock toast. It shows the chosen position and **plays the chosen animation** when the user changes a setting or presses **Replay** (enter, hold 1.5 s, exit). For multi-monitor setups it draws all monitors in their real arrangement and highlights the selected one.

### 4.3 Position (SettingsExpander, expanded)
- **Anchor grid** (3×3 selectable tiles on a mini-screen): Top-left, Top-center, Top-right, Middle-left, Center, Middle-right, Bottom-left, Bottom-center, Bottom-right, plus a **Custom** tile. Default **Bottom-right** (Windows default).
- **Edge margin** (NumberBox, 0–200 px, default 12): distance from the screen edge for anchored positions. Option **Respect taskbar** (default on): measure from the monitor work area, so the toast doesn't cover the taskbar.
- **Custom position**: button **Pick position on screen** enters pick mode (see 5.3). Stores X/Y as percentages of the monitor so it survives resolution changes; show the values and a **Reset** button.
- **Monitor** (ComboBox): *Primary* (default), *Monitor with the active window*, *Monitor with the mouse*, or a specific display (name and resolution). Handle hot-plug and DPI change.

### 4.4 Animation (SettingsExpander, expanded)
- **Enter from**: *Automatic (nearest screen edge)* (default), *Top*, *Bottom*, *Left*, *Right*, *Fade only*, *Scale + fade*, *None*. Shown as a 3×3 style arrow selector for quick choice.
- **Exit to**: *Same as enter (reverse)* (default), *Top*, *Bottom*, *Left*, *Right*, *Fade only*, *None*.
- **Distance**: *From screen edge* (default, travels from just outside the monitor edge) or *Short slide* (slide 40–200 px with a fade; slider).
- **Duration** (100–1500 ms, default 300 ms) for enter, separate value for exit (default 250 ms).
- **Easing**: *Ease out* (default), *Ease in-out*, *Linear*, *Spring (slight overshoot)*, *Bounce*.
- **Hold time** reminder text: how long the toast stays visible is controlled by Windows (Settings › Accessibility › Visual effects › "Dismiss notifications after this amount of time"); add a link that opens `ms-settings:easeofaccess-visualeffects`.
- **Reduce motion**: if Windows "Animation effects" is off (`SPI_GETCLIENTAREAANIMATION` false), default to fade/none and show a note; allow override.

### 4.5 Appearance (SettingsExpander)
- **Opacity** 20–100% (default 100%).
- **Click-through** toggle (default off) and **Hold Alt to interact** (default on when click-through is on).
- **Only for these apps** (optional, phase 2): per-app position/animation overrides keyed by the notification's source app (see 6.5).

### 4.6 Shortcuts and tray (optional)
- Hotkey **Toggle customization on/off** (unassigned by default) via the shared `HotkeyService` and capture dialog.
- Tray menu item **Notification position** with a submenu of the 9 anchors (checkable).

## 5. Engine

Create `INotificationToastHook`, `ToastWindowFinder`, `ToastPositioner`, `ToastAnimator` and a `NotificationCustomizerService` that owns them. Implement all Win32 calls with CsWin32 or P/Invoke, per-monitor DPI v2 aware (work in physical pixels).

### 5.1 Finding the toast window (event-driven, not 10 ms polling)
1. Install `SetWinEventHook` (out-of-context) for `EVENT_OBJECT_SHOW`, `EVENT_OBJECT_LOCATIONCHANGE`, `EVENT_OBJECT_HIDE`, `EVENT_OBJECT_DESTROY` and `EVENT_OBJECT_CLOAKED/UNCLOAKED` to learn about the shell's toast window as early as possible (to hide it before the first native frame is visible).
2. Identify candidates by: class `Windows.UI.Core.CoreWindow`, owning process **`ShellExperienceHost.exe`** (verify on the target builds with Spy++ / Accessibility Insights, because newer builds may use a different host process), visible, near the right edge of a monitor, and size about 396 × 120 at 100% scale (scaled by monitor DPI). Match the title against a bundled **localized "New notification" table** as an extra signal only (it must work when the language isn't in the table).
3. Keep a startup **discovery spike** (milestone 1): a diagnostics view listing every candidate window (hwnd, class, process, title, rect, styles) when a test toast is shown, so the agent can confirm the heuristics on this PC before building more.
4. Use `NtUserBuildHwndList` (via a tiny native helper or runtime `GetProcAddress`) **only** if `EnumWindows` / `FindWindowEx` fail to find immersive windows; otherwise stay with documented APIs. Cache the found HWND, but validate it again each time (`IsWindow`, class, size).

### 5.2 Positioning
- Compute the target rect from monitor bounds/work area, window size (`GetWindowRect`, physical px), anchor, margin and monitor choice. Clamp inside the monitor so the toast is never off-screen.
- Move with `SetWindowPos(SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)`. Never change size, owner, or z-order, and never steal focus.
- Re-apply when the shell moves the window (LOCATIONCHANGE event), but ignore events that AppLock itself caused (flag the move).

### 5.3 Pick position on screen (replaces TopNotify's cursor-hijacking drag mode)
Show a full-screen, semi-transparent overlay on the chosen monitor with a **ghost toast** (same size as a real one) that follows the mouse with a grid/snap guides (snap to edges and center, hold Shift to disable snapping). Click to confirm, Esc to cancel. Do **not** move or hide the real mouse cursor. Save percentages of the monitor plus the monitor id. Work with touch and keyboard (arrow keys nudge by 1 px, Shift+arrow by 10 px, Enter confirms).

### 5.4 Animation
The shell plays its own slide, so AppLock must take over the window position **from the first frame**.

**Strategy A (primary): drive the real window.**
1. On first sight of a toast (SHOW event), immediately set it fully transparent (`WS_EX_LAYERED` + `SetLayeredWindowAttributes` alpha 0) and move it to the **start pose** (off-edge position or the short-slide offset). Preserve the original extended style bits and restore them at the end; never leave a toast stuck transparent.
2. Run the animation on a **dedicated high-priority thread** paced with `DwmFlush` (or a 1 ms waitable timer plus `timeBeginPeriod`), using a `Stopwatch` for time. Each frame: compute eased progress, `SetWindowPos` to the interpolated position and `SetLayeredWindowAttributes` for the interpolated alpha (multiplied by the user's opacity). Skip frames if behind (time-based, not frame-count-based). Keep the shell's own movement suppressed by re-asserting the position every frame during the animation.
3. After the animation, hold at the rest pose, re-applying only if the shell moves it.
4. Clip while animating: when the start pose is off the monitor edge, the toast could appear on a neighboring monitor. Apply `SetWindowRgn` clipped to the monitor rect during motion and restore `NULL` afterward. If a neighbor monitor exists on that side and clipping misbehaves, fall back to **Short slide + fade** automatically.
5. Exit: detect the shell starting its dismissal (it moves the window toward the right edge, hides it, or the toast timeout elapses). On detection, run the exit pose animation, then let the window hide. If the exit can't be reliably intercepted (window is hidden before AppLock can animate it), use **Strategy B**.

**Strategy B (fallback for exit, or everything if A flickers): ghost window.** Capture the toast's pixels (Windows.Graphics.Capture `CreateForWindow`, or `DwmRegisterThumbnail`) into an AppLock-owned borderless, topmost, click-through, no-activate layered window (`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT`, excluded from Alt+Tab) positioned over the toast, and animate that ghost for the exit while the real window is already gone. The ghost must not capture itself (exclude it with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`). Never write captured pixels to disk or send them anywhere.

After the Milestone 1/3 spike, pick A, B, or a mix per observed behavior and record the decision and measured results in `NOTES.md`. Whichever strategy is used, "None" animation means position only.

### 5.5 Safety and lifecycle
- Never hold a toast hidden or transparent longer than 2 seconds beyond its expected lifetime: a watchdog restores alpha and style bits.
- Always restore the original extended style and clear the window region on service stop, setting off, settings change, sleep/resume, explorer restart, or any exception.
- If the toast window isn't found within 1.5 s of a notification, do nothing (the user simply gets the normal toast).
- Hooks and threads are created only while the master toggle is on, and torn down when it's off.
- Respect "Mute notifications"-type options in AppLock only for AppLock's own tips, not for Windows toasts.
- Handle high-DPI, multi-monitor, taskbar on any edge, auto-hidden taskbar, RTL languages (anchors are physical, not language-mirrored), and notifications on a locked or secure desktop (do nothing).

## 6. Data model, preview and test notification

### 6.1 Settings

```csharp
public class NotificationCustomSettings
{
    public bool Enabled { get; set; }
    public string Anchor { get; set; } = "BottomRight";   // TopLeft|TopCenter|TopRight|MiddleLeft|Center|MiddleRight|BottomLeft|BottomCenter|BottomRight|Custom
    public int MarginPx { get; set; } = 12;
    public bool RespectTaskbar { get; set; } = true;
    public float CustomPercentX { get; set; }
    public float CustomPercentY { get; set; }
    public string MonitorMode { get; set; } = "Primary";   // Primary|ActiveWindow|Mouse|Specific
    public string? MonitorId { get; set; }
    public string EnterFrom { get; set; } = "Auto";        // Auto|Top|Bottom|Left|Right|Fade|ScaleFade|None
    public string ExitTo { get; set; } = "Reverse";        // Reverse|Top|Bottom|Left|Right|Fade|None
    public string DistanceMode { get; set; } = "Edge";     // Edge|Short
    public int ShortDistancePx { get; set; } = 60;
    public int EnterMs { get; set; } = 300;
    public int ExitMs { get; set; } = 250;
    public string Easing { get; set; } = "EaseOut";        // EaseOut|EaseInOut|Linear|Spring|Bounce
    public int OpacityPercent { get; set; } = 100;
    public bool ClickThrough { get; set; }
    public bool AltToInteract { get; set; } = true;
    public HotkeyBinding? ToggleHotkey { get; set; }
    public List<AppNotificationOverride> AppOverrides { get; set; } = new(); // phase 2
}
```

Validate ranges on load (margin 0–200, durations 100–1500, opacity 20–100) and migrate safely. Store in the AppLock settings JSON with the same integrity protection as other sections.

### 6.2 Shared animation math
One pure, unit-tested class `ToastMotion` computes start/end poses from anchor, edge, distance mode and easing; both the live preview and the real animator use it so they match exactly. Include unit tests for each anchor/edge combination, clamping, "Auto" edge choice (the nearest edge to the anchor; center → fade+scale), and easing endpoints (0→0, 1→1).

### 6.3 Preview
Uses the same `ToastMotion` and easing, rendered with Composition animations in the mock screen. It never touches real windows.

### 6.4 Send test notification
Use `Microsoft.Windows.AppNotifications.AppNotificationManager` (Windows App SDK; works for unpackaged apps after `Register`) to post a real toast titled "AppLock test notification". Provide a **Send after 3 seconds** option so the user can switch windows first. If toasts are suppressed by Focus/Do-not-disturb, say so in an InfoBar. The test toast is also what the diagnostics spike uses.

### 6.5 Per-app overrides (phase 2, optional)
Identify the source app of a toast with `UserNotificationListener` (requires package identity and user permission; if AppLock is unpackaged, skip this feature and say why). Allow overrides of anchor, animation and opacity per app. Do this only after everything else works.

## 7. Project structure additions

```
src/AppLock/
  Views/NotificationsPage.xaml(.cs), Views/Controls/AnchorGrid, ToastPreview, EdgeArrowPicker
  Views/PickToastPositionOverlay.xaml(.cs)
  ViewModels/NotificationsViewModel.cs
  Services/Notifications/
    NotificationCustomizerService.cs   // lifecycle, settings sync, watchdog
    ToastWindowFinder.cs               // candidates, heuristics, localized-title table
    ToastWinEventHook.cs
    ToastPositioner.cs
    ToastAnimator.cs                   // Strategy A (and B ghost)
    ToastGhostWindow.cs
    ToastMotion.cs                     // pure math, shared with preview
    TestToastService.cs
    ToastDiagnostics.cs
  Models/NotificationCustomSettings.cs
  Assets/Meta/NotificationTitles.csv   // written from scratch, not copied from TopNotify
tests/AppLock.Tests/Notifications/    // ToastMotion, settings migration, candidate matching with fake windows
```

Define `IWindowApi` (find, get/set rect, set alpha, styles, region) and `IMonitorProvider` so the finder, positioner and animator are testable without a real shell.

## 8. Quality rules

- Localize all strings; keyboard accessible; screen-reader names on the anchor grid tiles ("Top left, selected"); status isn't conveyed by color alone; high-contrast friendly.
- Log discovery, strategy chosen and errors to `%APPDATA%\AppLock\logs`, with **no notification content** (titles/text are never read or logged).
- No network calls, no telemetry. AppLock does not read notification text in the core feature.
- UI thread stays free; hooks marshal to the animator thread, not the UI thread.
- Performance target: idle cost about zero when no toast is showing; during an animation ≤ 5% of one core.

## 9. Milestones (build, run, verify each before moving on)

1. **Discovery spike + page shell:** Notifications card (order 60), page with breadcrumb, master toggle, **Send test notification** and a diagnostics list showing candidate toast windows (class, process, title, rect). Report which heuristics identify the toast on this PC.
2. Position only: anchors, margin, taskbar option, monitor selection, Custom X/Y; event-driven hook (no 10 ms polling); safe restore on stop.
3. **Pick position on screen** overlay and live preview of position.
4. Enter animation (Strategy A): directions, Auto edge, distance modes, duration, easing, opacity, clipping; verify there is no first-frame flash at the old position.
5. Exit animation (A or ghost B), reverse/explicit exit, watchdog, reduce-motion handling.
6. Click-through with Alt bypass, hotkey, tray submenu, TopNotify-running warning.
7. Live preview with Replay using `ToastMotion`; theme (Light/Dark/System) and accessibility pass.
8. Tests (motion math, finder with fakes, settings migration), manual checklist, `NOTES.md` with the measured behavior on this Windows build.
9. (Optional) Per-app overrides.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 10. Manual test checklist

- [ ] The Notifications card appears in Others after Ring Light; page opens; breadcrumb/back work; Light, Dark, System themes look right
- [ ] Master toggle off: nothing is hooked, toasts behave exactly like stock Windows
- [ ] Test notification appears at each of the 9 anchors and at a custom position, on the primary and on a second monitor, at 100% and 150% DPI
- [ ] Margin and "respect taskbar" work with the taskbar at bottom, top, left, right and auto-hide
- [ ] Enter animations from top, bottom, left, right, fade, scale+fade and none look right, with no flash at the original bottom-right spot
- [ ] Exit animation works (reverse and explicit); a toast dismissed by clicking X or by timeout animates out
- [ ] Short slide mode is used automatically when a neighbor monitor would show the toast crossing into it
- [ ] Duration and easing sliders change the real animation and the preview identically
- [ ] Opacity and click-through work; holding Alt lets you click the toast
- [ ] Windows "Animation effects" off → default becomes fade/none; Do-not-disturb shows the InfoBar
- [ ] Running TopNotify alongside shows the warning
- [ ] Toggling off, closing AppLock, sleep/resume, and restarting Explorer leave no transparent or stuck toast and no leftover hooks
- [ ] No notification text appears in logs; nothing is sent over the network
