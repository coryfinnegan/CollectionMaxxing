// "Held still": turns device motion readings into how long the phone has been steady. Pure, so it is tested on Windows.
export interface MotionSample {
  /** Rotation speed, radians per second, on the three axes. */
  rotation: { alpha: number; beta: number; gamma: number } | null;
  /** Acceleration without gravity, m/s², on the three axes. */
  acceleration: { x: number; y: number; z: number } | null;
}

export const STILL_ROTATION = 0.3;
export const STILL_ACCELERATION = 0.25;

const magnitude = (a: number, b: number, c: number) => Math.sqrt(a * a + b * b + c * c);

export class MotionTracker {
  private stillSince: number | null = null;

  /** Feed one reading. A reading with nothing in it counts as moving: no signal never means steady. */
  push(sample: MotionSample, nowMs: number) {
    const spin = sample.rotation ? magnitude(sample.rotation.alpha, sample.rotation.beta, sample.rotation.gamma) : Infinity;
    const shake = sample.acceleration ? magnitude(sample.acceleration.x, sample.acceleration.y, sample.acceleration.z) : Infinity;
    if (spin < STILL_ROTATION && shake < STILL_ACCELERATION) this.stillSince ??= nowMs;
    else this.stillSince = null;
  }

  steadyForMs(nowMs: number): number {
    return this.stillSince === null ? 0 : Math.max(0, nowMs - this.stillSince);
  }

  reset() {
    this.stillSince = null;
  }
}
