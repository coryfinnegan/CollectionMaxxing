// "Deleted The Matrix. Undo": shown on whichever screen is up after a delete, and calls restore.
import { useEffect, useState } from 'react';
import { friendlyError } from '@/lib/problem';
import { Snackbar } from '@/ui';
import { useItemMutations, useUndoStore } from './useItem';

const VISIBLE_MS = 10_000;

export function UndoSnackbar() {
  const deleted = useUndoStore((s) => s.deleted);
  const setDeleted = useUndoStore((s) => s.setDeleted);
  const { restore } = useItemMutations(deleted?.libraryId ?? '');
  // The failed restore and the deletion it belonged to. Each deletion is a new object,
  // so the error only shows for that same deletion and can never poison a later Undo.
  const [failure, setFailure] = useState<{ at: NonNullable<typeof deleted>; message: string } | null>(null);

  useEffect(() => {
    if (!deleted) return;
    const timer = setTimeout(() => setDeleted(null), VISIBLE_MS);
    return () => clearTimeout(timer);
  }, [deleted, setDeleted]);

  if (!deleted) return null;
  if (failure && failure.at === deleted) return <Snackbar message={failure.message} />;
  return (
    <Snackbar
      message={`Deleted ${deleted.title}`}
      actionLabel="Undo"
      onAction={() =>
        restore.mutate(
          { id: deleted.id },
          {
            // Each deletion is a new object, so only the Undo for the currently shown
            // deletion may clear the snackbar; a late success for a superseded deletion
            // must not clear a newer one.
            onSuccess: () => {
              if (useUndoStore.getState().deleted === deleted) setDeleted(null);
            },
            onError: (e) => setFailure({ at: deleted, message: friendlyError(e) }),
          },
        )
      }
    />
  );
}
