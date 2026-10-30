// Items lying flat in piles (stacked tapes, a pile of hardcovers) are one item per spine, and upside-down text is read. Real OCR lines of
// three shelf photos (shelfReads.json: text and boxes only, never pictures) pin what the split finds; synthetic scenes pin the rules.
import type { Rect, TextLine } from '@/native/cm-vision';
import covers from './coverReads.json';
import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_CONFIG as C, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, RecordingVision, STILL, testIds } from './fakes';
import { normalize } from './lines';
import reads from './shelfReads.json';
import { hasPile, splitIntoPieces } from './splitting';
import { VisionPipelineEngine } from './VisionPipelineEngine';

const titles = (pieces: { lines: { text: string }[] }[]) => pieces.map((p) => p.lines.map((l) => l.text));

/** The split with piles switched off: what the splitter did before items lying flat were cut one per spine. */
const WITHOUT_PILES = { ...T, stackMinBands: Infinity };

/** A tape lying flat: a title line 0.35 wide and 0.05 tall, 0.08 below the last, so the gap is a little over half a letter. */
const tape = (i: number, text = `Tape ${i}`, x = 0.3, top = 0.1) => line(text, x, top + i * 0.08, 0.35, 0.05);

describe('a pile of items lying flat is one item per spine', () => {
  it('turns a stack of 5 flat spines into 5 items, top to bottom', () => {
    const spines = Array.from({ length: 5 }, (_, i) => tape(i));
    const { pieces, unreadableCount } = splitIntoPieces([...spines].reverse(), [], C.splitting, T);
    expect(titles(pieces)).toEqual(spines.map((l) => [l.text]));
    expect(pieces.map((p) => p.title)).toEqual(spines.map((l) => l.text));
    expect(unreadableCount).toBe(0);
  });

  it('is not a pile below 5 spines: 4 flat lines stay one item, as before', () => {
    const { pieces } = splitIntoPieces(Array.from({ length: 4 }, (_, i) => tape(i)), [], C.splitting, T);
    expect(pieces).toHaveLength(1);
    expect(pieces[0].title).toBeUndefined();
  });

  it('puts the small print on a tape, and a repeat reading of its title, in that tape and not in the next', () => {
    const spines = Array.from({ length: 5 }, (_, i) => tape(i));
    const smallPrint = line('Stock No. 038', 0.5, 0.1 + 2 * 0.08 + 0.02, 0.1, 0.01);
    const again = line('Tape 2', 0.305, 0.1 + 2 * 0.08 + 0.003, 0.35, 0.05);
    const { pieces } = splitIntoPieces([...spines, smallPrint, again], [], C.splitting, T);
    expect(pieces).toHaveLength(5);
    expect(titles(pieces)[2].sort()).toEqual(['Stock No. 038', 'Tape 2', 'Tape 2']);
    expect(pieces[2].title).toBe('Tape 2');
  });

  it('keeps a title that runs over two lines in one item and joins the lines for its title', () => {
    const spines = [tape(0, 'Alpha'), tape(1, 'Beta'), line('A COURT OF', 0.33, 0.1 + 2 * 0.08, 0.3, 0.025), line('WINGS AND RUIN', 0.31, 0.1 + 2 * 0.08 + 0.025, 0.33, 0.025), tape(3, 'Delta'), tape(4, 'Echo'), tape(5, 'Foxtrot')];
    const { pieces } = splitIntoPieces(spines, [], C.splitting, T);
    expect(pieces.map((p) => p.title)).toEqual(['Alpha', 'Beta', 'A COURT OF WINGS AND RUIN', 'Delta', 'Echo', 'Foxtrot']);
  });

  it('leaves the lettering that is not big out of the title, and keeps a surer reading of one place over its twin', () => {
    const spines = Array.from({ length: 5 }, (_, i) => tape(i));
    const twin = line('Tap3 2', 0.3, 0.1 + 2 * 0.08, 0.35, 0.05, 0.4);
    const credit = line('A film by Someone', 0.35, 0.1 + 2 * 0.08 + 0.03, 0.2, 0.012);
    const { pieces } = splitIntoPieces([...spines, twin, credit], [], C.splitting, T);
    expect(pieces[2].title).toBe('Tape 2');
  });

  it('does not cut a cover whose title, author and tagline are far apart', () => {
    const cover = [
      line('NATIONAL BESTSELLER', 0.2, 0.05, 0.6, 0.04),
      line('The Golden Hour', 0.15, 0.2, 0.7, 0.1),
      line('A NOVEL', 0.4, 0.35, 0.2, 0.03),
      line('Some Author', 0.25, 0.7, 0.5, 0.06),
      line('Praise for the author', 0.2, 0.85, 0.6, 0.03),
    ];
    expect(splitIntoPieces(cover, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('does not cut a column wider than half the frame: that is a cover filling it', () => {
    const wide = Array.from({ length: 6 }, (_, i) => line(`Line ${i}`, 0.1, 0.1 + i * 0.08, 0.8, 0.05));
    expect(splitIntoPieces(wide, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('does not cut a column too narrow to be a spine: a name repeated down a pile of covers is not a pile', () => {
    const names = Array.from({ length: 8 }, (_, i) => line('SARAH J.', 0.3, 0.1 + i * 0.05, 0.05, 0.02));
    expect(splitIntoPieces(names, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('keeps every line: nothing a pile does not take is dropped', () => {
    const lines = [...Array.from({ length: 5 }, (_, i) => tape(i)), line('Dust', 0.9, 0.9, 0.04, 0.01)];
    const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
    expect(pieces.reduce((n, p) => n + p.lines.length, 0)).toBe(lines.length);
    expect(titles(pieces)).toContainEqual(['Dust']);
  });

  describe('among upright spines and other piles', () => {
    const spine = (i: number) => line(`Spine ${i}`, 0.04 + i * 0.075, 0.05, 0.05, 0.4);
    const upright = Array.from({ length: 6 }, (_, i) => spine(i));
    const left = Array.from({ length: 5 }, (_, i) => tape(i, `Left ${i}`, 0.05, 0.5));
    const right = Array.from({ length: 6 }, (_, i) => tape(i, `Right ${i}`, 0.55, 0.5));

    it('counts a scene with two piles and a row of spines between them right', () => {
      const { pieces } = splitIntoPieces([...right, ...upright, ...left], [], C.splitting, T);
      expect(pieces).toHaveLength(6 + 5 + 6);
      expect(titles(pieces).filter((t) => t[0].startsWith('Spine'))).toEqual(upright.map((l) => [l.text]));
      expect(titles(pieces).filter((t) => t[0].startsWith('Left'))).toEqual(left.map((l) => [l.text]));
      expect(titles(pieces).filter((t) => t[0].startsWith('Right'))).toEqual(right.map((l) => [l.text]));
    });

    it('does not lend a pile below a row of spines to the spine above it', () => {
      const { pieces } = splitIntoPieces([...upright, ...left], [], C.splitting, T);
      expect(titles(pieces).filter((t) => t[0].startsWith('Spine')).every((t) => t.length === 1)).toBe(true);
    });

    it('lets a short line across a spine still join it', () => {
      const credit = line('Herbert', 0.04, 0.3, 0.04, 0.02);
      const { pieces } = splitIntoPieces([...upright, credit], [], C.splitting, T);
      expect(titles(pieces)[0]).toEqual(['Spine 0', 'Herbert']);
    });

    it('cuts a pile inside a rectangle, and the lines the rectangle does not hold', () => {
      const rect: Rect = { x: 0.02, y: 0.45, width: 0.5, height: 0.5 };
      const { pieces } = splitIntoPieces([...left, ...right], [rect], C.splitting, T);
      expect(titles(pieces).filter((t) => t[0].startsWith('Left'))).toEqual(left.map((l) => [l.text]));
      expect(titles(pieces).filter((t) => t[0].startsWith('Right'))).toEqual(right.map((l) => [l.text]));
    });

    it('says whether the lines hold a pile', () => {
      expect(hasPile(left, T)).toBe(true);
      expect(hasPile(upright, T)).toBe(false);
      expect(hasPile(left.slice(0, 3), T)).toBe(false);
    });
  });
});

describe('real shelf photos', () => {
  type Truth = { title: string; alsoTitled?: string[] };
  type Photo = { truth: Truth[]; lines: { text: string; x: number; y: number; width: number; height: number }[] };
  const photos = reads as Record<string, Photo>;
  const has = (text: string, wanted: string) => ` ${normalize(text)} `.includes(` ${normalize(wanted)} `);

  /**
   * The recorded lines kept no confidence. Vision is surer of a clean reading than of noise from text read the wrong way up, so a line of
   * plain ASCII stands in at 0.9 and any other at 0.5 (the same for the split before and after).
   */
  const confidence = (text: string) => (/^[\x20-\x7E]+$/.test(text) ? 0.9 : 0.5);

  /** The items the Vision pipeline makes of the photo's lines, and how many truth titles come out as an item's title. */
  async function read(id: string, local: typeof T) {
    const photo = photos[id];
    const text = photo.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));
    const vision = new RecordingVision({ [STILL.uri]: { text, rectangles: [] } });
    const engine = new VisionPipelineEngine({ vision, settings: () => ({ ...S, local }), newId: testIds });
    const { items } = await engine.read(STILL, () => {}, { shotKind: 'shelf_segment' });
    const found = photo.truth.filter((t) => [t.title, ...(t.alsoTitled ?? [])].some((v) => items.some((i) => has(i.title, v)))).length;
    return { items: items.length, found, truth: photo.truth.length };
  }

  // Today's counts, with piles switched off, then the counts with piles. A truth title counts when its words are in an item's title.
  const COUNTS: Record<string, { before: { items: number; found: number }; after: { items: number; found: number } }> = {
    // Three piles of tapes with a row of DVDs between: 7 lumps, then one item per tape. The OCR kept 7 titles whole on one line.
    'vhs-dvd-shelf-01': { before: { items: 7, found: 0 }, after: { items: 32, found: 6 } },
    // Upright DVD spines and no pile of 5 or more flat items: nothing for the split to change (its misses are merged spines).
    'vhs-dvd-shelf-02': { before: { items: 12, found: 3 }, after: { items: 12, found: 3 } },
    // A pile of hardcovers lies under a row of upright books; the 2-line titles of the pile now come out whole.
    'accept-shelf-04': { before: { items: 24, found: 17 }, after: { items: 32, found: 20 } },
    // Upright rows with no pile: unchanged.
    'accept-shelf-01': { before: { items: 19, found: 14 }, after: { items: 19, found: 14 } },
    'accept-shelf-02': { before: { items: 17, found: 15 }, after: { items: 17, found: 15 } },
  };

  it.each(Object.entries(COUNTS))('%s: items and truth titles found before and after', async (id, counts) => {
    const before = await read(id, WITHOUT_PILES);
    const after = await read(id, T);
    expect({ items: before.items, found: before.found }).toEqual(counts.before);
    expect({ items: after.items, found: after.found }).toEqual(counts.after);
  });

  it.each(['vhs-dvd-shelf-01', 'accept-shelf-04'])('%s: more truth titles are found, and more items are read, than before', async (id) => {
    const before = await read(id, WITHOUT_PILES);
    const after = await read(id, T);
    expect(after.found).toBeGreaterThan(before.found);
    expect(after.items).toBeGreaterThan(before.items);
  });

  it.each(['vhs-dvd-shelf-02', 'accept-shelf-01', 'accept-shelf-02'])('%s: finds no fewer truth titles than before', async (id) => {
    const before = await read(id, WITHOUT_PILES);
    const after = await read(id, T);
    expect(after.found).toBeGreaterThanOrEqual(before.found);
    expect(after.items).toBe(before.items);
  });

  it('reads a pile of tapes as one item per tape: the tape stack of the first photo is no longer one lump', async () => {
    const { items } = await read('vhs-dvd-shelf-01', T);
    expect(items).toBeGreaterThanOrEqual(photos['vhs-dvd-shelf-01'].truth.length - 5); // 5 of the 32 are unreadable
  });

  it('wants the extra upside-down pass for the two photos with a pile and for none of the others', () => {
    const wanted = Object.entries(photos)
      .filter(([, p]) => hasPile(p.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height)), T))
      .map(([id]) => id);
    expect(wanted).toEqual(['vhs-dvd-shelf-01', 'accept-shelf-04']);
  });

  it('never cuts a real single cover: none of the 12 recorded covers is split into more pieces than before', () => {
    const recorded = covers as Record<string, { lines: { text: string; x: number; y: number; width: number; height: number }[] }>;
    for (const [id, cover] of Object.entries(recorded)) {
      const lines: TextLine[] = cover.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height));
      const before = splitIntoPieces(lines, [], C.splitting, WITHOUT_PILES).pieces.length;
      expect([id, splitIntoPieces(lines, [], C.splitting, T).pieces.length]).toEqual([id, before]);
    }
  });
});

describe('upside-down text on a shelf of piled tapes', () => {
  // Tape 2 lies the other way up: read upright it is noise in its box. Read at 180, every tape that is the right way up is noise instead.
  const upright = [...[0, 1, 3, 4, 5].map((i) => tape(i)), line('HEPA DNOTS', 0.3, 0.1 + 2 * 0.08, 0.35, 0.05, 0.4)];
  const flipped = [tape(2, 'Tape 2'), ...[0, 1, 3, 4, 5].map((i) => line('lddnu', 0.3, 0.1 + i * 0.08, 0.35, 0.05, 0.35))];

  async function read(shotKind: 'shelf_segment' | 'single_cover', scene: { text: TextLine[]; text180?: TextLine[] }) {
    const vision = new RecordingVision({ [STILL.uri]: scene });
    const passes: number[][] = [];
    const recognize = vision.recognizeText.bind(vision);
    vision.recognizeText = (uri, options) => {
      passes.push(options?.orientations ?? [0]);
      return recognize(uri, options);
    };
    const engine = new VisionPipelineEngine({ vision, settings: () => S, newId: testIds });
    const reading = await engine.read(STILL, () => {}, { shotKind });
    return { items: reading.items, passes };
  }

  it('reads the tape the other way up as its own item, and the noise at 180 does not replace the real titles', async () => {
    const { items, passes } = await read('shelf_segment', { text: upright, text180: flipped });
    expect(items.map((i) => i.title)).toEqual(['Tape 0', 'Tape 1', 'Tape 2', 'Tape 3', 'Tape 4', 'Tape 5']);
    expect(passes).toEqual([[0, 90, 270], [180]]);
  });

  it('keeps the noise out of the item text: one reading per box', async () => {
    const { items } = await read('shelf_segment', { text: upright, text180: flipped });
    expect(items.map((i) => i.text).join(' ')).not.toMatch(/HEPA|lddnu/);
  });

  it('costs one more pass only on a shelf with a pile: a single cover is read in the three passes it always was', async () => {
    const { passes } = await read('single_cover', { text: upright, text180: flipped });
    expect(passes).toEqual([[0, 90, 270]]);
  });
});
