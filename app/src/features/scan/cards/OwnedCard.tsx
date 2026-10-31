// The owned card (design §7): "Already own — Hardcover, Living room › Shelf B" with Add another copy /
// Move it here / Skip. The answer comes from the ownership lookup (the server first, the owned index when
// offline), falling back to what the candidate carries. Move hides without a target (in-store, Not placed).
// A copy that is marked missing is still yours: the line says so ("· marked missing") and Found it is the action.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import type { CandidateDto } from '@/api/generated/models';
import { useOwnedLookup } from '@/features/offline/hooks';
import { Button, Text } from '@/ui';
import { alreadyOwnLine, formatName, MARKED_MISSING_NOTE, type CardCopy } from './copy';
import type { CardActions } from './ResultCards';
import { answerLineCap, noteLineCap, staleLineCap } from './stripHeight';

export function OwnedCard({ candidate, copy, actions, scale }: { candidate: CandidateDto; copy: CardCopy; actions: CardActions; scale: number }) {
  const releaseId = candidate.release?.id ?? null;
  const answer = useOwnedLookup(releaseId ? { releaseId } : null);
  const first = answer.owned[0];
  // Every copy is marked missing: it is yours, you just can't find it, so the way forward is Found it.
  const allMissing = !!first && answer.owned.every((c) => c.status === 'missing');
  const line = first
    ? alreadyOwnLine(first.platform ?? formatName(first.format), first.locationPath, answer.owned.length)
    : (copy.status ?? 'Already own');
  const accepted = candidate.decision === 'accepted';
  const busy = !!actions.busy?.[candidate.id];
  const moved = actions.moved?.[candidate.id];
  // Which button is working: every press sets it first, so a stale value never shows.
  const [pressed, setPressed] = useState<'add' | 'move' | 'skip' | 'found' | 'undo' | null>(null);
  const canAct = actions.canAdd && !accepted && !moved;
  const canMove = canAct && !allMissing && !!actions.onMove && !!actions.moveTo;
  const canFind = canAct && allMissing && !!actions.onMoveItem;

  return (
    <View style={styles.body}>
      <Text variant="label" tone="accent" accessibilityLiveRegion="polite" numberOfLines={answerLineCap(scale)}>
        {line}
      </Text>
      {allMissing ? (
        <Text variant="caption" tone="muted" numberOfLines={noteLineCap(scale)}>
          {MARKED_MISSING_NOTE}
        </Text>
      ) : null}
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
      {moved ? (
        <>
          <Text variant="label" tone="accent" accessibilityLiveRegion="polite">
            {moved}
          </Text>
          {actions.canAdd && actions.onUndoMoveItem ? (
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
        </>
      ) : null}
      {canAct ? (
        <View style={styles.actions}>
          {canFind ? (
            <Button
              label="Found it"
              accessibilityLabel={`Found it: ${copy.title}`}
              disabled={busy}
              busy={busy && pressed === 'found'}
              onPress={() => {
                setPressed('found');
                actions.onMoveItem?.(candidate, first!.itemId, 'found');
              }}
            />
          ) : (
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
          )}
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
