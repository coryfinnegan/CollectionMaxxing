// The seam between the camera and the scan session (docs/05). The session only knows this interface, so the
// same code runs on a real camera, in the dev menu, and in tests. `capturePhoto` and `onFrameSignals` serve auto-snap
// and the shutter (CAM-3a).
import { FakeFrameSignals, type FrameSignalsFeed } from '../autosnap/FakeFrameSignals';
import { RealFrameSignals } from '../autosnap/RealFrameSignals';
import type { MotionSample } from '../autosnap/motion';
import type { CapturedPhoto } from '../autosnap/snapPhoto';
import type { FrameSignals } from '../autosnap/types';

export type Unsubscribe = () => void;
export type { CapturedPhoto, FrameSignals };

export type SeenCode = { value: string; symbology: string };

export interface ScannerSource {
  /** Starts delivering the codes in each camera frame (every frame, so the stability filter can work). */
  start(onCodes: (codes: SeenCode[]) => void): Unsubscribe;
  setFlash(on: boolean): void;
  /** A full-resolution still, taken by auto-snap or the shutter. It stays on the phone. */
  capturePhoto(): Promise<CapturedPhoto>;
  /** Motion, sharpness, text and rectangle presence, a view hash and barcodes in view, a few times a second. */
  onFrameSignals(cb: (s: FrameSignals) => void): Unsubscribe;
}

/** A camera that is not there. `emit` hands codes over as if one frame saw them; `inject` plays them as steady reads. */
export class FakeScannerSource implements ScannerSource {
  private readonly listeners = new Set<(codes: SeenCode[]) => void>();
  flash = false;
  /** How many pictures were taken. */
  captures = 0;

  constructor(readonly signals: FakeFrameSignals = new FakeFrameSignals()) {}

  async capturePhoto(): Promise<CapturedPhoto> {
    this.captures += 1;
    return { uri: `file:///fake/still-${this.captures}.jpg`, width: 4032, height: 3024, orientation: 1 };
  }

  onFrameSignals(cb: (s: FrameSignals) => void): Unsubscribe {
    return this.signals.onFrameSignals(cb);
  }

  start(onCodes: (codes: SeenCode[]) => void): Unsubscribe {
    this.listeners.add(onCodes);
    return () => {
      this.listeners.delete(onCodes);
    };
  }

  setFlash(on: boolean) {
    this.flash = on;
  }

  /** One camera frame. */
  emit(codes: SeenCode[]) {
    for (const listener of [...this.listeners]) listener(codes);
  }

  /** Codes held steady in view: the same frame twice, which satisfies the stability filter. */
  inject(codes: SeenCode[]) {
    this.emit(codes);
    this.emit(codes);
  }
}

/** The real camera's side of the seam. The preview component feeds frames in; the screen toggles the flash. */
export class VisionCameraSource implements ScannerSource {
  private readonly listeners = new Set<(codes: SeenCode[]) => void>();
  private readonly flashListeners = new Set<(on: boolean) => void>();
  private camera: CameraHandle | undefined;
  private lastCodesAt = 0;
  private lastCodeCount = 0;
  private signals: FrameSignalsFeed | undefined;
  flash = false;

  /** The preview hands over what it can do once the camera is running (and takes it back with `undefined`). */
  attachCamera(handle: CameraHandle | undefined) {
    this.camera = handle;
  }

  async capturePhoto(): Promise<CapturedPhoto> {
    if (!this.camera) throw new Error('The camera is not ready.');
    return this.camera.capture();
  }

  onFrameSignals(cb: (s: FrameSignals) => void): Unsubscribe {
    this.signals ??= new RealFrameSignals({
      snapshot: async () => (await this.camera?.snapshot()) ?? null,
      release: async (uri) => this.camera?.releaseSnapshot(uri),
      vision: async () => (await import('@/native/cm-vision')).visionEngine,
      motion: startDeviceMotion,
      barcodesInView: () => (Date.now() - this.lastCodesAt < BARCODE_MEMORY_MS ? this.lastCodeCount : 0),
      flashOn: () => this.flash,
    });
    return this.signals.onFrameSignals(cb);
  }

  start(onCodes: (codes: SeenCode[]) => void): Unsubscribe {
    this.listeners.add(onCodes);
    return () => {
      this.listeners.delete(onCodes);
    };
  }

  setFlash(on: boolean) {
    this.flash = on;
    for (const listener of this.flashListeners) listener(on);
  }

  /** Called by the camera preview for each frame's codes. */
  feed(codes: SeenCode[]) {
    this.lastCodesAt = Date.now();
    this.lastCodeCount = codes.length;
    for (const listener of [...this.listeners]) listener(codes);
  }

  onFlash(listener: (on: boolean) => void): Unsubscribe {
    this.flashListeners.add(listener);
    return () => {
      this.flashListeners.delete(listener);
    };
  }
}

/** What the live preview can do for auto-snap and the shutter. */
export interface CameraHandle {
  capture(): Promise<CapturedPhoto>;
  /** A cheap, low-resolution look at the camera now (file uri), or null. */
  snapshot(): Promise<string | null>;
  /** Deletes a snapshot once it has been read. */
  releaseSnapshot(uri: string): Promise<void>;
}

/** A barcode seen this recently still counts as in view (the decoder reports only frames that have one). */
const BARCODE_MEMORY_MS = 600;

async function startDeviceMotion(onSample: (s: MotionSample) => void): Promise<Unsubscribe> {
  const { DeviceMotion } = await import('expo-sensors');
  DeviceMotion.setUpdateInterval(100);
  const subscription = DeviceMotion.addListener((m) => onSample({ rotation: m.rotationRate ?? null, acceleration: m.acceleration ?? null }));
  return () => subscription.remove();
}
