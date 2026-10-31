// The libraries you belong to, and which one the app is showing.
import { getListLibrariesQueryKey, useListLibraries } from '@/api/generated/libraries/libraries';
import type { LibrarySummaryDto } from '@/api/generated/models';
import { useLibraryStore } from '@/state/library';
import { librariesRetry, librariesRetryDelay } from './librariesRetry';

export const librariesQueryKey = getListLibrariesQueryKey;

/** Your libraries with your role in each. `loaded` is false until the first answer, so an empty list isn't mistaken for "none yet". */
export function useLibraries(): {
  libraries: LibrarySummaryDto[];
  loaded: boolean;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  refetch: () => Promise<unknown>;
} {
  const query = useListLibraries({ query: { retry: librariesRetry, retryDelay: librariesRetryDelay } });
  const page = query.data?.status === 200 ? query.data.data : undefined;
  return {
    libraries: page?.items ?? [],
    loaded: !!page,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
    refetch: query.refetch,
  };
}

/**
 * The library the app is showing: the one remembered on this device, else the first. A remembered id that is no longer yours is dropped.
 * When the list could not be loaded there is no library: `isError` says so, and `refetch` asks again.
 */
export function useActiveLibraryId(): {
  libraryId: string | undefined;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  refetch: () => Promise<unknown>;
} {
  const { libraries, isLoading, isError, error, refetch } = useLibraries();
  const stored = useLibraryStore((s) => s.activeLibraryId);
  // Not written back: a library you just created or joined is chosen before the refreshed list arrives, and must survive that gap.
  const resolved = libraries.find((l) => l.id === stored)?.id ?? libraries[0]?.id;
  return { libraryId: resolved, isLoading, isError, error, refetch };
}
