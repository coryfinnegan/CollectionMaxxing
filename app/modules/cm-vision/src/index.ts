// iOS entry. The web build resolves index.web.ts instead and never bundles this file.
import { requireNativeModule } from 'expo';
import { useCallback, useRef } from 'react';
import { useObjectOutput } from 'react-native-vision-camera';

import { mapScannedObjects } from './mapScannedObjects';
import type { DetectedCode, FeaturePrint, Rect, TextLine, VisionEngine } from './types';

export * from './types';
export { FakeBarcodeSource } from './FakeBarcodeSource';
export { FakeVisionEngine } from './FakeVisionEngine';
export { UnavailableVisionEngine } from './UnavailableVisionEngine';

/** What ios/CmVisionModule.swift exposes; result keys are pinned by contract.schema.json. */
interface NativeCmVision {
  isAvailable(): Promise<boolean>;
  recognizeText(imageUri: string): Promise<TextLine[]>;
  detectRectangles(imageUri: string): Promise<Rect[]>;
  detectBarcodes(imageUri: string): Promise<DetectedCode[]>;
  featurePrint(imageUri: string, cropBox: Rect | null): Promise<FeaturePrint>;
  featurePrintDistance(a: FeaturePrint, b: FeaturePrint): Promise<number>;
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
  recognizeText(imageUri: string) {
    return this.native().recognizeText(imageUri);
  }
  detectRectangles(imageUri: string) {
    return this.native().detectRectangles(imageUri);
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
