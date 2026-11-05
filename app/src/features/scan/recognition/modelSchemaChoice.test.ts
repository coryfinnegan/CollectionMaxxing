// Which shape the picture model is asked to write: the lean spine only for a shelf of spines, the full item for everything else
// (a cover framed as a segment must keep its platform, edition and completeness; a multi-cover shot is covers).
import type { Rect } from '@/native/cm-vision';
import { DEFAULT_RECOGNITION_SETTINGS as S, SPINE_SCHEMA_MIN_PIECES } from './defaults';
import { EngineSelector } from './EngineSelector';
import { failedRead, line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { union } from './geometry';
import coverReads from './coverReads.json';
import multiCoverReads from './multiCoverReads.json';
import shelfReads from './shelfReads.json';
import type { ReadOptions } from './types';

const PHOTO = multiCoverReads['game-box-multiple-01'];
const PHOTO_STILL = { uri: 'fixture://game-box-multiple-01', width: PHOTO.width, height: PHOTO.height };
const PHOTO_LINES = PHOTO.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height));
// The three game boxes' lines, as multiCover.test.ts groups them; each case's rectangle is its text grown a little.
const BOXES = [
  [18, 17, 16, 9, 12, 14, 8],
  [5, 7, 15, 6, 13, 3, 10, 11, 2, 1, 4, 0],
  [41, 45, 43, 37, 27, 44, 46, 42, 24, 25, 23, 19, 28, 21, 30, 20, 26, 29, 36, 31, 35, 38, 33, 32, 34, 39, 40, 22],
];
const grown = (indexes: number[]): Rect => {
  const box = union(indexes.map((i) => PHOTO_LINES[i].box));
  return { x: box.x - 0.03, y: box.y - 0.04, width: box.width + 0.06, height: box.height + 0.08 };
};

async function schemaAsked(scene: Scene, still = STILL, options: ReadOptions = { shotKind: 'shelf_segment' }) {
  const vision = new RecordingVision({ [still.uri]: scene });
  await new EngineSelector({ vision, settings: () => S, newId: testIds }).read(still, () => {}, options);
  return vision.readCalls[0].options.schema ?? 'full';
}

type Recorded = { width: number; height: number; lines: { text: string; x: number; y: number; width: number; height: number }[] };
const recordedCovers = coverReads as unknown as Record<string, Recorded>;
const recordedShelves = shelfReads as unknown as Record<string, Recorded>;
const recordedLines = (r: Recorded, factor = 1) =>
  r.lines.map((l) => line(l.text, 0.5 + (l.x - 0.5) * factor, 0.5 + (l.y - 0.5) * factor, l.width * factor, l.height * factor));
// One flat adjacent spine line at the left edge of the frame.
const sideSpine = () => line('SIDE SPINE TITLE', 0.01, 0.1, 0.04, 0.6);

/** How many pieces the selector's own split made of a scene (the number the schema rule counts). */
async function piecesOf(scene: Scene, still: { uri: string; width: number; height: number }) {
  // A declined model read hands back Vision's own reading, which carries the split.
  const vision = new RecordingVision({ [still.uri]: { ...scene, read: failedRead('declined'), extract: failedRead('declined') } });
  const result = await new EngineSelector({ vision, settings: () => S, newId: testIds }).read(still, () => {}, { shotKind: 'shelf_segment' });
  return result.ocr?.pieces.length ?? 0;
}
const stillOf = (name: string, r: Recorded) => ({ uri: `fixture://${name}`, width: r.width, height: r.height });

const spines = (n: number) => Array.from({ length: n }, (_, i) => line(`TITLE ${i}`, 0.05 + i * 0.09, 0.1, 0.05, 0.6));
const read = okRead([modelItem('Title 0', 'TITLE 0', 0)]);

describe('the shape the picture model is asked to write', () => {
  it('a ten-piece shelf asks for the lean spine', async () => {
    expect(await schemaAsked({ text: spines(10), read })).toBe('spine');
  });

  it('is only the lean spine from the configured number of pieces', async () => {
    expect(await schemaAsked({ text: spines(SPINE_SCHEMA_MIN_PIECES - 1), read })).toBe('full');
    expect(await schemaAsked({ text: spines(SPINE_SCHEMA_MIN_PIECES), read })).toBe('spine');
  });

  it('a one-piece cover framed as a segment asks for the full item', async () => {
    const cover = [line('HALO', 0.2, 0.1, 0.5, 0.2), line('XBOX', 0.3, 0.4, 0.3, 0.1)];
    expect(await schemaAsked({ text: cover, read })).toBe('full');
  });

  it('a multi-cover shot asks for the full item even when its pieces reach the spine threshold', async () => {
    // Spines of a shelf at the foot of the frame, outside every cover rectangle, lift the piece count to the threshold.
    const shelf = Array.from({ length: 9 }, (_, i) => line(`SHELF TITLE ${i}`, 0.04 + i * 0.1, 0.9, 0.05, 0.09));
    const scene = { text: [...PHOTO_LINES, ...shelf], rectangles: BOXES.map(grown), read };
    expect(await piecesOf(scene, PHOTO_STILL)).toBeGreaterThanOrEqual(SPINE_SCHEMA_MIN_PIECES);
    expect(await schemaAsked(scene, PHOTO_STILL)).toBe('full');
  });

  it('single shots ask for the full item', async () => {
    expect(await schemaAsked({ text: spines(10), read }, STILL, { shotKind: 'single_cover' })).toBe('full');
    expect(await schemaAsked({ text: spines(10), read }, STILL, { shotKind: 'single_cover', combine: false })).toBe('full');
  });

  const coverCases = Object.entries(recordedCovers).flatMap(([name, r]) =>
    [1, 0.7, 0.6].flatMap((factor) =>
      [false, true].map((withSideSpine) => ({ name, r, factor, withSideSpine })),
    ),
  );

  it.each(coverCases)('recorded cover $name at $factor framing (side spine: $withSideSpine) asks for the full item', async ({ name, r, factor, withSideSpine }) => {
    const text = [...recordedLines(r, factor), ...(withSideSpine ? [sideSpine()] : [])];
    expect(await schemaAsked({ text, read }, stillOf(name, r))).toBe('full');
  });

  it.each(Object.entries(recordedShelves))('recorded shelf %s still asks for the lean spine', async (name, r) => {
    expect(await schemaAsked({ text: recordedLines(r), read }, stillOf(name, r))).toBe('spine');
  });

  it('keeps a gap between the busiest cover and the emptiest shelf', async () => {
    const covers: number[] = [];
    for (const [name, r] of Object.entries(recordedCovers)) {
      for (const factor of [1, 0.7, 0.6]) {
        for (const extra of [[], [sideSpine()]]) {
          covers.push(await piecesOf({ text: [...recordedLines(r, factor), ...extra], read }, stillOf(name, r)));
        }
      }
    }
    const shelves: number[] = [];
    for (const [name, r] of Object.entries(recordedShelves)) shelves.push(await piecesOf({ text: recordedLines(r), read }, stillOf(name, r)));
    const coverMax = Math.max(...covers);
    const shelfMin = Math.min(...shelves);
    expect(SPINE_SCHEMA_MIN_PIECES).toBeGreaterThan(coverMax);
    expect(SPINE_SCHEMA_MIN_PIECES).toBeLessThanOrEqual(shelfMin);
  });
});
