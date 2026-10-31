// Naming what you picked in the Search view. The server gives every work and release that only a provider knows
// the same empty id, so an id alone tells such results apart from nothing: a row is keyed by what it is (provider and
// the provider's own id), and an add names a provider-only edition the same way.
import type { CatalogSearchResultDto, CreateItemRequest, ReleaseDto } from '@/api/generated/models';

/** The id the server gives a work or release it has not saved yet (`Guid.Empty`): shared by every such result. */
const UNSAVED_ID = '00000000-0000-0000-0000-000000000000';

/** How an add names the picked edition: its catalog id, or, when only a provider knows it, the provider and its id. */
export type PickedRelease = Pick<CreateItemRequest, 'releaseId' | 'providerRef'>;

export function pickedReleaseOf(release: ReleaseDto): PickedRelease {
  return release.id === UNSAVED_ID && release.providerRef ? { providerRef: release.providerRef } : { releaseId: release.id };
}

/**
 * A key that is unique per search result: the work's id when the catalog has saved it, else the provider and id of its
 * first release, else its row. Never the work id alone, which every provider-only work shares.
 */
export function resultKey(result: CatalogSearchResultDto, index: number): string {
  if (result.work.id !== UNSAVED_ID) return `work:${result.work.id}`;
  const ref = result.releases.find((release) => release.providerRef)?.providerRef;
  return ref ? `${ref.provider}:${ref.externalId}` : `row:${index}`;
}
