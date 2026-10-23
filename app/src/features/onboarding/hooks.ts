// TanStack Query hooks for creating, joining and leaving a library. Each one refreshes your library list before it reports success,
// so the screen that follows already sees the change.
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { getGetMeQueryKey } from '@/api/generated/account/account';
import { useLibraryStore } from '@/state/library';
import { acceptInvite, createLibrary, leaveLibrary, previewInvite } from './api';
import { librariesQueryKey } from './useLibraries';

function useRefreshAccount() {
  const queryClient = useQueryClient();
  return () => Promise.all([queryClient.invalidateQueries({ queryKey: librariesQueryKey() }), queryClient.invalidateQueries({ queryKey: getGetMeQueryKey() })]);
}

export function useCreateLibrary() {
  const refresh = useRefreshAccount();
  const setActive = useLibraryStore((s) => s.setActiveLibraryId);
  return useMutation({
    mutationFn: createLibrary,
    onSuccess: async (library) => {
      setActive(library.id);
      await refresh();
    },
  });
}

export function useInvitePreview() {
  return useMutation({ mutationFn: previewInvite });
}

export function useAcceptInvite() {
  const refresh = useRefreshAccount();
  const setActive = useLibraryStore((s) => s.setActiveLibraryId);
  return useMutation({
    mutationFn: acceptInvite,
    onSuccess: async (library) => {
      setActive(library.id);
      await refresh();
    },
  });
}

export function useLeaveLibrary() {
  const refresh = useRefreshAccount();
  const setActive = useLibraryStore((s) => s.setActiveLibraryId);
  return useMutation({
    mutationFn: leaveLibrary,
    onSuccess: async (_none, libraryId) => {
      if (useLibraryStore.getState().activeLibraryId === libraryId) setActive(null);
      await refresh();
    },
  });
}
