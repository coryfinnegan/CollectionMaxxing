// The seam between the camera and the scan session (docs/05). The session only knows this interface, so the
// same code runs on a real camera, in the dev menu, and in tests. Auto-snap's `capturePhoto` and
// `onFrameSignals` join this interface in CAM-3.
export type Unsubscribe = () => void;

export type SeenCode = { value: string; symbology: string };

export interface ScannerSource {
  /** Starts delivering the codes in each camera frame (every frame, so the stability filter can work). */
  start(onCodes: (codes: SeenCode[]) => void): Unsubscribe;
  setTorch(on: boolean): void;
}

/** A camera that is not there. `emit` hands codes over as if one frame saw them; `inject` plays them as steady reads. */
export class FakeScannerSource implements ScannerSource {
  private readonly listeners = new Set<(codes: SeenCode[]) => void>();
  torch = false;

  start(onCodes: (codes: SeenCode[]) => void): Unsubscribe {
    this.listeners.add(onCodes);
    return () => {
      this.listeners.delete(onCodes);
    };
  }

  setTorch(on: boolean) {
    this.torch = on;
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

/** The real camera's side of the seam. The preview component feeds frames in; the screen toggles the torch. */
export class VisionCameraSource implements ScannerSource {
  private readonly listeners = new Set<(codes: SeenCode[]) => void>();
  private readonly torchListeners = new Set<(on: boolean) => void>();
  torch = false;

  start(onCodes: (codes: SeenCode[]) => void): Unsubscribe {
    this.listeners.add(onCodes);
    return () => {
      this.listeners.delete(onCodes);
    };
  }

  setTorch(on: boolean) {
    this.torch = on;
    for (const listener of this.torchListeners) listener(on);
  }

  /** Called by the camera preview for each frame's codes. */
  feed(codes: SeenCode[]) {
    for (const listener of [...this.listeners]) listener(codes);
  }

  onTorch(listener: (on: boolean) => void): Unsubscribe {
    this.torchListeners.add(listener);
    return () => {
      this.torchListeners.delete(listener);
    };
  }
}
