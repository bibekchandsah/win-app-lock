# AppLock for Windows — Build Instructions for AI Agent

You are building **AppLock**, a Windows 10/11 desktop app that locks selected applications/windows behind a password (or Windows Hello). Build it step by step, compile and run after every milestone, and fix errors before moving on.

## 1. Tech Stack (do not change)

- **Language:** C# 12, **.NET 8** (LTS)
- **UI:** **WinUI 3 (Windows App SDK 1.5+)**, Fluent/Mica dark theme, packaged or unpackaged (prefer *unpackaged, self-contained* so it runs without MSIX signing)
- **Pattern:** MVVM using `CommunityToolkit.Mvvm`
- **Win32 interop:** `CsWin32` source generator (preferred) or manual P/Invoke
- **Storage:** JSON settings file in `%APPDATA%\AppLock\`, secrets protected with DPAPI (`ProtectedData`, CurrentUser scope)
- **Tray icon:** `H.NotifyIcon.WinUI`
- **Password hashing:** PBKDF2 (`Rfc2898DeriveBytes`, SHA-256, 200k+ iterations, random 16-byte salt) or Argon2id. Never store plaintext.
- **Windows Hello:** `Windows.Security.Credentials.UI.UserConsentVerifier`
- **Installer (final step):** Inno Setup script or `dotnet publish` single-folder output

## 2. Features

### 2.1 Main window (matches the preview screenshots)
- Left navigation rail with icons: **Protection** (lock), **Rules/Profiles** (placeholder), **Websites** (globe, phase 2), and at the bottom **Feedback** and **Settings**.
- **Top status card:** a master **toggle switch** (enable/disable AppLock protection), a green status dot, and text such as "Admin password verified — AppLock is protecting your apps." When locked: "Locked: <window title>".
- Two tabs: **Running programs** and **Locked programs**.
- **Running programs tab:** list of visible top-level windows showing icon, window title, process name, and a lock button on the right. A search box filters by process name or window title. A refresh button reloads the list. A **"Pick a window"** button lets the user click any on-screen window to select it. A badge next to it shows the count of currently locked items.
- **Locked programs tab:** list of locked rules with unlock/remove button, and the rule type (by process, by window title, by picked window).
- Dark theme, rounded cards, purple/lilac accent (#D9A0E8-ish), Mica backdrop.

### 2.2 Locking behavior
1. Lock rules can match by: **process name/exe path** (default), **window title contains/regex**, or a **picked window** (resolve to its process + title at pick time).
2. When protection is ON and a window matching a rule appears or gets focus, show a **lock overlay** that fully covers that window (see 2.3) until the user authenticates.
3. Master toggle OFF → all overlays removed, hooks paused, nothing is locked. Toggling it back ON or turning it OFF **requires admin password verification** (dialog: "Verify admin password — Enter your admin password to unlock and continue", with password box, **Windows Hello** button, Cancel and OK).
4. Re-lock rules: after a successful unlock the user chooses **Time allowed for this unlock**: *No limit*, 15 min, 30 min, 60 min, *Custom*. When the time expires, the app auto-relocks (overlay returns).
5. Closing a locked app window and reopening it must lock again (unless an unlock timer is still active).
6. Already-locked message on overlay: `Program "<window title>" is already locked.` with subtitle "Enter the app password to resume".
7. Overlay buttons: password box, time-allowed segmented selector, **Unlock**, **Cancel** (minimizes the target window), **Windows Hello**, footer with "Emergency lock: Ctrl+Alt+L" and a **"Report wrong lock"** link (removes the rule / adds an exclusion after confirming).
8. Failed attempts: after N wrong attempts (default 5), lock out for **Lockout duration** minutes (setting, default 10).

### 2.3 Window detection and overlay (core technical part)
- Enumerate windows with `EnumWindows`; keep only visible, non-cloaked (`DwmGetWindowAttribute(DWMWA_CLOAKED)`), non-tool windows with a title; skip AppLock itself. Get process via `GetWindowThreadProcessId` + `QueryFullProcessImageName`. Get icon from the exe.
- Detect new/changed windows **event-driven**, not by heavy polling: `SetWinEventHook` for `EVENT_OBJECT_SHOW`, `EVENT_OBJECT_NAMECHANGE`, `EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE`, `EVENT_SYSTEM_MINIMIZEEND`, `EVENT_OBJECT_DESTROY` (out-of-context, `WINEVENT_OUTOFCONTEXT`). Add a 1–2 second safety timer as fallback.
- **Pick a window:** minimize/hide AppLock, show a transparent full-screen capture overlay, highlight the window under the cursor (`WindowFromPoint` → `GetAncestor(GA_ROOT)`) with a colored border, click to select, Esc to cancel.
- **Overlay implementation:** a borderless, topmost WinUI/Win32 window positioned over the target window's rect (`GetWindowRect`/`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`), following it on move/resize via LOCATIONCHANGE events. Setting "Keep lock screen on top": ON = overlay is `HWND_TOPMOST` above everything; OFF = it only covers the locked window (Z-order just above the target) so other apps can be placed over it.
- Also hide the target's content from capture/Alt-Tab previews: set the **taskbar thumbnail/preview to a "Locked" image** using `DwmSetWindowAttribute` with `DWMWA_FORCE_ICONIC_REPRESENTATION` and `DWMWA_HAS_ICONIC_BITMAP`, supplying a dark bitmap with the word "Locked" via `DwmSetIconicThumbnail` and `DwmSetIconicLivePreviewBitmap` (handle `WM_DWMSENDICONICTHUMBNAIL` / `WM_DWMSENDICONICLIVEPREVIEWBITMAP`). If this proves unreliable on the target window, fall back to the overlay only.
- Block keyboard focus from reaching the locked window while the overlay is active (overlay takes foreground; re-assert if the target steals focus).
- Handle multi-monitor, DPI scaling (per-monitor v2), and UWP/ApplicationFrameHost windows (resolve the real app via child `Windows.UI.Core.CoreWindow` or the app's AUMID) — the screenshot shows entries like WhatsApp and Settings running under ApplicationFrameHost.

### 2.4 Settings page (matches screenshot)
Collapsible sections:
- **General:** Language (System default + a few), Theme (System default/Light/Dark), **Start with Windows** (toggle; use `HKCU\...\Run` or a Scheduled Task with highest privileges), **Mute notifications** (silences in-app tips).
- **Security:** **Change admin password** button, **Security Questions** button (set/answer 2–3 questions for password recovery; store answers hashed), **Lockout duration (minutes)** numeric box, **Windows Hello unlock** toggle (hidden/disabled if unsupported).
- **Auto & Emergency Lock:** **Auto-lock when idle** (minutes, 0 disables) — hides the main window and requires the admin password again after no keyboard/mouse activity (use `GetLastInputInfo`); **Emergency lock hotkey** toggle with a **Change** button to rebind (default **Ctrl+Alt+L**, register with `RegisterHotKey`; pressing it instantly re-locks all protected apps and ignores unlock timers); **Keep lock screen on top** toggle.
- **Other:** **Reset all prompts** button, **About** button.

### 2.5 First run
- Welcome screen: create **admin password** (min 6 chars, confirm), optionally set security questions, optionally enable Windows Hello.
- Every later launch (and after idle auto-lock) requires the admin password or Windows Hello to open the main window.

### 2.6 Tray behavior
- Closing the window minimizes to tray; protection keeps running. Tray menu: Open, Enable/Disable protection (asks for auth), Emergency lock, Exit (asks for auth).

### 2.7 Websites page (phase 2, optional)
Globe icon: lock/block chosen websites. Implement by matching browser window titles/URLs for selected browsers, showing the same overlay. Do not edit the hosts file. Do this only after everything above works.

## 3. Project structure

```
AppLock/
  AppLock.sln
  src/AppLock/
    App.xaml(.cs)
    Views/        MainWindow, ProtectionPage, SettingsPage, LockOverlayWindow, PickWindowOverlay, AuthDialog, FirstRunPage
    ViewModels/   MainViewModel, RunningProgramsViewModel, LockedProgramsViewModel, SettingsViewModel
    Services/
      WindowEnumerator.cs
      WinEventHookService.cs
      LockManager.cs          // rules, state, timers, relock logic
      OverlayManager.cs       // one overlay per locked window
      WindowPicker.cs
      AuthService.cs          // password hash, lockout, security questions
      HelloService.cs         // Windows Hello
      HotkeyService.cs
      IdleService.cs
      StartupService.cs
      SettingsStore.cs        // JSON + DPAPI
      ThumbnailService.cs     // DWM iconic thumbnail "Locked"
    Models/       LockRule, RunningWindow, AppSettings
    Native/       NativeMethods.txt (CsWin32) + helpers
    Assets/
  tests/AppLock.Tests/
  README.md
```

## 4. Milestones (build, run, verify each before continuing)

1. Solution scaffold, WinUI 3 window with Mica, nav rail, dark theme, empty tabs.
2. Window enumeration + Running programs list + search + refresh + icons.
3. Settings store, first-run password setup, AuthService (PBKDF2), Verify-password dialog.
4. Lock rules (add/remove), Locked programs tab, master toggle with auth.
5. WinEventHook service + overlay window covering a locked app, follows move/resize, unlock flow with time-allowed options and auto-relock.
6. "Pick a window" tool.
7. Settings page fully wired (all options in 2.4), hotkey, idle auto-lock, lockout, security questions, Windows Hello, start with Windows.
8. Tray icon, DWM "Locked" taskbar thumbnail, UWP window handling, multi-monitor/DPI polish.
9. Tests for AuthService, rule matching, lockout; manual test checklist; installer/publish.
10. (Optional) Websites page.

## 5. Security requirements

- No plaintext passwords or security answers anywhere (disk, logs, memory longer than needed). Clear password boxes after use.
- Use constant-time comparison for hash checks.
- Settings file must be tamper-resistant: DPAPI-protect the hash blob and add an HMAC over the rule list.
- Be honest about limits: a standard-user process can be killed from Task Manager. Mitigations to implement: (a) optional elevated/auto-start via Scheduled Task, (b) a small **watchdog** process that restarts AppLock if it is terminated, (c) optionally require admin rights to exit via tray. Document these limits in the README; do not claim it is unbreakable.
- Never send any data over the network. No telemetry.
- Do not hook or log keystrokes beyond what is needed for the idle timer and the hotkey.

## 6. Quality rules for the agent

- After each milestone: `dotnet build -c Release`, run the app, and list what you verified.
- Prefer small commits with clear messages (`git init` at start).
- Handle exceptions around every Win32 call; never crash the lock service — log to `%APPDATA%\AppLock\logs`.
- Keep UI thread free; marshal hook callbacks to the dispatcher queue.
- Keep hook delegates in fields so the GC doesn't collect them.
- If a requirement is infeasible or a Windows API behaves differently than described, choose the closest working approach, explain why in `NOTES.md`, and continue.
- Final deliverables: working source, `README.md` (build/run/usage), `NOTES.md` (decisions and limitations), published build in `dist/`.

## 7. Manual test checklist

- [ ] First run creates password; wrong password rejected; lockout after N failures for the set minutes
- [ ] Lock Notepad by process; open it → overlay appears; unlock with 15 min → relocks after 15 min
- [ ] Lock by window title ("Instagram Call"); lock a picked window
- [ ] Lock WhatsApp / Settings (UWP) correctly
- [ ] Master toggle OFF removes all overlays, ON restores them; both ask for auth
- [ ] Overlay follows window move/resize/maximize and works on a second monitor and at 150% DPI
- [ ] Taskbar hover preview shows "Locked"
- [ ] Ctrl+Alt+L instantly locks everything; rebinding works
- [ ] Idle auto-lock hides main window and requires password
- [ ] Windows Hello unlock works and hides cleanly if unsupported
- [ ] Start with Windows works; tray menu works; app survives sleep/resume and explorer restart
