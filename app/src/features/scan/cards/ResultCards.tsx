// The horizontal strip of result cards, newest first. A new card slides in unless Reduce Motion is on.
import { FlashList } from '@shopify/flash-list';
import { useEffect, useState } from 'react';
import { AccessibilityInfo, Animated, Pressable, View } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Cover, Text } from '@/ui';
import { useOwnedLookup } from '@/features/offline/hooks';
import { describeCandidate, isPictureCandidate, ownedCopy, slideDuration } from './copy';

export function useReduceMotion(): boolean {
  const [reduce, setReduce] = useState(false);
  useEffect(() => {
    let alive = true;
    void AccessibilityInfo.isReduceMotionEnabled().then((on) => alive && setReduce(on));
    const sub = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduce);
    return () => {
      alive = false;
      sub.remove();
    };
  }, []);
  return reduce;
}

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
  if (cards.length === 0) return null;
  return (
    <View style={{ height: 190 }}>
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
