// Result detail (docs/06 M4): your own photo with the item boxed on it, the card's facts, where owned copies live, the add buttons,
// Wrong item? Fix it, and Use my photo (sends only the crop of this item, and only when an editor taps).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useState, type ReactNode } from 'react';
import { Image, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto } from '@/api/generated/models';
import { formatLabels } from '@/features/library/labels';
import { useOwnedLookup } from '@/features/offline/hooks';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Card, Cover, EmptyState, FormatBadge, Screen, Text } from '@/ui';
import { useCardActions } from '../session/useCardActions';
import { loadSession } from '../session/sessionApi';
import { useLibraryRole } from '../session/useScanSession';
import { getTrayStore, useTrayValue } from '../tray/storage';
import { uploadCover, useCoverStore } from '../detail/coverPhoto';
import { FixItPanel } from '../detail/FixItPanel';
import { PhotoViewer } from '../detail/PhotoViewer';
import { useSnapContext } from '../detail/snapContext';
import { describeCandidate, MARKED_MISSING, SOLD_COPY, wishlistStateOf } from './copy';
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
  const sessionId = useTrayValue(libraryId ? getTrayStore(libraryId) : undefined, (s) => s.sessionId, null);
  const { canAdd } = useLibraryRole(libraryId);
  const actions = useCardActions({ libraryId: libraryId ?? '', sessionId, canAdd });
  // The server's answer (the phone's own copy offline) says whether the copies you own are marked missing.
  const lookup = useOwnedLookup(candidate?.release?.id ? { releaseId: candidate.release.id } : null);

  // Opened cold (the app restarted since the scan): load the session to find the card.
  useEffect(() => {
    if (candidate || !sessionId) return;
    void loadSession(sessionId)
      .then((all) => useCardsStore.getState().merge(all, { announce: false }))
      .catch(() => undefined);
  }, [candidate, sessionId]);

  if (!candidate) {
    return (
      <SafeScreen>
        <EmptyState title="That result isn't here any more" body="Open the scanner to see this scan's results." />
      </SafeScreen>
    );
  }

  const copy = describeCandidate(candidate);
  const release = candidate.release;
  const owned = candidate.alreadyOwnedCount > 0;
  const added = copy.kind === 'added';
  const wishlistCard = copy.kind === 'wishlist';
  const moved = actions.moved?.[candidate.id];
  // Found it moves a copy that is marked missing, when every copy you own is.
  const missingCopy = lookup.owned.length > 0 && lookup.owned.every((c) => c.status === 'missing') ? lookup.owned.find((c) => c.status === 'missing') : undefined;
  const wishlistState = wishlistStateOf(candidate, lookup);
  const addable = canAdd && !!release && !owned && !added && !wishlistCard;
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
    <SafeScreen>
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
        {context ? (
          <View style={{ flexDirection: 'row', gap: spacing.md }}>
            <Cover title={copy.title} uri={sentCrop ?? release?.coverUrl} width={96} />
            <View style={{ flex: 1, gap: spacing.xs }}>
              <FactsText title={copy.title} release={release} />
            </View>
          </View>
        ) : (
          // A barcode result has no photo above, so the facts compose like the item detail hero instead of a small
          // row over dead space (#373). Same facts, no new copy.
          <View testID="result-hero" style={heroStyles.hero}>
            <Cover title={copy.title} uri={sentCrop ?? release?.coverUrl} width={160} />
            <FactsText title={copy.title} release={release} />
          </View>
        )}
      </Card>

      <Card>
        {owned ? (
          <>
            <Text variant="heading">
              {(candidate.alreadyOwnedCount === 1
                ? `You own this${candidate.ownedAt[0] ? `: ${candidate.ownedAt[0]}` : ''}`
                : `You own ${candidate.alreadyOwnedCount} copies`) + (missingCopy ? MARKED_MISSING : '')}
            </Text>
            {candidate.alreadyOwnedCount === 1
              ? null
              : candidate.ownedAt.map((place, index) => (
                  <Text key={`${place}-${index}`} tone="muted">
                    {place}
                  </Text>
                ))}
          </>
        ) : wishlistCard ? (
          <Text variant="heading">{moved?.message ?? (wishlistState.nowOwned ? 'In your library' : copy.status)}</Text>
        ) : copy.kind === 'new' ? (
          <>
            <Text variant="heading">Not in your library</Text>
            {candidate.soldItemId || lookup.soldItemId ? <Text tone="muted">{SOLD_COPY}</Text> : null}
          </>
        ) : copy.kind === 'added' ? (
          <Text variant="heading">{copy.status}</Text>
        ) : (
          <Text>{copy.status}</Text>
        )}
        {canAdd && wishlistCard && !moved && !wishlistState.nowOwned && wishlistState.itemId ? (
          <Button
            label="Got it"
            accessibilityLabel={`Got it: ${copy.title}`}
            busy={busy}
            onPress={() => actions.onMoveItem?.(candidate, wishlistState.itemId!, 'got-it')}
          />
        ) : null}
        {canAdd && owned && missingCopy && !moved ? (
          <Button
            label="Found it"
            accessibilityLabel={`Found it: ${copy.title}`}
            busy={busy}
            onPress={() => actions.onMoveItem?.(candidate, missingCopy.itemId, 'found')}
          />
        ) : null}
        {moved ? (
          <>
            {owned ? <Text tone="accent">{moved.message}</Text> : null}
            {canAdd && moved.canUndo ? <Button label="Undo" variant="secondary" accessibilityLabel={`Undo: ${copy.title}`} disabled={busy} onPress={() => actions.onUndoMoveItem?.(candidate)} /> : null}
          </>
        ) : null}
        {addable && !accepted && !wishlisted ? (
          <View style={{ flexDirection: 'row', gap: spacing.sm }}>
            <Button label="Add to library" busy={busy} onPress={() => actions.onAdd(candidate)} />
            <Button label="Wishlist" variant="secondary" disabled={busy} onPress={() => actions.onWishlist(candidate)} />
          </View>
        ) : null}
        {addable && (accepted || wishlisted) ? <Text tone="accent">{wishlisted ? 'On your wishlist' : 'Added to this scan'}</Text> : null}
        {added && actions.onUndoAutoCommit ? (
          <Button label="Undo" variant="secondary" accessibilityLabel={`Undo adding ${copy.title}`} busy={busy} onPress={() => actions.onUndoAutoCommit?.(candidate)} />
        ) : null}
        {error ? (
          <Text tone="danger" accessibilityRole="alert">
            {error}
          </Text>
        ) : null}
      </Card>

      {canAdd && context ? (
        <Card>
          {sentCrop ? (
            <View testID="cover-choice" style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
              <Image source={{ uri: sentCrop }} accessibilityLabel="Your photo as the cover" accessibilityIgnoresInvertColors style={{ width: 64, height: 64, borderRadius: radius.sm }} />
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

function FactsText({ title, release }: { title: string; release: CandidateDto['release'] }) {
  return (
    <>
      <Text variant="heading" accessibilityRole="header">
        {title}
      </Text>
      {release ? <FormatBadge label={formatLabels[release.format]} /> : null}
      {release?.creators.length ? <Text tone="muted">{release.creators.join(', ')}</Text> : null}
      <Text variant="mono" tone="muted">
        {[release?.platform?.name, release?.year].filter(Boolean).join(' · ')}
      </Text>
    </>
  );
}

const heroStyles = StyleSheet.create((theme) => ({
  hero: { alignItems: 'center', gap: theme.spacing.sm },
}));

function SafeScreen({ children }: { children: ReactNode }) {
  const colors = useColors();
  return (
    <SafeAreaView testID="result-safe-area" edges={['bottom', 'left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen>{children}</Screen>
    </SafeAreaView>
  );
}
