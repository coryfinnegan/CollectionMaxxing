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
      // High, high, low, high: only 1 of 3 neighbor pairs shares a band, under the 60% share.
      const lines = [spine(0), spine(1), spine(2, 0.6, 0.3), spine(3)];
      const { pieces } = splitIntoPieces(lines, [], C.splitting, T);
      expect(pieces).toHaveLength(1);
      expect(pieces[0].lines).toHaveLength(4);
    });

    it('splits the spines and keeps a flat stack below them as one extra piece, even when the flat lines outnumber the spines', () => {
      const spines = Array.from({ length: 6 }, (_, i) => spine(i));
      // 'Stack 1' and 'Stack 2' sit under the third spine's column but below the row, so they belong to the stack, not to that spine.
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
      expect(pieces).toHaveLength(7);
      expect(titles(pieces.slice(0, 6))).toEqual(spines.map((l) => [l.text]));
      expect(titles(pieces.slice(6))[0]).toEqual(['Stack 1', 'Stack 4', 'Stack 2', 'Stack 5', 'Stack 3', 'Stack 6', 'Stack 7']);
      expect(unreadableCount).toBe(0);
    });

    it('lets a short line across a spine join that spine', () => {
      const lines = [line('Dune', 0.1, 0.1, 0.05, 0.4), line('Herbert', 0.1, 0.45, 0.05, 0.03), line('Emma', 0.3, 0.1, 0.05, 0.4)];
      expect(titles(splitIntoPieces(lines, [], C.splitting, T).pieces)).toEqual([['Dune', 'Herbert'], ['Emma']]);
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

  it('returns nothing for no text', () => {
    expect(splitIntoPieces([], [], C.splitting, T)).toEqual({ pieces: [], unreadableCount: 0 });
  });
});
