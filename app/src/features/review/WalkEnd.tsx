// The end of the walk (#833): what happened to everything, the skipped rows, where new items go, and one Add. Shown when the last
// card is answered, or at once when nothing needed the person. Add is never automatic: commit, the celebration and Undo stay as
// they are in the list.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import Animated, { FadeIn } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import type { LocationDto } from '@/api/generated/models';
import { breadcrumb } from '@/features/item/breadcrumb';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { Button, Text } from '@/ui';
import { maybeEntering, motionMs } from '@/ui/motion';
import { addLabel, leftOutLine } from './plan';
import { AddToControls } from './ReviewList';
import type { Review } from './RowSheets';
import { SkippedRows } from './SkippedRows';
import { TextAction } from './TextAction';
import { moreToCheckLabel } from './walk';

export type EndCounts = {
  /** Ready rows that matched by themselves. */
  confident: number;
  /** Rows the person answered Add this on. */
  picked: number;
  /** Rows already in the library and not ticked. */
  owned: number;
  /** Cards the person went through. */
  walked: number;
};

/** "25 matched by themselves.", "1 matched by itself." */
export const confidentLine = (n: number) => `${n} matched by ${n === 1 ? 'itself' : 'themselves'}.`;
export const pickedLine = (n: number) => `${n} you picked.`;
export const ownedLine = (n: number) => `${n} you own. Not added.`;
export const seeAllLabel = (n: number) => `See all ${n}`;

export function WalkEnd({
  review,
  locations,
  counts,
  more,
  reading,
  onMore,
  onCheck,
  onSeeAll,
  onUndo,
}: {
  review: Review;
  locations: LocationDto[];
  counts: EndCounts;
  /** Rows that joined after the end was reached. */
  more: number;
  reading: string | null;
  onMore(): void;
  onCheck(kind: 'confident' | 'owned'): void;
  onSeeAll(): void;
  /** Steps back to the last card answered. */
  onUndo?: () => void;
}) {
  const reduceMotion = useReduceMotion();
  const [pickingFor, setPickingFor] = useState<string | null>(null);
  const total = review.list.length;
  const pending = review.groups.pending.length;
  const addTo = breadcrumb(locations, review.draft.addTo);
  const check = (n: number) => (n === 1 ? 'Check it' : 'Check them');
  return (
    <Animated.View entering={maybeEntering(reduceMotion, FadeIn.duration(motionMs.base))} style={styles.root}>
      <View style={styles.headRow}>
        <Text variant="heading" accessibilityRole="header" style={styles.heading}>
          {counts.walked > 0 ? "That's everything that needed you." : 'Nothing needed you this time.'}
        </Text>
        {onUndo ? <TextAction label="Undo" accessibilityLabel="Undo the last answer" onPress={onUndo} /> : null}
      </View>
      {reading ? (
        <Text tone="muted" accessibilityLiveRegion="polite">
          {reading}
        </Text>
      ) : null}
      {more > 0 ? <Button label={moreToCheckLabel(more)} onPress={onMore} /> : null}

      <View style={styles.counts}>
        {counts.confident > 0 ? (
          <View style={styles.line}>
            <Text>{confidentLine(counts.confident)}</Text>
            {review.canAdd ? <TextAction label={check(counts.confident)} accessibilityLabel={`Check the ${counts.confident} matched by ${counts.confident === 1 ? 'itself' : 'themselves'}`} onPress={() => onCheck('confident')} /> : null}
          </View>
        ) : null}
        {counts.picked > 0 ? <Text>{pickedLine(counts.picked)}</Text> : null}
        {counts.owned > 0 ? (
          <View style={styles.line}>
            <Text>{ownedLine(counts.owned)}</Text>
            {review.canAdd ? <TextAction label={check(counts.owned)} accessibilityLabel={`Check the ${counts.owned} you own`} onPress={() => onCheck('owned')} /> : null}
          </View>
        ) : null}
      </View>

      <SkippedRows rows={review.skipped} canPutBack={review.canAdd} busy={review.rowBusy} errors={review.rowErrors} onPutBack={(c) => void review.putBack(c)} />

      {review.canAdd && total > 0 ? <AddToControls review={review} locations={locations} addTo={addTo} pickingFor={pickingFor} setPickingFor={setPickingFor} /> : null}

      {review.commitError ? (
        <Text tone="danger" accessibilityRole="alert">
          {review.commitError}
        </Text>
      ) : null}
      {review.canAdd && review.count > 0 && pending > 0 ? <Text tone="muted">{leftOutLine(pending)}</Text> : null}
      {review.canAdd && total > 0 ? (
        <Button label={addLabel(review.count)} haptic="commit" busy={review.committing} disabled={review.count === 0} style={styles.add} onPress={() => void review.commit()} />
      ) : null}
      {total > 0 ? <Button label={seeAllLabel(total)} variant="secondary" onPress={onSeeAll} /> : null}
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { gap: theme.spacing.lg },
  headRow: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  heading: { flex: 1 },
  counts: { gap: theme.spacing.xs },
  line: { flexDirection: 'row', alignItems: 'center', flexWrap: 'wrap', columnGap: theme.spacing.xs },
  add: { minHeight: 52 },
}));
