// The horizontal strip of result cards, newest first. A new card slides in unless Reduce Motion is on.
import { FlashList } from '@shopify/flash-list';
import { useEffect, useRef, useState } from 'react';
import { Animated, Pressable, useWindowDimensions, View } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Cover, Text } from '@/ui';
import { useOwnedLookup } from '@/features/offline/hooks';
import { isLocalCard } from './cardsStore';
import { describeCandidate,isPictureCandidate, ownedCopy, slideDuration } from './copy';

export { useReduceMotion };

/**
 * "Do I own this?" for a card the catalogue lookup has not resolved (offline, or it failed). The server answers when it
 * can; otherwise the local copy does, with how old it is.
 */
function OwnedAnswerLine({ code }: { code: string | null }) {
  const answer = useOwnedLookup(code);
  const first = answer.owned[0];
  const text = first
    ? `${ownedCopy(answer.owned.length)}${first.locationPath ? `: ${first.locationPath}` : ''}`
    : answer.notInLibrary
      ? answer.notInLibraryText
      : null;
  if (!text) return null;
  return (
    <View accessibilityLiveRegion="polite">
      <Text variant="label" tone={first ? 'scan' : 'muted'}>
        {text}
      </Text>
      {answer.staleMessage ? (
        <Text variant="caption" tone="muted">
          {answer.staleMessage}
        </Text>
      ) : null}
    </View>
  );
}

export type CardActions = {
  canAdd: boolean;
  onOpen(candidate: CandidateDto): void;
  onAdd(candidate: CandidateDto): void;
  onWishlist(candidate: CandidateDto): void;
  /** Fix it on a picture's item the catalogue could not find: opens a search that starts from what was read. */
  onFixIt?(candidate: CandidateDto): void;
  /** Friendly message per candidate id when its last action failed. */
  errors?: Record<string, string>;
  busy?: Record<string, boolean>;
  /** Candidates already put on the wishlist from their card. */
  wishlisted?: Record<string, true>;
};

export const CARD_WIDTH = 168;

export function ResultCard({ candidate, actions, reduceMotion }: { candidate: CandidateDto; actions: CardActions; reduceMotion: boolean }) {
  const colors = useColors();
  const copy = describeCandidate(candidate);
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
          <View style={{ flex: 1, gap: 2 }}>
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
        {copy.status ? (
          <Text variant="label" tone={copy.kind === 'owned' ? 'scan' : copy.kind === 'error' ? 'danger' : 'muted'} accessibilityLiveRegion="polite">
            {copy.status}
          </Text>
        ) : null}
        {(copy.kind === 'pending' || copy.kind === 'error') && candidate.rawCode ? <OwnedAnswerLine code={candidate.rawCode} /> : null}
        {canFix ? (
          <Button label="Fix it" variant="secondary" accessibilityLabel={`Fix it: ${copy.title}`} onPress={() => actions.onFixIt?.(candidate)} />
        ) : null}
        {addable && (accepted || wishlisted) ? (
          <Text variant="label" tone="scan">
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
  // The strip is a fixed-height list, so it grows with Dynamic Type; the cards inside wrap their text.
  const { fontScale } = useWindowDimensions();
  if (cards.length === 0) return null;
  return (
    <View testID="result-cards" style={{ height: Math.round(190 * Math.min(Math.max(fontScale, 1), 3)) }}>
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
