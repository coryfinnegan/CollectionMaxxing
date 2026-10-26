// The heraldic banner cloth per media kind (#363): TV rides with movies, Other stays iron.
import type { WorkKind } from '@/api/generated/models';
import { bannerMediumForKind } from './labels';

describe('bannerMediumForKind', () => {
  it.each([
    ['book', 'book'],
    ['game', 'game'],
    ['film', 'film'],
    ['tv', 'film'],
    ['other', undefined],
  ] as const)('flies %s for %s', (kind: WorkKind, medium) => {
    expect(bannerMediumForKind(kind)).toBe(medium);
  });
});
