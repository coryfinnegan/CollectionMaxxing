// Web entry: no native module, no camera. Never import './index' or anything native from here.
import { UnavailableVisionEngine } from './UnavailableVisionEngine';

export * from './types';
export { FakeBarcodeSource } from './FakeBarcodeSource';
export { FakeVisionEngine } from './FakeVisionEngine';
export { UnavailableVisionEngine } from './UnavailableVisionEngine';

export const visionEngine = new UnavailableVisionEngine();

export function useBarcodeFrameProcessor(): never {
  throw new Error('Barcode scanning is not available on the web.');
}
