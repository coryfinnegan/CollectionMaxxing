import type { AutoSnapConfig } from './types';

/** Starting values. The server's config (CAM-3b) and the owner's evals replace them; the machine hard-codes none. */
export const DEFAULT_AUTO_SNAP_CONFIG: AutoSnapConfig = {
  steadyMs: 700,
  minSharpness: 0.5,
  minTextBoxes: 2,
  minRectangleCoverage: 0.2,
  minViewDistance: 0.25,
  cooldownMs: 800,
  misfireStreak: 3,
  misfireRaise: 0.2,
  maxRaiseSteps: 3,
  hintAfterMs: 5000,
};
