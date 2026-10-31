import { useGetMe } from '@/api/generated/account/account';
import type { LibraryRole } from '@/api/generated/models';
import { useActiveLibraryId } from '@/features/locations/useLocations';

/**
 * The caller's role in the active library, once GET /me has answered. `canEdit` is false until then and for
 * viewers, so editing controls stay hidden until the role is known.
 */
export function useMyRole(): {
  libraryId: string | undefined;
  role: LibraryRole | undefined;
  canEdit: boolean;
  isLoading: boolean;
} {
  const { libraryId, isLoading } = useActiveLibraryId();
  const me = useGetMe();
  const role = me.data?.status === 200 ? me.data.data.memberships.find((m) => m.libraryId === libraryId)?.role : undefined;
  return { libraryId, role, canEdit: role === 'owner' || role === 'editor', isLoading: isLoading || me.isLoading };
}
