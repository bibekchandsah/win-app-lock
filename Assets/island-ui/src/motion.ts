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

export function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

export function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}
