// A single cover reads in 3 to 5 seconds, but the picture model has hung on one for the whole server budget twice in a row. A single-item shot
// gets its own, shorter budget, and runs out the way any timeout does: the OCR-text tier reads it instead, and says why.
import type { ReadItemsOptions, ReadItemsResult } from '@/native/cm-vision';
import { AppleModelEngine } from './AppleModelEngine';
import { DEFAULT_RECOGNITION_SETTINGS as S, DEFAULT_SINGLE_SHOT_TUNING, type RecognitionSettings, type SingleShotTuning } from './defaults';
import { EngineSelector } from './EngineSelector';
import { FmTextEngine } from './FmTextEngine';
import { line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import type { ReadOptions, RecognitionEngine } from './types';

const books = [modelItem('Dune', 'Dune', 0)];
const scene: Scene = { read: okRead(books), extract: okRead(books), text: [line('Dune', 0.1, 0.1, 0.1, 0.5)] };

const SINGLE_COVER: ReadOptions = { shotKind: 'single_cover' };
const SHELF: ReadOptions = { shotKind: 'shelf_segment', combine: false };
type Build = (vision: RecordingVision, settings: RecognitionSettings, tuning?: SingleShotTuning) => RecognitionEngine;
const tiers: [string, Build, 'readCalls' | 'extractCalls'][] = [
  ['the picture model', (vision, settings, singleShotTuning) => new AppleModelEngine({ vision, settings: () => settings, newId: testIds, singleShotTuning }), 'readCalls'],
  ['the OCR-text tier', (vision, settings, singleShotTuning) => new FmTextEngine({ vision, settings: () => settings, newId: testIds, singleShotTuning }), 'extractCalls'],
];
const withTimeout = (modelTimeoutMs: number): RecognitionSettings => ({ ...S, engine: { ...S.engine, modelTimeoutMs } });

/** A picture model that never answers: Swift times the call out after the `timeoutMs` it was given and resolves with that outcome. */
class StuckModel extends RecordingVision {
  override async readItems(imageUri: string, options: ReadItemsOptions): Promise<ReadItemsResult> {
    this.readCalls.push({ imageUri, options });
    return new Promise((resolve) => {
      setTimeout(() => resolve({ items: [], unreadableCount: 0, outcome: 'timeout', elapsedMs: options.timeoutMs }), options.timeoutMs);
    });
  }
}

describe('the model budget of a single cover', () => {
  it('is its own, shorter budget, about twelve seconds', () => {
    expect(DEFAULT_SINGLE_SHOT_TUNING.modelTimeoutMs).toBeGreaterThanOrEqual(10_000);
    expect(DEFAULT_SINGLE_SHOT_TUNING.modelTimeoutMs).toBeLessThanOrEqual(15_000);
    expect(DEFAULT_SINGLE_SHOT_TUNING.modelTimeoutMs).toBeLessThan(S.engine.modelTimeoutMs);
  });

  describe.each(tiers)('%s', (_name, build, calls) => {
    const asked = async (options: ReadOptions, settings: RecognitionSettings = S, tuning?: SingleShotTuning) => {
      const vision = new RecordingVision({ [STILL.uri]: scene });
      await build(vision, settings, tuning).read(STILL, () => {}, options);
      return vision[calls][0].options.timeoutMs;
    };

    it.each([['a cover shot', 'single_cover'], ['a loose item', 'loose_item']] as const)('gets the short budget for %s', async (_what, shotKind) => {
      await expect(asked({ shotKind })).resolves.toBe(DEFAULT_SINGLE_SHOT_TUNING.modelTimeoutMs);
    });

    it.each([['a shelf shot', { shotKind: 'shelf_segment' } as ReadOptions], ['a read with no shot kind', {} as ReadOptions], ['a barcode shot', { shotKind: 'barcode' } as ReadOptions]])(
      'keeps the server budget for %s',
      async (_what, options) => {
        await expect(asked(options)).resolves.toBe(S.engine.modelTimeoutMs);
      },
    );

    it('never exceeds the server budget: a shorter server value wins', async () => {
      await expect(asked(SINGLE_COVER, withTimeout(3000))).resolves.toBe(3000);
    });

    it('takes the budget from the tuning it is given', async () => {
      await expect(asked(SINGLE_COVER, S, { modelTimeoutMs: 4321 })).resolves.toBe(4321);
    });
  });

  describe('when the picture model hangs', () => {
    beforeEach(() => jest.useFakeTimers());
    afterEach(() => jest.useRealTimers());

    const selector = () => {
      const vision = new StuckModel({ [STILL.uri]: scene });
      return { vision, selector: new EngineSelector({ vision, settings: () => S, newId: testIds }) };
    };

    it('falls back to the OCR-text tier as soon as a single cover\'s budget is spent, as a timeout does', async () => {
      const { vision, selector: s } = selector();
      let reading: Awaited<ReturnType<typeof s.read>> | undefined;
      void s.read(STILL, () => {}, SINGLE_COVER).then((r) => (reading = r));
      await jest.advanceTimersByTimeAsync(DEFAULT_SINGLE_SHOT_TUNING.modelTimeoutMs - 1);
      expect(reading).toBeUndefined();
      await jest.advanceTimersByTimeAsync(1);
      expect(reading?.engine).toBe('fm_text');
      expect(reading?.fellBackFrom).toBe('timeout');
      expect(reading?.items.map((i) => i.title)).toEqual(['Dune']);
      expect(vision.readCalls).toHaveLength(1);
      expect(vision.extractCalls).toHaveLength(1);
    });

    it('lets a shelf shot take the whole server budget', async () => {
      const { selector: s } = selector();
      let reading: Awaited<ReturnType<typeof s.read>> | undefined;
      void s.read(STILL, () => {}, SHELF).then((r) => (reading = r));
      await jest.advanceTimersByTimeAsync(S.engine.modelTimeoutMs - 1);
      expect(reading).toBeUndefined();
      await jest.advanceTimersByTimeAsync(1);
      expect(reading?.fellBackFrom).toBe('timeout');
    });
  });
});
