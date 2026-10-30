import { addableItems, titleCoverage } from './combine';
import { DEFAULT_LOCAL_TUNING as T } from './defaults';
import type { ReadItem } from './types';

const item = (title: string, order: number, extra: Partial<ReadItem> = {}): ReadItem => ({
  localId: `id-${title}`,
  text: title,
  title,
  kind: 'book',
  order,
  confidence: 'high',
  ...extra,
});

describe('titleCoverage', () => {
  it('is 1 for the same title however it is cased, spaced or punctuated', () => {
    expect(titleCoverage('THE GREAT GATSBY', 'The Great Gatsby', T)).toBe(1);
    expect(titleCoverage("Ender's  Game!", "ender's game", T)).toBe(1);
  });

  it('is the share of the first title\'s words the second one has', () => {
    expect(titleCoverage('Great Gatsby', 'The Great Gatsby', T)).toBe(1);
    expect(titleCoverage('Dune Messiah', 'Dune', T)).toBe(0.5);
    expect(titleCoverage('Dune', 'Dune Messiah', T)).toBe(1);
  });

  it('does not count a shared article or preposition as evidence of the same book', () => {
    expect(titleCoverage('Lord of the Flies', 'The Lord of the Rings', T)).toBe(0.5);
    expect(titleCoverage('The Stand', 'The Shining', T)).toBe(0);
  });

  it('reads a cut-off word as the word it starts', () => {
    expect(titleCoverage('Hitchhikers Gui', 'Hitchhikers Guide', T)).toBe(1);
    expect(titleCoverage('Hitchhikers Gu', 'Hitchhikers Guide', T)).toBe(0.5); // too short to count as a start
  });

  it('keeps a title made only of filler words comparable', () => {
    expect(titleCoverage('The', 'The Stand', T)).toBe(1);
    expect(titleCoverage('', 'The Stand', T)).toBe(0);
  });
});

describe('addableItems', () => {
  it('keeps the Vision items whose titles are not alike any model item\'s, numbered after the model\'s', () => {
    const model = [item('Dune', 0), item('Emma', 1)];
    const vision = [item('Dune', 0, { confidence: 'low' }), item('Emma', 1), item('Neuromancer', 2, { confidence: 'low' })];
    const added = addableItems(vision, model, T);
    expect(added.map((i) => i.title)).toEqual(['Neuromancer']);
    expect(added[0].order).toBe(2);
    expect(added[0].confidence).toBe('low');
  });

  it('drops a Vision item at 0.6 coverage and keeps one just under it', () => {
    const model = [item('Alpha Bravo Charlie Delta Echo', 0)];
    expect(addableItems([item('Alpha Bravo Charlie Zulu Yankee', 0)], model, T)).toHaveLength(0); // 3 of 5 = 0.6
    expect(addableItems([item('Alpha Bravo Zulu Yankee Xray', 0)], model, T)).toHaveLength(1); // 2 of 5 = 0.4
  });

  it('numbers added items after the highest model order, in the Vision order', () => {
    const model = [item('Dune', 4), item('Emma', 7)];
    const added = addableItems([item('Neuromancer', 0), item('Ubik', 1)], model, T);
    expect(added.map((i) => [i.title, i.order])).toEqual([['Neuromancer', 8], ['Ubik', 9]]);
  });

  it('leaves every other field of an added item as the Vision engine made it', () => {
    const box = { x: 0.7, y: 0.1, width: 0.1, height: 0.6 };
    const original = item('Neuromancer', 3, { confidence: 'low', box, creator: 'William Gibson', kind: 'other', localId: 'v-1' });
    const [added] = addableItems([original], [item('Dune', 0)], T);
    expect(added).toEqual({ ...original, order: 1 });
  });

  it('adds everything when the model read nothing', () => {
    const added = addableItems([item('Dune', 0), item('Emma', 1)], [], T);
    expect(added.map((i) => [i.title, i.order])).toEqual([['Dune', 0], ['Emma', 1]]);
  });

  it('adds nothing when Vision read nothing', () => {
    expect(addableItems([], [item('Dune', 0)], T)).toEqual([]);
  });
});
