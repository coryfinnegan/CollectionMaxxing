import type { ItemStatus } from '@/api/generated/models';
import { statuses } from './labels';
import { BESIDE_COLLECTION, IN_COLLECTION, isInCollection } from './statuses';

describe('what an item status means', () => {
  it('has the library as owned, lent and missing', () => {
    expect([...IN_COLLECTION]).toEqual(['owned', 'lent', 'missing']);
  });

  it.each<[ItemStatus, boolean]>([
    ['owned', true],
    ['lent', true],
    ['missing', true],
    ['wishlist', false],
    ['sold', false],
  ])('says %s is in the library: %s', (status, expected) => {
    expect(isInCollection(status)).toBe(expected);
  });

  it('puts every status either in the library or beside it, never both', () => {
    expect([...IN_COLLECTION, ...BESIDE_COLLECTION].sort()).toEqual([...statuses].sort());
    expect(IN_COLLECTION.filter((s) => (BESIDE_COLLECTION as readonly string[]).includes(s))).toEqual([]);
  });

  it('lists the statuses with the library first, so a form reads the way the rest of the app does', () => {
    expect(statuses).toEqual(['owned', 'lent', 'missing', 'wishlist', 'sold']);
  });
});
