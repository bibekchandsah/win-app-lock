/**
 * Types for the Realtime Database layout in sync.md §7. These describe only the
 * shape of what crosses the wire; the PC remains the authority on permissions
 * and re-validates every command it receives.
 */

/** AES-256-GCM envelope: base64 nonce (12 bytes) and base64 ciphertext||tag. */
export interface Envelope {
  v: 1;
  n: string;
  c: string;
}

/** Plain control state the cloud is allowed to see (sync.md §6). */
export interface BatteryState {
  percent: number;
  charging: boolean;
  pluggedIn?: boolean;
  saver?: boolean;
  remainingMinutes?: number;
}

export interface PerfState {
  cpu: number;
  ram: number;
  disk: number;
  gpu: number;
  netDown?: number;
  netUp?: number;
}

export interface LiveState {
  battery?: BatteryState;
  radios?: { wifi?: boolean; bluetooth?: boolean };
  audio?: { muted?: boolean; volume?: number };
  display?: { brightness?: number; blur?: boolean };
  perf?: PerfState;
  keyboard?: { enabled?: boolean };
}

export interface DeviceMeta {
  app: string;
  os: string;
  platform: string;
  createdAt: number;
  nameEnc: Envelope;
}

export interface Presence {
  online: boolean;
  lastSeen: number;
}

export type CommandTier = 1 | 2 | 3;

export type CommandStatus =
  | 'ok'
  | 'failed'
  | 'rejected'
  | 'expired'
  | 'needs_elevation'
  | 'needs_local_approval'
  | 'cancelled';

export interface CommandEnvelope {
  type: string;
  payload: string;
  createdAt: number;
  expiresAt: number;
  nonce: string;
  tier: CommandTier;
  session: string;
  status: 'pending';
  proof?: string;
  stepUpAt?: number;
}

export interface CommandResult {
  status: CommandStatus;
  message?: string;
  at: number;
}

export interface AuditEntry {
  type: string;
  tier: CommandTier;
  status: CommandStatus;
  at: number;
  session?: string;
}

export interface ViewerHeartbeat {
  ts: number;
  ua: string;
  tab?: string;
}

export interface SyncNote {
  envelope?: Envelope;
  updatedAt: number;
  deviceId: string;
  deleted: boolean;
}

export const DEVICE_ID_PATTERN = /^[0-9a-f]{32}$/;
export const COMMAND_TYPE_PATTERN = /^[a-z]+(\.[a-z]+)+$/;
export const COMMAND_ID_PATTERN = /^[A-Za-z0-9_-]{8,64}$/;
