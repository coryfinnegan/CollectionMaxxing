// The auto-snap machine against scripted signal timelines. No camera, no clock: time is a number we pass in.
import { DEFAULT_AUTO_SNAP_CONFIG as config } from './defaults';
import { hashDistance, hashFromFeaturePrint } from './hash';
import { initialState, recordOutcome, shutter, step, type MachineState } from './machine';
import type { FrameSignals } from './types';

const HASH_A = '0123456789abcdef';
const HASH_B = 'fedcba9876543210';

const view = (over: Partial<FrameSignals> = {}): FrameSignals => ({
  steadyForMs: 0,
  sharpness: 0.9,
  textBoxes: 5,
  rectangleCoverage: 0,
  viewHash: HASH_A,
  flashOn: false,
  barcodesInView: 0,
  ...over,
});

/** Plays signals at 100 ms apart; returns how many snapped and where the machine ended up. */
const play = (signals: FrameSignals[], from: MachineState = initialState(), startMs = 0) => {
  let state = from;
  let snaps = 0;
  signals.forEach((s, i) => {
    const out = step(state, s, startMs + i * 100, config);
    state = out.state;
    if (out.snap) snaps++;
  });
  return { state, snaps };
};

/** Holding still: steadyForMs grows with the clock. */
const holding = (seconds: number, over: Partial<FrameSignals> = {}) =>
  Array.from({ length: Math.round(seconds * 10) + 1 }, (_, i) => view({ steadyForMs: i * 100, ...over }));

describe('auto-snap machine', () => {
  it('snaps once when the phone is steady for 0.7 s on a view with text', () => {
    expect(play(holding(0.7)).snaps).toBe(1);
  });

  it('does not snap at 0.6 s steady', () => {
    const run = play(holding(0.6));
    expect(run.snaps).toBe(0);
    expect(run.state.phase).toBe('candidate');
  });

  it('does not snap a blurry view', () => {
    expect(play(holding(2, { sharpness: 0.1 })).snaps).toBe(0);
  });

  it('does not snap a view with no text and no rectangle', () => {
    expect(play(holding(2, { textBoxes: 0, rectangleCoverage: 0 })).snaps).toBe(0);
  });

  it('snaps a big rectangle even with no text', () => {
    expect(play(holding(1, { textBoxes: 0, rectangleCoverage: 0.6 })).snaps).toBe(1);
  });

  it('does not snap while moving, then snaps once the phone settles', () => {
    const moving = Array.from({ length: 20 }, () => view({ steadyForMs: 100 }));
    expect(play(moving).snaps).toBe(0);
    expect(play([...moving, ...holding(0.7)]).snaps).toBe(1);
  });

  it('reads every threshold from the injected config', () => {
    let state = initialState();
    let snaps = 0;
    holding(0.7).forEach((s, i) => {
      const out = step(state, s, i * 100, { ...config, steadyMs: 5000 });
      state = out.state;
      if (out.snap) snaps++;
    });
    expect(snaps).toBe(0);
  });

  it('never snaps the same view again, and snaps a changed view once the cooldown has passed', () => {
    const first = play(holding(0.7));
    expect(first.snaps).toBe(1);
    expect(first.state.phase).toBe('cooldown');
    const same = play(holding(3), first.state, 800);
    expect(same.snaps).toBe(0);
    const changed = play(holding(0.7, { viewHash: HASH_B }), first.state, 2_000);
    expect(changed.snaps).toBe(1);
  });

  it('keeps the cooldown a moment even when the view changes at once', () => {
    const first = play(holding(0.7));
    const quick = step(first.state, view({ steadyForMs: 800, viewHash: HASH_B }), first.state.lastSnapAt! + 100, config);
    expect(quick.state.phase).toBe('cooldown');
  });

  it('raises the thresholds after three misfires in a row, so a borderline view no longer snaps', () => {
    const borderline = holding(0.7, { sharpness: 0.52 });
    expect(play(borderline).snaps).toBe(1);

    let raised = initialState();
    for (let i = 0; i < config.misfireStreak; i++) raised = recordOutcome(raised, false, config);
    expect(raised.raiseSteps).toBe(1);
    expect(play(borderline, raised).snaps).toBe(0);
  });

  it('two misfires are not enough, and a find starts the count again', () => {
    let state = recordOutcome(recordOutcome(initialState(), false, config), false, config);
    expect(state.raiseSteps).toBe(0);
    state = recordOutcome(state, true, config);
    state = recordOutcome(recordOutcome(state, false, config), false, config);
    expect(state.raiseSteps).toBe(0);
  });

  it('never raises the thresholds past the cap', () => {
    let state = initialState();
    for (let i = 0; i < 50; i++) state = recordOutcome(state, false, config);
    expect(state.raiseSteps).toBe(config.maxRaiseSteps);
  });

  it('the shutter snaps at once whatever state the machine is in, and the same view then waits for a change', () => {
    const afterShutter = shutter(initialState(), view({ barcodesInView: 2, sharpness: 0 }), 1_000);
    expect(afterShutter.phase).toBe('cooldown');
    expect(afterShutter.lastSnapHash).toBe(HASH_A);
    expect(play(holding(3), afterShutter, 1_100).snaps).toBe(0);
  });

  describe('hashes', () => {
    it('measure how far apart two views are', () => {
      expect(hashDistance(HASH_A, HASH_A)).toBe(0);
      expect(hashDistance(HASH_A, HASH_B)).toBeGreaterThan(0.5);
      expect(hashDistance('00', '0000')).toBe(1);
    });

    it('come from a feature print, and give up quietly on junk', () => {
      const floats = new Float32Array(64).map((_, i) => (i % 2 === 0 ? 1 : -1));
      const base64 = globalThis.btoa(String.fromCharCode(...new Uint8Array(floats.buffer)));
      expect(hashFromFeaturePrint(base64)).toMatch(/^[0-9a-f]{16}$/);
      expect(hashFromFeaturePrint('not base64 !!')).toBeNull();
      expect(hashFromFeaturePrint('AAAA')).toBeNull();
    });
  });
});

// LOCKED acceptance tests (CAM-3a, issue #117). Do not weaken, skip or delete.
describe('LOCKED: a barcode in view always wins', () => {
  it('never snaps while a barcode is in view, however steady and sharp the view', () => {
    expect(play(holding(10, { barcodesInView: 1 })).snaps).toBe(0);
  });

  it('resets a pending candidate to idle the moment a barcode appears', () => {
    const building = play(holding(0.5));
    expect(building.state.phase).toBe('candidate');
    const out = step(building.state, view({ steadyForMs: 600, barcodesInView: 1 }), 600, config);
    expect(out.state.phase).toBe('idle');
    expect(out.snap).toBe(false);
  });

  it('leaves the shutter as the only way to take a picture meanwhile', () => {
    const state = shutter(initialState(), view({ barcodesInView: 1 }), 0);
    expect(state.phase).toBe('cooldown');
    expect(state.lastSnapAt).toBe(0);
  });
});

describe('LOCKED: never the same view twice', () => {
  it('produces exactly one snap from identical signals repeated for a minute', () => {
    let state = initialState();
    let snaps = 0;
    for (let t = 0; t <= 60_000; t += 100) {
      const out = step(state, view({ steadyForMs: t }), t, config);
      state = out.state;
      if (out.snap) snaps++;
    }
    expect(snaps).toBe(1);
  });
});
