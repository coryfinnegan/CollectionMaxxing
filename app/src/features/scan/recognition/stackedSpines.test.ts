// Items lying flat in piles (stacked tapes, a pile of hardcovers) are one item per spine, and upside-down text is read. Real OCR lines of
// three shelf photos (shelfReads.json: text and boxes only, never pictures) pin what the split finds; synthetic scenes pin the rules.
import type { Rect, TextLine } from '@/native/cm-vision';
import covers from './coverReads.json';
import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_CONFIG as C, DEFAULT_RECOGNITION_SETTINGS as S } from './defaults';
import { line, RecordingVision, STILL, testIds } from './fakes';
import { normalize } from './lines';
import reads from './shelfReads.json';
import { hasPile, splitIntoPieces, type Piece } from './splitting';
import { VisionPipelineEngine } from './VisionPipelineEngine';

const titles = (pieces: { lines: { text: string }[] }[]) => pieces.map((p) => p.lines.map((l) => l.text));

/** The pile rules of #637 off: a pile's median gap is at most a letter (not 1.25), and a stack of four needs lettering of one size (0.5, not 0.4). */
const BEFORE_637 = { stackSureMaxGap: 1, stackFourLetters: 0.5 };

/** The cut of a tape lying across the tops of the spines off: a flat line joins the spine under its center. */
const BEFORE_LYING = { lyingLineWidth: Infinity };

/** The two-row rules off: the two rows of one spine title are two columns, and a pile's band that ends on a joining word ("A COURT OF") stands alone. */
const BEFORE_TWO_ROWS = { twoRowLetters: Infinity, bandJoinGap: -Infinity };

/** The split with piles switched off: what the splitter did before items lying flat were cut one per spine (nor was the frame cut into shelf rows). */
const WITHOUT_PILES = { ...T, stackMinBands: Infinity, shelfRowGap: Infinity, ...BEFORE_637, ...BEFORE_LYING, ...BEFORE_TWO_ROWS };

/** The values before #618: a pile needed 5 bands in a column no wider than half the frame, and a row 60% of its neighbors in one band. */
const BEFORE_618 = { ...T, stackMinBands: 5, stackMaxWidth: 0.5, spineRowShare: 0.6, shelfRowGap: Infinity, ...BEFORE_637, ...BEFORE_LYING, ...BEFORE_TWO_ROWS };

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

  it('is a pile from 3 spines up: a small stack of 4 flat lines is split into 4 items (#618)', () => {
    const spines = Array.from({ length: 4 }, (_, i) => tape(i));
    const { pieces } = splitIntoPieces(spines, [], C.splitting, T);
    expect(titles(pieces)).toEqual(spines.map((l) => [l.text]));
    expect(pieces.map((p) => p.title)).toEqual(spines.map((l) => l.text));
  });

  // The issue (#618) says two to four, but a title and its author are two flat lines set just like two tapes, so two never split.
  it('is not a pile below 3 spines: 2 flat lines stay one item, since a title and its author read exactly like two tapes', () => {
    const { pieces } = splitIntoPieces(Array.from({ length: 2 }, (_, i) => tape(i)), [], C.splitting, T);
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

  describe('a band that ends on a joining word reads with the band below it', () => {
    // A hardcover lying flat prints a small "A COURT OF" a little above its big subtitle. It sits further above it than a line may sit from its
    // band, so it came out as a band of its own. `gap` is how far above, as a share of the small lettering's height (0.025).
    const hardcovers = (small: string, gap: number) => [
      tape(0, 'Alpha'),
      tape(1, 'Beta'),
      line(small, 0.33, 0.26, 0.3, 0.025),
      line('SILVER FLAMES', 0.31, 0.285 + gap * 0.025, 0.33, 0.035),
      line('Delta', 0.3, 0.42, 0.35, 0.05),
      line('Echo', 0.3, 0.5, 0.35, 0.05),
    ];
    const titlesOf = (lines: TextLine[], local: typeof T = T) => splitIntoPieces(lines, [], C.splitting, local).pieces.map((p) => p.title);

    it('makes "A COURT OF" and the subtitle below it one item', () => {
      const { pieces } = splitIntoPieces(hardcovers('A COURT OF', 0.4), [], C.splitting, T);
      expect(pieces.map((p) => p.title)).toEqual(['Alpha', 'Beta', 'A COURT OF SILVER FLAMES', 'Delta', 'Echo']);
      expect(pieces[2].lines.map((l) => l.text)).toEqual(['A COURT OF', 'SILVER FLAMES']);
    });

    it.each(['A COURT OF', 'THE', 'Pride and', 'Salt &', 'Wings of a', 'Ready to', 'An'])('joins a band that ends on "%s"', (small) => {
      expect(titlesOf(hardcovers(small, 0.4))).toHaveLength(5);
    });

    it.each(['A COURT', 'OFFICE', 'Dune', 'Orphan', 'Bring It On', 'Hold On', 'Lean In', 'Worth Fighting For', 'Written By', 'Away From'])(
      'leaves a band that ends on "%s", which a title can end on, where it is',
      (small) => {
        expect(titlesOf(hardcovers(small, 0.4))).toEqual(['Alpha', 'Beta', small, 'SILVER FLAMES', 'Delta', 'Echo']);
      },
    );

    it('keeps tapes of one size apart that sit 0.4 letters apart, though one is named "Bring It On" (a stack of tapes sits 0.3 to 0.5 apart)', () => {
      const names = ['Alpha', 'Bring It On', 'Charlie', 'Delta', 'Echo'];
      const tapes = names.map((name, i) => line(name, 0.3, 0.1 + i * 0.07, 0.35, 0.05));
      expect(titlesOf(tapes)).toEqual(names);
    });

    it('joins within half the small lettering and no further: 0.6 above stays apart', () => {
      expect(titlesOf(hardcovers('A COURT OF', 0.45))).toHaveLength(5);
      expect(titlesOf(hardcovers('A COURT OF', 0.6))).toEqual(['Alpha', 'Beta', 'A COURT OF', 'SILVER FLAMES', 'Delta', 'Echo']);
      expect(titlesOf(hardcovers('A COURT OF', 2))).toHaveLength(6);
    });

    it('does not change which columns are piles: the same six bands are cut either way', () => {
      expect(hasPile(hardcovers('A COURT OF', 0.4), T)).toBe(true);
      expect(hasPile(hardcovers('A COURT OF', 0.4), { ...T, ...BEFORE_TWO_ROWS })).toBe(true);
    });

    it('is off when the gap needed is below zero: the band stands alone, as it did', () => {
      expect(titlesOf(hardcovers('A COURT OF', 0.4), { ...T, ...BEFORE_TWO_ROWS })).toEqual(['Alpha', 'Beta', 'A COURT OF', 'SILVER FLAMES', 'Delta', 'Echo']);
    });
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

  it('does not cut a column of 5 or more lines wider than half the frame: that is a cover filling it', () => {
    const wide = Array.from({ length: 6 }, (_, i) => line(`Line ${i}`, 0.1, 0.1 + i * 0.08, 0.8, 0.05));
    expect(splitIntoPieces(wide, [], C.splitting, T).pieces).toHaveLength(1);
    const justOver = Array.from({ length: 5 }, (_, i) => line(`Line ${i}`, 0.2, 0.1 + i * 0.08, 0.55, 0.05));
    expect(splitIntoPieces(justOver, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('does not cut a column of 4 lines as wide as a cover filling the frame', () => {
    const wide = Array.from({ length: 4 }, (_, i) => line(`Line ${i}`, 0.1, 0.1 + i * 0.08, 0.8, 0.05));
    expect(splitIntoPieces(wide, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('#618: cuts a small stack of 4 even though it is wider than half the frame', () => {
    const wide = Array.from({ length: 4 }, (_, i) => line(`Wide ${i}`, 0.1, 0.1 + i * 0.08, 0.55, 0.05));
    const { pieces } = splitIntoPieces(wide, [], C.splitting, T);
    expect(titles(pieces)).toEqual(wide.map((l) => [l.text]));
  });

  it('#618: does not cut a column of 4 lines of one size of lettering that is 0.65 wide: past the width a small stack stands', () => {
    const wider = Array.from({ length: 4 }, (_, i) => line(`Wider ${i}`, 0.15, 0.1 + i * 0.08, 0.65, 0.05));
    expect(splitIntoPieces(wider, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('#618: does not cut 3 lines whose lettering differs a lot in size, the look of a cover with its tagline, author and title', () => {
    // 0.055, 0.175 and 0.11 tall, 0.4 of a letter apart: the shape of a real recorded cover (paperback-01), which a stack of 3 tapes
    // (one size of lettering on each) never has.
    const cover = [line('NEW YORK TIMES BESTSELLER', 0.25, 0.2, 0.5, 0.055), line('AUTHOR NAME', 0.27, 0.28, 0.46, 0.175), line('THE TITLE', 0.28, 0.5, 0.44, 0.11)];
    expect(splitIntoPieces(cover, [], C.splitting, T).pieces).toHaveLength(1);
  });

  it('#618: cuts 3 lines whose lettering is about one size, however the sizes drift a little', () => {
    const stack = [line('Tape A', 0.3, 0.1, 0.35, 0.05), line('Tape B', 0.3, 0.18, 0.35, 0.04), line('Tape C', 0.3, 0.26, 0.35, 0.045)];
    expect(titles(splitIntoPieces(stack, [], C.splitting, T).pieces)).toEqual([['Tape A'], ['Tape B'], ['Tape C']]);
  });

  it('#637: cuts a stack of four tapes whose smallest lettering is 0.46 of its biggest (the recorded shelf-12 stack), though three would stay one', () => {
    // Four tapes in one column 0.305 wide: their gaps (0.88, 0.65 and 0.24 letters) pass, but a small stack needs lettering of one size
    // (0.5), and the smallest of these is 0.46 of the biggest. A stack of four needs 0.4, since only a cover's tagline, author and title
    // sit as far apart in size as 0.32.
    const stack = [
      line('RED OCTOBER', 0.0031, 0.2917, 0.1353, 0.0216), line('THE HUNT FOR', 0.0034, 0.2758, 0.1386, 0.0197),
      line('20 Quer Diangp Nontdl.', 0.0374, 0.3548, 0.2701, 0.0473), line('2°00 Cнeт Dвenep Noнl.', 0.0384, 0.3547, 0.2701, 0.045),
      line('Little People WONTEWRES', 0.1246, 0.4476, 0.1619, 0.0282), line('Little People', 0.1254, 0.4517, 0.1118, 0.0198),
      line("Darby O'Gill", 0.1257, 0.4338, 0.1069, 0.0213), line('IID0 KqrG', 0.1274, 0.4328, 0.1058, 0.0194),
      line('HER GRODVEBACK', 0.079, 0.5034, 0.2179, 0.025), line('HOWSTELLAGOT', 0.0368, 0.4826, 0.2054, 0.0203),
    ];
    const { pieces } = splitIntoPieces(stack, [], C.splitting, T);
    expect(pieces.map((p) => p.title)).toEqual(['THE HUNT FOR RED OCTOBER', '20 Quer Diangp Nontdl.', "Darby O'Gill Little People WONTEWRES", 'HOWSTELLAGOT HER GRODVEBACK']);
    expect(splitIntoPieces(stack, [], C.splitting, { ...T, ...BEFORE_637 }).pieces).toHaveLength(1);
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
      expect(hasPile(left.slice(0, 3), T)).toBe(true);
      expect(hasPile(left.slice(0, 2), T)).toBe(false);
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
    // Upright DVD spines and no pile of flat items; a tape of "Charlotte's Web" lies across their tops. The before counts rose with the
    // spine-title rule (shelfTitle.ts, #617), which the split shares; shelfTitle.test.ts owns that. The after moved when the tape became an
    // item of its own instead of joining the CRITTERS spine under its center (items 12 to 13, found 5 to 6: Critters).
    'vhs-dvd-shelf-02': { before: { items: 12, found: 5 }, after: { items: 13, found: 6 } },
    // A pile of hardcovers lies under a row of upright books; the 2-line titles of the pile now come out whole.
    // Both counts moved with the spine-title rule (shelfTitle.ts, #617), which the split shares; shelfTitle.test.ts owns it. The after
    // moved again when the frame was cut into its two shelf rows (items 32 to 40, found 19 to 25): 6 titles gained and none lost. The cut
    // lost "And Now, Back to You" until a shorter line beside a taller one joined its column (#655, 24 to 25). The shelf rows describe
    // below owns that. The items then fell from 40 to 39: the small "A COURT OF" above the subtitle of A Court of Silver Flames is read
    // with it now, not as an item of its own (the #637 describe below owns that).
    'accept-shelf-04': { before: { items: 24, found: 16 }, after: { items: 39, found: 25 } },
    // Upright rows with no pile. The two rows of "Indian FOR Everyone" were two items, each read alone, and are one spine now (items 19 to
    // 18, found 14 to 15); splitting.test.ts owns the rule.
    'accept-shelf-01': { before: { items: 19, found: 14 }, after: { items: 18, found: 15 } },
    'accept-shelf-02': { before: { items: 17, found: 15 }, after: { items: 17, found: 15 } },
  };

  it.each(Object.entries(COUNTS))('%s: items and truth titles found before and after', async (id, counts) => {
    const before = await read(id, WITHOUT_PILES);
    const after = await read(id, T);
    expect({ items: before.items, found: before.found }).toEqual(counts.before);
    expect({ items: after.items, found: after.found }).toEqual(counts.after);
  });

  it.each(['vhs-dvd-shelf-01', 'vhs-dvd-shelf-02', 'accept-shelf-04'])('%s: more truth titles are found, and more items are read, than before', async (id) => {
    const before = await read(id, WITHOUT_PILES);
    const after = await read(id, T);
    expect(after.found).toBeGreaterThan(before.found);
    expect(after.items).toBeGreaterThan(before.items);
  });

  // Upright rows with no pile: the split changes only where the two rows of one title were two items (accept-shelf-01 has one such title).
  it.each([
    ['accept-shelf-01', 1],
    ['accept-shelf-02', 0],
  ])('%s: finds no fewer truth titles than before, and reads %i fewer items', async (id, joined) => {
    const before = await read(id, WITHOUT_PILES);
    const after = await read(id, T);
    expect(after.found).toBeGreaterThanOrEqual(before.found);
    expect(after.items).toBe(before.items - joined);
  });

  describe('a bookcase shot with two shelves in the frame is cut into its two shelf rows', () => {
    /** The split before the frame was cut into rows: a spine on the lower shelf joined the spine above it, whose x it shares. */
    const WITHOUT_ROWS = { ...T, shelfRowGap: Infinity, ...BEFORE_637, ...BEFORE_LYING, ...BEFORE_TWO_ROWS };

    // The stretch of y that no tall line covers, from one row's last tall line to the next row's first: 10.0, 5.4 and 4.5 letters (the
    // median tall line's width). The single-row photos show 2.3 to 3.6 letters at their edges, or too few lines beyond to count as a row.
    const GAPS: Record<string, { from: number; to: number }> = {
      'shelf-01': { from: 0.444, to: 0.615 },
      'shelf-02': { from: 0.433, to: 0.545 },
      'accept-shelf-04': { from: 0.487, to: 0.583 },
    };

    // Items and truth titles found with the cut off, then on. The title counts are the whole frame's; with Vision's rectangles the
    // pieces are cut inside each rectangle, and the gain on the phone is measured by the next eval run. accept-shelf-04's 19 to 25 is
    // 6 gained (Throne of Glass, Crown of Midnight, Heir of Fire, Queen of Shadows, Empire of Storms, Kingdom of Ash) and none lost:
    // "And Now, Back to You" was lost to the cut until #655 (see below), which made it 19 to 24. shelf-02's 21 to 24 (items 36 to 44) is
    // the hardcover pile of A Court of Thorns and Roses books, cut into its 8 bands once a pile of 5 or more may have a median gap of 1.25
    // letters (#637): A Court of Wings and Ruin, A Court of Mist and Fury and A Court of Thorns and Roses, and none lost. shelf-01's 12 to 14
    // (items 36 to 38) is two titles whose rows were two columns, each too narrow to be a spine, joined into one that stands: "The Exquisite
    // Torment of Loving Your Enemy" and "The Irresistible Urge to Fall for Your Enemy"; none lost. (With Vision's rectangles, the second
    // title gained is "Rose in Chains" instead, which no longer shares a leftover piece with them; neither path loses a title.) The items of shelf-02 (44 to 42) and
    // accept-shelf-04 (40 to 39) fell as the small "A COURT OF" of 2 and 1 hardcovers of A Court of Silver Flames joined their subtitles.
    const ROW_COUNTS: Record<string, { without: { items: number; found: number }; with: { items: number; found: number } }> = {
      'shelf-01': { without: { items: 32, found: 11 }, with: { items: 38, found: 14 } },
      'shelf-02': { without: { items: 26, found: 16 }, with: { items: 42, found: 24 } },
      'accept-shelf-04': { without: { items: 32, found: 19 }, with: { items: 39, found: 25 } },
    };

    const linesOf = (id: string) => photos[id].lines.map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));
    /** The pieces that hold a line centered above the gap and one centered below it. */
    const crossing = (id: string, local: typeof T) => {
      const { from, to } = GAPS[id];
      const mid = (from + to) / 2;
      const side = (l: TextLine) => l.box.y + l.box.height / 2 > mid;
      return splitIntoPieces(linesOf(id), [], C.splitting, local).pieces.filter((p) => new Set(p.lines.map(side)).size > 1).length;
    };

    it.each(Object.entries(ROW_COUNTS))('%s: items and truth titles found without and with the cut', async (id, counts) => {
      const without = await read(id, WITHOUT_ROWS);
      const cut = await read(id, T);
      expect({ items: without.items, found: without.found }).toEqual(counts.without);
      expect({ items: cut.items, found: cut.found }).toEqual(counts.with);
    });

    it.each(Object.keys(GAPS))('%s: no item holds lines from both sides of the gap between its shelf rows', (id) => {
      expect(crossing(id, WITHOUT_ROWS)).toBeGreaterThan(0);
      expect(crossing(id, T)).toBe(0);
    });

    // #655: the cut once lost one title it used to find. Without the cut, one 10-line piece held "BACK TO YOU", "AND NOW," and the lower
    // shelf's "HEIR OF FIRE" among others, and the title read from it was found. Cut apart, the lower spine no longer widened the column, so
    // "AND NOW," (x 0.642, the title's first line, shorter than "BACK TO YOU" at x 0.628 and sharing 43% of its width) started a column of
    // its own, and the next spine's "TARAH DEWITT" (x 0.656) joined that. A shorter line beside a taller one now joins the taller line's
    // column (splitting.ts, spineShortLineOverlap), without widening it.
    it('accept-shelf-04 finds "And Now, Back to You" with the cut: "AND NOW," stays with "BACK TO YOU", not with TARAH DEWITT', async () => {
      const titlesWith = async (local: typeof T) => {
        const vision = new RecordingVision({ [STILL.uri]: { text: linesOf('accept-shelf-04'), rectangles: [] } });
        const engine = new VisionPipelineEngine({ vision, settings: () => ({ ...S, local }), newId: testIds });
        return (await engine.read(STILL, () => {}, { shotKind: 'shelf_segment' })).items.map((i) => i.title);
      };
      expect((await titlesWith(WITHOUT_ROWS)).some((t) => has(t, 'And Now, Back to You'))).toBe(true);
      expect((await titlesWith(T)).some((t) => has(t, 'And Now, Back to You'))).toBe(true);
      const { pieces } = splitIntoPieces(linesOf('accept-shelf-04'), [], C.splitting, T);
      const andNow = pieces.find((p) => p.lines.some((l) => l.text === 'AND NOW,'))?.lines.map((l) => l.text);
      expect(andNow).toContain('BACK TO YOU');
      expect(andNow).not.toContain('TARAH DEWITT');
    });

    it('finds more truth titles and reads more items in each of the three photos', async () => {
      for (const id of Object.keys(GAPS)) {
        const without = await read(id, WITHOUT_ROWS);
        const cut = await read(id, T);
        expect([id, cut.found > without.found, cut.items > without.items]).toEqual([id, true, true]);
      }
    });
  });

  it('reads a pile of tapes as one item per tape: the tape stack of the first photo is no longer one lump', async () => {
    const { items } = await read('vhs-dvd-shelf-01', T);
    expect(items).toBeGreaterThanOrEqual(photos['vhs-dvd-shelf-01'].truth.length - 5); // 5 of the 32 are unreadable
  });

  describe('#637: a pile of hardcovers lying flat, whose books sit further apart than tapes do', () => {
    // The recorded shelf-02 pile of A Court of Thorns and Roses books: 8 bands in one column 0.146 wide. Hardcover spines are thick against
    // their letters, so the gaps run 0.14, 1.09, 2.37, 0.11, 1.61, 1.18 and 0.51 letters, a median of 1.09 where a tape stack's is 0.3 to 0.5.
    // On two of the books "A COURT OF" sits 0.003 to 0.004 above "SILVER FLAMES", further than a line may be from its band, so each comes
    // out as two bands ("ACourt of" over "Silver Flames", and "A COURT OF" over "ILVER FLAMES"). A band that ends on a joining word reads
    // with the band below it, which makes each one item.
    const PILE = [63, 66, 67, 68, 69, 70, 71, 75, 76, 77, 80, 81, 82, 83, 87, 90];
    const pile = () => PILE.map((i) => photos['shelf-02'].lines[i]).map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));

    it('holds the lines it should (the recorded indices have not drifted)', () => {
      expect(pile().map((l) => l.text)).toEqual([
        'Silver Flames', 'FROST AND STARLIGH', 'FROST AI', 'ACourt of', 'A CourTOi', 'THORNS AND ROSES', 'WINGS AND RUIN', 'MIST AND FURY',
        'A COURT OF', 'A COURT OF', 'ND STARLIGHT', 'SILVER FLAMES', 'A COURT OF', 'ILVER FLAMES', 'A COURT OF', 'COURT OF',
      ]);
    });

    it('splits one item per book: the pile has a median gap of 1.09 letters, within the 1.25 a pile of 5 or more may have', () => {
      const { pieces } = splitIntoPieces(pile(), [], C.splitting, T);
      expect(pieces).toHaveLength(6);
      expect(pieces.map((p) => p.title)).toEqual([
        'ACourt of Silver Flames', 'ND STARLIGHT', 'A COURT OF ILVER FLAMES', 'A COURT OF WINGS AND RUIN', 'A COURT OF MIST AND FURY',
        'A COURT OF THORNS AND ROSES',
      ]);
      expect(splitIntoPieces(pile(), [], C.splitting, { ...T, ...BEFORE_637 }).pieces).toHaveLength(1);
    });

    it('cuts "A COURT OF" from the subtitle below it when the joining-word rule is off: 8 items, two of them half titles', () => {
      const { pieces } = splitIntoPieces(pile(), [], C.splitting, { ...T, ...BEFORE_TWO_ROWS });
      expect(pieces.map((p) => p.title)).toEqual([
        'ACourt of', 'Silver Flames', 'ND STARLIGHT', 'A COURT OF', 'ILVER FLAMES', 'A COURT OF WINGS AND RUIN', 'A COURT OF MIST AND FURY',
        'A COURT OF THORNS AND ROSES',
      ]);
    });

    it('keeps two recorded bands of a pile apart when the first does not end on a joining word, though they sit 0.3 letters apart (shelf-01: MEDUSA AYANA GRAY over HEKATE THE WITCH)', () => {
      // The three tapes above are made up so that the recorded lines stand in a pile of five bands.
      const tapes = Array.from({ length: 3 }, (_, i) => line(`Tape ${i}`, 0.6568, 0.2279 + i * 0.05, 0.1821, 0.0252));
      const medusa = [
        line('MEDUSA AYANA GRAY', 0.6568, 0.3779, 0.1821, 0.0252),
        line('MEDUSA', 0.6584, 0.3798, 0.0741, 0.0136),
        line('HEKATE THE WITCH', 0.6904, 0.4107, 0.1584, 0.0311),
      ];
      const { pieces } = splitIntoPieces([...tapes, ...medusa], [], C.splitting, T);
      expect(pieces.map((p) => p.title)).toEqual(['Tape 0', 'Tape 1', 'Tape 2', 'MEDUSA AYANA GRAY', 'HEKATE THE WITCH']);
    });
  });

  describe('a tape lying across the tops of the spines is not joined to the spine under it', () => {
    // vhs-dvd-shelf-02: a tape of "Charlotte's Web" (0.679 wide, 0.126 tall) lies across the tops of a row of DVDs. Its center is over the
    // "CRITTERS" spine, and the title read for that piece was the tape's.
    const lines = () => photos['vhs-dvd-shelf-02'].lines.map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));
    const piecesWith = (text: string, local: typeof T) =>
      splitIntoPieces(lines(), [], C.splitting, local).pieces.filter((p) => p.lines.some((l) => l.text === text));

    it("keeps the Charlotte's Web tape out of the piece that holds CRITTERS, and makes it an item of its own", () => {
      expect(photos['vhs-dvd-shelf-02'].lines[17].text).toBe("Charlotte's Web");
      expect(photos['vhs-dvd-shelf-02'].lines[32].text).toBe('CRITTERS');
      const critters = piecesWith('CRITTERS', T);
      expect(critters).toHaveLength(1);
      expect(critters[0].lines.map((l) => l.text)).not.toContain("Charlotte's Web");
      expect(piecesWith("Charlotte's Web", T)[0].lines.map((l) => l.text)).toEqual(["Charlotte's Web"]);
    });

    it("joined them before: the piece that held CRITTERS held the tape's title too", () => {
      const critters = piecesWith('CRITTERS', { ...T, ...BEFORE_LYING });
      expect(critters[0].lines.map((l) => l.text)).toContain("Charlotte's Web");
    });
  });

  describe("an author's name printed flat across a hardcover's spine is no tape lying across the tops", () => {
    // shelf-02: on two Crescent City hardcovers "SARAH J." is printed flat above the sideways title, 0.0408 and 0.0392 wide on spines
    // 0.0218 and 0.0203 wide: 1.87 and 1.93 columns. The cut needs a line 2 columns wide (lyingLineWidth), so these stand 0.07 columns inside
    // it, the narrowest margin of the rule: at 1.93 or less one of them would be cut out as an item of its own, at 1.87 or less both. The
    // titles found do not move between 1.5 and 2, so only these lines (and shelf-02's 44 items) guard the number.
    const AUTHORS = [20, 35];
    const all = photos['shelf-02'].lines.map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));
    const piecesOf = (local: typeof T) => {
      const { pieces } = splitIntoPieces(all, [], C.splitting, local);
      return AUTHORS.map((i) => pieces.find((p) => p.lines.includes(all[i]))!);
    };

    it('holds the lines it should (the recorded indices have not drifted)', () => {
      expect(AUTHORS.map((i) => [all[i].text, all[i].box.x, all[i].box.width])).toEqual([['SARAH J.', 0.1381, 0.0408], ['SARAH J.', 0.202, 0.0392]]);
    });

    it('keeps both SARAH J. lines in their Crescent City spines, never an item of their own', () => {
      for (const p of piecesOf(T)) {
        expect(p.lines.length).toBeGreaterThan(1);
        expect(p.lines.map((l) => l.text)).toContain('CRESCENT');
      }
    });

    it('would cut both out as items of their own with the width needed at 1.85 columns', () => {
      expect(piecesOf({ ...T, lyingLineWidth: 1.85 }).map((p) => p.lines.map((l) => l.text))).toEqual([['SARAH J.'], ['SARAH J.']]);
    });
  });

  describe('small stacks cut out of a real pile photo, each window of consecutive bands on its own', () => {
    /** The photo's bands as the pile cut them, column by column (a band whose top is above the last one's starts the next column). */
    function pileColumns(id: string): Piece[][] {
      const lines = photos[id].lines.map((l) => line(l.text, l.x, l.y, l.width, l.height, confidence(l.text)));
      const columns: Piece[][] = [];
      for (const band of splitIntoPieces(lines, [], C.splitting, T).pieces.filter((p) => p.title !== undefined)) {
        const column = columns[columns.length - 1];
        if (column && band.box.y > column[column.length - 1].box.y) column.push(band);
        else columns.push([band]);
      }
      return columns;
    }

    /** Windows of `size` consecutive bands of one column, how many there are and how many split into one item per band. */
    function windowsOf(id: string, size: number) {
      let windows = 0;
      let split = 0;
      for (const column of pileColumns(id)) {
        for (let i = 0; i + size <= column.length; i++) {
          windows += 1;
          const lines = column.slice(i, i + size).flatMap((band) => band.lines);
          if (splitIntoPieces(lines, [], C.splitting, T).pieces.length === size) split += 1;
        }
      }
      return { windows, split };
    }

    // KNOWN LIMIT (#618, #637): a stack of three splits only about 8 of 19 times. Without the lettering check 10 of the 19 three-band windows
    // would, but the tapes' smallest-to-biggest lettering runs from 0.31 to 0.82 while the cover paperback-01 has 0.32, and that cover's three
    // bands match real three-tape windows on every other signal tried (width, gap, alignment, pitch and fill), so no cut-off separates them.
    // A stack of four needs 0.4 (stackFourLetters), since no recorded cover has four bands that pass the width and gap rules: 11 of the 16
    // four-band windows split, where 14 would with no check at all. The real hardcover pile (accept-shelf-04) splits 0 of 6 three-band
    // windows and 1 of 5 four-band ones, because the gap rule rejects the rest. The three-band count is a floor, not the goal.
    it('splits 11 of the 16 four-tape windows and at least 8 of the 19 three-tape windows of the real tape piles into one item per tape', () => {
      const fours = windowsOf('vhs-dvd-shelf-01', 4);
      const threes = windowsOf('vhs-dvd-shelf-01', 3);
      expect([fours.windows, threes.windows]).toEqual([16, 19]);
      expect(fours.split).toBe(11);
      expect(threes.split).toBeGreaterThanOrEqual(8);
    });
  });

  it('wants the extra upside-down pass for the four photos with a pile (three of them books lying flat) and for none of the others', () => {
    const wanted = Object.entries(photos)
      .filter(([, p]) => hasPile(p.lines.map((l) => line(l.text, l.x, l.y, l.width, l.height)), T))
      .map(([id]) => id);
    expect(wanted).toEqual(['vhs-dvd-shelf-01', 'accept-shelf-04', 'shelf-01', 'shelf-02']);
  });

  /** The recorded covers, each line's box moved and sized about the frame's center, so the cover is shot from further away. */
  const recordedCovers = covers as Record<string, { lines: { text: string; x: number; y: number; width: number; height: number }[] }>;
  const shrunk = (cover: { lines: { text: string; x: number; y: number; width: number; height: number }[] }, scale: number): TextLine[] =>
    cover.lines.map((l) => line(l.text, 0.5 + (l.x - 0.5) * scale, 0.5 + (l.y - 0.5) * scale, l.width * scale, l.height * scale));

  it('never cuts a real single cover shot from further away: shrunk to 90, 80 and 70%, none of the 12 splits into more pieces than before #618', () => {
    // Ten of the twelve are one piece at every size. The other two (game-box-01, paperback-01) split into columns of tall text at the
    // edge even at full size, and what they split into must not grow.
    for (const [id, cover] of Object.entries(recordedCovers)) {
      for (const scale of [1, 0.9, 0.8, 0.7]) {
        const lines = shrunk(cover, scale);
        const before = splitIntoPieces(lines, [], C.splitting, BEFORE_618).pieces.length;
        expect([id, scale, splitIntoPieces(lines, [], C.splitting, T).pieces.length]).toEqual([id, scale, before]);
      }
    }
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
      if (!options?.regions) passes.push(options?.orientations ?? [0]); // the whole-picture passes: a tile read is counted in shelfTitle.test.ts
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
