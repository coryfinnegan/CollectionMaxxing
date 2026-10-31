// The horizontal strip of result cards, newest first. A new card slides in unless Reduce Motion is on.
import { FlashList } from '@shopify/flash-list';
import { useEffect, useRef, useState } from 'react';
import { Animated, Pressable, useWindowDimensions, View } from 'react-native';
import type { CandidateDto, ReleaseDto } from '@/api/generated/models';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Cover, Text } from '@/ui';
import { useOwnedLookup } from '@/features/offline/hooks';
import { alternativesOf } from '@/features/review/grouping';
import { isLocalCard } from './cardsStore';
import { describeCandidate, isPictureCandidate, ownershipLine, slideDuration } from './copy';
import { OwnedCard } from './OwnedCard';
import { PickAlternatives } from './PickAlternatives';
import { WishlistCard } from './WishlistCard';
import { answerLineCap, CARD_WIDTH, clampFontScale, estimateStripHeight, noteLineCap, staleLineCap } from './stripHeight';

export { useReduceMotion };
export { CARD_WIDTH } from './stripHeight';

/**
 * "Do I own this?" for a card the catalog lookup has not resolved (offline, or it failed). The server answers when it
 * can; otherwise the local copy answers fully — exact copies, soft matches, how old the answer is.
 */
function OwnedAnswerLine({ code, scale }: { code: string | null; scale: number }) {
  const answer = useOwnedLookup(code);
  const said = ownershipLine(answer);
  const line = said?.text ?? (answer.notInLibrary ? answer.notInLibraryText : null);
  if (!line) return null;
  return (
    <View accessibilityLiveRegion="polite">
      <Text variant="label" tone={said?.owned || answer.wishlistItemId ? 'accent' : 'muted'} numberOfLines={answerLineCap(scale)}>
        {line}
      </Text>
      {said?.note ? (
        <Text variant="caption" tone="muted" numberOfLines={noteLineCap(scale)}>
          {said.note}
        </Text>
      ) : null}
      {answer.staleMessage ? (
        <Text variant="caption" tone="muted" numberOfLines={staleLineCap(scale)}>
          {answer.staleMessage}
        </Text>
      ) : null}
    </View>
  );
}

/**
 * A new card's other facts, informational only: Add stays the default (docs/02 §7). The same work in another shape, or that the
 * library sold this release (the candidate says so online, the phone's own copy offline).
 */
function SoftAnswerLine({ releaseId, soldItemId, scale }: { releaseId: string; soldItemId: string | null; scale: number }) {
  const answer = useOwnedLookup({ releaseId });
  const said = ownershipLine({ ...answer, soldItemId: answer.soldItemId ?? soldItemId });
  if (!said) return null;
  return (
    <View accessibilityLiveRegion="polite">
      <Text variant="label" tone={said.owned ? 'accent' : 'muted'} numberOfLines={answerLineCap(scale)}>
        {said.text}
      </Text>
      {said.note ? (
        <Text variant="caption" tone="muted" numberOfLines={noteLineCap(scale)}>
          {said.note}
        </Text>
      ) : null}
      {answer.staleMessage ? (
        <Text variant="caption" tone="muted" numberOfLines={staleLineCap(scale)}>
          {answer.staleMessage}
        </Text>
      ) : null}
    </View>
  );
}

/** Where "Move it here" puts the copy: the session spot, by id and by name. */
export type MoveTarget = { locationId: string; label: string };

/** The move toast: what happened, and whether Undo is still on offer. */
export type MoveToast = { message: string; canUndo: boolean };

export type CardActions = {
  canAdd: boolean;
  onOpen(candidate: CandidateDto): void;
  onAdd(candidate: CandidateDto): void;
  onWishlist(candidate: CandidateDto): void;
  /** Fix it on a picture's item the catalog could not find: opens a search that starts from what was read. */
  onFixIt?(candidate: CandidateDto): void;
  /** Pick on a close call's card: binds the alternative, like choosing it in Review. */
  onPick?(candidate: CandidateDto, release: ReleaseDto): void;
  /** Skip on an owned card: the owned default; the card goes away and nothing is added. */
  onSkip?(candidate: CandidateDto): void;
  /** Move it here on an owned card: the first exact copy moves to `moveTo`. */
  onMove?(candidate: CandidateDto): void;
  /** Undo on an auto-added card: the auto-add is taken back and the card comes back undecided. */
  onUndoAutoCommit?(candidate: CandidateDto): void;
  /** Verify edition on a T1 auto-added card: opens the camera bound to the item it made. */
  onVerifyEdition?(candidate: CandidateDto): void;
  /** Null hides Move (in-store, Not placed). */
  moveTo?: MoveTarget | null;
  moveToast?: MoveToast | null;
  onUndoMove?(): void;
  /** Friendly message per candidate id when its last action failed. */
  errors?: Record<string, string>;
  busy?: Record<string, boolean>;
  /** Candidates already put on the wishlist from their card. */
  wishlisted?: Record<string, true>;
  /** Got it on a wishlist card, Found it on a missing one: the item becomes owned with one PATCH. */
  onMoveItem?(candidate: CandidateDto, itemId: string, move: 'got-it' | 'found'): void;
  /** Undo of the move above: puts the item back as it was. */
  onUndoMoveItem?(candidate: CandidateDto): void;
  /** Cards whose item was moved from the card, with what the card says now ("In your library"). */
  moved?: Record<string, string>;
};

export function ResultCard({ candidate, actions, reduceMotion }: { candidate: CandidateDto; actions: CardActions; reduceMotion: boolean }) {
  const colors = useColors();
  const copy = describeCandidate(candidate);
  const { fontScale } = useWindowDimensions();
  const scale = clampFontScale(fontScale);
  const [slide] = useState(() => new Animated.Value(reduceMotion ? 1 : 0));
  useEffect(() => {
    Animated.timing(slide, { toValue: 1, duration: slideDuration(reduceMotion), useNativeDriver: true }).start();
  }, [slide, reduceMotion]);

  // VoiceOver ignores live regions: say a card when it arrives, and say it again when its answer settles.
  const lastSpoken = useRef<string | undefined>(undefined);
  useEffect(() => {
    announceForScreenReader(`New result: ${copy.title}`);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per card
  }, []);
  useEffect(() => {
    const now = copy.kind === 'pending' ? 'pending' : [copy.title, copy.status].filter(Boolean).join('. ');
    if (lastSpoken.current !== undefined && lastSpoken.current !== now && now !== 'pending') announceForScreenReader(now);
    lastSpoken.current = now;
  }, [copy.kind, copy.title, copy.status]);

  const addable = actions.canAdd && copy.kind === 'new' && !!candidate.release;
  const accepted = candidate.decision === 'accepted';
  const wishlisted = !!actions.wishlisted?.[candidate.id];
  const label = [copy.title, copy.subtitle, copy.status].filter(Boolean).join(', ');
  const error = actions.errors?.[candidate.id];
  const busy = !!actions.busy?.[candidate.id];
  const canFix = actions.canAdd && !!actions.onFixIt && isPictureCandidate(candidate) && (copy.kind === 'not_found' || copy.kind === 'error');
  const canPick = actions.canAdd && !!actions.onPick && copy.kind === 'pick' && alternativesOf(candidate).length > 0;

  return (
    <Animated.View
      testID={`card-${candidate.rawCode ?? candidate.id}`}
      style={{
        width: CARD_WIDTH,
        marginRight: spacing.sm,
        opacity: slide,
        transform: [{ translateX: slide.interpolate({ inputRange: [0, 1], outputRange: [-24, 0] }) }],
      }}
    >
      <View style={{ backgroundColor: colors.surface, borderRadius: radius.md, padding: spacing.sm, gap: spacing.sm, borderWidth: 1, borderColor: colors.line }}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={`Open ${label}`}
          disabled={isLocalCard(candidate)} // the phone's own card has no detail until the server answers
          onPress={() => actions.onOpen(candidate)}
          style={{ flexDirection: 'row', gap: spacing.sm }}
        >
          <Cover title={copy.title} uri={candidate.release?.coverUrl} width={44} />
          <View style={{ flex: 1, gap: spacing.xs }}>
            <Text variant="label" numberOfLines={2}>
              {copy.title}
            </Text>
            {copy.subtitle ? (
              <Text variant="caption" tone="muted" numberOfLines={1}>
                {copy.subtitle}
              </Text>
            ) : null}
          </View>
        </Pressable>
        {copy.kind === 'owned' ? (
          <OwnedCard candidate={candidate} copy={copy} actions={actions} scale={scale} />
        ) : copy.kind === 'wishlist' ? (
          <WishlistCard candidate={candidate} copy={copy} actions={actions} />
        ) : (
          <>
            {copy.status ? (
              <Text variant="label" tone={copy.kind === 'error' ? 'danger' : 'muted'} accessibilityLiveRegion="polite">
                {copy.status}
              </Text>
            ) : null}
            {(copy.kind === 'pending' || copy.kind === 'error') && candidate.rawCode ? (
              <OwnedAnswerLine code={candidate.rawCode} scale={scale} />
            ) : null}
            {copy.kind === 'new' && candidate.release ? (
              <SoftAnswerLine releaseId={candidate.release.id} soldItemId={candidate.soldItemId ?? null} scale={scale} />
            ) : null}
            {copy.kind === 'added' && actions.onUndoAutoCommit ? (
              <Button
                label="Undo"
                variant="secondary"
                accessibilityLabel={`Undo adding ${copy.title}`}
                busy={busy}
                onPress={() => actions.onUndoAutoCommit?.(candidate)}
              />
            ) : null}
            {copy.kind === 'added' && candidate.tier === 'T1' && candidate.createdItemId && actions.onVerifyEdition ? (
              <Button
                label="Verify edition"
                variant="secondary"
                accessibilityLabel={`Verify edition for ${copy.title}`}
                disabled={busy}
                onPress={() => actions.onVerifyEdition?.(candidate)}
              />
            ) : null}
            {canFix ? (
              <Button label="Fix it" variant="secondary" accessibilityLabel={`Fix it: ${copy.title}`} onPress={() => actions.onFixIt?.(candidate)} />
            ) : null}
            {canPick && actions.onPick ? <PickAlternatives candidate={candidate} disabled={busy} onPick={actions.onPick} /> : null}
            {addable && (accepted || wishlisted) ? (
              <Text variant="label" tone="accent">
                {wishlisted ? 'On your wishlist' : 'Added to this scan'}
              </Text>
            ) : null}
            {addable && !accepted && !wishlisted ? (
              <View style={{ flexDirection: 'row', gap: spacing.xs }}>
                <Button label="+ Add" accessibilityLabel={`Add ${copy.title}`} busy={busy} onPress={() => actions.onAdd(candidate)} style={{ flex: 1, paddingHorizontal: spacing.sm }} />
                <Button
                  label="Wishlist"
                  variant="secondary"
                  accessibilityLabel={`Add ${copy.title} to wishlist`}
                  disabled={busy}
                  onPress={() => actions.onWishlist(candidate)}
                  style={{ flex: 1, paddingHorizontal: spacing.sm }}
                />
              </View>
            ) : null}
          </>
        )}
        {error ? (
          <Text variant="caption" tone="danger" accessibilityLiveRegion="polite">
            {error}
          </Text>
        ) : null}
      </View>
    </Animated.View>
  );
}

export function ResultCards({ cards, actions }: { cards: CandidateDto[]; actions: CardActions }) {
  const reduceMotion = useReduceMotion();
  const { fontScale } = useWindowDimensions();
  if (cards.length === 0) return null;
  return (
    // The strip fits its tallest card; shorter cards keep their whitespace below.
    <View testID="result-cards" style={{ height: estimateStripHeight(cards, actions, fontScale) }}>
      <FlashList
        horizontal
        data={cards}
        keyExtractor={(c) => c.id}
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={{ paddingHorizontal: spacing.lg }}
        renderItem={({ item }) => <ResultCard candidate={item} actions={actions} reduceMotion={reduceMotion} />}
        extraData={actions}
        accessibilityLabel="Result cards, newest first"
      />
    </View>
  );
}
