// A T2 search carries its segment neighbors' works (design §8(d)): the same picture
// segment, agreed on a release, as that release's work ids. A ranking bias only.
import { candidate, release } from './testData';
import { contextWorkIdsFor, MAX_CONTEXT_WORKS } from './searchContext';

describe('search neighbor works', () => {
  it('collects the agreed neighbors of the same segment, leaving the row itself out', () => {
    const row = candidate({ id: 'row', segmentId: 'seg-1' });
    const cards = [
      row,
      candidate({ id: 'a', segmentId: 'seg-1', release: release(1, 'Dune') }),
      candidate({ id: 'b', segmentId: 'seg-1', release: release(2, 'Dune Messiah') }),
      candidate({ id: 'elsewhere', segmentId: 'seg-2', release: release(3, 'Alien') }),
      candidate({ id: 'unagreed', segmentId: 'seg-1', release: null }),
    ];
    expect(contextWorkIdsFor(row, cards)).toEqual([release(1, 'Dune').workId, release(2, 'Dune Messiah').workId]);
  });

  it('says nothing for a row outside any segment', () => {
    const row = candidate({ id: 'row', segmentId: null });
    expect(contextWorkIdsFor(row, [row, candidate({ id: 'a', segmentId: 'seg-1', release: release(1, 'Dune') })])).toEqual([]);
  });

  it('counts a work once no matter how many neighbors name it', () => {
    const row = candidate({ id: 'row', segmentId: 'seg-1' });
    const work = release(7, 'Dune');
    const cards = [row, candidate({ id: 'a', segmentId: 'seg-1', release: work }), candidate({ id: 'b', segmentId: 'seg-1', release: release(7, 'Dune') })];
    expect(contextWorkIdsFor(row, cards)).toEqual([work.workId]);
  });

  it('carries at most forty works', () => {
    expect(MAX_CONTEXT_WORKS).toBe(40);
    const row = candidate({ id: 'row', segmentId: 'seg-1' });
    const cards = [row, ...Array.from({ length: 50 }, (_, n) => candidate({ id: `c-${n}`, segmentId: 'seg-1', release: release(100 + n, `Book ${n}`) }))];
    expect(contextWorkIdsFor(row, cards)).toHaveLength(40);
  });
});
