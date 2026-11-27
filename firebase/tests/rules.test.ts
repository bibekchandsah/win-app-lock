import { readFileSync } from 'node:fs';
import { afterAll, beforeAll, beforeEach, describe, it } from 'vitest';
import {
  assertFails,
  assertSucceeds,
  initializeTestEnvironment,
  type RulesTestEnvironment,
} from '@firebase/rules-unit-testing';

/**
 * Rules tests run against the Realtime Database emulator started by
 * `npm test` (firebase emulators:exec). Claims are minted locally by the test
 * SDK, so the Auth emulator is not required.
 */

const ALICE = 'alice-uid-1234567890';
const BOB = 'bob-uid-0987654321';
const DEVICE = '0123456789abcdef0123456789abcdef';
const OTHER_DEVICE = 'fedcba9876543210fedcba9876543210';
const SESSION = 'webSessionId1234';
const CMD = 'cmd_0001-abcdef';
const NOTE = 'note_0001-abcdef';

const rules = readFileSync(new URL('../database.rules.json', import.meta.url), 'utf8');

let testEnv: RulesTestEnvironment;

function emulatorHostPort(fallback = '127.0.0.1:9000'): { host: string; port: number } {
  const raw = process.env.FIREBASE_DATABASE_EMULATOR_HOST ?? fallback;
  const [host, port] = raw.split(':');
  return { host, port: Number(port) };
}

function verified(uid: string) {
  return testEnv.authenticatedContext(uid, { email_verified: true }).database();
}

function unverified(uid: string) {
  return testEnv.authenticatedContext(uid, { email_verified: false }).database();
}

function anon() {
  return testEnv.unauthenticatedContext().database();
}

/** A well-formed AES-GCM envelope as described in sync.md §6. */
function envelope(overrides: Record<string, unknown> = {}) {
  return {
    v: 1,
    n: 'AAAAAAAAAAAAAAAA', // base64 of a 12-byte nonce = 16 chars
    c: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
    ...overrides,
  };
}

const CRYPTO = {
  kdf: { alg: 'argon2id', m: 65536, t: 3, p: 1, salt: 'c2FsdHktc2FsdHktc2FsdA==' },
  slots: { pass: envelope(), recovery: envelope() },
  verifier: envelope(),
  v: 1,
};

const PRESENCE = { online: true, lastSeen: 1_799_000_000_000 };

/** Time now, so command expiry checks compare against the emulator clock. */
const NOW = Date.now();

function command(overrides: Record<string, unknown> = {}) {
  return {
    type: 'power.lock',
    payload: '{}',
    createdAt: NOW,
    expiresAt: NOW + 30_000,
    nonce: 'BBBBBBBBBBBBBBBB',
    tier: 1,
    session: SESSION,
    status: 'pending',
    ...overrides,
  };
}

function note(overrides: Record<string, unknown> = {}) {
  return {
    envelope: envelope(),
    updatedAt: 1_799_000_000_000,
    deviceId: DEVICE,
    deleted: false,
    ...overrides,
  };
}

beforeAll(async () => {
  const { host, port } = emulatorHostPort();
  testEnv = await initializeTestEnvironment({
    projectId: 'demo-applock',
    database: { host, port, rules },
  });
});

beforeEach(async () => {
  await testEnv.clearDatabase();
});

afterAll(async () => {
  await testEnv.cleanup();
});

describe('ownership and email verification', () => {
  it('lets the verified owner write and read their own device presence', async () => {
    const db = verified(ALICE);
    await assertSucceeds(db.ref(`users/${ALICE}/devices/${DEVICE}/presence`).set(PRESENCE));
    await assertSucceeds(db.ref(`users/${ALICE}/devices/${DEVICE}/presence`).get());
  });

  it('denies another signed-in user reading or writing that subtree', async () => {
    await testEnv.withSecurityRulesDisabled(async (ctx) => {
      await ctx.database().ref(`users/${ALICE}/devices/${DEVICE}/presence`).set(PRESENCE);
    });
    const bob = verified(BOB);
    await assertFails(bob.ref(`users/${ALICE}/devices/${DEVICE}/presence`).get());
    await assertFails(bob.ref(`users/${ALICE}/devices/${DEVICE}/presence`).set(PRESENCE));
    await assertFails(bob.ref(`users/${ALICE}/crypto`).set(CRYPTO));
  });

  it('denies an unverified email even for the owner', async () => {
    const db = unverified(ALICE);
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/presence`).set(PRESENCE));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/presence`).get());
  });

  it('denies unauthenticated access', async () => {
    const db = anon();
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/presence`).set(PRESENCE));
    await assertFails(db.ref(`users/${ALICE}`).get());
  });

  it('denies writes outside the account subtree', async () => {
    const db = verified(ALICE);
    await assertFails(db.ref(`other/${ALICE}`).set({ hello: 'world' }));
    await assertFails(db.ref('users').set({ hello: 'world' }));
  });

  it('denies malformed device ids', async () => {
    const db = verified(ALICE);
    await assertFails(db.ref(`users/${ALICE}/devices/not-a-hex-id/presence`).set(PRESENCE));
  });
});

describe('crypto node', () => {
  it('accepts floors-compliant Argon2id parameters', async () => {
    await assertSucceeds(verified(ALICE).ref(`users/${ALICE}/crypto`).set(CRYPTO));
  });

  it('rejects memory below 64 MiB', async () => {
    const weak = { ...CRYPTO, kdf: { ...CRYPTO.kdf, m: 32768 } };
    await assertFails(verified(ALICE).ref(`users/${ALICE}/crypto`).set(weak));
  });

  it('rejects iterations below 3 and parallelism other than 1', async () => {
    const fewIterations = { ...CRYPTO, kdf: { ...CRYPTO.kdf, t: 2 } };
    const parallel = { ...CRYPTO, kdf: { ...CRYPTO.kdf, p: 2 } };
    await assertFails(verified(ALICE).ref(`users/${ALICE}/crypto`).set(fewIterations));
    await assertFails(verified(ALICE).ref(`users/${ALICE}/crypto`).set(parallel));
  });

  it('rejects key slots with a malformed nonce or envelope version', async () => {
    const db = verified(ALICE);
    await assertFails(db.ref(`users/${ALICE}/crypto/slots/pass`).set(envelope({ n: 'AAAA' })));
    await assertFails(db.ref(`users/${ALICE}/crypto/slots/recovery`).set(envelope({ v: 2 })));
    await assertFails(db.ref(`users/${ALICE}/crypto/verifier`).set({ v: 1, n: 'x', c: '' }));
  });
});

describe('devices: plain state validation', () => {
  it('accepts in-range perf and battery values', async () => {
    const db = verified(ALICE);
    await assertSucceeds(
      db.ref(`users/${ALICE}/devices/${DEVICE}/live/perf`).set({ cpu: 12, ram: 48, disk: 3, gpu: 7 }),
    );
    await assertSucceeds(
      db.ref(`users/${ALICE}/devices/${DEVICE}/live/battery`).set({ percent: 87, charging: false }),
    );
  });

  it('rejects out-of-range numbers and wrong types', async () => {
    const db = verified(ALICE);
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/live/perf`).set({ cpu: 150 }));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/live/perf`).set({ cpu: '12' }));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/live/battery`).set({ percent: 150 }));
  });

  it('caps encrypted data categories and rejects unknown ones', async () => {
    const db = verified(ALICE);
    await assertSucceeds(db.ref(`users/${ALICE}/devices/${DEVICE}/data/media`).set(envelope()));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/data/media`).set(envelope({ c: 'A'.repeat(200_001) })));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/data/evil`).set(envelope()));
  });

  it('validates history day keys', async () => {
    const db = verified(ALICE);
    await assertSucceeds(db.ref(`users/${ALICE}/devices/${DEVICE}/history/screentime/2026-11-27`).set(envelope()));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/history/screentime/27-11-2026`).set(envelope()));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/history/other/2026-11-27`).set(envelope()));
  });
});

describe('commands', () => {
  it('accepts a well-formed, short-lived command', async () => {
    await assertSucceeds(verified(ALICE).ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`).set(command()));
  });

  it('rejects a command without a nonce, session or status', async () => {
    const db = verified(ALICE);
    const { nonce: _nonce, ...withoutNonce } = command();
    const { session: _session, ...withoutSession } = command();
    const ref = db.ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`);
    await assertFails(ref.set(withoutNonce));
    await assertFails(ref.set(withoutSession));
    await assertFails(ref.set(command({ status: 'done' })));
  });

  it('rejects a command that expires more than ten minutes out', async () => {
    const createdAt = Date.now();
    const far = command({ createdAt, expiresAt: createdAt + 900_000 });
    await assertFails(verified(ALICE).ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`).set(far));
  });

  it('rejects tiers outside 1..3 and non-dotted command types', async () => {
    const db = verified(ALICE);
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`).set(command({ tier: 4 })));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`).set(command({ type: 'Lock' })));
    await assertFails(
      db.ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`).set(command({ type: 'power.lock now' })),
    );
  });

  it('rejects oversized payloads', async () => {
    const db = verified(ALICE);
    await assertFails(
      db.ref(`users/${ALICE}/devices/${DEVICE}/commands/${CMD}`).set(command({ payload: 'A'.repeat(8193) })),
    );
  });
});

describe('results, audit and viewers', () => {
  it('accepts results with a known status and rejects unknown statuses', async () => {
    const db = verified(ALICE);
    await assertSucceeds(
      db.ref(`users/${ALICE}/devices/${DEVICE}/results/${CMD}`).set({ status: 'ok', at: 1_799_000_040_000 }),
    );
    await assertFails(
      db.ref(`users/${ALICE}/devices/${DEVICE}/results/${CMD}`).set({ status: 'done', at: 1_799_000_040_000 }),
    );
  });

  it('validates audit entries', async () => {
    const db = verified(ALICE);
    await assertSucceeds(
      db
        .ref(`users/${ALICE}/devices/${DEVICE}/audit/${CMD}`)
        .set({ type: 'power.lock', tier: 1, status: 'ok', at: 1_799_000_040_000, session: SESSION }),
    );
    await assertFails(
      db.ref(`users/${ALICE}/devices/${DEVICE}/audit/${CMD}`).set({ type: 'power.lock', tier: 9, status: 'ok', at: 1 }),
    );
  });

  it('requires a user agent on viewer heartbeats', async () => {
    const db = verified(ALICE);
    await assertSucceeds(db.ref(`users/${ALICE}/devices/${DEVICE}/viewers/${SESSION}`).set({ ts: 1, ua: 'Chrome' }));
    await assertFails(db.ref(`users/${ALICE}/devices/${DEVICE}/viewers/${SESSION}`).set({ ts: 1 }));
  });
});

describe('encrypted content', () => {
  it('accepts a note with an envelope and a delete tombstone without one', async () => {
    const db = verified(ALICE);
    await assertSucceeds(db.ref(`users/${ALICE}/sync/notes/${NOTE}`).set(note()));
    await assertSucceeds(
      db.ref(`users/${ALICE}/sync/notes/${NOTE}`).set({ updatedAt: 1_799_000_000_001, deviceId: DEVICE, deleted: true }),
    );
  });

  it('rejects a live note without an envelope and oversized clipboards', async () => {
    const db = verified(ALICE);
    await assertFails(
      db.ref(`users/${ALICE}/sync/notes/${NOTE}`).set({ updatedAt: 1, deviceId: DEVICE, deleted: false }),
    );
    await assertFails(
      db
        .ref(`users/${ALICE}/sync/clipboard/${NOTE}`)
        .set({ envelope: envelope({ c: 'A'.repeat(200_001) }), createdAt: 1, deviceId: DEVICE }),
    );
  });

  it('denies notes paths belonging to another account', async () => {
    await assertFails(verified(BOB).ref(`users/${ALICE}/sync/notes/${NOTE}`).set(note()));
  });
});
