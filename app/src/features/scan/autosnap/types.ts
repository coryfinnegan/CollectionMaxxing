// The auto-snap vocabulary (docs/05 "Auto-snap"). Pure types: nothing here knows about cameras.
export interface FrameSignals {
  /** How long the phone has been held still. */
  steadyForMs: number;
  /** 0..1, higher is sharper. */
  sharpness: number;
  /** Text lines Vision found in the frame. */
  textBoxes: number;
  /** 0..1, the share of the frame the largest rectangle covers. */
  rectangleCoverage: number;
  /** Spine-like rectangles in view. Absent on feeds that only measure the largest one. */
  rectangleCount?: number;
  /** Width over height of the largest rectangle; null when no rectangle was measured. */
  dominantAspect?: number | null;
  /** A perceptual hash of the view, as hex. Two views of the same thing give hashes that are close. */
  viewHash: string;
  flashOn: boolean;
  /** A barcode that can be read right now. */
  barcodesInView: number;
}

export type SnapTrigger = 'auto' | 'shutter' | 'import';

/** The thresholds. One object, injected; CAM-3b swaps in the server's `autoSnap` values. */
export interface AutoSnapConfig {
  /** Held still this long before a candidate snaps. */
  steadyMs: number;
  /** Sharpness at or above this counts as sharp. */
  minSharpness: number;
  /** This many text boxes make a view worth a picture. */
  minTextBoxes: number;
  /** Or a rectangle covering at least this share of the frame. */
  minRectangleCoverage: number;
  /** After a snap, the view must move at least this far (0..1 of the hash's bits) before the next one. */
  minViewDistance: number;
  /** A snap-to-snap pause, whatever the view does. */
  cooldownMs: number;
  /** This many pictures in a row with nothing found raise the thresholds one step. */
  misfireStreak: number;
  /** Each step raises the thresholds by this fraction. */
  misfireRaise: number;
  /** The most steps the thresholds can rise. */
  maxRaiseSteps: number;
  /** Nothing useful for this long and the hint chip shows. */
  hintAfterMs: number;
}
