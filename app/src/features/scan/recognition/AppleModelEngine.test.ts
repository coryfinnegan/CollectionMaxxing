// How the picture reader handles the lean shape the native module now generates: a spine with only title,
// creator, kind and confidence, whose visibleText is its title.
import { AppleModelEngine } from './AppleModelEngine';
import { DEFAULT_RECOGNITION_SETTINGS } from './defaults';
import { EngineSelector } from './EngineSelector';
import { failedRead, line, modelItem, okRead, RecordingVision, STILL, testIds } from './fakes';
import { ModelUnusableError, type ReadItem } from './types';

const deps = (vision: RecordingVision) => ({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds });
const lines = [line('DUNE', 0.1, 0.1, 0.3, 0.1), line('FRANK HERBERT', 0.1, 0.3, 0.3, 0.05), line('EMMA', 0.5, 0.1, 0.3, 0.1)];

describe('AppleModelEngine with lean items', () => {
  it('a shelf item with only title, creator, kind and confidence takes its title line box and streams', async () => {
    const lean = [modelItem('Dune', 'Dune', 0, { creator: 'Frank Herbert' }), modelItem('Emma', 'Emma', 1, { confidence: 'medium' })];
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead(lean) } });
    const streamed: ReadItem[] = [];
    const reading = await new AppleModelEngine(deps(vision)).read(STILL, (item) => streamed.push(item), { shotKind: 'shelf_segment' });

    expect(streamed.map((i) => i.title)).toEqual(['Dune', 'Emma']);
    const [dune, emma] = reading.items;
    expect(dune.box).toBeDefined();
    expect(dune.creator).toBe('Frank Herbert');
    expect(dune.kind).toBe('book');
    expect(emma.box).toBeDefined();
    expect(emma.confidence).toBe('medium');
    expect(emma.platform).toBeUndefined();
    expect(emma.edition).toBeUndefined();
    expect(emma.completeness).toBeUndefined();
    expect(dune.box).not.toEqual(emma.box);
  });

  it('passes the shape it was asked for on to the native read, and sends nothing (the full item) when none was asked for', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead([modelItem('Dune', 'Dune', 0)]) } });
    const engine = new AppleModelEngine(deps(vision));
    await engine.read(STILL, () => {}, { shotKind: 'shelf_segment', modelSchema: 'spine' });
    await engine.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(vision.readCalls.map((c) => c.options.schema)).toEqual(['spine', undefined]);
  });

  it('a cover keeps its visibleText line box, edition and completeness', async () => {
    const cover = modelItem('Dune', 'Dune Frank Herbert', 0, { creator: 'Frank Herbert', editionMarks: "Collector's Edition", completeness: 'boxed' });
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead([cover]) } });
    const reading = await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'single_cover' });
    const [item] = reading.items;
    expect(item.box).toBeDefined();
    expect(item.edition).toBe("Collector's Edition");
    expect(item.completeness).toBeDefined();
  });
});

describe('AppleModelEngine running out of time', () => {
  const dune = [modelItem('Dune', 'Dune', 0)];

  it('a picture read that ran out of time keeps its finished items and says it timed out', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: failedRead('timeout', dune) } });
    const reading = await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items.map((i) => i.title)).toEqual(['Dune']);
    expect(reading.timedOut).toBe(true);
  });

  it('a read that finished in time says nothing about time', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead(dune) } });
    const reading = await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading).not.toHaveProperty('timedOut');
  });

  it('a read that ran out of time with nothing read still falls back', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: failedRead('timeout') } });
    const error = await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'shelf_segment' }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ModelUnusableError);
  });

  it('a timed-out single cover read through the selector still carries timedOut', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: failedRead('timeout', dune) } });
    const selector = new EngineSelector({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds });
    const reading = await selector.read(STILL, () => {}, { shotKind: 'single_cover', combine: false });
    expect(reading.engine).toBe('apple_model');
    expect(reading.items.map((i) => i.title)).toEqual(['Dune']);
    expect(reading.timedOut).toBe(true);
  });
});
