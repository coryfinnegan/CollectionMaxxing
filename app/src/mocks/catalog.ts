// Mock catalog search: a handful of works, matched by title.
import { HttpResponse, http } from 'msw';
import type { CatalogSearchResultDto, ReleaseDto, WorkDto } from '@/api/generated/models';

const work = (n: number, kind: WorkDto['kind'], title: string, year: number): WorkDto => ({
  id: `f0000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  kind,
  title,
  originalYear: year,
  creators: [],
  genres: [],
  synopsis: null,
  coverUrl: null,
});

const release = (n: number, w: WorkDto, format: ReleaseDto['format']): ReleaseDto =>
  ({
    id: `f1000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    workId: w.id,
    title: w.title,
    format,
    platform: null,
    region: null,
    edition: null,
    publisher: null,
    releaseDate: null,
    year: w.originalYear,
    creators: [],
    pageCount: null,
    runtimeMinutes: null,
    coverUrl: null,
    source: 'user',
    identifiers: [],
  });

/** Fresh seed data for the catalog. */
export const seed = (): CatalogSearchResultDto[] => {
  const alien = work(1, 'film', 'Alien', 1979);
  const arrival = work(2, 'film', 'Arrival', 2016);
  const neuromancer = work(3, 'book', 'Neuromancer', 1984);
  return [
    { work: alien, releases: [release(1, alien, 'dvd'), release(2, alien, 'vhs')], score: 0.95, source: 'local', ownedReleaseIds: [] },
    { work: arrival, releases: [release(3, arrival, 'bluray')], score: 0.9, source: 'local', ownedReleaseIds: [] },
    { work: neuromancer, releases: [release(4, neuromancer, 'paperback')], score: 0.9, source: 'local', ownedReleaseIds: [] },
  ];
};

const entries = seed();

export const catalogReleases = (): { work: WorkDto; release: ReleaseDto }[] =>
  entries.flatMap((e) => e.releases.map((r) => ({ work: e.work, release: r })));

export const handlers = [
  http.get('*/api/v1/catalog/search', ({ request }) => {
    const q = new URL(request.url).searchParams.get('q')?.trim().toLowerCase();
    if (!q) {
      return HttpResponse.json(
        { title: 'One or more validation errors occurred.', status: 400, errors: { q: ['Enter something to search for.'] } },
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      );
    }
    return HttpResponse.json({
      items: entries.filter((e) => e.work.title.toLowerCase().includes(q)),
      nextCursor: null,
    });
  }),
];
