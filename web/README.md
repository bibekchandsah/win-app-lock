# AppLock Sync dashboard

Svelte 5 + TypeScript + Vite single-page app, published to Firebase Hosting. It talks to the
Realtime Database as the signed-in owner and must never hold secrets: the Firebase web config is
public by design, and content is decrypted in the browser only.

## Develop

```bash
npm ci
cp .env.example .env.local   # fill in the public config (or point at the emulator)
npm run dev                  # http://127.0.0.1:5173
npm run check                # svelte-check, warnings fail the run
npm run build                # -> dist/ (what Hosting serves)
```

Emulator values (milestone 3): `VITE_USE_EMULATORS=1` with project id `demo-applock`,
`VITE_FIREBASE_DATABASE_URL=http://127.0.0.1:9000?ns=demo-applock` and
`VITE_FIREBASE_AUTH_DOMAIN=127.0.0.1:9099`.

## Status

- **Milestone 0:** shell, theme (Light/Dark/System), typed Realtime Database protocol, config reader.
- **Milestone 1+:** sign-in, passphrase unlock, device list and the control pages.

## Rules of the road

- Never use `innerHTML` or `eval`; all PC-provided text is rendered as text.
- Never store the passphrase, keys or tokens in `localStorage`, cookies, logs or URLs. Only the
  theme choice is persisted.
- No analytics, no third-party scripts, no remote fonts. The CSP in `firebase/firebase.json`
  allows only same-origin code plus the Firebase/Google endpoints sign-in needs.
- Dependencies are pinned by `package-lock.json`; review before bumping.

See `docs/threat-model.md` for what the cloud can see and `docs/firebase-setup.md` for deployment.
