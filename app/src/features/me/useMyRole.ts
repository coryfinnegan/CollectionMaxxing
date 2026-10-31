import { useGetMe } from '@/api/generated/account/account';
import type { LibraryRole } from '@/api/generated/models';
import { useActiveLibraryId } from '@/features/locations/useLocations';

/** What a role may change: owners and editors edit; viewers only look. Nothing known yet means nothing to edit. */
export const canEditLibrary = (role: LibraryRole | undefined): boolean => role === 'owner' || role === 'editor';

/**
 * The caller's role in the active library, once GET /me has answered. When the libraries list could not be loaded
 * there is no library: `isError` says so, and `refetch` asks again.
 */
export function useMyRole(): {
  libraryId: string | undefined;
  role: LibraryRole | undefined;
  isLoading: boolean;
  isError: boolean;
  refetch: () => Promise<unknown>;
} {
  const { libraryId, isLoading, isError, refetch } = useActiveLibraryId();
  const me = useGetMe();
  const role = me.data?.status === 200 ? me.data.data.memberships.find((m) => m.libraryId === libraryId)?.role : undefined;
  return { libraryId, role, isLoading: isLoading || me.isLoading, isError, refetch };
}
