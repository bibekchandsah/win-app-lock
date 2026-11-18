# Vault (Secure Folder Vault) — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, auth dialogs, hotkey service and Fluent look as the other pages.

> **This is the most security-critical feature in AppLock.** A mistake can leak files or destroy the user's data. Follow the cryptography and safety rules in this spec exactly, use only the .NET/OS cryptographic primitives named here (never invent your own), write the tests in section 14, and have the finished crypto reviewed by a qualified person before advertising the vault as secure.

## 1. Idea

A **Vault** is an encrypted container for the user's files and folders. Files go in (they are encrypted and the originals are removed), stay encrypted on disk, and can be opened only after the user authenticates with AppLock (admin password, Windows Hello, or a recovery key). The user can browse the vault inside AppLock, preview files without writing them to disk, open them in other apps, export them back out, and have the vault lock itself automatically.

Light / Dark / System theme like every other page (theme resources only).

## 2. Sidebar change: replace "Rules" with "Vault"

The current left navigation (see the screenshot) is: Protection, **Rules** (greyed-out placeholder), Websites (greyed-out placeholder), Internet Access, BitLocker, and at the bottom Feedback and Settings.

- **Remove the "Rules" item** and put **Vault** in the same position (second item, after Protection). It is **enabled** (not greyed out).
- Label **Vault**, icon: a folder-with-lock or safe glyph from Segoe Fluent Icons (the agent picks one that exists in the font and verifies it renders; it must look different from the Protection padlock).
- Update `instructions.md`'s placeholder reference ("Rules/Profiles") in `NOTES.md`: Rules is superseded by Vault. If a Profiles feature is built later, it goes under Protection or Settings, not here.
- **Websites** stays as it is (greyed-out placeholder).
- Navigation, breadcrumb, back stack and theme behavior follow the existing pages.

## 3. Scope

**In scope (version 1):**
- Multiple named vaults, each in a folder the user chooses (local drive, external drive, or a cloud-synced folder).
- Strong encryption of file **contents and names**; password, recovery-key and optional Windows Hello unlock.
- A built-in vault browser: add files and folders (drag and drop or buttons), create folders, rename, move, delete (to an encrypted vault trash), export/extract.
- **Secure preview** in memory (images, text, PDF, audio, video) with no plaintext written to disk.
- **Open with another app** through a protected temporary decrypted copy that is written back and wiped afterwards.
- Auto-lock (idle, screen lock, sleep, minimize, exit, emergency hotkey), failed-attempt handling, capture protection.
- Backup, integrity check, and header repair.

**Phase 2 (not in the first build):**
- **Mount the vault as a virtual drive** (WinFsp or Dokan) so any app can use files without a temporary copy.
- Explorer right-click "Add to Vault" (Windows 11 needs a packaged shell extension); version 1 offers a **Send to** shortcut only.
- Searching file contents, sharing a vault between users, and multi-PC simultaneous editing.

**Out of scope, do not build:** your own cipher or protocol, a password manager, hiding the vault's existence (steganography), DRM, uploading to any cloud, securely wiping free disk space, encrypting the whole system or a whole drive (that is the **BitLocker** page), and any backdoor or key escrow (there is none; see 6.4).

## 4. Page layout

### 4.1 Vault list (landing view)
- Title **Vault** and short description ("Encrypted folders that open only after you authenticate").
- **Empty state:** an illustration/icon, "Create your first vault", a short explainer ("Your files are encrypted before they are stored. Without your password or recovery key, nobody can read them, including us."), and buttons **Create vault** and **Open existing vault**.
- **Vault cards** (one per vault): name and icon, location (shortened, tooltip with the full path), size and item count (shown when unlocked, otherwise "Locked"), status chip (**Locked / Unlocked / Not connected / Needs attention**), last opened, and buttons **Open** (or **Lock** when unlocked), **Settings**, **More** (Back up, Verify, Show in Explorer, Remove from list, Delete vault…).
- Command bar: **Create vault**, **Open existing vault…**, **Lock all**.
- InfoBars: "Wrong drive? This vault's folder wasn't found" (Not connected), "This vault still uses your previous AppLock password" (see 6.6), and a security tip when BitLocker is off on the drive holding a vault ("Turn on BitLocker for extra protection" with a link to the BitLocker page).

### 4.2 Create vault wizard (ContentDialog, steps)
1. **Name and icon.**
2. **Location:** default `%LOCALAPPDATA%\AppLock\Vaults\<name>`, or **Choose folder** (must be empty or new; warn on network paths, removable drives, and folders inside the Windows or Program Files directories; reject the AppLock settings folder).
3. **Protection:**
   - **Use my AppLock admin password** (default): the vault key is wrapped with a key derived from your admin password (see 6.2/6.6).
   - **Use a separate vault password** (recommended for very sensitive data): choose a password or passphrase with a strength meter and confirmation.
   - Password rules: minimum **10 characters**, strength estimate (length plus a common-password check, using a bundled offline list; no network). If the user picks "Use my AppLock admin password" and it is **weak** (shorter than 10 characters or in the common list), show a warning and offer to create a separate vault password instead. An attacker who copies the vault can try passwords offline, so weak passwords are the main risk.
   - **Windows Hello quick unlock** (optional toggle, shown only if available).
4. **Recovery key (required):** generate a random recovery key (160 bits, shown as 8 groups of 5 characters in an unambiguous alphabet, with a checksum). The user must **save it (file, print, or copy to clipboard with auto-clear after 60 s)** and then **type back two requested groups** to prove it's saved before **Create** is enabled. Explain clearly: **there is no other way to recover the vault if the password is lost**.
5. **Options:** auto-lock minutes (default 10), "Secure preview only" (don't allow opening in other apps), trash retention days (default 30), exclude the vault window from screenshots/screen sharing (default on).
6. **Create:** progress, then the vault opens.

### 4.3 Unlock dialog (same style as "Verify admin password")
Vault name, password box, **Windows Hello** button (when a Hello slot exists), **Use recovery key** link, **Cancel**, **Unlock**, and "Keep unlocked for: *Until I lock it* / 5 / 15 / 30 minutes" selector (default from settings). Wrong password: clear message, attempt counter and cooldown from the AppLock **Lockout duration** setting (shared counters). Clear the password box after every attempt.

### 4.4 Vault browser (when unlocked)
- Header: vault name, a lock icon/button (**Lock now**), storage used, item count, and a small "Auto-locks in 9:42" indicator.
- **Command bar:** **Add files**, **Add folder**, **New folder**, **Export**, **Open**, **Rename**, **Move**, **Delete**, **Trash**, **Search** (file names only), **View** (list / details / large icons), **Sort**, **More** (Vault settings, Back up vault, Verify, Change password).
- **Breadcrumb** path inside the vault and a folder tree.
- File list: icon by type, name, size, type, date added, date modified; multi-select; keyboard navigation; **drag and drop from Explorer onto the window** to add; context menu (Open, Preview, Export, Rename, Move, Delete, Properties).
- **Properties:** name, size, type, created/modified (preserved from the original), date added, stored object ID (only in *Advanced*).
- **Add progress panel** (docked at the bottom): per-file progress, overall progress, speed, **Pause/Cancel**, and an error list (skipped files with reasons: in use, access denied, path too long, not enough space).
- **Trash** view: deleted items with "deleted on" and "days left", **Restore** and **Delete permanently**, **Empty trash**.
- A banner shows "This vault is encrypted. Names and contents are protected. The number of files and their approximate sizes are not hidden." (one-time dismissible).

### 4.5 Vault settings (per vault) and global settings
Per vault: rename, icon, auto-lock minutes, secure-preview-only, trash retention, **Change password**, **Manage unlock methods** (password, recovery key, Windows Hello; add/remove), **Re-generate recovery key**, **Back up vault…**, **Verify vault**, **Delete vault…**.
Global (Vault page → More → Settings): see section 8.

## 5. Adding, opening and exporting files

### 5.1 Adding files and folders ("Move into vault")
Flow per file (streaming; never load a whole file in memory):
1. Check free space (vault location needs at least the file size plus a safety margin).
2. Read the original, encrypt into a new object file in the vault (written under a temporary name).
3. **Verify:** decrypt the new object and compare a SHA-256 of the plaintext with the original's hash (computed while reading). If different, delete the new object and keep the original.
4. Update the encrypted index atomically (see 7.4).
5. **Only then** remove the original (default setting **"Delete original after it is safely in the vault"**, on). Delete permanently (no Recycle Bin). Optionally **overwrite before deleting** (off by default). If the original can't be deleted (in use, read-only, no permission), keep it, tell the user, and list it in the result.
- Show a **one-time warning** about deleted originals: "On SSDs, Windows may keep old copies of deleted files until the space is reused. Add files to the vault before they are sensitive, or turn on BitLocker for the drive."
- Choices on the add dialog: *Move into vault* (default, deletes originals after verification) or *Copy into vault* (keeps originals).
- Preserve folder structure, empty folders, file times (created/modified), and the read-only attribute. Do **not** follow symbolic links or junctions (skip with a note). Alternate data streams, NTFS permissions and encryption attributes are not preserved (documented in the UI help).
- Name collisions inside the vault: ask *Replace / Keep both / Skip* (with "apply to all").
- Handle long paths, Unicode names, very large files (multi-GB), thousands of files, cancel/pause, and crash during add (see 7.4).
- Never delete or modify anything else. Never add the vault's own folder, the AppLock settings folder, or the Windows directory into a vault.

### 5.2 Secure preview (no plaintext on disk)
Default for double-click when the file type supports it. Decrypt chunks in memory and feed viewers through a stream:
- **Images** (PNG, JPEG, GIF, WebP, BMP, and others the OS decoders support) with zoom/rotate/next-previous.
- **Text** (plain, Markdown, JSON, logs, code) with find and word-wrap, size limit with "open first N MB".
- **PDF** through `Windows.Data.Pdf` loaded from the decrypted in-memory stream.
- **Audio and video** with `MediaSource.CreateFromStream` over a seekable decrypting stream (chunked format allows random access), with standard transport controls.
- Preview windows are excluded from screen capture (see 6.7), close when the vault locks, and **never** write temp files, thumbnails or caches. Disable any viewer feature that would write to disk.

### 5.3 Open with another app (temporary decrypted copy)
For other types or when the user picks **Open with default app**:
- Show a confirmation the first time: "This creates a temporary decrypted copy while the file is open. Choose Secure preview where possible." (setting can disable this path: *Secure preview only*).
- Decrypt to a private session folder `%LOCALAPPDATA%\AppLock\VaultTemp\<random-id>\`, created with an ACL that allows only the current user (remove inherited access), marked **not content-indexed**, with a **generic file name** such as `item-3f9a.docx` by default (setting "Use the original file name" is off by default, because recent-file lists and thumbnails can record names).
- Launch the default app. **Watch** the file; when it changes, mark the item as modified.
- When the app/process closes, or the vault locks, or AppLock exits: **re-encrypt the changes back** into the vault (detect conflicts by comparing the hash and modified time with what was extracted; if the vault copy changed too, ask), then **securely delete the temporary copy** (overwrite once, then delete; note SSD limits) and remove the session folder.
- **Crash recovery:** at the next AppLock start, if a session folder exists, show "Unsaved edits from your last session" with **Save back to vault / Export / Discard**, and wipe it after the choice (vault must be unlocked to save back).
- Show a visible indicator ("1 file open outside the vault") with **Close and re-lock** that closes the handles where possible and wipes the copies; **Lock now** warns when there are open temp copies.
- Limits told to the user: Explorer thumbnails, Windows search, antivirus scanners, and the opened app itself may keep their own copies or caches.

### 5.4 Export (extract)
**Export** decrypts selected items to a folder the user picks (default Downloads, never the temp folder), preserving structure and times, with the same warning about plaintext on disk. Choices: *Export (keep in vault)* or *Move out of vault* (remove from the vault after verified export). Verify with hashes.

## 6. Cryptography and security design

Use only: `System.Security.Cryptography.AesGcm`, `HKDF`, `RandomNumberGenerator`, `SHA256`/`HMACSHA256`, `CryptographicOperations.FixedTimeEquals` and `ZeroMemory`, and a maintained **Argon2id** implementation (for example the libsodium-based or Konscious/Isopoh packages; pick one with an active maintainer and record the decision in `NOTES.md`). No custom ciphers, no ECB, no unauthenticated CBC, no hard-coded keys, no `Random`/`Guid` for security values.

### 6.1 Key hierarchy
- **Vault Master Key (VMK):** 256 random bits generated at creation. It never leaves memory except wrapped inside key slots.
- **Key slots (LUKS-style)**, each holding a copy of the VMK encrypted with a different key-encryption key (KEK). Slots can be added, removed or replaced without re-encrypting file data:
  - **Password slot:** `KEK = Argon2id(password, salt16, params)`; VMK wrapped with AES-256-GCM (random 96-bit nonce, tag 128 bits, AAD = vault id ‖ slot type ‖ slot id).
  - **Recovery slot:** recovery key (160-bit random) expanded with `HKDF-SHA256` (random salt in config) into the KEK.
  - **Windows Hello slot (optional):** see 6.5.
  - A **verifier**: a small known value encrypted under the VMK, so a wrong password is reported without touching file data.
- **Derived keys** from the VMK through HKDF with fixed labels: `index-key` (encrypts the file index), `fek-wrap-key` (wraps per-file keys), and `config-mac-key` (authenticates the config).
- **Per-file key (FEK):** a random 256-bit key per file; stored in the file's header wrapped by `fek-wrap-key`. Re-keying or changing the password therefore never requires re-encrypting file contents.

### 6.2 KDF parameters
Argon2id with a random 16-byte salt per slot. Default targets about **0.5–1 s** on the user's PC, calibrated at creation (start at `m = 256 MiB, t = 3, p = 4`, lower the memory only if necessary), stored in the config, with **minimum floors** that the app enforces on open (`m ≥ 64 MiB`, `t ≥ 3`) so a tampered config can't downgrade protection. If Argon2id isn't available, fall back to PBKDF2-HMAC-SHA256 with at least 600,000 iterations and say so in `NOTES.md`.

### 6.3 File format (version 1)
Vault folder layout:

```
<Vault folder>/
  vault.config          // JSON: format version, vault id, name, KDF params, key slots, verifier, created, config MAC
  vault.config.bak      // previous good copy (header redundancy)
  index.enc             // encrypted file index (see below)
  index.enc.bak
  data/ab/cdef...obj    // encrypted file objects, named by random 128-bit IDs (fan-out by first byte)
  trash/ ...            // same object format
  vault.lock            // single-writer lock (machine id + timestamp), advisory
```

**File object:** `magic "ALV1" | version | cipher id | chunk size (default 256 KiB) | file id (16 B) | wrapped FEK | random 64-bit nonce prefix | header MAC/AAD` followed by chunks. Each chunk is `AES-256-GCM(FEK, nonce = prefix(8 B) ‖ counter(4 B), AAD = file id ‖ chunk index ‖ final-chunk flag, plaintext)` plus a 16-byte tag. The counter prevents reuse and reordering; the final-chunk flag in the AAD prevents silent truncation. The file's plaintext length is verified against the index. Chunking supports **random access** (decrypt only the needed chunks), which is what makes media seeking work.

**Index (`index.enc`):** the folder tree and metadata (names, sizes, timestamps, attributes, content type, object IDs, trash state, revision number), serialized (JSON or MessagePack, compressed) and encrypted as one AEAD blob with `index-key`. Names and structure are **not visible** on disk. Design for at least **50,000 items**; keep the index in memory while unlocked and write it back atomically (see 7.4).

**What is and isn't hidden (state this in the UI help):** contents, file names, and folder structure are encrypted. Not hidden: that a vault exists, the number of objects, and approximate file sizes. (Optional later: size padding.)

### 6.4 No backdoor, no recovery by AppLock
There is **no master key, no reset, and no escrow**. A forgotten password with no recovery key means the data is **permanently unreadable**. State this in the create wizard, in vault settings, and in the "Forgot password?" text. The AppLock **security-questions reset must not unlock or re-key vaults** (see 6.6).

### 6.5 Windows Hello slot (optional quick unlock)
- Preferred: create a per-vault **Windows Hello key credential** (`KeyCredentialManager.RequestCreateAsync`), sign a stored random challenge, and derive the KEK from the signature with HKDF. RSA PKCS#1 v1.5 signatures are deterministic, but **test this on real devices**; if a device returns non-deterministic signatures, do not ship this method for it.
- Fallback (clearly labeled **"Quick unlock — protects against casual access only"**): keep the VMK wrapped with DPAPI (CurrentUser) and require a Windows Hello verification before unwrapping. Malware running as the same Windows user could bypass this, so the UI says so and offers it as opt-in.
- Hello slots are device-bound: they don't work on another PC. The password and recovery slots always remain.

### 6.6 AppLock admin password integration
- With **"Use my AppLock admin password"**, the password slot's KEK is derived from that password. When the user **changes the admin password** in Settings (the old password is known at that moment), AppLock re-wraps the VMK of every reachable vault with the new password and shows a result list. Vaults that are not reachable (drive missing) are flagged **"Still uses your previous password"** (the old slot keeps working until updated; the user can sync later by unlocking it with the old password or recovery key and choosing *Sync with admin password*).
- **Security-questions reset** only resets the AppLock password. Show this notice in that flow: "Your vaults are not reset. Unlock them with their previous password or recovery key, then sync them." Never derive or store a vault key from security-question answers.
- Do not store the admin password or any derived vault key between uses. The vault key exists in memory only while unlocked.
- The AppLock **master protection toggle** does not affect vaults (they are protected by cryptography, not by the lock overlay).

### 6.7 Memory and process hygiene
- Keep the VMK and unwrapped FEKs in pinned `byte[]` buffers and zero them with `CryptographicOperations.ZeroMemory` on lock, on exit, and on errors. Zero password buffers as soon as possible (WinUI's `PasswordBox.Password` is a managed string; clear the box after use and don't copy it into other strings or logs). State the residual risk (managed strings may linger in memory) in `NOTES.md`.
- Never log passwords, keys, recovery keys, file names, or plaintext. Log only event types and vault IDs.
- Exclude vault windows and previews from screen capture and screen sharing with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (setting, default on). Disable the clipboard for file contents (no "copy file text to clipboard" in version 1).
- Do not allow dumping of the process secrets via crash reports: don't attach memory dumps to any report AppLock generates.
- When the vault locks: wipe keys, close viewers and preview streams, clear the in-memory index, close or flush temp sessions (5.3), clear thumbnails/caches the app created, and show the vault as Locked.

### 6.8 Brute-force and tamper handling
- Failed attempts follow AppLock's attempt/lockout policy (shared with the admin dialog); the KDF cost is the real protection against offline attacks, so enforce the password rules in 4.2.
- `vault.config` is authenticated; on a bad MAC, floors violation, or corrupt JSON, fall back to `vault.config.bak` and tell the user, and refuse to open if both are invalid ("Vault header damaged — restore from a backup").
- Every chunk and the index are authenticated; any failure shows a clear integrity error for that file and never returns partial data as valid.
- Detect rollback only in a limited way (the index carries a revision number; AppLock remembers the highest revision it saw for each vault on this PC and warns if an older one appears). Document that a full rollback to an old copy of the vault folder cannot be prevented.

## 7. Auto-lock, reliability and backup

### 7.1 Auto-lock triggers (each configurable; defaults in section 8)
Idle time (reuse `IdleService`), AppLock's own idle auto-lock, PC lock/sleep/hibernate/user switch (`WTS` session notifications and power events), window minimized or hidden to the tray (after N minutes), AppLock exit, the **emergency lock hotkey** (Ctrl+Alt+L locks all vaults instantly and wipes temp sessions), the **Lock all** command, the master "Panic" feature if present, and removing the drive that holds the vault. Auto-lock never loses data: unsaved temp edits are re-encrypted first (5.3), or the user is prompted if the vault can't be reached.

### 7.2 Single-writer lock
Create `vault.lock` (machine name + timestamp) while unlocked. If a lock from another machine or a stale lock exists, warn ("This vault may be open on another PC") and let the user choose read-only or take over. Never run two writers at once.

### 7.3 Backup, verify, repair
- **Back up vault…** copies the vault folder (already encrypted) to a location the user chooses, with a progress bar and a hash check. It doesn't need to be unlocked.
- **Verify vault** (needs unlock): check config MAC, index decrypts, every object exists, and optionally **deep verify** (decrypt all chunks and compare tags); report problems per file. Orphan objects (not in the index) can be listed and cleaned.
- **Repair**: restore `.bak` header/index, rebuild the index from object headers if possible (names can't be recovered without the index; contents can be exported by object ID), and clearly explain what was recovered.
- Recommend a regular backup and a separate copy of the recovery key (shown in the wizard and in vault settings).

### 7.4 Crash safety and atomicity
- Write order for any change: write new object files first (temporary names, flush to disk), then write the new index to a temporary file, flush, replace atomically (`File.Replace`) while keeping `index.enc.bak`, and only then delete originals/old objects.
- On open, detect interrupted operations: leftover temporary files and orphan objects are listed and offered for cleanup; never delete user data automatically.
- Keep two generations of the index and the config.

### 7.5 Trash
Deleted items move to an encrypted trash within the vault (same format) for N days (default 30; 0 = delete immediately), then are removed (overwrite best-effort, then delete). **Empty trash** and **Restore** are available. Permanent deletion needs a confirmation naming the items.

## 8. Settings

**Global** (Vault page → More → Settings):
- Default auto-lock after idle: 1 / 5 / **10** / 30 min / never (with a warning for "never").
- Lock when the PC locks or sleeps (**on**), when the window is minimized for N minutes (default 5), and on AppLock exit (**on**).
- Exclude vault windows from screenshots and screen sharing (**on**).
- **Secure preview only** (off): when on, the "Open with another app" path is disabled.
- Temporary copies: use generic file names (**on**), overwrite before deleting (**on**), clean up on exit (**on**).
- Delete originals after verified add (**on**); overwrite originals before deleting (off).
- Trash retention days (**30**).
- **Send to shortcut** ("Add to AppLock Vault") in the Windows Send to menu (off; creates a `.lnk` in `%APPDATA%\Microsoft\Windows\SendTo` that starts AppLock with `--vault-add <paths>`; the running instance receives the paths through the existing single-instance IPC, asks which vault, and requires unlock first).
- Require a fresh authentication (password or Hello) to open the Vault page even when the AppLock session is already authenticated (**on**).
- Show size/count in the vault list when locked (off; they are only known after unlocking, so this stores a cached number in the settings file and is off for privacy).

## 9. Data model (settings JSON; keys are never stored here)

```csharp
public class VaultSettings
{
    public List<VaultRegistration> Vaults { get; set; } = new();
    public int DefaultAutoLockMinutes { get; set; } = 10;
    public bool LockOnPcLock { get; set; } = true;
    public int LockOnMinimizeMinutes { get; set; } = 5;
    public bool LockOnExit { get; set; } = true;
    public bool ExcludeFromCapture { get; set; } = true;
    public bool SecurePreviewOnly { get; set; }
    public bool TempGenericNames { get; set; } = true;
    public bool TempOverwriteOnDelete { get; set; } = true;
    public bool DeleteOriginalsAfterVerify { get; set; } = true;
    public bool OverwriteOriginals { get; set; }
    public int TrashDays { get; set; } = 30;
    public bool SendToEnabled { get; set; }
    public bool RequireFreshAuth { get; set; } = true;
}

public class VaultRegistration
{
    public Guid Id { get; set; }                 // matches vault.config id
    public string Name { get; set; }
    public string Path { get; set; }
    public string? Icon { get; set; }
    public DateTime LastOpened { get; set; }
    public int? AutoLockMinutes { get; set; }    // per-vault override
    public bool? SecurePreviewOnly { get; set; }
    public bool UsesAdminPassword { get; set; }
    public bool PendingRewrap { get; set; }
    public long HighestIndexRevision { get; set; }
}
```

`vault.config` (JSON, in the vault folder): `formatVersion`, `vaultId`, `name`, `created`, `cipherSuite`, `chunkSize`, `kdf` (`algorithm`, `m`, `t`, `p`, `salt`), `slots[]` (`id`, `type`, `kdfSalt`/`hkdfSalt`, `nonce`, `wrappedVmk`, `tag`), `verifier` (nonce, ciphertext, tag), `helloChallenge` (if used), `configMac`. Validate every field and enforce the floors on load. Store the AppLock settings with the same integrity protection as other sections.

## 10. Project structure additions

```
src/AppLock/
  Views/VaultPage.xaml(.cs)                  // list/landing
  Views/Vault/ VaultBrowserPage, VaultCard, AddProgressPanel, SecurePreviewWindow, ImagePreview, TextPreview, PdfPreview, MediaPreview
  Views/Dialogs/ CreateVaultWizard, UnlockVaultDialog, RecoveryKeyDialog, VaultSettingsDialog, ChangePasswordDialog, ConflictDialog, ExportDialog, VerifyDialog, TempSessionRecoveryDialog
  ViewModels/Vault/ VaultListViewModel, VaultBrowserViewModel, VaultItemViewModel, AddJobViewModel
  Services/Vault/
    VaultManager.cs            // registry, lock/unlock state, single-writer lock, events
    Crypto/ VaultCrypto.cs, KeySlots.cs, Argon2Kdf.cs, RecoveryKey.cs, FileObjectWriter.cs, FileObjectReader.cs, ChunkedCryptoStream.cs
    VaultIndex.cs              // in-memory tree, search, atomic persistence, revisions
    VaultStorage.cs            // folder layout, atomic writes, backups, orphan scan
    VaultJobQueue.cs           // add/export/delete jobs with progress, pause/cancel, error list
    SecureDelete.cs            // best-effort overwrite + delete
    TempOpenService.cs         // protected session folders, watchers, write-back, crash recovery
    PreviewService.cs          // seekable decrypting streams for viewers
    HelloKeyService.cs         // Windows Hello key credential / DPAPI fallback
    VaultAutoLockService.cs    // idle, session, power, minimize, hotkey triggers
    SendToService.cs           // .lnk creation + --vault-add IPC handling
    PasswordStrength.cs        // length + common-password list (offline)
  Models/Vault/ VaultConfig.cs, KeySlot.cs, IndexEntry.cs, VaultSettings.cs
  Assets/Vault/ common-passwords.txt   // offline list (license-checked)
tests/AppLock.Tests/Vault/               // see 14
```

Define `IKdf` (with a fast test implementation), `IClock`, `IFileSystem`, `IHelloKeyProvider`, and `IRandom` so crypto and workflow logic can be tested without a UI or real drives.

## 11. UX and quality rules

- Native Fluent look (`ContentDialog`, `CommandBar`, `ListView`, `InfoBar`), Light/Dark/System with theme resources only, live theme switching.
- Plain, calm language; no fear-based wording; every destructive action has a confirmation that names the item and says if it's permanent.
- Accessibility: keyboard navigation (arrows, Enter, Delete, F2 rename, Ctrl+A), screen-reader names, status never by color alone, high contrast.
- Responsiveness: all crypto and file I/O on background threads with cancellation; the UI virtualizes long lists; adding 10,000 files doesn't freeze the UI; progress is accurate.
- Localize all strings; format sizes and dates by locale.
- Performance: encryption is I/O-bound with hardware AES; process chunks in a pipeline (read ‖ encrypt ‖ write); never hold whole files in memory.
- Log events (created, unlocked, failed unlock, locked, add/export counts, errors) to `%APPDATA%\AppLock\logs` with no names or secrets.
- No network calls or telemetry anywhere in this feature.

## 12. Honest limits (shown in the UI under "How the vault protects you")

- **Protected:** file contents, file names and folder structure, while the vault is locked.
- **Not protected:** the vault's existence, object count and approximate sizes; files while the vault is **unlocked** (malware running as you could read them); temporary decrypted copies while a file is open in another app; originals deleted from SSDs (recoverable until overwritten, so use BitLocker); anything you export.
- **No recovery without your password or recovery key.** AppLock can't reset it.
- A person with administrator access can delete the vault folder (loss, not leak). Keep a backup.
- Weak passwords can be guessed offline; use a long passphrase.

## 13. Milestones (build, run, verify each before moving on)

1. **Sidebar and shell:** replace **Rules** with **Vault** (same position, enabled), landing page with empty state and vault card layout, `VaultSettings`/`VaultRegistration` models; theme-correct (Light/Dark/System).
2. **Crypto core (no UI):** key hierarchy, Argon2id, key slots, recovery key, file object format with chunked AES-GCM and random access, config and index formats, atomic storage; full unit tests including tamper/truncation/reorder cases and test vectors. Report test results.
3. **Create wizard, unlock, lock:** wizard (name, location, protection, recovery key with proof step), unlock dialog (password, recovery key), lock, shared lockout counters, password-strength meter.
4. **Vault browser basics:** open an unlocked vault, folder tree, add files/folders with progress, **verify-before-delete** of originals, error list, export with hash verification, new folder/rename/move/delete, trash.
5. **Secure preview:** images, text, PDF, audio/video from in-memory streams; capture exclusion; nothing written to disk (verify with Process Monitor).
6. **Open with another app:** protected temp sessions, generic names, watcher and write-back with conflict handling, secure cleanup, crash recovery dialog.
7. **Auto-lock and AppLock integration:** idle, session lock, sleep, minimize, exit, emergency hotkey, Lock all, admin-password change re-wrap with "still uses previous password" handling, security-questions notice, single-writer lock.
8. **Windows Hello unlock slot** (key-credential method with the DPAPI-gate fallback, tested on real devices).
9. **Management and safety tools:** multiple vaults, open existing vault, backup, verify (including deep verify), repair from `.bak`, orphan cleanup, per-vault settings, Send to shortcut, change password, re-generate recovery key, delete vault.
10. **Polish and review:** tests (section 14), accessibility and theme pass, performance check on large vaults, `NOTES.md` (design decisions, residual risks, library choices), manual checklist, and a written **security review** of the crypto code.
11. *(Phase 2)* Virtual drive mount (WinFsp/Dokan; check licensing, require explicit user installation), Explorer context-menu command, content search, size padding, multi-user sharing.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 14. Tests

- **Crypto:** round-trip for 0 bytes, 1 byte, chunk size ±1, multiple chunks, multi-GB streaming; random-access reads at arbitrary offsets; deterministic test vectors for HKDF and key wrapping; wrong password and wrong recovery key; each key slot opens the vault; slot add/remove/replace; password change re-wrap leaves file data untouched; KDF floors enforced against a downgraded config.
- **Tamper detection:** flip a bit in the config, header, chunk, index or tag; truncate a file; remove or reorder chunks; swap two objects; replay an old index (revision warning); every case must fail closed with a clear error and no partial plaintext treated as valid.
- **Workflow and crash safety:** interrupted add (kill between each write step) leaves either the old or the new consistent state and never deletes the original early; verify-before-delete rejects a corrupted write; orphan detection; index/config `.bak` fallback; name collisions; Unicode and long paths; symlinks skipped; 100,000 items performance; trash retention.
- **Temp sessions:** ACL on the session folder, generic names, write-back with conflict detection, cleanup on exit/lock, crash recovery.
- **Auto-lock:** every trigger locks and zeroes keys (test with a fake key holder asserting `ZeroMemory`), including with open temp sessions.
- **Integration:** admin-password change re-wraps reachable vaults and flags unreachable ones; the security-questions reset does not touch vaults; shared lockout counters; settings migration; no secrets or names in logs (log-capture test).
- **Fuzzing:** malformed config, index, and object headers must not crash or hang.

## 15. Manual test checklist

- [ ] **Vault** replaces **Rules** in the sidebar (same position, enabled, readable in Light/Dark/System); Websites still shows as before
- [ ] Create a vault with the AppLock admin password and a weak password: the weakness warning appears; the recovery-key proof step blocks creation until completed
- [ ] Add files and a folder tree (including a 2 GB file and thousands of small files): progress is accurate, originals are deleted only after verification, pause/cancel works, skipped files are listed
- [ ] Browse the on-disk vault folder in Explorer: no readable file names, no plaintext, only `vault.config`, `index.enc` and random object files
- [ ] Lock and unlock with password, with the recovery key, and with Windows Hello; wrong passwords trigger the shared lockout; password boxes clear after each attempt
- [ ] Preview an image, text file, PDF, MP3 and MP4 (seeking works) while Process Monitor confirms nothing is written to disk; screenshots/screen sharing don't show the vault window
- [ ] Open a `.docx` with the default app, edit and save, close: changes are in the vault, the temp copy is gone; kill AppLock mid-edit and restart: the recovery dialog offers to save back
- [ ] Idle timeout, minimizing, PC lock, sleep, and Ctrl+Alt+L all lock the vault and close previews; temp sessions are saved back and wiped first
- [ ] Change the AppLock admin password: reachable vaults are re-wrapped; a vault on a disconnected drive shows "Still uses your previous password" and can be synced later; the security-questions reset does not unlock vaults
- [ ] Corrupt a chunk, the index, or `vault.config` in a copy: errors are clear, fallback to `.bak` works, nothing returns partial data as valid
- [ ] Backup copies the vault and verifies it; deep verify finds a corrupted object; opening an existing vault from an external drive works on a second PC with the password or recovery key
- [ ] Trash restore and permanent delete work; the trash retention timer removes old items
- [ ] Send to shortcut adds files to a chosen vault (asks for unlock first)
- [ ] No passwords, keys, file names or plaintext appear in logs; no network requests are made
- [ ] Security review notes are written and the crypto unit tests all pass before the feature is released
