# Island — Rendering Change: HTML UI in WebView2 (Addendum to island.md)

This addendum **changes how the Island is drawn**. Read `island.md` first. Everything in `island.md` about **features, behavior, settings, data, cards, tabs, tiles, privacy, and tests stays exactly as written**. Only the **rendering layer** changes: the Island's surface becomes an **HTML/CSS/TypeScript UI hosted in WebView2**, driven by the existing C# services. The rest of AppLock (Others page, the Island settings page, all services) stays WinUI 3.

## 1. Why this change, and why not Electron

The user finds the WinUI/Composition version not smooth enough. Browsers' compositors are very good at exactly this kind of UI (small, GPU-composited, animated shapes and cards), and web tooling makes spring-style animation and rapid visual tuning easy. Options compared:

| Option | Smoothness | Integration with the WinUI 3 project | Cost | Verdict |
|---|---|---|---|---|
| **WinUI 3 + Composition (island.md)** | Can be smooth, but easy to get wrong (layout during animation, HWND resizing, transparency tricks) | Native | Lowest memory | Keep only as fallback |
| **WebView2 (Chromium) hosted by the C# app** | Same Chromium engine as Electron, GPU composited | **In the same process tree and project.** C# ↔ JS via messages. Uses the Edge WebView2 runtime that Windows 11 already includes (and the Chat tab already needs it) | One extra browser process group, about 60–120 MB | **Recommended** |
| **Electron** | Same engine, so **no smoothness gain over WebView2** | A **separate app**: bundles its own Chromium + Node (+100–200 MB disk, 150–300 MB RAM), separate process lifecycle, separate updater/signing, IPC to AppLock. It cannot be embedded into WinUI 3 | Highest | Not recommended; last-resort fallback only (section 9) |

So: **use WebView2.** It gives the web-style rendering with none of Electron's extra runtime, and it stays one project.

## 2. What changes and what stays

**Stays (C#, unchanged):** all services and modules from `island.md` (media/SMTC, clipboard source, notifications, VPN, mic/cam detection, weather provider, reminders/timer, file index, command search, chord sender, QR scanner, controls, launcher/shelf/notes storage, hotkeys, visibility rules, positioner, settings model, Others card, settings page).

**Changes (replaces these parts of `island.md`):**
- Section 3 (window architecture) → section 4 below.
- Section 4.2 (Composition animation rules) and 4.4 → section 6 below. The **targets in 4.1 and the benchmark idea in 4.3 still apply** (measured in the browser now).
- Section 14 (project structure) → section 8 below.
- Section 18 (milestones) → section 10 below.
- Replace XAML views (`FoldedView.xaml`, `OpenShell.xaml`, tabs, cards, command bar) with web components. The **WinUI settings page for the Island is not replaced**; it pushes settings to the web UI over the bridge.
- The "Lite mode" setting becomes the web-style **"Hardware acceleration" switch** plus Lite mode (no shadows/transparency/springs); turning off hardware acceleration passes `--disable-gpu` to WebView2 and needs the Island to restart.

## 3. Architecture

```
AppLock (WinUI 3, C#)
 ├─ Others card + Island settings page (WinUI, unchanged)
 ├─ Island services (C#, unchanged)  ── events/state ──►  IslandBridge  ◄── commands ──┐
 └─ IslandHost (C#): window, WebView2, positioning, visibility, input/focus              │
        └─ WebView2 (Chromium) ── loads local bundle ── Island UI (TypeScript/CSS)  ────┘
```

- **IslandHost** owns the native window, WebView2, positioning/DPI, visibility rules, global hotkeys, focus handling, and screenshot exclusion.
- **Island UI** is a static web app (HTML/CSS/TS) loaded from local files through a **virtual host mapping** (`https://island.appassets/` → the `Assets/island-ui/dist` folder). It renders the folded pill, live cards, tabs, command bar, and animations.
- **IslandBridge** is the only way they talk: JSON messages over `CoreWebView2.PostWebMessageAsJson` (host → UI) and `window.chrome.webview.postMessage` (UI → host).

## 4. Window and WebView2 hosting

### 4.1 Requirements
- Top-level borderless window: `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, per-monitor DPI aware v2, top-center of the chosen monitor, excluded from screenshots by default (`WDA_EXCLUDEFROMCAPTURE`).
- **Per-pixel transparency** (the page background is fully transparent; the island shape is drawn by CSS). No visible rectangle or black box, anti-aliased shape edges.
- Does not steal focus on hover; typing works when the user clicks a field (section 4.4).

### 4.2 Hosting technique (decide in the spike, document in `docs/island-technique.md`)
- **Candidate 1 (try first):** a WinUI 3 window with a transparent backdrop containing the WinUI 3 `WebView2` control with `DefaultBackgroundColor` = transparent. Simplest, stays inside the WinUI project.
- **Candidate 2:** a Win32 window with **WebView2 visual hosting** (`CoreWebView2CompositionController` + a DirectComposition visual, `WS_EX_NOREDIRECTIONBITMAP`). More work (you forward mouse, pointer, and keyboard input to the controller yourself) but the most reliable per-pixel transparency.
- The winner must pass: transparent background, anti-aliased edges, topmost, no focus steal, correct DPI, 60 fps animation on a mid-range laptop, no flicker on resize/move.

### 4.3 Window size and click-through
- The page never relies on the transparent area being clickable. Keep the HWND **exactly the size of the visible island plus a small margin (about 8 DIPs)**, and update the window size and a **hit-test region** (`SetWindowRgn` with a rounded rectangle slightly larger than the island, so the CSS anti-aliased edge is never cut) at animation boundaries:
  - **before** an expand animation starts: grow the window to the **target** size;
  - **after** a collapse animation ends: shrink it to the folded size.
- The UI tells the host the target rectangle with a `layout.target` message (see 5) *before* it starts animating.
- Never resize the HWND every frame.

### 4.4 Focus and typing
- Default non-activating. When the UI sends `input.focusRequested` (user clicked a text field or WebView2 needs keyboard), the host clears `WS_EX_NOACTIVATE`, remembers the previous foreground window, activates the island, and calls `MoveFocus`. On `input.focusReleased`, Esc, or close, it restores `WS_EX_NOACTIVATE` and the previous focus. Test with another app focused (typing must not break that app afterwards).

### 4.5 Hardening the embedded browser
- Local content only for the Island UI: strict CSP (`default-src 'self'`; no remote scripts, fonts, or images except `data:` and the local icon/art cache), block navigation away from the virtual host, deny all permission requests, disable the browser context menu, zoom, devtools (devtools enabled only in Debug builds), autofill, status bar, and browser accelerator keys.
- Do not expose COM host objects (`AddHostObjectToScript`); use only the message bridge.
- **Validate every message from the UI** in C# (known type, size limit, schema), because it is a trust boundary (never execute a path/command from the UI without checking it against what the host offered, e.g. only launch shortcuts by **id**).
- Own user-data folder: `%LOCALAPPDATA%\AppLock\island\webview-ui`. The **Chat tab uses a second, separate WebView2** and profile (`...\webview-chat`) positioned over the Chat area by the host; third-party AI sites cannot be put in an iframe (frame-blocking headers) and must not share a process/profile with the Island UI.
- If the WebView2 runtime is missing: the Island stays off, and AppLock shows a clear message with the official Evergreen installer link; never download or install it silently.

## 5. The bridge (host ⇄ UI)

Transport-agnostic interface `IBridgeTransport` (WebView2 messages now; a loopback socket could be used later, section 9). Message envelope:

```json
{ "v": 1, "type": "state.media", "id": "optional-correlation-id", "payload": { } }
```

- **Host → UI (state/events):** `settings.snapshot` and `settings.changed`, `state.time/battery/media/clipboard/weather/volume/network/cpuram/vpn/miccam`, `event.card` (a live card to show), `list.shortcuts/shelf/notes/reminders/results` (command bar), `theme.changed`, `display.changed` (monitor, DPI, scale), `visibility.request` (show/hide/pin), `anim.pause`/`anim.resume`.
- **UI → Host (commands):** `layout.target` (rect + duration, sent before an expand/collapse), `hover.state`, `input.focusRequested/Released`, `media.play|pause|next|prev|seek`, `volume.set`, `tile.run` (by tile id), `shortcut.run|add|remove|reorder` (by id), `clipboard.paste|copy|pin|clear`, `note.save`, `reminder.add|done|snooze`, `timer.start|stop`, `search.query`, `controls.toggle` (Wi-Fi, Bluetooth, theme, lock, sleep, restart, shutdown), `chat.show|hide|navigate`, `window.drag`, `perf.report`.
- **Batching:** host coalesces updates to **at most one message per module per 100 ms**; high-frequency data (volume slider drag, audio peak for the equalizer) uses a dedicated lightweight channel capped at 30 Hz.
- **During motion, defer non-visual updates:** while an open/close animation is running, the host holds module updates (except the card that caused it) and sends them right after, so the UI thread is free to animate. This is a key part of staying smooth.
- **First state snapshot at startup**, then deltas. The UI must render from the snapshot only (no waiting on slow providers; show placeholders).
- Images (album art, thumbnails, icons) are served from a local cache through the virtual host (`https://island.appassets/cache/<hash>.png`) already sized for display, not sent as base64 over the bridge.

## 6. Making it smooth (web rules, hard acceptance criteria)

Targets are unchanged from `island.md` 4.1: **p95 frame interval ≤ 1.25× the refresh interval, no frame > 2×, first open ≤ 100 ms to first motion, idle < 1% CPU of one core, Island UI memory < 150 MB without the Chat tab.**

1. **Never animate layout properties of many elements.** Animate **`transform` and `opacity`** for everything except the single island body shape.
2. **The island body shape** (width, height, bottom corner radius, notch ears) is one element. Prove in the spike which of these is smoothest in the installed WebView2 version and use that, measured with the DevTools Performance panel (confirm the animation is on the compositor, no layout/paint per frame):
   - **(a)** animate `clip-path: inset(… round r)` on a full-size layer (preferred if composited),
   - **(b)** animate the body's width/height/border-radius with `contain: layout paint style` and with **all content absolutely positioned at its final fixed size and clipped by the body** (content never reflows during the motion).
   - Notch "ears" are separate small elements moved with `transform`.
3. **Spring physics, interruptible.** Drive the shape with a tiny **requestAnimationFrame spring integrator** (a few lines, critically tunable damping/stiffness, carries velocity so it can retarget mid-motion with no jump). Content (cards, tab panels, tiles) uses WAAPI/CSS `transform` + `opacity` with the staggers from `island.md` 4.2. All timing constants live in one file `motion.ts`, starting from the table in `island.md` 4.2.
4. **JS frame budget:** ≤ 4 ms of script per frame during animations. No `JSON.parse` of large payloads, no DOM creation, no image decoding, and no layout reads (`offsetWidth`, `getBoundingClientRect`) inside animation frames. Pre-measure sizes before the animation starts.
5. **Fixed sizes per tab** (table in `island.md` 6). Tab content is rendered at its final size before it is revealed.
6. **Pre-warm:** create WebView2 and load the UI at startup on idle priority, keep it **alive while the island is hidden** (never destroy/recreate per show), pre-render the Home tab and card templates, run an invisible warm-up animation. Cold-creating the WebView2 environment is slow (hundreds of ms), so it must not happen on first hover.
7. **Stable text:** tabular-nums for clock/timer (`font-variant-numeric: tabular-nums`), fixed-width clock slot, system font (Segoe UI Variable) to avoid font loading flashes. No web fonts from the network.
8. **No expensive effects:** no `backdrop-filter`, no large blurs/box-shadows animating, no filters on the body; use a solid color with optional alpha. At most one static shadow (disabled in Lite mode).
9. **Images:** decode ahead (`img.decode()`) before showing; fixed size boxes so nothing shifts when they load.
10. **Equalizer:** CSS transform animation, or driven by the 30 Hz peak channel; stops when hidden.
11. **Reduced motion:** honor `prefers-reduced-motion` and the Windows animation setting (the host forwards it): replace springs with 80 ms fades.
12. **Background throttling:** when the island is hidden, tell the UI (`anim.pause`) to stop all timers and observers; WebView2 can be put in low-power mode (`MemoryUsageTargetLevel = Low`) while hidden, restored before the show animation starts.
13. **Disable GPU raster only on request:** default hardware acceleration ON; the setting exists as an escape hatch.

### 6.1 Measurement
- In the UI, a **frame monitor** (`requestAnimationFrame` delta + `PerformanceObserver` for `long-animation-frame` / `longtask`) shows current/avg/p95/max in a hidden developer overlay, and a **"Run animation benchmark"** button performs 200 open/close cycles and 200 card pops and reports p50/p95/p99/max and the pass/fail verdict against the targets. The UI sends the report to the host (`perf.report`), which writes `%LOCALAPPDATA%\AppLock\island\bench.txt`.
- Every milestone report from milestone 3 on includes the p95 and max numbers.

## 7. Frontend stack

- **TypeScript + Vite**, with **Svelte** (compiles away, no virtual DOM, best fit for small animated UI) or Preact if the agent prefers; **no UI component libraries, no CSS frameworks, no CDN or network at runtime.** Vanilla CSS with custom properties for theme/colors.
- **Icons:** inline SVG from Microsoft's Fluent System Icons (MIT license, attribute in credits) or the Segoe Fluent Icons font that ships with Windows 11 (check availability on Windows 10 and fall back to SVG).
- **Build:** `npm run build` outputs `Assets/island-ui/dist`, which is copied into the app output. Node is needed only at development/build time; end users need nothing. Commit the `package-lock.json`; pin dependency versions; no postinstall scripts.
- **Code layout:** one component per tab/card, a small store (`state.ts`) fed only by the bridge, `bridge.ts` (typed messages matching section 5), `motion.ts`, `spring.ts`, `perf.ts`. Keep the UI **stateless about business logic**: it renders state and sends commands; C# remains the source of truth.
- **Theming:** the UI gets theme/colors/size/transparency/shape from `settings.snapshot` and reacts live to `settings.changed`; island colors, text-contrast flip, scale (`Island size`), and notch/capsule shape implemented in CSS variables.
- **Accessibility:** real buttons/inputs, ARIA labels, focus rings, `aria-live` for live cards, arrow-key/`Ctrl+Tab` navigation, high-contrast media query (`forced-colors`).
- Localization: strings come from the host's localization resources through `settings.snapshot.strings`, so there is a single source of translations.

## 8. Project structure (replaces island.md section 14)

```
AppLock/
  Island/
    Host/IslandWindow.cs, IslandHost.cs, IslandPositioner.cs, VisibilityController.cs, EdgeDwellWatcher.cs
    Host/WebViewHost.cs, ChatWebViewHost.cs, HitTestRegion.cs, FocusManager.cs
    Bridge/IBridgeTransport.cs, WebView2Transport.cs, BridgeMessages.cs (typed + schema validation), BridgeBatcher.cs
    Events/LiveEventQueue.cs, EventSources/...          (unchanged from island.md)
    Modules/...                                         (unchanged from island.md)
  Models/IslandSettings.cs                              (unchanged; add HardwareAcceleration)
  Views/IslandSettingsPage.xaml(.cs)                    (WinUI, unchanged)
  Assets/island-ui/                                     (source: Vite project)
    src/App.svelte, bridge.ts, state.ts, motion.ts, spring.ts, perf.ts
    src/components/ Folded.svelte, OpenShell.svelte, Tabs.svelte, cards/*.svelte, tabs/*.svelte, CommandBar.svelte, Tile.svelte
    dist/                                               (build output, loaded through the virtual host)
```

## 9. Fallback ladder (only if the spike fails)

1. Candidate 1 fails → Candidate 2 (visual hosting).
2. WebView2 cannot reach the smoothness targets → **say so with benchmark numbers** and keep the native WinUI/Composition renderer from `island.md` (the bridge-less services are reusable).
3. **Electron (last resort, requires the user's approval):** the same `dist` UI and the same bridge messages are reused unchanged by running the UI in a small Electron shell (a separate exe shipped beside AppLock, launched and supervised by AppLock, `IBridgeTransport` implemented over a named pipe or a loopback WebSocket secured with a per-launch random token, bound to 127.0.0.1, validated like WebView2 messages). This is the reason the UI and bridge are transport-agnostic. Costs: bundled Chromium + Node, higher memory, separate updates and code signing, and it cannot live inside the WinUI process.

## 10. Milestones (replace island.md section 18; milestones 4–16 there keep their scope)

1. **Spike:** transparent topmost non-activating window hosting WebView2 (try Candidate 1, then 2), a hard-coded notch shape morphing between folded and open with the rAF spring and the clip-path/size variants from section 6.2, hover open/close, DPI, hit-test region updates, frame monitor with numbers for each variant, and `docs/island-technique.md` with the decision. **Do not build features until the spike meets the smoothness targets.**
2. **Foundation:** `IslandSettings` (+ `HardwareAcceleration`), Others card + settings page skeleton, `IslandHost`/`WebViewHost`, bridge + typed messages + schema validation + batcher, positioner, Vite project with Svelte, folded island (time, battery), shape/theme/colors/size/transparency/position live from settings, toggle hotkey.
3. **Open/close shell + tabs:** hover logic with delays and hysteresis, interruptible spring, fixed tab sizes with empty content, tab-switch animation, keyboard navigation, pin/quiet/settings buttons, **benchmark button and overlay**. Report p95/max.
4. **Live event engine + first cards** (Caps Lock, Volume, Copied/Image copied, Screenshot saved) with the "defer non-visual updates during motion" rule.
5. **Home tab** (player, recently copied, volume row, the 8 tiles with the requested chords, Network speed and CPU & RAM tiles, Full screen, QR).
6. Clipboard + Shelf tabs.
7. Launcher + add dialog + drag-and-drop (native drop handling in the host, forwarding to the UI).
8. Command bar + file index.
9. Note tab, timer, reminders.
10. Weather tab.
11. Controls tab.
12. Notifications, VPN, mic/cam dots, battery cards.
13. Chat tab (second WebView2).
14. Text tab.
15. Visibility rules (fullscreen, auto-hide, chosen apps, edge dwell, multi-monitor).
16. Settings completion, polish, localization, credits (Fluent icons, ZXing.Net, MET Norway, etc.), all tests and the manual checklist, final benchmark.

After each milestone: build the C# project and the UI bundle (`npm run build`), **launch the app and show the Island**, report in 5 lines or fewer (include frame p95/max from milestone 3), commit, then **stop and wait for me to reply "continue"**. If smoothness regresses, fix it before adding anything new.

## 11. Extra tests

- Bridge: malformed, oversized, unknown-type, and wrong-schema messages are rejected without exceptions; UI cannot launch anything by raw path (only by shortcut id).
- Navigation lock: the Island WebView2 cannot navigate to an external URL; permission requests are denied.
- Transparency: no background rectangle on light and dark wallpapers; edges anti-aliased at 100/125/150/200% DPI and on two monitors.
- Focus: typing in Note/command bar works, and the previously focused app keeps focus afterwards.
- Hidden state: no timers/animation frames running in the UI while the Island is hidden (CPU near zero); memory stays within target after 1 hour.
- Missing WebView2 runtime handled with the message, no crash.
