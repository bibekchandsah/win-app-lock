<script lang="ts">
  import { onMount } from 'svelte';
  import { Spring } from './spring';
  import { FrameMonitor, formatStats, verdict, type Stats } from './perf';
  import { post, onMessage } from './bridge';
  import { FOLDED, OPEN, PAGE, TIMING, SPRING, COLORS, lerp, clamp01 } from './motion';

  const monitor = new FrameMonitor();
  const spring = new Spring(SPRING.stiffness, SPRING.damping, SPRING.mass);

  let islandEl: HTMLDivElement;
  let contentEl: HTMLDivElement;
  let bodyBlockEl: HTMLDivElement;
  let statsEl: HTMLDivElement;
  let benchEl: HTMLDivElement;
  let clockEl: HTMLSpanElement;
  let diagEl: HTMLSpanElement;

  const diag = { moves: 0, enters: 0, leaves: 0, last: '' };

  // Measured in the milestone 1 spike: both variants hold p95 16.9 ms, but clip-path produced
  // 100 ms+ max frames while the size variant stayed at 17.6 ms (see docs/island-technique.md).
  let variant: 'clip' | 'size' = 'size';
  let target = 0;
  let raf = 0;
  let lastFrame = 0;
  let openTimer: ReturnType<typeof setTimeout> | undefined;
  let closeTimer: ReturnType<typeof setTimeout> | undefined;
  let benchRunning = false;
  let paused = false;

  // ── Shape ─────────────────────────────────────────────────────────────────

  function apply(p: number): void {
    const progress = clamp01(p);

    if (variant === 'clip') {
      const insetX = (1 - progress) * ((OPEN.w - FOLDED.w) / 2);
      const insetBottom = (1 - progress) * (OPEN.h - FOLDED.h);
      const radius = lerp(FOLDED.r, OPEN.r, progress);
      islandEl.style.width = `${OPEN.w}px`;
      islandEl.style.height = `${OPEN.h}px`;
      islandEl.style.left = `${(PAGE.w - OPEN.w) / 2}px`;
      islandEl.style.borderRadius = '0px';
      islandEl.style.clipPath = `inset(0px ${insetX}px ${insetBottom}px ${insetX}px round ${radius}px)`;
    } else {
      const w = lerp(FOLDED.w, OPEN.w, progress);
      const h = lerp(FOLDED.h, OPEN.h, progress);
      const radius = lerp(FOLDED.r, OPEN.r, progress);
      islandEl.style.clipPath = 'none';
      islandEl.style.width = `${w}px`;
      islandEl.style.height = `${h}px`;
      islandEl.style.left = `${(PAGE.w - w) / 2}px`;
      islandEl.style.borderRadius = `${radius}px`;
    }

    islandEl.style.background = COLORS.closed;
    // The clock stays readable folded; everything below it appears with the open motion.
    contentEl.style.opacity = '1';
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
    statsEl.textContent = formatStats(monitor.live());

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
    if (target === 0) {
      sendLayout('folded');
      post('hover.state', { open: false });
    }
  }

  // ── Host notifications ────────────────────────────────────────────────────

  function sendLayout(phase: 'open' | 'folded'): void {
    const rect = phase === 'open' ? OPEN : FOLDED;
    post('layout.target', {
      phase,
      x: (PAGE.w - rect.w) / 2,
      y: 0,
      w: rect.w,
      h: rect.h,
      r: rect.r,
      durationMs: phase === 'open' ? 320 : 260,
    });
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
    post('hover.state', { open: true });
    schedule();
  }

  function startClose(): void {
    target = 0;
    schedule();
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
    const results: Partial<Record<'clip' | 'size', Stats>> = {};

    for (const next of ['clip', 'size'] as const) {
      setVariant(next);
      spring.snapTo(0);
      target = 0;
      apply(0);
      benchEl.textContent = `${next}: warming up…`;
      await new Promise((resolve) => setTimeout(resolve, 150));

      monitor.begin();
      for (let i = 0; i < cycles; i++) {
        await animateTo(1, TIMING.benchLeg);
        await animateTo(0, TIMING.benchLeg);
      }
      const stats = monitor.end();
      results[next] = stats;
      const check = verdict(stats);
      benchEl.textContent =
        `${next}: p95 ${stats.p95.toFixed(1)} · max ${stats.max.toFixed(1)} · ` +
        `${check.p95Pass && check.maxPass ? 'PASS' : 'FAIL'}`;
      post('perf.report', { variant: next, cycles, ...stats, ...check });
    }

    spring.snapTo(0);
    target = 0;
    apply(0);
    benchRunning = false;
    post('perf.done', results);
  }

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  function tickClock(): void {
    const now = new Date();
    clockEl.textContent = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
  }

  onMount(() => {
    apply(0);
    tickClock();

    // Spike diagnostics: proves whether WebView2 delivers pointer input to the page.
    const note = (x: number, y: number): void => {
      diag.last = `${Math.round(x)},${Math.round(y)}`;
      if (diagEl) {
        diagEl.textContent = `mv ${diag.moves} in ${diag.enters} out ${diag.leaves} ${diag.last}`;
      }
    };
    const onMove = (e: PointerEvent): void => {
      diag.moves++;
      note(e.clientX, e.clientY);
    };
    window.addEventListener('pointermove', onMove, { passive: true });
    islandEl.addEventListener('pointerenter', () => diag.enters++);
    islandEl.addEventListener('pointerleave', () => diag.leaves++);
    const clockTimer = setInterval(tickClock, 1000);

    onMessage((message) => {
      if (message.type === 'anim.pause') {
        paused = true;
        clearTimeout(openTimer);
        clearTimeout(closeTimer);
        return;
      }
      if (message.type === 'anim.resume') {
        paused = false;
        return;
      }
      if (message.type === 'state.hover') {
        const inside = (message.payload as { inside?: boolean } | undefined)?.inside === true;
        if (inside) hoverIn();
        else hoverOut();
      }
    });

    post('ui.ready', { variant, page: { w: PAGE.w, h: PAGE.h } });

    if (new URLSearchParams(location.search).has('bench')) {
      setTimeout(() => void runBenchmark(), 500);
    }

    return () => {
      window.removeEventListener('pointermove', onMove);
      clearInterval(clockTimer);
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
    <div class="content" bind:this={contentEl}>
      <span class="clock" bind:this={clockEl}>--:--</span>

      <div class="body-block" bind:this={bodyBlockEl}>
        <div class="stats" bind:this={statsEl}>frame —</div>
        <div class="spike-controls">
          <button
            type="button"
            data-active={variant === 'clip'}
            onclick={() => setVariant('clip')}>A · clip-path</button
          >
          <button
            type="button"
            data-active={variant === 'size'}
            onclick={() => setVariant('size')}>B · width/height</button
          >
          <button type="button" onclick={() => void runBenchmark()}>Run benchmark</button>
        </div>
        <div class="bench" bind:this={benchEl} aria-live="polite"></div>
        <span class="diag" bind:this={diagEl}></span>
        <div class="hint">Hover the notch to open · move away to close</div>
      </div>
    </div>
  </div>
</div>
