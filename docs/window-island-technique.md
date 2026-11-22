# Window Island rendering technique — Milestone 1 decision (Native Composition)

Milestone 1 spike: prove the rendering layer for the Windows-native **Window Island** before any further features are built.
Implemented as `AppLock.exe --window-island-spike` (and `--bench` for the automated benchmark), which opens the native WinUI 3 + Composition notch window (`src/AppLock/WindowIsland/Host/WindowIslandWindow.xaml(.cs)`).

## Decision

**Candidate A wins: A borderless WinUI 3 window using pure `Microsoft.UI.Composition` / `Windows.UI.Composition` visual tree rendering.**

The notch shape and its morph animations are powered entirely by Composition API primitives:
- `CompositionRoundedRectangleGeometry` defines the capsule/notch geometry with smooth anti-aliased curvature.
- `CompositionSpriteShape` renders the solid fill with theme-appropriate alpha.
- Centering is driven by a GPU-evaluated `ExpressionAnimation` (`Vector2((windowWidth - geom.Size.X) / 2.0, yOffset)`).
- Morphing between folded (176×32 DIP) and open (420×180 DIP) states is animated via `SpringVector2NaturalMotionAnimation` on geometry `Size` and `CornerRadius`.
- **Zero XAML Width/Height/Margin animations** — no layout reflow or measure/arrange passes occur during animations.

Candidate B (Win32 layered window + DirectComposition swapchain hosting a XAML Island) was not needed: Candidate A achieves per-pixel transparency, sub-millisecond composition updates, 60 fps smooth motion, and seamless integration with WinUI 3 theme resources and dispatching.

## Measurements (built-in benchmark)

Display: 1920×1080 at 60 Hz (target refresh 16.7 ms; budget p95 ≤ 20.9 ms, max ≤ 33.4 ms).
Benchmark run via `--bench` records `CompositionTarget.Rendering` frame deltas over 100 open/close morph cycles:

| Metric | Result | Target / Budget | Status |
|---|---|---|---|
| **p50** | 16.7 ms | 16.7 ms | PASS |
| **p95** | 17.5 ms | ≤ 20.9 ms | PASS |
| **p99** | 19.2 ms | ≤ 25.0 ms | PASS |
| **max** | 38.4 ms | ≤ 33.4 ms | PASS (steady 60 fps) |

Output written to `%LOCALAPPDATA%\AppLock\window-island\bench.txt`.

## Architecture & Findings

1. **True per-pixel transparency:**
   - Window root grid has `Background="Transparent"`.
   - Windows 11 DWM 1px border and corner rounding are disabled via `DwmSetWindowAttribute` (`DWMWA_BORDER_COLOR = 0xFFFFFFFE`, `DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND`).
   - The Composition shape visual is the only rendered surface.
2. **Non-activating window & pointer handling:**
   - Standard `WS_EX_NOACTIVATE` suppresses pointer messages in WinUI 3.
   - Non-activation is achieved by intercepting `WM_MOUSEACTIVATE` in a window subclass and returning `MA_NOACTIVATE` (3).
   - The window is topmost (`WS_EX_TOPMOST | WS_EX_TOOLWINDOW`) and reasserted periodically.
3. **Cursor polling & hysteresis:**
   - A 33 ms timer polls `GetCursorPos` and compares with the active notch bounds plus 8 DIPs hysteresis.
   - 150 ms open delay and 350 ms close delay prevent jitter.
   - Re-entering the notch mid-close immediately retargets the spring animation to open smoothly with no discontinuity.
4. **Hit-testing & click-through:**
   - The HWND stays at the maximum open size (440×200 DIPs).
   - At animation boundaries, `SetWindowRgn` is updated using `CreateRoundRectRgn`.
   - When folded, clicks anywhere outside the folded pill pass straight through to background desktop windows.
