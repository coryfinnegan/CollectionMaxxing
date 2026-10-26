// The live counters above the receipt (docs/16 §5): captured, added, to review, already owned. Offline, the bar shows
// the local queue against the last snapshot it saw, badged "offline". While a T0 auto-add stands, "Undo last add"
// takes the newest one back (the snackbar's 10 s twin for the whole session).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useMemo, useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { undoAutoCommit } from '@/features/review/api';
import { fixed } from '@/theme/tokens';
import { Button, Text } from '@/ui';
import { friendlyError } from '@/lib/problem';
import { useCardsStore } from '../cards/cardsStore';
import { useAutoCommitUndoStore } from './autoCommitUndo';
import { useSessionCounters } from './useSessionCounters';

export function SessionCountersBar({ libraryId, sessionId, canAdd }: { libraryId: string; sessionId: string | null; canAdd: boolean }) {
  const { counters, offline } = useSessionCounters(libraryId, sessionId);
  const byId = useCardsStore((s) => s.byId);
  // The newest auto-add that still stands, by when it last changed; null when there is nothing to undo.
  const undoable = useMemo(() => {
    const standing = Object.values(byId).filter((c) => c.createdItemId != null);
    return standing.length > 0 ? standing.reduce((a, b) => (b.changeSeq > a.changeSeq ? b : a)) : null;
  }, [byId]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const undoableId = undoable?.id ?? null;
  // A new add retires the last attempt's error. Adjusted during render (not in an effect).
  const [errorFor, setErrorFor] = useState(undoableId);
  if (errorFor !== undoableId) {
    setErrorFor(undoableId);
    setError(null);
  }
  const line = `${counters.captured} captured · ${counters.added} added · ${counters.toReview} to review · ${counters.owned} owned`;

  const undoLast = () => {
    if (!sessionId || busy) return; // a second tap while the first is running is ignored
    setBusy(true);
    setError(null);
    undoAutoCommit(sessionId, undefined)
      .then((undone) => {
        // The snackbar may still be showing this add: it goes away with it.
        if (useAutoCommitUndoStore.getState().last?.candidateId === undone.id) useAutoCommitUndoStore.getState().setLast(null);
      })
      .catch((e: unknown) => setError(friendlyError(e)))
      .finally(() => setBusy(false));
  };

  return (
    <View style={styles.wrap}>
      <View style={styles.bar}>
        <Text
          accessibilityRole="text"
          accessibilityLabel={`${line}${offline ? '. Offline: counts include scans still on this phone' : ''}`}
          accessibilityLiveRegion="polite"
          style={styles.text}
          numberOfLines={1}
        >
          {line}
        </Text>
        {offline ? (
          <View style={styles.badge} accessibilityLabel="Offline">
            <Text style={styles.badgeText}>Offline</Text>
          </View>
        ) : null}
        {undoable && sessionId && canAdd ? <Button label="Undo last add" variant="secondary" busy={busy} onPress={undoLast} /> : null}
      </View>
      {error ? (
        <Text variant="caption" tone="danger" accessibilityLiveRegion="polite" style={styles.error}>
          {error}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  wrap: { marginHorizontal: theme.spacing.lg, gap: theme.spacing.xs },
  bar: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.sm,
    paddingHorizontal: theme.spacing.md,
    minHeight: 36,
    borderRadius: theme.radius.pill,
    backgroundColor: theme.fixed.scrim,
  },
  text: { flex: 1, color: fixed.white },
  badge: { borderRadius: theme.radius.pill, backgroundColor: fixed.white, paddingHorizontal: theme.spacing.sm, paddingVertical: 2 },
  badgeText: { color: fixed.black },
  error: { paddingHorizontal: theme.spacing.md },
}));
