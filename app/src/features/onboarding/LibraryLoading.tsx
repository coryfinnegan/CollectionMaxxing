// The one gate every screen shows while it waits for the active library: the loading state, or, when the
// libraries list could not be loaded, the reason and Try again. Without it a failed list spins forever.
import { friendlyError } from '@/lib/problem';
import { EmptyState, LoadingState } from '@/ui';
import { useActiveLibraryId } from './useLibraries';

export function LibraryLoading({ label }: { label: string }) {
  const { isError, error, refetch } = useActiveLibraryId();
  if (isError) {
    return (
      <EmptyState
        title="Couldn't load your library"
        body={friendlyError(error)}
        actionLabel="Try again"
        onAction={() => void refetch()}
      />
    );
  }
  return <LoadingState label={label} />;
}
