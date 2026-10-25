// The phone-only signals, smoke-tested with stand-ins: a failure is silence, never a crash.
import { MotionTracker } from './motion';
import { RealFrameSignals, type RealSignalsDeps } from './RealFrameSignals';
import type { FrameSignals } from './types';

const floats = new Float32Array(64).map((_, i) => (i % 3 === 0 ? 1 : -1));
const print = { data: globalThis.btoa(String.fromCharCode(...new Uint8Array(floats.buffer))) };

const deps = (over: Partial<RealSignalsDeps> = {}): RealSignalsDeps => ({
  snapshot: async () => 'file:///snap.jpg',
  vision: async () => ({
    recognizeText: async () => [{ confidence: 0.9 }, { confidence: 0.7 }],
    detectRectangles: async () => [{ width: 0.5, height: 0.4 }],
    featurePrint: async () => print,
  }),
  motion: async () => () => undefined,
  barcodesInView: () => 0,
  flashOn: () => false,
  intervalMs: 1_000_000,
  ...over,
});

const read = async (d: RealSignalsDeps) => {
  const real = new RealFrameSignals(d);
  const got: FrameSignals[] = [];
  const off = real.onFrameSignals((s) => got.push(s));
  await real.sample();
  off();
  return got;
};

describe('real frame signals', () => {
  it('turn a snapshot into text count, rectangle coverage, sharpness and a hash', async () => {
    const [s] = await read(deps());
    expect(s).toMatchObject({ textBoxes: 2, rectangleCoverage: 0.2, flashOn: false, barcodesInView: 0 });
    expect(s!.sharpness).toBeCloseTo(0.8);
    expect(s!.viewHash).toMatch(/^[0-9a-f]{16}$/);
  });

  it('say nothing when there is no snapshot or Vision fails', async () => {
    expect(await read(deps({ snapshot: async () => null }))).toEqual([]);
    expect(await read(deps({ vision: async () => Promise.reject(new Error('no vision')) }))).toEqual([]);
  });

  it('hands the snapshot back once Vision has read it, even when Vision fails', async () => {
    const released: string[] = [];
    const release = async (u: string) => void released.push(u);
    await read(deps({ release }));
    await read(deps({ release, vision: async () => Promise.reject(new Error('no vision')) }));
    expect(released).toEqual(['file:///snap.jpg', 'file:///snap.jpg']);
  });
});

describe('motion tracker', () => {
  const still = { rotation: { alpha: 0, beta: 0, gamma: 0 }, acceleration: { x: 0, y: 0, z: 0 } };
  it('counts how long the phone has been still, and starts again after a shake', () => {
    const t = new MotionTracker();
    t.push(still, 1000);
    t.push(still, 1500);
    expect(t.steadyForMs(1800)).toBe(800);
    t.push({ ...still, rotation: { alpha: 2, beta: 0, gamma: 0 } }, 1900);
    expect(t.steadyForMs(2000)).toBe(0);
  });

  it('never reads a missing sensor as steady', () => {
    const t = new MotionTracker();
    t.push({ rotation: null, acceleration: null }, 0);
    expect(t.steadyForMs(5000)).toBe(0);
  });
});
