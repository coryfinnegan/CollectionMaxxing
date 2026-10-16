// The auto-snap state machine (docs/05): idle -> candidate -> snap -> cooldown. Pure: the caller feeds it signals and a
// clock reading and acts on the answer. Every number comes from the injected AutoSnapConfig.
import { hashDistance } from './hash';
import type { AutoSnapConfig, FrameSignals } from './types';

/** `snap` is the instant of taking the picture; the machine is in `cooldown` straight after it. */
export type Phase = 'idle' | 'candidate' | 'cooldown';

export interface MachineState {
  phase: Phase;
  lastSnapHash: string | null;
  lastSnapAt: number | null;
  /** Pictures in a row that found nothing. */
  missStreak: number;
  /** How many times the thresholds have been raised. */
  raiseSteps: number;
}

export const initialState = (): MachineState => ({ phase: 'idle', lastSnapHash: null, lastSnapAt: null, missStreak: 0, raiseSteps: 0 });

/** The thresholds after any misfire raises. Steadiness, sharpness, coverage and text count all go up together. */
export function effectiveConfig(state: MachineState, config: AutoSnapConfig): AutoSnapConfig {
  const factor = 1 + config.misfireRaise * state.raiseSteps;
  return {
    ...config,
    steadyMs: config.steadyMs * factor,
    minSharpness: Math.min(1, config.minSharpness * factor),
    minRectangleCoverage: Math.min(1, config.minRectangleCoverage * factor),
    minTextBoxes: Math.ceil(config.minTextBoxes * factor - 1e-9),
  };
}

const worthAPicture = (s: FrameSignals, c: AutoSnapConfig) =>
  s.barcodesInView === 0 && s.sharpness >= c.minSharpness && (s.textBoxes >= c.minTextBoxes || s.rectangleCoverage >= c.minRectangleCoverage);

export interface Step {
  state: MachineState;
  /** True when this signal takes an automatic picture. */
  snap: boolean;
}

/** One signal in, one decision out. */
export function step(state: MachineState, signals: FrameSignals, nowMs: number, config: AutoSnapConfig): Step {
  const c = effectiveConfig(state, config);

  if (state.phase === 'cooldown') {
    const moved = state.lastSnapHash === null || hashDistance(signals.viewHash, state.lastSnapHash) >= c.minViewDistance;
    const rested = state.lastSnapAt === null || nowMs - state.lastSnapAt >= c.cooldownMs;
    return { state: moved && rested ? { ...state, phase: 'idle' } : state, snap: false };
  }

  // A barcode in view always wins: nothing snaps, and a pending candidate is dropped.
  if (signals.barcodesInView > 0) return { state: { ...state, phase: 'idle' }, snap: false };

  if (!worthAPicture(signals, c)) return { state: { ...state, phase: 'idle' }, snap: false };

  if (signals.steadyForMs >= c.steadyMs) {
    return { state: { ...state, phase: 'cooldown', lastSnapHash: signals.viewHash, lastSnapAt: nowMs }, snap: true };
  }
  return { state: { ...state, phase: 'candidate' }, snap: false };
}

/** The shutter: a picture now, whatever the state. It starts a cooldown like any snap, from the view it saw. */
export function shutter(state: MachineState, signals: FrameSignals | null, nowMs: number): MachineState {
  return { ...state, phase: 'cooldown', lastSnapHash: signals?.viewHash ?? state.lastSnapHash, lastSnapAt: nowMs };
}

/** What the picture turned out to be. Enough misses in a row raise the thresholds; a find starts the count again. */
export function recordOutcome(state: MachineState, found: boolean, config: AutoSnapConfig): MachineState {
  if (found) return { ...state, missStreak: 0, raiseSteps: 0 };
  const missStreak = state.missStreak + 1;
  if (missStreak < config.misfireStreak) return { ...state, missStreak };
  return { ...state, missStreak: 0, raiseSteps: Math.min(config.maxRaiseSteps, state.raiseSteps + 1) };
}
