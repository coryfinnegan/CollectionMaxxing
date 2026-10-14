// Result detail (docs/06 M4, basic version): the card's facts, where owned copies live, and the add buttons.
// The photo with the item boxed on it is CAM-3.
import { useEffect } from 'react';
import { View } from 'react-native';
import { useStore } from 'zustand';
import { formatLabels } from '@/features/library/labels';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { spacing } from '@/theme/tokens';
import { Button, Card, Cover, EmptyState, FormatBadge, Screen, Text } from '@/ui';
import { useCardActions } from '../session/useCardActions';
import { loadSession } from '../session/sessionApi';
import { useLibraryRole } from '../session/useScanSession';
import { getTrayStore } from '../tray/storage';
import { describeCandidate } from './copy';
import { useCardsStore } from './cardsStore';

export function ResultDetail({ id }: { id: string }) {
  const { libraryId } = useActiveLibraryId();
  const candidate = useCardsStore((s) => s.byId[id]);
  const sessionId = useStore(getTrayStore(), (s) => s.sessionId);
  const { canAdd } = useLibraryRole(libraryId);
  const actions = useCardActions({ libraryId: libraryId ?? '', sessionId, canAdd });

  // Opened cold (the app restarted since the scan): load the session to find the card.
  useEffect(() => {
    if (candidate || !sessionId) return;
    void loadSession(sessionId)
      .then((all) => useCardsStore.getState().merge(all, { announce: false }))
      .catch(() => undefined);
  }, [candidate, sessionId]);

  if (!candidate) {
    return (
      <Screen title="Result">
        <EmptyState title="That result isn't here any more" body="Open the scanner to see this scan's results." />
      </Screen>
    );
  }

  const copy = describeCandidate(candidate);
  const release = candidate.release;
  const owned = candidate.alreadyOwnedCount > 0;
  const addable = canAdd && !!release && !owned;
  const busy = !!actions.busy?.[candidate.id];
  const error = actions.errors?.[candidate.id];
  const accepted = candidate.decision === 'accepted';
  const wishlisted = !!actions.wishlisted?.[candidate.id];

  return (
    <Screen title="Result">
      <Card>
        <View style={{ flexDirection: 'row', gap: spacing.md }}>
          <Cover title={copy.title} uri={release?.coverUrl} width={96} />
          <View style={{ flex: 1, gap: spacing.xs }}>
            <Text variant="heading" accessibilityRole="header">
              {copy.title}
            </Text>
            {release ? <FormatBadge label={formatLabels[release.format]} /> : null}
            {release?.creators.length ? <Text tone="muted">{release.creators.join(', ')}</Text> : null}
            <Text tone="muted">{[release?.platform?.name, release?.year].filter(Boolean).join(' · ')}</Text>
          </View>
        </View>
      </Card>

      <Card>
        {owned ? (
          <>
            <Text variant="heading">{candidate.alreadyOwnedCount === 1 ? 'You own this' : `You own ${candidate.alreadyOwnedCount} copies`}</Text>
            {candidate.ownedAt.map((place, index) => (
              <Text key={`${place}-${index}`} tone="muted">
                {place}
              </Text>
            ))}
          </>
        ) : copy.kind === 'new' ? (
          <Text variant="heading">Not in your library</Text>
        ) : (
          <Text>{copy.status}</Text>
        )}
        {addable && !accepted && !wishlisted ? (
          <View style={{ flexDirection: 'row', gap: spacing.sm }}>
            <Button label="Add to library" busy={busy} onPress={() => actions.onAdd(candidate)} />
            <Button label="Wishlist" variant="secondary" disabled={busy} onPress={() => actions.onWishlist(candidate)} />
          </View>
        ) : null}
        {addable && (accepted || wishlisted) ? <Text tone="scan">{wishlisted ? 'On your wishlist' : 'Added to this scan'}</Text> : null}
        {error ? <Text tone="danger">{error}</Text> : null}
      </Card>
    </Screen>
  );
}
