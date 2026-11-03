import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_CONFIG as C } from './defaults';
import { line } from './fakes';
import { shelfTitleOf } from './shelfTitle';
import { splitIntoPieces } from './splitting';

describe('splitting Vision text into items', () => {
  it('turns 12 spine lines in 12 columns into 12 items, left to right', () => {
    const lines = Array.from({ length: 12 }, (_, i) => line(`Title ${i}`, 0.04 + i * 0.075, 0.1, 0.05, 0.6));
    const shuffled = [...lines].reverse();
    const { pieces } = splitIntoPieces(shuffled, [], C.splitting, T);
    expect(pieces.map((p) => p.lines[0].text)).toEqual(lines.map((l) => l.text));
  });

  it('keeps the lines of one spine together, top to bottom', () => {
    const lines = [line('Herbert', 0.1, 0.7, 0.05, 0.2), line('Dune', 0.1, 0.1, 0.05, 0.5), line('Emma', 0.3, 0.1, 0.05, 0.5)];
    const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
    expect(pieces.map((p) => p.lines.map((l) => l.text))).toEqual([['Dune', 'Herbert'], ['Emma']]);
  });

  it('turns a single rectangle into one item', () => {
    const lines = [line('Dune', 0.3, 0.2, 0.4, 0.1), line('Frank Herbert', 0.3, 0.3, 0.4, 0.05)];
    const { pieces } = splitIntoPieces(lines, [{ x: 0.1, y: 0.1, width: 0.8, height: 0.8 }], C.splitting, T);
    expect(pieces).toHaveLength(1);
    expect(pieces[0].lines.map((l) => l.text)).toEqual(['Dune', 'Frank Herbert']);
    expect(pieces[0].box).toEqual({ x: 0.1, y: 0.1, width: 0.8, height: 0.8 });
  });

  it('counts a rectangle with no text as unreadable', () => {
    const lines = [line('Dune', 0.1, 0.2, 0.1, 0.1)];
    const rects = [
      { x: 0.05, y: 0.1, width: 0.3, height: 0.8 },
      { x: 0.5, y: 0.1, width: 0.3, height: 0.8 },
    ];
    const split = splitIntoPieces(lines, rects, C.splitting, T);
    expect(split.pieces).toHaveLength(1);
    expect(split.unreadableCount).toBe(1);
  });

  it('reads flat text with no rectangles as the whole frame', () => {
    const lines = [line('Super Mario 64', 0.2, 0.2, 0.6, 0.1), line('Nintendo 64', 0.3, 0.4, 0.4, 0.1)];
    const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
    expect(pieces).toHaveLength(1);
    expect(pieces[0].lines).toHaveLength(2);
  });

  it('#504: nested rectangles on a cover read as one item, never one shard per quad', () => {
    const lines = [
      line('Author of A Winter at the Lighthouse', 0.2, 0.08, 0.6, 0.06),
      line('LANTERN', 0.35, 0.2, 0.3, 0.08),
      line('MARSH', 0.36, 0.28, 0.28, 0.07),
      line('LANTERN ROAD', 0.25, 0.35, 0.5, 0.1),
      line('Weaver', 0.3, 0.55, 0.4, 0.06),
      line('QRLT VSOT', 0.4, 0.7, 0.2, 0.05),
    ];
    const rects = [
      { x: 0.05, y: 0.05, width: 0.9, height: 0.9 },
      { x: 0.1, y: 0.06, width: 0.8, height: 0.75 },
      { x: 0.15, y: 0.3, width: 0.7, height: 0.4 },
      { x: 0.2, y: 0.07, width: 0.6, height: 0.1 },
      { x: 0.25, y: 0.5, width: 0.5, height: 0.15 },
      { x: 0.3, y: 0.65, width: 0.4, height: 0.12 },
    ];
    const { pieces, unreadableCount } = splitIntoPieces(lines, rects, C.splitting, T);
    expect(pieces).toHaveLength(1);
    expect(pieces[0].lines).toHaveLength(6);
    expect(unreadableCount).toBe(0);
  });

  it('merges only the rectangles that overlap: a nested pair plus a disjoint spine read as two items', () => {
    const lines = [line('Left', 0.1, 0.2, 0.2, 0.1), line('Right', 0.65, 0.2, 0.2, 0.1)];
    const rects = [
      { x: 0.05, y: 0.05, width: 0.4, height: 0.8 },
      { x: 0.1, y: 0.1, width: 0.3, height: 0.7 },
      { x: 0.6, y: 0.05, width: 0.3, height: 0.8 },
    ];
    const { pieces } = splitIntoPieces(lines, rects, C.splitting, T);
    expect(pieces.map((p) => p.lines.map((l) => l.text))).toEqual([['Left'], ['Right']]);
  });

  it('#504: tall lines scattered over several heights read as the whole frame, not spine columns', () => {
    const lines = [
      line('LANTERN', 0.02, 0.05, 0.1, 0.25),
      line('ROAD', 0.18, 0.5, 0.1, 0.25),
      line('Author of', 0.34, 0.1, 0.1, 0.25),
      line('A Winter at', 0.5, 0.55, 0.1, 0.25),
      line('Weaver', 0.66, 0.05, 0.1, 0.25),
      line('QRLT', 0.82, 0.5, 0.1, 0.25),
    ];
    const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
    expect(pieces).toHaveLength(1);
    expect(pieces[0].lines).toHaveLength(6);
  });

  it('keeps disjoint spine rectangles as one item each', () => {
    const lines = [line('Dune', 0.07, 0.2, 0.1, 0.5), line('Emma', 0.42, 0.2, 0.1, 0.5)];
    const rects = [
      { x: 0.02, y: 0.05, width: 0.2, height: 0.8 },
      { x: 0.37, y: 0.05, width: 0.2, height: 0.8 },
    ];
    const { pieces } = splitIntoPieces(lines, rects, C.splitting, T);
    expect(pieces.map((p) => p.lines.map((l) => l.text))).toEqual([['Dune'], ['Emma']]);
  });

  describe('a shelf row of spines', () => {
    const spine = (i: number, y = 0.05, height = 0.5) => line(`Spine ${i}`, 0.04 + i * 0.15, y, 0.06, height);
    const titles = (pieces: { lines: { text: string }[] }[]) => pieces.map((p) => p.lines.map((l) => l.text));

    it('splits a shelf with one short book beside tall ones into one item per spine', () => {
      // The short book's text sits low, so it shares no vertical band with its neighbors: 5 of the 7 neighbor pairs still do.
      const lines = Array.from({ length: 8 }, (_, i) => (i === 3 ? line('Spine 3', 0.04 + i * 0.11, 0.68, 0.05, 0.15) : line(`Spine ${i}`, 0.04 + i * 0.11, 0.1, 0.05, 0.5)));
      const { pieces } = splitIntoPieces([...lines].reverse(), [], C.splitting, T);
      expect(titles(pieces)).toEqual(lines.map((l) => [l.text]));
    });

    it('splits the spines when most, not all, neighbors share a band', () => {
      // Three columns stand high and the fourth low: 3 of the 4 neighbor pairs share a band.
      const lines = [spine(0), spine(1), spine(2), spine(3, 0.6, 0.3), spine(4, 0.6, 0.3)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual(lines.map((l) => [l.text]));
    });

    it('reads the frame whole when too few neighbors share a band', () => {
      // High, high, low, high: only 1 of 3 neighbor pairs shares a band, under the share a row needs.
      const lines = [spine(0), spine(1), spine(2, 0.6, 0.3), spine(3)];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
      expect(pieces).toHaveLength(1);
      expect(pieces[0].lines).toHaveLength(4);
    });

    it('#618: splits a close-up row where only half the neighbors share a band, because titles and authors sit at different heights', () => {
      // Two spines whose title runs high, two whose title and author together sit low, then one back up high: only the 2 pairs of
      // like neighbors share a band, so 2 of the 4 pairs do (50%). A close-up shelf row is this uneven, book to book, yet it is still
      // one row, not five scattered lines.
      const lines = [spine(0), spine(1), spine(2, 0.6, 0.3), spine(3, 0.6, 0.3), spine(4)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual(lines.map((l) => [l.text]));
    });

    it('#618: splits a close-up row of three spines where only one pair shares a band into one item per spine', () => {
      // High, high, low: 1 of the 2 pairs shares a band (50%). No recorded single cover reads like this, even shrunk to 70% in the frame
      // (stackedSpines.test.ts pins that against the shelf before #618), so a row of three is split rather than kept as one lump.
      const lines = [spine(0), spine(1), spine(2, 0.6, 0.3)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual(lines.map((l) => [l.text]));
    });

    describe('around the share of neighbors that must share a band', () => {
      /** One narrow column per letter: H stands high (0.05 to 0.55), L low (0.6 to 0.9), so two neighbors share a band when they match. */
      const row = (pattern: string) =>
        [...pattern].map((c, i) => line(`Spine ${i}`, 0.02 + i * 0.06, c === 'H' ? 0.05 : 0.6, 0.04, c === 'H' ? 0.5 : 0.3));

      it('#618: splits a long row where 7 of 15 neighbor pairs (47%) share a band', () => {
        const lines = row('HHLLHLLHHLLHLLHH');
        expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual(lines.map((l) => [l.text]));
      });

      it('#504: reads scattered columns as the whole frame when 2 of 5 neighbor pairs (40%) share a band', () => {
        const lines = row('HHLHHL');
        const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
        expect(pieces).toHaveLength(1);
        expect(pieces[0].lines).toHaveLength(lines.length);
      });
    });

    it('splits the spines and never lends flat stacks below them to the spine above, even when the flat lines outnumber the spines', () => {
      const spines = Array.from({ length: 6 }, (_, i) => spine(i));
      // Two piles of items lying flat under the row: 4 titles at the left, 3 at the right. Their 7 lines outnumber the 6 spines and the
      // left pile sits right under the third and fourth spines' columns, yet every flat line belongs to its own item, never to a spine.
      const left = Array.from({ length: 4 }, (_, i) => line(`Left ${i + 1}`, 0.2, 0.62 + i * 0.08, 0.3, 0.05));
      const right = Array.from({ length: 3 }, (_, i) => line(`Right ${i + 1}`, 0.6, 0.62 + i * 0.08, 0.3, 0.05));
      const { pieces, unreadableCount } = splitIntoPieces([...right, ...left, ...spines], [], C.splitting, T);
      expect(titles(pieces.slice(0, 6))).toEqual(spines.map((l) => [l.text]));
      expect(titles(pieces.slice(6))).toEqual([...left, ...right].map((l) => [l.text]));
      expect(pieces.reduce((n, p) => n + p.lines.length, 0)).toBe(left.length + right.length + spines.length);
      expect(unreadableCount).toBe(0);
    });

    it('lets a short line across a spine join that spine', () => {
      const lines = [line('Dune', 0.1, 0.1, 0.05, 0.4), line('Herbert', 0.1, 0.45, 0.05, 0.03), line('Emma', 0.3, 0.1, 0.05, 0.4)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual([['Dune', 'Herbert'], ['Emma']]);
    });

    it('keeps a tall line in a column too narrow to be a spine instead of dropping it', () => {
      const lines = [spine(0), spine(1), spine(2), line('Smudge', 0.9, 0.05, 0.005, 0.1)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual([['Spine 0'], ['Spine 1'], ['Spine 2'], ['Smudge']]);
    });

    it('does not make an extra piece when every line is on a spine', () => {
      const lines = [spine(0), spine(1), spine(2)];
      expect(splitIntoPieces(lines, [], C.splitting, T).pieces).toHaveLength(3);
    });

    it('reads one tall line among flat text as the whole frame', () => {
      const lines = [line('Vertical', 0.02, 0.1, 0.05, 0.5), line('Super Mario 64', 0.2, 0.2, 0.6, 0.1), line('Nintendo 64', 0.3, 0.4, 0.4, 0.1)];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
      expect(pieces).toHaveLength(1);
      expect(pieces[0].lines).toHaveLength(3);
    });
  });

  describe('a frame with two shelf rows', () => {
    const titles = (pieces: { lines: { text: string }[] }[]) => pieces.map((p) => p.lines.map((l) => l.text));
    /** The spine at column `i` of a row: its lettering is 0.05 wide, so a row of them leaves gaps measured in 0.05s. */
    const spine = (text: string, i: number, y: number, height = 0.2) => line(text, 0.04 + i * 0.15, y, 0.05, height);

    it('splits two rows of spines standing at the same x into one item per spine, the top row first', () => {
      // A column joins tall lines on x alone, so each bottom spine used to share a column with the top spine above it: 3 items, not 6.
      const top = Array.from({ length: 3 }, (_, i) => spine(`Top ${i}`, i, 0.05));
      const bottom = Array.from({ length: 3 }, (_, i) => spine(`Bottom ${i}`, i, 0.6));
      const { pieces } = splitIntoPieces([...bottom, ...top].reverse(), [], C.splitting, T);
      expect(titles(pieces)).toEqual([...top, ...bottom].map((l) => [l.text]));
    });

    it('cuts the recorded two spines of shelf-02 (a book on the upper shelf, another on the lower) into two items', () => {
      // Seven pieces of the build-136 run held both: this piece (x 0.69 to 0.74) put "NOT ANOTHER LOVE SONG" and "QUEEN OF SHADOWS" together.
      const lines = [
        line('NOTVE SONG', 0.705, 0.117, 0.032, 0.107),
        line('NOT ANOTHER', 0.715, 0.117, 0.02, 0.107),
        line('LOVE SONG', 0.707, 0.131, 0.018, 0.084),
        line('JULIE SOTO', 0.694, 0.29, 0.023, 0.094),
        line('SARAR QUEEN OF SHADOWS', 0.701, 0.554, 0.021, 0.291),
        line('SARAH J.', 0.709, 0.556, 0.013, 0.076),
        line('SARAHJ. QUEEN OF SHADOWS', 0.701, 0.558, 0.024, 0.288),
        line('MAAS', 0.698, 0.57, 0.012, 0.045),
        line('QUEEN OF SHADOWS', 0.702, 0.632, 0.018, 0.215),
      ];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
      expect(titles(pieces).map((p) => [...p].sort())).toEqual([
        ['JULIE SOTO', 'LOVE SONG', 'NOT ANOTHER', 'NOTVE SONG'],
        ['MAAS', 'QUEEN OF SHADOWS', 'SARAH J.', 'SARAHJ. QUEEN OF SHADOWS', 'SARAR QUEEN OF SHADOWS'],
      ]);
      expect(shelfTitleOf(pieces[1].lines, T)?.text).toContain('QUEEN OF SHADOWS');
    });

    it('cuts the recorded two spines of shelf-02 at x 0.63 to 0.67 (AND NOW, BACK TO YOU above HEIR OF FIRE) into two items', () => {
      const lines = [
        line('BANK TO YOU', 0.634, 0.101, 0.038, 0.159),
        line('BACK TO YOU', 0.637, 0.121, 0.021, 0.132),
        line('AND NOW,', 0.649, 0.138, 0.021, 0.095),
        line('II B.K. BORISON I', 0.636, 0.284, 0.022, 0.127),
        line('B.K. BORISON M', 0.637, 0.288, 0.016, 0.122),
        line('B.K. BORISON', 0.638, 0.302, 0.014, 0.09),
        line('SARAH J.', 0.66, 0.56, 0.013, 0.066),
        line('MAAS', 0.65, 0.572, 0.012, 0.045),
        line('HEIR OF FIRE', 0.656, 0.674, 0.016, 0.136),
      ];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
      expect(titles(pieces).map((p) => [...p].sort())).toEqual([
        ['AND NOW,', 'B.K. BORISON', 'B.K. BORISON M', 'BACK TO YOU', 'BANK TO YOU', 'II B.K. BORISON I'],
        ['HEIR OF FIRE', 'MAAS', 'SARAH J.'],
      ]);
      expect(shelfTitleOf(pieces[1].lines, T)?.text).toBe('HEIR OF FIRE');
    });

    it('keeps one shelf row whole when every spine prints its author a little above its title (a gap of 2.6 letters)', () => {
      // A series on one shelf: author at the top of each spine (y 0.21, 0.256 long), title below it from y 0.549. The gap is 0.083 on lettering 0.031 wide.
      const lines = Array.from({ length: 4 }, (_, i) => [
        line(`Author ${i}`, 0.05 + i * 0.12, 0.21, 0.031, 0.256),
        line(`Title ${i}`, 0.05 + i * 0.12, 0.549, 0.031, 0.3),
      ]).flat();
      const { pieces } = splitIntoPieces([...lines].reverse(), [], C.splitting, T);
      expect(titles(pieces).map((p) => [...p].sort())).toEqual(Array.from({ length: 4 }, (_, i) => [`Author ${i}`, `Title ${i}`]));
    });

    it('does not cut when one side of the gap holds a single tall line: a sticker read sideways above a spine stays with it', () => {
      const sticker = line('Staff pick', 0.045, 0.02, 0.03, 0.1);
      const lines = [sticker, ...Array.from({ length: 3 }, (_, i) => spine(`Spine ${i}`, i, 0.35, 0.5))];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
      expect(titles(pieces).map((p) => [...p].sort())).toEqual([['Spine 0', 'Staff pick'], ['Spine 1'], ['Spine 2']]);
    });

    it('does not cut a frame with fewer than 4 tall lines, however far apart they stand', () => {
      const lines = [spine('Spine 0', 0, 0.05), spine('Spine 1', 1, 0.05), spine('Spine 2', 1, 0.7)];
      expect(splitIntoPieces(lines, [], C.splitting, T).pieces).toHaveLength(2);
    });

    it('sends a flat line to the row its center sits in', () => {
      const top = Array.from({ length: 3 }, (_, i) => spine(`Top ${i}`, i, 0.05));
      const bottom = Array.from({ length: 3 }, (_, i) => spine(`Bottom ${i}`, i, 0.6));
      const label = line('Shelf 2', 0.04, 0.7, 0.05, 0.02);
      const { pieces } = splitIntoPieces([...top, ...bottom, label], [], C.splitting, T);
      expect(titles(pieces)).toEqual([['Top 0'], ['Top 1'], ['Top 2'], ['Bottom 0', 'Shelf 2'], ['Bottom 1'], ['Bottom 2']]);
    });

    it('cuts the rows inside a rectangle too, since Vision may box the whole bookcase', () => {
      const lines = [spine('Top 0', 0, 0.05), spine('Top 1', 1, 0.05), spine('Bottom 0', 0, 0.6), spine('Bottom 1', 1, 0.6)];
      const { pieces } = splitIntoPieces(lines, [{ x: 0, y: 0, width: 0.5, height: 0.9 }], C.splitting, T);
      expect(titles(pieces)).toEqual([['Top 0'], ['Top 1'], ['Bottom 0'], ['Bottom 1']]);
    });

    it('is off when the gap needed is infinite: the frame is split as it was', () => {
      const lines = [spine('Top 0', 0, 0.05), spine('Top 1', 1, 0.05), spine('Bottom 0', 0, 0.6), spine('Bottom 1', 1, 0.6)];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, { ...T, shelfRowGap: Infinity });
      expect(titles(pieces).map((p) => [...p].sort())).toEqual([['Bottom 0', 'Top 0'], ['Bottom 1', 'Top 1']]);
    });
  });

  describe('#509: lines outside the detected rectangles, and rectangles that hold several spines', () => {
    const titles = (pieces: { lines: { text: string }[] }[]) => pieces.map((p) => p.lines.map((l) => l.text));

    it('keeps the spines outside the only rectangle, one item each', () => {
      const lines = Array.from({ length: 6 }, (_, i) => line(`Spine ${i}`, 0.04 + i * 0.15, 0.1, 0.06, 0.5));
      const { pieces, unreadableCount } = splitIntoPieces(lines, [{ x: 0, y: 0.05, width: 0.15, height: 0.6 }], C.splitting, T);
      expect(titles(pieces)).toEqual(lines.map((l) => [l.text]));
      expect(unreadableCount).toBe(0);
    });

    it('keeps flat lines outside the only rectangle as one more item', () => {
      const lines = [line('Dune', 0.3, 0.2, 0.4, 0.1), line('Stray 1', 0.05, 0.9, 0.2, 0.04), line('Stray 2', 0.3, 0.95, 0.2, 0.04)];
      const { pieces } = splitIntoPieces(lines, [{ x: 0.1, y: 0.1, width: 0.8, height: 0.4 }], C.splitting, T);
      expect(titles(pieces)).toEqual([['Dune'], ['Stray 1', 'Stray 2']]);
    });

    it('splits a rectangle that holds two spines into two items', () => {
      const lines = [line('Dune', 0.1, 0.2, 0.06, 0.5), line('Emma', 0.25, 0.2, 0.06, 0.5)];
      const { pieces } = splitIntoPieces(lines, [{ x: 0.05, y: 0.1, width: 0.3, height: 0.7 }], C.splitting, T);
      expect(titles(pieces)).toEqual([['Dune'], ['Emma']]);
    });

    it('#618: splits a rectangle that spans several spines sitting at different heights the same way the open shelf does', () => {
      const spine = (i: number, y = 0.1, height = 0.5) => line(`Spine ${i}`, 0.04 + i * 0.15, y, 0.06, height);
      // High, high, low, low, high: 2 of the 4 neighbor pairs share a band.
      const lines = [spine(0), spine(1), spine(2, 0.6, 0.3), spine(3, 0.6, 0.3), spine(4)];
      const { pieces } = splitIntoPieces(lines, [{ x: 0, y: 0.05, width: 0.85, height: 0.9 }], C.splitting, T);
      expect(titles(pieces)).toEqual(lines.map((l) => [l.text]));
    });

    it('keeps a rectangle with scattered tall cover lines as one item', () => {
      const lines = [line('LANTERN', 0.1, 0.05, 0.1, 0.25), line('ROAD', 0.3, 0.5, 0.1, 0.25), line('Weaver', 0.5, 0.05, 0.1, 0.25)];
      const rect = { x: 0.02, y: 0.02, width: 0.7, height: 0.9 };
      const { pieces } = splitIntoPieces(lines, [rect], C.splitting, T);
      expect(pieces).toHaveLength(1);
      expect(pieces[0].lines).toHaveLength(3);
      expect(pieces[0].box).toEqual(rect);
    });

    it('reads the real second shelf photo as a piece per spine though Vision found one rectangle', () => {
      const shelf: [string, number, number, number, number][] = [
        ['ina garten | make it ahead', 0.162, 0.216, 0.041, 0.382],
        ['barefoot contessa how easy is that?', 0.2, 0.183, 0.041, 0.488],
        ['garten', 0.215, 0.705, 0.029, 0.012],
        ['ına', 0.219, 0.69, 0.016, 0.012],
        ["Martha Stewart's Dinner at Home", 0.223, 0.86, 0.331, 0.098],
        ['barefoot contessa at home', 0.241, 0.265, 0.032, 0.403],
        ['garten', 0.247, 0.686, 0.032, 0.027],
        ['BAREFOOT CONTESSA FAMILY STYLE', 0.279, 0.185, 0.034, 0.507],
        ['SARTE', 0.292, 0.719, 0.023, 0.01],
        ['oman ooks', 0.306, 0.926, 0.077, 0.037],
        ['Veman Occks RBE DRUMMOND', 0.307, 0.922, 0.221, 0.088],
        ['HanOecki RBE DRUMMOND', 0.313, 0.921, 0.214, 0.091],
        ['the barefoot contessa cookbook', 0.315, 0.178, 0.038, 0.531],
        ['GARTEN', 0.328, 0.729, 0.029, 0.01],
        ['Potter', 0.33, 0.789, 0.027, 0.017],
        ['modern comfort food', 0.366, 0.259, 0.037, 0.382],
        ['REE DRUMMOND', 0.382, 0.953, 0.146, 0.052],
        ['barefoot contessa back to basics', 0.407, 0.189, 0.035, 0.499],
        ['barefoot contessa back to basics F', 0.407, 0.189, 0.035, 0.536],
        ['garten', 0.413, 0.729, 0.031, 0.01],
        ['BAREFOOT IN PARIS', 0.443, 0.25, 0.033, 0.433],
        ['GARTEN', 0.451, 0.738, 0.033, 0.012],
        ['POTTER', 0.452, 0.808, 0.029, 0.008],
        ['INA', 0.458, 0.727, 0.017, 0.012],
        ['GARTEN', 0.49, 0.74, 0.037, 0.013],
        ['BAREFOOT CONTESSA PARTIES!', 0.492, 0.186, 0.024, 0.531],
        ['Potter', 0.496, 0.807, 0.026, 0.015],
        ['ina garten | cooking for Jeffrey', 0.533, 0.17, 0.032, 0.498],
        ['contessa', 0.533, 0.722, 0.033, 0.013],
        ['barefoot', 0.533, 0.711, 0.033, 0.01],
        ['cookbool', 0.535, 0.736, 0.031, 0.01],
        ['potter', 0.539, 0.793, 0.026, 0.01],
        ['barefoot contessa foolproof', 0.572, 0.17, 0.034, 0.51],
        ['garten', 0.573, 0.734, 0.036, 0.012],
        ['potter', 0.576, 0.81, 0.029, 0.012],
        ['ina', 0.578, 0.711, 0.022, 0.019],
        ['FALLOS', 0.612, 0.94, 0.007, 0.035],
        ['cookbook', 0.617, 0.711, 0.037, 0.014],
        ['garten', 0.618, 0.236, 0.044, 0.023],
        ['contessa', 0.619, 0.7, 0.033, 0.01],
        ['barefoot', 0.62, 0.683, 0.033, 0.016],
        ['potter', 0.622, 0.799, 0.032, 0.015],
        ['go-to dinners', 0.623, 0.36, 0.031, 0.243],
        ['ina', 0.629, 0.217, 0.023, 0.016],
        ['1ª', 0.669, 0.847, 0.031, 0.025],
        ['THE StaUb COOKBOOK MODERN RECIPES FOR CLASSIC CAST IRON', 0.678, 0.225, 0.029, 0.582],
        ['MODERN RECIPES FOR CLASSIC CAST IRON', 0.68, 0.539, 0.011, 0.256],
        ['THE Staub COOKBOOK', 0.68, 0.221, 0.029, 0.283],
        ['Simple Beautiful Food FREDERCKSON', 0.718, 0.342, 0.041, 0.475],
        ['Simple Beautiful Food', 0.722, 0.342, 0.037, 0.383],
        ['1₴', 0.722, 0.851, 0.028, 0.023],
        ['FREDERICKSON', 0.725, 0.72, 0.013, 0.094],
        ['NOSRAT', 0.774, 0.753, 0.076, 0.034],
        ['SAMIN', 0.781, 0.715, 0.068, 0.035],
        ['KNOPF', 0.879, 0.858, 0.032, 0.012],
        ['deb perelman', 0.887, 0.654, 0.026, 0.125],
        ['THE SMITTEN KITCHEN COOKBOOK del perelman', 0.887, 0.274, 0.04, 0.506],
        ['del perelman', 0.891, 0.655, 0.019, 0.122],
        ['THE SMITTEN KITCHEN COOKBOOK', 0.899, 0.274, 0.029, 0.347],
        ['RED', 0.958, 0.86, 0.019, 0.01],
        ['KItchen every day DEB PERELMAN', 0.959, 0.361, 0.044, 0.398],
        ['DEB PERELMAN', 0.964, 0.592, 0.024, 0.159],
        ['in kitchen every day', 0.971, 0.334, 0.036, 0.222],
        ['simırren kitchen every day', 0.971, 0.264, 0.034, 0.291],
      ];
      const lines = shelf.map(([text, x, y, width, height]) => line(text, x, y, width, height));
      // Vision found one rectangle, around the two right-most books; it caught 7 of the 64 lines and the rest were thrown away.
      const { pieces, unreadableCount } = splitIntoPieces(lines, [{ x: 0.77, y: 0.27, width: 0.18, height: 0.62 }], C.splitting, T);
      expect(pieces.length).toBeGreaterThanOrEqual(14);
      expect(pieces.reduce((n, p) => n + p.lines.length, 0)).toBe(lines.length);
      for (const wanted of ['BAREFOOT IN PARIS', 'modern comfort food', 'go-to dinners']) {
        expect(pieces.filter((p) => p.lines.some((l) => l.text === wanted))).toHaveLength(1);
      }
      expect(unreadableCount).toBe(0);
    });
  });

  it('returns nothing for no text', () => {
    expect(splitIntoPieces([], [], C.splitting, T)).toEqual({ pieces: [], unreadableCount: 0 });
  });
});
