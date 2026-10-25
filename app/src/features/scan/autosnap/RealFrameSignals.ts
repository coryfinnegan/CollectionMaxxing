// The phone's side of FrameSignals: motion from expo-sensors, and a low-rate snapshot read by Apple Vision (through
// cm-vision) for text, rectangles, sharpness and a view hash. It can only be exercised on a phone, so it is thin and
// defensive: any failure means "no signal this time" (nothing is emitted), never a crash and never a snap.
import { hashFromFeaturePrint } from './hash';
import { MotionTracker, type MotionSample } from './motion';
import type { FrameSignalsFeed, SignalsUnsubscribe } from './FakeFrameSignals';
import type { FrameSignals } from './types';

interface VisionReads {
  recognizeText(uri: string): Promise<{ confidence: number }[]>;
  detectRectangles(uri: string): Promise<{ width: number; height: number }[]>;
  featurePrint(uri: string): Promise<{ data: string }>;
}

export interface RealSignalsDeps {
  /** A low-resolution snapshot of the camera right now, as a file uri; null when there is none. */
  snapshot(): Promise<string | null>;
  /** Called with the snapshot's uri once Vision has read it (or failed to), so the file can be deleted. */
  release?(uri: string): Promise<void>;
  vision(): Promise<VisionReads>;
  /** Starts motion readings; returns a stop function. */
  motion(onSample: (s: MotionSample) => void): Promise<SignalsUnsubscribe>;
  barcodesInView(): number;
  flashOn(): boolean;
  now?(): number;
  intervalMs?: number;
}

const mean = (xs: number[]) => (xs.length === 0 ? 0 : xs.reduce((a, b) => a + b, 0) / xs.length);

export class RealFrameSignals implements FrameSignalsFeed {
  private readonly listeners = new Set<(s: FrameSignals) => void>();
  private readonly tracker = new MotionTracker();
  private timer: ReturnType<typeof setInterval> | undefined;
  private stopMotion: SignalsUnsubscribe | undefined;
  private running = false;
  private busy = false;

  constructor(private readonly deps: RealSignalsDeps) {}

  onFrameSignals(cb: (s: FrameSignals) => void): SignalsUnsubscribe {
    this.listeners.add(cb);
    if (this.listeners.size === 1) this.start();
    return () => {
      this.listeners.delete(cb);
      if (this.listeners.size === 0) this.stop();
    };
  }

  private now() {
    return this.deps.now ? this.deps.now() : Date.now();
  }

  private start() {
    this.running = true;
    this.tracker.reset();
    this.deps
      .motion((s) => this.tracker.push(s, this.now()))
      .then((stop) => {
        if (this.running) this.stopMotion = stop;
        else stop();
      })
      .catch(() => undefined); // no motion sensor: the phone never reads as steady, so nothing auto-snaps
    this.timer = setInterval(() => void this.sample(), this.deps.intervalMs ?? 500);
  }

  private stop() {
    this.running = false;
    if (this.timer) clearInterval(this.timer);
    this.timer = undefined;
    this.stopMotion?.();
    this.stopMotion = undefined;
  }

  /** One reading of everything. Overlapping readings are skipped; any failure is silence. */
  async sample(): Promise<void> {
    if (this.busy || !this.running) return;
    this.busy = true;
    try {
      const uri = await this.deps.snapshot();
      if (!uri) return;
      let lines, rectangles, print;
      try {
        const vision = await this.deps.vision();
        [lines, rectangles, print] = await Promise.all([vision.recognizeText(uri), vision.detectRectangles(uri), vision.featurePrint(uri)]);
      } finally {
        // The snapshot is deleted as soon as it has been read (it never leaves the phone).
        await this.deps.release?.(uri)?.catch(() => undefined);
      }
      const viewHash = hashFromFeaturePrint(print.data);
      if (!viewHash) return;
      const signals: FrameSignals = {
        steadyForMs: this.tracker.steadyForMs(this.now()),
        // Vision has no sharpness score; the average confidence of the text it read is a stand-in that drops with blur.
        sharpness: lines.length === 0 ? 0 : mean(lines.map((l) => l.confidence)),
        textBoxes: lines.length,
        rectangleCoverage: rectangles.reduce((max, r) => Math.max(max, r.width * r.height), 0),
        viewHash,
        flashOn: this.deps.flashOn(),
        barcodesInView: this.deps.barcodesInView(),
      };
      for (const listener of [...this.listeners]) listener(signals);
    } catch {
      // no signal this time
    } finally {
      this.busy = false;
    }
  }
}
