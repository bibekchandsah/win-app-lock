<script lang="ts">
  import { onMount } from 'svelte';
  import { Spring } from './spring';
  import { FrameMonitor, formatStats } from './perf';
  import { post, onMessage } from './bridge';
  import HomeTab from './components/HomeTab.svelte';
  import {
    CARD,
    DEFAULT_SETTINGS,
    RESIZE_SPRING,
    SPRING,
    TAB_ORDER,
    TAB_SIZES,
    TIMING,
    clamp01,
    geometryOf,
    lerpRect,
    type DeviceItem,
    type Geometry,
    type HomeStatsState,
    type IslandSnapshot,
    type MediaState,
    type RecentItem,
    type Rect,
    type TabId,
  } from './motion';

  const monitor = new FrameMonitor();
  const spring = new Spring(SPRING.stiffness, SPRING.damping, SPRING.mass);
  const sizeSpring = new Spring(RESIZE_SPRING.stiffness, RESIZE_SPRING.damping, RESIZE_SPRING.mass, 1);
  const cardSpring = new Spring(SPRING.stiffness, SPRING.damping, SPRING.mass, 0);

  type CardAction = { id: string; label: string; primary?: boolean };

  type Card = {
    kind: string;
    icon: string;
    title: string;
    detail: string;
    accent: string;
    value: number;
    actions: CardAction[];
  };

  let canvasEl: HTMLCanvasElement;
  let clipEl: HTMLDivElement;
  let contentEl: HTMLDivElement;
  let foldedViewEl: HTMLDivElement;
  let openViewEl: HTMLDivElement;
  let cardEl: HTMLDivElement;
  let clockEl: HTMLSpanElement;
  let batteryEl: HTMLSpanElement;
  let statsEl: HTMLDivElement;
  let benchEl: HTMLDivElement;
  let debugPanelEl: HTMLDivElement;

  let settings: IslandSnapshot = { ...DEFAULT_SETTINGS };
  // ?tab=<id> opens the shell on a tab (used by --tab on the host and by the spike runs).
  const requestedTab = new URLSearchParams(location.search).get('tab') as TabId | null;
  let activeTab: TabId = requestedTab && requestedTab in TAB_SIZES ? requestedTab : 'home';
  let geometry: Geometry = geometryOf(settings, activeTab);
  const debug = new URLSearchParams(location.search).has('debug')
    || new URLSearchParams(location.search).has('bench');

  let context: CanvasRenderingContext2D | null = null;
  let dpr = 1;

  let target = 0;
  let raf = 0;
  let lastFrame = 0;
  let openTimer: ReturnType<typeof setTimeout> | undefined;
  let closeTimer: ReturnType<typeof setTimeout> | undefined;
  let clockTimer: ReturnType<typeof setInterval> | undefined;
  let benchRunning = false;
  let paused = false;

  /** Tab switches animate from the previous shell size to the new one. */
  let sizing: { from: Rect; to: Rect } | null = null;
  /** Last body rect drawn, so a tab switch starts from what is on screen. */
  let drawnOpen: Rect = geometry.open;

  let pinned = false;
  let quiet = false;

  // Home tab state (island.md 7), fed only by the bridge.
  let media: MediaState = {
    hasSession: false,
    playing: false,
    title: '',
    artist: '',
    app: '',
    position: 0,
    duration: 0,
    art: '',
  };
  let stats: HomeStatsState = { cpu: 0, ram: 0, down: 0, up: 0, volume: 0 };
  let recent: RecentItem[] = [];
  let devices: DeviceItem[] = [];
  let homeVisibleSent = false;

  let card: Card | null = null;
  let cardTarget = 0;
  let cardFading = false;

  // ── Shape ─────────────────────────────────────────────────────────────────

  function sizeCanvas(): void {
    if (!canvasEl) return;
    dpr = window.devicePixelRatio || 1;
    canvasEl.width = Math.round(geometry.pageW * dpr);
    canvasEl.height = Math.round(geometry.pageH * dpr);
    canvasEl.style.width = `${geometry.pageW}px`;
    canvasEl.style.height = `${geometry.pageH}px`;

    context = canvasEl.getContext('2d');
    context?.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  function currentOpenRect(): Rect {
    return sizing ? lerpRect(sizing.from, sizing.to, sizeSpring.value) : geometry.open;
  }

  /** The pill, expanded by a live card when one is showing. */
  function currentFoldedRect(): Rect {
    const t = clamp01(cardSpring.value);
    const k = geometry.folded.w / 196;
    return lerpRect(geometry.folded, { w: CARD.w * k, h: CARD.h * k, r: CARD.r * k }, t);
  }

  /**
   * The body is painted on a canvas that is cleared first, so a shrinking shape can never leave
   * stale pixels behind (the transparent WebView2 surface has no other way to clear them).
   */
  function apply(p: number): void {
    const progress = clamp01(p);
    const { pageW, topRadius, bottomRadius } = geometry;
    const folded = currentFoldedRect();
    const open = currentOpenRect();
    drawnOpen = open;

    const w = progress >= 1 ? open.w : progress <= 0 ? folded.w : folded.w + (open.w - folded.w) * progress;
    const h = progress >= 1 ? open.h : progress <= 0 ? folded.h : folded.h + (open.h - folded.h) * progress;
    const x = (pageW - w) / 2;
    const radiusTop = topRadius;
    const radiusBottom = progress >= 1 ? bottomRadius : folded.r + (bottomRadius - folded.r) * progress;

    if (context) {
      context.clearRect(0, 0, pageW, geometry.pageH);
      context.beginPath();
      context.roundRect(x, 0, w, h, [
        radiusTop,
        radiusTop,
        radiusBottom,
        radiusBottom,
      ]);
      context.fillStyle = settings.fill;
      context.fill();
    }

    if (clipEl) {
      clipEl.style.left = `${x}px`;
      clipEl.style.width = `${w}px`;
      clipEl.style.height = `${h}px`;
      clipEl.style.borderRadius =
        `${radiusTop}px ${radiusTop}px ${radiusBottom}px ${radiusBottom}px`;
    }

    if (openViewEl) {
      const fade = clamp01((progress - 0.45) / 0.4);
      openViewEl.style.opacity = String(sizing ? fade * clamp01(0.35 + 0.65 * sizeSpring.value) : fade);
      openViewEl.style.visibility = progress > 0.02 ? 'visible' : 'hidden';
    }
    if (foldedViewEl) foldedViewEl.style.opacity = String((1 - clamp01(progress * 2.2)) * (1 - clamp01(cardSpring.value)));
    if (cardEl) {
      cardEl.style.opacity = String(clamp01(cardSpring.value) * (1 - clamp01(progress * 2.2)));
      cardEl.style.visibility = cardSpring.value > 0.01 ? 'visible' : 'hidden';
    }

    if (debug && statsEl) statsEl.textContent = formatStats(monitor.live());
  }

  // ── Spring loop ───────────────────────────────────────────────────────────

  function step(now: number): void {
    const dt = lastFrame > 0 ? Math.min(0.05, (now - lastFrame) / 1000) : 1 / 60;
    lastFrame = now;
    monitor.tick(now);

    const foldSettled = spring.step(dt, target);
    const resizeSettled = sizing ? sizeSpring.step(dt, 1) : true;
    const cardSettled = cardSpring.step(dt, cardTarget);
    apply(spring.value);

    if (foldSettled && resizeSettled && cardSettled) {
      raf = 0;
      lastFrame = 0;
      onSettled();
      return;
    }
    raf = requestAnimationFrame(step);
  }

  function schedule(): void {
    if (raf === 0 && !paused) {
      lastFrame = 0;
      raf = requestAnimationFrame(step);
    }
  }

  function onSettled(): void {
    if (sizing) {
      sizing = null;
      if (target === 1) sendLayout('open');
      return;
    }

    if (cardFading && cardTarget === 0) {
      cardFading = false;
      card = null;
    }

    // Re-assert the resting rect: the host's hit region must always match what is on screen,
    // even if a card collapse or a resize reported a different rectangle in between.
    if (target === 0) sendLayout('folded', currentFoldedRect());
    else sendLayout('open', drawnOpen);
  }

  // ── Live cards ────────────────────────────────────────────────────────────

  function showCard(next: Card, animate: boolean): void {
    card = next;
    cardFading = false;
    cardTarget = 1;

    if (animate) {
      // The region must cover the card straight away, before the pill grows.
      sendLayout('folded', {
        w: Math.max(geometry.folded.w, CARD.w * (geometry.folded.w / 196)),
        h: Math.max(geometry.folded.h, CARD.h * (geometry.folded.h / 36)),
        r: CARD.r,
      });
    }

    schedule();
  }

  function clearCard(): void {
    if (!card) return;
    cardFading = true;
    cardTarget = 0;
    schedule();
  }

  // ── Host notifications ────────────────────────────────────────────────────

  /**
   * The host samples only while the Home tab is on screen, so it is told exactly when that is true:
   * open, on Home, not paused (island.md 4.1: no work while hidden).
   */
  function syncHome(): void {
    const visible = target === 1 && activeTab === 'home' && !paused;
    if (visible === homeVisibleSent) return;
    homeVisibleSent = visible;
    post('home.visible', { visible });
  }

  function runTile(id: string): void {
    post('tile.run', { id });
  }

  function mediaCommand(command: string): void {
    post('media.command', { command });
  }

  function setVolumeRequest(percent: number): void {
    post('volume.set', { percent: Math.max(0, Math.min(100, Math.round(percent))) });
  }

  function setOutputDevice(id: string): void {
    post('volume.device', { id });
  }

  function sendLayout(phase: 'open' | 'folded', rect: Rect = geometry.open): void {
    post('layout.target', {
      phase,
      x: Math.round((geometry.pageW - rect.w) / 2),
      y: 0,
      w: Math.round(rect.w),
      h: Math.round(rect.h),
      r: Math.round(rect.r),
      durationMs: phase === 'open' ? 320 : 260,
    });
  }

  /** While a tab resizes the region must cover both sizes, so the shape is never clipped. */
  function sendUnionLayout(from: Rect, to: Rect): void {
    sendLayout('open', {
      w: Math.max(from.w, to.w),
      h: Math.max(from.h, to.h),
      r: Math.max(from.r, to.r),
    });
  }

  function applySettings(next: IslandSnapshot): void {
    settings = next;
    geometry = geometryOf(next, activeTab);
    sizeCanvas();
    apply(spring.value);

    if (clockEl) clockEl.style.display = next.showClock ? '' : 'none';
  }

  function setClock(hhmm: string): void {
    if (clockEl) clockEl.textContent = hhmm;
  }

  function tickClock(): void {
    const now = new Date();
    setClock(`${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`);
  }

  function setBattery(percent: number, charging: boolean, present: boolean): void {
    if (!batteryEl) return;
    if (!present || !settings.showBattery) {
      batteryEl.style.display = 'none';
      return;
    }
    batteryEl.style.display = '';
    batteryEl.textContent = `${Math.round(percent)}%${charging ? ' \u26A1' : ''}`;
  }

  // ── Tabs ──────────────────────────────────────────────────────────────────

  function selectTab(id: TabId): void {
    if (id === activeTab || target === 0) return;

    const from = drawnOpen;
    activeTab = id;
    geometry = geometryOf(settings, id);
    sizeCanvas();
    sizing = { from, to: geometry.open };
    sizeSpring.snapTo(0);
    sendUnionLayout(from, geometry.open);
    schedule();
    syncHome();
  }

  function cycleTab(delta: number): void {
    const index = TAB_ORDER.indexOf(activeTab);
    const next = TAB_ORDER[(index + delta + TAB_ORDER.length) % TAB_ORDER.length];
    selectTab(next);
  }

  // ── Hover ─────────────────────────────────────────────────────────────────

  function hoverIn(): void {
    if (benchRunning) return;
    clearTimeout(closeTimer);
    if (target === 1) return;
    clearTimeout(openTimer);
    openTimer = setTimeout(startOpen, TIMING.openDelay);
  }

  function hoverOut(): void {
    if (benchRunning || pinned) return;
    clearTimeout(openTimer);
    if (target === 0) return;
    clearTimeout(closeTimer);
    closeTimer = setTimeout(startClose, TIMING.closeDelay);
  }

  function startOpen(): void {
    sizing = null;
    sendLayout('open');
    target = 1;
    schedule();
    syncHome();
  }

  function startClose(): void {
    openTimer && clearTimeout(openTimer);
    sizing = null;
    target = 0;
    schedule();
    syncHome();
  }

  /** The wheel over the folded pill changes the system volume (island.md 5.1). */
  function onWheel(event: WheelEvent): void {
    if (target !== 0 || sizing) return;
    event.preventDefault();

    // One wheel notch is about 100 px; a fast flick carries several, so the step follows it.
    const notches = Math.min(3, Math.max(1, Math.round(Math.abs(event.deltaY) / 100)));
    const delta = event.deltaY === 0 ? 0 : event.deltaY < 0 ? notches : -notches;
    if (delta !== 0) post('volume.step', { delta });
  }

  function onKeyDown(event: KeyboardEvent): void {
    if (target === 0) return;

    if (event.key === 'Escape') {
      event.preventDefault();
      startClose();
      return;
    }
    if (event.ctrlKey && event.key === 'Tab') {
      event.preventDefault();
      cycleTab(event.shiftKey ? -1 : 1);
      return;
    }
    if (event.key === 'ArrowRight') {
      event.preventDefault();
      cycleTab(1);
      return;
    }
    if (event.key === 'ArrowLeft') {
      event.preventDefault();
      cycleTab(-1);
    }
  }

  // ── Benchmark ─────────────────────────────────────────────────────────────

  function animateTo(value: number, holdMs: number): Promise<void> {
    target = value;
    schedule();
    return new Promise((resolve) => setTimeout(resolve, holdMs));
  }

  async function runBenchmark(): Promise<void> {
    if (benchRunning) return;
    benchRunning = true;
    clearTimeout(openTimer);
    clearTimeout(closeTimer);

    const cycles = Number(new URLSearchParams(location.search).get('cycles') ?? '100') || 100;

    spring.snapTo(0);
    target = 0;
    apply(0);
    if (benchEl) benchEl.textContent = 'warming up\u2026';
    await new Promise((resolve) => setTimeout(resolve, 150));

    monitor.begin();
    for (let i = 0; i < cycles; i++) {
      if (i % 2 === 0) sendLayout('open');
      await animateTo(1, TIMING.benchLeg);
      await animateTo(0, TIMING.benchLeg);
    }
    const stats = monitor.end();
    const budget95 = 1.25 * stats.refresh;
    const budgetMax = 2 * stats.refresh;
    const pass = stats.p95 <= budget95 && stats.max <= budgetMax;

    if (benchEl) {
      benchEl.textContent = `canvas: p95 ${stats.p95.toFixed(1)} \u00B7 max ${stats.max.toFixed(1)} \u00B7 ${pass ? 'PASS' : 'FAIL'}`;
    }

    post('perf.report', {
      variant: 'canvas',
      cycles,
      ...stats,
      text: `${pass ? 'PASS' : 'FAIL'} p95 ${stats.p95.toFixed(1)} (<=${budget95.toFixed(1)}) max ${stats.max.toFixed(1)} (<=${budgetMax.toFixed(1)})`,
    });

    spring.snapTo(0);
    target = 0;
    apply(0);
    benchRunning = false;
    post('perf.done', { cycles });
  }

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  onMount(() => {
    sizeCanvas();
    applySettings({ ...DEFAULT_SETTINGS });
    tickClock();
    clockTimer = setInterval(tickClock, 1000);

    if (debug) debugPanelEl.style.display = 'flex';

    const onResize = (): void => {
      sizeCanvas();
      apply(spring.value);
    };
    window.addEventListener('resize', onResize);
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('wheel', onWheel, { passive: false });

    onMessage((message) => {
      switch (message.type) {
        case 'settings.snapshot': {
          const payload = message.payload as IslandSnapshot | undefined;
          if (payload) applySettings({ ...DEFAULT_SETTINGS, ...payload });
          break;
        }
        case 'state.time': {
          const hhmm = (message.payload as { hhmm?: string } | undefined)?.hhmm;
          if (hhmm) setClock(hhmm);
          break;
        }
        case 'state.battery': {
          const payload = message.payload as
            | { percent: number; charging: boolean; present: boolean }
            | undefined;
          if (payload) setBattery(payload.percent, payload.charging, payload.present);
          break;
        }
        case 'state.hover': {
          const inside = (message.payload as { inside?: boolean } | undefined)?.inside === true;
          if (inside) hoverIn();
          else hoverOut();
          break;
        }
        case 'state.media': {
          const payload = message.payload as Partial<MediaState> | undefined;
          if (payload) media = { ...media, ...payload };
          break;
        }
        case 'state.stats': {
          const payload = message.payload as Partial<HomeStatsState> | undefined;
          if (payload) stats = { ...stats, ...payload };
          break;
        }
        case 'state.volume': {
          const payload = message.payload as { percent?: number; devices?: DeviceItem[] } | undefined;
          if (payload) {
            if (typeof payload.percent === 'number') stats = { ...stats, volume: payload.percent };
            if (Array.isArray(payload.devices)) devices = payload.devices;
          }
          break;
        }
        case 'state.recent': {
          const payload = message.payload as { items?: RecentItem[] } | undefined;
          if (payload && Array.isArray(payload.items)) recent = payload.items;
          break;
        }
        case 'event.card': {
          const payload = message.payload as
            | {
                kind?: string;
                icon?: string;
                title?: string;
                detail?: string;
                accent?: string;
                value?: number;
                animate?: boolean;
                actions?: CardAction[];
              }
            | undefined;
          if (!payload || !payload.kind || !payload.title) break;
          showCard(
            {
              kind: payload.kind,
              icon: payload.icon ?? '',
              title: payload.title,
              detail: payload.detail ?? '',
              accent: payload.accent ?? '#FFFFFF',
              value: typeof payload.value === 'number' ? payload.value : -1,
              actions: Array.isArray(payload.actions)
                ? payload.actions.filter(
                    (action): action is CardAction =>
                      typeof action?.id === 'string' && typeof action?.label === 'string',
                  )
                : [],
            },
            payload.animate !== false,
          );
          break;
        }
        case 'event.card.clear':
          clearCard();
          break;
        case 'anim.pause':
          paused = true;
          clearTimeout(openTimer);
          clearTimeout(closeTimer);
          syncHome();
          break;
        case 'anim.resume':
          paused = false;
          syncHome();
          break;
      }
    });

    post('ui.ready', { page: { w: geometry.pageW, h: geometry.pageH } });

    if (new URLSearchParams(location.search).has('bench')) {
      setTimeout(() => void runBenchmark(), 500);
    }

    return () => {
      window.removeEventListener('resize', onResize);
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('wheel', onWheel);
      if (clockTimer) clearInterval(clockTimer);
      clearTimeout(openTimer);
      clearTimeout(closeTimer);
      if (raf !== 0) cancelAnimationFrame(raf);
    };
  });
</script>

<div class="page" role="presentation">
  <canvas class="body" bind:this={canvasEl} aria-hidden="true"></canvas>

  <div class="clip" bind:this={clipEl}>
    <div
      class="content"
      bind:this={contentEl}
      style={`width:${Math.round(geometry.open.w)}px;height:${Math.round(geometry.open.h)}px;margin-left:${-Math.round(geometry.open.w / 2)}px`}
    >
      <div class="folded-row" bind:this={foldedViewEl}>
        <span class="clock" bind:this={clockEl}>--:--</span>
        <span class="battery" bind:this={batteryEl}></span>
      </div>

      <div class="card" bind:this={cardEl}>
        <span class="card-icon" style={`color:${card?.accent ?? '#fff'}`}>{card?.icon ?? ''}</span>
        <div class="card-text">
          <span class="card-title">{card?.title ?? ''}</span>
          {#if card && card.detail}<span class="card-detail">{card.detail}</span>{/if}
        </div>
        {#if card && card.kind === 'volume'}
          <div class="card-bar" aria-hidden="true">
            <div class="card-bar-fill" style={`width:${Math.max(0, Math.min(100, card.value))}%`}></div>
          </div>
        {/if}
        {#if card && card.actions.length > 0}
          <div class="card-actions">
            {#each card.actions as action (action.id)}
              <button
                type="button"
                class="card-action"
                class:primary={action.primary}
                onclick={() => post('card.action', { id: action.id })}
              >
                {action.label}
              </button>
            {/each}
          </div>
        {/if}
      </div>

      <div class="open-view" bind:this={openViewEl}>
      <div class="tabs" role="tablist" aria-label="Island tabs">
        <div class="tab-strip">
          {#each TAB_ORDER as id (id)}
            <button
              type="button"
              class:active={id === activeTab}
              class="tab"
              role="tab"
              aria-selected={id === activeTab}
              title={TAB_SIZES[id].label}
              onclick={() => selectTab(id)}
            >
              <span class="glyph" aria-hidden="true">{TAB_SIZES[id].icon}</span>
              {#if id === activeTab}<span class="tab-label">{TAB_SIZES[id].label}</span>{/if}
            </button>
          {/each}
        </div>

        <div class="actions">
          <button
            type="button"
            class="chip"
            class:on={quiet}
            title="Quiet: pause live cards"
            onclick={() => (quiet = !quiet)}
          >
            <span class="glyph" aria-hidden="true">{'\uE708'}</span>
          </button>
          <button
            type="button"
            class="chip"
            class:on={pinned}
            title="Keep the island open"
            onclick={() => (pinned = !pinned)}
          >
            <span class="glyph" aria-hidden="true">{'\uE718'}</span>
          </button>
          <button type="button" class="chip" title="Island settings">
            <span class="glyph" aria-hidden="true">{'\uE115'}</span>
          </button>
        </div>
      </div>

      <div class="shell-body" class:home={activeTab === 'home'}>
        {#if activeTab === 'home'}
          <HomeTab
            {media}
            {stats}
            {recent}
            {devices}
            onTile={runTile}
            onMedia={mediaCommand}
            onVolume={setVolumeRequest}
            onDevice={setOutputDevice}
            onOpenClipboard={() => selectTab('clipboard')}
          />
        {:else}
          <span class="placeholder-glyph" aria-hidden="true">{TAB_SIZES[activeTab].icon}</span>
          <span class="placeholder-label">{TAB_SIZES[activeTab].label}</span>
        {/if}
      </div>
      </div>
    </div>
  </div>

  <div class="debug" bind:this={debugPanelEl}>
    {#if debug}
      <div class="stats" bind:this={statsEl}>frame —</div>
      <div class="spike-controls">
        <button type="button" onclick={() => void runBenchmark()}>Run benchmark</button>
      </div>
      <div class="bench" bind:this={benchEl} aria-live="polite"></div>
    {/if}
  </div>
</div>
