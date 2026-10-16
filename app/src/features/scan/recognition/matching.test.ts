import { DEFAULT_LOCAL_TUNING as T } from './defaults';
import { line } from './fakes';
import { matchBoxes } from './matching';

describe('box matching', () => {
  it('gives an item whose visibleText is one Vision line that line\'s box', () => {
    const lines = [line('Dune', 0.1, 0.1, 0.2, 0.1), line('Emma', 0.5, 0.1, 0.2, 0.1)];
    const [match] = matchBoxes(['Dune'], lines, T);
    expect(match.box?.x).toBe(0.1);
    expect(match.box?.width).toBeCloseTo(0.2);
  });

  it('still matches when the model completed a word that is cut off at the edge', () => {
    const lines = [line('Raiders of the Lost Arch', 0.7, 0.2, 0.3, 0.1)];
    const [completed] = matchBoxes(['Raiders of the Lost Archive'], lines, T);
    expect(completed.box?.x).toBe(0.7);
    expect(completed.box?.width).toBeCloseTo(0.3);
  });

  it('gives an item nothing when no Vision line reads like it', () => {
    const [match] = matchBoxes(['Totally Different Book'], [line('Dune', 0.1, 0.1, 0.2, 0.1)], T);
    expect(match.box).toBeUndefined();
    expect(match.lines).toEqual([]);
  });

  it('covers an item that spans two lines with both lines', () => {
    const lines = [line('Dune', 0.1, 0.1, 0.2, 0.1), line('Frank Herbert', 0.1, 0.2, 0.2, 0.05)];
    const [match] = matchBoxes(['Dune Frank Herbert'], lines, T);
    expect(match.box?.y).toBe(0.1);
    expect(match.box?.height).toBeCloseTo(0.15);
  });

  it('gives two similar spines their own boxes', () => {
    const lines = [
      line('Dune', 0.1, 0.1, 0.05, 0.5),
      line('Frank Herbert', 0.1, 0.65, 0.05, 0.2),
      line('Dune Messiah', 0.3, 0.1, 0.05, 0.5),
      line('Frank Herbert', 0.3, 0.65, 0.05, 0.2),
    ];
    const [first, second] = matchBoxes(['Dune Frank Herbert', 'Dune Messiah Frank Herbert'], lines, T);
    expect(first.box?.x).toBe(0.1);
    expect(second.box?.x).toBe(0.3);
    expect(first.lines.map((l) => l.text)).toEqual(['Dune', 'Frank Herbert']);
    expect(second.lines.map((l) => l.text)).toEqual(['Dune Messiah', 'Frank Herbert']);
  });

  it('gives two spines that read exactly alike one line each, left to right', () => {
    const lines = [line('Jaws', 0.1, 0.1, 0.05, 0.5), line('Jaws', 0.4, 0.1, 0.05, 0.5)];
    const [first, second] = matchBoxes(['Jaws', 'Jaws'], lines, T);
    expect(first.box?.x).toBe(0.1);
    expect(second.box?.x).toBe(0.4);
  });
});
