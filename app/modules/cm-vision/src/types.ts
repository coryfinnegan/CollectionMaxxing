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

/**
 * Apple Vision on the phone. Native code only calls Vision and shapes results; everything
 * else (splitting, scoring, the prompt text) is TypeScript.
 *
 * Model reading (`readItems`) is implemented in Swift by NAT-2b with Foundation Models image input
 * (`Attachment(cgImage, orientation:)`, guarded by `SystemLanguageModel.default.capabilities.contains(.vision)`).
 */
export interface VisionEngine {
  isAvailable(): Promise<boolean>;
  /**
   * One pass per orientation (default `[0]`), lines concatenated in the order of `orientations`.
   * Lines are not de-duplicated across orientations; the caller decides.
   */
  recognizeText(imageUri: string, options?: { orientations?: (0 | 90 | 270)[] }): Promise<TextLine[]>;
  detectRectangles(imageUri: string): Promise<Rect[]>;
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
