// A shelf shot is read by the Vision pipeline first: its items stream at once. The picture model then reads, and adds only the items
// it found in a place where Vision found nothing. The same place is the same item, whatever the two engines called it (combine.ts).
import type { Rect } from '@/native/cm-vision';
import { DEFAULT_RECOGNITION_SETTINGS as S, type RecognitionSettings } from './defaults';
import { EngineSelector } from './EngineSelector';
import { failedRead, FakeCropper, line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { VisionPipelineEngine } from './VisionPipelineEngine';
import type { ReadItem } from './types';

const spine = (name: string, x: number) => line(name, x, 0.1, 0.05, 0.6);
const column = (x: number): Rect => ({ x: x - 0.02, y: 0.05, width: 0.09, height: 0.7 });
const titles = (items: ReadItem[]) => items.map((i) => i.title);

/** The model read two of three spines; Vision, reading the same text, splits all three. */
const shelf: Scene = {
  read: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)]),
  extract: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)]),
  text: [spine('Dune', 0.1), spine('Emma', 0.4), spine('Neuromancer', 0.7)],
  rectangles: [column(0.1), column(0.4), column(0.7)],
};
/** What the model reads that is nowhere in the picture's text: no Vision line to give it a box. */
const ubik = modelItem('Ubik', 'Ubik', 2);

function selectorFor(scene: Scene, caps?: object, settings: RecognitionSettings = S, cropper?: FakeCropper) {
  const vision = new RecordingVision({ [STILL.uri]: scene }, caps ? { capabilities: caps as never } : undefined);
  return { vision, selector: new EngineSelector({ vision, settings: () => settings, newId: testIds, cropper }) };
}

/** Makes the Vision pipeline read exactly these items, so a test can put them where it wants them. */
function visionReads(items: ReadItem[]) {
  return jest.spyOn(VisionPipelineEngine.prototype, 'read').mockImplementation(async (_still, onItem) => {
    items.forEach(onItem);
    return { items, unreadableCount: 0, engine: 'vision_pipeline', timings: { totalMs: 1, visionMs: 1 } };
  });
}
const visionItem = (title: string, box: Rect, order: number): ReadItem => ({
  localId: `vision-${order}`,
  text: title,
  title,
  kind: 'other',
  box,
  order,
  confidence: 'low',
});

afterEach(() => jest.restoreAllMocks());

describe('a shelf shot read by the picture model', () => {
  it('streams the Vision items first, in order, before the model has read anything, then the items the model adds', async () => {
    const { vision, selector } = selectorFor({ ...shelf, read: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1), ubik]) });
    const streamed: string[] = [];
    const modelReadsStarted: number[] = [];
    const reading = await selector.read(
      STILL,
      (i) => {
        streamed.push(i.title);
        modelReadsStarted.push(vision.readCalls.length);
      },
      { shotKind: 'shelf_segment' },
    );

    expect(streamed).toEqual(['Dune', 'Emma', 'Neuromancer', 'Ubik']);
    expect(modelReadsStarted).toEqual([0, 0, 0, 1]); // the three Vision items went out before the model was even asked
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer', 'Ubik']);
    expect(reading.items.map((i) => i.order)).toEqual([0, 1, 2, 3]);
    expect(new Set(reading.items.map((i) => i.localId)).size).toBe(4);
  });

  it('is still the model\'s snap when it ran: apple_model, no fallback', async () => {
    const reading = await selectorFor(shelf).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
  });

  it('keeps Vision\'s item where both read the same book, with Vision\'s fields', async () => {
    const { selector } = selectorFor({ ...shelf, read: okRead([modelItem('Dune', 'Dune', 0, { creator: 'Frank Herbert' }), modelItem('Emma', 'Emma', 1)]) });
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const dune = reading.items.filter((i) => i.title === 'Dune');
    expect(dune).toHaveLength(1);
    expect(dune[0]).toMatchObject({ confidence: 'low', kind: 'other' });
    expect(dune[0].creator).toBeUndefined();
  });

  it('does not add a model item that sits where a Vision item does, however the model titled it', async () => {
    const scene: Scene = {
      read: okRead([modelItem('A Completely Different Title', 'GREAT GATSBY', 0), modelItem('Neuromancer', 'Neuromancer', 1)]),
      text: [spine('GREAT GATSBY', 0.1), spine('Neuromancer', 0.4)],
      rectangles: [column(0.1), column(0.4)],
    };
    const reading = await selectorFor(scene).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(titles(reading.items)).toEqual(['GREAT GATSBY', 'Neuromancer']);
  });

  it('adds a model item that sits in a new place, as the model read it, and streams it after the Vision items', async () => {
    visionReads([visionItem('Dune', column(0.1), 0), visionItem('Emma', column(0.4), 1)]);
    const { selector } = selectorFor({ ...shelf, read: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1), modelItem('Neuromancer', 'Neuromancer', 2, { creator: 'William Gibson' })]) });
    const streamed: ReadItem[] = [];
    const reading = await selector.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment' });

    expect(titles(streamed)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    const added = reading.items[2];
    expect(added).toMatchObject({ creator: 'William Gibson', confidence: 'high', kind: 'book', order: 2 });
    expect(added.box?.x).toBe(0.7); // the line the model's text matched, not a Vision piece
    expect(streamed[2].localId).toBe(added.localId);
  });

  it('adds a model item whose title a Vision item has when it sits in a different place', async () => {
    visionReads([visionItem('Dune', column(0.1), 0)]);
    const { selector } = selectorFor({
      ...shelf,
      read: okRead([modelItem('Dune', 'Dune', 0), modelItem('Dune', 'Dune', 1)]),
      text: [spine('Dune', 0.1), spine('Dune', 0.7)],
    });
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items.map((i) => [i.title, i.box?.x])).toEqual([['Dune', column(0.1).x], ['Dune', 0.7]]);
  });

  it('keeps two Vision items with the same title in different places, as two items', async () => {
    const scene: Scene = {
      read: okRead([modelItem('HIGHLANDER', 'HIGHLANDER', 0)]),
      text: [spine('HIGHLANDER', 0.1), spine('HIGHLANDER', 0.4)],
      rectangles: [column(0.1), column(0.4)],
    };
    const streamed: ReadItem[] = [];
    const reading = await selectorFor(scene).selector.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment' });
    expect(reading.items.map((i) => [i.title, i.box?.x])).toEqual([['HIGHLANDER', column(0.1).x], ['HIGHLANDER', column(0.4).x]]);
    expect(titles(streamed)).toEqual(['HIGHLANDER', 'HIGHLANDER']);
  });

  it('keeps five copies of one title on a shelf when the model read them all too', async () => {
    const xs = [0.05, 0.2, 0.35, 0.5, 0.65];
    const scene: Scene = {
      read: okRead(xs.map((_, i) => modelItem('HIGHLANDER', 'HIGHLANDER', i))),
      text: xs.map((x) => spine('HIGHLANDER', x)),
      rectangles: xs.map(column),
    };
    const reading = await selectorFor(scene).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items).toHaveLength(5);
    expect(new Set(reading.items.map((i) => i.box?.x)).size).toBe(5);
  });

  it('is read the same way when the framing is not known (a snap stored before classification)', async () => {
    const reading = await selectorFor(shelf).selector.read(STILL, () => {});
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer']);
  });

  it('reads with the model alone when asked to (the eval measures the two engines separately)', async () => {
    const { vision, selector } = selectorFor(shelf);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment', combine: false });
    expect(titles(reading.items)).toEqual(['Dune', 'Emma']);
    expect(detect).not.toHaveBeenCalled();
  });

  describe('a wide shot read in halves', () => {
    // Six lines near the cut at 0.425: the left half is 0..0.475 of the picture, the right half 0.375..1. Each line, seen in a half, is where it is in the picture.
    const LEFT: Rect = { x: 0, y: 0, width: 0.475, height: 1 };
    const RIGHT: Rect = { x: 0.375, y: 0, width: 0.625, height: 1 };
    const within = (region: Rect, name: string, x: number) => line(name, (x - region.x) / region.width, 0.1, 0.05 / region.width, 0.6);
    const wide = (whole: ReturnType<typeof modelItem>[]): Record<string, Scene> => ({
      [STILL.uri]: {
        read: okRead(whole),
        text: [spine('Alpha', 0.05), spine('Bravo', 0.2), spine('Delta', 0.6), spine('Echo', 0.8), spine('Zulu', 0.92)],
        rectangles: [column(0.05), column(0.2), column(0.6), column(0.8), column(0.92)],
      },
      [`${STILL.uri}#crop1`]: { read: okRead([modelItem('Alpha', 'Alpha', 0), modelItem('Bravo', 'Bravo', 1)]), text: [within(LEFT, 'Alpha', 0.05), within(LEFT, 'Bravo', 0.2)] },
      [`${STILL.uri}#crop2`]: { read: okRead([modelItem('Delta', 'Delta', 0), modelItem('Echo', 'Echo', 1)]), text: [within(RIGHT, 'Delta', 0.6), within(RIGHT, 'Echo', 0.8)] },
    });
    const small: RecognitionSettings = { ...S, splitting: { ...S.splitting, halvesWhenItemsOver: 3 } };
    const whole = [modelItem('Alpha', 'Alpha', 0), modelItem('Bravo', 'Bravo', 1), modelItem('Delta', 'Delta', 2), modelItem('Echo', 'Echo', 3)];

    it('still reads the halves, and the Vision items stay what the reading is made of', async () => {
      const cropper = new FakeCropper();
      const selector = new EngineSelector({ vision: new RecordingVision(wide(whole)), settings: () => small, newId: testIds, cropper });
      const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
      expect(cropper.regions).toHaveLength(2);
      expect(reading.engine).toBe('apple_model');
      expect(titles(reading.items)).toEqual(['Alpha', 'Bravo', 'Delta', 'Echo', 'Zulu']);
      expect(reading.items.map((i) => i.order)).toEqual([0, 1, 2, 3, 4]);
      expect(reading.items.every((i) => i.confidence === 'low')).toBe(true);
    });

    it('adds the model\'s item from a half when Vision found nothing in that place', async () => {
      visionReads([visionItem('Alpha', column(0.05), 0), visionItem('Bravo', column(0.2), 1), visionItem('Echo', column(0.8), 2)]);
      const cropper = new FakeCropper();
      const selector = new EngineSelector({ vision: new RecordingVision(wide(whole)), settings: () => small, newId: testIds, cropper });
      const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
      expect(titles(reading.items)).toEqual(['Alpha', 'Bravo', 'Echo', 'Delta']);
      expect(reading.items[3]).toMatchObject({ confidence: 'high', kind: 'book', order: 3 });
    });
  });

  it('reports the time the model took, and the rest as Vision\'s', async () => {
    let clock = 1000;
    const vision = new RecordingVision({ [STILL.uri]: shelf });
    const ticking = new EngineSelector({ vision, settings: () => S, newId: testIds, now: () => (clock += 100) });
    const reading = await ticking.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.timings.totalMs).toBeGreaterThan(0);
    expect(reading.timings.modelMs).toBeGreaterThan(0);
    expect(reading.timings.visionMs).toBe(reading.timings.totalMs - (reading.timings.modelMs ?? 0));
  });

  it('counts the items the model could not read, for the "Get closer" chip', async () => {
    const reading = await selectorFor({ ...shelf, read: okRead([modelItem('Dune', 'Dune', 0)], 3) }).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.unreadableCount).toBe(3);
  });
});

describe('a shelf shot where the model adds nothing new', () => {
  it('returns the Vision reading as it is when the model found only what Vision had', async () => {
    const { selector } = selectorFor(shelf);
    const combined = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const alone = await selector.tiers.pipeline.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(combined.items).toEqual(alone.items.map((i) => ({ ...i, localId: expect.any(String) })));
    expect(combined.unreadableCount).toBe(alone.unreadableCount);
    expect(combined.engine).toBe('apple_model');
  });

  it('returns the model reading when the Vision read throws', async () => {
    jest.spyOn(VisionPipelineEngine.prototype, 'read').mockRejectedValue(new Error('vision broke'));
    const streamed: ReadItem[] = [];
    const reading = await selectorFor(shelf).selector.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(titles(reading.items)).toEqual(['Dune', 'Emma']);
    expect(titles(streamed)).toEqual(['Dune', 'Emma']);
    expect(reading.items.map((i) => i.order)).toEqual([0, 1]);
  });

  it('returns the model reading when Vision text cannot be read at all', async () => {
    const { vision, selector } = selectorFor(shelf);
    jest.spyOn(vision, 'recognizeText').mockRejectedValue(new Error('no text'));
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('apple_model');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma']);
  });

  it('does not let a failing item callback fail the snap, or lose an item', async () => {
    const reading = await selectorFor({ ...shelf, read: okRead([modelItem('Dune', 'Dune', 0), ubik]) }).selector.read(
      STILL,
      () => {
        throw new Error('store broke');
      },
      { shotKind: 'shelf_segment' },
    );
    expect(reading.engine).toBe('apple_model');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer', 'Ubik']);
  });
});

describe('a shelf shot where the model gives no reading', () => {
  const nothing = { ...shelf, read: failedRead('timeout'), extract: failedRead('timeout') };

  it('keeps the Vision reading as the snap, says the model timed out, and does not stream or read Vision twice', async () => {
    const { vision, selector } = selectorFor(nothing);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const streamed: ReadItem[] = [];
    const reading = await selector.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe('timeout');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    expect(titles(streamed)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    expect(detect).toHaveBeenCalledTimes(1);
    expect(reading.timings.totalMs).toBeGreaterThanOrEqual(reading.timings.visionMs);
  });

  it.each(['declined', 'contextExceeded', 'unavailable'] as const)('says why when the model %s', async (why) => {
    const reading = await selectorFor({ ...shelf, read: failedRead(why), extract: failedRead(why) }).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe(why);
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer']);
  });

  it('keeps what the model had read when it ran out of time part way, as the model\'s snap', async () => {
    const reading = await selectorFor({ ...shelf, read: failedRead('timeout', [ubik]) }).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer', 'Ubik']);
  });

  it('adds what the OCR-text tier reads when the picture read fails, as that tier\'s snap with the picture failure as the reason', async () => {
    const extract = okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1), ubik]);
    const streamed: ReadItem[] = [];
    const reading = await selectorFor({ ...shelf, read: failedRead('declined'), extract }).selector.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('fm_text');
    expect(reading.fellBackFrom).toBe('declined');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer', 'Ubik']);
    expect(titles(streamed)).toEqual(['Dune', 'Emma', 'Neuromancer', 'Ubik']);
  });

  it('fails the snap, as before, when Vision and every model tier fail', async () => {
    jest.spyOn(VisionPipelineEngine.prototype, 'read').mockRejectedValue(new Error('vision broke'));
    await expect(selectorFor(nothing).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' })).rejects.toThrow('vision broke');
  });
});

describe('shots and engines the combination leaves alone', () => {
  it.each(['single_cover', 'loose_item'] as const)('does not run the Vision pipeline on a %s shot', async (shotKind) => {
    const { vision, selector } = selectorFor(shelf);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind });
    expect(detect).toHaveBeenCalledTimes(1); // the look at the case's shape (coverFormat); the pipeline would ask again
    expect(reading.engine).toBe('apple_model');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma']); // the model's items alone, no Neuromancer
  });

  it('streams a single cover\'s items as the model reads them', async () => {
    const streamed: ReadItem[] = [];
    await selectorFor(shelf).selector.read(STILL, (i) => streamed.push(i), { shotKind: 'single_cover' });
    expect(titles(streamed)).toEqual(['Dune', 'Emma']);
  });

  it('leaves the fm_text tier as it was on a phone whose model cannot see pictures', async () => {
    const textOnly = { available: true, vision: false, guidedGeneration: true, contextSize: 4096 };
    const { vision, selector } = selectorFor(shelf, textOnly);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('fm_text');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma']);
    expect(detect).not.toHaveBeenCalled();
  });

  it('leaves the Vision fallback as it was when the model is off: Vision items only, once', async () => {
    const off = { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' };
    const { vision, selector } = selectorFor(shelf, off);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe('unavailable');
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    expect(detect).toHaveBeenCalledTimes(1);
  });
});
