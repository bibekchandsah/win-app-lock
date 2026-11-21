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

## Host hardening already in place

Virtual host mapping to `Assets/island-ui/dist` (`DenyCors`), navigation blocked to any other
origin, `NewWindowRequested` and `PermissionRequested` denied, context menus / devtools / zoom /
browser accelerator keys / status bar / autofill disabled, own profile in
`%LOCALAPPDATA%\AppLock\island\webview-ui`, strict CSP in `index.html` (`default-src 'self'`,
no remote anything). Every message is parsed in C#: envelope `{v:1,type,payload}`, 8 KB size cap,
type whitelist, range-checked `layout.target`, and per-field validation for `perf.report`; a
malformed or unknown message is dropped and logged, never thrown.

## Known gaps (later milestones)

- Typing while the island has focus (`input.focusRequested` → clear `MA_NOACTIVATE`, activate,
  restore afterwards) is not implemented yet.
- `WDA_EXCLUDEFROMCAPTURE`, per-monitor DPI changes, multi-monitor choice, pin/quiet, settings,
  and the Chat WebView2 profile belong to milestones 2–15.
- Hover polling costs one `GetCursorPos` per 33 ms; it is paused with the island's visibility
  rules later.

## Reproduce

```
cd Assets/island-ui && npm ci && npm run build
dotnet build src/AppLock/AppLock.csproj -c Release
src/AppLock/bin/x64/Release/net8.0-windows10.0.19041.0/AppLock.exe --island-spike          # show the island
src/AppLock/bin/x64/Release/net8.0-windows10.0.19041.0/AppLock.exe --island-spike --bench  # write bench.txt
```
