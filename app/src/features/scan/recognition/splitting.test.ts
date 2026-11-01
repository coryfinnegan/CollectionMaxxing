import { DEFAULT_LOCAL_TUNING as T, DEFAULT_RECOGNITION_CONFIG as C } from './defaults';
import { line } from './fakes';
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

    it('#618: splits a close-up row where only about half the neighbors share a band, because titles and authors sit at different heights', () => {
      // Two spines whose title runs high, two whose title and author together sit lower, then one back up high: 2 of 4 neighbor
      // pairs share a band. A close-up shelf row is this uneven, book to book, yet it is still one row, not five scattered lines.
      const lines = [spine(0), spine(1), spine(2, 0.35, 0.3), spine(3, 0.35, 0.3), spine(4)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual(lines.map((l) => [l.text]));
    });

    it('splits the spines and never lends a flat stack below them to the spine above, even when the flat lines outnumber the spines', () => {
      const spines = Array.from({ length: 6 }, (_, i) => spine(i));
      // 'Stack 1' and 'Stack 2' sit under the third spine's column but below the row, so they belong to the stack, not to that spine.
      // Below the spine row these seven lines fall into two small piles of their own (#618), never into the spine column above them.
      const stack = [
        line('Stack 1', 0.1, 0.75, 0.5, 0.05),
        line('Stack 2', 0.2, 0.83, 0.4, 0.05),
        line('Stack 3', 0.1, 0.9, 0.3, 0.04),
        line('Stack 4', 0.5, 0.78, 0.2, 0.04),
        line('Stack 5', 0.55, 0.85, 0.3, 0.04),
        line('Stack 6', 0.6, 0.91, 0.2, 0.04),
        line('Stack 7', 0.7, 0.96, 0.2, 0.03),
      ];
      const { pieces, unreadableCount } = splitIntoPieces([...stack, ...spines], [], C.splitting, T);
      expect(titles(pieces.slice(0, 6))).toEqual(spines.map((l) => [l.text]));
      expect(titles(pieces.slice(6))).toEqual([['Stack 1', 'Stack 4'], ['Stack 2'], ['Stack 3'], ['Stack 5'], ['Stack 6'], ['Stack 7']]);
      expect(pieces.reduce((n, p) => n + p.lines.length, 0)).toBe(stack.length + spines.length);
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
      const lines = [spine(0), spine(1), spine(2, 0.35, 0.3), spine(3, 0.35, 0.3), spine(4)];
      const { pieces } = splitIntoPieces(lines, [{ x: 0, y: 0.05, width: 0.85, height: 0.85 }], C.splitting, T);
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
