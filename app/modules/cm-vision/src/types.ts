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
 * else (splitting, scoring) is TypeScript. No model reading here: that is NAT-2.
 *
 * NAT-2 seam (no code yet): Foundation Models image input uses `Attachment(cgImage, orientation:)`
 * inside the prompt builder, guarded by `SystemLanguageModel.default.capabilities.contains(.vision)`.
 */
export interface VisionEngine {
  isAvailable(): Promise<boolean>;
  recognizeText(imageUri: string): Promise<TextLine[]>;
  detectRectangles(imageUri: string): Promise<Rect[]>;
  /** Barcodes in a still picture (Photos import), through VNDetectBarcodesRequest. */
  detectBarcodes(imageUri: string): Promise<DetectedCode[]>;
  /** `cropBox` is normalised, origin top-left. */
  featurePrint(imageUri: string, cropBox?: Rect): Promise<FeaturePrint>;
  /** Symmetric; 0 for identical prints. */
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number>;
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
