import type { Rect } from '@/native/cm-vision';
import { addableItems, enrichedItems, isSameItem, shelfReading, titleCoverage } from './combine';
import { DEFAULT_LOCAL_TUNING as T } from './defaults';
import { printedWords } from './printSupport';
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

/** A box 0.05 wide and 0.6 tall, `x` from the left: the size of a spine. */
const spineAt = (x: number): Rect => ({ x, y: 0.1, width: 0.05, height: 0.6 });

describe('isSameItem: the same place read the same way is the same item', () => {
  it('is the same item when the boxes overlap by at least half of the smaller one and both read the same thing', () => {
    const a = item('Dune', 0, { box: { x: 0, y: 0, width: 0.5, height: 0.5 } });
    const b = item('DUNE', 1, { box: { x: 0.25, y: 0, width: 0.5, height: 0.5 } }); // exactly half of either
    expect(isSameItem(a, b, T)).toBe(true);
    expect(isSameItem(b, a, T)).toBe(true);
  });

  it('is not the same item when the two read that place as different things: it is a second read of the place', () => {
    const a = item('Dune', 0, { box: { x: 0, y: 0, width: 0.5, height: 0.5 } });
    const b = item('Something else entirely', 1, { box: { x: 0.25, y: 0, width: 0.5, height: 0.5 } });
    expect(isSameItem(a, b, T)).toBe(false);
    expect(isSameItem(b, a, T)).toBe(false);
  });

  it('is not the same item when one read saw words the other did not, however the boxes sit', () => {
    // Vision's piece of a spine holds the author's line, so the phone cannot tell "Dune" inside "DUNE FRANK HERBERT" from "Dune"
    // inside "DUNE MESSIAH": both go out and the server settles the place.
    const spine = item('Dune Frank Herbert', 0, { box: spineAt(0.1) });
    const titleLine = item('Dune', 1, { box: { x: 0.11, y: 0.2, width: 0.03, height: 0.3 } });
    expect(isSameItem(titleLine, spine, T)).toBe(false);
    expect(isSameItem(spine, titleLine, T)).toBe(false);
  });

  it('is a different item when the boxes overlap by less than half, even with the same title', () => {
    const a = item('HIGHLANDER', 0, { box: { x: 0, y: 0, width: 0.5, height: 0.5 } });
    const b = item('HIGHLANDER', 1, { box: { x: 0.26, y: 0, width: 0.5, height: 0.5 } });
    expect(isSameItem(a, b, T)).toBe(false);
  });

  it('is a different item when the boxes are apart, even with the same title', () => {
    expect(isSameItem(item('HIGHLANDER', 0, { box: spineAt(0.1) }), item('HIGHLANDER', 1, { box: spineAt(0.3) }), T)).toBe(false);
  });

  it('reads the title only when an item has no box', () => {
    const boxed = item('Dune', 0, { box: spineAt(0.1) });
    expect(isSameItem(item('Dune', 1), boxed, T)).toBe(true);
    expect(isSameItem(boxed, item('Dune', 1), T)).toBe(true);
    expect(isSameItem(item('Emma', 1), boxed, T)).toBe(false);
    expect(isSameItem(item('Dune', 0), item('Dune', 1), T)).toBe(true);
    expect(isSameItem(item('Dune', 0), item('Emma', 1), T)).toBe(false);
  });

  it('asks for the overlap the tuning says', () => {
    const a = item('Dune', 0, { box: { x: 0, y: 0, width: 0.5, height: 0.5 } });
    const b = item('Dune', 1, { box: { x: 0.25, y: 0, width: 0.5, height: 0.5 } });
    expect(isSameItem(a, b, { ...T, sameItemOverlap: 0.6 })).toBe(false);
    expect(isSameItem(a, b, { ...T, sameItemOverlap: 0.4 })).toBe(true);
  });
});

describe('enrichedItems: the same item read by the model fills in what Vision lacks', () => {
  const visionItem = (extra: Partial<ReadItem> = {}) =>
    item('SPACE MUTINY', 3, { localId: 'v-1', text: 'SPACE MUTINY', kind: 'other', confidence: 'low', box: spineAt(0.1), ...extra });
  const modelFilm = (extra: Partial<ReadItem> = {}) =>
    item('Space Mutiny', 0, { localId: 'm-1', kind: 'film', format: 'vhs', box: { x: 0.11, y: 0.2, width: 0.03, height: 0.3 }, ...extra });

  it('keeps Vision\'s title, box, localId, order, text and confidence, and takes the model\'s kind and format', () => {
    const vision = visionItem();
    const [out] = enrichedItems([vision], [modelFilm()], T);
    expect(out).toEqual({ ...vision, kind: 'film', format: 'vhs' });
  });

  it('fills in the creator, platform, edition and completeness Vision does not have', () => {
    const [out] = enrichedItems([visionItem()], [modelFilm({ kind: 'game', platform: 'Nintendo 64', creator: 'Rare', edition: "Player's Choice", completeness: 'complete', format: undefined })], T);
    expect(out).toMatchObject({ title: 'SPACE MUTINY', kind: 'game', platform: 'Nintendo 64', creator: 'Rare', edition: "Player's Choice", completeness: 'complete' });
  });

  it('keeps what Vision already has: its own kind, format and creator', () => {
    const vision = visionItem({ kind: 'book', creator: 'Vision Author' });
    const [out] = enrichedItems([vision], [modelFilm({ creator: 'Model Author', edition: 'Director\'s Cut' })], T);
    expect(out).toMatchObject({ kind: 'book', creator: 'Vision Author', edition: 'Director\'s Cut' });
    expect(out.format).toBeUndefined(); // a format that belongs to the model's kind is not put on Vision's
  });

  it('does not take a kind from the model when the model has none either', () => {
    expect(enrichedItems([visionItem()], [modelFilm({ kind: 'other', format: undefined })], T)[0]).toEqual(visionItem());
  });

  it('does not fill in from an item in another place, or one with no box', () => {
    const vision = visionItem();
    expect(enrichedItems([vision], [modelFilm({ box: spineAt(0.5) })], T)[0]).toBe(vision);
    expect(enrichedItems([vision], [modelFilm({ box: undefined, title: 'SPACE MUTINY' })], T)[0]).toBe(vision);
    expect(enrichedItems([visionItem({ box: undefined })], [modelFilm()], T)[0].kind).toBe('other');
  });

  it('gives a model item to the one Vision item it overlaps the most, not to every one it touches', () => {
    const left = visionItem({ localId: 'v-left', box: spineAt(0.1) });
    const right = visionItem({ localId: 'v-right', box: spineAt(0.13) });
    const out = enrichedItems([right, left], [modelFilm({ box: { x: 0.12, y: 0.2, width: 0.03, height: 0.3 } })], T);
    expect(out.map((i) => [i.localId, i.kind])).toEqual([['v-right', 'other'], ['v-left', 'film']]);
  });

  it('fills in the item at its place that read what it read, not the one it merely overlaps most', () => {
    const wrong = visionItem({ localId: 'v-wrong', title: 'SOMETHING ELSE', box: { x: 0.10, y: 0.1, width: 0.10, height: 0.6 } });
    const right = visionItem({ localId: 'v-right', title: 'SPACE MUTINY', box: { x: 0.12, y: 0.1, width: 0.04, height: 0.6 } });
    const out = enrichedItems([wrong, right], [modelFilm({ box: { x: 0.11, y: 0.1, width: 0.04, height: 0.6 } })], T);
    expect(out.map((i) => [i.localId, i.kind, i.format])).toEqual([
      ['v-wrong', 'other', undefined],
      ['v-right', 'film', 'vhs'],
    ]);
  });

  it('leaves the Vision items as they are, the same objects, when there is nothing to add', () => {
    const items = [visionItem()];
    expect(enrichedItems(items, [], T)[0]).toBe(items[0]);
    expect(enrichedItems(items, [modelFilm({ kind: 'other', format: undefined })], T)[0]).toBe(items[0]);
  });

  describe('a game\'s product code (ADR-031)', () => {
    const modelGame = (extra: Partial<ReadItem> = {}) => modelFilm({ kind: 'game', format: undefined, platform: 'PlayStation', ...extra });

    it('stays on the Vision item when the model\'s fields fill in around it', () => {
      const [out] = enrichedItems([visionItem({ productCode: 'SLUS-00594' })], [modelGame()], T);
      expect(out).toMatchObject({ title: 'SPACE MUTINY', kind: 'game', platform: 'PlayStation', productCode: 'SLUS-00594' });
    });

    it('stays on the Vision item whatever kind the model gives it, and is not replaced by the model\'s code', () => {
      const [book] = enrichedItems([visionItem({ productCode: 'SLUS-00594' })], [modelFilm({ kind: 'book', format: undefined })], T);
      expect(book).toMatchObject({ kind: 'book', productCode: 'SLUS-00594' });
      const [own] = enrichedItems([visionItem({ productCode: 'SLUS-00594' })], [modelGame({ productCode: 'SLUS-00001' })], T);
      expect(own.productCode).toBe('SLUS-00594');
    });

    it('is taken from the model item when the Vision item has none', () => {
      const [out] = enrichedItems([visionItem()], [modelGame({ productCode: 'SLUS-00594' })], T);
      expect(out).toMatchObject({ kind: 'game', productCode: 'SLUS-00594' });
    });
  });
});

describe('addableItems', () => {
  it('keeps the candidates whose titles are not alike any existing item\'s, numbered after the existing ones', () => {
    const existing = [item('Dune', 0, { confidence: 'low' }), item('Emma', 1, { confidence: 'low' })];
    const candidates = [item('Dune', 0), item('Emma', 1), item('Neuromancer', 2, { confidence: 'medium' })];
    const added = addableItems(candidates, existing, T);
    expect(added.map((i) => i.title)).toEqual(['Neuromancer']);
    expect(added[0].order).toBe(2);
    expect(added[0].confidence).toBe('medium');
  });

  it('does not add a candidate that sits where an existing item does and reads the same words', () => {
    const existing = [item('GREAT GATSBY', 0, { box: spineAt(0.1) })];
    expect(addableItems([item('The Great Gatsby', 0, { box: { x: 0.11, y: 0.2, width: 0.03, height: 0.3 } })], existing, T)).toEqual([]);
  });

  it('adds a candidate that sits where an existing item does but reads it as something else: the server picks between the two', () => {
    const existing = [item('GREAT GATSBY FITZGERALD', 0, { box: spineAt(0.1) })];
    const added = addableItems([item('Something misread', 0, { box: spineAt(0.1) })], existing, T);
    expect(added.map((i) => [i.title, i.order, i.unconfirmed])).toEqual([['Something misread', 1, true]]);
  });

  it('adds a candidate whose words are only part of what the existing item read there, because that may be another release', () => {
    // "Dune" inside Vision's "DUNE MESSIAH" is another book; inside its "DUNE FRANK HERBERT" it is the same one. The phone cannot
    // tell them apart, so both go out and the server shows the read that grounds better.
    const existing = [item('DUNE MESSIAH', 0, { box: spineAt(0.1) })];
    expect(addableItems([item('Dune', 0, { box: spineAt(0.1) })], existing, T).map((i) => i.title)).toEqual(['Dune']);
  });

  it('adds a candidate in a place where nothing else is, even when its title is an existing one', () => {
    const existing = [item('HIGHLANDER', 0, { box: spineAt(0.1) })];
    const added = addableItems([item('HIGHLANDER', 0, { box: spineAt(0.1) }), item('HIGHLANDER', 1, { box: spineAt(0.3) })], existing, T);
    expect(added.map((i) => [i.title, i.box?.x, i.order])).toEqual([['HIGHLANDER', 0.3, 1]]);
  });

  it('keeps five copies of one title on a shelf: the same title in different places is five items', () => {
    const copies = [0.1, 0.2, 0.3, 0.4, 0.5].map((x, i) => item('HIGHLANDER', i, { box: spineAt(x) }));
    expect(addableItems(copies, [], T)).toHaveLength(5);
    expect(addableItems(copies, copies.slice(0, 2), T).map((i) => i.box?.x)).toEqual([0.3, 0.4, 0.5]);
  });

  it('reads the title only for a candidate that has no box, however many existing items have one', () => {
    const existing = [item('Dune', 0, { box: spineAt(0.1) }), item('Emma', 1, { box: spineAt(0.3) })];
    expect(addableItems([item('Dune', 0)], existing, T)).toEqual([]);
    expect(addableItems([item('Ubik', 0)], existing, T).map((i) => i.title)).toEqual(['Ubik']);
  });

  it('reads the title only for an existing item that has no box', () => {
    expect(addableItems([item('Dune', 0, { box: spineAt(0.1) })], [item('Dune', 0)], T)).toEqual([]);
    expect(addableItems([item('Ubik', 0, { box: spineAt(0.1) })], [item('Dune', 0)], T)).toHaveLength(1);
  });

  it('drops an exact re-read, in any casing, and a candidate whose title the existing title wholly contains', () => {
    const model = [item('The Lantern Road Cookbook', 0)];
    expect(addableItems([item('The Lantern Road Cookbook', 0)], model, T)).toHaveLength(0);
    expect(addableItems([item('LANTERN ROAD COOKBOOK', 0)], model, T)).toHaveLength(0);
    expect(addableItems([item('Lantern Road', 0)], model, T)).toHaveLength(0);
  });

  it('keeps a sibling title in the same series: sharing most words is not the same book', () => {
    expect(addableItems([item('barefoot contessa foolproof', 0)], [item('Barefoot Contessa Family Style', 0)], T)).toHaveLength(1);
    expect(addableItems([item('THE SMITTEN KITCHEN COOKBOOK', 0)], [item('Smitten Kitchen Every Day', 0)], T)).toHaveLength(1);
  });

  it('keeps a candidate with junk words after the existing title; the server merges reads of the same book', () => {
    const added = addableItems([item('Lantern Road Cookbook QXRTVZ', 0)], [item('The Lantern Road Cookbook', 0)], T);
    expect(added.map((i) => i.title)).toEqual(['Lantern Road Cookbook QXRTVZ']);
  });

  it('numbers added items after the highest existing order, in the order of the candidates', () => {
    const model = [item('Dune', 4), item('Emma', 7)];
    const added = addableItems([item('Neuromancer', 0), item('Ubik', 1)], model, T);
    expect(added.map((i) => [i.title, i.order])).toEqual([['Neuromancer', 8], ['Ubik', 9]]);
  });

  it('leaves every other field of an added item as its own engine made it, and marks it unconfirmed', () => {
    const box = { x: 0.7, y: 0.1, width: 0.1, height: 0.6 };
    const original = item('Neuromancer', 3, { confidence: 'low', box, creator: 'William Gibson', kind: 'other', localId: 'v-1' });
    const [added] = addableItems([original], [item('Dune', 0)], T);
    expect(added).toEqual({ ...original, order: 1, unconfirmed: true });
  });

  it('marks every added item unconfirmed: nothing but the model saw it, where the other engine found nothing', () => {
    const added = addableItems([item('Neuromancer', 0), item('Ubik', 1)], [item('Dune', 0)], T);
    expect(added.map((i) => i.unconfirmed)).toEqual([true, true]);
  });

  it('does not mark the existing items, or the candidates it leaves out', () => {
    const existing = [item('Dune', 0, { box: spineAt(0.1) })];
    const candidates = [item('Dune', 0, { box: spineAt(0.1) }), item('Emma', 1, { box: spineAt(0.4) })];
    addableItems(candidates, existing, T);
    expect(existing[0]).not.toHaveProperty('unconfirmed');
    expect(candidates[0]).not.toHaveProperty('unconfirmed');
    expect(candidates[1]).not.toHaveProperty('unconfirmed');
  });

  it('adds everything when nothing else is there yet', () => {
    const added = addableItems([item('Dune', 0), item('Emma', 1)], [], T);
    expect(added.map((i) => [i.title, i.order])).toEqual([['Dune', 0], ['Emma', 1]]);
  });

  it('adds nothing when there are no candidates', () => {
    expect(addableItems([], [item('Dune', 0)], T)).toEqual([]);
  });
});

/** The print of a shelf whose Vision text read these lines. */
const printOf = (...lines: string[]) => printedWords(lines.map((text) => ({ text })));

describe('addableItems: a model-only read is added only when the shelf\'s print has its words', () => {
  const existing = [item('Dune', 0, { confidence: 'low', box: spineAt(0.1) })];

  it('does not add a model-only read whose words the shelf did not print', () => {
    const invented = item('The Matrix', 0);
    expect(addableItems([invented], existing, T, printOf('DUNE', 'FRANK HERBERT'))).toEqual([]);
  });

  it('adds one the print has, unconfirmed, as it always was', () => {
    const added = addableItems([item('Neuromancer', 0)], existing, T, printOf('DUNE', 'NEUROMANCER'));
    expect(added.map((i) => [i.title, i.order, i.unconfirmed])).toEqual([['Neuromancer', 1, true]]);
  });

  it('adds one with two thirds of its words printed, and not one with less', () => {
    const print = printOf('RED', 'OCTOBER');
    expect(addableItems([item('The First for a Red October', 0)], [], T, print)).toHaveLength(1);
    expect(addableItems([item('The Lion, the Witch and the Wardrobe', 0)], [], T, printOf('KIMBA THE LION'))).toHaveLength(0);
  });

  it('numbers the items it adds one after another, with no gap where it left one out', () => {
    const added = addableItems([item('The Matrix', 0), item('Neuromancer', 1), item('Warcraft', 2), item('Emma', 3)], existing, T, printOf('NEUROMANCER', 'EMMA'));
    expect(added.map((i) => [i.title, i.order])).toEqual([['Neuromancer', 1], ['Emma', 2]]);
  });

  it('leaves a candidate that is an existing item read again to the existing item, with print or without', () => {
    expect(addableItems([item('Dune', 0, { box: spineAt(0.1) })], existing, T, printOf('SOMETHING ELSE'))).toEqual([]);
  });

  it('adds a candidate at an existing item\'s place that reads it as something else, though the print lacks its words: the server picks between the two', () => {
    const vision = [item('FRANK HERBERT', 0, { confidence: 'low', box: spineAt(0.1) })];
    const dune = item('Dune', 0, { box: { x: 0.11, y: 0.2, width: 0.03, height: 0.3 } });
    const added = addableItems([dune], vision, T, printOf('FRANK HERBERT'));
    expect(added.map((i) => [i.title, i.order, i.unconfirmed])).toEqual([['Dune', 1, true]]);
  });

  it('checks a candidate with a box that sits at no existing item\'s place, and one with no box', () => {
    const print = printOf('DUNE', 'NEUROMANCER');
    const elsewhere = item('The Matrix', 0, { box: spineAt(0.6) });
    const nowhere = item('Warcraft', 1);
    const printed = item('Neuromancer', 2, { box: spineAt(0.8) });
    expect(addableItems([elsewhere, nowhere, printed], existing, T, print).map((i) => i.title)).toEqual(['Neuromancer']);
  });

  it('takes the places to check against from the Vision items it is given, not from the items that already stand', () => {
    const piece = item('EMMA DUNE', 0, { box: { x: 0.1, y: 0.1, width: 0.3, height: 0.6 } });
    const inside = item('Dune', 1, { box: spineAt(0.2) });
    expect(addableItems([inside], [], T, printOf('EMMA'))).toEqual([]);
    expect(addableItems([inside], [], T, printOf('EMMA'), [piece]).map((i) => i.title)).toEqual(['Dune']);
  });

  it('adds every model-only read when there is no print to check against: Vision failed, or read no lines', () => {
    const added = addableItems([item('The Matrix', 0), item('Warcraft', 1)], existing, T, undefined);
    expect(added.map((i) => i.title)).toEqual(['The Matrix', 'Warcraft']);
  });
});

describe('shelfReading: the shelf\'s print only decides which model-only reads are added', () => {
  const visionDune = () => item('DUNE FRANK HERBERT', 0, { localId: 'v-dune', kind: 'other', confidence: 'low', box: spineAt(0.1) });
  const modelDune = () => item('Dune', 0, { localId: 'm-dune', kind: 'book', format: 'paperback', box: { x: 0.11, y: 0.2, width: 0.03, height: 0.3 } });

  it('adds a read the print has and leaves out one it does not, numbering the added ones after Vision\'s', () => {
    const out = shelfReading([visionDune()], [item('The Matrix', 1), item('Neuromancer', 2)], T, printOf('DUNE FRANK HERBERT', 'NEUROMANCER'));
    expect(out.map((i) => [i.title, i.order, i.unconfirmed])).toEqual([['DUNE FRANK HERBERT', 0, undefined], ['Neuromancer', 1, true]]);
  });

  it('still lends the kind and format of a read of a Vision item, even when the print does not have the read\'s words', () => {
    const same = item('DUNE', 0, { localId: 'm-same', kind: 'book', format: 'paperback', box: { x: 0.11, y: 0.2, width: 0.03, height: 0.3 } });
    const out = shelfReading([item('DUNE', 0, { localId: 'v-dune', kind: 'other', confidence: 'low', box: spineAt(0.1) })], [same], T, printOf('SOMETHING ELSE ENTIRELY'));
    expect(out).toHaveLength(1);
    expect(out[0]).toMatchObject({ localId: 'v-dune', title: 'DUNE', kind: 'book', format: 'paperback' });
  });

  it('lends the Vision item the read\'s kind and adds the read beside it, whatever the print says: the place is Vision\'s, so the server settles it', () => {
    const out = shelfReading([visionDune()], [modelDune()], T, printOf('SOMETHING ELSE ENTIRELY'));
    expect(out.map((i) => [i.title, i.unconfirmed])).toEqual([['DUNE FRANK HERBERT', undefined], ['Dune', true]]);
    expect(out[0]).toMatchObject({ localId: 'v-dune', kind: 'book', format: 'paperback' });
  });

  it('keeps every read inside a Vision piece that holds several, whatever the print says, and drops the piece', () => {
    const piece = item('EMMA DUNE UBIK', 0, { localId: 'v-piece', box: { x: 0.1, y: 0.1, width: 0.3, height: 0.6 } });
    const reads = [
      item('Emma', 1, { box: { x: 0.1, y: 0.1, width: 0.05, height: 0.6 } }),
      item('Dune', 2, { box: { x: 0.2, y: 0.1, width: 0.05, height: 0.6 } }),
    ];
    const out = shelfReading([piece], reads, T, printOf('EMMA'));
    expect(out.map((i) => i.title)).toEqual(['Emma', 'Dune']);
  });

  it('checks a model read at no Vision item\'s place, though it sits near the piece the others were in', () => {
    const piece = item('EMMA DUNE UBIK', 0, { localId: 'v-piece', box: { x: 0.1, y: 0.1, width: 0.3, height: 0.6 } });
    const reads = [
      item('Emma', 1, { box: { x: 0.1, y: 0.1, width: 0.05, height: 0.6 } }),
      item('Dune', 2, { box: { x: 0.2, y: 0.1, width: 0.05, height: 0.6 } }),
      item('The Matrix', 3, { box: { x: 0.7, y: 0.1, width: 0.05, height: 0.6 } }),
    ];
    const out = shelfReading([piece], reads, T, printOf('EMMA'));
    expect(out.map((i) => i.title)).toEqual(['Emma', 'Dune']);
  });

  it('adds the same read beside the Vision item when the print has it', () => {
    const out = shelfReading([visionDune()], [modelDune()], T, printOf('DUNE FRANK HERBERT'));
    expect(out.map((i) => [i.title, i.unconfirmed])).toEqual([['DUNE FRANK HERBERT', undefined], ['Dune', true]]);
    expect(out[0].kind).toBe('book');
  });

  it('adds every model read when there is no print', () => {
    const out = shelfReading([visionDune()], [item('The Matrix', 1)], T, undefined);
    expect(out.map((i) => i.title)).toEqual(['DUNE FRANK HERBERT', 'The Matrix']);
  });
});
