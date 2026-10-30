// The cover cue is one piece that wraps any engine, so the picture model, the OCR-text tier and the Vision tier all get the same
// correction on a single cover, and the eval measures what the scanner does. (The picture model's own cases are in EngineSelector.test.ts.)
import type { ModelItem, Rect } from '@/native/cm-vision';
import coverReads from './coverReads.json';
import { withCoverCue } from './CoverCueEngine';
import { DEFAULT_COVER_FORMAT_TUNING, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { failedRead, line, modelItem, okRead, RecordingVision, testIds, type Scene } from './fakes';
import { FmTextEngine } from './FmTextEngine';
import { AppleModelEngine } from './AppleModelEngine';
import { type ReadItem, type ReadOptions, ModelUnusableError, type RecognitionEngine, type Still } from './types';
import { VisionPipelineEngine } from './VisionPipelineEngine';

interface RecordedCover {
  width: number;
  height: number;
  lines: { text: string; x: number; y: number; width: number; height: number }[];
}
const recorded = coverReads as Record<string, RecordedCover>;

const COVER: ReadOptions = { shotKind: 'single_cover' };

/** A real cover's Vision text, its size, and a scene to read it in. */
function realCover(id: string, scene: Scene = {}) {
  const cover = recorded[id];
  const still: Still = { uri: `fixture://${id}`, width: cover.width, height: cover.height };
  const text = cover.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height));
  return { still, scene: { text, ...scene } satisfies Scene };
}

/** A case standing upright on `still`, `ratio` wide for its height, 80% of the picture tall. */
const caseOn = (still: Still, ratio: number): Rect => {
  const width = (ratio * 0.8 * still.height) / still.width;
  return { x: (1 - width) / 2, y: 0.1, width, height: 0.8 };
};

const asRead = (items: ReadItem[]) => items.map((i) => `${i.title}: ${i.kind}/${i.format}`);

function engines(scenes: Record<string, Scene>, tuning = DEFAULT_COVER_FORMAT_TUNING) {
  const vision = new RecordingVision(scenes);
  const deps = { vision, settings: () => S, newId: testIds };
  const cued = (engine: RecognitionEngine) => withCoverCue(engine, { vision, settings: () => S, coverFormatTuning: tuning });
  return {
    vision,
    model: cued(new AppleModelEngine(deps)),
    text: cued(new FmTextEngine(deps)),
    pipeline: cued(new VisionPipelineEngine(deps)),
  };
}

async function readWith(engine: RecognitionEngine, still: Still, options: ReadOptions = COVER) {
  const streamed: ReadItem[] = [];
  const reading = await engine.read(still, (i) => streamed.push(i), options);
  return { streamed, reading };
}

const rushmore = (kind: ModelItem['kind']) => okRead([modelItem('RUSHMORE', 'RUSHMORE', 0, { kind })]);

describe('the OCR-text tier on a single cover', () => {
  // Last eval: the text tier read the Criterion "RUSHMORE" Blu-ray as a book and a 1999 book was added.
  it('reads a Criterion Blu-ray as a film in the format its case has, not as a book', async () => {
    const { still, scene } = realCover('bluray-01', { extract: rushmore('book') });
    const { text } = engines({ [still.uri]: { ...scene, rectangles: [caseOn(still, 0.86)] } });
    const { streamed, reading } = await readWith(text, still);
    expect(asRead(streamed)).toEqual(['RUSHMORE: film/bluray']);
    expect(asRead(reading.items)).toEqual(['RUSHMORE: film/bluray']);
    expect(reading.engine).toBe('fm_text');
  });

  it('reads a Criterion film as a film even when its case says no format', async () => {
    const { still, scene } = realCover('bluray-01', { extract: rushmore('book') });
    const { text } = engines({ [still.uri]: scene });
    expect(asRead((await readWith(text, still)).reading.items)).toEqual(['RUSHMORE: film/undefined']);
  });

  it('reads a cartridge with a console\'s name on it as a game', async () => {
    const { still, scene } = realCover('game-cartridge-01', { extract: okRead([modelItem('Super Mario World', 'SUPER NINTENDO', 0, { kind: 'book' })]) });
    const { text } = engines({ [still.uri]: scene });
    expect(asRead((await readWith(text, still)).reading.items)).toEqual(['Super Mario World: game/undefined']);
  });

  it('keeps a film adaptation line from turning a read into a book', async () => {
    const { still, scene } = realCover('dvd-02', { extract: okRead([modelItem('CONGO', 'CONGO', 0, { kind: 'dvd' })]) });
    const { text } = engines({ [still.uri]: scene });
    expect(asRead((await readWith(text, still)).reading.items)).toEqual(['CONGO: film/dvd']);
  });

  it('leaves a shelf shot as the tier read it, and does not look at the case', async () => {
    const { still, scene } = realCover('bluray-01', { extract: rushmore('book') });
    const { text, vision } = engines({ [still.uri]: scene });
    const rectangles = jest.spyOn(vision, 'detectRectangles');
    const { reading } = await readWith(text, still, { shotKind: 'shelf_segment' });
    expect(asRead(reading.items)).toEqual(['RUSHMORE: book/undefined']);
    expect(rectangles).not.toHaveBeenCalled();
  });
});

describe('the Vision tier on a single cover', () => {
  it('reads a paperback with a bestseller line as a book rather than "other"', async () => {
    const { still, scene } = realCover('paperback-04');
    const { pipeline } = engines({ [still.uri]: scene });
    const { streamed, reading } = await readWith(pipeline, still);
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].kind).toBe('book');
    expect(streamed.map((i) => i.kind)).toEqual(['book']);
  });

  it('reads a Criterion film as a film and a game cartridge as a game rather than "other"', async () => {
    const criterion = realCover('bluray-01');
    const cartridge = realCover('game-cartridge-02');
    const { pipeline } = engines({ [criterion.still.uri]: criterion.scene, [cartridge.still.uri]: cartridge.scene });
    expect((await readWith(pipeline, criterion.still)).reading.items.map((i) => i.kind)).toEqual(['film']);
    expect((await readWith(pipeline, cartridge.still)).reading.items.map((i) => i.kind)).toEqual(['game']);
  });

  it('reads a Blu-ray logo as a Blu-ray', async () => {
    const { still, scene } = realCover('dvd-02');
    const logo = line('Blu-ray Disc', 0.1, 0.9, 0.3, 0.04);
    const { pipeline } = engines({ [still.uri]: { ...scene, text: [...(scene.text ?? []), logo] } });
    expect(asRead((await readWith(pipeline, still)).reading.items).map((r) => r.replace(/^.*: /, ''))).toEqual(['film/bluray']);
  });

  it('leaves a cover with no cue as "other", the way the server expects it', async () => {
    const { still, scene } = realCover('dvd-02');
    const { pipeline } = engines({ [still.uri]: scene });
    const { reading } = await readWith(pipeline, still);
    expect(reading.items.map((i) => i.kind)).toEqual(['other']);
  });

  it('leaves a shelf shot alone', async () => {
    const { still, scene } = realCover('paperback-04');
    const { pipeline } = engines({ [still.uri]: scene });
    const { reading } = await readWith(pipeline, still, { shotKind: 'shelf_segment' });
    expect(reading.items.length).toBeGreaterThan(0);
    expect(reading.items.every((i) => i.kind === 'other')).toBe(true);
  });
});

describe('a game cartridge\'s product code on a single cover (ADR-031)', () => {
  // Last eval: neither SNES cartridge was read by any engine, yet Vision read both codes exactly.
  const codes = (items: ReadItem[]) => items.map((i) => `${i.kind}/${i.productCode}`);

  it('puts the code on the Vision tier\'s item and makes it a game', async () => {
    const first = realCover('game-cartridge-01');
    const second = realCover('game-cartridge-02');
    const { pipeline } = engines({ [first.still.uri]: first.scene, [second.still.uri]: second.scene });
    const one = await readWith(pipeline, first.still);
    expect(codes(one.streamed)).toEqual(['game/SNS-8C-USA']);
    expect(codes(one.reading.items)).toEqual(['game/SNS-8C-USA']);
    expect(codes((await readWith(pipeline, second.still)).reading.items)).toEqual(['game/SNS-ADNE-USA']);
  });

  it.each([['game'], ['other']] as const)('puts the code on the picture model\'s %s read', async (kind) => {
    const { still, scene } = realCover('game-cartridge-01', { read: okRead([modelItem('Super Mario World', 'SUPER MARIO WORLD', 0, { kind })]) });
    const { model } = engines({ [still.uri]: scene });
    const { streamed, reading } = await readWith(model, still);
    expect(codes(streamed)).toEqual(['game/SNS-8C-USA']);
    expect(codes(reading.items)).toEqual(['game/SNS-8C-USA']);
    expect(reading.items[0]).toMatchObject({ title: 'Super Mario World', confidence: 'high', order: 0 });
  });

  it('puts the code on the OCR-text tier\'s read', async () => {
    const { still, scene } = realCover('game-cartridge-02', { extract: okRead([modelItem('Donkey Kong Country', 'DONKEY KONG', 0, { kind: 'other' })]) });
    const { text } = engines({ [still.uri]: scene });
    const { streamed, reading } = await readWith(text, still);
    expect(codes(streamed)).toEqual(['game/SNS-ADNE-USA']);
    expect(codes(reading.items)).toEqual(['game/SNS-ADNE-USA']);
  });

  it('puts the code on a game the model called a book, once the console\'s name has made it a game', async () => {
    const { still, scene } = realCover('game-cartridge-01', { read: okRead([modelItem('Super Mario World', 'SUPER MARIO WORLD', 0, { kind: 'book' })]) });
    const { model } = engines({ [still.uri]: scene });
    expect(codes((await readWith(model, still)).reading.items)).toEqual(['game/SNS-8C-USA']);
  });

  it('gives a book or a film no code and changes neither, however code-like a line on the cover is', async () => {
    const book = realCover('paperback-01', { read: okRead([modelItem('My Friends', 'MY FRIENDS', 0, { kind: 'book' })]) });
    const film = realCover('dvd-01', { read: okRead([modelItem('The Hunt for Red October', 'THE HUNT FOR RED OCTOBER', 0, { kind: 'dvd' })]) });
    const stray = line('SNS-8C-USA', 0.1, 0.95, 0.2, 0.03);
    const { model } = engines({
      [book.still.uri]: { ...book.scene, text: [...(book.scene.text ?? []), stray] },
      [film.still.uri]: { ...film.scene, text: [...(film.scene.text ?? []), stray] },
    });
    expect(codes((await readWith(model, book.still)).reading.items)).toEqual(['book/undefined']);
    expect(codes((await readWith(model, film.still)).reading.items)).toEqual(['film/undefined']);
  });

  it('gives no code to a cover that prints none', async () => {
    const ids = Object.keys(recorded).filter((id) => !id.startsWith('game-cartridge'));
    const { pipeline } = engines(Object.fromEntries(ids.map((id) => [`fixture://${id}`, realCover(id).scene])));
    for (const id of ids) {
      const { reading } = await readWith(pipeline, realCover(id).still);
      expect([id, reading.items.map((i) => i.productCode)]).toEqual([id, reading.items.map(() => undefined)]);
    }
  });

  it('leaves a shelf shot to the shelf path: the cue does not put a code on a model item there', async () => {
    const { still, scene } = realCover('game-cartridge-01', { read: okRead([modelItem('Super Mario World', 'SUPER MARIO WORLD', 0, { kind: 'game' })]) });
    const { model } = engines({ [still.uri]: scene });
    const { reading } = await readWith(model, still, { shotKind: 'shelf_segment' });
    expect(reading.items.map((i) => i.productCode)).toEqual([undefined]);
  });
});

describe('the cue beside the read', () => {
  const slipcase = (still: Still) => caseOn(still, 0.55);

  it('looks for a case as wide as it is tall', async () => {
    const { still, scene } = realCover('bluray-01', { extract: rushmore('dvd') });
    const { text, vision } = engines({ [still.uri]: scene });
    const rectangles = jest.spyOn(vision, 'detectRectangles');
    await readWith(text, still);
    expect(rectangles).toHaveBeenCalledWith(still.uri, { maximumAspectRatio: DEFAULT_COVER_FORMAT_TUNING.maxCaseAspectRatio });
    expect(DEFAULT_COVER_FORMAT_TUNING.maxCaseAspectRatio).toBe(1);
  });

  it('starts looking at the case before the read is over, not after it', async () => {
    const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
    const { text, vision } = engines({ [still.uri]: { ...scene, rectangles: [slipcase(still)] } });
    const order: string[] = [];
    const extract = vision.extractItems.bind(vision);
    jest.spyOn(vision, 'extractItems').mockImplementation(async (...args) => {
      const result = await extract(...args);
      order.push('read done');
      return result;
    });
    const rectangles = vision.detectRectangles.bind(vision);
    jest.spyOn(vision, 'detectRectangles').mockImplementation(async (uri, options) => {
      order.push('rectangles asked');
      return rectangles(uri, options);
    });
    const { reading } = await readWith(text, still);
    expect(order).toEqual(['rectangles asked', 'read done']);
    expect(asRead(reading.items)).toEqual(['RED OCTOBER: film/vhs']);
  });

  it('keeps the order of the items it streams, and streams them corrected', async () => {
    const { still, scene } = realCover('vhs-01', {
      extract: okRead([modelItem('First', 'RED', 0, { kind: 'dvd' }), modelItem('Second', 'OCTOBER', 1, { kind: 'dvd' })]),
    });
    const { text } = engines({ [still.uri]: { ...scene, rectangles: [slipcase(still)] } });
    const { streamed, reading } = await readWith(text, still);
    expect(asRead(streamed)).toEqual(['First: film/vhs', 'Second: film/vhs']);
    expect(asRead(reading.items)).toEqual(['First: film/vhs', 'Second: film/vhs']);
  });

  it('still fixes the kind from the text when the rectangle search fails', async () => {
    const { still, scene } = realCover('bluray-01', { extract: rushmore('book') });
    const { text, vision } = engines({ [still.uri]: scene });
    jest.spyOn(vision, 'detectRectangles').mockRejectedValue(new Error('Vision failed'));
    expect(asRead((await readWith(text, still)).reading.items)).toEqual(['RUSHMORE: film/undefined']);
  });

  it('sends the items as they were when Vision throws before it even returns a promise', async () => {
    const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
    const { text, vision } = engines({ [still.uri]: scene });
    jest.spyOn(vision, 'detectRectangles').mockImplementation(() => {
      throw new Error('Vision failed');
    });
    const { reading } = await readWith(text, still);
    expect(asRead(reading.items)).toEqual(['RED OCTOBER: film/dvd']);
  });

  it('hands back the reading it was given when the cover gives no cue', async () => {
    const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
    const { vision } = engines({ [still.uri]: scene });
    const inner = new FmTextEngine({ vision, settings: () => S, newId: testIds });
    const innerReading = jest.spyOn(inner, 'read');
    const cued = withCoverCue(inner, { vision, settings: () => S });
    const { reading } = await readWith(cued, still);
    const given = await innerReading.mock.results[0].value;
    expect(reading.items).toBe(given.items); // the items are the ones it was given; only the record of what the cue saw is added
    expect(reading).toEqual({ ...given, coverCue: expect.stringContaining('fmt=none') });
  });

  describe('when the cue is late', () => {
    afterEach(() => jest.useRealTimers());

    it('waits for it no longer than the cap after the read ends, then sends the items as they were', async () => {
      jest.useFakeTimers();
      const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
      const { text, vision } = engines({ [still.uri]: { ...scene, rectangles: [slipcase(still)] } }, { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 1500 });
      jest.spyOn(vision, 'detectRectangles').mockReturnValue(new Promise<Rect[]>(() => {})); // never answers
      const streamed: ReadItem[] = [];
      let reading: Awaited<ReturnType<typeof text.read>> | undefined;
      void text.read(still, (i) => streamed.push(i), COVER).then((r) => (reading = r));
      await jest.advanceTimersByTimeAsync(1499);
      expect(reading).toBeUndefined();
      expect(streamed).toEqual([]);
      await jest.advanceTimersByTimeAsync(1);
      expect(asRead(reading?.items ?? [])).toEqual(['RED OCTOBER: film/dvd']);
      expect(asRead(streamed)).toEqual(['RED OCTOBER: film/dvd']);
    });

    it('uses a cue that arrives inside the cap', async () => {
      jest.useFakeTimers();
      const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
      const { text, vision } = engines({ [still.uri]: scene }, { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 1500 });
      jest.spyOn(vision, 'detectRectangles').mockImplementation(() => new Promise<Rect[]>((resolve) => setTimeout(() => resolve([slipcase(still)]), 1000)));
      let reading: Awaited<ReturnType<typeof text.read>> | undefined;
      void text.read(still, () => {}, COVER).then((r) => (reading = r));
      await jest.advanceTimersByTimeAsync(1000);
      expect(asRead(reading?.items ?? [])).toEqual(['RED OCTOBER: film/vhs']);
    });
  });

  describe('the record of what the cue saw, for the eval', () => {
    afterEach(() => jest.useRealTimers());

    it('says a Blu-ray-shaped case was found: the shape, its ratio, and that the cue was ready', async () => {
      const { still, scene } = realCover('bluray-01', { extract: rushmore('dvd') });
      const { text } = engines({ [still.uri]: { ...scene, rectangles: [caseOn(still, 0.86)] } });
      const { reading } = await readWith(text, still);
      expect(reading.coverCue).toMatch(/^cue=ready fmt=shape:bluray r=0\.86 cov=0\.\d\d rects=1 kind=film code=none$/);
    });

    it('says no rectangle was found', async () => {
      const { still, scene } = realCover('bluray-01', { extract: rushmore('dvd') });
      const { text } = engines({ [still.uri]: scene });
      expect((await readWith(text, still)).reading.coverCue).toContain('rects=0');
    });

    it('says a rectangle that hugs the frame was left out', async () => {
      const { still, scene } = realCover('bluray-01', { extract: rushmore('dvd') });
      const frame: Rect = { x: 0, y: 0, width: 1, height: 1 };
      const { text } = engines({ [still.uri]: { ...scene, rectangles: [frame] } });
      expect((await readWith(text, still)).reading.coverCue).toContain('rects=1 (1 frame-hugging)');
    });

    it('says the product code a game cover printed', async () => {
      const { still, scene } = realCover('game-cartridge-01', { extract: okRead([modelItem('Super Mario World', 'SUPER NINTENDO', 0, { kind: 'book' })]) });
      const { text } = engines({ [still.uri]: scene });
      expect((await readWith(text, still)).reading.coverCue).toMatch(/kind=game code=\S+/);
    });

    it('is on the picture model, the OCR-text tier and the Vision tier alike', async () => {
      const { still, scene } = realCover('bluray-01', { read: rushmore('dvd'), extract: rushmore('dvd') });
      const { model, text, pipeline } = engines({ [still.uri]: { ...scene, rectangles: [caseOn(still, 0.86)] } });
      for (const engine of [model, text, pipeline]) {
        expect((await readWith(engine, still)).reading.coverCue).toContain('fmt=shape:bluray');
      }
    });

    it('is there when the cover gave no cue at all', async () => {
      const { still, scene } = realCover('dvd-02', { extract: rushmore('dvd') });
      const { text } = engines({ [still.uri]: scene });
      expect((await readWith(text, still)).reading.coverCue).toBe('cue=ready fmt=none rects=0 kind=none code=none');
    });

    it('says the cue was late when it arrived after the read ended, inside the cap', async () => {
      jest.useFakeTimers();
      const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
      const { text, vision } = engines({ [still.uri]: scene }, { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 1500 });
      jest.spyOn(vision, 'detectRectangles').mockImplementation(() => new Promise<Rect[]>((resolve) => setTimeout(() => resolve([slipcase(still)]), 1000)));
      let reading: Awaited<ReturnType<typeof text.read>> | undefined;
      void text.read(still, () => {}, COVER).then((r) => (reading = r));
      await jest.advanceTimersByTimeAsync(1000);
      expect(reading?.coverCue).toMatch(/^cue=late fmt=shape:vhs r=0\.55 /);
    });

    it('says the cue was given up on when it never came inside the cap', async () => {
      jest.useFakeTimers();
      const { still, scene } = realCover('vhs-01', { extract: okRead([modelItem('RED OCTOBER', 'RED OCTOBER', 0, { kind: 'dvd' })]) });
      const { text, vision } = engines({ [still.uri]: scene }, { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 1500 });
      jest.spyOn(vision, 'detectRectangles').mockReturnValue(new Promise<Rect[]>(() => {})); // never answers
      let reading: Awaited<ReturnType<typeof text.read>> | undefined;
      void text.read(still, () => {}, COVER).then((r) => (reading = r));
      await jest.advanceTimersByTimeAsync(1500);
      expect(reading?.coverCue).toBe('cue=timeout');
    });

    it('is not on a shelf shot, which the cue does not look at', async () => {
      const { still, scene } = realCover('bluray-01', { extract: rushmore('book') });
      const { text } = engines({ [still.uri]: scene });
      expect((await readWith(text, still, { shotKind: 'shelf_segment' })).reading.coverCue).toBeUndefined();
    });

    it('does not change the items: the reading is what it was without the record', async () => {
      const { still, scene } = realCover('bluray-01', { extract: rushmore('dvd') });
      const { text } = engines({ [still.uri]: { ...scene, rectangles: [caseOn(still, 0.86)] } });
      const { streamed, reading } = await readWith(text, still);
      expect(reading.items).toEqual(streamed);
      expect(asRead(reading.items)).toEqual(['RUSHMORE: film/bluray']);
    });
  });

  describe('when the engine fails', () => {
    it('passes the failure on as it was, so the selector can fall to the next tier', async () => {
      const { still, scene } = realCover('bluray-01', { extract: failedRead('declined') });
      const { text } = engines({ [still.uri]: scene });
      const failure = await readWith(text, still).catch((e: unknown) => e);
      expect(failure).toBeInstanceOf(ModelUnusableError);
      expect((failure as ModelUnusableError).reason).toBe('declined');
    });

    it('does not wait for a cue that never comes', async () => {
      const { still, scene } = realCover('bluray-01', { extract: failedRead('declined') });
      const { text, vision } = engines({ [still.uri]: scene }, { ...DEFAULT_COVER_FORMAT_TUNING, cueWaitMs: 60000 });
      jest.spyOn(vision, 'detectRectangles').mockReturnValue(new Promise<Rect[]>(() => {}));
      const started = Date.now();
      await readWith(text, still).catch(() => undefined);
      expect(Date.now() - started).toBeLessThan(1000);
    });
  });

  it('says what the engine under it says, and is available when it is', async () => {
    const { vision } = engines({});
    const inner = new FmTextEngine({ vision, settings: () => S, newId: testIds });
    const cued = withCoverCue(inner, { vision, settings: () => S });
    expect(cued.name).toBe('fm_text');
    await expect(cued.isAvailable()).resolves.toBe(true);
  });
});
