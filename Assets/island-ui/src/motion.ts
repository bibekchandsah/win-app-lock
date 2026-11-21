// One place for every size and timing constant (island.md 4.2 / 6 tables).

export type Rect = { w: number; h: number; r: number };

export const FOLDED: Rect = { w: 196, h: 36, r: 18 };

/** Open shell sizes per tab, in design DIPs at 100% scale (island.md 6). */
export const TAB_SIZES = {
  home: { label: 'Home', icon: '\uE80F', w: 560, h: 250 },
  clipboard: { label: 'Clipboard', icon: '\uE8C8', w: 560, h: 300 },
  shelf: { label: 'Shelf', icon: '\uE8F1', w: 560, h: 250 },
  text: { label: 'Text', icon: '\uE8D2', w: 560, h: 250 },
  chat: { label: 'Chat', icon: '\uE8BD', w: 600, h: 440 },
  note: { label: 'Note', icon: '\uE70F', w: 580, h: 330 },
  weather: { label: 'Weather', icon: '\uE753', w: 620, h: 290 },
  controls: { label: 'Controls', icon: '\uE713', w: 560, h: 200 },
} as const;

export type TabId = keyof typeof TAB_SIZES;
export const TAB_ORDER: TabId[] = [
  'home',
  'clipboard',
  'shelf',
  'text',
  'chat',
  'note',
  'weather',
  'controls',
];

export const OPEN_RADIUS = 24;

/** Window is sized for the largest tab plus a small margin for the anti-aliased edge. */
export const MARGIN = { left: 8, right: 8, bottom: 8 };
export const PAGE = {
  w: TAB_SIZES.weather.w + MARGIN.left + MARGIN.right,
  h: TAB_SIZES.chat.h + MARGIN.bottom,
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
/** Tab resize is stiffer: a short, quick settle with no overshoot in width. */
export const RESIZE_SPRING = { stiffness: 320, damping: 34, mass: 1 };

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

export function scaleOf(settings: IslandSnapshot): number {
  return Math.min(1.5, Math.max(0.7, settings.sizePercent / 100));
}

/** Every size is the design size multiplied by the island size setting. */
export function geometryOf(settings: IslandSnapshot, tab: TabId = 'home'): Geometry {
  const k = scaleOf(settings);
  const capsule = settings.shape === 'Capsule';
  const tabSize = TAB_SIZES[tab];
  const folded: Rect = { w: FOLDED.w * k, h: FOLDED.h * k, r: FOLDED.r * k };
  const open: Rect = { w: tabSize.w * k, h: tabSize.h * k, r: OPEN_RADIUS * k };

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

export function lerpRect(from: Rect, to: Rect, t: number): Rect {
  const p = clamp01(t);
  return {
    w: lerp(from.w, to.w, p),
    h: lerp(from.h, to.h, p),
    r: lerp(from.r, to.r, p),
  };
}

export function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}
