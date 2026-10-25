import { createOwnedIndex } from './ownedIndex';
import { entry, tombstone } from './testData';

describe('OwnedIndex.lookup', () => {
  const vhs = entry({
    title: 'Jaws',
    format: 'vhs',
    workId: 'work-jaws',
    identifiers: ['0042100005264'],
    locationPath: 'Living room › Shelf B',
  });
  const dvd = entry({ title: 'Jaws', format: 'dvd', workId: 'work-jaws', identifiers: ['5050582000000'] });
  const book = entry({ title: 'Dune', format: 'paperback', workId: 'work-dune', identifiers: ['9780306406157'] });
  const index = createOwnedIndex([vhs, dvd, book]);

  it('finds an owned copy with its location text from any form of its code', () => {
    for (const code of ['042100005264', '0042100005264', '04252614', '0-42100-00526-4']) {
      const result = index.lookup(code);
      expect(result.owned.map((c) => c.itemId)).toEqual([vhs.itemId]);
      expect(result.owned[0].locationPath).toBe('Living room › Shelf B');
      expect(result.owned[0].format).toBe('vhs');
    }
  });

  it('finds a book by its ISBN-10 and ISBN-13 forms', () => {
    expect(index.lookup('0306406152').owned.map((c) => c.itemId)).toEqual([book.itemId]);
    expect(index.lookup('ISBN 978-0-306-40615-7').owned.map((c) => c.itemId)).toEqual([book.itemId]);
  });

  it('offers another format of the same work as a soft match', () => {
    const result = index.lookup('042100005264');
    expect(result.softMatches.map((c) => c.itemId)).toEqual([dvd.itemId]);
  });

  it('says nothing is owned for an unknown or invalid code', () => {
    expect(index.lookup('4006381333931')).toEqual({ owned: [], softMatches: [] });
    expect(index.lookup('not a code')).toEqual({ owned: [], softMatches: [] });
    expect(index.lookup('')).toEqual({ owned: [], softMatches: [] });
  });

  it('looks up by release id and by work id', () => {
    expect(index.lookup({ releaseId: vhs.releaseId }).owned.map((c) => c.itemId)).toEqual([vhs.itemId]);
    expect(
      index
        .lookup({ workId: 'work-jaws' })
        .owned.map((c) => c.itemId)
        .sort(),
    ).toEqual([vhs.itemId, dvd.itemId].sort());
  });

  it('gives a soft match for a work when only another format of it is owned', () => {
    const onlyDvd = createOwnedIndex([dvd]);
    const result = onlyDvd.lookup({ code: '042100005264', workId: 'work-jaws' });
    expect(result.owned).toEqual([]);
    expect(result.softMatches.map((c) => c.itemId)).toEqual([dvd.itemId]);
  });

  it('keeps two copies of the same release as two owned copies', () => {
    const second = entry({ ...vhs, itemId: 'second-copy', locationPath: 'Basement' });
    const result = createOwnedIndex([vhs, second]).lookup('042100005264');
    expect(result.owned.map((c) => c.locationPath).sort()).toEqual(['Basement', 'Living room › Shelf B']);
  });
});

describe('OwnedIndex.searchTitles', () => {
  const park = entry({ title: 'Jurassic Park', format: 'dvd' });
  const park3 = entry({ title: 'Jurassic Park III', format: 'dvd' });
  const matrix = entry({ title: 'The Matrix', format: 'dvd' });
  const index = createOwnedIndex([park, park3, matrix]);

  it('matches every word in any case and ranks the exact title first', () => {
    expect(index.searchTitles('JURASSIC park').map((c) => c.title)).toEqual(['Jurassic Park', 'Jurassic Park III']);
    expect(index.searchTitles('park jurassic').map((c) => c.title)).toEqual(['Jurassic Park', 'Jurassic Park III']);
  });

  it('needs every word and skips entries without a title', () => {
    expect(index.searchTitles('jurassic matrix')).toEqual([]);
    expect(createOwnedIndex([entry({ title: null })]).searchTitles('title')).toEqual([]);
  });

  it('answers nothing for a blank query and honors the limit', () => {
    expect(index.searchTitles('  ')).toEqual([]);
    expect(index.searchTitles('jurassic', 1).map((c) => c.title)).toEqual(['Jurassic Park']);
  });
});

describe('OwnedIndex.apply', () => {
  it('adds, replaces and removes by item id without changing the original', () => {
    const a = entry({ identifiers: ['4006381333931'], title: 'A' });
    const original = createOwnedIndex([a]);

    const b = entry({ identifiers: ['5901234123457'] });
    const edited = { ...a, locationPath: 'Garage' };
    const next = original.apply([b, edited]);
    expect(next.size).toBe(2);
    expect(next.lookup('4006381333931').owned[0].locationPath).toBe('Garage');
    expect(original.size).toBe(1);

    const gone = next.apply([tombstone(a.itemId)]);
    expect(gone.size).toBe(1);
    expect(gone.lookup('4006381333931').owned).toEqual([]);
  });

  it('ignores a tombstone for an item it never held', () => {
    expect(createOwnedIndex([]).apply([tombstone('nope')]).size).toBe(0);
  });
});

describe('OwnedIndex at 5,000 items', () => {
  it('looks a code up in well under a millisecond', () => {
    const entries = Array.from({ length: 5000 }, (_, i) => {
      const body = String(100000000000 + i);
      const check = (() => {
        let sum = 0;
        for (let k = 0; k < body.length; k++) sum += Number(body[k]) * ((body.length - k) % 2 === 1 ? 3 : 1);
        return String((10 - (sum % 10)) % 10);
      })();
      return entry({ identifiers: [body + check], locationPath: 'Shelf' });
    });
    const index = createOwnedIndex(entries);
    const code = entries[2500].identifiers[0];

    index.lookup(code); // warm up
    const runs = 1000;
    const started = performance.now();
    for (let i = 0; i < runs; i++) index.lookup(code);
    const perLookupMs = (performance.now() - started) / runs;

    expect(index.lookup(code).owned).toHaveLength(1);
    expect(perLookupMs).toBeLessThan(1);
  });
});
