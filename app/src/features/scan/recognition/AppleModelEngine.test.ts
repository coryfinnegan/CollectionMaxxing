// How the picture reader handles the lean shape the native module now generates: a spine with only title,
// creator, kind and confidence, whose visibleText is its title, and a cover without the tagline text.
import { AppleModelEngine } from './AppleModelEngine';
import { DEFAULT_RECOGNITION_SETTINGS } from './defaults';
import { line, modelItem, okRead, RecordingVision, STILL, testIds } from './fakes';
import type { ReadItem } from './types';

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

  it('a shelf read asks for the full snap, so the native side picks the spine shape', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead([modelItem('Dune', 'Dune', 0)]) } });
    await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(vision.readCalls[0].options.maxItems).toBe(DEFAULT_RECOGNITION_SETTINGS.engine.maxItemsPerSnap);
  });

  it('a single cover asks for one item, so the native side picks the cover shape', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead([modelItem('Dune', 'Dune', 0)]) } });
    await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(vision.readCalls[0].options.maxItems).toBe(1);
  });

  it('a single cover whose visibleText is its title still gets its title line box', async () => {
    const cover = modelItem('Dune', 'Dune', 0, { creator: 'Frank Herbert', editionMarks: "Collector's Edition", completeness: 'boxed' });
    const vision = new RecordingVision({ [STILL.uri]: { text: lines, read: okRead([cover]) } });
    const reading = await new AppleModelEngine(deps(vision)).read(STILL, () => {}, { shotKind: 'single_cover' });
    const [item] = reading.items;
    expect(item.box).toBeDefined();
    expect(item.edition).toBe("Collector's Edition");
    expect(item.completeness).toBeDefined();
  });
});
