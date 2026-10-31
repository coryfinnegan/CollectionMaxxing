import type { StatsDto } from '@/api/generated/models';
import { emptyCopy, itemsLabel, viewCount, viewOf } from './views';

const stats: StatsDto = {
  total: 34,
  byKind: {},
  byFormat: {},
  byPlatform: {},
  byDecade: {},
  wishlistCount: 12,
  soldCount: 4,
  lentCount: 2,
  missingCount: 1,
};

describe('the views beside the library', () => {
  it('knows the view of a fixed status; no fixed status is the library', () => {
    expect(viewOf()).toBe('library');
    expect(viewOf('wishlist')).toBe('wishlist');
    expect(viewOf('lent')).toBe('lent');
    expect(viewOf('sold')).toBe('sold');
    expect(viewOf('owned')).toBe('library');
  });

  it('counts the library as what you have, and each other view as its own status', () => {
    expect(viewCount(stats, 'library')).toBe(34);
    expect(viewCount(stats, 'wishlist')).toBe(12);
    expect(viewCount(stats, 'sold')).toBe(4);
    expect(viewCount(stats, 'lent')).toBe(2);
    expect(viewCount(undefined, 'library')).toBeUndefined();
  });

  it('says in the owners words what an empty wishlist and an empty sold list are', () => {
    expect(`${emptyCopy.wishlist.title} ${emptyCopy.wishlist.body}`).toBe(
      'Nothing on your wishlist. Scan something in a store and tap Wishlist, or add it from search.',
    );
    expect(`${emptyCopy.sold.title} ${emptyCopy.sold.body}`).toBe('Nothing sold. Items you sell or give away land here, out of your library.');
    expect(emptyCopy.lent.title).toBe('Nothing is lent out.');
  });

  it('pluralizes the item count', () => {
    expect(itemsLabel(1)).toBe('1 item');
    expect(itemsLabel(0)).toBe('0 items');
    expect(itemsLabel(4)).toBe('4 items');
  });
});
