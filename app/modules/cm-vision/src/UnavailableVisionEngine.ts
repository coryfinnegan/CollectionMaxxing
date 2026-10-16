import type { DeviceStatus, ModelCapabilities, ReadItemsResult, VisionEngine } from './types';

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
  async modelCapabilities(): Promise<ModelCapabilities> {
    return { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'notSupported' };
  }
  async readItems(): Promise<ReadItemsResult> {
    return { items: [], unreadableCount: 0, outcome: 'unavailable', elapsedMs: 0 };
  }
  async deviceStatus(): Promise<DeviceStatus> {
    return { thermalState: 'nominal', osVersion: 'web' };
  }
}
