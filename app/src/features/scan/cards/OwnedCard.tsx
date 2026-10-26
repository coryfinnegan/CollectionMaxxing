// The owned card (design §7): "Already own — Hardcover, Living room › Shelf B" with Add another copy /
// Move it here / Skip. The answer comes from the ownership lookup (the server first, the owned index when
// offline), falling back to what the candidate carries. Move hides without a target (in-store, Not placed).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto } from '@/api/generated/models';
import { useOwnedLookup } from '@/features/offline/hooks';
import { Button, Text } from '@/ui';
import { alreadyOwnLine, formatName, type CardCopy } from './copy';
import type { CardActions } from './ResultCards';
import { answerLineCap, staleLineCap } from './stripHeight';

export function OwnedCard({ candidate, copy, actions, scale }: { candidate: CandidateDto; copy: CardCopy; actions: CardActions; scale: number }) {
  const releaseId = candidate.release?.id ?? null;
  const answer = useOwnedLookup(releaseId ? { releaseId } : null);
  const first = answer.owned[0];
  const line = first
    ? alreadyOwnLine(first.platform ?? formatName(first.format), first.locationPath, answer.owned.length)
    : (copy.status ?? 'Already own');
  const accepted = candidate.decision === 'accepted';
  const busy = !!actions.busy?.[candidate.id];
  // Which button is working: every press sets it first, so a stale value never shows.
  const [pressed, setPressed] = useState<'add' | 'move' | 'skip' | null>(null);
  const canAct = actions.canAdd && !accepted;
  const canMove = canAct && !!actions.onMove && !!actions.moveTo;

  return (
    <View style={styles.body}>
      <Text variant="label" tone="accent" accessibilityLiveRegion="polite" numberOfLines={answerLineCap(scale)}>
        {line}
      </Text>
      {first && answer.staleMessage ? (
        <Text variant="caption" tone="muted" numberOfLines={staleLineCap(scale)}>
          {answer.staleMessage}
        </Text>
      ) : null}
      {accepted ? (
        <Text variant="label" tone="accent">
          Added to this scan
        </Text>
      ) : null}
      {canAct ? (
        <View style={styles.actions}>
          <Button
            label="Add another copy"
            accessibilityLabel={`Add another copy of ${copy.title}`}
            variant="secondary"
            disabled={busy}
            busy={busy && pressed === 'add'}
            onPress={() => {
              setPressed('add');
              actions.onAdd(candidate);
            }}
          />
          {canMove ? (
            <Button
              label="Move it here"
              accessibilityLabel={`Move ${copy.title} to ${actions.moveTo?.label}`}
              variant="secondary"
              disabled={busy}
              busy={busy && pressed === 'move'}
              onPress={() => {
                setPressed('move');
                actions.onMove?.(candidate);
              }}
            />
          ) : null}
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
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  body: { gap: theme.spacing.sm },
  actions: { gap: theme.spacing.xs },
}));
