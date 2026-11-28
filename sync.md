# Sync & Remote Control — Add-on Spec for AppLock (Others page)

This is an add-on to `instructions.md`. Read that file first. Implement on top of the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure (CommunityToolkit.Mvvm), shared services (HotkeyService, elevation helper, shared toast service, Island live-card queue, protected settings JSON with DPAPI/HMAC, admin password / Windows Hello verification, `IClock`), and the Light/Dark/System theme rules.

It builds on these existing specs and reuses their services instead of duplicating logic: `island.md` + `controls-addon.md` (power, radios, Bluetooth, brightness), `brightness.md`, `volume-mixer.md`, `speed-meter.md`, `clipboard.md`, `bitlocker.md`, `connection-firewall.md`, `network-scanner.md`, `open-ports.md`, `web-filter.md`, `screen-time.md`, `self-discipline.md`, `pc-checker.md`, `vault.md`.

## 0. Summary and the decisions I made (please confirm)

A **Sync** card on the **Others** page (order **100**, after Island 90) lets the user **sign in** (Continue with Google, or email + password) to **Firebase**, link this PC to their account, and then **see and control the PC from a web dashboard** from anywhere: lock, sleep, shut down, radios, brightness, audio, media, notes, reminders, clipboard, running/protected apps, screen time, BitLocker drives, internet/website rules, performance, and more.

Because this is remote control of a whole PC, **security is the main design problem**, not an afterthought. Decisions:

1. **Database: Firebase Realtime Database** (not Firestore). It is cheaper and faster for small, frequent state updates and live command delivery. Auth: Firebase Authentication (Google + email/password). Web hosting: Firebase Hosting. **Cloud Functions are optional (phase 2)** so the core works on the free Spark plan.
2. **End-to-end encryption (E2EE) for all content data** (notes, clipboard, app lists, histories, device names, media titles, etc.) with a **Sync passphrase** the user chooses. Firebase and the developer never see content. This is an addition to what was requested, and it is **required** for a responsible design.
3. **Sensitive commands are protected in tiers** (section 5.4): the web must prove it knows the Sync passphrase, and the most dangerous ones need a step-up and a cancel window on the PC. **Permissions are enforced on the PC, never trusted from the cloud.**
4. **Saved Wi-Fi passwords** are supported **on demand only** (the PC sends one password when asked, encrypted, and it is deleted after about a minute), **not** stored in the cloud. Off by default.
5. Items in the request that were ambiguous were interpreted as follows. **Tell me if any is wrong:**
   - "Volume mixture: camera brightness sync" → the **per-app volume mixer** (from `volume-mixer.md`) **and** the **webcam image brightness** (only when the camera exposes the control).
   - "Wifi (schedule)" → a **Wi-Fi on/off schedule** that runs on the PC (so it works offline) and can be edited from the web.
   - "Protected / internet / website lists sync" → mirrored to the web; **adding protection is easy, weakening it is a protected action.**
   - "Keyboard enable/disable" → block keyboard input on the PC (mouse still works), with local recovery.

## 1. Goals and non-goals

**Goals:** account sign-in on PC and web; live status; remote commands with a clear audit trail; encrypted sync of notes, reminders, clipboard; read-mostly mirrors of lists and histories; multiple PCs per account; safe defaults (everything off until the user opts in).

**Non-goals (never build these, and say so in the UI):** remote desktop/screen viewing, remote keyboard/mouse, file access, **Vault access of any kind**, listening to the microphone or camera, changing the AppLock admin password, changing the Sync passphrase remotely, uninstalling AppLock, running arbitrary commands, family/multi-user sharing of one PC (single owner per account in v1), Wake-on-LAN (phase 2).

## 2. Architecture

```
Browser dashboard (Firebase Hosting, TypeScript SPA/PWA)
   │  Firebase Auth + Realtime Database (JS SDK) + WebCrypto (E2EE, command proofs)
   ▼
Firebase (Auth · Realtime Database · Hosting · App Check)   ← sees only ciphertext + minimal metadata
   ▲
   │  REST + Server-Sent Events (streaming), Identity Toolkit REST, Google OAuth (loopback + PKCE)
AppLock on the PC (C#): SyncService
   ├─ AuthService        (sign-in, tokens)
   ├─ CryptoService      (key slots, AES-GCM envelopes, command proofs)
   ├─ RtdbClient         (REST/SSE, reconnect, ServerClock)
   ├─ SyncEngine         (modules: collect state → encrypt → upload; apply remote changes)
   └─ CommandProcessor   (validate → local policy → execute → result → audit)
        └─ existing AppLock modules (power, radios, audio, brightness, bitlocker, firewall, ...)
```

- The PC talks to Firebase over **HTTPS only**. **Never ship a Firebase Admin SDK, a service-account key, or any secret** in the desktop app or the web bundle. The Firebase API key and project config are public by design; security comes from **Auth, Security Rules, App Check, and E2EE**.
- The PC app must be **running in the user's session** to act. It cannot wake a sleeping or powered-off PC and cannot answer UAC prompts remotely (section 16).

## 3. Firebase setup (document in `docs/firebase-setup.md`; the developer does this once)

1. Create a Firebase project. Enable **Authentication** with **Google** and **Email/Password**; require **email verification** (enforced by the app and the rules). Recommend enabling **multi-factor authentication** (Identity Platform) and turn on password policy (min 10 chars) if available. Keep "One account per email address" enabled.
2. Create a **Realtime Database** in a region near most users; deploy the rules from `firebase/database.rules.json` (section 7.2).
3. Enable **App Check** (reCAPTCHA Enterprise/v3 for the web app; debug tokens for development) and enforce it for Realtime Database and Auth when stable. Restrict the web API key by HTTP referrer; restrict the Desktop OAuth client.
4. Create an **OAuth client of type Desktop** in Google Cloud for the PC Google sign-in (loopback redirect). Configure the OAuth consent screen (needed before public use).
5. **Firebase Hosting** for the web app (`firebase.json` with the security headers in section 11.3).
6. Optional later (Blaze plan, phase 2): Cloud Functions for push alerts (FCM), device custom claims, session revocation, and scheduled cleanup.
7. Use the **Firebase Emulator Suite** (Auth + Database + Hosting) for all development and for the rules tests. Never develop against production data.
8. Budget note: Spark limits include **100 simultaneous Realtime Database connections** (each online PC and each open dashboard tab is one) and a monthly download cap. Design for low traffic (section 8.3) and tell the developer when to move to Blaze.

## 4. Accounts and sign-in

### 4.1 On the PC (Sync settings page › Account)
UI: **Continue with Google** button, a divider, then **Email** + **Password** with **Sign in** / **Create account**, **Forgot password?**, show/hide eye. Signed-in state shows the account email, a verified badge, **Sign out**, and **Delete account and cloud data**.

- **Email/password:** Identity Toolkit REST (`accounts:signUp`, `accounts:signInWithPassword`, `accounts:sendOobCode` for `VERIFY_EMAIL` and `PASSWORD_RESET`, `accounts:lookup`, `accounts:delete`). Client-side password rules: min 10 characters, reject common passwords, show a strength hint. Map error codes (`EMAIL_EXISTS`, `INVALID_LOGIN_CREDENTIALS`, `TOO_MANY_ATTEMPTS_TRY_LATER`, `USER_DISABLED`) to clear messages. **Require a verified email before Remote control or any data sync can be turned on**; show `Verify your email` with a Resend button and poll `accounts:lookup`.
- **Google:** OAuth 2.0 **installed-app flow in the system browser** (never an embedded webview, Google blocks it): start a loopback listener on `127.0.0.1` with a random port, **PKCE (S256)**, random `state` and `nonce`, scopes `openid email profile`; exchange the code at Google's token endpoint; then call Firebase `accounts:signInWithIdp` with the Google ID token (`providerId=google.com`) to get the Firebase ID token and refresh token. Validate `state` and `nonce`, bind the listener to loopback only, close it after one use, time out after 3 minutes. Handle "email already exists with another sign-in method" with a clear message (`Sign in with the method you used first`).
- **Tokens:** keep the **refresh token** protected with DPAPI (CurrentUser) inside AppLock's protected storage; keep the ID token in memory only; refresh through `securetoken.googleapis.com` about 5 minutes before expiry and on `auth_revoked`. Never log tokens, never write them to the settings JSON in plain text. Sign out wipes them.
- **Metered connection / offline:** sign-in needs internet; the rest of AppLock keeps working normally when Sync is off or offline.

### 4.2 On the web
Firebase JS SDK: Google (popup, with redirect fallback), email/password, email verification, password reset, optional MFA. Default **session persistence = this tab/session only**; an explicit **Keep me signed in on this device** option (with a warning) uses local persistence. Automatic sign-out after 30 minutes of inactivity unless "keep me signed in" is on (the Sync passphrase unlock is always forgotten at sign-out or tab close).

### 4.3 Linking this PC (device registration)
After sign-in and **after the user sets the Sync passphrase** (section 6), the PC registers itself: it creates `deviceId` (random 128-bit, stored locally), a name (default = computer name, editable), and writes its metadata. The PC appears in the dashboard's device list. **Unlinking** a PC (from the PC or from the web) removes its cloud data and stops syncing.

## 5. Security model

### 5.1 Threats this design must handle
1. **Account takeover** (stolen password or session) → attacker tries to control the PC or read data. Mitigations: email verification, MFA recommendation, App Check, **Sync passphrase proofs for risky commands**, E2EE for content, PC-enforced policy, audit log, local kill switch.
2. **Compromised web page** (XSS) → steals passphrase or session. Mitigations: strict CSP, Trusted Types, no third-party scripts, non-extractable keys, passphrase only in memory (section 11.3).
3. **Replay/forgery of commands** → timestamps, nonces, expiry, one-time ids, HMAC proofs (section 5.5).
4. **Malicious or curious cloud operator / data leak** → E2EE; only minimal metadata visible.
5. **Local abuse** (someone at the PC) → enabling Sync and Remote control requires AppLock authentication; sign-out/disable is always possible locally.
6. **Malicious content from the PC** (a clipboard item or window title is attacker-controlled text) → never rendered as HTML (section 11.3).

### 5.2 Principles
- **The cloud is untrusted for permissions.** What a web session may do is decided by the **local policy stored on the PC** (protected settings), never by a flag in the database.
- **Default deny.** Sync is off, Remote control is off, every data category is off, until the user turns them on, each with a plain-language explanation.
- **Weakening needs more proof than strengthening** (lock yes, unlock no).
- **Everything is logged** locally and in the cloud audit list (metadata only).
- **Tell the user:** the Island shows a **Remote chip** while a dashboard session is active and a live card for each remote command (`Remote: Lock (from Chrome on Android)`), with a setting to turn those cards off.

### 5.3 Local controls (always available on the PC)
- **Pause remote control** button/tray item/Island chip/hotkey (`Ctrl+Alt+Shift+R`): instantly stops executing remote commands. **No authentication needed to pause** (it only strengthens local safety). Resuming needs authentication.
- **Sign out / Unlink** needs no authentication to *stop* syncing (it only removes access). Turning Remote control **on**, changing what is synced, changing policy, or viewing the recovery key needs the AppLock admin password / Windows Hello.
- A **kill switch on the web account side:** `Sign out of all web sessions` (needs Functions in phase 2; until then the user can change the account password, and the PC shows a warning that existing sessions stay valid up to the Firebase token lifetime).

### 5.4 Command tiers (enforced on the PC)
| Tier | Meaning | Needed to run |
|---|---|---|
| **T1 Signed-in** | Low-risk or reversible | Valid signed-in owner session |
| **T2 Proof** | Medium risk | T1 + **HMAC proof** made with the key derived from the Sync passphrase (the web unlocked it this session) |
| **T3 Step-up** | High risk | T2 + **fresh passphrase entry within the last 5 minutes** (the proof includes a step-up timestamp) + a **delay and local cancel window** where applicable |

Each **command class** has a shipped minimum tier. The PC owner may **raise** a class's tier or **turn the class off** in settings; they cannot lower it below the shipped minimum. Commands also pass **rate limits** (10 per minute per device; after **5 failed proofs in 10 minutes** the PC blocks T2/T3 for 15 minutes, shows an Island card, and logs a security event).

### 5.5 Command envelope, freshness, and replay protection
A command is a node `devices/{deviceId}/commands/{cmdId}`:

```json
{ "type":"power.lock", "payload":{...}, "createdAt":{".sv":"timestamp"}, "expiresAt":1790000000000,
  "nonce":"base64-16-bytes", "tier":1, "session":"webSessionId", "proof":"base64(hmac) or null",
  "stepUpAt":1789999990000, "status":"pending" }
```

The PC accepts a command only if **all** hold: schema and size valid; `type` is known and enabled by local policy; **not expired** (default lifetime 60 s, max 10 min, compared against a **ServerClock** synchronized by reading `{".sv":"timestamp"}`, not the PC's raw clock); `cmdId` not seen before (the PC persists processed ids for 24 h); the **tier requirement is met** (HMAC over the canonical JSON of `{deviceId, cmdId, type, payload, expiresAt, nonce, tier, stepUpAt}` with the `cmd-v1` key, constant-time compare; for T3 `stepUpAt` within 5 minutes of `createdAt`); rate limits; and the module's own preconditions (elevation, hardware present). It then writes `results/{cmdId}` = `{status: ok | failed | rejected | expired | needs_elevation | needs_local_approval | cancelled, message, at}` and deletes the command node (the audit entry stays).

## 6. Cryptography (must be identical in C# and TypeScript)

Use only standard primitives. Do not invent crypto. Treat this like `vault.md`: a **cross-language test-vector file** (`docs/crypto-vectors.json`) is mandatory and both implementations must pass it before any data is synced. **Recommend an independent crypto review before public release.**

- **Sync Master Key (SMK):** 256-bit random, created on the PC when the passphrase is first set. It never leaves a device unwrapped.
- **Key slots** (stored in the cloud as wrapped blobs, LUKS-style): **passphrase slot** = `AES-256-GCM(KEK_p, SMK)` where `KEK_p = Argon2id(passphrase, salt)`; **recovery slot** = same with `KEK_r = HKDF(recoveryKey)`. Changing the passphrase re-wraps the SMK; no data re-encryption.
- **KDF:** Argon2id, **m ≥ 64 MiB, t ≥ 3, p = 1**, 16-byte random salt, parameters stored and versioned; enforce floors on read (reject weaker). Browser: Argon2id in WASM (a pinned, audited library such as `hash-wasm`, loaded locally with Subresource Integrity, not from a CDN). C#: `Konscious.Security.Cryptography.Argon2` (MIT) or libsodium bindings. Fallback only if WASM is unavailable: PBKDF2-HMAC-SHA256 ≥ 600,000 iterations.
- **Derived keys:** `HKDF-SHA256(SMK, info)` → `data-v1` (AES-256-GCM data key), `cmd-v1` (HMAC-SHA256 command proof key), `meta-v1` (reserved).
- **Envelope** for every encrypted value: `{ "v":1, "n":"base64(12-byte random nonce)", "c":"base64(ciphertext||tag)" }`, **AES-256-GCM**, **AAD = `"<database path>|<schema version>"`** so ciphertext cannot be moved between nodes. Compress (deflate/gzip) lists **before** encrypting. Use a fresh random 96-bit nonce per encryption; rotate the data key (`data-v2`) if one key ever exceeds 2^31 messages.
- **Passphrase rules:** min 10 characters, strength check, not equal to the account password (cannot verify; warn in UI), never stored. A **passphrase verifier** (MAC of a constant under `meta-v1`) lets the web check the entry without exposing the key.
- **Recovery key:** 160+ bit, shown once as grouped Base32 with a "I saved it" proof step; **there is no backdoor**. Losing both passphrase and recovery key means the encrypted cloud data is unrecoverable; offer **Reset sync** (deletes encrypted cloud data and creates new keys).
- **Web handling:** derive keys with WebCrypto, keep them as **non-extractable `CryptoKey` objects** in memory only; never write the passphrase or keys to storage; zero/clear on sign-out, tab close, idle timeout. **PC handling:** SMK protected with DPAPI on disk for unattended operation (this is how the PC can sync while the user is away); wipe on unlink.
- **Metadata that stays plain:** device name label is **encrypted**; plain fields are limited to `online`, `lastSeen`, app version, battery %, charging, radio on/off flags, mute/brightness/volume numbers, CPU/RAM/disk/GPU percentages, command ids/types/statuses and timestamps, sizes. State this honestly in the privacy text.

## 7. Realtime Database

### 7.1 Data layout
```
/users/{uid}/
  crypto/            { kdf:{alg,m,t,p,salt}, slots:{pass:{envelope}, recovery:{envelope}}, verifier:{envelope}, v:1 }
  devices/{deviceId}/
    meta/            { app:"2.x", os:"Windows 11", nameEnc:{envelope}, createdAt, platform }
    presence/        { online:true, lastSeen:ts }                 // heartbeat
    live/            { battery:{...}, radios:{...}, audio:{...}, display:{...}, perf:{...}, keyboard:{...} }   // plain control state
    data/            { <category>: {envelope} }                    // E2EE snapshots (apps, media, devices, lists...)
    history/         { screentime/{yyyy-MM-dd}:{envelope}, usage/{yyyy-MM-dd}:{envelope} }
    commands/{cmdId} (see 5.5)
    results/{cmdId}  { status, message, at }
    audit/{id}       { type, tier, session, status, at }           // metadata only, capped to last 200
    viewers/{sessionId} { ts, ua }                                  // web heartbeat → fast cadence
  sync/              // account-wide E2EE content shared by all devices and the web
    notes/{noteId}      { envelope, updatedAt, deviceId, deleted }
    reminders/{id}      { envelope, updatedAt, deviceId, deleted }
    clipboard/{id}      { envelope, createdAt, deviceId }
  sessions/{sessionId}  { createdAt, ua, lastSeen }                // web sessions list
```
Every write is capped (each value ≤ 200 KB, notes ≤ 100 KB, total synced content ≤ 20 MB per account in v1). Large lists are chunked or trimmed with documented limits.

### 7.2 Security Rules (write complete rules in `firebase/database.rules.json` and test with the emulator)
Skeleton (the agent must complete, tighten, and unit-test it with `@firebase/rules-unit-testing`):

```json
{
  "rules": {
    ".read": false, ".write": false,
    "users": { "$uid": {
      ".read":  "auth != null && auth.uid === $uid && auth.token.email_verified === true",
      ".write": "auth != null && auth.uid === $uid && auth.token.email_verified === true",
      "devices": { "$deviceId": {
        "commands": { "$cmdId": {
          ".validate": "newData.hasChildren(['type','createdAt','expiresAt','nonce','status','tier']) && newData.child('type').isString() && newData.child('type').val().length <= 40 && newData.child('expiresAt').isNumber() && newData.child('expiresAt').val() <= now + 600000 && newData.child('tier').isNumber()"
        } },
        "live": { ".validate": "newData.hasChildren()" }
      } },
      "$other": { ".validate": true }
    } }
  }
}
```
Required rule behaviors: only the signed-in, **email-verified** owner can read/write their subtree; **size and type validation on every node**; commands must have the required fields and a bounded `expiresAt`; no wildcard writes outside `users/$uid`; deny everything else. Add rule tests for: another user cannot read/write, unverified email denied, oversized value denied, malformed command denied, expired-far-future command denied. (Note: both the PC and the web act as the same Firebase user, so rules cannot tell them apart; that is why **the PC re-validates everything itself** and ignores any cloud-stored policy. Optional phase 2: Cloud Functions mint a custom token with a `role: "device"` claim so rules can restrict who may write `live/data/results`.)

## 8. Sync engine (PC)

### 8.1 Transport
`RtdbClient` uses the Realtime Database **REST API with Server-Sent Events streaming** (`Accept: text/event-stream`, `auth=<idToken>`), implemented as a small, tested class (no heavy SDK): parse `put`/`patch`/`keep-alive`/`cancel`/`auth_revoked`, apply merges to a local tree, reconnect with exponential backoff + jitter, re-auth on token refresh. Writes use PATCH/PUT with ETag/conditional where needed. All calls are async off the UI thread, with timeouts and cancellation. Listen **only** to: this device's `commands`, `sync/*` (when content sync is on), `viewers`, and the account's `crypto` node. Write `presence/lastSeen` every 60 s while online (REST has no `onDisconnect`; the web treats a device as offline if `lastSeen` is older than 150 s).

### 8.2 Modules
Each synced category is a small `ISyncModule`: `Id`, `DataClass` (Plain | Encrypted), `Collect()` (snapshot or delta), `Apply(command)`, `Cadence` (OnChange | Interval | OnDemand), `RequiresViewer` (bool), `Enabled` (user setting). The engine **coalesces** changes, **hashes** each snapshot and uploads only if changed, **debounces** writes (≥ 1 s), **diff-patches** top-level keys, and queues uploads while offline (persisted, capped at 5 MB, oldest dropped).

### 8.3 Cadence and cost (viewer-aware)
- **No dashboard open** (no fresh `viewers` heartbeat): upload only **changes** to cheap plain state (battery plug in/out, radios, mute, brightness) and a slow 5-minute full refresh of lists; `perf` is **not** sent.
- **Dashboard open** (a viewer heartbeat in the last 45 s): switch to the **fast cadence**: `perf` every 2 s, battery 10 s, media/audio on change, lists every 15–30 s while their tab is visible (the viewer writes `viewers/{id}.tab` so the PC sends only what is being looked at).
- Skip telemetry on **metered connections** unless the user allows it; stop everything while the session is locked? No: keep command listening and presence running when locked; throttle telemetry.
- Target: under 5 MB/day with no viewer and under 15 MB/hour with a viewer on the dashboard. Report real numbers in the final milestone.

### 8.4 Conflict resolution (notes, reminders)
Per item `updatedAt` (server time) + `deviceId` + a per-item `rev`. **Last write wins**; if two devices edit the same item within 60 s of each other from different devices, **keep both**: the loser is saved as `"<title> (conflict <device>, <time>)"`. Deletes are **tombstones** (`deleted:true`) kept 30 days. Clipboard items are append-only with TTL.

## 9. Command processor (PC)

Pipeline: receive → validate (5.5) → **local policy check** (class enabled, tier) → **local approval** if configured → **cancel window** if applicable → execute on the right module (UI-thread-safe, off the UI thread when possible) → result → audit → Island card. All execution happens inside existing, already-tested modules; **never** `cmd /c` strings built from cloud data; every payload field is validated against a schema (types, ranges, allowed enums, ids that must exist locally). Unknown or disabled types are rejected with `rejected`.

**Never available remotely (hard-coded deny list):** disable AppLock master protection, change the admin password or recovery/security questions, access or unlock the **Vault**, uninstall, change Sync settings/policy/passphrase, run arbitrary commands, read files, take screenshots, control mouse/keyboard input injection.

**Elevation:** commands that need administrator rights (BitLocker, firewall rules, reading Wi-Fi keys, DNS changes) return `needs_elevation` if AppLock is not elevated; **a UAC prompt cannot be answered remotely**. Offer the setting **"Start AppLock with administrator rights"** (via the existing elevation helper / scheduled task with highest privileges, only if the user explicitly turns it on, with a clear warning).

## 10. Feature catalog (everything requested)

Legend: **Plain** = visible to Firebase; **E2EE** = encrypted; **Tier** = minimum command tier (user may raise). Cadence per section 8.3. All data categories are individually switchable on the PC.

| # | Feature | Direction | Data / command | Class | Tier | Notes |
|---|---|---|---|---|---|---|
| 1 | **Lock** | web → PC | `power.lock` | | **T1** | `LockWorkStation` (Windows lock screen) |
| 2 | **Sleep** | web → PC | `power.sleep` | | **T1** | PC then goes offline; UI says so |
| 3 | **Restart / Shut down** | web → PC | `power.restart`, `power.shutdown` | | **T3** | Default **30 s delay** with an Island/toast card **Cancel** on the PC and a **Cancel** command from the web; no force-close of apps by default (so unsaved work can block it); optional `force` flag is T3 + confirm |
| 4 | **Wi-Fi on/off, Bluetooth on/off** | web → PC | `radio.wifi.set`, `radio.bluetooth.set` | state: Plain | **T2** | **Turning Wi-Fi off can cut the PC off from remote control forever.** Web asks `Turn off for: 10 min / 1 h / until turned on locally`; the PC **auto-restores** after the chosen time (default 10 min); warn if no Ethernet. Bluetooth has no such risk. |
| 5 | **Wi-Fi schedule** | both | `schedule.wifi.set`; list of `{days, offTime, onTime}` | E2EE | **T2** | Stored and **evaluated on the PC** (works offline), uses `ScheduleEvaluator`/`IClock` from Self-Discipline; handles midnight spans and DST |
| 6 | **Connected Wi-Fi and Bluetooth devices, with battery** | PC → web | `net.wifi` (SSID, band, signal), `bt.devices[{name, connected, battery?}]` | E2EE | read-only | Reuses `controls-addon.md` services; battery only if the device reports it (never `0%` as a guess) |
| 7 | **Battery % and time remaining; plugged in/out** | PC → web | `battery{percent, charging, pluggedIn, remainingMinutes, saver}` + event on plug change | Plain | read-only | Immediate on change; web shows a timeline; push alerts are phase 2 (Functions) |
| 8 | **Battery saver toggle** | web → PC | `power.batterySaver.set` | state: Plain | **T1** | No official toggle API: **spike** with `powercfg` Energy Saver threshold (`SUB_ENERGYSAVER ESBATTTHRESHOLD`) and read `PowerManager.EnergySaverStatus`; remember and restore the user's original threshold. Only active on battery. Name differs on Windows 11 (Energy saver). Report honestly if unreliable. |
| 9 | **Microphone list, per-mic volume, default marked** | both | `audio.capture.devices[{id,name,volume,muted,isDefault}]`; `audio.capture.setVolume` | list: E2EE (names), numbers Plain? keep whole object E2EE | **T1** | Core Audio endpoint volume |
| 10 | **Set default microphone / default speaker** | web → PC | `audio.capture.setDefault`, `audio.render.setDefault` | | **T2** | Windows has no public API for this; use the widely used **undocumented `IPolicyConfig`** COM interface behind an `IDefaultAudioDeviceSwitcher` with a spike and graceful failure (`Not supported on this Windows build`). |
| 11 | **Microphone mute/unmute + lock/unlock** | web → PC | `audio.capture.mute`, `audio.capture.lock` | state: Plain | mute **T1**, unmute **T2**, lock **T1**, unlock **T2** | **Mic lock** (`MicGuard`): while locked the PC **re-mutes the microphone whenever it is unmuted** (endpoint-volume callback, includes newly plugged mics) and shows a card; unlocking needs a remote T2 command or local AppLock authentication. State plainly: a muted mic is muted at the Windows level; a local admin can bypass. |
| 12 | **Brightness control** | web → PC | `display.brightness.set {monitorId, value}` | state: Plain | **T1** | Reuses `brightness.md`; per monitor when supported; hidden if unsupported |
| 13 | **Brightness blur effect toggle** | web → PC | `display.blur.set {on}` | | **T1** | The blur overlay from `brightness.md` |
| 14 | **Volume mixer (per-app volumes)** | both | `audio.apps[{app, volume, muted}]`; `audio.app.setVolume`, `audio.app.mute` | E2EE (app names) | **T1** | Reuses `volume-mixer.md` |
| 15 | **Camera brightness** | web → PC | `camera.brightness.set` | | **T1** | Webcam image brightness via `IAMVideoProcAmp` (Media Foundation), **only for cameras that expose it**; hidden otherwise. This is **image brightness, not camera access**; nothing is captured. |
| 16 | **Speaker output selection and volume/mute** | both | `audio.render.devices[...]`, `audio.render.setVolume`, `audio.render.mute`, default via #10 | E2EE | volume **T1**, default **T2** | |
| 17 | **Media (multiple sessions)** | both | `media.sessions[{sessionId, app, title, artist, state, canNext, canPrev}]`; `media.play|pause|next|prev {sessionId}` | E2EE | **T1** | `GlobalSystemMediaTransportControlsSessionManager`; titles encrypted; **no album art** uploaded in v1 |
| 18 | **Keyboard enable/disable** | web → PC | `input.keyboard.set {enabled}` | state: Plain | **T2** | Low-level keyboard hook swallows **non-injected** keys (so the on-screen keyboard still works for authentication); mouse unaffected; cannot block Ctrl+Alt+Del. **Local recovery:** the hook disappears if AppLock exits; tray/Island chip `Keyboard disabled — Enable` (authenticate with Windows Hello or the on-screen keyboard); a held chord `Ctrl+Alt+Shift+Esc` for 3 s re-enables; optional auto re-enable timer from the web. |
| 19 | **Notes sync** | both | `sync/notes` | E2EE | n/a (content) | From the Island Note tab; conflict rules 8.4; ≤ 100 KB each |
| 20 | **Reminders sync** | both | `sync/reminders` | E2EE | n/a | Fire on the chosen device (default the PC); web can add/edit/complete; the PC fires them offline from its local copy |
| 21 | **Clipboard history sync** | both | `sync/clipboard` (text ≤ 64 KB; images off by default, optional ≤ 1 MB) | E2EE | n/a; web→PC `clipboard.set` **T2** | Reuses the clipboard source; **respects the exclusion flags** (password managers) and an excluded-apps list; **one-tap Pause clipboard sync**; TTL default 7 days (auto-delete); retention and item count configurable |
| 22 | **Running applications** | PC → web | `apps.running[{name, exe}]` (no window titles by default) | E2EE | read-only | Only while the Apps tab is open (viewer-aware); "End task" is **phase 2** (T3) |
| 23 | **Protected applications** | both | `protected.apps[{id, name, mode, locked, selfDiscipline summary}]`; `protected.lock {id}` , `protected.add {...}`, `protected.unlock {id, minutes}`, `protected.remove {id}` | E2EE | lock/add **T2**, unlock/remove **T3** | Strengthening is easy; weakening is protected. Uses existing `LockManager`. |
| 24 | **Screen time history** | PC → web | `history/screentime/{day}` per-app totals (from `screen-time.md`) | E2EE | read-only | Daily summaries (last 90 days), plus today's hourly while the tab is open; limits can be edited later as T2 (phase 2) |
| 25 | **BitLocker drives lock/unlock** | both | `bitlocker.drives[{letter,label,status,protection,percent}]`; `bitlocker.lock {letter}`, `bitlocker.unlock {letter, passwordEnc}` | E2EE | lock **T2**, unlock **T3** | Reuses `bitlocker.md`. OS drive cannot be locked. Unlock sends the drive password **encrypted in the browser** under the command key; the PC decrypts, unlocks, and **deletes the command node immediately**; never logged. Needs elevation (section 9). |
| 26 | **Internet access block list** | both | `net.rules[{app, blocked}]`; `net.rule.block {app}` (T2), `net.rule.allow {app}` (T3) | E2EE | block **T2**, allow **T3** | From `connection-firewall.md`; needs elevation |
| 27 | **Devices on the Wi-Fi network** | PC → web | `lan.devices[{name, ip, mac, vendor, type, firstSeen, lastSeen, isNew}]` | E2EE | read-only | From `network-scanner.md`; new-device alerts via push are phase 2 |
| 28 | **Open ports** | PC → web | `ports.listening[{proto, port, app, scope}]` | E2EE | read-only (remote block = phase 2, T2) | From `open-ports.md` |
| 29 | **Websites allow/block lists** | both | `web.allow[]`, `web.block[]`, categories on/off | E2EE | add to block **T2**; add to allow / remove from block / turn category off **T3** | From `web-filter.md`; the PC's own auth rules still apply to local edits |
| 30 | **Saved Wi-Fi passwords** | web → PC → web | list of saved SSIDs (E2EE); `wifi.password.get {ssid}` → password returned **encrypted** in `results/{cmdId}`, deleted after 60 s | E2EE | **T3** | **Off by default; highest-risk item.** On-demand only, **no copy stored in the cloud**. Needs elevation (`netsh wlan show profile key=clear`). Enabling requires local authentication + an explicit risk acknowledgement; every request is audited and shown as an Island card. The web shows the password for 30 s with a copy button. **Recommend leaving this off.** |
| 31 | **Performance: CPU, RAM, disk, GPU (and network speed)** | PC → web | `live/perf {cpu, ram, disk{usage,read,write}, gpu, net{down,up}}` | Plain | read-only | Only while a viewer is present (2 s); reuses the Speed Meter / PDH counters; GPU via PDH GPU engine counters |
| 32 | **Usage history** | PC → web | `history/usage/{day}` active seconds, first/last active, uptime/sessions (from Screen Time + boot/lock events) | E2EE | read-only | Daily summaries |
| 33 | **Battery plugged in / out** | PC → web | see #7 | Plain | read-only | |
| 34 | **Presence and device list** | PC → web | `presence {online, lastSeen}`, device name | Plain/E2EE | | Offline after 150 s without heartbeat; the web shows `Last seen …` and **greys out** commands for offline devices |

## 11. Web dashboard

### 11.1 Stack and structure
- **TypeScript + Vite + Svelte** (or Preact), Firebase JS SDK (modular: Auth, Database, App Check), WebCrypto + a pinned Argon2id WASM library, **PWA** (installable, works well on a phone), light/dark theme, responsive mobile-first layout, localization-ready. **No UI frameworks with heavy runtime, no third-party scripts, no analytics.**
- Pages: **Sign in** (Google, email/password, reset, verify), **Unlock sync** (enter the Sync passphrase; recovery key option), **Devices** (cards: name, online dot, battery, last seen), **Device** with tabs:
  - **Overview:** status, battery with time remaining, **Lock / Sleep / Restart / Shut down** buttons (confirm dialogs; step-up prompt for T3), CPU/RAM/disk/GPU/network live charts when open.
  - **Audio:** speaker volume/mute/default picker, microphones (volume, mute, **lock**, default), per-app mixer, **media** cards per session (app, title, play/pause/next/previous), camera brightness.
  - **Display:** brightness per monitor, blur toggle.
  - **Network:** Wi-Fi/Bluetooth toggles (with the auto-restore choice), Wi-Fi schedule editor, connected Wi-Fi/Bluetooth devices with battery, LAN devices, open ports, internet rules, website lists.
  - **Apps & protection:** running apps, protected apps (lock/add/unlock/remove), keyboard toggle.
  - **Activity:** screen time (day/week charts), usage history.
  - **Content:** notes (editor), reminders, clipboard history (copy, send to PC).
  - **Drives:** BitLocker drives with Lock/Unlock.
  - **Security:** command audit log, web sessions, Wi-Fi passwords (if enabled), change/reset sync, delete data.
- Every command shows **pending → running → done/failed/expired** from `results/{cmdId}`, with clear messages (`PC is offline`, `Needs the PC to run as administrator`, `Blocked by this PC's remote-control settings`).

### 11.2 Behavior
- Writes `viewers/{sessionId}` heartbeat every 20 s while a tab is visible (and the current tab name) so the PC knows to send fast data; removes it on hide/close.
- Never sends a command to an offline device (button disabled with the reason); commands expire in 60 s so a late-arriving PC does not run old actions.
- Dangerous actions use **hold-to-confirm** or a second confirm and, for T3, a **step-up dialog** (`Enter your Sync passphrase again`).

### 11.3 Web security requirements
- Firebase Hosting headers: `Content-Security-Policy: default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; connect-src 'self' https://*.googleapis.com https://*.firebaseio.com wss://*.firebaseio.com https://*.firebaseapp.com; frame-ancestors 'none'; base-uri 'none'; object-src 'none'; require-trusted-types-for 'script'` (adjust for Firebase Auth popups and test), `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy` with everything off, `Cross-Origin-Opener-Policy` set so Google popup sign-in still works (test).
- **Never use `innerHTML`/`eval`**; render all PC-provided text (clipboard, titles, names) with `textContent` only; links only after validating `http(s)` and with `rel="noopener noreferrer"`.
- Passphrase and keys: memory only, non-extractable `CryptoKey`, cleared on sign-out/idle/tab close; never in `localStorage`, cookies, logs, error reports, or URLs.
- Dependencies pinned with a lockfile, no postinstall scripts, `npm audit` in CI, Subresource Integrity for any asset that is not same-origin (prefer none).

## 12. PC UI

### 12.1 Others card
**"Sync"** card (icon cloud/sync, subtitle *"Sign in and control this PC from anywhere"*), registry order **100**. The card shows the state: `Not signed in`, `Signed in as a@b.com · Remote control off`, `Online · Remote control on`, or an error. Clicking opens the **Sync settings page** (SettingsCard/SettingsExpander style like the other Others pages).

### 12.2 Settings page sections
1. **Account:** sign-in UI (4.1), email verification, sign out, delete account.
2. **This PC:** device name, device id (short), linked state, **Unlink this PC**.
3. **Sync passphrase:** set / change / **show recovery key** (needs authentication) / reset sync, with the explanation of what it protects.
4. **Remote control:** master switch (needs authentication to turn on; **Pause** needs none), and the per-**command class** table (class, on/off, current tier, `Raise tier`), local approval options (`Ask on this PC before shutting down`), shutdown delay (default 30 s), Wi-Fi-off auto-restore default, `Start AppLock with administrator rights`, `Show Island cards for remote commands`.
5. **What to sync:** a switch per category in the section-10 catalog, each with a one-line description, whether it is encrypted, and its cadence; sensitive ones show a warning icon (clipboard, Wi-Fi passwords, running apps, notes). Defaults: only **battery/status/perf** on after the user enables Sync; everything else off.
6. **Activity:** local audit list (time, command, tier, result, session label), web sessions seen, security events (failed proofs, rate-limit blocks). Export as CSV.
7. **Status:** connection state, last sync, queue size, data used today, last error, a **Sync now** button.
8. **Privacy:** what is stored where (the section-6 plain-metadata list), retention settings, **Delete all my cloud data**, link to the privacy policy.
9. **Advanced:** metered-connection behavior, telemetry cadence (Low / Normal), developer diagnostics.

### 12.3 Island and tray integration
- Island **Remote chip** while a viewer session is active (tooltip `Viewed from Chrome on Android · 3 min`), live cards for remote commands and **shutdown/restart countdown with Cancel**, a `Keyboard disabled` recovery chip, a `Mic locked` indicator, and the Sync status in the Controls tab is optional.
- Tray menu: `Pause remote control`, `Sync now`, `Open Sync settings`.

### 12.4 Authentication rules inside AppLock
Turning **on** Sync categories, Remote control, command-class changes, Wi-Fi password feature, passphrase/recovery key view, unlinking other devices needs the AppLock admin password / Windows Hello. **Turning things off, pausing, signing out** needs none.

## 13. Settings model

```csharp
public class SyncSettings
{
    public bool SignedIn { get; set; }
    public string AccountEmail { get; set; }            // display only
    public string DeviceId { get; set; }                // random 128-bit, hex
    public string DeviceName { get; set; }
    public bool RemoteControlEnabled { get; set; }
    public bool RemotePaused { get; set; }
    public Dictionary<string, CommandClassPolicy> Classes { get; set; } = new();   // enabled + minTier (never below shipped minimum)
    public Dictionary<string, bool> Categories { get; set; } = new();              // battery, perf, audio, media, notes, ...
    public int ShutdownDelaySeconds { get; set; } = 30;
    public int WifiOffAutoRestoreMinutes { get; set; } = 10;
    public bool AskBeforeShutdown { get; set; }
    public bool ShowRemoteCards { get; set; } = true;
    public bool AllowMetered { get; set; }
    public int ClipboardItems { get; set; } = 50;
    public int ClipboardTtlDays { get; set; } = 7;
    public bool ClipboardImages { get; set; }
    public bool WifiPasswordRequests { get; set; }      // off by default
    public bool StartElevated { get; set; }
    public HashSet<string> ProcessedCommandIds { get; set; } = new();              // 24 h window
}
```
Secrets (refresh token, SMK wrapped by DPAPI) are **not** in this JSON; they live in the DPAPI-protected secret store. Migrate older settings files without errors.

## 14. Project structure (additions)

```
AppLock/
  Sync/
    Auth/AuthService.cs, GoogleLoopbackAuth.cs, IdentityToolkitClient.cs, TokenStore.cs
    Crypto/CryptoService.cs, KeySlots.cs, Envelope.cs, CommandProof.cs
    Rtdb/RtdbClient.cs, SseParser.cs, ServerClock.cs, RtdbTree.cs
    Engine/SyncEngine.cs, ISyncModule.cs, UploadQueue.cs, Cadence.cs
    Modules/ BatteryModule, PerfModule, RadioModule, AudioModule, MediaModule, DisplayModule, NotesModule, RemindersModule, ClipboardModule, AppsModule, ProtectedModule, ScreenTimeModule, UsageModule, BitLockerModule, NetRulesModule, LanModule, PortsModule, WebFilterModule, WifiPasswordModule, KeyboardModule
    Commands/CommandProcessor.cs, CommandCatalog.cs (types, schema, shipped min tier), PolicyEnforcer.cs, RateLimiter.cs, AuditLog.cs, Executors/*.cs
  Models/SyncSettings.cs
  ViewModels/Sync/*.cs
  Views/SyncSettingsPage.xaml(.cs)
firebase/
  database.rules.json, firebase.json, .firebaserc.example, emulator tests (rules)
web/
  (Vite + Svelte + TypeScript app, section 11)
docs/
  firebase-setup.md, crypto-vectors.json, threat-model.md, sync-data-map.md
```

## 15. Privacy, retention, deletion

- On first enabling Sync show a consent screen listing exactly **what leaves the PC, what is encrypted, and what stays plain**, with a link to the privacy policy (the developer must publish one; Firebase/Google process the data).
- **Delete all my cloud data** (PC and web): removes `/users/{uid}` and, optionally, the Firebase account (`accounts:delete`, which may need a recent login). **Export my data** (encrypted JSON that the user can decrypt locally) is nice to have.
- Retention: clipboard TTL (default 7 days), tombstones 30 days, audit last 200 entries, screen time/usage history 90 days (configurable), results and processed commands deleted within minutes.
- No analytics, no crash reporting to third parties, no advertising IDs. Logs contain no tokens, passphrases, passwords, or decrypted content.

## 16. Honest limits (show a short version in the UI)

- The PC must be **on, awake, signed in to Windows, running AppLock, and online**. Asleep or off = offline; commands expire. Wake-on-LAN is not included.
- **UAC cannot be answered remotely**; admin-only actions need AppLock to be started as administrator (opt-in) or they report `needs_elevation`.
- **Turning off Wi-Fi remotely can strand the PC**; the auto-restore timer exists for this reason.
- Some features rely on **undocumented or indirect Windows behavior** (battery saver via the Energy Saver threshold, default audio device switching via `IPolicyConfig`, Bluetooth battery levels, camera brightness only on supporting cameras, GPU counters). They are isolated behind interfaces and may not work on every PC or Windows build; report failures clearly, never claim success without verifying.
- A local Windows **administrator can always stop, remove, or bypass** AppLock. The Sync passphrase protects against **remote** attackers and the cloud operator, not against someone with full control of the PC.
- **No passphrase and no recovery key = no way to read the encrypted cloud data.** There is no backdoor.
- Firebase **Spark** limits (100 connections, bandwidth) and Auth quotas apply; heavy use needs Blaze. Cloud Functions features (push alerts, global sign-out, custom claims) are phase 2.
- An attacker who has the user's **account login and Sync passphrase** (for example a malicious browser extension on the user's phone/PC) can do T1–T3 things until revoked; the audit log, Island cards, Pause switch, and tier design limit and expose this, they do not make it impossible. Strongly recommend MFA.

## 17. Tests and manual checklist

Unit/integration tests:
- **Crypto:** shared test vectors pass in C# and TypeScript (Argon2id, HKDF, AES-GCM envelopes with AAD, key-slot wrap/unwrap, recovery key); tamper tests (flipped bit, wrong AAD, moved node) fail; KDF floor enforcement.
- **Command validation:** expired, far-future, replayed id, wrong HMAC, missing proof for T2, stale step-up for T3, unknown type, oversized payload, bad schema, disabled class, tier below the shipped minimum, rate limit and 5-failures lockout, ServerClock skew.
- **Rules:** emulator tests for every rule in 7.2 (other user, unverified email, size/type validation, malformed command).
- **SSE parser** with recorded streams (put/patch/keep-alive/cancel/auth_revoked), reconnect/backoff, token refresh.
- **Sync engine:** change hashing, coalescing, offline queue persistence and trimming, viewer-aware cadence, conflict resolution (two edits, delete vs edit), TTL deletion.
- **Executors** with fakes: shutdown delay + cancel, Wi-Fi off + auto-restore, MicGuard re-mute, keyboard hook enable/disable and recovery, battery saver restore, protected-app weakening requires T3.

Manual (two devices or a PC plus a phone):
- [ ] Sign up with email, verify email, sign in on PC; sign in with Google on PC (system browser) and on the web
- [ ] Set Sync passphrase and recovery key; web unlock works; wrong passphrase is rejected; recovery key works
- [ ] PC shows online on the web within seconds; closing AppLock shows offline within ~2.5 minutes
- [ ] Battery, plug in/out, radios, brightness, volume update live; perf updates every ~2 s only while the dashboard is open
- [ ] Lock from the web locks Windows; Sleep works; Shut down shows the 30 s countdown on the PC and can be cancelled from the PC and the web
- [ ] Wi-Fi off with auto-restore returns after the chosen time; Wi-Fi schedule runs offline
- [ ] Mic mute/lock: unmuting locally is re-muted while locked; unlock works
- [ ] Media: two playing apps show as two cards; play/pause/next/previous work per session
- [ ] Keyboard disable works, OSK still types, mouse works, recovery chord and Island chip re-enable
- [ ] Notes/reminders/clipboard sync both ways; conflict copy appears; clipboard exclusions respected; TTL deletes
- [ ] Protected-app lock works; unlock/remove requires step-up; website allow requires step-up
- [ ] BitLocker lock works; unlock with the encrypted password works and the command is deleted
- [ ] Wi-Fi password request works only when enabled, shows 30 s, leaves nothing in the database
- [ ] A forged command written directly to the database without a valid proof is rejected and logged; a replayed command is ignored
- [ ] Pausing remote control stops all execution immediately; resuming needs authentication
- [ ] Delete all cloud data empties `/users/{uid}`; account deletion works
- [ ] Light/Dark/System theme correct; web is usable on a phone; CSP shows no violations

## 18. Delivery

Implement in this order, **one milestone at a time**:

0. **Decisions + Firebase skeleton:** `docs/firebase-setup.md`, `docs/threat-model.md`, `firebase/` with emulator config and **draft rules + rules tests**, `web/` project skeleton that builds. No PC changes yet.
1. **Others › Sync card + settings page skeleton + auth on the PC:** email/password and Google (loopback + PKCE) sign-in, email verification, token storage with DPAPI, sign out, delete account. Verify against the emulator and a real test project.
2. **Crypto core:** passphrase and recovery key slots, envelopes, command proofs, **shared test vectors passing in both C# and TypeScript**. Nothing is synced before this passes.
3. **RTDB client + device registration + presence:** REST/SSE client, `ServerClock`, heartbeat, device list on the web with online/offline, rules v1 enforced and tested.
4. **Plain state sync + Overview page:** battery/charging, radios, brightness, volume, perf with viewer-aware cadence; data-usage readout.
5. **Command pipeline + local policy:** envelope validation, tiers, proofs, replay protection, results, audit log, rate limits, **Pause**, Island remote chip/cards; first commands: lock, sleep, brightness, volume.
6. **Power and device controls:** restart/shutdown (delay + cancel), radios (auto-restore), Wi-Fi schedule, battery saver (spike), keyboard disable, mic mute + lock, default audio device switching (spike), camera brightness.
7. **Audio, media, mixer, speaker/mic lists** on the web.
8. **E2EE content sync:** notes, reminders, clipboard (conflicts, TTL, exclusions, pause).
9. **Mirrors and protected edits:** running apps, protected apps, screen time, usage history, internet rules, LAN devices, ports, website lists (read-only first, then the tiered edits).
10. **BitLocker lock/unlock and on-demand Wi-Fi passwords** (with the elevation handling and all extra safeguards).
11. **Web polish and security hardening:** PWA, mobile layout, CSP/headers/Trusted Types, session list, audit view, data deletion/export, account deletion.
12. **Hardening and release checks:** failure injection (offline, token expiry, clock skew, duplicate commands), quota/cost measurement, run the full checklist, finish the threat-model review, and list what needs an external crypto/security review. Optional phase 2 (needs Blaze): Cloud Functions for push alerts, custom `device` claims, global session revocation, scheduled cleanup.

After each milestone: build the C# project and the web app (`npm run build`), run the tests, **launch the app and show the Sync card/page** (and open the web dashboard from milestone 3), report in 5 lines or fewer, commit, then **stop and wait for me to reply "continue"**. Never test destructive commands (shutdown, restart, BitLocker, keyboard disable) without the safeguards from this spec in place and a way to recover. Never commit secrets, service-account keys, tokens, or real user data.
