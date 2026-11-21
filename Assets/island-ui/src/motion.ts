// One place for every size and timing constant (island.md 4.2 table, spike subset).

export type Rect = { w: number; h: number; r: number };

export const FOLDED: Rect = { w: 196, h: 36, r: 18 };
export const OPEN: Rect = { w: 420, h: 180, r: 24 };

/** Window is the open island plus a small margin for the anti-aliased edge. */
export const MARGIN = { left: 8, right: 8, bottom: 8 };
export const PAGE = {
  w: OPEN.w + MARGIN.left + MARGIN.right,
  h: OPEN.h + MARGIN.bottom,
};

export const TIMING = {
  /** Hover must stay this long before opening (island.md 4.2: 150 ms). */
  openDelay: 150,
  /** Pointer must stay away this long before closing (island.md 4.2: 350 ms). */
  closeDelay: 350,
  /** One leg of the benchmark before the next retarget. */
  benchLeg: 220,
};

/** Spring feel: 320 ms open, 0.72 damping / 260 ms close, 0.90 damping in the native spec. */
export const SPRING = { stiffness: 190, damping: 27, mass: 1 };

export const COLORS = {
  closed: 'rgba(17, 17, 20, 0.949)',
  open: 'rgba(22, 22, 26, 0.969)',
};

/** What the host sends in `settings.snapshot`. */
export type IslandSnapshot = {
  enabled: boolean;
  position: string;
  shape: string;
  theme: string;
  dark: boolean;
  sizePercent: number;
  opacityPercent: number;
  hardwareAcceleration: boolean;
  showClock: boolean;
  showBattery: boolean;
  hotkey: string;
  fill: string;
  text: string;
  dimText: string;
  faintText: string;
  version: string;
};

export const DEFAULT_SETTINGS: IslandSnapshot = {
  enabled: true,
  position: 'TopCenter',
  shape: 'Notch',
  theme: 'System',
  dark: true,
  sizePercent: 100,
  opacityPercent: 95,
  hardwareAcceleration: true,
  showClock: true,
  showBattery: true,
  hotkey: 'Ctrl+Alt+I',
  fill: COLORS.closed,
  text: '#FFFFFF',
  dimText: 'rgba(255, 255, 255, 0.72)',
  faintText: 'rgba(255, 255, 255, 0.5)',
  version: '1.0.0',
};

export type Geometry = {
  pageW: number;
  pageH: number;
  folded: Rect;
  open: Rect;
  /** Top corner radius: a notch is nearly square at the screen edge, a capsule is round. */
  topRadius: number;
  bottomRadius: number;
};

/** Every size is the design size multiplied by the island size setting. */
export function geometryOf(settings: IslandSnapshot): Geometry {
  const k = Math.min(1.5, Math.max(0.7, settings.sizePercent / 100));
  const capsule = settings.shape === 'Capsule';
  const folded: Rect = { w: FOLDED.w * k, h: FOLDED.h * k, r: FOLDED.r * k };
  const open: Rect = { w: OPEN.w * k, h: OPEN.h * k, r: OPEN.r * k };

  return {
    pageW: PAGE.w * k,
    pageH: PAGE.h * k,
    folded,
    open,
    topRadius: capsule ? open.r : 4 * k,
    bottomRadius: open.r,
  };
}

export function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

export function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}
