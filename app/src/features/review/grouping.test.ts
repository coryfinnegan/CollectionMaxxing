import type { CandidateDto, ReleaseDto } from '@/api/generated/models';
import { alternativesOf, defaultChecked, groupCandidates, groupOf, isAggregated, reviewable } from './grouping';
import { candidate, release } from './testData';

describe('grouping', () => {
  it('puts every candidate in exactly one group by resolution and ownership', () => {
    const list: CandidateDto[] = [
      candidate({ id: 'ready', resolution: 'matched', release: release(1, 'Mario') }),
      candidate({ id: 'owned', resolution: 'matched', release: release(2, 'Dune'), alreadyOwnedCount: 1 }),
      candidate({ id: 'pick', resolution: 'ambiguous', release: release(3, 'Alien'), alreadyOwnedCount: 1 }),
      candidate({ id: 'nf', resolution: 'not_found' }),
      candidate({ id: 'err', resolution: 'error' }),
      candidate({ id: 'wait', resolution: 'pending' }),
    ];
    const groups = groupCandidates(list);
    expect(groups.ready.map((c) => c.id)).toEqual(['ready']);
    expect(groups.owned.map((c) => c.id)).toEqual(['owned']);
    expect(groups.pick.map((c) => c.id)).toEqual(['pick']);
    expect(groups.notFound.map((c) => c.id)).toEqual(['nf', 'err']);
    expect(groups.pending.map((c) => c.id)).toEqual(['wait']);
    const all = Object.values(groups).flat();
    expect(all).toHaveLength(list.length);
    expect(new Set(all.map((c) => c.id)).size).toBe(list.length);
  });

  it('leaves out skipped and already committed candidates', () => {
    const list = [
      candidate({ id: 'a', decision: 'rejected', release: release(1, 'A') }),
      candidate({ id: 'b', createdItemId: 'i1', release: release(2, 'B') }),
      candidate({ id: 'c', release: release(3, 'C') }),
    ];
    expect(reviewable(list).map((c) => c.id)).toEqual(['c']);
  });

  it('orders by candidate arrival inside a group', () => {
    const groups = groupCandidates([
      candidate({ id: 'late', seq: 900, release: release(1, 'Late') }),
      candidate({ id: 'early', seq: 800, release: release(2, 'Early') }),
    ]);
    expect(groups.ready.map((c) => c.id)).toEqual(['early', 'late']);
  });

  it('aggregates the successes and lists the exceptions', () => {
    expect(isAggregated('ready')).toBe(true);
    expect(isAggregated('owned')).toBe(true);
    expect(isAggregated('pick')).toBe(false);
    expect(isAggregated('notFound')).toBe(false);
    expect(isAggregated('pending')).toBe(false);
  });

  it('checks ready and accepted rows by default and nothing else', () => {
    expect(defaultChecked(candidate({ release: release(1, 'A') }), 'ready')).toBe(true);
    expect(defaultChecked(candidate({ release: release(1, 'A'), alreadyOwnedCount: 1 }), 'owned')).toBe(false);
    expect(defaultChecked(candidate({ release: release(1, 'A'), alreadyOwnedCount: 1, decision: 'accepted' }), 'owned')).toBe(true);
    expect(defaultChecked(candidate({ resolution: 'ambiguous' }), 'pick')).toBe(false);
    expect(defaultChecked(candidate({ resolution: 'not_found' }), 'notFound')).toBe(false);
  });

  it('reads at most the top three alternatives, in either shape the API may use', () => {
    const rs: ReleaseDto[] = [1, 2, 3, 4].map((n) => release(n, `Alien ${n}`));
    expect(alternativesOf(candidate({ alternatives: rs as never })).map((r) => r.title)).toEqual(['Alien 1', 'Alien 2', 'Alien 3']);
    expect(alternativesOf(candidate({ alternatives: rs.map((r) => ({ release: r, score: 0.5 })) as never })).map((r) => r.title)).toEqual([
      'Alien 1',
      'Alien 2',
      'Alien 3',
    ]);
    expect(alternativesOf(candidate({ alternatives: null }))).toEqual([]);
    expect(alternativesOf(candidate({ alternatives: 'nonsense' as never }))).toEqual([]);
  });

  it('names the group of one candidate', () => {
    expect(groupOf(candidate({ resolution: 'matched', release: release(1, 'A') }))).toBe('ready');
  });
});
