# Self-Discipline Feature — Add-on Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement this feature on top of the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, theme (dark Fluent, lilac accent), and services. Build, run, and verify before reporting.

## 1. Entry point (UI placement)

Each locked program row has a split button **"Lock ▾"** with a dropdown menu. Current items:

1. Unlock
2. Set custom password
3. Remove custom password

Add, **after "Remove custom password"**:

4. A **divider (MenuFlyoutSeparator)**
5. **"Self-Discipline"** item with a stopwatch/timer icon (Segoe Fluent Icons `\uE916` Stopwatch or `\uE823`)

Clicking "Self-Discipline" opens a **modal ContentDialog** for **that specific locked app** (settings are stored per lock rule, not globally). The dialog title bar can be empty as in the design; the dialog has three tabs at the top, and **Save** (lilac accent, wide) and **Cancel** buttons at the bottom. Save persists and closes; Cancel discards changes. Tabs use a segmented/pivot control with the selected tab highlighted; tab titles can truncate with ellipsis ("Auto-relock on timeout").

If the item is disabled states: "Unlock" is disabled when already locked, and "Remove custom password" is disabled when none is set. "Self-Discipline" is always enabled for any rule.

## 2. Data model

Extend `LockRule` with:

```csharp
public class SelfDisciplineSettings
{
    // Daily usage quota
    public bool QuotaEnabled { get; set; }
    public int DailyLimitMinutes { get; set; } = 60;     // 1..1440
    public DateOnly QuotaDate { get; set; }              // the day the counter belongs to
    public int UsedSecondsToday { get; set; }
    public int ExtraSecondsGrantedToday { get; set; }    // from password-extended time

    // Scheduled lock
    public List<TimeBlock> TimeBlocks { get; set; } = new();

    // Auto-relock on idle
    public bool IdleRelockEnabled { get; set; }
    public int IdleRelockMinutes { get; set; } = 15;     // 1..240
}

public class TimeBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TimeOnly Start { get; set; } = new(20, 0);
    public TimeOnly End { get; set; } = new(23, 0);
    public bool[] Days { get; set; } = { true, true, true, true, true, true, true }; // Mon..Sun
}
```

Persist inside the existing settings JSON (HMAC/DPAPI protection from `instructions.md` applies). Add `SelfDiscipline` property to `LockRule` with defaults. Migrate older settings files without errors.

## 3. Tab 1 — Daily usage quota

UI (matches design):
- Card with title **"Enable daily usage quota"**, description: *"Only counts while the app is in the foreground. When the limit is reached, the app is locked again; enter the password to extend time."* and a **ToggleSwitch** on the right.
- Card **"Daily limit (minutes)"** with a **NumberBox** (spin buttons, min 1, max 1440, default 60). **Disabled (greyed out) while the toggle is off.**
- Below, a **circular progress ring** (donut) showing used time as `H:MM` in the center (e.g. `0:00`, `0:45`, `1:30`) and caption **"Used today"**. The ring fills proportionally to used / limit and turns a warning color above 90%. When the quota is disabled, the ring is dimmed.

Behavior:
- Count time **only while the app's window is the foreground window** and the user is not idle (no input for 60 s+ pauses counting). Tick every second via a service, accumulate `UsedSecondsToday`; flush to disk every 30 s and on exit.
- Reset automatically at **local midnight** (compare `QuotaDate` with today at each tick and after resume from sleep).
- When `UsedSecondsToday >= DailyLimitMinutes*60 + ExtraSecondsGrantedToday`: immediately show the lock overlay for that app and mark it locked. Overlay message: *"Daily limit reached for <app>. Enter the password to extend time."*
- On unlocking after the limit is reached, show an **extend-time** choice (15 / 30 / 60 min / Custom) instead of the normal "time allowed" chooser; add to `ExtraSecondsGrantedToday`. Unlock always requires the admin/custom password or Windows Hello.
- Changing the limit upward takes effect immediately; lowering it below used time locks the app immediately after Save.
- The ring in the dialog updates live while the dialog is open.

## 4. Tab 2 — Scheduled Lock

UI:
- **Empty state:** calendar icon, text *"No time blocks set. The app won't be locked by schedule."* and an **"Add time block"** button.
- **With blocks:** header text *"During scheduled periods the app is locked automatically; outside them it works normally."* Then one card per block:
  - Row: **start hour ComboBox (00–23) : start minute ComboBox (00, 05, …55)  –  end hour : end minute**, and a **trash icon button** to delete the block.
  - Below: **7 circular day toggles M T W T F S S** (Mon–Sun). Selected = lilac filled with dark letter; unselected = dark outline. At least one day must be selected (disable deselecting the last one or show validation).
  - Default new block: 20:00–23:00, all days selected (as in design).
  - After the last card, a centered **"+ Add time block"** button (lilac outline style, with plus icon).
- Max 10 blocks. Validate start ≠ end.

Behavior:
- A block that spans midnight (e.g. 22:00–02:00) is valid: it covers 22:00–24:00 on the selected day and 00:00–02:00 on the **next** day. Define the selected day as the day the block starts.
- While the current local time falls inside any block (for the matching day), the app is **forcibly locked**: overlay appears even if the app was unlocked with a timer, and "No limit" unlocks are overridden. Unlocking via password during a scheduled period is **not allowed** for the rest of that period (overlay message: *"Locked by schedule until HH:MM"* and the password box is replaced by a disabled state with an **emergency override** only through the admin password + confirmation, and logged).
- Evaluate on a 1-second timer and also on system time change / resume events (`SystemEvents.TimeChanged`, `PowerModeChanged`). Handle DST and time zone changes using local time.
- Overlapping blocks are merged for evaluation.

## 5. Tab 3 — Auto-relock on timeout (idle)

UI:
- Card **"Enable auto-relock after idle"**, description *"Timeout is based on keyboard/mouse idle time."* with a ToggleSwitch.
- Card **"Auto-relock after idle (minutes)"** with a NumberBox (default 15, min 1, max 240), **disabled while the toggle is off**.

Behavior:
- Use `GetLastInputInfo` (global input idle time). When idle time ≥ configured minutes and the app is currently **unlocked**, relock it: show the overlay and cancel any remaining unlock timer. This is per app, using that app's setting.
- Idle relock does not affect apps that are already locked, and it doesn't trigger when the app has no unlock session.
- Reset the idle tracking when the user unlocks.

## 6. Dialog behavior and polish

- Dialog opens on the tab the user last used (default: Daily usage quota). The three tabs are always available.
- **Save** validates all tabs; if invalid, switch to the offending tab and show an inline error. **Cancel** or Esc discards.
- Do not require re-authentication to open the dialog **only if** the main window session is authenticated; **weakening** settings (disabling the quota/idle toggle, deleting a time block, raising the limit) requires verifying the admin password (or Windows Hello) on Save. Strengthening settings does not require it. During a currently active scheduled period, settings for that app cannot be weakened at all.
- Show a small badge/icon (the stopwatch) on the program row in the Locked programs list when any self-discipline rule is active; tooltip summarizes (e.g. "60 min/day, 1 time block, idle 15 min").
- Persist immediately on Save; services pick up changes without restart.
- Respect the global master toggle: when protection is OFF, no quota counting, schedules, or idle relock apply.
- All strings should go through the localization resource file already used by the app.

## 7. Services to add

- `SelfDisciplineService`: single 1-second timer coordinating quota counting, schedule evaluation, and idle relock; calls `LockManager.ForceLock(rule, reason)`.
- `QuotaTracker`, `ScheduleEvaluator`, `IdleMonitor`: small, testable classes with a clock abstraction (`IClock`) so they can be unit-tested.
- `LockManager.ForceLock(rule, LockReason reason)` where `LockReason` is `Quota`, `Schedule`, `Idle`, `Manual`; the overlay shows the matching message.

## 8. Tests and manual checklist

Unit tests: quota rollover at midnight, quota counting only on foreground and not idle, midnight-spanning blocks, overlapping blocks, DST day, idle threshold, JSON migration.

Manual:
- [ ] Menu shows divider and "Self-Discipline" after "Remove custom password"
- [ ] Limit 1 minute: app locks after 1 minute of foreground use; extend 15 min works; the ring updates live
- [ ] Switching to another window pauses counting
- [ ] Add a block covering the current time: app locks immediately and cannot be unlocked with the normal password
- [ ] Block spanning midnight works; deleting a block (with auth) works
- [ ] Idle 1 minute: app relocks after 1 minute of no input
- [ ] Disabling toggles greys out the NumberBoxes; settings persist after restart
- [ ] Master toggle OFF suspends everything

## 9. Delivery

Implement in this order: (1) data model + migration, (2) menu item + dialog shell with 3 tabs, (3) idle relock, (4) quota tracker + ring, (5) scheduled lock, (6) auth rules for weakening, (7) tests. After each step: `dotnet build -c Release`, launch the app, report in 5 lines or fewer, commit, and wait for "continue".
