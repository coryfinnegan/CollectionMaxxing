// The four views of the same list: the Library (what you have) and the three that live beside it. Plain data, so the grid,
// the web table and the tests read the same words and the same counts.
import type { ItemStatus, StatsDto } from '@/api/generated/models';

export type LibraryViewName = 'library' | 'wishlist' | 'lent' | 'sold';

/** Which view a list with this fixed status is. No fixed status is the Library itself. */
export const viewOf = (fixedStatus?: ItemStatus): LibraryViewName =>
  fixedStatus === 'wishlist' ? 'wishlist' : fixedStatus === 'lent' ? 'lent' : fixedStatus === 'sold' ? 'sold' : 'library';

/** What an empty view says. The library points at the camera; the others say what lands there. */
export const emptyCopy: Record<LibraryViewName, { title: string; body: string }> = {
  library: { title: 'Nothing here yet.', body: 'Point your camera at a shelf or a stack of cases.' },
  wishlist: { title: 'Nothing on your wishlist.', body: 'Scan something in a store and tap Wishlist, or add it from search.' },
  lent: { title: 'Nothing is lent out.', body: 'Lend an item from its page and it shows up here.' },
  sold: { title: 'Nothing sold.', body: 'Items you sell or give away land here, out of your library.' },
};

/** The number a view's header shows: the Library counts what you have, every other view counts its own status. */
export function viewCount(stats: StatsDto | undefined, view: LibraryViewName): number | undefined {
  if (!stats) return undefined;
  switch (view) {
    case 'wishlist':
      return stats.wishlistCount;
    case 'sold':
      return stats.soldCount;
    case 'lent':
      return stats.lentCount;
    default:
      return stats.total;
  }
}

/** "12 items", "1 item". */
export const itemsLabel = (count: number): string => `${count} ${count === 1 ? 'item' : 'items'}`;
