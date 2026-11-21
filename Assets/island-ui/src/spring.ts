/**
 * Retargetable damped spring. Keeps its velocity, so the target can change in the
 * middle of a motion (hover in, hover out) without a visible jump.
 */
export class Spring {
  value: number;
  velocity = 0;

  constructor(
    public stiffness: number,
    public damping: number,
    public mass = 1,
    start = 0,
  ) {
    this.value = start;
  }

  /** Advances one frame; returns true once the spring has settled on the target. */
  step(dt: number, target: number): boolean {
    const force = -this.stiffness * (this.value - target) - this.damping * this.velocity;
    this.velocity += (force / this.mass) * dt;
    this.value += this.velocity * dt;

    if (Math.abs(this.value - target) < 0.0008 && Math.abs(this.velocity) < 0.01) {
      this.value = target;
      this.velocity = 0;
      return true;
    }
    return false;
  }

  snapTo(value: number): void {
    this.value = value;
    this.velocity = 0;
  }
}
