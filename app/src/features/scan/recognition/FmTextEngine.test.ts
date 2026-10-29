// The OCR-text tier behaves like the picture reader, minus the picture.
import type { ModelCapabilities } from '@/native/cm-vision';
import { DEFAULT_RECOGNITION_SETTINGS, extractionPromptOf } from './defaults';
import { FmTextEngine } from './FmTextEngine';
import { failedRead, line, modelItem, okRead, RecordingVision, STILL, testIds } from './fakes';
import { ModelUnusableError } from './types';

const TEXT_26: ModelCapabilities = { available: true, vision: false, guidedGeneration: true, contextSize: 4096 };
const TEXT_27: ModelCapabilities = { available: true, vision: true, guidedGeneration: true, contextSize: 4096 };

const scene = (overrides = {}) => ({
  [STILL.uri]: {
    text: [line('Dune', 0.3, 0.2, 0.4, 0.1), line('Frank Herbert', 0.3, 0.8, 0.4, 0.06)],
    extract: okRead([modelItem('Dune', 'Dune Frank Herbert', 0, { creator: 'Frank Herbert', kind: 'book' })]),
    ...overrides,
  },
});

const deps = (vision: RecordingVision) => ({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds });

describe('FmTextEngine availability', () => {
  it.each([
    ['an iOS 26 phone (text model, no vision)', TEXT_26, true],
    ['an iOS 27 phone (vision too)', TEXT_27, true],
  ])('is available on %s', async (_name, capabilities, expected) => {
    const vision = new RecordingVision(scene(), { capabilities });
    await expect(new FmTextEngine(deps(vision)).isAvailable()).resolves.toBe(expected);
  });

  it.each([
    ['Apple Intelligence off', { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' } as const],
    ['no guided generation', { ...TEXT_26, guidedGeneration: false }],
  ])('is unavailable when %s', async (_name, capabilities) => {
    const vision = new RecordingVision(scene(), { capabilities });
    await expect(new FmTextEngine(deps(vision)).isAvailable()).resolves.toBe(false);
  });
});

describe('FmTextEngine reading', () => {
  it('sends the cleaned Vision lines as one text with the server-owned extraction prompt and limits', async () => {
    const vision = new RecordingVision(scene(), { capabilities: TEXT_26 });
    const reading = await new FmTextEngine(deps(vision)).read(STILL, () => {});
    expect(reading.engine).toBe('fm_text');
    expect(vision.extractCalls).toHaveLength(1);
    expect(vision.extractCalls[0].ocrText).toBe('Dune\nFrank Herbert');
    expect(vision.extractCalls[0].options).toEqual({
      prompt: extractionPromptOf(DEFAULT_RECOGNITION_SETTINGS),
      timeoutMs: DEFAULT_RECOGNITION_SETTINGS.engine.modelTimeoutMs,
      maxItems: DEFAULT_RECOGNITION_SETTINGS.engine.maxItemsPerSnap,
    });
  });

  it('asks for one item on a cover shot and the full snap on a shelf shot', async () => {
    const cover = new RecordingVision(scene(), { capabilities: TEXT_26 });
    await new FmTextEngine(deps(cover)).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(cover.extractCalls[0].options.maxItems).toBe(1);

    const shelf = new RecordingVision(scene(), { capabilities: TEXT_26 });
    await new FmTextEngine(deps(shelf)).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(shelf.extractCalls[0].options.maxItems).toBe(DEFAULT_RECOGNITION_SETTINGS.engine.maxItemsPerSnap);
  });

  it('returns typed items with boxes from the matched Vision lines', async () => {
    const vision = new RecordingVision(scene(), { capabilities: TEXT_26 });
    const reading = await new FmTextEngine(deps(vision)).read(STILL, () => {});
    expect(reading.items).toHaveLength(1);
    const [item] = reading.items;
    expect(item.title).toBe('Dune');
    expect(item.creator).toBe('Frank Herbert');
    expect(item.kind).toBe('book');
    expect(item.confidence).toBe('high');
    expect(item.text).toBe('Dune Frank Herbert');
    expect(item.box).toBeDefined();
    expect(item.order).toBe(0);
  });

  it('streams items as they arrive and reports model and vision time separately', async () => {
    let now = 1000;
    const vision = new RecordingVision(scene(), { capabilities: TEXT_26 });
    const engine = new FmTextEngine({ ...deps(vision), now: () => now });
    const streamed: string[] = [];
    const get = engine.read(STILL, (i) => {
      streamed.push(i.title);
      now += 10;
    });
    now += 100;
    const reading = await get;
    expect(streamed).toEqual(['Dune']);
    expect(reading.timings.modelMs).toBeDefined();
    expect(reading.timings.totalMs).toBeGreaterThanOrEqual(reading.timings.modelMs ?? 0);
    expect(reading.timings.visionMs).toBe(reading.timings.totalMs - (reading.timings.modelMs ?? 0));
  });

  it('reads nothing from the model when Vision saw no text', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: [] } }, { capabilities: TEXT_26 });
    const reading = await new FmTextEngine(deps(vision)).read(STILL, () => {});
    expect(reading).toEqual({ items: [], unreadableCount: 0, engine: 'fm_text', timings: reading.timings });
    expect(vision.extractCalls).toEqual([]);
  });

  it.each(['declined', 'timeout', 'contextExceeded', 'unavailable'] as const)(
    'throws ModelUnusableError when extraction ends %s, so a selector can fall back',
    async (outcome) => {
      const vision = new RecordingVision(
        { [STILL.uri]: { text: [line('Dune', 0.3, 0.2, 0.4, 0.1)], extract: failedRead(outcome) } },
        { capabilities: TEXT_26 },
      );
      const error = await new FmTextEngine(deps(vision)).read(STILL, () => {}).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(ModelUnusableError);
      expect((error as ModelUnusableError).reason).toBe(outcome === 'contextExceeded' ? 'contextExceeded' : outcome);
    },
  );

  it('keeps what it had extracted when the extraction runs out of time', async () => {
    const vision = new RecordingVision(
      { [STILL.uri]: { text: [line('Dune', 0.3, 0.2, 0.4, 0.1)], extract: failedRead('timeout', [modelItem('Dune', 'Dune', 0)]) } },
      { capabilities: TEXT_26 },
    );
    const reading = await new FmTextEngine(deps(vision)).read(STILL, () => {});
    expect(reading.engine).toBe('fm_text');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune']);
  });

  it('throws ModelUnusableError when the native call itself fails', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: [line('Dune', 0.3, 0.2, 0.4, 0.1)] } }, { capabilities: TEXT_26 });
    vision.extractItems = async () => {
      throw new Error('no module');
    };
    const error = await new FmTextEngine(deps(vision)).read(STILL, () => {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ModelUnusableError);
    expect((error as ModelUnusableError).reason).toBe('unavailable');
  });

  it('leaves unknown kinds as other and unknown completeness out, never throwing', async () => {
    const vision = new RecordingVision(
      {
        [STILL.uri]: {
          text: [line('Dune', 0.3, 0.2, 0.4, 0.1)],
          extract: okRead([modelItem('Dune', 'Dune', 0, { kind: 'laserdisc' as never, completeness: 'shrinkwrapped' as never })]),
        },
      },
      { capabilities: TEXT_26 },
    );
    const [item] = (await new FmTextEngine(deps(vision)).read(STILL, () => {})).items;
    expect(item.kind).toBe('other');
    expect(item.completeness).toBeUndefined();
  });

  it('runs one extraction at a time', async () => {
    const vision = new RecordingVision(scene(), { capabilities: TEXT_26 });
    const engine = new FmTextEngine(deps(vision));
    const [a, b] = await Promise.all([engine.read(STILL, () => {}), engine.read(STILL, () => {})]);
    expect(a.items).toHaveLength(1);
    expect(b.items).toHaveLength(1);
    expect(vision.extractCalls).toHaveLength(2);
  });
});
