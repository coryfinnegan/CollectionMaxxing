// Mock catalog search: a handful of works, matched by title like the server
// (trigram similarity there, substring here), honoring the kind, platform and
// year filters.
import { HttpResponse, http } from 'msw';
import type { CatalogSearchResultDto, PlatformDto, ReleaseDto, WorkDto } from '@/api/generated/models';

const work = (n: number, kind: WorkDto['kind'], title: string, year: number, creators: string[]): WorkDto => ({
  id: `f0000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  kind,
  title,
  originalYear: year,
  creators,
  genres: [],
  synopsis: null,
  coverUrl: null,
});

const release = (n: number, w: WorkDto, format: ReleaseDto['format'], overrides: Partial<ReleaseDto> = {}): ReleaseDto => ({
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
  creators: [...w.creators],
  pageCount: null,
  runtimeMinutes: null,
  coverUrl: null,
  source: 'user',
  identifiers: [],
  ...overrides,
});

const platform = (n: number, name: string, shortName: string, manufacturer: string): PlatformDto => ({
  id: `f2000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  name,
  shortName,
  manufacturer,
});

export const mockPlatforms: PlatformDto[] = [
  platform(1, 'Nintendo 64', 'N64', 'Nintendo'),
  platform(2, 'Game Boy', 'GB', 'Nintendo'),
  platform(3, 'PlayStation 2', 'PS2', 'Sony'),
];

/**
 * The id the real server gives a work and a release that only a provider knows (`Guid.Empty`): every such result
 * shares it, so nothing in the app may tell results apart by id. The release names itself in `providerRef` instead.
 */
export const UNSAVED_ID = '00000000-0000-0000-0000-000000000000';

/** A search result only a provider knows, shaped like the server's: the empty id on the work and on its one release. */
const providerResult = (
  externalId: string,
  title: string,
  year: number,
  publisher: string,
  format: ReleaseDto['format'] = 'game_disc',
): CatalogSearchResultDto => {
  const w: WorkDto = { ...work(0, 'game', title, year, ['id Software']), id: UNSAVED_ID };
  const providerRef = { provider: 'igdb', externalId };
  return {
    work: w,
    releases: [release(0, w, format, { id: UNSAVED_ID, workId: UNSAVED_ID, source: 'provider', publisher, providerRef })],
    score: 0.5,
    source: 'igdb',
    ownedReleaseIds: [],
  };
};

/** Fresh seed data for the catalog. */
export const seed = (): CatalogSearchResultDto[] => {
  const alien = work(1, 'film', 'Alien', 1979, ['Ridley Scott']);
  const arrival = work(2, 'film', 'Arrival', 2016, ['Denis Villeneuve']);
  const neuromancer = work(3, 'book', 'Neuromancer', 1984, ['William Gibson']);
  const mario = work(4, 'game', 'Super Mario 64', 1996, ['Nintendo']);
  return [
    { work: alien, releases: [release(1, alien, 'dvd'), release(2, alien, 'vhs', { year: 1980 })], score: 0.95, source: 'local', ownedReleaseIds: [] },
    { work: arrival, releases: [release(3, arrival, 'bluray')], score: 0.9, source: 'local', ownedReleaseIds: [] },
    { work: neuromancer, releases: [release(4, neuromancer, 'paperback')], score: 0.9, source: 'local', ownedReleaseIds: [] },
    { work: mario, releases: [release(5, mario, 'game_cartridge', { platform: mockPlatforms[0]! })], score: 0.9, source: 'local', ownedReleaseIds: [] },
    // Three provider-only works, all with the empty id (the regression guard for #572): picking the 2nd must open the 2nd.
    providerResult('doom-1993', 'Doom', 1993, 'id Software'),
    providerResult('doom-ii-1994', 'Doom II', 1994, 'GT Interactive'),
    providerResult('doom-3-2004', 'Doom 3', 2004, 'Activision'),
  ];
};

const entries = seed();

const isSaved = (e: CatalogSearchResultDto) => e.work.id !== UNSAVED_ID;

/** Releases the catalog has saved: the ones `releaseId` can name. */
export const catalogReleases = (): { work: WorkDto; release: ReleaseDto }[] =>
  entries.filter(isSaved).flatMap((e) => e.releases.map((r) => ({ work: e.work, release: r })));

/** The provider-only releases a search showed, found by the `providerRef` an add sends. */
export const providerHitFor = (ref: { provider: string; externalId: string }): { work: WorkDto; release: ReleaseDto } | undefined =>
  entries
    .filter((e) => !isSaved(e))
    .flatMap((e) => e.releases.map((r) => ({ work: e.work, release: r })))
    .find((c) => c.release.providerRef?.provider === ref.provider && c.release.providerRef.externalId === ref.externalId);

export const handlers = [
  http.get('*/api/v1/catalog/search', ({ request }) => {
    const params = new URL(request.url).searchParams;
    const q = params.get('q')?.trim().toLowerCase();
    if (!q) {
      return HttpResponse.json(
        { title: 'One or more validation errors occurred.', status: 400, errors: { q: ['Enter something to search for.'] } },
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      );
    }
    const kind = params.get('kind');
    const platformName = params.get('platform')?.toLowerCase();
    const year = params.get('year');
    return HttpResponse.json({
      items: entries.filter(
        (e) =>
          e.work.title.toLowerCase().includes(q) &&
          (!kind || e.work.kind === kind) &&
          (!year || e.releases.some((r) => r.year === Number(year))) &&
          (!platformName ||
            e.releases.some((r) => r.platform?.name.toLowerCase() === platformName || r.platform?.shortName.toLowerCase() === platformName)),
      ),
      nextCursor: null,
    });
  }),
  http.get('*/api/v1/catalog/platforms', () => HttpResponse.json({ items: mockPlatforms, nextCursor: null })),
];
