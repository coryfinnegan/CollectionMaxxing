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
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // A new deletion replaces whatever came before, including a failed restore's error.
    setError(null);
    if (!deleted) return;
    const timer = setTimeout(() => setDeleted(null), VISIBLE_MS);
    return () => clearTimeout(timer);
  }, [deleted, setDeleted]);

  if (!deleted) return null;
  if (error) return <Snackbar message={error} />;
  return (
    <Snackbar
      message={`Deleted ${deleted.title}`}
      actionLabel="Undo"
      onAction={() =>
        restore.mutate(
          { id: deleted.id },
          { onSuccess: () => setDeleted(null), onError: (e) => setError(friendlyError(e)) },
        )
      }
    />
  );
}
