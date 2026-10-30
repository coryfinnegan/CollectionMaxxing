/** Normalised 0..1, origin top-left. */
export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface TextLine {
  text: string;
  confidence: number;
  box: Rect;
}

export interface FeaturePrint {
  revision: number;
  /** base64 */
  data: string;
}

export const SYMBOLOGIES = ['ean-13', 'ean-8', 'upc-e', 'qr', 'code-128'] as const;
export type Symbology = (typeof SYMBOLOGIES)[number];

export interface DetectedCode {
  value: string;
  symbology: Symbology;
  bounds: Rect;
}

export type Unsubscribe = () => void;

/** Degrees the picture is turned clockwise for one text pass: 90 reads lines that run top to bottom, 270 bottom to top, 180 upside down. */
export type TextOrientation = 0 | 90 | 180 | 270;

/**
 * What a rectangle search is asked for. Left out entirely, the request is the shelf splitter's own: upright shapes, at least a tenth
 * of the picture's shorter side. A caller that wants something else names only what it changes.
 */
export interface DetectRectanglesOptions {
  /**
   * Short side over long side of the tallest shape to return, 0..1. Vision's own limit is 0.5 (twice as tall as wide), which
   * never returns a DVD case (about 0.71) or a Blu-ray case (about 0.86): a caller looking for a case asks for 1.
   */
  maximumAspectRatio?: number;
  /** The least size of a rectangle as a share of the picture's shorter side, 0..1. */
  minimumSize?: number;
}

/**
 * Apple Vision on the phone. Native code only calls Vision and shapes results; everything
 * else (splitting, scoring, the prompt text) is TypeScript.
 *
 * Model reading (`readItems`) is implemented in Swift by NAT-2b with Foundation Models image input
 * (`Attachment(cgImage, orientation:)`, guarded by `SystemLanguageModel.default.capabilities.contains(.vision)`).
 * Text extraction (`extractItems`, spike #505) sends no picture: Vision OCR text goes to the same on-device
 * model as text, so it works on iOS 26 where the model cannot see pictures yet.
 */
export interface VisionEngine {
  isAvailable(): Promise<boolean>;
  /**
   * One pass per orientation (default `[0]`), lines concatenated in the order of `orientations`.
   * Lines are not de-duplicated across orientations; the caller decides.
   */
  recognizeText(imageUri: string, options?: { orientations?: TextOrientation[] }): Promise<TextLine[]>;
  detectRectangles(imageUri: string, options?: DetectRectanglesOptions): Promise<Rect[]>;
  /** Barcodes in a still picture (Photos import), through VNDetectBarcodesRequest. */
  detectBarcodes(imageUri: string): Promise<DetectedCode[]>;
  /** `cropBox` is normalised, origin top-left. */
  featurePrint(imageUri: string, cropBox?: Rect): Promise<FeaturePrint>;
  /** Symmetric; 0 for identical prints. */
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number>;
  /** What the on-device language model can do right now. Never throws. */
  modelCapabilities(): Promise<ModelCapabilities>;
  /**
   * Asks the on-device model to read the items in a picture, using `options.prompt` (the prompt is
   * an input: no prompt text lives in Swift). Each item is streamed to `onItem` in `order` before the
   * promise resolves. A declined, timed-out or oversized read resolves with an `outcome`; it never throws.
   */
  readItems(imageUri: string, options: ReadItemsOptions, onItem?: (item: ModelItem) => void): Promise<ReadItemsResult>;
  /**
   * Asks the on-device model to extract typed items from Vision OCR text (no picture crosses this call).
   * Same options, same streamed items, same result shape as `readItems`; needs only
   * `modelCapabilities().guidedGeneration`, so iOS 26 phones can use it. Never throws.
   */
  extractItems(ocrText: string, options: ReadItemsOptions, onItem?: (item: ModelItem) => void): Promise<ReadItemsResult>;
  deviceStatus(): Promise<DeviceStatus>;
}

export type ModelKind = 'book' | 'vhs' | 'dvd' | 'bluray' | 'uhdBluray' | 'game' | 'other';
export type ModelCompleteness = 'looseCart' | 'discOnly' | 'boxed' | 'manualVisible';
export type ModelConfidence = 'high' | 'medium' | 'low';

/**
 * One item the model read. Names mirror docs/02's `ScannedItem`; mapping to the server's enums is NAT-2c.
 * `visibleText` is a hint for matching, not verbatim truth: the model can complete words that are cut off
 * at an edge of the picture.
 */
export interface ModelItem {
  visibleText: string;
  title: string;
  creator?: string;
  kind: ModelKind;
  platform?: string;
  editionMarks?: string;
  completeness?: ModelCompleteness;
  order: number;
  confidence: ModelConfidence;
}

export type ModelUnavailableReason = 'osTooOld' | 'notSupported' | 'appleIntelligenceOff' | 'modelNotReady';

export interface ModelCapabilities {
  available: boolean;
  vision: boolean;
  guidedGeneration: boolean;
  contextSize: number | null;
  reason?: ModelUnavailableReason;
}

export interface ReadItemsOptions {
  prompt: string;
  timeoutMs: number;
  maxItems?: number;
}

export type ReadOutcome = 'ok' | 'declined' | 'timeout' | 'contextExceeded' | 'unavailable';

export interface ReadItemsResult {
  items: ModelItem[];
  unreadableCount: number;
  outcome: ReadOutcome;
  elapsedMs: number;
}

export type ThermalState = 'nominal' | 'fair' | 'serious' | 'critical';

export interface DeviceStatus {
  thermalState: ThermalState;
  osVersion: string;
}

/** Live camera codes. The real one is `useBarcodeFrameProcessor`; tests use `FakeBarcodeSource`. */
export interface BarcodeSource {
  start(onCodes: (codes: DetectedCode[]) => void): Unsubscribe;
}

/** The shape of a fixture file; a dev build will later record these from a real phone. */
export interface FixtureRecord<I, O> {
  capturedAt: string;
  engine: string;
  input: I;
  output: O;
}
