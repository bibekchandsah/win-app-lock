# Firebase setup for AppLock Sync

This is a one-time, developer-side setup. Everything here uses the **Firebase Emulator Suite** until the last step; never develop against production data.

Nothing in this document may be committed as a secret. The Firebase **API key** and project config are public by design (they identify the project, they do not authorise anything). What protects the data is Authentication, Security Rules, App Check and end-to-end encryption.

## 1. Create the project

1. <https://console.firebase.google.com> → **Add project**. Pick a name (`applock-sync-<something>`), disable Google Analytics (the spec forbids analytics).
2. Project settings → **General** → add a **Web app** (`AppLock Dashboard`) and copy the `firebaseConfig` object. It goes into `web/.env.local` as `VITE_FIREBASE_*` variables (see `web/.env.example`). Commit only the example file.
3. Project settings → **Your apps** → add an **Android/iOS** app only if you later want FCM (phase 2); not needed for v1.

## 2. Authentication

1. **Build → Authentication → Sign-in method**:
   - **Email/Password**: enable. Enable *email link* only if you want it (not used by v1).
   - **Google**: enable, set the support email, and set the project's **public-facing name**.
2. **Authentication → Settings → User actions**: keep "One account per email address" enabled.
3. **Authentication → Templates**: customise the *Verify email* and *Password reset* mails (sender name, reply-to).
4. Recommended, needs Identity Platform (Blaze later): enable **multi-factor authentication** and a password policy of **minimum 10 characters**. The PC and the web enforce the 10-character floor themselves regardless.
5. **Authorized domains** (Authentication → Settings): `localhost`, `<project>.firebaseapp.com`, `<project>.web.app`. Add the custom domain when you have one.

## 3. Realtime Database

1. **Build → Realtime Database → Create database**, pick the region closest to most users, start in **locked mode**.
2. Deploy the rules from this repository (they live in `firebase/database.rules.json`):

   ```bash
   cd firebase
   npx firebase --config firebase.json --project <project-id> deploy --only database
   ```

3. Verify in the console's **Rules** tab that the playground shows the deployed rules, and run the rules tests locally first (§7).
4. Enable **backups** only on Blaze; on Spark keep the export habit (`firebase database:get / --project <id> > backup.json`) out of the repository.

## 4. App Check

1. **Build → App Check → Apps → Web app → reCAPTCHA Enterprise** (or v3 while in development).
2. Register the debug token for local development:
   - Web: set `VITE_APPCHECK_DEBUG_TOKEN=true` in `web/.env.local`, open the app, copy the debug token the SDK prints, then register it in the console under *Manage debug tokens*.
   - Never commit a debug token.
3. Enforce App Check for **Realtime Database** only after the dashboard has been running with tokens for a few days. Do **not** enforce it for Authentication (it breaks the PC's REST sign-in, which does not send App Check tokens).
4. Restrict the web API key: Google Cloud console → APIs & Services → Credentials → the browser key → **HTTP referrers** = your Hosting domains + `localhost`.

## 5. Google sign-in from the PC (desktop OAuth client)

1. Google Cloud console → **APIs & Services → OAuth consent screen**: External, app name, support email, scopes `openid`, `email`, `profile`. Add test users while unverified.
2. **Credentials → Create credentials → OAuth client ID → Desktop app**. Download nothing; just note the client id.
3. The PC uses the **installed-app flow**: a loopback listener on `127.0.0.1` with a random port, **PKCE (S256)**, random `state` and `nonce`, then `accounts:signInWithIdp` against Identity Toolkit (`providerId=google.com`).
4. No client *secret* is used or stored by the PC — a desktop client secret is not a secret, but we do not need it.
5. Restrict the key (Google Cloud → Credentials → the Android/iOS/browser keys) so only the intended clients use them.

## 6. Hosting

1. `firebase/firebase.json` already declares Hosting with `public: "../web/dist"` and the security headers from the spec (§11.3).
2. Build and deploy:

   ```bash
   cd web && npm ci && npm run build
   cd ../firebase && npx firebase --config firebase.json --project <project-id> deploy --only hosting
   ```

3. Add the Hosting domain to **Authentication → Authorized domains**.
4. Re-check the headers after deploy:

   ```bash
   curl -sI https://<project>.web.app | grep -i -E 'content-security-policy|strict-transport|x-content-type|referrer|permissions-policy|cross-origin'
   ```

## 7. Emulator Suite (development and tests)

```bash
cd firebase
npm ci
npx firebase --config firebase.json emulators:start --only database,auth
```

- Realtime Database: `127.0.0.1:9000` · Auth: `127.0.0.1:9099` · Emulator UI: `127.0.0.1:4000`.
- The PC app reads `APPLOCK_SYNC_EMULATOR=1` (milestone 3) and then talks to the emulator host/ports instead of the cloud.
- Rules tests (database emulator only, no Auth emulator needed because claims are minted by the test SDK):

  ```bash
  cd firebase && npm test
  ```

## 8. Budget and limits

- Spark: **100 simultaneous Realtime Database connections**. Each online PC and each dashboard tab is one connection; the web also uses REST for the viewer heartbeat, which counts as a connection while streaming.
- Spark includes 1 GB stored and 10 GB/month download. The viewer-aware cadence in the spec targets **< 5 MB/day with no dashboard open** and **< 15 MB/hour with one open**.
- Move to **Blaze** when: more than ~80 devices+sessions are online at once, the monthly download cap is approached, or phase-2 features (Cloud Functions for FCM push, custom claims, global sign-out) are needed.
- Set a **budget alert** in Google Cloud (Billing → Budgets) even on Spark, so a misconfiguration cannot surprise you.

## 9. Phase 2 (Blaze) — not part of v1

- Cloud Functions: FCM push for alerts, `role: "device"` custom claims (rules can then separate the PC from the dashboard), forced session revocation, scheduled cleanup of tombstones/`results`.
- Storage: nothing in v1 (no album art, no images unless clipboard images are enabled by the user).

## 10. Checklist before shipping

- [ ] Rules deployed and rules tests green.
- [ ] App Check enforced for the database, debug tokens removed.
- [ ] API key referrer-restricted; desktop OAuth client restricted.
- [ ] Email verification template reviewed; password policy set.
- [ ] Hosting headers verified with `curl -I`.
- [ ] No service-account JSON, admin SDK or refresh token anywhere in the repo or the bundles:

  ```bash
  grep -ril "private_key\|serviceAccount\|refresh_token" web/dist src docs
  ```
