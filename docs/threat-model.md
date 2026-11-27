# Sync threat model

Scope: the **Sync & Remote Control** add-on for AppLock (`sync.md`). The PC app is the trusted endpoint; the cloud (Firebase Auth, Realtime Database, Hosting) is treated as **honest-but-curious storage** and as an **untrusted input channel**.

## 1. Assets

| Asset | Why it matters | Where it lives |
|---|---|---|
| Windows session control (lock, sleep, power, radios, brightness, audio, keyboard) | Abusing it disrupts or strands the PC | PC only, reachable through validated commands |
| Notes, reminders, clipboard, app/media names, device names, Wi-Fi schedules, protected lists, histories | Private user content | Cloud, **E2EE ciphertext only** |
| Sync passphrase and Sync Master Key (SMK) | Decrypts everything | Passphrase: never stored. SMK: DPAPI-wrapped on the PC, and wrapped inside key slots in the cloud |
| Firebase account (Google / email) | Identity for both the PC and the web | Firebase Auth |
| Refresh token (PC) | Long-lived session | DPAPI `CurrentUser` secret store, never in settings JSON or logs |
| BitLocker and saved Wi-Fi passwords | Highest-sensitivity secrets | Never stored in the cloud; on-demand, single-shot, encrypted, deleted after the TTL |
| Vault | Out of scope by design | Never readable or writable through Sync |

## 2. Trust boundaries

```
[ Browser ]  --TLS-->  [ Firebase ]  <--TLS--  [ PC (AppLock) ]
   untrusted input channel        untrusted storage / relay      trusted policy + execution point
```

- **Permissions are decided on the PC** from its own protected settings. A flag in the database means nothing: `remoteControlEnabled` is read from local settings only.
- **The cloud cannot tell the PC from the web.** Both are the same Firebase user, so rules cannot separate them; that is why the PC re-validates every command (schema, tier, proof, freshness, replay) and never executes anything it did not classify itself.
- **The browser is untrusted by the PC** even when authenticated: every field of every command is validated against a schema with types, ranges, enums and size caps.

## 3. Threats and mitigations

### T1 — Account takeover (stolen password / session)
*Attacker signs in as the user and tries to control the PC or read content.*
- Email verification is required by both rules and app (`auth.token.email_verified == true`).
- MFA recommended; password policy ≥ 10 chars.
- T2/T3 commands require an **HMAC proof** derived from the Sync passphrase, which the attacker does not have from an account takeover alone (unless it leaks with it).
- E2EE keeps content unreadable without the passphrase.
- PC-enforced tiers, rate limits (10/min), 5 failed proofs in 10 min → 15 min block, Island card + audit entry.
- Local **Pause** (no auth needed) and the audit log make an attack visible and stoppable.

### T2 — Compromised dashboard / XSS
*Injected script steals the passphrase, keys, or session.*
- Strict CSP with `script-src 'self'`, no third-party scripts, `require-trusted-types-for 'script'`.
- No `innerHTML`/`eval`; all PC-provided text (clipboard, titles, device names) rendered with `textContent`.
- Keys are **non-extractable `CryptoKey`** objects in memory; passphrase never in `localStorage`, cookies, URLs, or logs.
- `frame-ancestors 'none'`, `base-uri 'none'`, `object-src 'none'`.
- PC-side validation means a malicious page cannot exceed the tier it can prove.

### T3 — Forged or replayed commands
*Someone with database write access plants commands.*
- Command envelope: `nonce` (16 random bytes), `expiresAt` (≤ 10 min, default 60 s), `cmdId` one-time (persisted 24 h on the PC), `tier`, `session`.
- Freshness compared against a **ServerClock** derived from `{".sv":"timestamp"}`, not the PC clock (skew is logged and rejected).
- T2/T3: HMAC-SHA256 over the canonical JSON `{deviceId, cmdId, type, payload, expiresAt, nonce, tier, stepUpAt}` with the `cmd-v1` key, constant-time compare.
- T3 adds `stepUpAt` within 5 minutes of `createdAt` plus a local cancel window where applicable.
- Rules validate shape, type, size and `expiresAt <= now + 600000` server-side as defence in depth.

### T4 — Cloud operator / data breach
*Firebase or a leaked dump reads user content.*
- Content is AES-256-GCM ciphertext with AAD bound to the database path; the operator sees only the plain metadata listed in §6.
- Key slots are wrapped blobs (passphrase KEK via Argon2id, recovery KEK via HKDF) — no plaintext SMK ever leaves a device unwrapped.

### T5 — Local attacker at the PC
*Someone sitting at the machine tries to weaken protection.*
- Turning Sync categories, Remote control, or class tiers **on**, viewing the recovery key, unlinking other devices: needs the AppLock admin password or Windows Hello.
- Turning things **off**, pausing, signing out: allowed without auth (it only reduces access).
- Remote-control pause is unauthenticated by design; resuming is authenticated.
- Honest limit: a local Windows administrator can always stop or remove AppLock. Sync protects against remote attackers, not physical control.

### T6 — Malicious content from the PC to the browser
*A clipboard item or window title contains script.*
- Rendered as text only (`textContent`), links validated as `http(s)` with `rel="noopener noreferrer"`.
- CSP blocks inline script even if a sink were missed.

### T7 — Destructive actions by accident or malice
*Shutdown/restart/BitLocker/keyboard disable.*
- T3 where shipped; 30 s default delay with a PC-side **Cancel** card and a web **Cancel** command.
- No force-close by default; unsaved work can block a shutdown.
- Keyboard disable keeps the on-screen keyboard working (needed for recovery), has a recovery chord and an Island chip; the hook dies with the process.
- BitLocker unlock deletes the command node immediately; passwords never logged.
- Safeguards must exist **before** a destructive command is implemented or tested (see the delivery order in §18).

### T8 — Stranding the PC (Wi-Fi off)
- Web must choose a duration (10 min / 1 h / until local), the PC **auto-restores** (default 10 min), and the UI warns when no Ethernet is present.

### T9 — Denial of service on the PC
- Command rate limit 10/min; proof-failure block; per-command size caps; upload queue capped at 5 MB with oldest-dropped.
- Telemetry is viewer-aware so a stuck dashboard cannot flood the PC.

## 4. Cryptography rules (non-negotiable)

- Primitives: **AES-256-GCM**, **HKDF-SHA256**, **Argon2id**, **HMAC-SHA256**. Nothing else, nothing home-made.
- Argon2id floors on read: **m ≥ 64 MiB, t ≥ 3, p = 1**, 16-byte salt. Weaker parameters are rejected.
- Envelope: `{v:1, n: base64(12-byte nonce), c: base64(ciphertext||tag)}`, AAD = `"<database path>|<schema version>"`, fresh nonce per encryption, key rotation at 2^31 messages.
- `docs/crypto-vectors.json` is the single source of truth; C# and TypeScript must both pass it before any data is synced (milestone 2 gate). Tamper tests (flipped bit, wrong AAD, moved node) must fail.

## 5. Hard-coded remote deny list

Never available from the web, at any tier: disable AppLock protection, change admin password or security questions, read/unlock the **Vault**, uninstall, change Sync settings/policy/passphrase, arbitrary command execution, file access, screenshots, mouse/keyboard **input injection**, remote desktop.

## 6. What the cloud sees in plaintext (and must be stated in the UI)

`online`, `lastSeen`, app version, OS string, battery %, charging/plugged-in, radio on/off, mute/brightness/volume numbers, CPU/RAM/disk/GPU percentages, network up/down, command ids + types + tiers + statuses + timestamps, result statuses, session user-agent strings, record sizes and counts.

Everything else — device name, notes, reminders, clipboard, app names, media titles, Wi-Fi SSIDs, LAN devices, ports, website lists, protected lists, Wi-Fi schedule bodies, histories — is **ciphertext**.

## 7. Residual risks (accepted, disclosed in the UI)

- The PC must be on, awake, signed in, running AppLock and online. Asleep/off = offline; commands expire in 60 s. No Wake-on-LAN.
- UAC cannot be answered remotely: admin-only actions report `needs_elevation` unless AppLock was started elevated (opt-in).
- Undocumented Windows behaviour (energy-saver threshold, `IPolicyConfig`, Bluetooth battery, camera brightness, GPU counters) may not work on every build; failures must be reported, never faked.
- An attacker holding **both** the account and the passphrase can act within their tiers until revoked; the audit log, Island cards, Pause and the tier design limit and expose that, they do not prevent it.
- Losing both passphrase and recovery key means the encrypted cloud data is unrecoverable. There is no backdoor; the only remedy is a sync reset that discards it.
