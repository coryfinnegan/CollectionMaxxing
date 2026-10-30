import type { ModelItem, Rect } from '@/native/cm-vision';
import { DEFAULT_COVER_FORMAT_TUNING, DEFAULT_RECOGNITION_SETTINGS as S, promptOf, type RecognitionSettings } from './defaults';
import { EngineSelector, type SelectorDeps } from './EngineSelector';
import { failedRead, FakeCropper, line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import type { ReadItem, ReadOptions } from './types';

const books = [modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)];
const text = [line('Dune', 0.1, 0.1, 0.1, 0.5), line('Emma', 0.5, 0.1, 0.1, 0.5)];
const ok: Scene = { read: okRead(books), text };
/** These tests are about the model's own reading (timeouts, halves); the Vision items a shelf shot adds are tested in shelfCombine.test.ts. */
const ALONE = { combine: false };

function selector(scene: Scene, caps?: object, settings: RecognitionSettings = S, cropper?: FakeCropper) {
  const vision = new RecordingVision({ [STILL.uri]: scene }, caps ? { capabilities: caps as never } : undefined);
  return { vision, selector: new EngineSelector({ vision, settings: () => settings, newId: testIds, cropper }) };
}

describe('EngineSelector', () => {
  it('uses the Apple model when it is available with vision', async () => {
    const { selector: s } = selector(ok);
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
  });

  it.each([
    ['the model is off', { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' }, ok, 'unavailable'],
    ['guided generation is off', { available: true, vision: false, guidedGeneration: false, contextSize: 4096 }, ok, 'unavailable'],
    ['the model declines', undefined, { ...ok, read: failedRead('declined') }, 'declined'],
    ['the model times out before reading anything', undefined, { ...ok, read: failedRead('timeout') }, 'timeout'],
    ['the context is exceeded', undefined, { ...ok, read: failedRead('contextExceeded') }, 'contextExceeded'],
    ['the call reports it is unavailable', undefined, { ...ok, read: failedRead('unavailable') }, 'unavailable'],
  ])('falls back to Vision text when %s, and says why', async (_name, caps, scene, why) => {
    const { selector: s } = selector(scene as Scene, caps);
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe(why);
    expect(reading.items.length).toBeGreaterThan(0);
  });

  it('keeps what the model had read when a shelf runs out of time, as the Apple reading', async () => {
    // First eval run: a dense shelf streamed for longer than the budget and every spine it had read was thrown away.
    const { selector: s } = selector({ ...ok, read: failedRead('timeout', [books[0]]) });
    const reading = await s.read(STILL, () => {}, ALONE);
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(reading.items.map((i) => i.title)).toEqual(['Dune']);
  });

  it('falls back for that snap only: the next snap tries the model again', async () => {
    const declined: Scene = { ...ok, read: failedRead('declined') };
    const vision = new RecordingVision({ [STILL.uri]: declined, 'fixture://next': ok });
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds });
    expect((await s.read(STILL, () => {})).fellBackFrom).toBe('declined');
    const next = await s.read({ ...STILL, uri: 'fixture://next' }, () => {});
    expect(next.engine).toBe('apple_model');
    expect(next.fellBackFrom).toBeUndefined();
    expect(vision.readCalls).toHaveLength(2);
  });

  it('passes on how many items could not be read, for the "Get closer" chip', async () => {
    const { selector: s } = selector({ ...ok, read: okRead(books, 3) });
    expect((await s.read(STILL, () => {})).unreadableCount).toBe(3);
  });

  it('reports the engine and the Apple Intelligence state for Settings', async () => {
    expect(await selector(ok).selector.state()).toEqual({ engine: 'apple_model', appleIntelligence: 'on' });
    const off = { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' };
    expect(await selector(ok, off).selector.state()).toEqual({ engine: 'vision_pipeline', appleIntelligence: 'appleIntelligenceOff' });
  });

  it('streams items as they are read and runs one model read at a time', async () => {
    const { vision, selector: s } = selector(ok);
    const streamed: ReadItem[] = [];
    await Promise.all([s.read(STILL, (i) => streamed.push(i)), s.read(STILL, () => {})]);
    expect(streamed.map((i) => i.title)).toEqual(['Dune', 'Emma']);
    expect(vision.maxConcurrentReads).toBe(1);
  });
});

describe('EngineSelector with the OCR-text tier', () => {
  const textOnly = { available: true, vision: false, guidedGeneration: true, contextSize: 4096 };
  const extracted: Scene = { ...ok, extract: okRead(books) };

  it('reads with the text tier first on a phone whose model cannot see pictures', async () => {
    const { vision, selector: s } = selector(extracted, textOnly);
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('fm_text');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
    expect(vision.readCalls).toHaveLength(0);
    expect(vision.extractCalls).toHaveLength(1);
  });

  it('falls back to Vision text when extraction fails on a text-only phone, and says why', async () => {
    const { selector: s } = selector({ ...ok, extract: failedRead('declined') }, textOnly);
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe('declined');
    expect(reading.items.length).toBeGreaterThan(0);
  });

  it('tries the text tier when the picture read fails, and keeps the picture failure as the reason', async () => {
    const { selector: s } = selector({ ...ok, read: failedRead('declined'), extract: okRead(books) });
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('fm_text');
    expect(reading.fellBackFrom).toBe('declined');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
  });

  it('falls back to Vision text with the picture failure when extraction fails too', async () => {
    const { selector: s } = selector({ ...ok, read: failedRead('timeout'), extract: failedRead('declined') });
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe('timeout');
    expect(reading.items.length).toBeGreaterThan(0);
  });

  it('reports the text tier for Settings on a text-only phone', async () => {
    expect(await selector(ok, textOnly).selector.state()).toEqual({ engine: 'fm_text', appleIntelligence: 'on' });
  });
});

describe('EngineSelector with a wide shot', () => {
  const many = (names: string[], offset = 0) => names.map((n, i) => modelItem(n, n, i + offset));
  const spine = (name: string, x: number) => line(name, x, 0.1, 0.04, 0.6);
  const smallLimit: RecognitionSettings = { ...S, splitting: { ...S.splitting, halvesWhenItemsOver: 3 } };

  function wide() {
    // Six spines; the cut falls in the gap near the middle, and "Cujo" sits in the overlap so both halves read it.
    const cropper = new FakeCropper();
    const scenes: Record<string, Scene> = {
      [STILL.uri]: {
        read: okRead(many(['Alpha', 'Bravo', 'Cujo', 'Delta']), 2),
        text: [spine('Alpha', 0.05), spine('Bravo', 0.2), spine('Cujo', 0.45), spine('Delta', 0.8), spine('Echo', 0.9), spine('Foxtrot', 0.95)],
      },
      // left half covers x 0..0.55 of the picture; right half 0.45..1 (cut at 0.5, overlap 0.05)
      [`${STILL.uri}#crop1`]: { read: okRead(many(['Alpha', 'Bravo', 'Cujo'])), text: [line('Alpha', 0.1, 0.1, 0.05, 0.6), line('Bravo', 0.4, 0.1, 0.05, 0.6), line('Cujo', 0.8, 0.1, 0.1, 0.6)] },
      [`${STILL.uri}#crop2`]: {
        read: okRead(many(['Cujo', 'Delta', 'Echo', 'Foxtrot']), 0),
        text: [line('Cujo', 0.0, 0.1, 0.05, 0.6), line('Delta', 0.5, 0.1, 0.05, 0.6), line('Echo', 0.7, 0.1, 0.05, 0.6), line('Foxtrot', 0.85, 0.1, 0.05, 0.6)],
      },
    };
    return { cropper, scenes };
  }

  it('reads a shelf over the limit in two halves and merges them without duplicates at the seam', async () => {
    const { cropper, scenes } = wide();
    const vision = new RecordingVision(scenes);
    const s = new EngineSelector({ vision, settings: () => smallLimit, newId: testIds, cropper });
    const reading = await s.read(STILL, () => {});
    expect(cropper.regions).toHaveLength(2);
    expect(reading.engine).toBe('apple_model');
    expect(reading.items.map((i) => i.title)).toEqual(['Alpha', 'Bravo', 'Cujo', 'Delta', 'Echo', 'Foxtrot']);
    expect(reading.items.map((i) => i.order)).toEqual([0, 1, 2, 3, 4, 5]);
  });

  it('reads a shelf at or under the limit in one look', async () => {
    const { cropper, scenes } = wide();
    const vision = new RecordingVision(scenes);
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds, cropper });
    const reading = await s.read(STILL, () => {}, ALONE);
    expect(cropper.regions).toHaveLength(0);
    expect(reading.items).toHaveLength(4);
  });

  it('keeps the whole-shot reading when a half fails', async () => {
    const { cropper, scenes } = wide();
    scenes[`${STILL.uri}#crop2`] = { read: failedRead('declined') };
    const vision = new RecordingVision(scenes);
    const s = new EngineSelector({ vision, settings: () => smallLimit, newId: testIds, cropper });
    const reading = await s.read(STILL, () => {}, ALONE);
    expect(reading.engine).toBe('apple_model');
    expect(reading.items).toHaveLength(4);
  });
});

describe('EngineSelector with a single cover: the case says the format', () => {
  // Last eval: the model called a Criterion Blu-ray a DVD, a Disney VHS clamshell a DVD, and a VHS slipcase a book.
  const COVER = { shotKind: 'single_cover' as const, combine: false };
  const STILL_RATIO = STILL.width / STILL.height; // 0.75: the sizes below are in pixels of this still
  /** A case standing upright, `ratio` wide for its height, 80% of the picture tall. */
  const caseWith = (ratio: number): Rect => {
    const width = (ratio * 0.8) / STILL_RATIO;
    return { x: (1 - width) / 2, y: 0.1, width, height: 0.8 };
  };
  const slipcase = caseWith(0.55);
  const keepcase = caseWith(0.71);

  const cover = (kind: ModelItem['kind'], extra: Scene = {}): Scene => ({
    read: okRead([modelItem('Casablanca', 'CASABLANCA', 0, { kind })]),
    text: [line('CASABLANCA', 0.2, 0.2, 0.5, 0.1)],
    ...extra,
  });
  const readCover = async (scene: Scene, options: ReadOptions = COVER, deps: Partial<SelectorDeps> = {}) => {
    const vision = new RecordingVision({ [STILL.uri]: scene });
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds, ...deps });
    const streamed: ReadItem[] = [];
    const reading = await s.read(STILL, (i) => streamed.push(i), options);
    return { vision, streamed, reading };
  };
  const formats = (items: ReadItem[]) => items.map((i) => `${i.kind}/${i.format}`);

  it('streams and returns VHS when the model called a tall narrow cover a DVD', async () => {
    const { streamed, reading } = await readCover(cover('dvd', { rectangles: [slipcase] }));
    expect(formats(streamed)).toEqual(['film/vhs']);
    expect(formats(reading.items)).toEqual(['film/vhs']);
    expect(reading.items[0]).toMatchObject({ title: 'Casablanca', confidence: 'high', order: 0 });
  });

  it('corrects DVD to Blu-ray when the cover says Blu-ray, whatever its shape', async () => {
    const { streamed, reading } = await readCover(cover('dvd', { rectangles: [keepcase], text: [line('Blu-ray Disc', 0.1, 0.8, 0.3, 0.04)] }));
    expect(formats(streamed)).toEqual(['film/bluray']);
    expect(formats(reading.items)).toEqual(['film/bluray']);
  });

  it('corrects a book read to the film format a logo names', async () => {
    const { streamed, reading } = await readCover(cover('book', { text: [line('VHS', 0.1, 0.8, 0.1, 0.04), line('HI-FI', 0.3, 0.8, 0.1, 0.04)] }));
    expect(formats(streamed)).toEqual(['film/vhs']);
    expect(formats(reading.items)).toEqual(['film/vhs']);
  });

  it('does not turn a book into a film because of the shape of the case', async () => {
    const { reading } = await readCover(cover('book', { rectangles: [keepcase] }));
    expect(formats(reading.items)).toEqual(['book/undefined']);
  });

  it('leaves a game alone, whatever the logo or the shape', async () => {
    const scene = cover('game', { rectangles: [slipcase], text: [line('DVD VIDEO', 0.1, 0.8, 0.3, 0.04)] });
    const { streamed, reading } = await readCover(scene);
    expect(formats(streamed)).toEqual(['game/undefined']);
    expect(formats(reading.items)).toEqual(['game/undefined']);
  });

  it('corrects a loose item shot the same way', async () => {
    const { reading } = await readCover(cover('dvd', { rectangles: [slipcase] }), { shotKind: 'loose_item', combine: false });
    expect(formats(reading.items)).toEqual(['film/vhs']);
  });

  it('corrects the cover whatever the combine option says', async () => {
    const { reading } = await readCover(cover('dvd', { rectangles: [slipcase] }), { shotKind: 'single_cover' });
    expect(formats(reading.items)).toEqual(['film/vhs']);
  });

  it('keeps the order of the items it streams', async () => {
    const read = okRead([modelItem('Casablanca', 'CASABLANCA', 0, { kind: 'dvd' }), modelItem('Gigi', 'GIGI', 1, { kind: 'dvd' })]);
    const { streamed, reading } = await readCover(cover('dvd', { read, rectangles: [slipcase] }));
    expect(streamed.map((i) => `${i.title}/${i.format}`)).toEqual(['Casablanca/vhs', 'Gigi/vhs']);
    expect(reading.items.map((i) => `${i.title}/${i.format}`)).toEqual(['Casablanca/vhs', 'Gigi/vhs']);
  });

  it('leaves a shelf shot as the model read it, and does not even look at the case', async () => {
    const scene = cover('dvd', { rectangles: [slipcase], text: [line('Blu-ray', 0.1, 0.8, 0.3, 0.04)] });
    const vision = new RecordingVision({ [STILL.uri]: scene });
    const rectangles = jest.spyOn(vision, 'detectRectangles');
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds });
    const streamed: ReadItem[] = [];
    const reading = await s.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment', combine: false });
    expect(formats(streamed)).toEqual(['film/dvd']);
    expect(formats(reading.items)).toEqual(['film/dvd']);
    expect(rectangles).not.toHaveBeenCalled();
  });

  it('leaves a read with no shot kind as the model read it', async () => {
    const { reading } = await readCover(cover('dvd', { rectangles: [slipcase] }), { combine: false });
    expect(formats(reading.items)).toEqual(['film/dvd']);
  });

  it('leaves an item as it was when the case gives no cue', async () => {
    const { reading } = await readCover(cover('dvd'));
    expect(formats(reading.items)).toEqual(['film/dvd']);
  });

  it('starts looking at the case while the model reads, not after it', async () => {
    const vision = new RecordingVision({ [STILL.uri]: cover('dvd', { rectangles: [slipcase] }) });
    const order: string[] = [];
    const model = vision.readItems.bind(vision);
    jest.spyOn(vision, 'readItems').mockImplementation(async (...args) => {
      const result = await model(...args);
      order.push('model done');
      return result;
    });
    const rectangles = vision.detectRectangles.bind(vision);
    jest.spyOn(vision, 'detectRectangles').mockImplementation(async (uri) => {
      order.push('rectangles asked');
      return rectangles(uri);
    });
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds });
    await s.read(STILL, () => {}, COVER);
    expect(order).toEqual(['rectangles asked', 'model done']);
  });

  it('waits a moment for a cue that is a little late, and uses it', async () => {
    const vision = new RecordingVision({ [STILL.uri]: cover('dvd') });
    jest.spyOn(vision, 'detectRectangles').mockImplementation(() => new Promise<Rect[]>((resolve) => setTimeout(() => resolve([slipcase]), 30)));
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds, coverFormatTuning: { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 2000 } });
    const reading = await s.read(STILL, () => {}, COVER);
    expect(formats(reading.items)).toEqual(['film/vhs']);
  });

  it('sends the model items as they were when the cue is slower than the cap, and does not wait for it', async () => {
    const vision = new RecordingVision({ [STILL.uri]: cover('dvd', { rectangles: [slipcase] }) });
    jest.spyOn(vision, 'detectRectangles').mockReturnValue(new Promise<Rect[]>(() => {})); // never answers
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds, coverFormatTuning: { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 20 } });
    const streamed: ReadItem[] = [];
    const started = Date.now();
    const reading = await s.read(STILL, (i) => streamed.push(i), COVER);
    expect(formats(streamed)).toEqual(['film/dvd']);
    expect(formats(reading.items)).toEqual(['film/dvd']);
    expect(reading.engine).toBe('apple_model');
    expect(Date.now() - started).toBeLessThan(1000);
  });

  it('sends the model items as they were when Vision throws looking at the case', async () => {
    const vision = new RecordingVision({ [STILL.uri]: cover('dvd', { rectangles: [slipcase] }) });
    jest.spyOn(vision, 'detectRectangles').mockRejectedValue(new Error('Vision failed'));
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds });
    const streamed: ReadItem[] = [];
    const reading = await s.read(STILL, (i) => streamed.push(i), COVER);
    expect(formats(streamed)).toEqual(['film/dvd']);
    expect(formats(reading.items)).toEqual(['film/dvd']);
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
  });

  it('sends the model items as they were when Vision throws before it even returns a promise', async () => {
    const vision = new RecordingVision({ [STILL.uri]: cover('dvd', { rectangles: [slipcase] }) });
    jest.spyOn(vision, 'detectRectangles').mockImplementation(() => {
      throw new Error('Vision failed');
    });
    const s = new EngineSelector({ vision, settings: () => S, newId: testIds });
    const reading = await s.read(STILL, () => {}, COVER);
    expect(formats(reading.items)).toEqual(['film/dvd']);
  });

  describe('the lower tiers get the same correction', () => {
    const textOnly = { available: true, vision: false, guidedGeneration: true, contextSize: 4096 };
    const off = { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' };
    const casablanca = (kind: ModelItem['kind']) => okRead([modelItem('Casablanca', 'CASABLANCA', 0, { kind })]);
    const select = (scene: Scene, capabilities?: object, deps: Partial<SelectorDeps> = {}) =>
      new EngineSelector({ vision: new RecordingVision({ [STILL.uri]: scene }, capabilities ? { capabilities: capabilities as never } : undefined), settings: () => S, newId: testIds, ...deps });

    it('corrects the OCR-text tier on a phone whose model cannot see pictures', async () => {
      const streamed: ReadItem[] = [];
      const reading = await select(cover('dvd', { extract: casablanca('dvd'), rectangles: [slipcase] }), textOnly).read(STILL, (i) => streamed.push(i), COVER);
      expect(reading.engine).toBe('fm_text');
      expect(formats(streamed)).toEqual(['film/vhs']);
      expect(formats(reading.items)).toEqual(['film/vhs']);
    });

    it('corrects the OCR-text tier when the picture model could not read the cover, and still says why it fell back', async () => {
      const scene = cover('dvd', { read: failedRead('timeout'), extract: casablanca('book'), text: [line('CASABLANCA', 0.2, 0.2, 0.5, 0.1), line('Blu-ray Disc', 0.1, 0.8, 0.3, 0.04)] });
      const reading = await select(scene).read(STILL, () => {}, COVER);
      expect(reading.engine).toBe('fm_text');
      expect(reading.fellBackFrom).toBe('timeout');
      expect(formats(reading.items)).toEqual(['film/bluray']);
    });

    it('corrects the Vision-only tier', async () => {
      const scene = cover('dvd', { text: [line('CASABLANCA', 0.2, 0.2, 0.5, 0.1), line('DVD VIDEO', 0.1, 0.8, 0.3, 0.04)] });
      const streamed: ReadItem[] = [];
      const reading = await select(scene, off).read(STILL, (i) => streamed.push(i), COVER);
      expect(reading.engine).toBe('vision_pipeline');
      expect(formats(streamed)).toEqual(['film/dvd']);
      expect(formats(reading.items)).toEqual(['film/dvd']);
    });

    it('corrects the kind the picture model read from the text on the cover too', async () => {
      const scene = cover('book', { text: [line('RUSHMORE', 0.2, 0.2, 0.5, 0.1), line('THE CRITERION COLLECTION', 0.1, 0.8, 0.3, 0.04)] });
      const reading = await select(scene).read(STILL, () => {}, COVER);
      expect(formats(reading.items)).toEqual(['film/undefined']);
    });

    it('hands the eval runner the same tiers, so what it measures is what the scanner does', async () => {
      const selector = select(cover('dvd', { extract: casablanca('dvd'), rectangles: [slipcase] }), textOnly);
      expect(selector.tiers.fmText.name).toBe('fm_text');
      expect(selector.tiers.pipeline.name).toBe('vision_pipeline');
      expect(formats((await selector.tiers.fmText.read(STILL, () => {}, COVER)).items)).toEqual(['film/vhs']);
      expect(formats((await selector.tiers.fmText.read(STILL, () => {}, { shotKind: 'shelf_segment' })).items)).toEqual(['film/dvd']);
    });

    it('reads the case for the Vision tier the same way as for the picture model', async () => {
      const scene = cover('dvd', { rectangles: [slipcase], text: [line('CASABLANCA', 0.2, 0.2, 0.5, 0.1), line('VHS', 0.1, 0.8, 0.1, 0.04), line('HI-FI', 0.3, 0.8, 0.1, 0.04)] });
      const reading = await select(scene).tiers.pipeline.read(STILL, () => {}, COVER);
      expect(formats(reading.items)).toEqual(['film/vhs']);
    });
  });
});

describe('config reaching the model', () => {
  it('sends the prompt and limits from the current settings on every call', async () => {
    let current = S;
    const vision = new RecordingVision({ [STILL.uri]: ok });
    const s = new EngineSelector({ vision, settings: () => current, newId: testIds });
    await s.read(STILL, () => {});
    current = {
      ...S,
      version: 'v2',
      prompt: { template: 'Read the shelf.', outputRules: 'Be careful.' },
      engine: { modelTimeoutMs: 1234, maxItemsPerSnap: 5 },
    };
    await s.read(STILL, () => {});
    expect(vision.readCalls[0].options).toEqual({ prompt: promptOf(S), timeoutMs: S.engine.modelTimeoutMs, maxItems: S.engine.maxItemsPerSnap });
    expect(vision.readCalls[1].options).toEqual({ prompt: 'Read the shelf.\n\nBe careful.', timeoutMs: 1234, maxItems: 5 });
  });
});
