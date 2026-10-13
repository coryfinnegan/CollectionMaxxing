import type { VisionEngine } from './types';

const message = 'Apple Vision is not available on the web. Recognition runs on the iPhone app only.';

/** Web has no recognition (ADR-017); every call says so. */
export class UnavailableVisionEngine implements VisionEngine {
  async isAvailable(): Promise<boolean> {
    return false;
  }
  async recognizeText(): Promise<never> {
    throw new Error(message);
  }
  async detectRectangles(): Promise<never> {
    throw new Error(message);
  }
  async detectBarcodes(): Promise<never> {
    throw new Error(message);
  }
  async featurePrint(): Promise<never> {
    throw new Error(message);
  }
  async featurePrintDistance(): Promise<never> {
    throw new Error(message);
  }
}
