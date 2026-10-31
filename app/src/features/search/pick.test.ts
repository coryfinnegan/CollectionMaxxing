// What an add sends to name the edition you picked: a saved release by its catalog id, a provider-only one by the
// provider and its own id (the server gives every such result the same empty id, so the id names nothing).
import type { ReleaseDto } from '@/api/generated/models';
import { UNSAVED_ID } from '@/mocks/catalog';
import { pickedReleaseOf, resultKey } from './pick';

const release = (overrides: Partial<ReleaseDto> = {}): ReleaseDto => ({
  id: 'f1000000-0000-4000-8000-000000000002',
  workId: 'f0000000-0000-4000-8000-000000000001',
  title: 'Alien',
  format: 'vhs',
  platform: null,
  region: null,
  edition: null,
  publisher: null,
  releaseDate: null,
  year: 1980,
  creators: [],
  pageCount: null,
  runtimeMinutes: null,
  coverUrl: null,
  source: 'user',
  identifiers: [],
  ...overrides,
});

describe('pickedReleaseOf', () => {
  it('names a saved release by its catalog id', () => {
    expect(pickedReleaseOf(release())).toEqual({ releaseId: 'f1000000-0000-4000-8000-000000000002' });
  });

  it('names a provider-only release by its provider and id, never by the shared empty id', () => {
    const providerRef = { provider: 'igdb', externalId: 'doom-ii-1994' };

    expect(pickedReleaseOf(release({ id: UNSAVED_ID, source: 'provider', providerRef }))).toEqual({ providerRef });
  });

  it('keeps the catalog id of a release that has one even when the server also names a provider', () => {
    const providerRef = { provider: 'igdb', externalId: 'x' };

    expect(pickedReleaseOf(release({ providerRef }))).toEqual({ releaseId: 'f1000000-0000-4000-8000-000000000002' });
  });

  it('falls back to the empty id when an older server names no provider, so the add fails as it did, not silently', () => {
    expect(pickedReleaseOf(release({ id: UNSAVED_ID, providerRef: null }))).toEqual({ releaseId: UNSAVED_ID });
  });
});

describe('resultKey', () => {
  const work = (id: string) => ({ id, kind: 'game' as const, title: 'Doom', originalYear: 1993, creators: [], genres: [], synopsis: null, coverUrl: null });

  it('is the work id for a saved work', () => {
    const result = { work: work('w-1'), releases: [release()], score: 1, source: 'local', ownedReleaseIds: [] };

    expect(resultKey(result, 3)).toBe('work:w-1');
  });

  it('is the first provider and id for a work only a provider knows, so results sharing the empty id stay apart', () => {
    const doom = { work: work(UNSAVED_ID), releases: [release({ id: UNSAVED_ID, providerRef: { provider: 'igdb', externalId: 'doom' } })], score: 1, source: 'igdb', ownedReleaseIds: [] };
    const doom2 = { work: work(UNSAVED_ID), releases: [release({ id: UNSAVED_ID, providerRef: { provider: 'igdb', externalId: 'doom-ii' } })], score: 1, source: 'igdb', ownedReleaseIds: [] };

    expect(resultKey(doom, 0)).toBe('igdb:doom');
    expect(resultKey(doom2, 1)).toBe('igdb:doom-ii');
  });

  it('falls back to the row when nothing names the result', () => {
    const bare = { work: work(UNSAVED_ID), releases: [release({ id: UNSAVED_ID })], score: 1, source: 'igdb', ownedReleaseIds: [] };

    expect(resultKey(bare, 2)).toBe('row:2');
  });
});
