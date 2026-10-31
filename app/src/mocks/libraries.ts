// Mock library-level endpoints: stats, computed from the mock items. Like the server, the total and every breakdown
// count only what you have (owned, lent, missing); wishlist, sold, lent and missing are counted beside them.
import { HttpResponse, http } from 'msw';
import type { StatsDto } from '@/api/generated/models';
import { itemsState } from './items';

const count = (values: (string | null | undefined)[]): Record<string, number> => {
  const out: Record<string, number> = {};
  for (const v of values) if (v) out[v] = (out[v] ?? 0) + 1;
  return out;
};

/** Stats for the live (not deleted) mock items. */
export const seed = (): StatsDto => {
  const all = itemsState.entries.filter((e) => !e.item.deletedAt);
  const live = all.filter((e) => e.item.status !== 'wishlist' && e.item.status !== 'sold');
  const withStatus = (status: string) => all.filter((e) => e.item.status === status).length;
  return {
    total: live.length,
    wishlistCount: withStatus('wishlist'),
    soldCount: withStatus('sold'),
    lentCount: withStatus('lent'),
    missingCount: withStatus('missing'),
    byKind: count(live.map((e) => e.work.kind)),
    byFormat: count(live.map((e) => e.release.format)),
    byPlatform: count(live.map((e) => e.release.platform?.name)),
    byDecade: count(live.map((e) => (e.release.year ? `${Math.floor(e.release.year / 10) * 10}s` : null))),
  };
};

export const handlers = [http.get('*/api/v1/libraries/:libraryId/stats', () => HttpResponse.json(seed()))];
