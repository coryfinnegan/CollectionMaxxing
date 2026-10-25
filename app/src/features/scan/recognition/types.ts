// The shapes the recognition engines share. Nothing here carries picture data: only text and fields.
import type { ItemCompleteness, ReleaseFormat, WorkKind } from '@/api/generated/models';
import type { ModelConfidence, Rect } from '@/native/cm-vision';

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
  /** Normalized 0..1 on the still, origin top-left. Absent when no Vision text matched. */
  box?: Rect;
  order: number;
  confidence: ModelConfidence;
}

export type EngineName = 'apple_model' | 'vision_pipeline';
export type FellBackFrom = 'unavailable' | 'declined' | 'timeout' | 'contextExceeded';

export interface SnapTimings {
  totalMs: number;
  /** Time in the language model; absent for the Vision text engine. */
  modelMs?: number;
  /** Time in Vision text and rectangle reads. */
  visionMs: number;
}

export interface SnapReading {
  items: ReadItem[];
  /** Items that were there but could not be read; the scanner shows its "Get closer" chip when above 0. */
  unreadableCount: number;
  engine: EngineName;
  fellBackFrom?: FellBackFrom;
  timings: SnapTimings;
}

export interface RecognitionEngine {
  readonly name: EngineName;
  isAvailable(): Promise<boolean>;
  read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading>;
}

/** Cuts a region (normalized, origin top-left) out of a still and returns the new still. Only the halves of a wide shot use it. */
export interface StillCropper {
  crop(still: Still, region: Rect): Promise<Still>;
}

/** Thrown by `AppleModelEngine` when the model gave no usable reading; the selector reads that snap with Vision text. */
export class ModelUnusableError extends Error {
  constructor(readonly reason: FellBackFrom) {
    super(`The on-device model gave no reading (${reason}).`);
    this.name = 'ModelUnusableError';
  }
}
