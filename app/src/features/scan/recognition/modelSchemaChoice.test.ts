// Which shape the picture model is asked to write: the lean spine only for a shelf of spines, the full item for everything else
// (a cover framed as a segment must keep its platform, edition and completeness; a multi-cover shot is covers).
import type { Rect } from '@/native/cm-vision';
import { DEFAULT_RECOGNITION_SETTINGS as S, SPINE_SCHEMA_MIN_PIECES } from './defaults';
import { EngineSelector } from './EngineSelector';
import { line, modelItem, okRead, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { union } from './geometry';
import multiCoverReads from './multiCoverReads.json';
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

  it('a multi-cover shot asks for the full item', async () => {
    const scene = { text: PHOTO_LINES, rectangles: BOXES.map(grown), read };
    expect(await schemaAsked(scene, PHOTO_STILL)).toBe('full');
  });

  it('single shots ask for the full item', async () => {
    expect(await schemaAsked({ text: spines(10), read }, STILL, { shotKind: 'single_cover' })).toBe('full');
    expect(await schemaAsked({ text: spines(10), read }, STILL, { shotKind: 'single_cover', combine: false })).toBe('full');
  });
});
