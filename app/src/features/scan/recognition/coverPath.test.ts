// #504: a cover shot reads as one item whatever the geometry; a shelf shot still splits per spine.
import type { Rect } from '@/native/cm-vision';
import { DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { AppleModelEngine } from './AppleModelEngine';
import { EngineSelector } from './EngineSelector';
import { line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { VisionPipelineEngine } from './VisionPipelineEngine';

// The repro's cover: six OCR line-clusters under six nested quads (the cover's edge, an art
// frame, a photo inset, a title block, a quote block, a badge).
const coverLines = [
  line('Author of How to Be a Domestic Goddess', 0.2, 0.08, 0.6, 0.06),
  line('LANTERN', 0.35, 0.2, 0.3, 0.08),
  line('LAVSON', 0.36, 0.28, 0.28, 0.07),
  line('LANTERN ROAD', 0.25, 0.35, 0.5, 0.1),
  line('Potter', 0.3, 0.55, 0.4, 0.06),
  line('ELOST LSOT', 0.4, 0.7, 0.2, 0.05),
];
const coverRects: Rect[] = [
  { x: 0.05, y: 0.05, width: 0.9, height: 0.9 },
  { x: 0.1, y: 0.06, width: 0.8, height: 0.75 },
  { x: 0.15, y: 0.3, width: 0.7, height: 0.4 },
  { x: 0.2, y: 0.07, width: 0.6, height: 0.1 },
  { x: 0.25, y: 0.5, width: 0.5, height: 0.15 },
  { x: 0.3, y: 0.65, width: 0.4, height: 0.12 },
];
const coverScene: Scene = { text: coverLines, rectangles: coverRects };

// Six spines, disjoint quads in one row.
const shelfLines = Array.from({ length: 6 }, (_, i) => line(`Title ${i}`, 0.04 + i * 0.15, 0.1, 0.1, 0.6));
const shelfRects: Rect[] = Array.from({ length: 6 }, (_, i) => ({ x: 0.02 + i * 0.15, y: 0.05, width: 0.13, height: 0.8 }));
const shelfScene: Scene = { text: shelfLines, rectangles: shelfRects };

const modelOff = { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' };

const pipelineOf = (scene: Scene) =>
  new VisionPipelineEngine({ vision: new RecordingVision({ [STILL.uri]: scene }), settings: () => S, newId: testIds });

describe('the Vision pipeline routes on the shot kind', () => {
  it('reads a cover shot as one item even when the geometry would split', async () => {
    const reading = await pipelineOf(shelfScene).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].text).toContain('Title 0');
    expect(reading.items[0].text).toContain('Title 5');
  });

  it('reads a loose item as one item', async () => {
    const reading = await pipelineOf(shelfScene).read(STILL, () => {}, { shotKind: 'loose_item' });
    expect(reading.items).toHaveLength(1);
  });

  it('still splits a shelf shot per spine', async () => {
    const reading = await pipelineOf(shelfScene).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items).toHaveLength(6);
  });

  it.each([['unknown' as const], [undefined]])('segments a %s shot like a shelf', async (shotKind) => {
    const reading = await pipelineOf(shelfScene).read(STILL, () => {}, shotKind === undefined ? {} : { shotKind });
    expect(reading.items).toHaveLength(6);
  });

  it('#504: a shelf-labelled cover collapses to one item instead of one shard per quad', async () => {
    const reading = await pipelineOf(coverScene).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].text).toContain('LANTERN ROAD');
  });
});

describe('the Apple model is asked for one item on a cover shot', () => {
  const scene: Scene = {
    read: okRead([modelItem('Dune', 'Dune', 0), modelItem('Emma', 'Emma', 1)]),
    text: [line('Dune', 0.1, 0.1, 0.1, 0.5), line('Emma', 0.5, 0.1, 0.1, 0.5)],
  };

  it('caps the ask at one item for a cover shot', async () => {
    const vision = new RecordingVision({ [STILL.uri]: scene });
    await new AppleModelEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(vision.readCalls[0].options.maxItems).toBe(1);
  });

  it('asks for the full snap on a shelf shot', async () => {
    const vision = new RecordingVision({ [STILL.uri]: scene });
    await new AppleModelEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(vision.readCalls[0].options.maxItems).toBe(S.engine.maxItemsPerSnap);
  });
});

describe('the selector routes the fallback read on the shot kind', () => {
  const selectorOf = (scene: Scene) =>
    new EngineSelector({ vision: new RecordingVision({ [STILL.uri]: scene }, { capabilities: modelOff as never }), settings: () => S, newId: testIds });

  it('#504: the repro path (model unavailable, shelf-labelled cover) yields one item', async () => {
    const reading = await selectorOf(coverScene).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.fellBackFrom).toBe('unavailable');
    expect(reading.items).toHaveLength(1);
  });

  it('a cover shot falls back to one item', async () => {
    const reading = await selectorOf(shelfScene).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.items).toHaveLength(1);
  });

  it('a shelf shot still falls back to per-spine items', async () => {
    const reading = await selectorOf(shelfScene).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.engine).toBe('vision_pipeline');
    expect(reading.items).toHaveLength(6);
  });
});
