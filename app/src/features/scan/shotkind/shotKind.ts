// Shot classification from framing (design §3): one scanner, no modes, but every shot is recorded as what the framing
// says it is, so engine choice, guidance and evals can condition on it. Pure: signals in, kind out. The auto-snap gate
// is unchanged and feeds in only as `gateFired`.
import type { FrameSignals } from '../autosnap/types';

/** The server's `ShotKind` enum, mirrored (the API spells them the same). */
export type ShotKind = 'barcode' | 'single_cover' | 'shelf_segment' | 'loose_item' | 'unknown';

/** The session's media hint (design §4; #336 owns the real one). Only games and movies can go loose. */
export type MediaHint = 'books' | 'games' | 'movies' | 'mixed';

/** Everything the classifier looks at, cheap signals first. */
export interface ShotSignals {
  /** Stable barcodes in view. At least one always wins. */
  barcodesInView: number;
  /** The share of the frame the largest rectangle covers, 0..1. */
  rectangleCoverage: number;
  /** Spine-like rectangles in view. */
  rectangleCount: number;
  /** Width over height of the largest rectangle; null when no rectangle was measured. */
  dominantAspect: number | null;
  /** Text rows in view. */
  textRows: number;
  /** The person tapped-to-focus a cover. */
  tappedCover: boolean;
  /** The session's media hint, when the session has one. */
  mediaHint?: MediaHint;
  /** Consecutive frames with no barcode in view. */
  framesWithoutBarcode: number;
  /** The auto-snap gate fired for this shot. */
  gateFired: boolean;
}

export interface ShotKindThresholds {
  /** One rectangle covering at least this share of the frame reads as a single cover. */
  singleCoverMinCoverage: number;
  /** A single cover's width-over-height stays within these (book, disc and game shapes). */
  singleCoverAspectMin: number;
  singleCoverAspectMax: number;
  /** This many text rows read as a shelf segment. */
  shelfMinTextRows: number;
  /** Or this many spine-like rectangles. */
  shelfMinRectangles: number;
  /** Single-cover geometry goes loose only after this many barcode-free frames. */
  looseItemMinFrames: number;
}

/** Locked (design §3, §16): changing one is a deliberate contract change. */
export const DEFAULT_SHOT_KIND_THRESHOLDS: ShotKindThresholds = {
  singleCoverMinCoverage: 0.45,
  singleCoverAspectMin: 0.5,
  singleCoverAspectMax: 1.8,
  shelfMinTextRows: 2,
  shelfMinRectangles: 2,
  looseItemMinFrames: 10,
};

const isCoverShaped = (signals: ShotSignals, t: ShotKindThresholds): boolean =>
  signals.rectangleCount === 1 &&
  signals.rectangleCoverage >= t.singleCoverMinCoverage &&
  signals.dominantAspect !== null &&
  signals.dominantAspect >= t.singleCoverAspectMin &&
  signals.dominantAspect <= t.singleCoverAspectMax;

/** What the framing says this shot is. Barcode wins; loose refines single-cover; the gate firing reads as a shelf. */
export function classifyShot(signals: ShotSignals, thresholds: ShotKindThresholds = DEFAULT_SHOT_KIND_THRESHOLDS): ShotKind {
  if (signals.barcodesInView >= 1) return 'barcode';
  const coverShaped = isCoverShaped(signals, thresholds);
  // A loose cart or disc is framed like a single cover; the hint plus frames with no barcode say which it is.
  if (coverShaped && (signals.mediaHint === 'games' || signals.mediaHint === 'movies') && signals.framesWithoutBarcode >= thresholds.looseItemMinFrames)
    return 'loose_item';
  if (coverShaped || signals.tappedCover) return 'single_cover';
  if (signals.textRows >= thresholds.shelfMinTextRows || signals.rectangleCount >= thresholds.shelfMinRectangles || signals.gateFired)
    return 'shelf_segment';
  return 'unknown';
}

/** The classifier's view of one frame: Vision's text lines count as rows; an unmeasured count falls back to presence. */
export function shotSignalsFromFrame(
  frame: FrameSignals,
  context: { tappedCover: boolean; mediaHint?: MediaHint; framesWithoutBarcode: number; gateFired: boolean },
): ShotSignals {
  return {
    barcodesInView: frame.barcodesInView,
    rectangleCoverage: frame.rectangleCoverage,
    rectangleCount: frame.rectangleCount ?? (frame.rectangleCoverage > 0 ? 1 : 0),
    dominantAspect: frame.dominantAspect ?? null,
    textRows: frame.textBoxes,
    tappedCover: context.tappedCover,
    mediaHint: context.mediaHint,
    framesWithoutBarcode: context.framesWithoutBarcode,
    gateFired: context.gateFired,
  };
}
