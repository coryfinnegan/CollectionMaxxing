// Shot classification from framing (design §3): barcode always wins; shelf vs single-cover on the fixed fixture set.
import { SHOT_FIXTURES } from './fixtures';
import { DEFAULT_SHOT_KIND_THRESHOLDS, classifyShot, type ShotSignals } from './shotKind';

const signals = (overrides: Partial<ShotSignals>): ShotSignals => ({
  barcodesInView: 0,
  rectangleCoverage: 0,
  rectangleCount: 0,
  dominantAspect: null,
  textRows: 0,
  tappedCover: false,
  mediaHint: undefined,
  framesWithoutBarcode: 0,
  gateFired: false,
  ...overrides,
});

describe('classifyShot', () => {
  it.each(SHOT_FIXTURES.map((f) => [f.name, f.signals, f.expected] as const))('classifies %s', (_name, frame, expected) => {
    expect(classifyShot(frame)).toBe(expected);
  });

  it('a barcode in view wins over shelf rows, a fired gate and a tap at once', () => {
    expect(
      classifyShot(signals({ barcodesInView: 1, textRows: 8, rectangleCount: 8, gateFired: true, tappedCover: true })),
    ).toBe('barcode');
  });

  it('honors injected thresholds instead of the defaults', () => {
    const frame = signals({ rectangleCoverage: 0.5, rectangleCount: 1, dominantAspect: 0.7 });

    expect(classifyShot(frame, { ...DEFAULT_SHOT_KIND_THRESHOLDS, singleCoverMinCoverage: 0.6 })).toBe('unknown');
    expect(classifyShot(frame, { ...DEFAULT_SHOT_KIND_THRESHOLDS, singleCoverMinCoverage: 0.4 })).toBe('single_cover');
  });
});

describe('shot-kind thresholds', () => {
  it('are locked: changing one is a deliberate contract change', () => {
    expect(DEFAULT_SHOT_KIND_THRESHOLDS).toEqual({
      singleCoverMinCoverage: 0.45,
      singleCoverAspectMin: 0.5,
      singleCoverAspectMax: 1.8,
      shelfMinTextRows: 2,
      shelfMinRectangles: 2,
      looseItemMinFrames: 10,
    });
  });
});
