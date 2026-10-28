import { DEFAULT_RECOGNITION_SETTINGS as S, promptOf, type RecognitionSettings } from './defaults';
import { EngineSelector } from './EngineSelector';
import { failedRead, FakeCropper, line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import type { ReadItem } from './types';

const books = [modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)];
const text = [line('Dune', 0.1, 0.1, 0.1, 0.5), line('Emma', 0.5, 0.1, 0.1, 0.5)];
const ok: Scene = { read: okRead(books), text };

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
    ['the model times out', undefined, { ...ok, read: failedRead('timeout', [books[0]]) }, 'timeout'],
    ['the context is exceeded', undefined, { ...ok, read: failedRead('contextExceeded') }, 'contextExceeded'],
    ['the call reports it is unavailable', undefined, { ...ok, read: failedRead('unavailable') }, 'unavailable'],
  ])('falls back to Vision text when %s, and says why', async (_name, caps, scene, why) => {
    const { selector: s } = selector(scene as Scene, caps);
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe(why);
    expect(reading.items.length).toBeGreaterThan(0);
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
    const reading = await s.read(STILL, () => {});
    expect(cropper.regions).toHaveLength(0);
    expect(reading.items).toHaveLength(4);
  });

  it('keeps the whole-shot reading when a half fails', async () => {
    const { cropper, scenes } = wide();
    scenes[`${STILL.uri}#crop2`] = { read: failedRead('declined') };
    const vision = new RecordingVision(scenes);
    const s = new EngineSelector({ vision, settings: () => smallLimit, newId: testIds, cropper });
    const reading = await s.read(STILL, () => {});
    expect(reading.engine).toBe('apple_model');
    expect(reading.items).toHaveLength(4);
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
