# BitLocker Page — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement this feature in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, and Fluent look. Treat BitLocker management as a **new feature built from scratch**. Do not assume any other BitLocker tool exists.

## 1. Goal

Add a **BitLocker** page to the left navigation rail. It lets the user see and manage BitLocker-encrypted drives: view status, lock/unlock, encrypt/decrypt, manage protectors and recovery keys, save passwords securely, and auto-unlock drives at startup.

The page uses native Windows 11 Fluent UI and follows the app's **Light / Dark / System** theme setting like every other page (use `ThemeResource` brushes only, no hard-coded colors, and test all three themes).

## 2. Availability (supported devices only)

BitLocker management is not available on Windows Home. The nav item must appear **only on supported systems**.

Detection at startup (cache the result, re-check on each launch):
1. Read `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\EditionID`. Values `Core`, `CoreN`, `CoreSingleLanguage`, `CoreCountrySpecific` mean Home: **unsupported**.
2. Check that the WMI namespace `root\CIMV2\Security\MicrosoftVolumeEncryption` exists and `Win32_EncryptableVolume` can be queried (admin required, so on access denied treat the feature as supported but not elevated).
3. Check that `%SystemRoot%\System32\manage-bde.exe` exists.

If the edition is Home or the WMI class is missing: **hide the BitLocker nav item completely** and skip all BitLocker services (no timers, no startup task). In Settings → About, show a small line "BitLocker: not supported on this edition" for troubleshooting.

Supported editions include Pro, Pro for Workstations, Enterprise, Education, and Server.

## 3. Administrator rights

BitLocker operations require elevation.
- If AppLock already runs elevated, call APIs directly.
- If not, show an **InfoBar** at the top of the page: "Administrator rights are needed to manage BitLocker." with a **Restart as administrator** button (relaunch with `runas`, preserving the signed-in session after the admin password prompt). Until elevated, the drive list is read-only where possible and all action buttons are disabled.
- Never ask the user to type the Windows admin password into AppLock. Use the standard UAC prompt.

## 4. Page layout (native Fluent UI)

Page title: **BitLocker**. Top command bar (`CommandBar` with icons and labels):
- **Refresh** (F5)
- **Lock**, **Unlock**
- **Turn on BitLocker**, **Turn off BitLocker**
- **Suspend / Resume protection**
- **Back up recovery key**
- **More** (…): Change password, Manage key protectors, Check usage, Open in File Explorer, Auto-unlock on this PC, Diagnostics

Main area: a **drive list** with one card per volume (a `ListView` with a templated Grid is fine; a toolkit DataGrid is also acceptable). Each card shows:
- Drive icon (fixed disk, removable USB, or OS drive), letter and label, e.g. "D: Data"
- **Type**: OperatingSystem / Data / Removable
- **Protection status**: On / Off / Suspended (colored chip)
- **Lock status**: Locked / Unlocked (lock icon)
- **Encryption %**: progress bar with percent; live-updating during encryption/decryption
- **Encryption method**: e.g. XTS-AES 256 (if available)
- **Key protectors**: chips such as Password, RecoveryPassword, TPM, ExternalKey, TpmPin
- **Capacity**: used/free bar from the file system when unlocked

Selecting a card enables the relevant commands. A details pane (right side or expander) shows protector list with IDs, "Saved password: Yes/No", "Auto-unlock on startup: On/Off", and the last unlock time.

Below the list, a quick-unlock row (shown when the selected drive is locked): `PasswordBox` + **Unlock** button + a **Save password for this drive** checkbox + **Use saved password** button (enabled only if a password is saved).

Bottom status bar: one-line status text of the last action (e.g. "Drive D: unlocked"), shown with `InfoBar` severity styling for errors and warnings.

Empty state: if no BitLocker-capable volumes are found, show an illustration/icon with text "No drives found" and a Refresh button.

## 5. Features and behavior

### 5.1 List drives and status
Enumerate volumes with `Win32_EncryptableVolume` (via `System.Management`, scope `\\.\root\CIMV2\Security\MicrosoftVolumeEncryption`). For each, read:
- `DriveLetter`, `GetProtectionStatus`, `GetLockStatus`, `GetConversionStatus` (encryption percentage, status), `GetEncryptionMethod`
- `GetKeyProtectors(0)` then `GetKeyProtectorType(id)` to build the protector list
- Drive type from `DriveInfo` / `Win32_LogicalDisk` (OS drive = `%SystemDrive%`)

Auto-refresh every 5 s while the page is visible, plus immediately after any action. Subscribe to volume arrival/removal (`Win32_VolumeChangeEvent`) so a plugged-in USB drive appears without a manual refresh.

### 5.2 Unlock
- Unlock using the password protector: `Win32_EncryptableVolume.UnlockWithPassphrase`. **Do not pass the password on a command line** (it would be visible in process lists). Fall back to the PowerShell SDK `Unlock-BitLocker -MountPoint D: -Password <SecureString>` if WMI fails.
- Support unlocking with the **recovery password** (48-digit) through a separate "Use recovery key" link: `UnlockWithNumericalPassword`.
- On wrong password, show a clear error and count attempts; after 5 failures apply a short cooldown (30 s) in the UI.
- After a successful unlock, optionally open the drive in File Explorer (setting: "Open drive after unlock", default off).

### 5.3 Lock
- `Lock(ForceDismount)` through WMI. The OS drive cannot be locked: disable the Lock button for the OS drive and for the drive AppLock itself runs from.
- Before locking, detect open handles and running processes on the drive; if any, show a confirm dialog listing them with "Lock anyway (force dismount)" or Cancel.

### 5.4 Turn on BitLocker (encrypt a drive)
A `ContentDialog` wizard with these steps:
1. **Choose drive** (data and removable drives; for the OS drive show a warning that it needs TPM or policy changes and is out of scope unless a TPM is detected).
2. **Protector**: password (min 8 chars, with strength indicator and confirmation box) and/or Smart card is out of scope. A recovery password is always added.
3. **Encryption scope**: *Used space only* (faster, default for new drives) or *Entire drive*.
4. **Method**: XTS-AES 128 (default), XTS-AES 256, or AES-CBC for compatibility with older Windows (for removable drives).
5. **Back up recovery key**: must save to a file or copy to clipboard before the Start button is enabled.
6. **Start encryption**, then show live progress on the drive card.

Use `ProtectKeyWithPassphrase`, `ProtectKeyWithNumericalPassword`, then `Encrypt(method, flags)`, or the PowerShell SDK `Enable-BitLocker` as a fallback. Handle "already encrypted", "not enough free space" and policy errors with human-readable messages.

### 5.5 Turn off BitLocker (decrypt)
Confirmation dialog with a clear warning and a type-the-drive-letter confirmation. Requires the admin password check (section 6). Run `Disable-BitLocker`/`Decrypt` and show progress. Disabled for the OS drive unless elevated and confirmed twice.

### 5.6 Suspend / Resume protection
`DisableKeyProtectors(RebootCount)` and `EnableKeyProtectors`. Suspend dialog lets the user choose reboot count (default 1) for the OS drive. Show the status chip "Suspended".

### 5.7 Change password and manage protectors
- **Change password**: ask for the current password (or confirm that the drive is unlocked), then add the new password protector and remove the old one atomically; on failure roll back.
- **Manage key protectors** dialog: list protectors with type and ID; add a password or recovery password; delete a protector (never allow deleting the last protector; confirm before deleting).

### 5.6 Recovery key
- **Back up recovery key**: save as `.txt` (drive label, protector ID, 48-digit key, date) via file picker, or copy to clipboard (auto-clear clipboard after 60 s). Printing is optional.
- **View recovery key** requires AppLock admin password or Windows Hello (section 6) and shows the key in a dialog with a Copy button.
- Warn if a drive has no recovery password protector and offer to add one.

### 5.7 Saved passwords ("Manage Passwords")
- Optional per-drive saved password so the user can unlock with one click.
- Store only **DPAPI-protected** (CurrentUser scope, plus per-app entropy) blobs in the AppLock settings store; key by the volume's unique ID (volume GUID), not the drive letter. Never store plaintext, never log it.
- **Manage Passwords** dialog: list drives with saved passwords (label, ID, date saved), **Update**, **Delete**, and **Delete all**. Viewing is not allowed, only replacing or removing. Requires admin password/Windows Hello to open.
- Show a one-time notice explaining that a saved password lets anyone using this Windows account unlock that drive, and that it is stored encrypted for this user only.
- If the saved password stops working (changed elsewhere), mark it as invalid and ask for the new one.

### 5.8 Auto-unlock
Two separate mechanisms:
1. **Windows auto-unlock** for fixed data drives (`EnableAutoUnlock` / `manage-bde -autounlock`): available only when the OS drive is encrypted and unlocked. Toggle in the drive's details with a clear explanation.
2. **AppLock auto-unlock using the saved password**:
   - Checkbox on the page: **"Auto unlock saved drives when AppLock opens"** and a status label like "Last unlocked: Drive D:".
   - On app start (and when a drive is plugged in), if enabled and a saved password exists, unlock silently and show a toast notification on success or failure (respect "Mute notifications").
   - A per-drive toggle "Include in auto-unlock".

### 5.9 Startup task
A **Setup startup task** button (and status chip) creates a Task Scheduler task so auto-unlock works before the user opens AppLock:
- Trigger: at log on of the current user (optionally at system startup), run with highest privileges (`RunLevel = Highest`), so no UAC prompt appears.
- Action: `AppLock.exe --bitlocker-autounlock` — a headless mode with no window: unlock saved drives, then exit (or continue to tray if AppLock is set to start with Windows).
- Buttons: **Create/Update task**, **Remove task**, **Task status** (shows state: Not created / Ready / Running / Last run time / Last result code and a Run now button).
- Use the `Microsoft.Win32.TaskScheduler` library or `schtasks.exe` with an XML definition. Handle the case where the task exists but points to a moved exe (offer Repair).

### 5.10 Usage check
**Check usage** opens a small dialog with used/free space and a bar chart for the selected unlocked drive (`DriveInfo`), plus total encrypted size and encryption percent.

### 5.11 Diagnostics
**Diagnostics** dialog (replaces any "debug" tools): shows detected edition, WMI availability, elevation state, startup task state, per-drive raw status values, and a log viewer of the last 200 BitLocker actions (redacted, no passwords or full recovery keys). Buttons: Copy report, Open logs folder.

## 6. Security rules

- Opening the BitLocker page requires the AppLock admin session to be authenticated, as with other pages.
- Require the AppLock **admin password or Windows Hello** again before: viewing/backing up a recovery key, opening Manage Passwords, turning off BitLocker, deleting protectors, and enabling/changing saved passwords. Cache that verification for at most 2 minutes.
- Passwords live in `SecureString`/byte arrays only as long as needed; zero and clear after use; clear `PasswordBox` after every attempt.
- Never write passwords or recovery keys to logs, crash reports, or the clipboard (except explicit Copy, with auto-clear).
- Never send anything over the network. No cloud backup of keys (Microsoft account/AAD backup may be offered as a link to the system UI only).
- All destructive actions need a confirmation dialog stating the drive letter and label.
- Keep UI responsive: run all WMI/PowerShell calls on background threads with cancellation and a timeout (30 s for status, 10 min for encryption start); show a progress ring on the affected card.

## 7. Settings (add to the BitLocker page or the Settings page)

- Auto unlock saved drives when AppLock opens (on/off)
- Open drive after unlock (on/off)
- Show toast notifications for BitLocker events (follows "Mute notifications")
- Refresh interval (5 / 10 / 30 s)
- Clear clipboard after copying a recovery key (on, 60 s)

## 8. Project structure additions

```
src/AppLock/
  Views/BitLockerPage.xaml(.cs), Views/Dialogs/ (EnableBitLockerDialog, ManageProtectorsDialog, ManagePasswordsDialog, RecoveryKeyDialog, UsageDialog, DiagnosticsDialog, StartupTaskDialog)
  ViewModels/BitLockerViewModel.cs, DriveItemViewModel.cs
  Services/BitLocker/
    BitLockerSupportService.cs   // edition + WMI + manage-bde detection
    BitLockerService.cs          // WMI wrapper: list, lock, unlock, enable, disable, suspend, protectors
    BitLockerPasswordStore.cs    // DPAPI saved passwords
    BitLockerAutoUnlockService.cs
    StartupTaskService.cs        // Task Scheduler
    VolumeWatcher.cs             // arrival/removal events
  Models/BitLockerDrive.cs, KeyProtectorInfo.cs
tests/AppLock.Tests/BitLocker/   // mock IBitLockerService; test support detection, password store, auto-unlock logic
```

Define `IBitLockerService` so the UI and logic can be tested with a fake implementation and run on machines without BitLocker.

## 9. UX details

- Status chips are color-coded but also have text/icons (accessibility). Support keyboard navigation, screen readers (`AutomationProperties.Name`), and high-contrast theme.
- Full theme support: verify list cards, chips, dialogs, and the progress rings in Light, Dark, and System, and when the system theme changes while the app is running.
- Localize all strings through the existing resource file.
- Disabled commands show a tooltip explaining why (e.g. "The OS drive can't be locked").

## 10. Milestones (build, run, verify each before moving on)

1. `BitLockerSupportService` + nav item shown/hidden correctly; page shell with command bar, InfoBar for elevation, theme-correct empty state.
2. Drive listing with all columns (type, protection, lock status, encryption %, protectors), auto-refresh and volume watcher.
3. Lock and unlock (password and recovery key), error handling, attempt cooldown.
4. Saved passwords: store, "Use saved", Manage Passwords dialog, security gates.
5. Auto-unlock on app open + startup task creation, status, and headless `--bitlocker-autounlock` mode.
6. Turn on BitLocker wizard with recovery key backup and live progress.
7. Turn off, suspend/resume, change password, manage protectors.
8. Recovery key view/backup, usage dialog, diagnostics dialog, settings.
9. Tests, theme pass (Light/Dark/System), accessibility pass, manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors/warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 11. Manual test checklist

- [ ] On Windows Home (or with the support check forced to false) the BitLocker nav item is hidden and nothing BitLocker-related runs
- [ ] On Pro/Enterprise the item appears; non-elevated shows the InfoBar and the Restart as administrator button
- [ ] Drive list shows C: and data/USB drives with correct type, protection, lock status, percent, protectors; hot-plugging a USB drive updates the list
- [ ] Unlock with password, with wrong password (error + cooldown), with recovery key
- [ ] Lock a data drive; OS drive's Lock is disabled with a tooltip; open-handle warning works
- [ ] Save password → "Use saved" works; Manage Passwords update/delete/delete all, all gated by admin password/Hello
- [ ] Auto-unlock on app open; startup task create/status/run now/remove; headless mode unlocks and exits
- [ ] Encrypt a small test VHD or USB drive via the wizard (cannot proceed without recovery key backup), progress updates; decrypt with confirmation
- [ ] Suspend/Resume, change password, add/remove protector (last protector can't be removed)
- [ ] Recovery key view/copy/save works and the clipboard auto-clears
- [ ] Light, Dark, and System themes look correct, including live theme switching
- [ ] No passwords or recovery keys appear in logs or diagnostics output
