# Eye Care Reminder: Implementation Spec (WinUI 3)

Add an **Eye Care** card to the existing **Others** sidebar page (next to the Brightness card). It reminds the user to rest their eyes by showing a fullscreen HTML overlay at a chosen interval. The overlay is `index.html` (blinking eyes + countdown). The user can set their own message and can also use their own overlay file, e.g. `name.html`.

Stack: C# / .NET 8 / WinUI 3 (Windows App SDK) + WebView2 for rendering the HTML overlay.

---

## 1. Features

1. **Enable/Disable** toggle for Eye Care reminders (master switch for this feature).
2. **Reminder interval**: preset choices `1, 20 (default), 25, 30, 60` minutes, plus **Custom...** (user enters minutes).
3. **Reminder message**: user-editable text. Default: `Have a look far away from your current screen to protect your eyes`.
4. **Schedule** (optional): checkbox "Enable schedule" with Start time and End time in 24-hour format (default `09:00` to `21:00`). Reminders fire only inside this window. Support windows that cross midnight (e.g. 22:00 to 06:00).
5. **Overlay duration**: seconds the overlay stays visible (default `20`), then it closes itself.
6. **Overlay template**:
   - Default: built-in `index.html`.
   - Custom: user picks any `.html` file (e.g. `mytheme.html`) with a file picker, or drops it into the templates folder and picks it from a dropdown.
7. **Preview** button: shows the overlay immediately using the current message/template.
8. Settings persist across app restarts and the reminder timer resumes automatically on app start.

---

## 2. UI (Others page, Eye Care card)

Use the same card style as the Brightness card. Controls:

| Control | WinUI element | Notes |
|---|---|---|
| Enable Eye Care | `ToggleSwitch` | Starts/stops the scheduler |
| Reminder Interval | `DropDownButton` + `MenuFlyout` with `ToggleMenuFlyoutItem`s | Items: 1, 20, 25, 30, 60 minutes, Custom... A check mark shows the current one |
| Custom interval | `ContentDialog` with `NumberBox` (min 1, max 1440) | Opens from "Custom..."; label "Enter the interval in minutes:" |
| Reminder message | `TextBox` (single line, `MaxLength=200`) | Header: "Enter the reminder message:". Empty means use default message |
| Schedule | `CheckBox` "Enable schedule" + two `TimePicker`s (`ClockIdentifier="24HourClock"`) | Helper text: "24-hour format. Reminders only fire during this window." Disable the pickers when the checkbox is off |
| Duration | `NumberBox` (5 to 300 s) | Default 20 |
| Overlay template | `ComboBox` (Default, plus every `*.html` found in the templates folder) + "Browse..." and "Open folder" buttons | |
| Preview | `Button` | Calls `EyeCareOverlayService.ShowAsync(...)` |

Save to settings on every change (debounce the message `TextBox` by about 500 ms).

---

## 3. Files and folders

```
/Assets/EyeCare/index.html                         <- default overlay (shipped, read-only; Content, Copy if newer)
%LOCALAPPDATA%\AppLock\EyeCare\templates\          <- user custom overlays (name.html + their images/css/js)
%LOCALAPPDATA%\AppLock\EyeCare\.render\            <- temp rendered output (recreated on each show)

/Services/EyeCareSettings.cs
/Services/EyeCareScheduler.cs
/Services/EyeCareOverlayService.cs
/Views/EyeCareOverlayWindow.xaml(.cs)
/Views/Controls/EyeCareCard.xaml(.cs)              <- the card shown on the Others page
/ViewModels/EyeCareViewModel.cs
```

On first run, copy the shipped `index.html` into `templates\default.html`. This gives users a working example to edit. Keep the built-in one as the fallback.

---

## 4. Settings model

Store in the same settings mechanism the app already uses (JSON file in `%LOCALAPPDATA%\AppLock`). Do not use a new mechanism.

```csharp
public sealed class EyeCareSettings
{
    public bool Enabled { get; set; } = false;
    public int IntervalMinutes { get; set; } = 20;          // 1..1440
    public string Message { get; set; } = "";               // empty => default
    public bool ScheduleEnabled { get; set; } = false;
    public TimeSpan StartTime { get; set; } = new(9, 0, 0);
    public TimeSpan EndTime { get; set; } = new(21, 0, 0);
    public int DurationSeconds { get; set; } = 20;          // 5..300
    public string? TemplatePath { get; set; }               // null => built-in default
    public const string DefaultMessage =
        "Have a look far away from your current screen to protect your eyes";
}
```

Validate on load: clamp ranges, and if `TemplatePath` no longer exists, fall back to the default.

---

## 5. Scheduler (`EyeCareScheduler`)

- Use a single `DispatcherQueueTimer` (or `System.Threading.Timer` marshalled to the UI thread).
- `Start()`: compute next fire = now + `IntervalMinutes`. `Stop()`: cancel the timer.
- On tick:
  1. If schedule is enabled and the current local time is outside `[StartTime, EndTime]`, skip showing and just re-arm the timer. Handle the cross-midnight case: if `Start > End`, inside = `now >= Start || now <= End`.
  2. If an overlay is already visible, skip.
  3. Otherwise call `EyeCareOverlayService.ShowAsync(...)`.
  4. Re-arm the timer for the next interval. Start counting the next interval **after the overlay closes**, so reminders never stack.
- Changing interval, schedule, or the enable toggle restarts the timer immediately.
- Handle system sleep/resume: subscribe to `SystemEvents.PowerModeChanged` (or `Microsoft.Win32.SystemEvents`) and re-arm the timer on resume. Handle session lock/unlock too, and don't show the overlay while the Windows session is locked.
- Start the scheduler from app startup if `Enabled` is true. If AppLock already runs in the tray or background, the scheduler must keep running when the main window is closed.

---

## 6. Overlay window (`EyeCareOverlayWindow`)

A borderless fullscreen WinUI 3 `Window` containing one `WebView2` control.

Behavior:
- Fullscreen and always on top: use `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)`, then `OverlappedPresenter.IsAlwaysOnTop = true` where applicable. No title bar, hidden from taskbar (`AppWindow.IsShownInSwitchers = false`).
- **Multi-monitor**: create one overlay window per display (`DisplayArea.FindAll()`), position each with `AppWindow.MoveAndResize(displayArea.OuterBounds)` and fullscreen. Closing any one closes all.
- Do not steal focus aggressively. Activate once, and do not block system shortcuts.
- Auto close after `DurationSeconds` (hard timeout in C#, independent of the HTML countdown, so a broken custom template can never trap the user).
- Always provide a way out: right-click shows "Close" (already in `index.html`), and also handle `Esc` natively (`KeyDown` on the root element) as a safety exit. This works even if a custom template has no close UI.

WebView2 setup:

```csharp
await WebView.EnsureCoreWebView2Async();
var core = WebView.CoreWebView2;

core.Settings.AreDefaultContextMenusEnabled = false;
core.Settings.AreDevToolsEnabled = false;
core.Settings.IsStatusBarEnabled = false;
core.Settings.IsZoomControlEnabled = false;

// Lets relative assets (images/css/js) in custom templates work.
core.SetVirtualHostNameToFolderMapping(
    "eyecare.local", renderFolder, CoreWebView2HostResourceAccessKind.Allow);

core.WebMessageReceived += (s, e) =>
{
    if (e.TryGetWebMessageAsString() == "close") CloseAll();
};

core.Navigate("https://eyecare.local/rendered.html");
```

Rendering the HTML (placeholder substitution):

1. Read the selected template (custom path, or the shipped `index.html`).
2. Replace placeholders with HTML-encoded values (use `WebUtility.HtmlEncode` for the message):
   - `{{REMINDER_MESSAGE}}` becomes the user message (or the default if empty)
   - `{{DURATION}}` becomes the seconds as an integer
3. Write the result as `rendered.html` into a render folder **next to the template** (or copy the template's folder contents to `.render` first), so relative paths such as `images/eye.png` resolve. Map `eyecare.local` to that folder.
4. If the template contains no `{{REMINDER_MESSAGE}}`, still show it unchanged. Do not treat that as an error.
5. Delete the rendered file when the overlay closes.

---

## 7. Changes needed in `index.html`

The current file was built for pywebview. Update these parts:

**a) Duration placeholder.** Replace the hard-coded value:

```js
let countdown = {{DURATION}};
```

**b) Close bridge.** `window.pywebview.api.close_window()` does not exist in WebView2. Replace `closeWindow()` with:

```js
function closeWindow() {
    if (document.fullscreenElement) document.exitFullscreen();
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage('close');   // handled by C#
    } else {
        window.close();                               // browser fallback for testing
    }
}
```

**c) Auto close.** In `updateCountdown()`, when the countdown reaches 0, call `closeWindow()` instead of `window.close()`.

**d) Remove the JS fullscreen request.** The native window is already fullscreen, so delete the `requestFullscreen()` call (it needs a user gesture and fails anyway).

**e) Message element.** Keep `<div class="title">{{REMINDER_MESSAGE}}</div>`. The text is already HTML-encoded by C#, so `textContent` is safe to reuse.

**f) Small fixes found while reviewing the file:**
- `body` sets `background` twice (the gradient is overridden by `#212121`). Remove the unused gradient line.
- `.eyelashes-upper` CSS exists, but no element uses it. Remove it or add the element.

---

## 8. Custom overlay contract (for `name.html`)

Document this in the card's help text or tooltip so users can make their own overlay:

- Any `.html` file works. Put it in the templates folder or browse to it.
- Supported placeholders: `{{REMINDER_MESSAGE}}` and `{{DURATION}}`.
- To close itself, the page calls `window.chrome.webview.postMessage('close')`. The app also closes the overlay automatically after the duration, and `Esc` always works.
- Relative files (images, CSS, JS) next to the HTML file load normally.
- Remote URLs are blocked by default. If you want to allow them, add a setting rather than allowing by default.
- Do not call native APIs. Only `postMessage('close')` is handled.

Security: custom templates are user-supplied HTML. Keep DevTools off, block navigation away from `eyecare.local` (`core.NavigationStarting` cancels any other host), and cancel `NewWindowRequested`.

---

## 9. Navigation wiring

1. Add `EyeCareCard` to the Others page layout below the Brightness card.
2. Register `EyeCareScheduler` and `EyeCareOverlayService` as singletons (same DI approach the app already uses).
3. Do not add a new sidebar item; Eye Care lives only inside **Others**.

---

## 10. Edge cases

- Custom interval is empty or invalid: keep the previous value and show an inline error.
- Start time equals End time: treat as "always inside" and show a hint.
- WebView2 Runtime missing: show an `InfoBar` ("WebView2 Runtime is required") and fall back to a simple native overlay window with the message and a Close button.
- Template file deleted or unreadable: fall back to the built-in default and show an `InfoBar`.
- Overlay requested while a fullscreen game or video is running: still show it (user choice), but add an optional setting "Don't show over fullscreen apps" using `SHQueryUserNotificationState` (`QUNS_BUSY` / `QUNS_RUNNING_D3D_FULL_SCREEN`) as a future enhancement.
- Preview while the scheduler is running does not reset the timer.

---

## 11. Acceptance checklist

- [ ] Eye Care card appears in **Others** and settings persist after restart.
- [ ] Interval presets (1, 20, 25, 30, 60) and Custom work; the current one shows a check mark.
- [ ] Custom message appears on the overlay; empty message shows the default.
- [ ] Schedule limits reminders to the chosen window, including past midnight.
- [ ] Overlay is fullscreen on all monitors, always on top, and closes after the duration.
- [ ] Right-click "Close" and `Esc` both close the overlay.
- [ ] Choosing `name.html` as the template uses it, with relative assets working.
- [ ] Reminders resume after sleep and unlock, and the next interval starts after the overlay closes.
- [ ] Disabling the toggle stops all reminders immediately.
