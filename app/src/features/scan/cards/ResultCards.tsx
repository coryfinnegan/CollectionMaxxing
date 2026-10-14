// The horizontal strip of result cards, newest first. A new card slides in unless Reduce Motion is on.
import { FlashList } from '@shopify/flash-list';
import { useEffect, useState } from 'react';
import { AccessibilityInfo, Animated, Pressable, View } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Cover, Text } from '@/ui';
import { describeCandidate, slideDuration } from './copy';

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

export type CardActions = {
  canAdd: boolean;
  onOpen(candidate: CandidateDto): void;
  onAdd(candidate: CandidateDto): void;
  onWishlist(candidate: CandidateDto): void;
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
