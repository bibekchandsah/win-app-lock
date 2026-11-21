<script lang="ts">
  import { onMount } from 'svelte';
  import { Spring } from './spring';
  import { FrameMonitor, formatStats } from './perf';
  import { post, onMessage } from './bridge';
  import {
    DEFAULT_SETTINGS,
    SPRING,
    TIMING,
    clamp01,
    geometryOf,
    lerp,
    type Geometry,
    type IslandSnapshot,
  } from './motion';

  const monitor = new FrameMonitor();
  const spring = new Spring(SPRING.stiffness, SPRING.damping, SPRING.mass);

  let islandEl: HTMLDivElement;
  let bodyBlockEl: HTMLDivElement;
  let clockEl: HTMLSpanElement;
  let batteryEl: HTMLSpanElement;
  let statsEl: HTMLDivElement;
  let benchEl: HTMLDivElement;
  let debugPanelEl: HTMLDivElement;

  let settings: IslandSnapshot = { ...DEFAULT_SETTINGS };
  let geometry: Geometry = geometryOf(settings);
  let variant: 'clip' | 'size' = 'clip';
  const debug = new URLSearchParams(location.search).has('debug')
    || new URLSearchParams(location.search).has('bench');

  let target = 0;
  let raf = 0;
  let lastFrame = 0;
  let openTimer: ReturnType<typeof setTimeout> | undefined;
  let closeTimer: ReturnType<typeof setTimeout> | undefined;
  let clockTimer: ReturnType<typeof setInterval> | undefined;
  let benchRunning = false;
  let paused = false;

  // ── Shape ─────────────────────────────────────────────────────────────────

  function apply(p: number): void {
    if (!islandEl) return;

    const progress = clamp01(p);
    const { open, folded, topRadius, bottomRadius } = geometry;
    const top = lerp(topRadius, topRadius, progress);
    const bottom = lerp(lerp(folded.r, bottomRadius, progress), bottomRadius, 0);

    if (variant === 'clip') {
      const insetX = (1 - progress) * ((open.w - folded.w) / 2);
      const insetBottom = (1 - progress) * (open.h - folded.h);
      const radiusTop = lerp(topRadius, topRadius, progress);
      const radiusBottom = lerp(folded.r, bottomRadius, progress);
      islandEl.style.width = `${open.w}px`;
      islandEl.style.height = `${open.h}px`;
      islandEl.style.left = `${(geometry.pageW - open.w) / 2}px`;
      islandEl.style.borderRadius = '0px';
      islandEl.style.clipPath =
        `inset(0px ${insetX}px ${insetBottom}px ${insetX}px round ` +
        `${radiusTop}px ${radiusTop}px ${radiusBottom}px ${radiusBottom}px)`;
    } else {
      const w = lerp(folded.w, open.w, progress);
      const h = lerp(folded.h, open.h, progress);
      islandEl.style.clipPath = 'none';
      islandEl.style.width = `${w}px`;
      islandEl.style.height = `${h}px`;
      islandEl.style.left = `${(geometry.pageW - w) / 2}px`;
      islandEl.style.borderRadius =
        `${top}px ${top}px ${lerp(folded.r, bottom, progress)}px ${lerp(folded.r, bottom, progress)}px`;
    }

    islandEl.style.background = settings.fill;
    bodyBlockEl.style.opacity = String(clamp01((progress - 0.45) / 0.4));
  }

  function setVariant(next: 'clip' | 'size'): void {
    variant = next;
    apply(spring.value);
  }

  // ── Spring loop ───────────────────────────────────────────────────────────

  function step(now: number): void {
    const dt = lastFrame > 0 ? Math.min(0.05, (now - lastFrame) / 1000) : 1 / 60;
    lastFrame = now;
    monitor.tick(now);
    const settled = spring.step(dt, target);
    apply(spring.value);
    if (debug && statsEl) statsEl.textContent = formatStats(monitor.live());

    if (settled) {
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
    if (target === 0) sendLayout('folded');
  }

  // ── Host notifications ────────────────────────────────────────────────────

  function sendLayout(phase: 'open' | 'folded'): void {
    const rect = phase === 'open' ? geometry.open : geometry.folded;
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

  function applySettings(next: IslandSnapshot): void {
    settings = next;
    geometry = geometryOf(next);

    const root = document.documentElement;
    root.style.setProperty('--island-fill', next.fill);
    root.style.setProperty('--island-text', next.text);
    root.style.setProperty('--island-text-dim', next.dimText);
    root.style.setProperty('--island-text-faint', next.faintText);
    root.style.setProperty('--island-scale', String(next.sizePercent / 100));

    clockEl.style.display = next.showClock ? '' : 'none';
    if (!next.showBattery) batteryEl.style.display = 'none';

    apply(spring.value);
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
    batteryEl.textContent = `${Math.round(percent)}%${charging ? ' ⚡' : ''}`;
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
    if (benchRunning) return;
    clearTimeout(openTimer);
    if (target === 0) return;
    clearTimeout(closeTimer);
    closeTimer = setTimeout(startClose, TIMING.closeDelay);
  }

  function startOpen(): void {
    sendLayout('open');
    target = 1;
    schedule();
  }

  function startClose(): void {
    target = 0;
    schedule();
  }

  // ── Benchmark (milestone 3 reports) ───────────────────────────────────────

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

    for (const next of ['clip', 'size'] as const) {
      setVariant(next);
      spring.snapTo(0);
      target = 0;
      apply(0);
      if (benchEl) benchEl.textContent = `${next}: warming up…`;
      await new Promise((resolve) => setTimeout(resolve, 150));

      monitor.begin();
      for (let i = 0; i < cycles; i++) {
        await animateTo(1, TIMING.benchLeg);
        await animateTo(0, TIMING.benchLeg);
      }
      const stats = monitor.end();
      const budget95 = 1.25 * stats.refresh;
      const budgetMax = 2 * stats.refresh;
      const pass = stats.p95 <= budget95 && stats.max <= budgetMax;
      if (benchEl) {
        benchEl.textContent =
          `${next}: p95 ${stats.p95.toFixed(1)} · max ${stats.max.toFixed(1)} · ${pass ? 'PASS' : 'FAIL'}`;
      }
      post('perf.report', {
        variant: next,
        cycles,
        ...stats,
        p95Pass: stats.p95 <= budget95,
        maxPass: stats.max <= budgetMax,
        text: `${pass ? 'PASS' : 'FAIL'} p95 ${stats.p95.toFixed(1)} (<=${budget95.toFixed(1)}) max ${stats.max.toFixed(1)} (<=${budgetMax.toFixed(1)})`,
      });
    }

    spring.snapTo(0);
    target = 0;
    apply(0);
    benchRunning = false;
    post('perf.done', { variants: 2, cycles });
  }

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  onMount(() => {
    applySettings({ ...DEFAULT_SETTINGS });
    tickClock();
    clockTimer = setInterval(tickClock, 1000);

    if (debug) debugPanelEl.style.display = 'flex';

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
        case 'anim.pause':
          paused = true;
          clearTimeout(openTimer);
          clearTimeout(closeTimer);
          break;
        case 'anim.resume':
          paused = false;
          break;
      }
    });

    post('ui.ready', {
      variant,
      page: { w: geometry.pageW, h: geometry.pageH },
    });

    if (new URLSearchParams(location.search).has('bench')) {
      setTimeout(() => void runBenchmark(), 500);
    }

    return () => {
      if (clockTimer) clearInterval(clockTimer);
      clearTimeout(openTimer);
      clearTimeout(closeTimer);
      if (raf !== 0) cancelAnimationFrame(raf);
    };
  });
</script>

<div class="page">
  <div
    class="island"
    bind:this={islandEl}
    role="presentation"
    onpointerenter={hoverIn}
    onpointerleave={hoverOut}
  >
    <div class="content">
      <div class="top-row">
        <span class="clock" bind:this={clockEl}>--:--</span>
        <span class="battery" bind:this={batteryEl} aria-live="off"></span>
      </div>

      <div class="body-block" bind:this={bodyBlockEl}>
        <!-- Milestone 3 fills this shell with tabs, cards and the command bar. -->
        <div class="shell-placeholder"></div>
      </div>
    </div>

    <div class="debug" bind:this={debugPanelEl}>
      {#if debug}
        <div class="stats" bind:this={statsEl}>frame —</div>
        <div class="spike-controls">
          <button type="button" data-active={variant === 'clip'} onclick={() => setVariant('clip')}>
            A · clip-path
          </button>
          <button type="button" data-active={variant === 'size'} onclick={() => setVariant('size')}>
            B · width/height
          </button>
          <button type="button" onclick={() => void runBenchmark()}>Run benchmark</button>
        </div>
        <div class="bench" bind:this={benchEl} aria-live="polite"></div>
      {/if}
    </div>
  </div>
</div>
