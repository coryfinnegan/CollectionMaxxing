// A stand-in for the camera's signals, driven by tests and the dev menu.
import type { FrameSignals } from './types';

export type SignalsUnsubscribe = () => void;

/** Anything that can deliver FrameSignals: the real sensors, or this fake. */
export interface FrameSignalsFeed {
  onFrameSignals(cb: (s: FrameSignals) => void): SignalsUnsubscribe;
}

/** A sharp, steady view with text and no barcode: what a cart, a sleeve or a shelf looks like to the machine. */
export const STEADY_TEXT_VIEW: FrameSignals = {
  steadyForMs: 1000,
  sharpness: 0.9,
  textBoxes: 6,
  rectangleCoverage: 0,
  viewHash: '0123456789abcdef',
  torchOn: false,
  barcodesInView: 0,
};

export class FakeFrameSignals implements FrameSignalsFeed {
  private readonly listeners = new Set<(s: FrameSignals) => void>();

  onFrameSignals(cb: (s: FrameSignals) => void): SignalsUnsubscribe {
    this.listeners.add(cb);
    return () => {
      this.listeners.delete(cb);
    };
  }

  /** One reading. */
  emit(signals: FrameSignals) {
    for (const listener of [...this.listeners]) listener(signals);
  }
}
