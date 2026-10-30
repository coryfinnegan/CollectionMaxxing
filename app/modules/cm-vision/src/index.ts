// iOS entry. The web build resolves index.web.ts instead and never bundles this file.
import { requireNativeModule } from 'expo';
import { useCallback, useRef } from 'react';
import { useObjectOutput } from 'react-native-vision-camera';

import { mapScannedObjects } from './mapScannedObjects';
import type {
  DetectedCode,
  DetectRectanglesOptions,
  DeviceStatus,
  FeaturePrint,
  ModelCapabilities,
  ModelItem,
  ReadItemsOptions,
  ReadItemsResult,
  Rect,
  TextLine,
  VisionEngine,
} from './types';

export * from './types';
export { FakeBarcodeSource } from './FakeBarcodeSource';
export { FakeVisionEngine } from './FakeVisionEngine';
export { UnavailableVisionEngine } from './UnavailableVisionEngine';

/** What ios/CmVisionModule.swift exposes; result keys are pinned by contract.schema.json. */
interface NativeCmVision {
  isAvailable(): Promise<boolean>;
  recognizeText(imageUri: string, orientations: number[]): Promise<TextLine[]>;
  detectRectangles(imageUri: string, options: DetectRectanglesOptions | null): Promise<Rect[]>;
  detectBarcodes(imageUri: string): Promise<DetectedCode[]>;
  featurePrint(imageUri: string, cropBox: Rect | null): Promise<FeaturePrint>;
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number>;
  modelCapabilities(): Promise<ModelCapabilities>;
  /** Emits `onItem` events (payload: a ModelItem) while running, then resolves. */
  readItems(imageUri: string, options: ReadItemsOptions): Promise<ReadItemsResult>;
  /** Same events and result shape as `readItems`, from OCR text instead of a picture. */
  extractItems(ocrText: string, options: ReadItemsOptions): Promise<ReadItemsResult>;
  deviceStatus(): Promise<DeviceStatus>;
  addListener(event: 'onItem', listener: (item: ModelItem) => void): { remove(): void };
}

class NativeVisionEngine implements VisionEngine {
  private module: NativeCmVision | undefined;

  private native(): NativeCmVision {
    this.module ??= requireNativeModule<NativeCmVision>('CmVision');
    return this.module;
  }

  async isAvailable(): Promise<boolean> {
    try {
      return await this.native().isAvailable();
    } catch {
      return false; // e.g. Expo Go, which does not contain the module
    }
  }
  recognizeText(imageUri: string, options?: { orientations?: (0 | 90 | 270)[] }) {
    return this.native().recognizeText(imageUri, options?.orientations ?? [0]);
  }
  detectRectangles(imageUri: string, options?: DetectRectanglesOptions) {
    return this.native().detectRectangles(imageUri, options ?? null);
  }
  detectBarcodes(imageUri: string) {
    return this.native().detectBarcodes(imageUri);
  }
  featurePrint(imageUri: string, cropBox?: Rect) {
    return this.native().featurePrint(imageUri, cropBox ?? null);
  }
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint) {
    return this.native().featurePrintDistance(a, b);
  }
  async modelCapabilities(): Promise<ModelCapabilities> {
    try {
      return await this.native().modelCapabilities();
    } catch {
      return { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'notSupported' };
    }
  }
  // onItem events carry no call id, so native model calls must not overlap: each call waits for the one before it.
  private readQueue: Promise<unknown> = Promise.resolve();

  readItems(imageUri: string, options: ReadItemsOptions, onItem?: (item: ModelItem) => void): Promise<ReadItemsResult> {
    const run = this.readQueue.then(() => this.readOne((native) => native.readItems(imageUri, options), onItem));
    this.readQueue = run; // readOne never rejects, so a failed call cannot block the next
    return run;
  }

  extractItems(ocrText: string, options: ReadItemsOptions, onItem?: (item: ModelItem) => void): Promise<ReadItemsResult> {
    const run = this.readQueue.then(() => this.readOne((native) => native.extractItems(ocrText, options), onItem));
    this.readQueue = run;
    return run;
  }

  private async readOne(
    call: (native: NativeCmVision) => Promise<ReadItemsResult>,
    onItem?: (item: ModelItem) => void,
  ): Promise<ReadItemsResult> {
    let subscription: { remove(): void } | undefined;
    try {
      const native = this.native();
      subscription = onItem ? native.addListener('onItem', onItem) : undefined;
      return await call(native);
    } catch {
      return { items: [], unreadableCount: 0, outcome: 'unavailable', elapsedMs: 0 };
    } finally {
      subscription?.remove();
    }
  }
  deviceStatus() {
    return this.native().deviceStatus();
  }
}

export const visionEngine: VisionEngine = new NativeVisionEngine();

const LIVE_TYPES = ['ean-13', 'ean-8', 'upc-e', 'qr', 'code-128'] as ('ean-13' | 'ean-8' | 'upc-e' | 'qr' | 'code-128')[];

/**
 * Live barcodes on camera frames. Pass the result in `<Camera outputs={[...]} />`.
 *
 * It uses VisionCamera's object output, which is AVFoundation's metadata output: Apple's own
 * decoder, run by the capture pipeline at the camera's rate, so there is no per-frame
 * JavaScript, no worklet and no allocation beyond the result. (A Vision `VNDetectBarcodesRequest`
 * per frame would need a custom Nitro frame plugin; `visionEngine.detectBarcodes` uses
 * VNDetectBarcodesRequest for still pictures.)
 */
export function useBarcodeFrameProcessor(onCodes: (codes: DetectedCode[]) => void) {
  const latest = useRef(onCodes);
  latest.current = onCodes;
  const onObjectsScanned = useCallback((objects: Parameters<typeof mapScannedObjects>[0]) => {
    const codes = mapScannedObjects(objects);
    if (codes.length > 0) latest.current(codes);
  }, []);
  return useObjectOutput({ types: LIVE_TYPES, onObjectsScanned });
}
