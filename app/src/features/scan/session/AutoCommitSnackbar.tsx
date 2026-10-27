// "Added Dune. Undo": shown on the scanner for 10 s after a T0 auto-add, and takes the add back.
// The timeout only hides the snackbar: the counters bar's "Undo last add" keeps offering it for the session.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { invalidateLibraryItems } from '@/features/item/useItem';
import { undoAutoCommit } from '@/features/review/api';
import { UNDO_MS } from '@/features/review/useReview';
import { friendlyError } from '@/lib/problem';
import { Snackbar } from '@/ui';
import { describeCandidate } from '../cards/copy';
import { useCardsStore } from '../cards/cardsStore';
import { useAutoCommitUndoStore } from './autoCommitUndo';

export function AutoCommitSnackbar({ libraryId }: { libraryId: string }) {
  const queryClient = useQueryClient();
  const last = useAutoCommitUndoStore((s) => s.last);
  const setLast = useAutoCommitUndoStore((s) => s.setLast);
  const card = useCardsStore((s) => (last ? s.byId[last.candidateId] : undefined));
  const [visible, setVisible] = useState(true);
  const [busy, setBusy] = useState(false);
  // The failed undo and the add it belonged to. Each add is a new object,
  // so the error only shows for that same add and can never poison a later Undo.
  const [failure, setFailure] = useState<{ at: NonNullable<typeof last>; message: string } | null>(null);
  // A new add shows its own snackbar. Adjusted during render (not in an effect) so the
  // old add's hidden state never swallows the new one.
  const [shownFor, setShownFor] = useState(last);
  if (shownFor !== last) {
    setShownFor(last);
    setVisible(true);
    setBusy(false);
  }

  useEffect(() => {
    if (!last) return;
    const timer = setTimeout(() => setVisible(false), UNDO_MS);
    return () => clearTimeout(timer);
  }, [last]);

  if (!last || !visible) return null;
  const title = card ? describeCandidate(card).title : null;
  if (!title) return null;
  if (failure && failure.at === last)
    return (
      <View style={styles.snack}>
        <Snackbar message={failure.message} />
      </View>
    );
  return (
    <View style={styles.snack}>
      <Snackbar
        message={`Added ${title}`}
        actionLabel="Undo"
        onAction={() => {
          if (busy) return; // a second tap while the first is running is ignored
          setBusy(true);
          undoAutoCommit(last.sessionId, last.candidateId)
            .then(async () => {
              // Each add is a new object, so only the Undo for the currently shown
              // add may clear it; a late success for a superseded add must not clear a newer one.
              if (useAutoCommitUndoStore.getState().last === last) setLast(null);
              // The taken-back add leaves the library list by itself (#448).
              await invalidateLibraryItems(queryClient, libraryId);
            })
            .catch((e: unknown) => {
              setBusy(false);
              setFailure({ at: last, message: friendlyError(e) });
            });
        }}
      />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  snack: { paddingHorizontal: theme.spacing.lg, paddingBottom: theme.spacing.sm },
}));
