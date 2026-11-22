# Island rendering technique — milestone 1 decision (WebView2)

Milestone 1 spike: prove the rendering layer before any Island feature is built.
Implemented as `AppLock.exe --island-spike`, which opens only the island window
(`src/AppLock/Island/Host/IslandWindow.xaml(.cs)`) hosting the local Vite/Svelte bundle in
`Assets/island-ui` through the WebView2 virtual host `https://island.appassets/`.

## Decision

**Candidate 1 wins: a WinUI 3 borderless window containing the WinUI 3 `WebView2` control.**

The page background is transparent (`WebView.DefaultBackgroundColor = Colors.Transparent`,
XAML root `Background="Transparent"`), the island shape is drawn by CSS, and the window is
topmost, out of the taskbar and Alt-Tab, and never activates on hover or click.

Candidate 2 (Win32 window + `CoreWebView2CompositionController` + DirectComposition) was not
needed: per-pixel transparency, anti-aliased edges, topmost behaviour and 60 fps motion all
work in Candidate 1 on this machine. It stays the fallback if a later milestone needs something
the XAML control cannot do (for example real typing while inactive, see "Known gaps").

**Shape animation variant (b) wins**: the island body is one element whose `width`, `height`
and `border-radius` are written per frame inside `contain: layout paint style`, with all content
absolutely positioned at its final (open) size and clipped by the body. Variant (a)
(`clip-path: inset(... round r)` on a full-size layer) is kept in the spike behind the
`B · width/height` / `A · clip-path` switch and can be re-measured at any time.

## Measurements (built-in benchmark)

`AppLock.exe --island-spike --bench` runs 100 open/close cycles per variant, measures every
`requestAnimationFrame` interval, and writes `%LOCALAPPDATA%\AppLock\island\bench.txt`.
Display: 1920×1080 at 60 Hz (refresh 16.7 ms; budgets p95 ≤ 20.9 ms, max ≤ 33.4 ms).

| Run | Variant (a) clip-path | Variant (b) width/height |
|---|---|---|
| 1 | p50 16.7 · p95 16.9 · p99 17.1 · max **116.6** (FAIL) | p50 16.7 · p95 16.9 · p99 17.0 · max 33.3 (PASS) |
| 2 | p50 16.7 · p95 16.9 · p99 17.0 · max **100.0** (FAIL) | p50 16.7 · p95 16.9 · p99 17.0 · max 17.6 (PASS) |
| 3 (final build) | p50 16.7 · p95 16.9 · p99 17.0 · max 17.2 (PASS) | p50 16.7 · p95 16.9 · p99 17.0 · max 19.6 (PASS) |

Both variants hold p95 at the refresh interval, but `clip-path` produced 100 ms+ frames in two
of three runs while the size variant never exceeded 33 ms, so variant (b) is the default.
Spring: one `requestAnimationFrame` integrator (`src/spring.ts`) drives a single 0→1 scalar;
open 320 ms / 0.72 damping, close 260 ms / 0.90 damping, hover delays 150 ms / 350 ms
(`src/motion.ts`). Nothing in the layout reflows during the motion and no layout reads happen
inside animation frames.

## What the spike proved, and the two gotchas found

1. **Transparency works in Candidate 1.** The page and the window are transparent; the notch is
   the only painted thing and its anti-aliased edge is intact on a light wallpaper. No black box,
   no backdrop, no `WS_EX_LAYERED` (layering a WinUI window breaks both rendering and input).
2. **`WS_EX_NOACTIVATE` cannot be used on a WinUI window**: with it, neither the XAML input
   bridge nor the WebView2 child receives pointer messages, so hover never fires. The window is
   kept non-activating with a `WM_MOUSEACTIVATE` subclass returning `MA_NOACTIVATE` instead.
   Deviation from island-webview2.md 4.1 is deliberate and measured; it will be revisited together
   with the `input.focusRequested` flow (milestone 2/4), where activation is legitimate.
3. **The WinUI `WebView2` control only delivers pointer input while its window is active.**
   Measured with a page-side pointer counter (`mv 0` while inactive, `mv 130` after
   `SetForegroundWindow`). Since the island must react without ever taking focus, **hover is
   detected in the host**: a 33 ms `GetCursorPos` poll compares the cursor with the island
   rectangle (folded or open) plus 8 DIP hysteresis and posts `state.hover { inside }` over the
   bridge. The page keeps its DOM hover handlers for the active case; both paths call the same
   open/close delays. This also removes the "pointer did not move but the body resized" edge case
   that plagued the native version.
4. **Window size and click-through.** The HWND is kept at the open page size (436×188 CSS px,
   scaled by the monitor DPI), and the click-through area is a `SetWindowRgn` round rect updated
   only at animation boundaries. The UI sends `layout.target` (phase, x, y, w, h, radius) *before*
   an expand and *after* a collapse; the host validates the values against the two layouts it
   offers and never resizes the HWND per frame, so nothing can flicker or tear mid-motion.
5. **Topmost needs re-asserting.** Another topmost window can end up above the island, so a 1 s
   `SetWindowPos(HWND_TOPMOST, NOMOVE|NOSIZE|NOACTIVATE)` keeps the slot. It does not touch the
   visual tree.

## Milestone 2 follow-up: two visual defects fixed after review

1. **A thin rectangle around the notch.** A transparent Windows 11 window still gets the DWM 1px
   border and OS corner rounding. Both are turned off with `DwmSetWindowAttribute`
   (`DWMWA_BORDER_COLOR = 0xFFFFFFFE`, `DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND`), so only the
   notch shape itself is visible on the desktop.
2. **A dark ghost left behind after the island shrank.** Shrinking a painted element on a
   transparent WebView2 surface leaves the previously painted area on screen, because the newly
   transparent region is not repainted. The default shape variant is therefore (a) `clip-path`:
   keep the full-size 420×180 layer and clip it per frame, so every animation frame repaints the
   whole painted area and the transparent background is cleared correctly. Variant (b) stays
   available for comparison in the benchmark.

## Milestone 2: settings, host lifetime and the bridge

- `Models/IslandSettings.cs` (position, shape, theme, size, opacity, hardware acceleration,
  hotkey, clock and battery toggles) is persisted inside `settings.json` (`AppSettings.Island`).
- `IslandHost` owns one window for the whole session (never per show), feeds `state.time` and
  `state.battery`, registers the show/hide hotkey and re-applies settings live.
- `BridgeBatcher` coalesces host → UI updates to one message per module per 100 ms;
  `BridgeMessages` is the single place that decides the wire format.
- `IslandPositioner` places the page on the chosen monitor at its DPI; the page size follows the
  size setting, and the click-through region follows the folded/open rectangle.
- Setting `HardwareAcceleration` off passes `--disable-gpu` to the WebView2 process and takes
  effect on the next start (the environment is created once per process).
- The WinUI settings page (`Views/IslandSettingsPage.xaml`) is a skeleton with every setting the
  milestone needs, reachable from the Others page card.

## Host hardening already in place

Virtual host mapping to `Assets/island-ui/dist` (`DenyCors`), navigation blocked to any other
origin, `NewWindowRequested` and `PermissionRequested` denied, context menus / devtools / zoom /
browser accelerator keys / status bar / autofill disabled, own profile in
`%LOCALAPPDATA%\AppLock\island\webview-ui`, strict CSP in `index.html` (`default-src 'self'`,
no remote anything). Every message is parsed in C#: envelope `{v:1,type,payload}`, 8 KB size cap,
type whitelist, range-checked `layout.target`, and per-field validation for `perf.report`; a
malformed or unknown message is dropped and logged, never thrown.

## The grey box (fixed)

The WinUI `WebView2` control painted its default background — the dark theme colour `#202020` —
behind the transparent page, so every part of the window the canvas did not cover showed a grey
rectangle around/below the notch. Two things together remove it:

1. `WebView.DefaultBackgroundColor` is set **before the window is shown** (in the constructor) and
   re-asserted after `EnsureCoreWebView2Async`; setting it only later leaves the controller on its
   default. `WebView2` has no `Background` property and no public `CoreWebView2Controller`, so this
   control property is the only lever in this hosting mode.
2. `SetWindowRgn` follows the island shape with **no padding**: the region is what keeps the
   (still opaque) control surface off the desktop, so any padding shows as a grey rim.

The whole page is still painted by the canvas (`clearRect` + `roundRect` every frame), which keeps
stale pixels from being left behind while the shape shrinks.

## Milestone 3: the open shell

- The page is sized for the largest tab (Weather 620 × 290, Chat 600 × 440) plus the 8 px margin:
  636 × 448 DIPs, scaled by the size setting. The window keeps that size while folded; only the
  region follows the notch.
- `TAB_SIZES` in `motion.ts` is the single table of per-tab design sizes; the tab strip, the body
  rect and the host's `layout.target` all read it.
- Hover uses the last rectangle the page reported (not a size recomputed in C#), so a tab switch
  cannot desync the pointer area from the shape. It is still padded by 8 DIP of hysteresis.
- A tab switch animates the shell from the drawn size to the new tab's size with its own spring
  (`RESIZE_SPRING`), and reports the union of both rectangles as the region so the shape is never
  clipped while shrinking.
- The shell has the tab strip (icon + label for the active tab), and Quiet, Pin and Settings chips.
  Pin keeps the shell open regardless of hover-out. Content is an empty placeholder per tab; the
  modules land in milestones 5+.
- `--tab=<id>` (host) and `?tab=<id>` (page) open the shell on a given tab instead of Home.
- Frame budget on this machine (1920 × 1080, 60 Hz), 100 open/close cycles of the canvas body:
  p50 16.7 ms, p95 16.9 ms, p99 17.0 ms, max 33.3 ms — PASS (`bench.txt`).

## Milestone 4: live event engine and the first cards

- `Island/Events/LiveEventQueue.cs` is the single queue from island.md 5.2: one visible card,
  same-kind coalescing (volume dragging and two copies in a row update the card in place),
  higher priority replaces, lower waits (max 5, oldest low priority dropped), the countdown
  pauses while the pointer is on the island, and no card is shown for ~60 ms after an open/close
  motion starts (`HoldForMotion`) so the animation keeps its frame budget.
- Sources: `LockKeyCardSource` polls `GetKeyState` at 10 Hz (island.md allows polling; a second
  global keyboard hook next to AppLock's is avoided), `ClipboardCardSource` is an
  `AddClipboardFormatListener` on the island HWND that stores nothing and honours
  `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory = 0`,
  `ScreenshotCardSource` watches the screenshots folder (falling back to its parent when the
  folder does not exist yet or is deleted while the island runs) and `IslandVolumeController`
  wraps the existing NAudio `SpeakerVolumeService`.
- The wheel over the folded pill is handled in the page (`wheel` → `volume.step`), not in the
  window procedure: once WebView2 is under the pointer its child window receives `WM_MOUSEWHEEL`,
  so a window-level subclass never sees it.
- Cards render in the folded pill only (`FOLDED` → `CARD` 344 × 58 via its own spring); the region
  is grown to the card rect before the pill expands so the pointer area is correct immediately.
- Card toggles and the stay duration (2 s / 3.5 s / 6 s) live in `IslandSettings` and travel in
  `settings.snapshot.cards`; the settings page shows them in milestone 16.
- Frame budget after this milestone: 100 open/close cycles, p50 16.7 ms, p95 16.9 ms, p99 17.0 ms,
  max 33.3 ms — PASS.

## Why the shape has a mask, and what that costs during motion

WinUI 3 cannot render WebView2 with a transparent background: WebView2 is *external content*, and
"it's not possible to give a WebView2 a transparent background" (Windows App SDK visual-layer docs;
see also microsoft-ui-xaml#2992 and #6527). The control's surface is the theme colour (`#202020` in
dark), so the opaque window region (`SetWindowRgn`) is what keeps it off the desktop — and the
region is only updated at animation boundaries, because updating it per frame cost a dropped frame
(max 83.6 ms against a 33.4 ms budget, measured).

Consequence: during the ~300 ms open/close motion the region is the *target* rect while the shape
is still smaller, so a thin band of `#202020` is visible around the moving shape. Fixing that
properly needs **Candidate 2** from island-webview2.md 4.2 (Win32 `WS_EX_NOREDIRECTIONBITMAP`
window + `CoreWebView2CompositionController` + DirectComposition, forwarding input), which renders
the page with real per-pixel alpha and needs no region at all.

## Milestone 5: the Home tab

The Home tab (island.md 7) is the first screen with real content, and it is built so that none of
that content costs a frame during the open/close motion:

- **Sampled off the UI thread, only while visible.** `IslandHomeState` runs a 1 Hz
  `PeriodicTimer` on the thread pool with `GetSystemTimes`/`GlobalMemoryStatusEx` (CPU and RAM),
  `NetworkStatsProvider` (speed) and the volume level. The page tells the host when the Home tab is
  really on screen (`home.visible`, sent on open/close/tab switch/pause), and the loop is stopped
  otherwise — a folded or hidden island does nothing (island.md 4.1). Samples are dropped while a
  motion is running, so a 1 s tick can never land inside an animation.
- **Media** (`MediaSessionSource`) polls the current SMTC session once a second: title, artist,
  source app, playback state and a position extrapolated from `LastUpdatedTime`, so the progress
  bar keeps moving between 1 Hz updates (the bar animates `transform: scaleX`, not width). Album
  art is written once per track to `%LOCALAPPDATA%\AppLock\island\art` and loaded by the page from
  a second virtual host (`https://island.cache/…`); it never travels over the message bridge. The
  CSP gained `img-src … https://island.cache`. Verified with a real playing track: cover, title,
  artist, pause/play/next/previous.
- **Recently copied** keeps the last three clipboard items in memory (text preview or image size,
  source `.exe`, age). The source app is read **before** `OpenClipboard`, because opening the
  clipboard makes AppLock its owner and the real source would be lost.
- **Volume row** uses the existing speaker service: slider (`volume.set`, no card — the slider is
  its own feedback), device dropdown (`volume.device`, validated against the enumerated ids before
  `SetDefaultSpeaker`).
- **Tiles.** Area / Record screen / Screen text hide the island first (~180 ms) and then send
  `Win + Shift + S/T/R` with `SendInput`, so the island is never in the capture UI. Screen text
  checks for PowerToys first and shows a one-time hint card instead of sending a dead chord. Full
  screen captures the monitor under the cursor with a GDI `BitBlt` into a `Bitmap`, saves
  `Pictures\Screenshots\Island\Island_yyyy-MM-dd_HH-mm-ss.png`, copies it with the WinRT clipboard
  (`DataPackage.SetBitmap` + `Flush`) and raises the Screenshot card. QR code scans the same
  capture with ZXing.Net (`RGBLuminanceSource` over the BGRA copy, `QR_CODE` only) and shows the
  text with **Open** (http/https only, checked in C#) and **Copy** buttons — the card actions are
  ids from a whitelist, never a path or URL from the page.
- **Benchmark** with the Home tab rendered: 100 cycles, 2748 samples, p50 16.7 · p95 16.9 ·
p99 17.0 · max 17.8 ms → PASS (budget p95 ≤ 20.9, max ≤ 33.4).

### Defects found while verifying milestone 5

1. **Hover was lost when the pointer already rested on the notch at page load** (or after a page
   reload): the host sends `state.hover` only on a change, and that change happened before the page
   existed, so the island never opened. `ui.ready` now replays the current hover state.
2. **The hit region could disagree with what was on screen.** After a card collapsed (or a resize),
   the host could keep the card/pill rectangle while the page showed the open shell: the window
   region then clipped the shell, so clicks below the tab strip fell through, and moving the
   pointer over the (invisible) lower part of the shell counted as a hover-out and closed it. The
   page now re-asserts its resting rectangle on every settle, so the region always ends up correct.
3. **The Home volume slider collapsed the island.** `volume.set` raised a volume card, and a card
   animates the *folded* pill and tells the host a folded rectangle, which made the pointer look
   like it had left. `volume.set` no longer raises a card, and `LiveEventQueue` now takes a
   `canShow` predicate: while the open shell is on screen a card waits in the queue (the 10 Hz tick
   keeps running and drains it as soon as the shell folds).

## Milestone 6: the Clipboard and Shelf tabs

Two tabs that read the app's own stores instead of keeping a second copy of anything.

- **Clipboard** reuses `ClipboardStore` and `ClipboardPasteService`, so the island shows exactly the
  history the app's clipboard feature records: text, images and file lists, newest first. An image
  thumbnail is loaded straight from the clipboard store's folder through a second virtual host,
  `https://island.clips/…`. Search is a match in the store (debounced 150 ms in the page), a pin
  toggles the store's own flag, and the rows come back in the store's order.
- **Shelf** is a link list in `%LOCALAPPDATA%\AppLock\island\shelf.json`. Row icons are rendered
  once per path into `…\island\cache\shelf` and loaded from `https://island.cache/shelf/…`:
  pictures get a 48 px thumbnail, everything else the shell's own icon, and a path that no longer
  exists is greyed out instead of dropped. Files are picked with the host's file dialog (native
  drag-and-drop belongs to milestone 7); `ShelfCopyFiles` copies the file in instead of linking it.
- All images now live under one `island.cache` host (`art/`, `shelf/`), so album art moved from
  `…\island\art` to `…\island\cache\art`.

**One message for tab work.** `home.visible` became `tab.visible { id, visible }`: the page reports
which tab is on screen (or `''` when nothing is), and the host keys every sampler and list query off
it, so only the visible tab does any work.

**Focus handoff.** The island still never activates on a click (`WM_MOUSEACTIVATE` →
`MA_NOACTIVATE`), so a text field has to ask for the keyboard explicitly: `input.focusRequested`
remembers the current foreground window, clears the non-activation guard, activates, and focuses
the WebView2 control; `input.focusReleased` — a blur, a paste, closing the island — restores that
window. Without this the search box could not be typed into at all.

**Paste goes to the app you came from.** Clicking a row blurs the search box first (which hands
focus back), then posts `clipboard.paste`; the host pastes into whatever window is foreground at
that moment while the page folds the island. Shift+click only puts the item back on the clipboard.
The page may also only remove shelf paths the host itself just sent, so a stale render cannot
disagree with the file on disk.

### Verified

- Clipboard tab opened with 60 rows from the real store (shell 504×270); typing into the search box
  logged `focus: acquired` and then `clipboard: 0 row(s) for 'zzzzzz'` after the debounce; pinning a
  row rewrote the list with the golden pin shown, and a second click cleared it again.
- Shelf: three parked files (a picture, a text file, a path that no longer exists) rendered with a
  thumbnail, a shell icon and a greyed missing row; removing one dropped the count and rewrote
  `shelf.json`; adding a file through the picker persisted it too.
- This change was not benchmarked: the user's own AppLock instance was running and could not be
  stopped for a clean 100-cycle run.

## Known gaps (later milestones)

- Paste targets whatever window is foreground when the row is clicked; an elevated app cannot be a
  target from a non-elevated island (UIPI), and the island cannot tell whether the paste landed.
- The keyboard hooks (`Ctrl+Tab`, arrows, `Esc`) still cannot fire: the page only gets the keyboard
  after a field asks for it, and no field outside the Clipboard search has. Mouse clicks on tabs
  and chips work because the window receives them without activating.
- `WDA_EXCLUDEFROMCAPTURE`, per-monitor DPI changes, multi-monitor choice, pin/quiet, settings,
  and the Chat WebView2 profile belong to milestones 2–15.
- Hover polling costs one `GetCursorPos` per 33 ms; it is paused with the island's visibility
  rules later.
- A volume change also makes Windows show its own volume flyout next to the Island's card; Windows
  has no supported way to suppress it (follow-up: decide whether to hide the card or keep both).
- The Command tile posts `command.open`; the host only logs it until the command bar lands in
  milestone 8. The Network tile's "open Speed Meter settings" needs the main window, which the
  island-only run does not create. "Make QR from clipboard text" is not built yet.
- The region/opaque-surface limitation above (grey band during motion) until Candidate 2 lands.

## Reproduce

```
cd Assets/island-ui && npm ci && npm run build
dotnet build src/AppLock/AppLock.csproj -c Release
src/AppLock/bin/x64/Release/net8.0-windows10.0.19041.0/AppLock.exe --island-spike          # show the island
src/AppLock/bin/x64/Release/net8.0-windows10.0.19041.0/AppLock.exe --island-spike --bench  # write bench.txt
src/AppLock/bin/x64/Release/net8.0-windows10.0.19041.0/AppLock.exe --island-spike --tab=chat
```
