// The shelf's tile read (batch 7, brief B part 2): a spine whose biggest print is a name printed on other spines may stack its title in flat
// words that no pass read, so the tiles that spine touches are read once more, and only the lines they add in new places are kept.
import type { Rect, RecognizeTextOptions, TextLine } from '@/native/cm-vision';
import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, RecordingVision, STILL, testIds } from './fakes';
import { tilesOf } from './lines';
import { VisionPipelineEngine } from './VisionPipelineEngine';

/** Three spines: a series author on the first and the third, a title nobody else prints on the second. */
const spineA = line('ELSIE SILVER', 0.6, 0.259, 0.03, 0.111);
const spineB = line('THE ROGUE KING', 0.2, 0.1, 0.03, 0.6);
// The author's name on a third spine, in a longer line: a neighboring spine does not read the author's name alone as its title.
const spineC = [line('DEEP END by ELSIE SILVER', 0.8, 0.6, 0.03, 0.3)];
const stackA = [line('OFF TO', 0.605, 0.224, 0.02, 0.012), line('THE RACES', 0.601, 0.233, 0.028, 0.013)];

type Tiles = (regions: Rect[]) => TextLine[];

/** Vision reading `upright` in the whole-picture passes; a tile read is answered by `tiles` (lines already on the whole picture, `region` the index asked for). */
function vision(upright: TextLine[], tiles: Tiles) {
  const fake = new RecordingVision({ [STILL.uri]: { text: upright, rectangles: [] } });
  const asked: RecognizeTextOptions[] = [];
  const read = fake.recognizeText.bind(fake);
  fake.recognizeText = async (uri, options) => {
    if (!options?.regions) return read(uri, options);
    asked.push(options);
    return tiles(options.regions);
  };
  return { fake, asked };
}

const engine = (fake: RecordingVision) => new VisionPipelineEngine({ vision: fake, settings: () => S, newId: testIds });
const titles = async (fake: RecordingVision, shotKind: 'shelf_segment' | 'single_cover' = 'shelf_segment') =>
  (await engine(fake).read(STILL, () => {}, { shotKind })).items.map((i) => i.title);

const tileLines: Tiles = () => stackA.map((l) => ({ ...l, pass: 0 as const, region: 0 }));

describe('the shelf tile read', () => {
  it('asks only for the tiles a spine named by other spines touches', async () => {
    const { fake, asked } = vision([spineA, spineB, ...spineC], tileLines);
    await titles(fake);
    const grid = tilesOf(T);
    expect(asked).toHaveLength(1);
    expect(asked[0].orientations).toEqual([0]);
    // Spine A is top right and its title is printed on spine C too; the titles of B and C are printed nowhere else, so no other tile is asked for.
    expect(asked[0].regions).toEqual([grid[1]]);
  });

  it('reads the title stacked on a spine from the tile lines, and says the author as its creator', async () => {
    const { fake } = vision([spineA, spineB, ...spineC], tileLines);
    const { items } = await engine(fake).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(items.map((i) => i.title).sort()).toEqual(['DEEP END by ELSIE SILVER', 'OFF TO THE RACES', 'THE ROGUE KING']);
    expect(items.find((i) => i.title === 'OFF TO THE RACES')?.creator).toBe('ELSIE SILVER');
  });

  it('asks for no tiles when no spine is named by another', async () => {
    const { fake, asked } = vision([spineA, spineB], tileLines);
    await titles(fake);
    expect(asked).toEqual([]);
  });

  it('never reads tiles for a single cover', async () => {
    const { fake, asked } = vision([spineA, ...spineC], tileLines);
    await titles(fake, 'single_cover');
    expect(asked).toEqual([]);
  });

  it('keeps what the passes read when the tile read fails', async () => {
    const { fake } = vision([spineA, spineB, ...spineC], () => {
      throw new Error('Vision declined');
    });
    expect((await titles(fake)).sort()).toEqual(['DEEP END by ELSIE SILVER', 'ELSIE SILVER', 'THE ROGUE KING']);
  });

  it('adds nothing a pass already read: a tile line in the same box is dropped', async () => {
    const { fake } = vision([spineA, spineB, ...spineC], () => [{ ...spineA, text: 'ELS1E S1LVER', pass: 0, region: 0 }]);
    expect((await titles(fake)).sort()).toEqual(['DEEP END by ELSIE SILVER', 'ELSIE SILVER', 'THE ROGUE KING']);
  });
});
