// The walk's rules (#833): which rows need the person, the shelf order, and the walk's own state.
import { emptyDraft } from './plan';
import type { Place } from './placement';
import { candidate, release } from './testData';
import {
  answerWalk,
  confident,
  currentOf,
  emptyWalk,
  joinWalk,
  needsYou,
  ordinal,
  orderRows,
  progressLabel,
  progressOf,
  shelfOrder,
  shelfPosition,
  stillReadingLine,
  undoWalk,
  walkQueue,
} from './walk';

const unconfirmed = { unconfirmed: true } as never;
const matched = () => candidate({ release: release(1, 'Dune') });
const serverRead = () => candidate({ release: release(2, 'Emma'), inferred: unconfirmed });
const pick = () => candidate({ resolution: 'ambiguous', release: release(3, 'Alien'), alternatives: [release(3, 'Alien'), release(4, 'Aliens')] as never });
const lost = () => candidate({ resolution: 'not_found' });
const owned = () => candidate({ release: release(5, 'Heat'), alreadyOwnedCount: 1 });
const pending = () => candidate({ resolution: 'pending' });

const box = (x: number, y = 0.1, width = 0.05, height = 0.8) => ({ x, y, width, height });
const at = (snapId: string, takenAt: number, b?: ReturnType<typeof box>): Place => ({ snapId, takenAt, how: b ? 'phone' : 'unmarked', ...(b ? { box: b } : {}) });

describe('the rows that need you', () => {
  it('walks only the rows that need you: pick one, not found and unconfirmed reads', () => {
    const rows = [matched(), serverRead(), pick(), lost(), owned(), pending()];
    const draft = emptyDraft();
    expect(rows.filter((c) => needsYou(c, draft)).map((c) => c.id)).toEqual([rows[1]!.id, rows[2]!.id, rows[3]!.id]);
    expect(rows.filter((c) => confident(c, draft)).map((c) => c.id)).toEqual([rows[0]!.id]);
  });

  it('opens on the end when everything matched: a 30-book shelf with 25 confident rows is 5 cards', () => {
    const rows = [...Array.from({ length: 25 }, matched), ...Array.from({ length: 5 }, serverRead)];
    expect(walkQueue(rows, emptyDraft(), {})).toHaveLength(5);
    expect(walkQueue(rows.slice(0, 25), emptyDraft(), {})).toEqual([]);
  });

  it('lets an unconfirmed read the person ticked themselves go', () => {
    const row = serverRead();
    expect(needsYou(row, { ...emptyDraft(), checked: { [row.id]: true } })).toBe(false);
  });
});

describe('shelf order', () => {
  it('orders rows left to right by the center of their box', () => {
    expect(shelfOrder([{ item: 'c', box: box(0.7) }, { item: 'a', box: box(0.1) }, { item: 'b', box: box(0.4) }])).toEqual(['a', 'b', 'c']);
  });

  it('reads a stack top to bottom', () => {
    const stack = [
      { item: 'low', box: { x: 0.3, y: 0.6, width: 0.3, height: 0.1 } },
      { item: 'top', box: { x: 0.32, y: 0.4, width: 0.28, height: 0.1 } },
      { item: 'left', box: box(0.1) },
      { item: 'right', box: box(0.8) },
    ];
    expect(shelfOrder(stack)).toEqual(['left', 'top', 'low', 'right']);
  });

  it('walks a photo of two shelves shelf by shelf, top to bottom, each left to right', () => {
    const top = (x: number) => ({ x, y: 0.05, width: 0.08, height: 0.4 });
    const low = (x: number) => ({ x, y: 0.55, width: 0.08, height: 0.4 });
    const entries = [
      { item: 'a', box: top(0.1) },
      { item: 'd', box: low(0.1) },
      { item: 'b', box: top(0.4) },
      { item: 'e', box: low(0.4) },
      { item: 'c', box: top(0.7) },
      { item: 'f', box: low(0.7) },
    ];
    expect(shelfOrder(entries)).toEqual(['a', 'b', 'c', 'd', 'e', 'f']);
    // The same answer whatever order the boxes come in.
    expect(shelfOrder([...entries].reverse())).toEqual(['a', 'b', 'c', 'd', 'e', 'f']);
  });

  it('keeps two shelves apart when one box is tall enough to span both', () => {
    const top = (x: number) => ({ x, y: 0.05, width: 0.08, height: 0.3 });
    const low = (x: number) => ({ x, y: 0.55, width: 0.08, height: 0.3 });
    const entries = [
      { item: 'tall', box: { x: 0.8, y: 0.05, width: 0.08, height: 0.8 } },
      { item: 'f', box: low(0.4) },
      { item: 'a', box: top(0.1) },
      { item: 'e', box: low(0.25) },
      { item: 'd', box: low(0.1) },
      { item: 'c', box: top(0.4) },
      { item: 'b', box: top(0.25) },
    ];
    // The tall box stands among the shelf its center is nearest to (the top one, whose center it shares), at its place from the left.
    expect(shelfOrder(entries)).toEqual(['a', 'b', 'c', 'tall', 'd', 'e', 'f']);
    expect(shelfOrder([...entries].reverse())).toEqual(['a', 'b', 'c', 'tall', 'd', 'e', 'f']);
  });

  it('keeps a tilted shelf in its own row: no book from the shelf below joins the row above', () => {
    // Both shelves lean by about 8 degrees (0.2 of the photo's height per width: 8 degrees on a 4:3 photo); the shelves are 0.3 apart.
    const lean = (x: number) => (x - 0.1) * 0.2;
    const row = (names: string[], y: number) => names.map((item, i) => ({ item, box: { x: 0.1 + i * 0.16, y: y + lean(0.1 + i * 0.16), width: 0.1, height: 0.3 } }));
    const entries = [...row(['a', 'b', 'c', 'd', 'e', 'f'], 0.05), ...row(['g', 'h', 'i', 'j', 'k', 'l'], 0.35)];
    const order = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l'];
    expect(shelfOrder(entries)).toEqual(order);
    expect(shelfOrder([...entries].reverse())).toEqual(order);
    expect(shelfOrder([entries[3]!, entries[9]!, ...entries.filter((_, n) => n !== 3 && n !== 9)])).toEqual(order);
  });

  it('reads a flat stack beside upright spines top to bottom, in its place in the row', () => {
    const entries = [
      { item: 'spine-left', box: { x: 0.1, y: 0.1, width: 0.05, height: 0.8 } },
      { item: 'flat-low', box: { x: 0.3, y: 0.8, width: 0.3, height: 0.1 } },
      { item: 'flat-high', box: { x: 0.31, y: 0.7, width: 0.29, height: 0.1 } },
      { item: 'spine-right', box: { x: 0.7, y: 0.1, width: 0.05, height: 0.8 } },
    ];
    expect(shelfOrder(entries)).toEqual(['spine-left', 'flat-high', 'flat-low', 'spine-right']);
  });

  it('keeps a row left to right under books lying across its top, which come first', () => {
    const spine = (x: number) => ({ x, y: 0.3, width: 0.05, height: 0.65 });
    const entries = [
      { item: 'row-3', box: spine(0.5) },
      { item: 'lying', box: { x: 0.1, y: 0.18, width: 0.35, height: 0.1 } },
      { item: 'row-1', box: spine(0.1) },
      { item: 'row-2', box: spine(0.3) },
    ];
    expect(shelfOrder(entries)).toEqual(['lying', 'row-1', 'row-2', 'row-3']);
  });

  it('counts a row its place from the left on its own shelf', () => {
    const [a, b, c] = [serverRead(), serverRead(), serverRead()];
    const places = {
      [a!.id]: at('s', 1, { x: 0.5, y: 0.05, width: 0.08, height: 0.4 }),
      [b!.id]: at('s', 1, { x: 0.1, y: 0.05, width: 0.08, height: 0.4 }),
      [c!.id]: at('s', 1, { x: 0.3, y: 0.55, width: 0.08, height: 0.4 }),
    };
    expect(shelfPosition(c!, [a!, b!, c!], places)).toBe(1);
    expect(shelfPosition(a!, [a!, b!, c!], places)).toBe(2);
  });

  it('puts rows with no place after the placed rows of the same photo, then rows from no photo', () => {
    const [a, b, c, d, e] = [serverRead(), serverRead(), serverRead(), serverRead(), serverRead()];
    const places = {
      [a!.id]: at('later', 2, box(0.2)),
      [b!.id]: at('first', 1),
      [c!.id]: at('first', 1, box(0.6)),
      [d!.id]: at('first', 1, box(0.1)),
    };
    expect(orderRows([a!, b!, c!, d!, e!], places).map((r) => r.id)).toEqual([d!.id, c!.id, b!.id, a!.id, e!.id]);
  });

  it('names a row its place on the shelf for VoiceOver', () => {
    const [a, b, c] = [serverRead(), serverRead(), serverRead()];
    const places = { [a!.id]: at('s', 1, box(0.5)), [b!.id]: at('s', 1, box(0.1)), [c!.id]: at('s', 1, box(0.3)) };
    expect(shelfPosition(a!, [a!, b!, c!], places)).toBe(3);
    expect(ordinal(3)).toBe('3rd');
    expect(ordinal(4)).toBe('4th');
    expect(ordinal(11)).toBe('11th');
    expect(ordinal(22)).toBe('22nd');
  });
});

describe('the walk', () => {
  const always = () => true;
  const exists = () => true;

  it('adds a late row at the end, never before the card on screen', () => {
    let walk = joinWalk(emptyWalk(), ['a', 'b']);
    walk = answerWalk(walk, { id: 'a', kind: 'accepted' });
    walk = joinWalk(walk, ['z', 'b']);
    expect(walk.order).toEqual(['a', 'b', 'z']);
    expect(currentOf(walk, always, exists)).toBe('b');
    expect(progressOf(walk, always, 'b')).toEqual({ index: 2, total: 3 });
  });

  it('brings back the last card and its answer on Undo', () => {
    const walk = answerWalk(answerWalk(joinWalk(emptyWalk(), ['a', 'b', 'c']), { id: 'a', kind: 'accepted' }), { id: 'b', kind: 'skipped' });
    const { state, answer } = undoWalk(walk);
    expect(answer).toEqual({ id: 'b', kind: 'skipped' });
    expect(currentOf(state, always, exists)).toBe('b');
    expect(progressOf(state, always, 'b')).toEqual({ index: 2, total: 3 });
  });

  it('keeps the card on screen when the server skips it, and drops an unseen one quietly', () => {
    const walk = { ...joinWalk(emptyWalk(), ['a', 'b', 'c']), place: 'a' };
    const needs = (id: string) => id !== 'a' && id !== 'b'; // the server skipped a (on screen) and b (not shown yet)
    expect(currentOf(walk, needs, exists)).toBe('a');
    expect(progressOf(walk, needs, 'a')).toEqual({ index: 1, total: 2 });
    const next = answerWalk(walk, { id: 'a', kind: 'skipped' });
    expect(currentOf(next, needs, exists)).toBe('c');
  });

  it('counts no progress for one card', () => {
    expect(progressLabel({ index: 1, total: 1 })).toBeNull();
    expect(progressLabel({ index: 4, total: 12 })).toBe('4 of 12');
  });

  it('says how many photos are still being read', () => {
    expect(stillReadingLine(0)).toBeNull();
    expect(stillReadingLine(1)).toBe('Still reading 1 photo. Its finds join the end.');
    expect(stillReadingLine(2)).toBe('Still reading 2 photos. Their finds join the end.');
  });
});
