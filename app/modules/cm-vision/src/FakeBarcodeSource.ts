import { fixtures, type FixtureSet } from './fixtures';
import type { BarcodeSource, DetectedCode, Unsubscribe } from './types';

/**
 * A camera that isn't there. `emit` injects codes by hand (the dev menu: "scan these 6 codes");
 * `replay` plays a recorded scene, one camera callback per batch.
 */
export class FakeBarcodeSource implements BarcodeSource {
  private readonly listeners = new Set<(codes: DetectedCode[]) => void>();

  constructor(private readonly set: Pick<FixtureSet, 'barcodeScenes'> = fixtures) {}

  start(onCodes: (codes: DetectedCode[]) => void): Unsubscribe {
    this.listeners.add(onCodes);
    return () => {
      this.listeners.delete(onCodes);
    };
  }

  emit(codes: DetectedCode[]): void {
    for (const listener of [...this.listeners]) listener(codes);
  }

  replay(scene: string): void {
    const record = this.set.barcodeScenes.find((r) => r.input.scene === scene);
    if (!record) throw new Error(`FakeBarcodeSource: no barcode scene fixture for scene "${scene}"`);
    for (const batch of record.output) this.emit(batch);
  }
}
