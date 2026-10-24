// Result detail (docs/06 M4): your own photo with the item boxed on it, the card's facts, where owned copies live, the add buttons,
// Wrong item? Fix it, and Use my photo (the one place a picture is sent, and only the crop of this item, only when an editor taps).
import { useEffect, useState, type ReactNode } from 'react';
import { Image, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useStore } from 'zustand';
import { formatLabels } from '@/features/library/labels';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { spacing, useColors } from '@/theme/tokens';
import { Button, Card, Cover, EmptyState, FormatBadge, Screen, Text } from '@/ui';
import { useCardActions } from '../session/useCardActions';
import { loadSession } from '../session/sessionApi';
import { useLibraryRole } from '../session/useScanSession';
import { getTrayStore } from '../tray/storage';
import { uploadCover, useCoverStore } from '../detail/coverPhoto';
import { FixItPanel } from '../detail/FixItPanel';
import { PhotoViewer } from '../detail/PhotoViewer';
import { useSnapContext } from '../detail/snapContext';
import { describeCandidate } from './copy';
import { useCardsStore } from './cardsStore';

export function ResultDetail({ id }: { id: string }) {
  const { libraryId } = useActiveLibraryId();
  // The pager moves between the items of one picture without leaving this screen.
  const [currentId, setCurrentId] = useState(id);
  const candidate = useCardsStore((s) => s.byId[currentId]);
  const context = useSnapContext(currentId);
  const sentCrop = useCoverStore((s) => s.sent[currentId]);
  const [fixing, setFixing] = useState(false);
  const [sending, setSending] = useState(false);
  const [coverError, setCoverError] = useState<string | null>(null);
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
      <SafeScreen title="Result">
        <EmptyState title="That result isn't here any more" body="Open the scanner to see this scan's results." />
      </SafeScreen>
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
  const cropUri = context?.peers.find((p) => p.card.id === candidate.id)?.stored?.cropUri;
  const canCover = canAdd && !!context && !!cropUri;
  const loose = release?.format === 'game_cartridge';

  async function sendMyPhoto() {
    setSending(true);
    setCoverError(null);
    const result = await uploadCover({ canEdit: canAdd, sessionId, candidateId: candidate!.id, cropUri });
    if (!result.ok && result.reason === 'failed') setCoverError(result.message ?? null);
    setSending(false);
  }

  return (
    <SafeScreen title="Result">
      {context ? (
        <PhotoViewer
          context={context}
          currentId={candidate.id}
          onSelect={(next) => {
            setCurrentId(next);
            setFixing(false);
            setCoverError(null);
          }}
        />
      ) : null}
      <Card>
        <View style={{ flexDirection: 'row', gap: spacing.md }}>
          <Cover title={copy.title} uri={sentCrop ?? release?.coverUrl} width={96} />
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
            <Text variant="heading">
              {candidate.alreadyOwnedCount === 1
                ? `You own this${candidate.ownedAt[0] ? `: ${candidate.ownedAt[0]}` : ''}`
                : `You own ${candidate.alreadyOwnedCount} copies`}
            </Text>
            {candidate.alreadyOwnedCount === 1
              ? null
              : candidate.ownedAt.map((place, index) => (
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

      {canAdd && context ? (
        <Card>
          {sentCrop ? (
            <View testID="cover-choice" style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
              <Image source={{ uri: sentCrop }} accessibilityLabel="Your photo as the cover" accessibilityIgnoresInvertColors style={{ width: 64, height: 64, borderRadius: 6 }} />
              <Text variant="label">Your photo is the cover</Text>
            </View>
          ) : canCover ? (
            <>
              {loose ? <Text tone="muted">A loose cartridge looks best as your own photo.</Text> : null}
              <Button label="Use my photo" variant={loose ? 'primary' : 'secondary'} busy={sending} onPress={() => void sendMyPhoto()} />
              {coverError ? (
                <Text tone="danger" accessibilityRole="alert">
                  {coverError}
                </Text>
              ) : null}
            </>
          ) : null}
          <Button label="Wrong item? Fix it" variant="secondary" onPress={() => setFixing(true)} />
        </Card>
      ) : null}
      {fixing ? <FixItPanel candidate={candidate} sessionId={sessionId} onClose={() => setFixing(false)} /> : null}
    </SafeScreen>
  );
}

function SafeScreen({ title, children }: { title: string; children: ReactNode }) {
  const colors = useColors();
  return (
    <SafeAreaView testID="result-safe-area" edges={['bottom', 'left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen title={title}>{children}</Screen>
    </SafeAreaView>
  );
}
