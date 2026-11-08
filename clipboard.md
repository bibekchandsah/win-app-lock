# Clipboard Manager: Implementation Spec (WinUI 3)

A persistent clipboard history for AppLock. It works like the Windows clipboard history (`Win + V`), but it **keeps history across restarts and reboots**, lets the user choose **how many items to keep**, and opens with a **custom global hotkey**. It stores copied **text and images** (and optionally file lists) and adds extra tools: pinning, search, snippets, privacy controls and more.

Stack: C# / .NET 8 / WinUI 3 (Windows App SDK). Storage: SQLite (`Microsoft.Data.Sqlite`). Theme: **Light / Dark / System**.

Placement: a **Clipboard** card in the **Others** page (enable toggle, quick stats, "Open history" and "Settings" buttons). Do not add a new sidebar item.

---

## 1. Why this exists (problem)

The Windows clipboard history is wiped on restart, has a small fixed limit (25 items), and has no custom hotkey, search or privacy tools. This feature fixes these:
- History is saved to disk and survives restarts.
- The user sets the item limit.
- The user sets the hotkey.
- Extra features (section 8) make it useful every day.

---

## 2. Feature list

### Core
1. **Capture** every copy event: text, images, and (optional) file lists. Skip duplicates; a repeated item moves to the top.
2. **Persistent history** stored locally (survives app restart, Windows restart).
3. **History limit** chosen by the user: presets `25, 50, 100, 250, 500, 1000` + **Custom** (10 to 5000). Oldest **unpinned** item is removed first when the limit is reached.
4. **Custom global hotkey** to open the clipboard popup (section 5). Default `Ctrl + Shift + V`.
5. **Clipboard popup** near the cursor with search, filters and keyboard navigation.
6. **Paste** the chosen item into the previously active app (auto-paste or copy only, user choice).
7. **Pin** items so they are never removed by the limit or auto-delete.
8. **Delete** one item, or **Clear all** (keeps pinned unless the user chooses to clear those too).
9. **Light / Dark / System** theme for the popup and settings.

### Extra features (useful additions)
10. **Search** inside history (text, source app name, labels).
11. **Filters**: All, Text, Images, Links, Pinned.
12. **Source app** shown on each item (icon + name) and date/time.
13. **Paste as plain text** (strips formatting) with `Shift + Enter`.
14. **Pause / Private mode**: stop capturing for 5, 15, 60 minutes or until resumed.
15. **Ignore apps** list (e.g. password managers, `keepass.exe`): never capture from them.
16. **Respect "do not record" flags** that apps set on sensitive data (section 7).
17. **Sensitive item detection** (passwords, card numbers, tokens) with optional auto-expire.
18. **Encrypt history on disk** (DPAPI, current user), on by default.
19. **Windows Hello / PIN lock** to open history (optional).
20. **Snippets** (saved reusable text with a title and optional abbreviation) that never expire.
21. **Text actions** on an item: UPPERCASE, lowercase, Title Case, trim, remove line breaks, URL encode/decode, Base64 encode/decode, then paste.
22. **Edit text item** before pasting.
23. **Multi-select and merge paste** (join with newline, comma, space, or custom separator).
24. **Auto-delete after N days** (unpinned only; 0 = never).
25. **Max item size** limits (text length, image size) and **storage cap** in MB.
26. **Export / Import** history as a ZIP (JSON + images) for backup or moving to another PC.
27. **Quick paste numbers**: items 1 to 9 pasted with `1`..`9` while the popup is open.
28. **Link preview helpers**: detect URLs, show a link icon, "Open in browser" action.
29. **Color detection**: if text is a color code (`#FF8800`, `rgb(...)`), show a color swatch.
30. **Image extras**: thumbnail, size and dimensions, "Save as file...", "Copy as PNG", optional OCR text search (Windows OCR, local, optional).
31. **Tray menu** (if the app has one): Open history, Pause capture, Clear history.
32. **Sound or toast on capture** (optional, off by default).

Do not build every extra at once. Follow the milestones (see the prompt delivered with this spec).

---

## 3. Data model and storage

Location: `%LOCALAPPDATA%\AppLock\Clipboard\`
```
clipboard.db            <- SQLite database
images\<hash>.png       <- full images (encrypted when encryption is on)
thumbs\<hash>.jpg       <- 160 px thumbnails
```

Schema:

```sql
CREATE TABLE items (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  type          TEXT NOT NULL,        -- 'text' | 'image' | 'files'
  content       BLOB,                 -- text/files list, encrypted if enabled (null for image)
  preview       BLOB,                 -- first ~200 chars for the list, encrypted if enabled
  html          BLOB,                 -- optional rich text (HTML) so formatting survives paste
  hash          TEXT NOT NULL,        -- SHA-256 of the content (dedupe)
  image_path    TEXT,
  thumb_path    TEXT,
  width         INTEGER, height INTEGER,
  size_bytes    INTEGER NOT NULL,
  source_process TEXT,                -- e.g. chrome.exe
  source_title  TEXT,                 -- window title (optional)
  created_at    INTEGER NOT NULL,     -- Unix ms
  last_used_at  INTEGER NOT NULL,
  use_count     INTEGER NOT NULL DEFAULT 0,
  pinned        INTEGER NOT NULL DEFAULT 0,
  is_snippet    INTEGER NOT NULL DEFAULT 0,
  label         TEXT,                 -- snippet title or user label
  is_sensitive  INTEGER NOT NULL DEFAULT 0,
  expires_at    INTEGER               -- for sensitive auto-expire
);
CREATE UNIQUE INDEX ux_items_hash ON items(hash) WHERE is_snippet = 0;
CREATE INDEX ix_items_last_used ON items(last_used_at DESC);
```

Rules:
- All DB access on a single background worker (serialize writes). Never block the UI thread.
- **Dedupe**: if the hash exists, update `last_used_at` and move it to the top instead of inserting.
- **Limit enforcement** runs after every insert: delete oldest by `last_used_at` where `pinned = 0 AND is_snippet = 0` until count <= limit. Delete the matching image/thumbnail files too.
- Run `VACUUM` occasionally (e.g. on startup if more than 1000 rows were deleted).
- Schema versioning with `PRAGMA user_version` and simple migrations.
- If the DB is corrupted: rename it to `clipboard.corrupt.db`, create a new one, show an `InfoBar`; never crash.
- Encryption: `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` for `content`, `preview`, `html` and image/thumbnail files when "Encrypt history" is on. Toggling the option re-encrypts or decrypts existing data in a background task with progress.

Settings model:

```csharp
public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = false;
    public int MaxItems { get; set; } = 100;            // 10..5000
    public string Hotkey { get; set; } = "Ctrl+Shift+V";
    public bool CaptureText { get; set; } = true;
    public bool CaptureImages { get; set; } = true;
    public bool CaptureFiles { get; set; } = false;
    public bool KeepRichText { get; set; } = true;
    public int MaxTextKb { get; set; } = 1024;          // skip larger text
    public int MaxImageMb { get; set; } = 10;           // skip larger images
    public int StorageCapMb { get; set; } = 500;
    public int AutoDeleteDays { get; set; } = 0;        // 0 = never
    public bool AutoPaste { get; set; } = true;         // false = copy only
    public string PopupPosition { get; set; } = "cursor"; // cursor | center | caret
    public bool EncryptHistory { get; set; } = true;
    public bool RequireHello { get; set; } = false;
    public bool DetectSensitive { get; set; } = true;
    public int SensitiveExpireSeconds { get; set; } = 60; // 0 = never expire
    public List<string> IgnoredApps { get; set; } = new();
    public bool OverrideWinV { get; set; } = false;     // experimental (section 5)
    public bool PlaySound { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string ThemeMode { get; set; } = "System";   // Light | Dark | System
}
```

Use the app's existing settings store and clamp values on load.

---

## 4. Clipboard capture engine

Create a **hidden message-only native window** (`HWND_MESSAGE`) on a dedicated thread with its own message loop. It is used for both clipboard notifications and the hotkey. This keeps working when the main window is closed.

- Register with `AddClipboardFormatListener(hwnd)`; handle `WM_CLIPBOARDUPDATE`. Also compare `GetClipboardSequenceNumber()` to ignore duplicate notifications.
- **Debounce** about 100 ms, then read the clipboard. Opening the clipboard can fail with `CLIPBRD_E_CANT_OPEN` (another app holds it): retry up to 5 times with short backoff.
- Read formats in priority: `CF_UNICODETEXT` (text), `HTML Format` / `Rich Text Format` (optional rich), `CF_DIB`/`CF_DIBV5`/`PNG` (images), `CF_HDROP` (files). Use Win32 or `Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()`; whichever is more reliable in an unpackaged WinUI 3 app.
- **Image handling**: convert to PNG, compute the hash from pixel bytes, create a 160 px JPEG thumbnail, skip if larger than `MaxImageMb`.
- **Files**: store only the path list (do not copy the files). Mark missing files in the UI later.
- **Source app**: `GetClipboardOwner()` -> `GetWindowThreadProcessId` -> process name; fallback `GetForegroundWindow()`. Store exe name; resolve the icon with the same approach the Volume Mixer spec uses (shell icon, cached).
- **Self-copy guard**: when the app itself writes to the clipboard (to paste an item), set an `_ignoreNext` flag or compare the sequence number so it is not re-captured as new. (Update `last_used_at` instead.)
- Raise an event `ItemAdded` / `ItemChanged` for the UI (marshalled to the UI thread).
- Never throw out of the capture loop; log and continue.

---

## 5. Hotkey (customizable)

### Registration
- Use `RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, vk)` on the hidden window; handle `WM_HOTKEY`.
- Settings control: a **hotkey recorder** (a `Button`/`TextBox` that says "Press new shortcut..." while focused; captures modifiers + key). Rules:
  - Require at least one modifier (`Ctrl`, `Alt`, `Shift`, `Win`), except for function keys (F1 to F24).
  - Show the combination as text, e.g. `Ctrl + Shift + V`.
  - Buttons: **Reset to default**, **Clear** (disables the hotkey).
  - If `RegisterHotKey` fails (already used by another app), show an inline error: "This shortcut is used by another app. Choose a different one." and keep the old hotkey.
  - Unregister and re-register immediately when changed; unregister on exit.

### About `Win + V`
`Win + V` is **reserved by Windows** and `RegisterHotKey` fails for it. So:
- Default is `Ctrl + Shift + V`.
- Provide an **experimental option "Use Win + V" (OverrideWinV, off by default)**. It installs a low-level keyboard hook (`WH_KEYBOARD_LL`) on the capture thread; when `Win` is down and `V` is pressed, it swallows the key and opens the popup. To stop the Start menu opening when `Win` is released, inject a dummy key (`VK 0xFF` down/up) before the release. Show a warning: "Windows' own clipboard history may still open on some systems. You can turn it off in Windows Settings > System > Clipboard." Add an "Open Windows clipboard settings" link (`ms-settings:clipboard`).
- The hook must be fast (do no work in the callback except posting a message) and removed when the option is off or the app exits.

---

## 6. Popup window (UI)

A small borderless, topmost, rounded `Window` (about 420 x 520 px) opened by the hotkey (and by the card's "Open history" button).

Behavior:
- Before showing, remember the previous foreground window: `prev = GetForegroundWindow()`.
- Position by setting: `cursor` (at mouse, kept inside the monitor work area), `center` (of the active monitor), or `caret` (use `GetGUIThreadInfo` caret rect, fall back to cursor).
- Activate and focus the search box so typing searches immediately. Close on `Esc`, on click outside (lost focus), or after paste.
- If the hotkey is pressed while the popup is open, close it (toggle).
- Per-monitor DPI aware, hidden from taskbar and Alt+Tab.
- Backdrop: Mica/Acrylic with theme brushes. Light / Dark / System follows the same approach as the Volume Mixer spec (`RequestedTheme` on the root content + `UISettings.ColorValuesChanged` for System).

Layout (top to bottom):
1. **Search box** with placeholder "Search clipboard...".
2. **Filter chips**: All, Text, Images, Links, Pinned (use `SelectorBar` or `RadioButtons`).
3. **List** (`ListView`, virtualized) of items. Row content:
   - Text: up to 3 lines of preview, monospace for code-like content; link icon for URLs; color swatch for color codes.
   - Image: thumbnail (max height about 120 px) + dimensions and size.
   - Files: first file name + "+N more".
   - Meta line: source app icon + name, relative time ("2 min ago"), pin indicator.
   - Hover buttons: Pin/Unpin, Delete, More (`...` flyout with text actions, Edit, Save as snippet, Open link, Save image as...).
4. **Footer**: item count / limit ("87 / 100"), "Clear all" button, Settings button, and a hint line "Enter paste, Shift+Enter plain text, Del delete, Ctrl+P pin".

Keyboard:
| Key | Action |
|---|---|
| Up / Down, PageUp / PageDown | Move selection (search box keeps typing focus) |
| Enter | Paste selected |
| Shift + Enter | Paste as plain text |
| Ctrl + C | Copy only (no auto paste) |
| Delete | Delete selected |
| Ctrl + P | Pin / unpin |
| Ctrl + E | Edit text item |
| Space (with multi-select mode) / Ctrl + click | Multi-select; Enter pastes merged |
| 1 to 9 (when the search box is empty) | Paste the Nth visible item |
| Esc | Close |

Empty states: "Nothing copied yet" and "No results for ...". Use `AutomationProperties` and tooltips everywhere.

### Pasting flow
1. Write the chosen item to the system clipboard (set `_ignoreNext`). For text: set unicode text (+ HTML if rich and not plain mode). Images: set bitmap/PNG. Files: set `CF_HDROP`.
2. Hide the popup, `SetForegroundWindow(prev)` (use the `AttachThreadInput` trick if needed).
3. If `AutoPaste` is on: wait about 50 ms, send `Ctrl + V` with `SendInput` (release held modifiers from the hotkey first, otherwise Shift/Alt would corrupt the paste).
4. Update `use_count` and `last_used_at` (item moves to the top).
5. If paste into elevated (admin) windows fails because of UIPI, fall back to "copied to clipboard" and show a small toast "Press Ctrl+V to paste".

---

## 7. Privacy and safety

Clipboard history can contain passwords. Treat this as the most important section.

- **Respect clipboard exclusion formats**: do not capture when the clipboard contains `ExcludeClipboardContentFromMonitorProcessing`, or `CanIncludeInClipboardHistory` set to 0, or `CanUploadToCloudClipboard` set to 0 (password managers set these).
- **Ignored apps list** (process names, same input rules as the Volume Mixer blacklist: trim, lower-case, add `.exe`): ignore captures whose source process is in the list. Ship with suggested entries the user can add with one click (KeePass, Bitwarden, 1Password, LastPass).
- **Sensitive detection** (when on): flag text that looks like a card number (Luhn valid), private key blocks (`-----BEGIN ... PRIVATE KEY-----`), JWT/API-token patterns, or a password-like single token (no spaces, 8+ chars, mixed classes) copied from a browser or password field. Flagged items show a lock icon, preview is masked (`********`) until the user clicks "Reveal", and they auto-expire after `SensitiveExpireSeconds` (0 = never) by deleting the item.
- **Pause / Private mode**: toggle in the card, popup footer and tray; with a timer (5/15/60 min or until resumed). Show a clear indicator when paused.
- **Encryption** (DPAPI) on by default; never log clipboard content (log only types, sizes and hashes).
- **Windows Hello lock** (optional): when `RequireHello` is on, opening the popup calls `UserConsentVerifier.RequestVerificationAsync`; if it fails or is unavailable, keep history hidden. Cache a successful unlock for 1 minute.
- Make sure the popup preview is not captured by screen recording when possible (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, optional setting).
- "Clear history" always confirms with a dialog and deletes files securely enough (delete DB rows + image files, then `VACUUM`).

---

## 8. Settings UI (card + settings page)

**Card in Others**: Enable toggle, status text ("Capturing", "Paused until 15:30", "Disabled"), item count and storage used, buttons **Open history**, **Pause** (flyout with durations), **Settings**.

**Settings page** with tabs (`TabView` without close buttons or `SelectorBar` + `Frame`):

| Tab | Controls |
|---|---|
| **General** | Enable, History limit (`ComboBox` presets + Custom with `NumberBox`), Hotkey recorder (+ Reset, Clear, "Use Win + V (experimental)" checkbox with warning), Popup position (cursor/center/caret), Paste behavior (Auto paste / Copy only), Start with Windows, Theme (Light/Dark/System) |
| **Capture** | Capture text, images, files; Keep rich text; Max text size; Max image size; Storage cap; Auto-delete after N days |
| **Privacy** | Encrypt history, Require Windows Hello, Detect sensitive data + auto-expire seconds, Ignored apps list (Add/Remove + suggestions), Exclude from screen capture |
| **Snippets** | List of snippets with Add / Edit / Delete (title, text, optional abbreviation) |
| **Data** | Export history, Import history, Clear history (with "include pinned" checkbox), storage path with "Open folder", storage usage |

All settings apply live and persist.

---

## 9. Files

```
/Services/ClipboardSettings.cs
/Services/ClipboardMonitor.cs         <- hidden window thread, listener, hotkey, optional WH_KEYBOARD_LL
/Services/ClipboardStore.cs           <- SQLite, limit enforcement, encryption, queries
/Services/ClipboardPasteService.cs    <- set clipboard, restore focus, SendInput
/Services/SensitiveDetector.cs
/Services/HotkeyParser.cs             <- "Ctrl+Shift+V" <-> modifiers + vk
/Services/ClipboardExportService.cs
/Views/ClipboardPopupWindow.xaml(.cs)
/Views/Controls/ClipboardItemRow.xaml(.cs)
/Views/Controls/HotkeyRecorder.xaml(.cs)
/Views/Controls/ClipboardCard.xaml(.cs)        <- card in Others page
/Views/ClipboardSettingsPage.xaml(.cs)
/ViewModels/ClipboardViewModel.cs
/ViewModels/ClipboardItemViewModel.cs
```

NuGet: `Microsoft.Data.Sqlite`. Use `CommunityToolkit.WinUI` only if the project already uses it.

---

## 10. Edge cases

- Clipboard locked by another app: retry, then skip silently.
- Huge text or image: skip and optionally show a small "Not saved: too large" indicator.
- Copy in rapid bursts (e.g. spreadsheets): debounce so only the final content is saved.
- Delayed rendering formats (apps that render data only when requested): request with a time limit, skip on timeout.
- Duplicate content copied from different apps: keep one item, update source app and time.
- Limit lowered (e.g. 500 to 50): trim immediately with a confirmation if more than 100 items will be removed.
- Pinned items exceed the limit: pinned items are exempt; show "Pinned items don't count toward the limit" hint.
- Image/thumbnail file missing on disk: show a placeholder and remove the row on next cleanup.
- Hotkey conflict, hotkey cleared, or app started while another instance runs (use a single-instance mutex so two hooks never exist).
- Session lock / fast user switching: pause capturing while the session is locked; resume on unlock.
- Remote desktop clipboard redirection: capture normally, don't crash on empty formats.
- Elevated (admin) target apps: UIPI may block synthetic paste; use the fallback toast.
- System sleep/resume: re-add the clipboard listener if needed and re-register hotkeys.
- Multi-monitor and DPI changes: popup repositions to the correct monitor and scale.
- The popup must never appear on the lock screen or UAC secure desktop.
- Low disk space: stop saving images, warn once.

---

## 11. Acceptance checklist

- [ ] Copying text and images in other apps adds items; duplicates move to the top.
- [ ] History survives closing the app and restarting Windows.
- [ ] The item limit works for all presets and Custom; oldest unpinned items go first; pinned stay.
- [ ] The hotkey opens and closes the popup; changing it works immediately; conflicts show an error; the default is `Ctrl + Shift + V`.
- [ ] Optional `Win + V` mode works or fails gracefully with the warning.
- [ ] Popup opens at the chosen position without breaking the previous app's focus; Enter pastes into that app; Shift+Enter pastes plain text; "copy only" mode works.
- [ ] Search, filters, pin, delete, clear all, multi-select merge and keyboard shortcuts all work.
- [ ] Images show thumbnails, paste correctly and can be saved as a file.
- [ ] Ignored apps and "do not record" flagged data are never captured; Pause stops capture until resumed.
- [ ] Sensitive items are masked and auto-expire; encryption on disk verified (DB file does not contain readable text).
- [ ] Windows Hello lock works when enabled.
- [ ] Snippets, text actions, auto-delete, storage cap, export and import work.
- [ ] Light, Dark and System themes render correctly in popup and settings.
- [ ] Start with Windows works; capture and hotkey keep working when the main window is closed.
- [ ] No noticeable CPU or memory use in the background; no UI freeze with 1000 items.
