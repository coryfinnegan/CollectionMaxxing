// A shelf shot read by the picture model also gets the Vision pipeline's read: the two engines find different spines, and
// the model's items win where both found one. The added items are the Vision engine's own, left as it made them.
import { DEFAULT_RECOGNITION_SETTINGS as S, type RecognitionSettings } from './defaults';
import { EngineSelector } from './EngineSelector';
import { failedRead, FakeCropper, line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { VisionPipelineEngine } from './VisionPipelineEngine';
import type { ReadItem } from './types';

const spine = (name: string, x: number) => line(name, x, 0.1, 0.05, 0.6);
const column = (x: number) => ({ x: x - 0.02, y: 0.05, width: 0.09, height: 0.7 });

/** The model read two of three spines; Vision, reading the same text, splits all three. */
const shelf: Scene = {
  read: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)]),
  extract: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)]),
  text: [spine('Dune', 0.1), spine('Emma', 0.4), spine('Neuromancer', 0.7)],
  rectangles: [column(0.1), column(0.4), column(0.7)],
};

function selectorFor(scene: Scene, caps?: object, settings: RecognitionSettings = S, cropper?: FakeCropper) {
  const vision = new RecordingVision({ [STILL.uri]: scene }, caps ? { capabilities: caps as never } : undefined);
  return { vision, selector: new EngineSelector({ vision, settings: () => settings, newId: testIds, cropper }) };
}

afterEach(() => jest.restoreAllMocks());

describe('a shelf shot read by the picture model', () => {
  it('gets the Vision items the model did not read, after the model\'s own', async () => {
    const { selector } = selectorFor(shelf);
    const streamed: ReadItem[] = [];
    const reading = await selector.read(STILL, (i) => streamed.push(i), { shotKind: 'shelf_segment' });

    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    expect(streamed.map((i) => i.title)).toEqual(['Dune', 'Emma', 'Neuromancer']);
    expect(new Set(reading.items.map((i) => i.localId)).size).toBe(3);
  });

  it('keeps the model\'s item where both read the same book, with the model\'s fields', async () => {
    const { selector } = selectorFor({ ...shelf, read: okRead([modelItem('Dune', 'Dune', 0, { creator: 'Frank Herbert' }), modelItem('Emma', 'Emma', 1)]) });
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const dune = reading.items.filter((i) => i.title === 'Dune');
    expect(dune).toHaveLength(1);
    expect(dune[0]).toMatchObject({ creator: 'Frank Herbert', confidence: 'high', kind: 'book' });
  });

  it('counts a book both read once when the titles are alike but not the same', async () => {
    const scene: Scene = {
      read: okRead([modelItem('The Great Gatsby', 'THE GREAT GATSBY Fitzgerald', 0)]),
      text: [spine('GREAT GATSBY', 0.1), spine('Neuromancer', 0.4)],
      rectangles: [column(0.1), column(0.4)],
    };
    const reading = await selectorFor(scene).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items.map((i) => i.title)).toEqual(['The Great Gatsby', 'Neuromancer']);
  });

  it('leaves an added item exactly as the Vision engine alone would have made it: low confidence, its own box', async () => {
    const { vision, selector } = selectorFor(shelf);
    const combined = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const alone = await new VisionPipelineEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const added = combined.items[2];
    const twin = alone.items.find((i) => i.title === 'Neuromancer');
    expect(added).toMatchObject({ confidence: 'low', kind: 'other', title: 'Neuromancer' });
    expect(added.box).toEqual(twin?.box);
    expect(added.box).toBeDefined();
    expect(added.order).toBe(2);
  });

  it('is read the same way when the framing is not known (a snap stored before classification)', async () => {
    const reading = await selectorFor(shelf).selector.read(STILL, () => {});
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma', 'Neuromancer']);
  });

  it('reads with the model alone when asked to (the eval measures the two engines separately)', async () => {
    const { vision, selector } = selectorFor(shelf);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment', combine: false });
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
    expect(detect).not.toHaveBeenCalled();
  });

  it('adds the Vision items after the halves of a wide shot are merged', async () => {
    const wide: Record<string, Scene> = {
      [STILL.uri]: {
        read: okRead([modelItem('Alpha', 'Alpha', 0), modelItem('Bravo', 'Bravo', 1), modelItem('Delta', 'Delta', 2), modelItem('Echo', 'Echo', 3)]),
        text: [spine('Alpha', 0.05), spine('Bravo', 0.2), spine('Delta', 0.6), spine('Echo', 0.8), spine('Zulu', 0.92)],
        rectangles: [column(0.05), column(0.2), column(0.6), column(0.8), column(0.92)],
      },
      [`${STILL.uri}#crop1`]: { read: okRead([modelItem('Alpha', 'Alpha', 0), modelItem('Bravo', 'Bravo', 1)]), text: [line('Alpha', 0.1, 0.1, 0.05, 0.6), line('Bravo', 0.4, 0.1, 0.05, 0.6)] },
      [`${STILL.uri}#crop2`]: { read: okRead([modelItem('Delta', 'Delta', 0), modelItem('Echo', 'Echo', 1)]), text: [line('Delta', 0.2, 0.1, 0.05, 0.6), line('Echo', 0.6, 0.1, 0.05, 0.6)] },
    };
    const cropper = new FakeCropper();
    const small: RecognitionSettings = { ...S, splitting: { ...S.splitting, halvesWhenItemsOver: 3 } };
    const selector = new EngineSelector({ vision: new RecordingVision(wide), settings: () => small, newId: testIds, cropper });
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(cropper.regions).toHaveLength(2);
    expect(reading.items.map((i) => i.title)).toEqual(['Alpha', 'Bravo', 'Delta', 'Echo', 'Zulu']);
    expect(reading.items.map((i) => i.order)).toEqual([0, 1, 2, 3, 4]);
  });

  it('does not start the Vision read before the model has read, so the streamed items are not held up', async () => {
    const { vision, selector } = selectorFor(shelf);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const callsWhenStreamed: number[] = [];
    await selector.read(STILL, () => callsWhenStreamed.push(detect.mock.calls.length), { shotKind: 'shelf_segment' });
    // Two model items stream first, with no Vision pipeline call yet; the third (the added one) comes after it ran.
    expect(callsWhenStreamed).toEqual([0, 0, 1]);
  });

  it('reports the time the Vision read took, on top of the model\'s', async () => {
    let clock = 1000;
    const vision = new RecordingVision({ [STILL.uri]: shelf });
    const ticking = new EngineSelector({ vision, settings: () => S, newId: testIds, now: () => (clock += 100) });
    const reading = await ticking.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.timings.totalMs).toBeGreaterThan(0);
    expect(reading.timings.modelMs).toBeGreaterThan(0);
    expect(reading.timings.visionMs).toBe(reading.timings.totalMs - (reading.timings.modelMs ?? 0));
  });
});

describe('a shelf shot where the Vision read adds nothing', () => {
  it('returns the model reading unchanged when Vision found only what the model had', async () => {
    const { selector } = selectorFor({ ...shelf, text: shelf.text?.slice(0, 2), rectangles: shelf.rectangles?.slice(0, 2) });
    const withVision = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const alone = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment', combine: false });
    expect(withVision.items).toEqual(alone.items.map((i) => ({ ...i, localId: expect.any(String) })));
    expect(withVision.unreadableCount).toBe(alone.unreadableCount);
    expect(withVision.timings.totalMs).toBeGreaterThanOrEqual(0);
  });

  it('returns the model reading when the Vision read throws', async () => {
    jest.spyOn(VisionPipelineEngine.prototype, 'read').mockRejectedValue(new Error('vision broke'));
    const reading = await selectorFor(shelf).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('apple_model');
    expect(reading.fellBackFrom).toBeUndefined();
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
  });

  it('returns the model reading when Vision text cannot be read at all', async () => {
    const { vision, selector } = selectorFor(shelf);
    jest.spyOn(vision, 'recognizeText').mockRejectedValue(new Error('no text'));
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('apple_model');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
  });

  it('does not let a failing item callback fail the snap', async () => {
    let calls = 0;
    const reading = await selectorFor(shelf).selector.read(
      STILL,
      () => {
        calls += 1;
        if (calls === 3) throw new Error('store broke');
      },
      { shotKind: 'shelf_segment' },
    );
    expect(reading.engine).toBe('apple_model');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
  });
});

describe('shots and engines the combination leaves alone', () => {
  it.each(['single_cover', 'loose_item'] as const)('does not run the Vision pipeline on a %s shot', async (shotKind) => {
    const { vision, selector } = selectorFor(shelf);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind });
    expect(detect).toHaveBeenCalledTimes(1); // the look at the case's shape (coverFormat); the pipeline would ask again
    expect(reading.engine).toBe('apple_model');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']); // the model's items alone, no Neuromancer
  });

  it('leaves the fm_text tier as it was on a phone whose model cannot see pictures', async () => {
    const textOnly = { available: true, vision: false, guidedGeneration: true, contextSize: 4096 };
    const { vision, selector } = selectorFor(shelf, textOnly);
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('fm_text');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
    expect(detect).not.toHaveBeenCalled();
  });

  it('leaves the fm_text tier as it was when the picture read fails', async () => {
    const reading = await selectorFor({ ...shelf, read: failedRead('declined') }).selector.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('fm_text');
    expect(reading.fellBackFrom).toBe('declined');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma']);
  });

  it('leaves the Vision fallback as it was when no model tier answers: Vision items only, once', async () => {
    const reading = await selectorFor({ ...shelf, read: failedRead('declined'), extract: failedRead('declined') }).selector.read(STILL, () => {}, {
      shotKind: 'shelf_segment',
    });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe('declined');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune', 'Emma', 'Neuromancer']);
  });
});
