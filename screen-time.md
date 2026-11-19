# Screen Time Tracker — Add-on Spec for AppLock

This is an add-on to `instructions.md`. Read that file first, then read `self-discipline.md` (this feature feeds it). Implement on top of the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure (CommunityToolkit.Mvvm), the same services (shared toast service, admin password / Windows Hello verification, settings JSON with DPAPI/HMAC), and the same theme rules (Light / Dark / System via theme resources only, no hard-coded colors). Build, run, and verify before reporting.

## 1. What this feature is

A **Screen Time** tab on the **Protection** page that shows how long the user actually spent **in the foreground of each app**, with daily and weekly charts, and lets the user set a **daily limit per app**. A limit is not a second, separate system: it is stored as the app's **Self-Discipline daily quota** and enforced by the existing lock overlay ("Daily limit reached… enter the password to extend time").

So the rule is: **Screen Time measures and displays. Self-Discipline enforces. They share one counter.**

Out of scope for this spec (phase 2, keep hooks only): website tracking (needs the Websites feature), category/group limits, total-PC daily limit, per-day-of-week limits, weekly email/report, multi-PC sync.

## 2. Entry point (UI placement)

The Protection page currently has two tabs: **Running Applications** and **Protected Applications**. Add a **third tab: "Screen Time"** with icon `` (Chart) or `` (Recent). Keep the same tab-strip style as the existing two tabs. The master protection toggle stays above the tabs and is unchanged.

Layout of the Screen Time tab, top to bottom:

1. **Range bar:** segmented control **Today | Week | Month** on the left; on the right a **◀ date ▶** stepper (previous/next day or week or month; next is disabled at the current period) and a **⋯ menu** (Export CSV, Settings, Clear data).
2. **Summary row (3 cards):**
   - **Total screen time** for the range (e.g. `4h 12m`), with a delta line: `▲ 18% vs yesterday` / `▼ 7% vs last week` (hide the delta if the previous period has no data).
   - **Daily average** (Week/Month only; for Today show **First active / Last active**, e.g. `08:41 – 22:15`).
   - **Most used app** with its icon, name and time.
3. **Chart card** (see section 6).
4. **App list** for the range, sorted by time descending (see section 7).
5. A small status line at the bottom: `Tracking is on · Data stays on this PC · 90 days kept` with a link **Settings**. When tracking is off, show a banner with a **Turn on** button instead.

Empty state (first run, no data yet): chart icon, text *"Screen time will appear here as you use your PC. Only the app in front counts, and only while you are active."*, and a **Turn on tracking** button if tracking is off.

## 3. First-run consent

Tracking is **on by default but explained once**. On first opening the tab show a ContentDialog:

*"AppLock can record which app is in front and for how long, so you can see your screen time and set limits. This is stored only on this PC. Window titles and website addresses are not recorded."* Buttons: **Turn on** (accent) / **Not now**. "Not now" leaves tracking off and shows the banner. Self-Discipline quotas keep working for apps that already have a quota even when Screen Time tracking is off (they use the same sampler, see section 5.6); only history/charts are disabled.

## 4. Data model

### 4.1 Settings (inside the existing protected settings JSON)

```csharp
public class ScreenTimeSettings
{
    public bool TrackingEnabled { get; set; }                 // set by consent dialog
    public bool ConsentShown { get; set; }
    public int IdlePauseSeconds { get; set; } = 60;           // 30..900; SHARED with Self-Discipline quota
    public bool CountWhileMediaPlaying { get; set; } = true;  // video/music playing = active even without input
    public int RetentionDays { get; set; } = 90;              // 30, 90, 365, 0 = keep forever
    public bool RememberWindowTitles { get; set; } = false;   // off by default, see section 11
    public List<string> ExcludedAppKeys { get; set; } = new();// not tracked at all (default list in 5.4)
    public List<string> HiddenAppKeys { get; set; } = new();  // tracked + counted for limits, hidden from lists/charts
    public bool LimitWarningsEnabled { get; set; } = true;    // toasts at 80%, 95% / 5 min left, and at the limit
    public bool ShowHourlyDetail { get; set; } = true;
}
```

Migrate older settings files without errors (all new properties get defaults).

### 4.2 Extension of `LockRule` (the link to Self-Discipline)

```csharp
public enum LockRuleMode
{
    AlwaysLocked,   // current behavior: locked whenever opened (until unlocked)
    LimitOnly       // runs freely; locks ONLY when quota/schedule/idle rules trigger
}

// add to LockRule
public LockRuleMode Mode { get; set; } = LockRuleMode.AlwaysLocked;
public string AppKey { get; set; } = "";  // same identity key used by Screen Time (section 5.3)
```

Existing rules migrate to `AlwaysLocked`. `AppKey` is filled lazily for existing rules the first time the process image path is known.

### 4.3 Usage history (SQLite)

Usage history is a time series, so it does **not** go in the settings JSON. Use **SQLite** via `Microsoft.Data.Sqlite` (the only new package), file `%LOCALAPPDATA%\AppLock\screentime.db`, `PRAGMA journal_mode=WAL`, `user_version` for migrations.

```sql
CREATE TABLE app (
  id            INTEGER PRIMARY KEY,
  app_key       TEXT NOT NULL UNIQUE,      -- identity, see 5.3
  display_name  TEXT NOT NULL,
  exe_path      TEXT,
  aumid         TEXT,                      -- packaged apps
  first_seen    INTEGER NOT NULL,          -- unix seconds UTC
  icon_png      BLOB                       -- cached 64x64, optional
);

CREATE TABLE usage_hour (                  -- aggregated per app per local hour
  day      TEXT    NOT NULL,               -- 'YYYY-MM-DD' local date
  hour     INTEGER NOT NULL,               -- 0..23 local
  app_id   INTEGER NOT NULL REFERENCES app(id),
  seconds  INTEGER NOT NULL,
  opens    INTEGER NOT NULL DEFAULT 0,     -- number of times it became foreground
  PRIMARY KEY (day, hour, app_id)
);

CREATE TABLE coverage_hour (               -- how long tracking was actually running
  day TEXT NOT NULL, hour INTEGER NOT NULL,
  tracked_seconds INTEGER NOT NULL,        -- includes idle time; 3600 = fully covered
  PRIMARY KEY (day, hour)
);

CREATE TABLE window_title (                -- ONLY used when RememberWindowTitles = true
  day TEXT NOT NULL, app_id INTEGER NOT NULL, title TEXT NOT NULL, seconds INTEGER NOT NULL,
  PRIMARY KEY (day, app_id, title)
);
```

Aggregation per hour keeps the DB tiny (about 24 × apps rows per day). Do not store raw per-second samples.

Writes go through one `ScreenTimeStore` class with a single writer (a `Channel<UsageDelta>` drained by a background task), batch-flushed every 30 s and on exit/shutdown/sleep. Reads are cheap queries on the aggregated tables. All SQL is parameterized.

## 5. Tracking engine

### 5.1 The sampler: `ForegroundUsageSampler`

One shared service for **both** Screen Time and Self-Discipline's quota counting. There must be **no second timer** anywhere that measures foreground time.

- Detect foreground changes with `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)` (also listen to `EVENT_OBJECT_NAMECHANGE` only if titles are being remembered). Keep the callback tiny: capture `hwnd` and a `Stopwatch` timestamp, push to a channel, return.
- A **1-second tick** is also running, used to (a) close the current slice on idle/lock, (b) feed the quota tracker, (c) roll buckets at hour/day boundaries.
- Use a **monotonic clock** (`Stopwatch.GetTimestamp`) for durations. Use wall-clock local time **only** to choose the bucket (day, hour). Wrap the clock in the existing `IClock` abstraction so it is testable.
- A "slice" is `(appKey, startMono, endMono)`. When a slice crosses an hour or midnight boundary it is split so each part lands in the correct `(day, hour)` bucket.
- The sampler raises `UsageTicked(appKey, deltaSeconds, isActive)` once per second for the current foreground app. `QuotaTracker` (Self-Discipline) subscribes to this event instead of running its own timer, so the number shown in the Self-Discipline ring and the number shown in Screen Time for the same app are **always equal**.

### 5.2 What counts as "active"

A second counts for the foreground app only if **all** are true:

1. The user session is unlocked and the display is on (listen to `SystemEvents.SessionSwitch` for lock/unlock, `PowerModeChanged` for suspend/resume, and `WTS_SESSION_LOCK` / display-off).
2. The foreground window belongs to a normal visible app (not an excluded app, not the desktop/shell, not the lock screen).
3. The user is **not idle**: `GetLastInputInfo` idle time < `IdlePauseSeconds` (default 60 s, the same threshold used by Self-Discipline's quota), **or** `CountWhileMediaPlaying` is on and media is playing (see 5.5).

When any condition fails, the slice is closed and no time is added until it holds again. Idle time is still recorded in `coverage_hour.tracked_seconds` so gaps in tracking can be told apart from genuinely idle time.

> This intentionally changes `self-discipline.md` section 3 slightly: the hard-coded "no input for 60 s+ pauses counting" becomes the shared `IdlePauseSeconds` setting whose **default is still 60 s**. Update that code to read the shared setting.

### 5.3 App identity (`AppKey`)

The same app must always map to the same key, regardless of how many windows/processes it has.

- **Win32 apps:** key = `exe:` + lower-cased full image path from `QueryFullProcessImageName` (open the process with `PROCESS_QUERY_LIMITED_INFORMATION`; this works for most elevated processes too).
- **Packaged/UWP apps:** windows owned by `ApplicationFrameHost.exe` are hosts. Find the real app by enumerating the host window's child windows for the one with a different process ID, then read its **AUMID** (`GetApplicationUserModelId`). Key = `aumid:` + AUMID. Display name and icon come from the package (`Windows.ApplicationModel` / `PackageManager` or the AUMID's start-tile info).
- **Display name:** `FileVersionInfo.FileDescription` → `ProductName` → exe file name without extension. Packaged apps use the package display name.
- **Icon:** extract once (`SHGetFileInfo` / `IShellItemImageFactory` 64×64 → PNG), cache in the `app.icon_png` column, never re-extract per tick.
- If the process cannot be opened (protected processes, access denied) use key `proc:` + process name and display name "Protected app (name)". Never crash and never drop the time silently.
- Self-Discipline's rule matching continues to use its existing matching logic; `LockRule.AppKey` is just set to this key so the two features refer to the same app.

### 5.4 Default exclusions

Not tracked and not shown (user can edit the list in Settings, shown with a note): AppLock itself, `LockApp.exe`, `LogonUI.exe`, `explorer.exe` **only when the foreground window is the desktop/taskbar/Start/Search** (File Explorer windows are tracked as "File Explorer"), `SearchHost.exe`, `StartMenuExperienceHost.exe`, `ShellExperienceHost.exe`, `TextInputHost.exe`, `Taskmgr.exe` is **not** excluded. Excluded apps don't count toward limits either; the list in Settings says so.

### 5.5 Media-playing exception

When `CountWhileMediaPlaying` is on, the user watching a video without touching the keyboard is not idle. Use `GlobalSystemMediaTransportControlsSessionManager` (WinRT): if any session reports `PlaybackStatus == Playing`, treat the foreground app as active **only if it is the foreground window** and the session's source app matches the foreground app (or the foreground app is a browser, since browsers report as their own session). If the media APIs fail or are unavailable, silently fall back to plain idle detection.

### 5.6 Sharing with Self-Discipline (important integration rules)

- `QuotaTracker` stops owning a timer; it consumes `UsageTicked`. For a rule with quota enabled, `UsedSecondsToday` (protected settings) is incremented by the **same** delta that is written to `usage_hour`.
- **The quota counter in the protected settings is the source of truth for enforcement. The SQLite history is only for display.** Deleting or editing `screentime.db` must never reset or reduce a quota. (The DB can be tampered with by the same Windows user; the HMAC-protected settings are the stronger store.)
- If tracking history is off (`TrackingEnabled = false`), the sampler still runs for apps that have an enabled quota, and writes only to the quota counter, not to the DB.
- Midnight rollover, extend-time handling, and "lowering the limit below used time locks immediately" stay exactly as defined in `self-discipline.md`.
- **Clock tampering:** keep `LastSeenLocalDate` and a monotonic/wall-clock sanity pair in the protected settings. If the wall clock jumps backwards (date earlier than `LastSeenLocalDate`), do **not** start a fresh day and do **not** reset any quota: keep counting into the previous day's counter until real time (by monotonic clock + last known wall time) passes midnight. Log a security event and show it in the existing activity log.

## 6. Charts

Do **not** add a charting dependency. Draw the charts with XAML (`ItemsControl` + `Rectangle`/`Border` bars in a `Grid`, `Canvas` for the average line). This keeps theming, animation, scaling and accessibility under our control and avoids a heavy native dependency. Wrap them in a reusable `UsageBarChart` UserControl with a typed `IReadOnlyList<BarSeriesPoint>` source.

### 6.1 Today view (hourly)

- 24 bars (00–23) of foreground minutes per hour, max height = 60 minutes (or the busiest hour if scale auto-fit is simpler; the Y axis shows `0`, `30m`, `60m`).
- Each bar is **stacked by the top 3 apps of that hour + "Other"** using the app colors in 6.4.
- Hover/tap tooltip: `14:00 – 15:00 · 47m` and the top 3 apps with times. Keyboard focusable (arrow keys move between bars), with `AutomationProperties.Name` like "14:00 to 15:00, 47 minutes, mostly Chrome".
- Hours with **incomplete tracking** (`coverage_hour.tracked_seconds < 3300`) show a thin hatched strip at the bar base and the tooltip adds "AppLock was not running for part of this hour." Future hours of today are drawn empty and dimmed.

### 6.2 Week view (7 days)

- 7 stacked bars, one per day (Mon–Sun or the system's first day of week), labeled with weekday letters and the date in the tooltip. Y axis auto-scales to the nearest hour above the max.
- A **dashed horizontal line = daily average** of the days that have data. If any app in the list has a limit, no extra line is drawn (limits are per app, shown in the list).
- Clicking a bar **filters the app list below to that day** (chip appears: `Showing Tue 17 Nov  ✕`). Clicking again or the ✕ returns to the whole week.
- Days with no tracking show a faint empty bar with tooltip "No data".

### 6.3 Month view

- A calendar-style heat strip (7 columns × up to 6 rows) where each cell's accent opacity (4 steps) represents total time that day; tooltip shows the total; click selects that day and switches to Today view for it. Keep it simple; no stacking.

### 6.4 Colors and theming

- The chart must be readable in Light, Dark, and High Contrast. Use theme brushes only (`AccentFillColorDefaultBrush`, `TextFillColorSecondaryBrush`, `CardStrokeColorDefaultBrush`, `ControlStrongStrokeColorDefaultBrush`, etc.).
- Per-app color: choose from a **fixed palette of 8 theme-aware colors** (define light and dark variants as theme resources, each at least 3:1 against the card background) by a **stable hash of `AppKey`**, so an app keeps its color across days. "Other" always uses the neutral gray. Never rely on color alone: the list below shows names and times, and the tooltip names apps.
- Animate bar heights on range change (200 ms ease-out). Respect the system "reduce animations" setting (`UISettings.AnimationsEnabled`).

## 7. App list

A card list below the chart, sorted by time for the selected range:

| Element | Detail |
|---|---|
| Icon + name | From `app` table; packaged apps use the package icon |
| Time | `2h 05m`; under one minute shows `<1m` |
| Share bar | Thin bar showing the share of the range total, in the app's color |
| Opens | `12 opens` (hidden in Week/Month to keep rows clean, shown in the detail pane) |
| Limit chip | e.g. `⏱ 1h 30m / day` with a tiny progress fill; turns warning-colored above 90%, error-colored when reached. Only for **Today** view (in Week/Month the chip just shows the limit value). |
| ⋯ menu | **Set limit…**, **Edit limit** / **Remove limit** (replaces "Set limit" when one exists), **Lock this app** (adds to Protected Applications as `AlwaysLocked`), **Hide from lists**, **Exclude from tracking** |

- Search box above the list ("Search apps") and a "Show hidden apps" toggle in the ⋯ menu of the tab.
- Show the top 15 apps and a **"Show all (N)"** button.
- Clicking a row opens a **detail pane** (right-side flyout or in-place expansion): the app's 7-day bar chart, today's hourly bars, total/average/opens/longest session, limit controls, and (only if enabled) top window titles.

## 8. Limits (the link into Self-Discipline)

### 8.1 Set limit flyout

Opened from the row menu or the limit chip. A small flyout/dialog titled **"Daily limit for <App>"**:

- Preset chips: **15 m · 30 m · 1 h · 2 h · 3 h · Custom**, plus a **NumberBox** in minutes (1..1440, same as Self-Discipline).
- Text: *"When the limit is reached, <App> is locked until you enter the AppLock password. Only time in front, while you are active, is counted."*
- Buttons: **Save limit** (accent) / **Cancel**. A link **"More options (schedule, idle relock)…"** opens the full Self-Discipline dialog for that app.

### 8.2 What Save does

- If the app **already has a lock rule**: set `SelfDiscipline.QuotaEnabled = true` and `DailyLimitMinutes = value`.
- If the app has **no rule**: create a `LockRule` with `Mode = LimitOnly`, `AppKey` set, matching by executable path (or AUMID), no custom password, and the same quota fields. It appears in **Protected Applications** with a small badge **"Limit only"** and the stopwatch icon; it does not lock on launch.
- **Remove limit:** `QuotaEnabled = false`; if the rule is `LimitOnly` and has no schedule/idle rules left, delete the rule entirely.
- Changing a `LimitOnly` rule to locked: the **Lock this app** menu item sets `Mode = AlwaysLocked`.

### 8.3 Authentication (anti-bypass), same rules as Self-Discipline §6

- Opening the Screen Time tab needs no extra auth beyond the existing session.
- **Setting a new limit or lowering a limit** needs no re-authentication.
- **Removing a limit or raising it** requires admin password / Windows Hello, and is **blocked entirely while that app is currently locked by the quota or inside a scheduled period** (same "cannot weaken during an active period" rule).
- **Exclude from tracking** on an app that has an enabled limit is blocked (otherwise it is a loophole); the menu item is disabled with a tooltip "Remove the limit first".
- **Clear data** and **Change retention to a shorter period** need auth (see section 10). Clearing history does **not** clear quota counters.

### 8.4 When the limit is reached

Use the **existing Self-Discipline overlay and extend-time flow unchanged**. Screen Time only adds:

- Warning toasts via the shared custom toast service (when `LimitWarningsEnabled`): at **80%** used (`"You've used 80% of today's limit for Chrome (24 min left)"`), and at **5 minutes left**, and when it is reached. One toast per threshold per app per day; never repeated after a restart the same day (persist "warned" flags with `QuotaDate`).
- In the app list, a reached limit shows an error-colored chip `Limit reached`.
- If an app is unlocked with extra time, the chip shows `+30m extra`.

## 9. Behavior details and edge cases

- **Several windows of one app** count once (the foreground one); two windows of the same app are not additive.
- **Alt-Tab / switching** closes the slice for the old app and opens one for the new app in the same event; sub-second switches (< 1 s) are discarded to avoid noise.
- **Elevated or protected foreground window**: use the fallback key from 5.3; time still counts.
- **Multiple monitors/virtual desktops**: only the foreground window counts, regardless of monitor.
- **Fullscreen games / UWP fullscreen**: `EVENT_SYSTEM_FOREGROUND` still fires; verify with a fullscreen app during the manual checklist.
- **RDP / fast user switching:** pause when the session is disconnected or locked.
- **Sleep/hibernate/resume:** close the slice on suspend; do not count the time asleep. On resume re-evaluate the date (midnight might have passed).
- **Crash/force-kill:** at most the last 30 s are lost because the store flushes every 30 s. On next start, never "extend" the last slice across the downtime.
- **DST and time zone change:** buckets use local date/hour at the moment of the slice; on a DST fall-back day the repeated hour merges into the same `(day, hour)` row (acceptable; the 60-minute axis cap is only a visual scale). Test it.
- **Master protection toggle OFF**: tracking history continues (it is independent), but limits/quotas stop being enforced, exactly like the rest of Self-Discipline. The tab shows a subtle info bar: *"Protection is off. Limits are paused; screen time is still being recorded."*
- **AppLock not running:** nothing is recorded (no background service in v1). The hatched coverage gap makes this visible. State this plainly in Settings: *"Screen time is only recorded while AppLock is running."* Offer the existing "Start with Windows" option inline if it is off.

## 10. Settings (⋯ → Settings, a ContentDialog or a section in the existing Settings page)

- **Tracking** ToggleSwitch (off → stop recording; history is kept).
- **Count as idle after:** ComboBox 30 s / 1 / 2 / 5 / 10 / 15 min (default 1 min). Note: *"Also used by daily limits."*
- **Keep watching videos and listening as active** ToggleSwitch (`CountWhileMediaPlaying`).
- **Keep history for:** 30 days / 90 days / 1 year / Forever. Shortening requires auth and deletes older rows on confirmation. A daily prune job runs at startup and once per 24 h.
- **Remember window titles** ToggleSwitch, off, with the warning *"Titles can contain private information such as document names. They are stored only on this PC."* Turning it off deletes stored titles after confirmation.
- **Excluded apps** list (add by picking from running apps or browsing to an .exe; remove with auth when the app has a limit).
- **Hidden apps** list.
- **Limit warnings** ToggleSwitch.
- **Export CSV** (day, hour, app, seconds) with a SaveFileDialog; **Clear all screen time data** (auth + a confirm dialog that states it does not reset today's limits).

All strings go through the localization resource file.

## 11. Privacy and security

- Everything is local. **No network calls, no telemetry.**
- Window titles and URLs are **not** recorded unless the user explicitly enables titles. Browser tabs and websites are not separated in v1 (the browser is one app).
- The DB lives in the per-user `%LOCALAPPDATA%\AppLock\` folder with default user-only ACLs. Do not claim it is tamper-proof; enforcement uses the HMAC-protected counters (section 5.6).
- **Export** writes only what the user sees (no hidden-app data unless "Show hidden apps" is on) and includes a header row with the export date.
- Honest limits, to show in the info text and to repeat in the final report to the user: it is not parental-control grade. A user with admin rights can stop AppLock, edit the clock, or uninstall it. Quotas are protected as strongly as the existing AppLock settings, no more.

## 12. Project structure (additions)

```
AppLock/
  Services/ScreenTime/
    ForegroundUsageSampler.cs       // hook + 1 s tick, UsageTicked event
    AppIdentityResolver.cs          // AppKey, display name, UWP resolution, icon
    ActivityGate.cs                 // lock/sleep/idle/media rules (5.2, 5.5)
    ScreenTimeStore.cs              // SQLite, single writer, queries, prune, export
    ScreenTimeQueries.cs            // day/week/month aggregation DTOs
    UsageClockGuard.cs              // clock tamper handling (5.6)
  Models/
    ScreenTimeSettings.cs
    UsageModels.cs                  // BarSeriesPoint, AppUsageRow, DayTotal
  ViewModels/
    ScreenTimeViewModel.cs
    AppUsageRowViewModel.cs
    LimitFlyoutViewModel.cs
  Views/
    ScreenTimeTab.xaml(.cs)
    Controls/UsageBarChart.xaml(.cs)
    Controls/UsageCalendarHeat.xaml(.cs)
    Dialogs/ScreenTimeConsentDialog.xaml
    Dialogs/ScreenTimeSettingsDialog.xaml
    Dialogs/SetLimitFlyout.xaml
    Dialogs/AppUsageDetailPane.xaml
```

Modify: `QuotaTracker` (consume `UsageTicked`), `LockRule` (`Mode`, `AppKey`), Protected Applications list (the "Limit only" badge), settings migration, the Protection page tab strip.

## 13. Tests and manual checklist

Unit tests (with fake `IClock`, fake foreground provider, in-memory SQLite):

- Slice splitting across hour boundary, midnight boundary, and DST fall-back day.
- Idle pause at exactly the threshold; resume counts again; media-playing exception on/off.
- Sub-second switch is discarded; switching apps closes and opens slices correctly.
- Same app with several windows counts once.
- Quota counter increments by the same delta as the DB row (equality test), and deleting the DB does not change the quota.
- Wall clock moved backwards does not reset the quota (5.6).
- Retention prune, CSV export format, settings migration from the old JSON.
- `LimitOnly` rule: does not lock on launch, locks at the limit, rule is deleted on Remove limit when nothing else is configured.

Manual checklist:

- [ ] Protection page shows a third tab "Screen Time"; first open shows the consent dialog
- [ ] Use Chrome for 2 minutes with input: it appears in the list with about `2m`; switch to Notepad: Chrome stops and Notepad starts
- [ ] Stop touching input for 1 minute: counting pauses; play a video with the media option on: counting continues
- [ ] Lock the PC (Win+L) for a minute and unlock: the locked time is not counted
- [ ] Today view shows the current hour's bar growing; hovering shows the apps
- [ ] Week view shows 7 bars and the average line; clicking a bar filters the list
- [ ] Set limit 1 minute on an unprotected app: it gets a "Limit only" rule; it opens normally and locks after about 1 minute; extending works
- [ ] The Self-Discipline ring and the Screen Time chip for that app show the same time
- [ ] Remove limit needs the password; it is blocked while the app is locked by the quota
- [ ] Delete `screentime.db` while the app is closed and reopen: history is empty but the app's quota did not reset
- [ ] Change the system clock back by a day: quota does not reset, an event is logged
- [ ] Light, Dark, System theme and High Contrast all look correct; chart is keyboard navigable
- [ ] Turning tracking off stops history; apps with limits are still enforced
- [ ] Export CSV opens in Excel with correct columns; Clear data needs auth and does not reset today's limits
- [ ] Master toggle OFF: limits pause, an info bar explains it, recording continues

## 14. Delivery

Implement in this order, one milestone at a time:

1. **Data model + store:** `ScreenTimeSettings`, `LockRule.Mode/AppKey`, settings migration, `ScreenTimeStore` with SQLite schema, prune, and unit tests.
2. **Sampler + identity:** `ForegroundUsageSampler`, `ActivityGate`, `AppIdentityResolver` (Win32 + UWP + icons), exclusions, with unit tests for slicing/idle. Log results to the debug console to prove it works before any UI.
3. **Self-Discipline integration:** refactor `QuotaTracker` to consume `UsageTicked` (shared idle setting), clock-guard, `LimitOnly` mode and badge. Self-Discipline must still work exactly as before.
4. **Screen Time tab shell:** third tab, consent dialog, range bar, summary cards, app list for Today with real data.
5. **Charts:** `UsageBarChart` for Today (hourly) and Week, week filtering, tooltips, keyboard support; then Month heat strip.
6. **Limits UI:** Set limit flyout, limit chips, row menu actions, auth rules for weakening, detail pane.
7. **Warnings + coverage:** limit-warning toasts, coverage hatching, info bars (protection off, tracking off).
8. **Settings, export, retention, clear data**, hidden/excluded apps.
9. **Polish + tests:** theme/High Contrast pass, localization, all unit tests, run the manual checklist and report any item that fails.

After each milestone: `dotnet build -c Release` (fix all errors and warnings), **launch the app**, report in 5 lines or fewer, commit, then **stop and wait for me to reply "continue"**. Do not start the next milestone on your own.
