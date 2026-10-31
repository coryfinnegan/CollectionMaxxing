// The wishlist card: a release the library does not own yet but wants. "On your wishlist", and Got it, which is what the wishlist is
// for in a store: one tap moves the item to owned with today's date. Not "You own this". Skip leaves it as it is.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto } from '@/api/generated/models';
import { Button, Text } from '@/ui';
import type { CardCopy } from './copy';
import type { CardActions } from './ResultCards';

export function WishlistCard({ candidate, copy, actions }: { candidate: CandidateDto; copy: CardCopy; actions: CardActions }) {
  const busy = !!actions.busy?.[candidate.id];
  const moved = actions.moved?.[candidate.id];
  const itemId = candidate.wishlistItemId;
  // Which button is working: every press sets it first, so a stale value never shows.
  const [pressed, setPressed] = useState<'got' | 'skip' | 'undo' | null>(null);
  const canAct = actions.canAdd && !moved && !!itemId && !!actions.onMoveItem;

  return (
    <View style={styles.body}>
      <Text variant="label" tone="accent" accessibilityLiveRegion="polite">
        {moved ?? copy.status}
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
      {moved && actions.canAdd && actions.onUndoMoveItem ? (
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
