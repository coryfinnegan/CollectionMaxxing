// Several covers lying side by side in one photo are one item per cover, not one guess at the whole table (multiCover.ts).
import type { Rect, TextLine } from '@/native/cm-vision';
import { DEFAULT_LOCAL_TUNING as T, DEFAULT_MULTI_COVER_TUNING as M, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, RecordingVision, STILL, testIds, type Scene } from './fakes';
import { union } from './geometry';
import { multipleCovers } from './multiCover';
import reads from './multiCoverReads.json';
import type { ReadItem } from './types';
import { VisionPipelineEngine } from './VisionPipelineEngine';

// ---- The real photo: three Nintendo Switch boxes lying apart on a table (golden/shelf/game-box-multiple-01) ----------------------------------

const PHOTO = reads['game-box-multiple-01'];
const PHOTO_STILL = { uri: 'fixture://game-box-multiple-01', width: PHOTO.width, height: PHOTO.height };
/** The cleaned lines the Vision engine read, as the eval recorded them. The record keeps no confidence, so every line is read as 0.9, as in the other real fixtures. */
const PHOTO_LINES: TextLine[] = PHOTO.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height));

/** Which of those lines each box holds, from the pieces the Vision engine made in that run (each was one box's lines): Donkey Kong, Tears of the Kingdom, Breath of the Wild. */
const BOXES = {
  donkeyKong: [18, 17, 16, 9, 12, 14, 8],
  tearsOfTheKingdom: [5, 7, 15, 6, 13, 3, 10, 11, 2, 1, 4, 0],
  breathOfTheWild: [41, 45, 43, 37, 27, 44, 46, 42, 24, 25, 23, 19, 28, 21, 30, 20, 26, 29, 36, 31, 35, 38, 33, 32, 34, 39, 40, 22],
};

/**
 * The run recorded no rectangles, so each box's is made up: the extent of its lines, grown by 0.03 of the width and 0.04 of the height, because a
 * case's edge lies a little outside its outermost text (measured on the photo: the Donkey Kong case is x 0.17 to 0.52 and y 0.06 to 0.43 around text
 * at x 0.19 to 0.48 and y 0.08 to 0.37). They come out 0.72, 0.58 and 0.57 as short over long side, in pixels, and 0.13 to 0.15 of the frame.
 */
const grown = (indexes: number[]): Rect => {
  const box = union(indexes.map((i) => PHOTO_LINES[i].box));
  return { x: box.x - 0.03, y: box.y - 0.04, width: box.width + 0.06, height: box.height + 0.08 };
};
const PHOTO_RECTANGLES = [grown(BOXES.donkeyKong), grown(BOXES.tearsOfTheKingdom), grown(BOXES.breathOfTheWild)];
const [DONKEY_KONG, TEARS_OF_THE_KINGDOM, BREATH_OF_THE_WILD] = PHOTO_RECTANGLES;

const photoCovers = (rectangles: Rect[] = PHOTO_RECTANGLES) => multipleCovers(PHOTO_LINES, rectangles, PHOTO_STILL, T);

describe('the three game boxes of the real photo', () => {
  it('are three covers, one per rectangle, and no line is left over', () => {
    const found = photoCovers();
    expect(found?.covers.map((c) => c.box)).toEqual([TEARS_OF_THE_KINGDOM, DONKEY_KONG, BREATH_OF_THE_WILD]); // left to right
    expect(found?.rest).toEqual([]);
  });

  it('hold the lines inside their rectangles, each line in one cover', () => {
    const found = photoCovers();
    const sizes = found?.covers.map((c) => c.lines.length);
    expect(sizes).toEqual([BOXES.tearsOfTheKingdom.length, BOXES.donkeyKong.length, BOXES.breathOfTheWild.length]);
    expect(found?.covers.flatMap((c) => c.lines)).toHaveLength(PHOTO_LINES.length);
  });

  it('have their own titles: the biggest text, with its subtitle', () => {
    const titles = photoCovers()?.covers.map((c) => c.title?.line.text);
    expect(titles?.[0]).toMatch(/LEGEND.*TEARS.*KINGDOM/); // the Zelda logo's letters read as "%EIDA", between "THE LEGEND OF." and the subtitle
    expect(titles?.[1]).toBe('DONKEY KONG BANANZA');
    expect(titles?.[2]).toMatch(/BREATH.*WILD/);
  });

  it('are games, the two that print no console name too, because the third does', () => {
    expect(photoCovers()?.covers.map((c) => c.kind)).toEqual(['game', 'game', 'game']);
  });

  it('are read as three items by the Vision engine, each boxed by its rectangle and a game', async () => {
    const reading = await engineFor({ text: PHOTO_LINES, rectangles: PHOTO_RECTANGLES }, PHOTO_STILL).read(PHOTO_STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items).toHaveLength(3);
    expect(reading.items.map((i) => i.title)).toEqual([
      expect.stringMatching(/TEARS.*KINGDOM/),
      expect.stringMatching(/BANANZA/),
      expect.stringMatching(/BREATH.*WILD/),
    ]);
    expect(reading.items.map((i) => i.kind)).toEqual(['game', 'game', 'game']);
    expect(reading.items.map((i) => i.box)).toEqual([TEARS_OF_THE_KINGDOM, DONKEY_KONG, BREATH_OF_THE_WILD]);
    expect(reading.items.map((i) => i.order)).toEqual([0, 1, 2]);
    expect(reading.items.every((i) => i.creator === undefined && i.confidence === 'low')).toBe(true);
    expect(reading.unreadableCount).toBe(0);
    expect(reading.ocr?.pieces.map((p) => p.length)).toEqual([12, 7, 28]);
  });
});

// ---- Made-up photos on the fakes' 3000 by 4000 still -------------------------------------------------------------------------------------------

/** A cover-shaped rectangle: 900 by 1200 pixels on the fakes' still, 0.75, 0.09 of the frame. */
const caseAt = (x: number, y: number): Rect => ({ x, y, width: 0.3, height: 0.3 });
const LEFT = caseAt(0.05, 0.1);
const RIGHT = caseAt(0.55, 0.1);
const BELOW = caseAt(0.05, 0.55);

/** A cover's text: its title, big, and two small lines. */
const coverText = (at: Rect, title: string, ...extra: TextLine[]): TextLine[] => [
  line('Revised edition', at.x + 0.05, at.y + 0.03, 0.2, 0.015),
  line(title, at.x + 0.04, at.y + 0.1, 0.22, 0.08),
  line('A story of two brothers and a very long road', at.x + 0.03, at.y + 0.22, 0.24, 0.015),
  ...extra,
];
const nintendo = (at: Rect) => line('NINTENDO SWITCH', at.x + 0.05, at.y + 0.26, 0.15, 0.02);

const found = (lines: TextLine[], rectangles: Rect[]) => multipleCovers(lines, rectangles, STILL, T);

describe('what counts as several covers', () => {
  const two = [...coverText(LEFT, 'DUNE'), ...coverText(RIGHT, 'EMMA')];

  it('is two cover-shaped rectangles that hold the text, one cover each', () => {
    const result = found(two, [LEFT, RIGHT]);
    expect(result?.covers.map((c) => c.title?.line.text)).toEqual(['DUNE', 'EMMA']);
    expect(result?.covers.map((c) => c.box)).toEqual([LEFT, RIGHT]);
    expect(result?.rest).toEqual([]);
  });

  it('is not one cover: a single cover is the single shot', () => {
    expect(found(coverText(LEFT, 'DUNE'), [LEFT])).toBeUndefined();
    expect(found(two, [LEFT])).toBeUndefined();
  });

  it('is not one big cover rectangle either, even with a second shape inside it', () => {
    const big: Rect = { x: 0.1, y: 0.1, width: 0.8, height: 0.7 };
    expect(found(two, [big])).toBeUndefined();
    expect(found(two, [big, LEFT])).toBeUndefined(); // the big one stands and the one inside it goes: one cover again
  });

  it('never counts a rectangle that hugs the frame: it is the crop of the photo', () => {
    const frame: Rect = { x: 0.005, y: 0.005, width: 0.99, height: 0.99 };
    expect(found(two, [frame, LEFT])).toBeUndefined();
    // Touching two edges is a case at the photo's side, which is allowed.
    const atTheSide = caseAt(0, 0);
    expect(found([...coverText(atTheSide, 'DUNE'), ...coverText(RIGHT, 'EMMA')], [atTheSide, RIGHT])?.covers).toHaveLength(2);
  });

  it('keeps only the outer rectangle of a box inside a box', () => {
    const inside: Rect = { x: LEFT.x + 0.05, y: LEFT.y + 0.05, width: 0.2, height: 0.2 };
    const result = found(two, [inside, LEFT, RIGHT]);
    expect(result?.covers.map((c) => c.box)).toEqual([LEFT, RIGHT]);
    expect(found(coverText(LEFT, 'DUNE'), [inside, LEFT])).toBeUndefined();
  });

  it('keeps the bigger of two rectangles that overlap much, and both when they barely touch', () => {
    const mostly: Rect = { x: LEFT.x + 0.05, y: LEFT.y, width: 0.3, height: 0.3 };
    expect(found(two, [LEFT, mostly, RIGHT])?.covers.map((c) => c.box)).toEqual([LEFT, RIGHT]);
    const edge: Rect = { x: LEFT.x + 0.28, y: LEFT.y, width: 0.3, height: 0.3 };
    expect(found([...coverText(LEFT, 'DUNE'), ...coverText(edge, 'EMMA')], [LEFT, edge])?.covers).toHaveLength(2);
  });

  it('ignores a rectangle too small to be a cover, and ones shaped like spines or a square', () => {
    const label: Rect = { x: 0.6, y: 0.1, width: 0.1, height: 0.1 }; // 0.01 of the frame
    const spine: Rect = { x: 0.6, y: 0.1, width: 0.1, height: 0.5 }; // 300 by 2000 pixels
    const square: Rect = { x: 0.55, y: 0.1, width: 0.3, height: 0.225 }; // 900 by 900 pixels
    expect(found(two, [LEFT, label])).toBeUndefined();
    expect(found(two, [LEFT, spine])).toBeUndefined();
    expect(found(two, [LEFT, square])).toBeUndefined();
  });

  it('is not a cover when the rectangle holds only a line or two: a stretch of shelf that happened to be boxed', () => {
    const wide = (x: number): Rect => ({ x, y: 0.05, width: 0.46, height: 0.8 }); // 0.575 on the fakes' still, 0.37 of the frame
    const lines = [line('A very long tagline set in tiny type', 0.1, 0.1, 0.3, 0.03), line('BIG', 0.2, 0.4, 0.2, 0.08), line('Longer', 0.6, 0.2, 0.3, 0.1), line('Short', 0.6, 0.6, 0.2, 0.05)];
    expect(found(lines, [wide(0.02), wide(0.52)])).toBeUndefined();
  });

  it('ignores a rectangle that holds no text, so a cover beside an empty table-top shape is one cover', () => {
    expect(found(coverText(LEFT, 'DUNE'), [LEFT, RIGHT])).toBeUndefined();
  });

  it('measures shape in pixels, not shares: a 0.5 by 0.5 rectangle on a 3 by 4 still is not a square', () => {
    const half: Rect = { x: 0.05, y: 0.05, width: 0.4, height: 0.4 }; // 1200 by 1600: 0.75
    const other: Rect = { x: 0.55, y: 0.05, width: 0.4, height: 0.4 };
    expect(found([...coverText(half, 'DUNE'), ...coverText(other, 'EMMA')], [half, other])?.covers).toHaveLength(2);
  });

  it('leaves lines outside every rectangle for the splitter, and gives them back', () => {
    const stray = line('Neuromancer', 0.4, 0.9, 0.05, 0.08);
    const result = found([...two, stray], [LEFT, RIGHT]);
    expect(result?.covers).toHaveLength(2);
    expect(result?.rest).toEqual([stray]);
  });

  it('is not several covers when the rectangles hold only a few of the lines', () => {
    const shelf = Array.from({ length: 12 }, (_, i) => line(`Spine ${i}`, 0.05 + i * 0.07, 0.6, 0.03, 0.3));
    expect(found([...two, ...shelf], [LEFT, RIGHT])).toBeUndefined();
  });

  it('is not several covers when the rectangles are blocks of spines', () => {
    const upright = (at: Rect) => Array.from({ length: 6 }, (_, i) => line(`Title ${i}`, at.x + 0.02 + i * 0.04, at.y + 0.03, 0.03, 0.24));
    expect(found([...upright(LEFT), ...upright(RIGHT)], [LEFT, RIGHT])).toBeUndefined();
  });

  it('is nothing without lines or rectangles', () => {
    expect(found([], [LEFT, RIGHT])).toBeUndefined();
    expect(found(two, [])).toBeUndefined();
  });

  it('treats a picture of unknown size as square pixels, so the shares are the shapes', () => {
    const tall: Rect = { x: 0.05, y: 0.1, width: 0.3, height: 0.4 };
    const taller: Rect = { x: 0.55, y: 0.1, width: 0.3, height: 0.4 };
    const lines = [...coverText(tall, 'DUNE'), ...coverText(taller, 'EMMA')];
    expect(multipleCovers(lines, [tall, taller], { width: 0, height: 0 }, T)?.covers).toHaveLength(2);
  });

  it('orders the covers left to right, top to bottom', () => {
    const lines = [...coverText(RIGHT, 'EMMA'), ...coverText(BELOW, 'RUTH'), ...coverText(LEFT, 'DUNE')];
    expect(found(lines, [RIGHT, BELOW, LEFT])?.covers.map((c) => c.title?.line.text)).toEqual(['DUNE', 'RUTH', 'EMMA']);
  });
});

describe('the kind of each cover', () => {
  it('is what its own text names: a console is a game, the Criterion Collection a film', () => {
    const criterion = line('THE CRITERION COLLECTION', RIGHT.x + 0.04, RIGHT.y + 0.26, 0.2, 0.02);
    const lines = [...coverText(LEFT, 'ZELDA', nintendo(LEFT)), ...coverText(RIGHT, 'RUSHMORE', criterion)];
    expect(found(lines, [LEFT, RIGHT])?.covers.map((c) => c.kind)).toEqual(['game', 'film']);
  });

  it('is nothing when no cover names one', () => {
    expect(found([...coverText(LEFT, 'DUNE'), ...coverText(RIGHT, 'EMMA')], [LEFT, RIGHT])?.covers.map((c) => c.kind)).toEqual([undefined, undefined]);
  });

  it('is the kind the others name when it names none, and none names another', () => {
    const lines = [...coverText(LEFT, 'ZELDA', nintendo(LEFT)), ...coverText(RIGHT, 'MARIO', nintendo(RIGHT)), ...coverText(BELOW, 'BANANZA')];
    expect(found(lines, [LEFT, RIGHT, BELOW])?.covers.map((c) => c.kind)).toEqual(['game', 'game', 'game']);
    const one = [...coverText(LEFT, 'ZELDA', nintendo(LEFT)), ...coverText(RIGHT, 'EMMA')];
    expect(found(one, [LEFT, RIGHT])?.covers.map((c) => c.kind)).toEqual(['game', 'game']);
  });

  it('is left alone when too few covers name it for the others to take it', () => {
    const one = [...coverText(LEFT, 'ZELDA', nintendo(LEFT)), ...coverText(RIGHT, 'EMMA')];
    const covers = multipleCovers(one, [LEFT, RIGHT], STILL, T, { ...M, kindAgreeingCovers: 2 })?.covers;
    expect(covers?.map((c) => c.kind)).toEqual(['game', undefined]);
  });

  it('is left alone when the covers that name a kind do not agree', () => {
    const criterion = (at: Rect) => line('THE CRITERION COLLECTION', at.x + 0.04, at.y + 0.26, 0.2, 0.02);
    const fourth = caseAt(0.55, 0.55);
    const lines = [
      ...coverText(LEFT, 'ZELDA', nintendo(LEFT)),
      ...coverText(RIGHT, 'MARIO', nintendo(RIGHT)),
      ...coverText(BELOW, 'RUSHMORE', criterion(BELOW)),
      ...coverText(fourth, 'SOMETHING'),
    ];
    // Left to right, top to bottom: Zelda, Rushmore (below it), Mario, and the one that says nothing.
    expect(found(lines, [LEFT, RIGHT, BELOW, fourth])?.covers.map((c) => c.kind)).toEqual(['game', 'film', 'game', undefined]);
  });
});

describe('the title of a cover', () => {
  it('is its own biggest text, per cover, and not another cover\'s', () => {
    const lines = [...coverText(LEFT, 'DUNE'), ...coverText(RIGHT, 'EMMA')];
    const covers = found(lines, [LEFT, RIGHT])?.covers;
    expect(covers?.[0].title?.line.text).toBe('DUNE');
    expect(covers?.[1].title?.line.text).toBe('EMMA');
  });

  it('puts a book\'s author, printed bigger than its title, aside as the creator, as a single cover does', () => {
    const book = (at: Rect, author: string, title: string) => [
      line(author, at.x + 0.03, at.y + 0.02, 0.24, 0.08),
      line(title, at.x + 0.05, at.y + 0.12, 0.2, 0.045),
      line('A novel of the sea and what it took', at.x + 0.03, at.y + 0.24, 0.24, 0.015),
    ];
    const covers = found([...book(LEFT, 'FREDRIK BACKMAN', 'MY FRIENDS'), ...book(RIGHT, 'MARY SHELLEY', 'FRANKENSTEIN')], [LEFT, RIGHT])?.covers;
    expect(covers?.map((c) => c.title?.line.text)).toEqual(['MY FRIENDS', 'FRANKENSTEIN']);
    expect(covers?.map((c) => c.title?.creator?.text)).toEqual(['FREDRIK BACKMAN', 'MARY SHELLEY']);
  });
});

// ---- The Vision engine ----------------------------------------------------------------------------------------------------------------------

const engineFor = (scene: Scene, still = STILL) => new VisionPipelineEngine({ vision: new RecordingVision({ [still.uri]: scene }), settings: () => S, newId: testIds });
const readShelf = (scene: Scene, onItem: (item: ReadItem) => void = () => {}) => engineFor(scene).read(STILL, onItem, { shotKind: 'shelf_segment' });
const titles = (items: ReadItem[]) => items.map((i) => i.title);
const withoutIds = (items: ReadItem[]) => items.map((i) => ({ ...i, localId: '' }));

describe('the Vision engine on a shelf shot', () => {
  const two = [...coverText(LEFT, 'DUNE'), ...coverText(RIGHT, 'EMMA')];

  it('reads two covers side by side as two items, each boxed by its rectangle', async () => {
    const reading = await readShelf({ text: two, rectangles: [LEFT, RIGHT] });
    expect(titles(reading.items)).toEqual(['DUNE', 'EMMA']);
    expect(reading.items.map((i) => i.box)).toEqual([LEFT, RIGHT]);
    expect(reading.items.map((i) => i.kind)).toEqual(['other', 'other']);
  });

  it('streams each cover as it is read', async () => {
    const streamed: ReadItem[] = [];
    const reading = await readShelf({ text: two, rectangles: [LEFT, RIGHT] }, (i) => streamed.push(i));
    expect(streamed).toEqual(reading.items);
  });

  it('gives every cover the kind its own text says', async () => {
    const lines = [...coverText(LEFT, 'ZELDA', nintendo(LEFT)), ...coverText(RIGHT, 'MARIO', nintendo(RIGHT)), ...coverText(BELOW, 'BANANZA')];
    const reading = await readShelf({ text: lines, rectangles: [LEFT, RIGHT, BELOW] });
    expect(reading.items.map((i) => `${i.title}/${i.kind}`)).toEqual(['ZELDA/game', 'BANANZA/game', 'MARIO/game']);
  });

  it('sends no product code and no guessed creator for a cover', async () => {
    const lines = [...two, line('SLUS-00594', LEFT.x + 0.05, LEFT.y + 0.28, 0.1, 0.015)];
    const reading = await readShelf({ text: lines, rectangles: [LEFT, RIGHT] });
    expect(reading.items.some((i) => i.productCode !== undefined || i.creator !== undefined)).toBe(false);
  });

  it('reads the lines outside every rectangle as it always did, after the covers', async () => {
    const stray = line('Neuromancer', 0.4, 0.8, 0.05, 0.1);
    const reading = await readShelf({ text: [...two, stray], rectangles: [LEFT, RIGHT] });
    expect(titles(reading.items)).toEqual(['DUNE', 'EMMA', 'Neuromancer']);
    expect(reading.items.map((i) => i.order)).toEqual([0, 1, 2]);
    expect(reading.items[2].kind).toBe('other');
  });

  it('asks for the rectangles as wide as a case, while it reads the text, not after', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: two, rectangles: [LEFT, RIGHT] } });
    const events: string[] = [];
    const rectangles = vision.detectRectangles.bind(vision);
    const text = vision.recognizeText.bind(vision);
    const detect = jest.spyOn(vision, 'detectRectangles').mockImplementation(async (uri, options) => {
      events.push('rectangles asked');
      return rectangles(uri, options);
    });
    jest.spyOn(vision, 'recognizeText').mockImplementation(async (uri, options) => {
      events.push('text asked');
      await new Promise((resolve) => setTimeout(resolve, 5));
      events.push('text answered');
      return text(uri, options);
    });
    await new VisionPipelineEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(detect).toHaveBeenCalledTimes(1);
    expect(detect).toHaveBeenCalledWith(STILL.uri, { maximumAspectRatio: M.maxAspectRatio });
    expect(events.indexOf('rectangles asked')).toBeLessThan(events.indexOf('text answered'));
  });

  it('is today\'s reading when there are no rectangles, or the search throws', async () => {
    const today = await readShelf({ text: two, rectangles: [] });
    const vision = new RecordingVision({ [STILL.uri]: { text: two, rectangles: [LEFT, RIGHT] } });
    jest.spyOn(vision, 'detectRectangles').mockRejectedValue(new Error('Vision failed'));
    const thrown = await new VisionPipelineEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(withoutIds(thrown.items)).toEqual(withoutIds(today.items));
    expect(thrown.items.length).toBeGreaterThan(0);
  });

  it('is today\'s reading when the search throws before it answers, as an exception and not a rejection', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: two } });
    jest.spyOn(vision, 'detectRectangles').mockImplementation(() => {
      throw new Error('Vision failed');
    });
    const reading = await new VisionPipelineEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'shelf_segment' });
    expect(reading.items.length).toBeGreaterThan(0);
  });

  it('splits a shelf of spines as before: one item per spine rectangle', async () => {
    const spines = [0.1, 0.2, 0.3].map((x): Rect => ({ x, y: 0.1, width: 0.06, height: 0.7 })); // 180 by 2800 pixels
    const text = ['Dune', 'Emma', 'Ruth'].map((title, i) => line(title, spines[i].x + 0.01, 0.2, 0.04, 0.5));
    const reading = await readShelf({ text, rectangles: spines });
    expect(titles(reading.items)).toEqual(['Dune', 'Emma', 'Ruth']);
    expect(reading.items.map((i) => i.box)).toEqual(spines);
  });

  it('leaves one cover-shaped rectangle to the splitter, which boxes the one item with it as it always did', async () => {
    const reading = await readShelf({ text: coverText(LEFT, 'DUNE'), rectangles: [LEFT] });
    expect(reading.items).toHaveLength(1);
    expect(reading.items[0].box).toMatchObject({ x: expect.closeTo(LEFT.x), y: expect.closeTo(LEFT.y), width: expect.closeTo(LEFT.width), height: expect.closeTo(LEFT.height) });
    expect(reading.items[0].kind).toBe('other');
  });

  it('does not look for covers in a single cover shot, which is one item whatever is drawn on it', async () => {
    const vision = new RecordingVision({ [STILL.uri]: { text: two, rectangles: [LEFT, RIGHT] } });
    const detect = jest.spyOn(vision, 'detectRectangles');
    const reading = await new VisionPipelineEngine({ vision, settings: () => S, newId: testIds }).read(STILL, () => {}, { shotKind: 'single_cover' });
    expect(reading.items).toHaveLength(1);
    expect(detect).not.toHaveBeenCalled();
  });
});
