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

  it('returns nothing for no text', () => {
    expect(splitIntoPieces([], [], C.splitting, T)).toEqual({ pieces: [], unreadableCount: 0 });
  });
});
