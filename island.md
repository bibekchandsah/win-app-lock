# Island — Add-on Spec for AppLock (Others page)

This is an add-on to `instructions.md`. Read that file first. Implement on top of the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure (CommunityToolkit.Mvvm), the shared services (HotkeyService, IdleService, shared custom toast service, elevation helper, protected settings JSON with DPAPI/HMAC, admin password / Windows Hello verification), and the same Light/Dark/System theme rules (theme resources only, no hard-coded colors in AppLock's own pages). Build, run, and verify before reporting.

Related specs this feature **reuses** (read them if present, do not duplicate their logic): `clipboard.md` (clipboard history), `volume-mixer.md` (audio endpoint/session volume), `speed-meter.md` (`NetworkStatsProvider`, CPU/RAM sampling), `brightness.md` (brightness service), `notification.md`, `ringlight.md`, `pc-checker.md`.

The reference screenshots show a "Dynamic Island for Windows" style app. They are a **functional reference only**. Do not copy any of its code, icons, artwork, text strings, or name; draw icons from Segoe Fluent Icons and write all copy yourself.

## 1. What this feature is

**Island** is a small bar pinned to the top-center of the screen (a notch or capsule shape) that:

- shows the clock, battery, temperature, and a mini media player while folded,
- **pops out live cards** for events (Caps Lock, volume, copied text/image, screenshots, reminders, timer, VPN, mic/camera use, Windows notifications, rain alerts),
- **expands on hover** into a panel with tabs: **Home, Clipboard, Shelf, Text, Chat, Note, Weather, Controls**,
- has a **command bar** (Alt+Space) and a **launcher** for apps, links, folders, and files.

**The most important requirement is smoothness.** Every open, close, card, and tab change must feel instant and fluid, with no stutter, no flicker, and no layout jumping. Section 4 is a hard requirement with measurable targets, not a polish item. If a feature cannot be made smooth, simplify the feature, not the animation.

## 2. Entry point (Others page)

- Add an **"Island"** card to the **Others** page (icon Segoe Fluent `` or similar; subtitle *"A dynamic bar at the top of your screen"*), registry order **90** (after PC Checker 80).
- The card has an **on/off ToggleSwitch**. Clicking the card (not the toggle) opens the **Island settings page** (section 12), same pattern as the other Others cards.
- Enabling Island also starts it with AppLock; show the existing "Start with Windows" hint if that is off.
- The Island must never lock itself, appear in AppLock's lockable-process list, or interfere with the AppLock lock overlay (the overlay is always above it; the Island hides while an AppLock overlay is visible).

## 3. Window architecture

### 3.1 The Island window
- A separate top-level window created by the Island service (not part of the main AppLock window): borderless, no caption, `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` (no taskbar button, not in Alt-Tab, does not steal focus), per-monitor DPI aware v2, positioned top-center of the chosen monitor.
- **Typing support:** the window is non-activating by default. When the user clicks into a text field (Note, command bar, search boxes, Chat), temporarily make it activatable (clear `WS_EX_NOACTIVATE`, call `SetForegroundWindow`), remember the previously focused window, and **restore focus to it** when the Island closes or the field loses focus. Test with a game/other app focused so typing in the Island does not break the other app's focus afterwards.
- Excluded from screenshots/recordings by default (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`); setting "Show the island in screenshots and recordings" turns that off.
- Rounded/notch shape with **real anti-aliased edges and per-pixel transparency** (no jagged `SetWindowRgn` edges).

### 3.2 Discovery spike (Milestone 1, do this before anything else)
Prove the rendering technique and pick one; document the decision in `docs/island-technique.md`:

- **Candidate A:** WinUI 3 window with a transparent backdrop (e.g. `TransparentTintBackdrop` if available in the Windows App SDK version in use, or an equivalent transparent system backdrop), drawing the island with Composition visuals.
- **Candidate B:** a plain Win32 layered/DirectComposition window hosting a XAML Island or Composition visual tree, with `WS_EX_NOREDIRECTIONBITMAP`.
- Requirements the winner must meet: per-pixel alpha with smooth edges, topmost, non-activating, DPI-correct, 60 fps while animating on a mid-range laptop, input only where the island visually is (see 3.3), no flicker when the window is resized or moved.
- The spike must include a prototype notch shape morphing between folded and open states with a spring animation and a frame-time readout.

### 3.3 Window size and click-through
- Animation runs **inside the window** on the compositor, not by resizing the HWND every frame.
- Change the HWND size only at animation boundaries: **enlarge to the target size before an open/card animation starts; shrink to the folded size after a close animation finishes.** The window is always exactly the island's size at rest, so there are no invisible regions that eat clicks. During the short animation the transparent margin may briefly intercept clicks, which is acceptable.
- Re-position on: monitor/DPI change, resolution change, taskbar position or auto-hide change (`SHAppBarMessage ABM_GETTASKBARPOS`), `WM_DISPLAYCHANGE`, and settings changes.

### 3.4 Shapes
- **Notch:** flush to the top edge of the screen, flat top, rounded bottom corners, with small concave "ears" at the top-left/top-right where it meets the screen edge (the look in the reference). Build it as a body (rounded bottom corners, radius animated) plus two ear visuals that track the body's edges via expression animations.
- **Capsule:** floating pill, fully rounded, with a small gap (about 6 DIPs) from the top edge.
- Shape, color, size, and transparency changes apply live with a short crossfade (no restart).

## 4. Smoothness requirements (hard acceptance criteria)

### 4.1 Targets (measure on a mid-range laptop and a 120/144 Hz monitor if available)
- Open and close animations run at the display refresh rate; **p95 frame interval ≤ 1.25× the refresh interval, no frame > 2× it**, over 200 scripted open/close cycles.
- **First open after hover ≤ 100 ms to first visible motion** (pre-warmed, see 4.3).
- **Idle cost (folded):** < 1% of one CPU core, no per-frame work when nothing animates, clock updates at 1 Hz (only if seconds are shown, otherwise once per minute aligned to the minute).
- **Memory:** < 150 MB working set without the Chat tab loaded; Chat/WebView2 is created lazily and released after a configurable idle time (default 5 minutes closed).
- No animation may ever block on I/O, network, disk, or WMI. UI thread work per frame must stay tiny.

### 4.2 Rules for the animation code
1. **Animate with the Composition API on the compositor thread** (implicit animations, `SpringVector3NaturalMotionAnimation`, `ScalarKeyFrameAnimation`, expression animations, `CompositionGeometricClip` / rounded-rectangle geometry for the body). **Never animate XAML `Width`/`Height`/`Margin`** (they trigger layout every frame). Use `Visual` Offset/Scale/Opacity and clip geometry only.
2. **Fixed layouts per state:** each tab has a fixed design size (section 6). Content is laid out once at its final size; the clip and transforms reveal it. No layout pass during an animation.
3. **Springs for shape, easing for content.** All timing values live in one static `IslandMotion` class so they can be tuned in one place. Starting values (tune by eye, keep them in this one file):

| Motion | Type | Starting values |
|---|---|---|
| Open (folded → tab) | Spring | damping 0.72, period 320 ms |
| Close (tab → folded) | Spring | damping 0.90, period 260 ms |
| Live card in | Spring | damping 0.70, period 300 ms |
| Live card out | Ease-in-out | 220 ms |
| Content fade in | Ease-out | 140 ms, delayed 80 ms after shape starts |
| Content fade out | Ease-in | 90 ms, starts immediately |
| Stagger of child tiles | Ease-out | 18 ms per item, max 8 items |
| Tab switch | Crossfade + 8 DIP slide | 160 ms; the island size springs to the new tab size |
| Auto-hide / show | Ease-in / spring | 200 ms / 280 ms, Y offset only |
| Equalizer bars | Scalar keyframes | looping, or driven by audio peak (4.4) |

4. **Interruptible:** if the pointer leaves mid-open, or a new card arrives mid-close, the animation **retargets from the current position and velocity** (update the spring's `FinalValue`; never restart from the beginning, never jump).
5. **No double rendering:** do not use acrylic/blur on the island body (expensive and flickers); use a solid color with optional alpha (section 12). Shadows are optional and must be a single cached composition shadow; disabled in Lite mode.
6. **Stable text:** use tabular (fixed-width) digits for the clock and timer, and give the clock a fixed-width slot so the pill never changes width when digits change. Pre-measure text for cards and set the card size before animating; do not let text reflow while the card is moving.
7. **Pre-warm:** at startup (after a short delay, idle priority) instantiate the folded view, the Home tab, and the card templates, create composition resources, and run a hidden zero-opacity warm-up of the open animation so the first real open does not JIT, load, or allocate.
8. **Lazy but ahead of time:** other tabs are created on first use, but when the user hovers the island (during the open delay) start creating the last-used tab in the background so it is ready by the time the animation ends. Keep created tabs alive (collapsed, `Visibility.Collapsed`, no data timers running while hidden). Exception: Chat/WebView2 (see 4.1).
9. **Data off the UI thread:** media, weather, clipboard, notification, network, file index, and battery providers run on background threads and push **batched** updates (coalesce to at most one UI update per 100 ms per module) through a dispatcher queue. Images (album art, thumbnails, icons) are decoded off-thread at display size and cached.
10. **Respect reduced motion:** if `UISettings.AnimationsEnabled` is false, replace springs with 80 ms fades and skip staggers and equalizer motion.
11. **Lite mode** (replaces the reference's "hardware acceleration" switch, because WinUI is always GPU composited): setting *"Lite mode — fewer effects"*: no shadow, no transparency, no springs (simple 120 ms fades), 30 fps cap for the equalizer, clock only once per minute. Suggest it automatically if the frame-time monitor sees repeated dropped frames (an `InfoBar` in settings, never a popup).

### 4.3 Frame-time harness
- Add a hidden developer option (settings page, press a documented key chord to reveal "Developer") with a **frame-stats overlay** using `CompositionTarget.Rendering` (or the best available timing source) that shows the current/avg/p95/max frame interval, and a **"Run animation benchmark"** button that performs 200 open/close cycles and 200 card pops, then writes a report (`%LOCALAPPDATA%\AppLock\island\bench.txt`) with p50/p95/p99/max and the verdict against 4.1.
- Run it in every milestone from 3 onwards and include the p95 number in the milestone report.

### 4.4 Equalizer bars
Use the default render endpoint peak meter (`IAudioMeterInformation`) sampled at 30 Hz **only while** the folded island is visible and media is playing; fall back to a simple looping animation if unavailable. Stop sampling when hidden or paused.

## 5. Folded island and live events

### 5.1 Folded content (each part has a setting, section 12)
- **Time** (24h / 12h / Auto, optional seconds) and **battery** (icon + %, only on devices with a battery; charging shown with the bolt icon), **temperature + weather icon** (when Weather is on and nothing else is happening), and when media is playing: **album art thumbnail on the left and a small equalizer on the right** (as in the reference).
- Priority of what the folded pill shows: live card > media mini + time > time/battery/temperature.
- Hover opens after the **open delay** (default 150 ms); leaving closes after the **close delay** (default 350 ms). Click on the folded island opens immediately. A pointer hysteresis margin (6–8 DIPs) prevents flicker at the edge.
- **Mouse wheel over the folded island** changes the system volume (see Volume card). Right-click opens a small menu: **Pin open**, **Hide for 1 hour**, **Settings**, **Exit Island**.
- Do **not** close while: a text field has focus, a flyout/dialog is open, an item is being dragged over it, a recording/dictation/timer-edit is active, or it is pinned.

### 5.2 Live cards
A card is a temporary expansion of the folded pill (wider/taller, content fades in, then collapses after the stay time). **How long a card stays:** Shorter 2 s / Usual 3.5 s / Longer 6 s, hover pauses the countdown. Cards go through one **`LiveEventQueue`**:

- **Priority (high → low):** Reminder/Timer finished > Microphone/Camera in use (persistent dots, not a card) > Windows notification > VPN up/down > Rain/snow warning > Low battery/charging > Copied text/image/screenshot > Caps Lock/Num Lock/Layout > Volume.
- A higher-priority card replaces the current one with a crossfade (no collapse/re-expand); equal or lower priority waits in the queue (max 5, drop oldest low-priority).
- **Coalescing:** repeated events of the same type update the visible card without re-animating (volume dragging updates the bar live; copying twice in a row replaces the text).
- Never show cards while the Island is hidden by full-screen rules unless the card is a reminder/timer (those use "come out by themselves").

Cards to implement (each has its own on/off toggle):

| Card | Content | Notes |
|---|---|---|
| **Caps Lock** | Icon + `Caps Lock on/off` (yellow accent when on) | Also Num Lock and Scroll Lock (default off), keyboard layout change (`EN → FR`, default off). Poll with `GetKeyState` on a key hook-free event: use `GetAsyncKeyState` at 10 Hz only while enabled, or a low-level keyboard hook if it is demonstrably cheaper; document the choice. |
| **Volume** | Speaker icon, slider bar, value | Shown on any volume change (endpoint volume callback), mouse wheel over the island changes volume |
| **Copied** | `Copied` + text preview (1 line) · source app | Uses the clipboard history source (section 7) |
| **Image copied** | Thumbnail + `Image copied` + `268×125 · ScreenClippingHost.exe` | Source process from `GetClipboardOwner` → process name. Shows when the Snipping Tool puts an image on the clipboard. |
| **Screenshot saved** | Thumbnail + file name | After the Full screen tile saves to the screenshots folder |
| **Reminder** | Bell icon, text, **`+10 min`** snooze button, **✓** done button; warm orange glow border | Stays until acted on; see Note tab |
| **Timer finished** | Timer text + Stop / +5 min | Optional soft sound |
| **Windows notification** | App icon, title, one-line body; click opens the program | See 9.3 |
| **VPN** | `VPN connected · <country>` / `VPN disconnected` | Country/IP lookup is **off by default** (it calls a web service); see 9.4 |
| **Microphone / Camera** | Colored dots on the folded pill while any app is using the mic (orange) or camera (green); tooltip lists the app | Read the `CapabilityAccessManager\ConsentStore` registry (`LastUsedTimeStop == 0` marks an active session) for microphone and webcam, both packaged and non-packaged apps; poll every 2 s only while enabled |
| **Rain/snow warning** | `Rain starts in 1 h` | See Weather |
| **Battery** | `Battery low 15%` / `Charging` / `Fully charged` | Default on for low battery only |

## 6. Open island: layout and tab sizes

The top of the open island has a **tab strip** (icon + label for the active tab, icon only for others) on the left, and on the right: a **Quiet chip** (pauses all live cards except reminders; `NP`-style chip in the reference is replaced by this clear label/tooltip), a **VPN chip** (only when a VPN is on, shows country if lookup is enabled), a **Pin** button, and a **Settings** button (opens the Island settings page in AppLock). Keyboard: `Ctrl+Tab` next tab, `Esc` closes (or leaves a text field first), arrow keys move between tab icons.

Fixed design sizes in DIPs at 100% scale (scaled by the Island size setting; tune during the spike, keep in one table in code):

| Tab | Size (W × H) |
|---|---|
| Home | 560 × 250 |
| Clipboard | 560 × 300 |
| Shelf | 560 × 250 |
| Text | 560 × 250 |
| Chat | 600 × 440 |
| Note | 580 × 330 |
| Weather | 620 × 290 |
| Controls | 560 × 200 |

**Tabs can be hidden** (settings › Modules › "Tabs of the island"); a hidden tab still opens through its hotkey/command. Remember the last used tab; the Island opens on it unless "Always open on Home" is set.

## 7. Home tab

Three zones (what is shown is configurable in settings › Modules › "What is on Home", with a small live preview of the layout; Home re-lays itself out when modules are removed):

**Left column (top to bottom, user can disable each):**
1. **Media player** (when something is playing; otherwise this area shows **Recently copied**): cover art, title, artist, source app name (e.g. "MICROSOFT EDGE"), timeline with elapsed/remaining time (click to seek where supported), previous / play-pause / next. Uses `GlobalSystemMediaTransportControlsSessionManager`; follows the **current** session, with a small session switcher if several apps play. Smooth progress bar: animate with a composition animation between 1 Hz position updates, not by re-layout. Optional (phase 2): synced lyrics panel.
2. **Recently copied:** the last 2–3 clipboard items with age (`31 min`) and a `>` that opens the Clipboard tab. Empty state: *"Copy something and your latest items show up here; the player appears above once music is playing."*
3. **Volume row:** speaker icon, slider, value, and the **output device** dropdown (reuse the Volume Mixer service).

**Tile grid (2 columns × 4 rows) on the right.** Tiles are data-driven (`IslandTileDefinition`) so they can be reordered/hidden. The tiles, with the behavior requested by the user:

| Tile | Behavior |
|---|---|
| **Area** | Hides the Island (close animation), then sends **Win + Shift + S** (opens Windows Snipping Tool region capture). Fallback if the chord does nothing: launch `ms-screenclip:`. |
| **Full screen** | Captures the monitor under the cursor (Windows.Graphics.Capture or DXGI duplication, excluded Island), saves a PNG to the screenshots folder (default `Pictures\Screenshots\Island`, configurable) named `Island_yyyy-MM-dd_HH-mm-ss`, copies it to the clipboard, shows the **Screenshot saved / Image copied** card. |
| **Screen text** | Hides the Island and sends **Win + Shift + T**. This chord belongs to **PowerToys Text Extractor** by default; Windows itself does not define it. The tile therefore: checks whether PowerToys is installed; if not, shows a one-time card *"Install PowerToys Text Extractor, or change this shortcut in settings"*. The chord is configurable. |
| **QR code** | Captures the current monitor, scans it for QR codes (ZXing.Net, Apache-2.0), and shows a result card with **Open / Copy** (URLs open only after the user taps Open). If none is found: *"No QR code found on screen"*. A secondary action "Make QR from clipboard text" shows the code in a small popup. |
| **Network speed** *(replaces Color picker)* | Live tile showing **↓ download / ↑ upload** speed (e.g. `↓ 4.2 MB/s  ↑ 380 KB/s`), updated at 1 Hz **only while the Home tab is visible**, using the existing `NetworkStatsProvider` from `speed-meter.md`. Click opens the **Speed Meter** settings in AppLock (or toggles the floating Speed Meter widget if enabled; pick one and keep it consistent). |
| **CPU & RAM** *(replaces Plain text)* | Live tile showing **CPU %** and **RAM %** (e.g. `CPU 18%  ·  RAM 61%`), small bars, 1 Hz while visible, using the shared PDH/performance counter sampler. Click opens Task Manager. |
| **Record screen** | Hides the Island and sends **Win + Shift + R** (Snipping Tool screen recording, available on recent Windows 11 builds). If the chord is unsupported (older Windows), show a one-time card *"Screen recording needs the Snipping Tool update"* and open the Snipping Tool app. |
| **Command** | Opens the command bar (section 8). |

Chord sending: use `SendInput` with correct press/release order and a short delay after the Island's close animation starts (so the Island never appears in the capture UI); each tile's chord is configurable in settings (a key-capture box) and can be reset. The Island ignores its own clipboard events caused by these tools only for the source label, not for showing the "Image copied" card (the card is wanted).

**Bottom row: Shortcuts (launcher).** App/link/folder/file icons in a row with a dashed `+` tile. Empty state text: *"Drop app shortcuts, folders or files here."*
- **Add dialog (ContentDialog, or in-island flyout):** *"A link for the launcher"* with a text field (`https://…`, `steam://run/730`, a folder or file path, or a command), optional **Name**, toggle **"Use the site's or app's own icon"** (on by default; off lets the user **Choose a picture or app…**), **Cancel / Add**.
- Click launches (`ShellExecute`), Ctrl+click opens the file's folder, right-click: Edit / Remove / Run as administrator (for exe), drag to reorder. Drag-and-drop of files/shortcuts/folders onto the row adds them.
- Icons: for apps use the shell icon (cache as PNG, 64×64); for sites fetch the site's own favicon directly from its host (`https://<host>/favicon.ico` or the page's `<link rel=icon>`), **not** from a third-party favicon service; cache on disk; placeholder globe until loaded.
- Max 24 items; stored in `shortcuts.json`.

## 8. Command bar (Alt + Space, configurable)

A centered mini-window or an in-island search field (decide in the spike; in-island is preferred so it shares the animation) that comes out over the screen even when the Island is hidden:

- **Files and folders by name** from configured locations (default: Desktop, Documents, Downloads, Pictures, Music, Videos; settings allows more folders, one per line). The Island builds the name index **itself in memory** on first open and keeps it fresh with `FileSystemWatcher` (names only, no content, never written to disk). Indexing runs on a low-priority background thread with a progress hint, and never blocks typing.
- Results: `Enter` opens, `Ctrl+Enter` shows in its folder, `Alt+Enter` copy path. Also matches **launcher shortcuts**, **installed apps** (Start-menu shortcuts), and built-in commands: `lock`, `sleep`, `restart`, `shut down` (these ask for a confirmation step), `vpn on/off` (only for Windows built-in VPN profiles via `rasdial`), `note <text>`, `remind <text>`, `timer 10m`, `weather`, and a quick **calculator** (`2*(3+4)`). The last row is always **"Search in the browser"** (engine: By language: Google / Bing / DuckDuckGo, configurable).
- Fuzzy matching with ranking (prefix > word-start > substring > subsequence) and recency boost. Results update within 50 ms of typing for 100k names.

## 9. Other tabs

### 9.1 Clipboard tab
- Search box, list of items (type icon, one-line preview, source app, age, characters), trash button to clear (with confirmation), footer hint *"Click to paste · Shift+click to copy only"* and `↑↓ Enter`.
- **Reuse the clipboard history of `clipboard.md`** via an `IClipboardHistorySource` interface so there is only one clipboard monitor in the app. If the Clipboard card is not yet implemented, add a minimal implementation of the interface (listener via `AddClipboardFormatListener`, last N items, persisted) that the Clipboard card can later take over. Respect the same privacy rules (ignore items flagged `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory = 0`, e.g. password managers, and an excluded-apps list).
- Click pastes into the previously focused app (set clipboard, restore focus, send Ctrl+V); Shift+click only copies. Pin items; images show as thumbnails.

### 9.2 Shelf tab
- A temporary shelf for files: **drag files onto the folded island** (it expands straight into the Shelf with a "Drop here" highlight) or onto the tab; drag items **out** to other apps/folders. Items are **references to the original files by default** (option "Copy files into the shelf" for a stable copy). Thumbnails/icons, remove button, "Clear all". Stored in `shelf.json`. Missing files show a greyed state.

### 9.3 Text tab
Small text helpers that replace the removed "Plain text" tile on Home: **Paste as plain text** (strips formatting from the clipboard and pastes), text transforms on clipboard text (UPPERCASE, lowercase, Title Case, trim, remove line breaks, sort lines, count words/characters), and a **Dictation** button that triggers Windows voice typing (`Win + H`). Everything works on the clipboard text and shows the result with a Copy button; never sends text anywhere.

### 9.4 Chat tab
- Embedded **WebView2** views of web AI assistants as sub-tabs, defaulting to **Gemini, DeepSeek, ChatGPT**, plus user-added sites (name + URL, max 6). An **"In a window"** button pops the current site out to a normal window. Own WebView2 user-data folder (`%LOCALAPPDATA%\AppLock\island\webview`) so logins persist; AppLock never reads credentials.
- Created lazily on first use, suspended (`TrySuspend`) while the Island is closed, disposed after the idle timeout. Opening the tab must not stall the open animation: show a skeleton placeholder, create the WebView2 after the animation ends, and fade it in.
- Not included in screenshots (follows the capture-exclusion setting). The tab is hidden if the WebView2 runtime is missing (with a one-line install hint in settings).

### 9.5 Note tab
- **Notes:** multiple note tabs (`Note`, `+`), auto-saved, plain text with a light formatting bar (bold, italic, underline, strikethrough, bulleted/numbered list, checklist), copy and delete buttons. Placeholder: *"Write anything here: an order number, a to-do list, a code snippet. It saves itself."* Stored **encrypted with DPAPI** in `notes.dat`; option **"Require AppLock authentication to open Notes"** (Windows Hello / admin password) for private notes.
- **Timer (right panel):** ring with `5:00`, **Start**, **Custom**, presets **5 / 10 / 15 / 25 / 45 min**. Running timer shows a small countdown on the folded pill; ends with a **Timer finished** card.
- **Reminders:** list with relative times (`in 9 min`), and a quick-add box (*"Call mum tomorrow at 9"*). Parse natural language: `in 10 minutes`, `at 21:30`, `tomorrow at 9`, `Friday 5pm`, `in 2 hours` (use a small tested parser; localized time formats). Reminders persist in `reminders.json` and fire as **Reminder cards** with **+10 min** (snooze) and **✓**. Missed reminders (app was closed) are shown once at startup. A reminder card is never auto-dismissed. The `remind` command in the command bar uses the same parser.

### 9.6 Weather tab
- **First run:** *"Where should the weather be for?"* city search (button-triggered, **no autocomplete-per-keystroke**), results list with region/country; the chosen city is stored on this PC; **the city is never guessed from the IP address.**
- **Data:** MET Norway Locationforecast (no API key; send a **User-Agent identifying AppLock with a contact URL/email**; respect `Expires`/`If-Modified-Since` caching; coordinates rounded to 4 decimals; refresh at most once per 30 minutes; **show "Data from MET Norway"**) and OpenStreetMap Nominatim for city search (max 1 request/second, valid User-Agent, results cached). Verify both services' current terms of use when implementing. Hide the provider behind `IWeatherProvider` so it can be swapped. Weather symbols: use the MET weather icon set (MIT) or Segoe Fluent weather glyphs, with attribution.
- **Layout:** left card with city, big temperature, condition text, `Feels like`, today's high/low, a one-line outlook (*"The rain ends by 6 PM, down to 24° at night"*), and a refresh button; right: **Hours / Week** toggle with an hourly strip (time, icon, temperature, precipitation) or 7-day list; tiles for **Wind** (compass + speed), **Humidity** (+dew point), **Pressure** (gauge + trend), **Sun** (sunrise/sunset arc, computed locally with the NOAA solar algorithm, no extra API), **Precipitation today**, **UV index** (with level text; label as clear-sky UV if that is what the API gives).
- **Settings:** City, Units (`°C · m/s` / `°F · mph`), **Temperature on the folded island**, **Warn about rain and snow** (card about one hour before it starts, once per event).
- Cache the last forecast on disk so the tab opens instantly and shows `Updated 12 min ago` when offline.

### 9.7 Controls tab (control centre)
- Tiles: **Wi-Fi** (state, network name, toggle), **Bluetooth** (toggle), **Dark theme** (toggles Windows app/system theme via `AppsUseLightTheme` / `SystemUsesLightTheme`), **Battery** (percent, `On mains power`/time remaining), plus **VPN** (state; toggle only for Windows built-in VPN profiles) and a **Brightness slider** if the Brightness module exists (reuse its service).
- Buttons: **Lock**, **Sleep**, **Restart**, **Shut down**. Restart/Shut down need a **second confirming click** (the button changes to `Tap again` for 3 s) to prevent accidents.
- Wi-Fi/Bluetooth via `Windows.Devices.Radios` (`Radio.RequestAccessAsync`, `SetStateAsync`); show `No adapter` when absent. A setting **"Network in the control centre: Auto / Wi-Fi / Ethernet"**; toggling a wired adapter needs administrator rights, so show that clearly and use the elevation helper only when the user actually taps it.
- Island theme switch from the Controls tab changes only Windows' theme; the Island's own theme setting is separate (section 12).

## 10. Windows notifications, VPN, and privacy behavior

### 10.1 Windows notifications on the Island
- Use `UserNotificationListener` to read notifications and show them as cards (app name/icon, title, one line); a click opens the program. **Do not store notification content anywhere.** The Island respects Do Not Disturb/Focus (stays quiet), has a **"Do not show notifications of these programs"** list (one per line, as Windows names the program), and an **Open** button to Windows Settings › Notifications so the user can turn off the duplicate banner.
- **`UserNotificationListener` requires package identity.** If AppLock is unpackaged, either register a sparse MSIX package for identity or mark this feature "Not available in this build" with an explanation; do not crash and do not fake it. Decide in Milestone 12.

### 10.2 VPN
- Detect VPN state from network adapters (tunnel/TAP/WireGuard/known VPN adapter descriptions and `NetworkChange` events). **Country/IP lookup is off by default** (the reference app does this by default; AppLock does not): when the user turns it on, call a public IP-geolocation service **only when the VPN state changes and at most every 15 minutes**, show the country in the chip, and tell the user in the setting description that an external service sees their IP. When off, the chip just says `VPN`.

### 10.3 Network and privacy summary (must also appear as a read-only list in settings › About)
Network calls this feature may make: MET Norway forecast, OSM Nominatim city search, the user's chosen web AI sites in Chat (inside WebView2), favicons from the user's own launcher links, optional IP-geolocation lookup (off by default), and nothing else. No telemetry. All data stays in `%LOCALAPPDATA%\AppLock\island\`.

## 11. Visibility rules

All with the animations from section 4 (slide up/down, never pop):

- **Hide in full-screen apps** (default on): games, full-screen video, presentations (`SHQueryUserNotificationState`, plus a foreground-window-covers-monitor check). Exception: reminders/timer cards.
- **Come out over full-screen apps when the cursor rests at the top edge** (default on, 500 ms dwell). Warn in the description that this can minimize some games. Note in the spec that the Island cannot draw above true exclusive-fullscreen games on some systems.
- **Hide the island until needed** (auto-hide): the folded island slides off-screen; resting the cursor at the very top edge brings it back; cards/recordings/reminders bring it out by themselves.
- **Stay in sight on the desktop** (for auto-hide): while the desktop itself is foreground, keep it visible.
- **Hide only while a window is under the island:** auto-hide applies only if the foreground window overlaps the Island's area (maximized or moved up).
- **Chosen apps:** list of process names with mode **Hide** (Island hides while one is in front) or **Keep** (Island is only shown while one is in front); **Pick from open apps** and **Browsers** (adds installed browsers) helper buttons.
- While hidden, detect the top-edge dwell with a **30 Hz `GetCursorPos` poll that runs only in hidden/fullscreen states** (no global mouse hook). Stop polling when not needed.
- Multi-monitor: **Show on:** Primary / Monitor with the cursor / a chosen monitor.

## 12. Island settings page (opened from the Others card)

Same visual style as the other Others settings pages (SettingsCard/SettingsExpander, Light/Dark/System). Sections:

**General:** Enable Island · Start with AppLock · Show on (monitor) · Lite mode · **Global hotkeys** (each with a key-capture box and conflict check through `HotkeyService`): Toggle/Open island (default `Ctrl+Alt+I`), Command bar (`Alt+Space`), Quiet mode (`Ctrl+Alt+Q`), and optional per-tab hotkeys (empty by default). All hotkeys keep working even when the Island is hidden.

**Appearance:** Island shape (Notch / Capsule) · Island theme (Dark / Light / Windows) · **Island colour** (swatches: auto black, dark navy, indigo, green, maroon, brown, cream, mint, lavender, and a custom color picker; text color flips automatically for contrast) · Island size (75–200%, default 100) · Transparency of the folded island (0–60%) · Transparency of the open island (0–60%) · **Place along the top edge** (slider −100…+100, 0 = center) · **Under the taskbar when it is at the top** (default on).

**Folded island:** Time (on/off) · Seconds · Time format (Auto / 24-hour / 12-hour) · Battery charge · Temperature (in Weather) · Open delay (0–600 ms, default 150) · Close delay (0–1500 ms, default 350).

**Behavior (visibility rules):** all items from section 11 · **Show the island in screenshots and recordings** (default off, note about capture software).

**Live events:** How long a card stays (Shorter / Usual / Longer) · toggles: Volume · Microphone and camera · Caps Lock (+ Num Lock, layout) · Clipboard copies · Screenshots · VPN (+ "Look up country and IP", default off) · Windows notifications (+ excluded programs list, Open Windows notification settings) · Battery · Screenshots folder (text box + Browse, empty = default).

**Weather:** City · Units · Temperature on the folded island · Warn about rain and snow.

**Modules:** *What is on Home* (Player, Recently copied, Volume; the 8 tiles; Shortcuts bar) with live layout preview · *Tabs of the island* (Clipboard, Shelf, Text, Chat, Note, Weather, Controls) · Media player · File shelf · Launcher · **File search in the command bar** + *Where to look for files* (multi-line, one folder per line) + *Search in the browser* engine · Weather · Control centre + *Network in the control centre* · Chat sites list · Chat idle timeout.

**Tiles & shortcuts:** the chord for Area, Record screen, Screen text (key-capture boxes, `Reset` each).

**About / Developer:** privacy & network list (10.3), credits and licenses (ZXing.Net, MET Norway data, weather icons, etc.), hidden frame-stats overlay and benchmark (4.3).

All strings go through the existing localization resource file. Every setting applies live without restarting the Island, except Show-on-monitor changes which animate the island to the new monitor.

## 13. Data model (settings)

Add `IslandSettings` to the protected settings JSON (migrate older files without errors); large/user data lives in separate files under `%LOCALAPPDATA%\AppLock\island\`:

```csharp
public class IslandSettings
{
    public bool Enabled { get; set; }
    public IslandShape Shape { get; set; } = IslandShape.Notch;           // Notch | Capsule
    public IslandTheme Theme { get; set; } = IslandTheme.Dark;            // Dark | Light | Windows
    public string ColorHex { get; set; } = "auto";
    public int SizePercent { get; set; } = 100;
    public int FoldedTransparency { get; set; } = 0;
    public int OpenTransparency { get; set; } = 0;
    public int PositionAlongTop { get; set; } = 0;                        // -100..100
    public bool UnderTaskbarWhenTop { get; set; } = true;
    public bool LiteMode { get; set; }
    public MonitorChoice Monitor { get; set; } = MonitorChoice.Primary;

    public bool ShowTime { get; set; } = true;
    public bool ShowSeconds { get; set; } = true;
    public TimeFormat TimeFormat { get; set; } = TimeFormat.Auto;
    public bool ShowBattery { get; set; } = true;
    public bool ShowTemperature { get; set; } = true;
    public int OpenDelayMs { get; set; } = 150;
    public int CloseDelayMs { get; set; } = 350;

    public bool HideInFullscreen { get; set; } = true;
    public bool TopEdgeOverFullscreen { get; set; } = true;
    public bool AutoHide { get; set; }
    public bool StayOnDesktop { get; set; }
    public bool HideOnlyWhenUnder { get; set; }
    public AppListMode ChosenAppsMode { get; set; } = AppListMode.Hide;
    public List<string> ChosenApps { get; set; } = new();
    public bool ShowInCaptures { get; set; }

    public CardDuration CardDuration { get; set; } = CardDuration.Usual;
    public LiveEventToggles Events { get; set; } = new();                  // volume, micCam, caps, num, layout, copied, screenshot, vpn, notifications, battery
    public bool VpnLookupEnabled { get; set; }
    public List<string> NotificationBlockedApps { get; set; } = new();
    public string ScreenshotsFolder { get; set; } = "";

    public List<string> HomeLeft { get; set; } = new() { "player", "recent", "volume" };
    public List<string> HomeTiles { get; set; } = new() { "area", "fullscreen", "screentext", "qr", "netspeed", "cpuram", "record", "command" };
    public bool HomeShortcuts { get; set; } = true;
    public List<string> Tabs { get; set; } = new() { "clipboard", "shelf", "text", "chat", "note", "weather", "controls" };
    public string LastTab { get; set; } = "home";
    public bool AlwaysOpenOnHome { get; set; }

    public Dictionary<string, string> Hotkeys { get; set; } = new();       // toggle, command, quiet, per-tab
    public TileChords Chords { get; set; } = new();                        // area "Win+Shift+S", record "Win+Shift+R", screentext "Win+Shift+T"

    public WeatherSettings Weather { get; set; } = new();                  // City, Lat, Lon, Units, TempOnIsland, RainWarning
    public List<string> FileSearchFolders { get; set; } = new();
    public SearchEngine BrowserSearch { get; set; } = SearchEngine.ByLanguage;
    public bool NotesRequireAuth { get; set; }
    public List<ChatSite> ChatSites { get; set; } = new();                 // defaults: Gemini, DeepSeek, ChatGPT
    public int ChatIdleMinutes { get; set; } = 5;
    public NetworkChoice ControlNetwork { get; set; } = NetworkChoice.Auto;
}
```

## 14. Project structure (additions)

```
AppLock/
  Island/
    Host/IslandWindow.cs, IslandHost.cs, IslandPositioner.cs, VisibilityController.cs, EdgeDwellWatcher.cs
    Motion/IslandMotion.cs, IslandAnimator.cs, NotchShape.cs, FrameStats.cs
    Events/LiveEventQueue.cs, LiveEvent.cs, EventSources/ (CapsLock, Volume, Clipboard, Battery, MicCam, Vpn, Notifications, Reminder, Weather)
    Modules/ (MediaModule, ClipboardAdapter, ShelfModule, LauncherModule, NoteModule, TimerModule, WeatherModule, ControlsModule, ChatModule, CommandBarModule, FileIndex, ReminderParser, QrScanner, ChordSender)
    Views/FoldedView.xaml, OpenShell.xaml, HomeTab.xaml, ClipboardTab.xaml, ShelfTab.xaml, TextTab.xaml, ChatTab.xaml, NoteTab.xaml, WeatherTab.xaml, ControlsTab.xaml, Cards/*.xaml, CommandBar.xaml, TileControl.xaml
  Models/IslandSettings.cs
  ViewModels/Island/*.cs
  Views/IslandSettingsPage.xaml(.cs)           // reached from the Others card
  Assets/island/ (weather icons, tile icons if not Segoe Fluent)
```

Register the Others card with order 90. Keep the Island's heavy modules in separate classes behind interfaces (`IMediaSource`, `IClipboardHistorySource`, `IWeatherProvider`, `IFileIndex`) so they can be unit-tested and swapped.

## 15. Edge cases

- Several monitors with different DPI/scale; docking/undocking; RDP sessions; a taskbar at the top, left, or right; auto-hiding taskbar; Windows 10 vs 11 corner styles.
- Window focus: typing must work in Note/command bar/Chat and focus must return to the previous app; the Island must never take focus when only hovered.
- A game or video player in fullscreen: the Island hides, hotkeys still work, mic/camera dots and reminders can still appear.
- Media sessions that change quickly; apps without thumbnails or timelines (hide seek, show a generic icon).
- Large clipboard images (limit preview size, decode off-thread); sensitive clipboard items (respect exclusion flags).
- No network: weather shows cached data; no battery (desktop PC): battery elements are hidden; no Bluetooth/Wi-Fi adapter.
- Sleep/resume, lock/unlock, user switch: re-position, re-subscribe event sources, refresh weather; pause polling while the session is locked.
- Windows Settings › Accessibility › "Show animations" off: reduced-motion path.
- AppLock master protection toggle OFF does **not** disable the Island (it has its own toggle).

## 16. Tests and manual checklist

Unit tests: `LiveEventQueue` (priority, coalescing, preemption, expiry), `ReminderParser` (many phrases, locales, edge times), command-bar ranking/fuzzy match, file index watcher updates, tile chord builder, MET response parsing and sunrise/sunset math, settings migration, positioner math (DPI, taskbar edges, offsets).

Manual:
- [ ] Others page shows an "Island" card (order after PC Checker); toggle starts/stops the Island; clicking the card opens its settings page
- [ ] Folded island shows time, battery, and (when set) temperature; seconds and format toggles work live
- [ ] Hover opens smoothly after the open delay; leaving closes after the close delay; moving out and back mid-animation reverses smoothly with no jump
- [ ] Open/close benchmark (Developer) reports p95 within target; the number is in the milestone report
- [ ] Caps Lock on/off cards animate in and out; volume card follows the slider; mouse wheel over the island changes volume
- [ ] Copying text shows a Copied card; Win+Shift+S capture shows an Image copied card with size and source
- [ ] Home: media player controls work; seek works; recently copied updates; volume row and output device work
- [ ] Tiles: Area sends Win+Shift+S; Record screen sends Win+Shift+R; Screen text sends Win+Shift+T (hint if PowerToys is missing); Network speed and CPU & RAM tiles update live and stop updating when the tab is hidden; QR and Full screen work; Command opens the bar
- [ ] Launcher: add via dialog and by drag-and-drop, launch, Ctrl+click opens folder, reorder, remove; favicons load directly from the site
- [ ] Clipboard tab: search, click-to-paste returns focus to the previous app; Shift+click only copies
- [ ] Shelf: drag a file onto the folded island → it opens on the Shelf; drag out works
- [ ] Note: typing works, notes persist after restart and are encrypted on disk; quick-add reminder "in 2 minutes" fires a card with +10 min and ✓; timer finishes with a card
- [ ] Weather: city search, forecast, hourly/week, tiles, rain warning; works from cache offline
- [ ] Controls: Wi-Fi/Bluetooth/Dark theme toggles, Lock/Sleep; Restart/Shut down need a second tap
- [ ] Chat tab loads Gemini/DeepSeek/ChatGPT lazily without stalling the open animation; memory drops after the idle timeout
- [ ] Windows notifications appear as cards (or the feature clearly says it is unavailable); VPN card works; country lookup is off by default
- [ ] Mic/camera dots appear while an app uses them
- [ ] Fullscreen game/video hides the Island; top-edge dwell brings it back; auto-hide, chosen apps (Hide/Keep), and desktop rules behave
- [ ] Multi-monitor and DPI changes reposition correctly; taskbar at top places the island under it
- [ ] Light/Dark/Windows island themes and all colors have readable text; High Contrast is usable
- [ ] Reduced motion and Lite mode remove springs/shadows and stay smooth
- [ ] Idle CPU < 1%; memory targets met

## 17. Out of scope / phase 2

Synced lyrics in the player, AI features that call APIs directly (Chat is just embedded websites), Color picker and Plain text as optional tiles, calendar integration, a plugin system, per-monitor Islands, a "Windows service" host.

## 18. Delivery

Implement in this order, **one milestone at a time**:

1. **Spike (3.2):** rendering technique, transparent non-activating topmost window, notch morph with springs, hover open/close, frame-time readout, decision document. Smoothness must be proven here before building anything else.
2. **Foundation:** `IslandSettings` + migration, Others card (order 90) + settings page skeleton, `IslandHost`, positioner (monitor/DPI/taskbar/offset), folded island with time + battery, shape/theme/color/size/transparency settings, hotkey to toggle.
3. **Open/close shell + tab strip:** hover logic with delays and hysteresis, interruptible springs, fixed tab sizes with empty tab content, tab-switch animation, keyboard navigation, pin/quiet/settings buttons, **frame-stats overlay and benchmark (4.3)**. Report p95.
4. **Live event engine + first cards:** `LiveEventQueue`, Caps Lock, Volume (+ mouse wheel), Copied/Image copied (clipboard source), Screenshot saved.
5. **Home tab:** media player (SMTC) + folded media mini with equalizer, recently copied, volume row, the 8 tiles with the requested chords, Network speed and CPU & RAM tiles, Full screen and QR.
6. **Clipboard and Shelf tabs.**
7. **Launcher (shortcuts bar) + add dialog + drag-and-drop.**
8. **Command bar + file index + calculator/commands.**
9. **Note tab: notes, timer, reminders, reminder cards, parser.**
10. **Weather tab + folded temperature + rain/snow warning.**
11. **Controls tab.**
12. **Notifications, VPN, mic/camera dots, battery cards** (decide the package-identity approach for notifications).
13. **Chat tab (WebView2).**
14. **Text tab.**
15. **Visibility rules:** fullscreen, auto-hide, chosen apps, edge dwell, multi-monitor.
16. **Settings completion + polish + tests:** every setting live, localization, credits & privacy list, reduced motion/Lite mode, run the full manual checklist and the benchmark, report any failing item and the final p95.

After each milestone: `dotnet build -c Release` (fix all errors and warnings), **launch the app and show the Island**, report in 5 lines or fewer (include the frame p95 from milestone 3 on), commit, then **stop and wait for me to reply "continue"**. Do not start the next milestone on your own. If smoothness regresses at any milestone, fix that before adding anything new.
