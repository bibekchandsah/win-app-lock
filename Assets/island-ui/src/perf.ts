export type Stats = {
  samples: number;
  avg: number;
  p50: number;
  p95: number;
  p99: number;
  max: number;
  refresh: number;
};

function percentile(sorted: number[], p: number): number {
  if (sorted.length === 0) return 0;
  const idx = Math.min(sorted.length - 1, Math.max(0, Math.round((p / 100) * (sorted.length - 1))));
  return sorted[idx];
}

function statsOf(samples: number[]): Stats {
  if (samples.length === 0) {
    return { samples: 0, avg: 0, p50: 0, p95: 0, p99: 0, max: 0, refresh: 0 };
  }
  const sorted = [...samples].sort((a, b) => a - b);
  const sum = sorted.reduce((acc, v) => acc + v, 0);
  return {
    samples: sorted.length,
    avg: sum / sorted.length,
    p50: percentile(sorted, 50),
    p95: percentile(sorted, 95),
    p99: percentile(sorted, 99),
    max: sorted[sorted.length - 1],
    refresh: percentile(sorted, 50),
  };
}

/**
 * Collects requestAnimationFrame intervals. `begin()`/`end()` scope a measurement
 * window (a benchmark leg, an animation) without stopping the long-running sampler.
 */
export class FrameMonitor {
  private recent: number[] = [];
  private capture: number[] | null = null;
  private last = 0;

  tick(now: number): void {
    if (this.last > 0) {
      const delta = now - this.last;
      if (delta > 0 && delta < 250) {
        this.recent.push(delta);
        if (this.recent.length > 240) this.recent.shift();
        this.capture?.push(delta);
      }
    }
    this.last = now;
  }

  begin(): void {
    this.capture = [];
  }

  end(): Stats {
    const samples = this.capture ?? [];
    this.capture = null;
    return statsOf(samples.slice(2));
  }

  live(): Stats {
    return statsOf(this.recent);
  }
}

export function formatStats(s: Stats): string {
  if (s.samples === 0) return 'frame —';
  return `frame ${s.avg.toFixed(1)} ms · p95 ${s.p95.toFixed(1)} · max ${s.max.toFixed(1)}`;
}

export function verdict(s: Stats): { p95Pass: boolean; maxPass: boolean; text: string } {
  const budget95 = 1.25 * s.refresh;
  const budgetMax = 2 * s.refresh;
  const p95Pass = s.p95 <= budget95;
  const maxPass = s.max <= budgetMax;
  return {
    p95Pass,
    maxPass,
    text: `${p95Pass && maxPass ? 'PASS' : 'FAIL'} p95 ${s.p95.toFixed(1)} (<=${budget95.toFixed(1)}) max ${s.max.toFixed(1)} (<=${budgetMax.toFixed(1)})`,
  };
}
