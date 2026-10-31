// The wishlist card: a release the library does not own yet but wants. "On your wishlist", and Got it, which is what the wishlist is
// for in a store: one tap moves the item to owned with today's date. Not "You own this". Skip leaves it as it is. What it shows follows
// the live ownership answer, not only the candidate it was made from: once the item is owned (from this card, the result screen, or
// before the app restarted) it says "In your library" and offers no second Got it.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto } from '@/api/generated/models';
import { useOwnedLookup } from '@/features/offline/hooks';
import { Button, Text } from '@/ui';
import { type CardCopy, wishlistStateOf } from './copy';
import type { CardActions } from './ResultCards';

/** What a wishlist card says once its item is owned and no move of its own is on record. */
const IN_YOUR_LIBRARY = 'In your library';

export function WishlistCard({ candidate, copy, actions }: { candidate: CandidateDto; copy: CardCopy; actions: CardActions }) {
  const busy = !!actions.busy?.[candidate.id];
  const moved = actions.moved?.[candidate.id];
  const releaseId = candidate.release?.id ?? null;
  const { nowOwned, itemId } = wishlistStateOf(candidate, useOwnedLookup(releaseId ? { releaseId } : null));
  // Which button is working: every press sets it first, so a stale value never shows.
  const [pressed, setPressed] = useState<'got' | 'skip' | 'undo' | null>(null);
  const canAct = actions.canAdd && !moved && !nowOwned && !!itemId && !!actions.onMoveItem;

  return (
    <View style={styles.body}>
      <Text variant="label" tone="accent" accessibilityLiveRegion="polite">
        {moved?.message ?? (nowOwned ? IN_YOUR_LIBRARY : copy.status)}
      </Text>
      {canAct ? (
        <View style={styles.actions}>
          <Button
            label="Got it"
            accessibilityLabel={`Got it: ${copy.title}`}
            disabled={busy}
            busy={busy && pressed === 'got'}
            onPress={() => {
              setPressed('got');
              actions.onMoveItem?.(candidate, itemId, 'got-it');
            }}
          />
          {actions.onSkip ? (
            <Button
              label="Skip"
              accessibilityLabel={`Skip ${copy.title}`}
              variant="secondary"
              disabled={busy}
              busy={busy && pressed === 'skip'}
              onPress={() => {
                setPressed('skip');
                actions.onSkip?.(candidate);
              }}
            />
          ) : null}
        </View>
      ) : null}
      {moved?.canUndo && actions.canAdd && actions.onUndoMoveItem ? (
        <Button
          label="Undo"
          accessibilityLabel={`Undo: ${copy.title}`}
          variant="secondary"
          disabled={busy}
          busy={busy && pressed === 'undo'}
          onPress={() => {
            setPressed('undo');
            actions.onUndoMoveItem?.(candidate);
          }}
        />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  body: { gap: theme.spacing.sm },
  actions: { gap: theme.spacing.xs },
}));
