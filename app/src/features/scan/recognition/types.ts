// The shapes the recognition engines share. Nothing here carries picture data: only text and fields.
import type { ItemCompleteness, ReleaseFormat, WorkKind } from '@/api/generated/models';
import type { ModelConfidence, Rect, TextLine } from '@/native/cm-vision';
import type { ShotKind } from '../shotkind/shotKind';

/** A still the camera took. `uri` stays on the phone and is only ever handed to the native module. */
export interface Still {
  uri: string;
  width: number;
  height: number;
}

/** What one item looks like after either engine has read it. The model is a hint: nothing here says "confirmed". */
export interface ReadItem {
  localId: string;
  /** The model's visibleText hint plus the Vision lines that matched it. */
  text: string;
  title: string;
  creator?: string;
  kind: WorkKind;
  format?: ReleaseFormat;
  platform?: string;
  edition?: string;
  completeness?: ItemCompleteness;
  /** A game's product code printed on its label (`SNS-8C-USA`, `SLUS-00594`), as read from Vision's text. The server names the game from it. */
  productCode?: string;
  /** Normalized 0..1 on the still, origin top-left. Absent when no Vision text matched. */
  box?: Rect;
  order: number;
  confidence: ModelConfidence;
  /**
   * Set on a shelf shot's item that only the picture model read, at a place where the Vision pipeline found nothing (`addableItems`). The model can read
   * a title that happens to exist, so the server matches such an item but keeps it for review (tier T2 at most) instead of adding it. An item Vision
   * read, or that the model read where Vision did, is never unconfirmed; neither is any item of a single cover or of a model-only read.
   */
  unconfirmed?: boolean;
}

export type EngineName = 'apple_model' | 'vision_pipeline' | 'fm_text';
export type FellBackFrom = 'unavailable' | 'declined' | 'timeout' | 'contextExceeded';

export interface SnapTimings {
  totalMs: number;
  /** Time in the language model; absent for the Vision text engine. */
  modelMs?: number;
  /** Time in Vision text and rectangle reads. */
  visionMs: number;
}

/** What the Vision text engine saw and how it split it, kept for the eval record. The scanner never sends it anywhere. */
export interface OcrReading {
  /** The cleaned lines the engine split, as it split them (box normalized 0..1, origin top-left). */
  lines: TextLine[];
  /** For each piece the engine made, the indexes of its lines in `lines`. */
  pieces: number[][];
}

export interface SnapReading {
  items: ReadItem[];
  /** The Vision text engine's lines and pieces; only its own reading has them. */
  ocr?: OcrReading;
  /**
   * What the cover cue saw on a single-item shot, as one short line of text (`describeCue`, coverCue.ts): the rectangles found and why each was
   * left out, the format, kind and product code it gave, and whether it came in time. For the eval record only; the scanner ignores it.
   */
  coverCue?: string;
  /** Items that were there but could not be read; the scanner shows its "Get closer" chip when above 0. */
  unreadableCount: number;
  engine: EngineName;
  fellBackFrom?: FellBackFrom;
  timings: SnapTimings;
}

/** What the reader knows about the shot beyond the still itself. */
export interface ReadOptions {
  /** What the framing said this shot was; absent on re-reads of snaps stored before classification. */
  shotKind?: ShotKind;
  /**
   * A shelf shot is read by the Vision pipeline first, and the picture model adds the items it found where Vision found nothing.
   * `false` reads with the model alone; the eval runner does, because it reads and scores the Vision pipeline as its own engine.
   */
  combine?: boolean;
}

export interface RecognitionEngine {
  readonly name: EngineName;
  isAvailable(): Promise<boolean>;
  read(still: Still, onItem: (item: ReadItem) => void, options?: ReadOptions): Promise<SnapReading>;
}

/** Cuts a region (normalized, origin top-left) out of a still and returns the new still. Only the halves of a wide shot use it. */
export interface StillCropper {
  crop(still: Still, region: Rect): Promise<Still>;
}

/** Thrown by the model engines when the model gave no usable reading; the selector reads that snap with the next tier down. */
export class ModelUnusableError extends Error {
  constructor(readonly reason: FellBackFrom) {
    super(`The on-device model gave no reading (${reason}).`);
    this.name = 'ModelUnusableError';
  }
}
