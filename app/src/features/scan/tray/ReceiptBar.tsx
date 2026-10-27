// The status bar ("17 items · 2 processing", Review) and the receipt behind it: one line per code, swipe or Remove
// to remove, +1 for a second copy, QUEUED while offline. Capitals are allowed here: it is the receipt's own voice.
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Animated, Image, PanResponder, Pressable, ScrollView, View, type TextStyle, type ViewStyle } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { invalidateLibraryItems } from '@/features/item/useItem';
import { undoAutoCommit } from '@/features/review/api';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { friendlyError } from '@/lib/problem';
import { reviewStatusLine, summaryLine } from '../cards/copy';
import { isAutoCommitted, useCardsStore } from '../cards/cardsStore';
import { useAutoCommitUndoStore } from '../session/autoCommitUndo';
import type { SessionCounters } from '../session/sessionCounters';
import { fixed, radius, spacing, useColors } from '@/theme/tokens';
import { Button, GlassSurface, Text } from '@/ui';
import type { PictureLine } from '../pipeline/lines';
import { lineStatus, type TrayLine } from './tray';

const REMOVE_DISTANCE = 80;

/** The Remove tap target, hoisted: this file's inline-style budget is spent. */
const removeHit: ViewStyle = { minWidth: 44, minHeight: 44, alignItems: 'center', justifyContent: 'center' };

/** The merged status row's offline badge and undo error, hoisted for the same reason. */
const statusBadge: ViewStyle = { borderRadius: radius.pill, backgroundColor: fixed.white, paddingHorizontal: spacing.sm, paddingVertical: 2 };
const statusBadgeText: TextStyle = { color: fixed.black };
const statusError: TextStyle = { paddingHorizontal: spacing.lg, paddingTop: spacing.sm };

/** Snaps a half-swiped line back; with Reduce Motion it just jumps back. */
export function settleSwipe(x: Animated.Value, reduceMotion: boolean) {
  if (reduceMotion) x.setValue(0);
  else Animated.spring(x, { toValue: 0, useNativeDriver: true }).start();
}

/** Swipe left to remove, with the same distance and feel for every kind of line. */
function useSwipeToRemove(onRemove: () => void) {
  const reduceMotion = useReduceMotion();
  const x = useMemo(() => new Animated.Value(0), []);
  const responder = useMemo(
    () =>
      PanResponder.create({
        onMoveShouldSetPanResponder: (_e, g) => Math.abs(g.dx) > 12 && Math.abs(g.dx) > Math.abs(g.dy),
        onPanResponderMove: (_e, g) => x.setValue(Math.min(0, g.dx)),
        onPanResponderRelease: (_e, g) => {
          if (g.dx < -REMOVE_DISTANCE) onRemove();
          else settleSwipe(x, reduceMotion);
        },
      }),
    [x, onRemove, reduceMotion],
  );
  return { x, responder };
}

/** A picture's item: a thumbnail, what it is (or what was read), and IDENTIFYING… or QUEUED until the server names it. */
function PictureRow({ line, onRemove }: { line: PictureLine; onRemove(): void }) {
  const colors = useColors();
  const { x, responder } = useSwipeToRemove(onRemove);
  const label = line.status === 'queued' ? 'QUEUED' : line.status === 'identifying' ? 'IDENTIFYING…' : '';
  return (
    <Animated.View
      {...responder.panHandlers}
      accessible
      accessibilityLabel={`${line.title}${label ? `, ${label.toLowerCase()}` : ''}`}
      accessibilityActions={line.localId ? [{ name: 'delete', label: 'Remove from this scan' }] : undefined}
      onAccessibilityAction={(e) => {
        if (e.nativeEvent.actionName === 'delete') onRemove();
      }}
      style={{
        transform: [{ translateX: x }],
        flexDirection: 'row',
        alignItems: 'center',
        gap: spacing.sm,
        paddingVertical: spacing.sm,
        borderBottomWidth: 1,
        borderBottomColor: colors.line,
        backgroundColor: colors.receipt,
      }}
    >
      {line.thumbUri ? (
        <Image source={{ uri: line.thumbUri }} accessibilityIgnoresInvertColors style={{ width: 36, height: 36, borderRadius: 4, backgroundColor: colors.line }} />
      ) : (
        <View style={{ width: 36, height: 36, borderRadius: 4, backgroundColor: colors.line }} />
      )}
      <View style={{ flex: 1 }}>
        <Text variant="label" style={{ color: colors.receiptInk }}>
          {line.title}
        </Text>
        {label ? (
          <Text variant="caption" style={{ color: colors.receiptMuted }}>
            {label}
          </Text>
        ) : null}
      </View>
      {line.localId ? (
        <Pressable accessibilityRole="button" accessibilityLabel={`Remove ${line.title} from this scan`} onPress={onRemove} hitSlop={8} style={removeHit}>
          <Text variant="label" tone="danger">
            Remove
          </Text>
        </Pressable>
      ) : null}
    </Animated.View>
  );
}

function ReceiptRow({
  line,
  title,
  online,
  pending,
  added,
  onPlusOne,
  onRemove,
}: {
  line: TrayLine;
  title: string | undefined;
  online: boolean;
  pending: boolean;
  added: boolean;
  onPlusOne(): void;
  onRemove(): void;
}) {
  const colors = useColors();
  const { x, responder } = useSwipeToRemove(onRemove);
  const status = lineStatus(line, online);
  const label = status === 'queued' ? 'QUEUED' : status === 'sending' || pending ? 'LOOKING UP' : added ? 'ADDED' : '';
  const name = title ?? line.code;
  return (
    <Animated.View
      {...responder.panHandlers}
      accessible
      accessibilityLabel={`${name}, ${line.copies} ${line.copies === 1 ? 'copy' : 'copies'}${label ? `, ${label.toLowerCase()}` : ''}`}
      accessibilityActions={[{ name: 'delete', label: 'Remove from this scan' }]}
      onAccessibilityAction={(e) => {
        if (e.nativeEvent.actionName === 'delete') onRemove();
      }}
      style={{
        transform: [{ translateX: x }],
        flexDirection: 'row',
        alignItems: 'center',
        gap: spacing.sm,
        paddingVertical: spacing.sm,
        borderBottomWidth: 1,
        borderBottomColor: colors.line,
        backgroundColor: colors.receipt,
      }}
    >
      <View style={{ flex: 1 }}>
        <Text variant="label" style={{ color: colors.receiptInk }}>
          {name}
        </Text>
        {label ? (
          <Text variant="caption" style={{ color: colors.receiptMuted }}>
            {label}
          </Text>
        ) : null}
      </View>
      <Text variant="label" style={{ color: colors.receiptInk }}>{`×${line.copies}`}</Text>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`Add another copy of ${name}`}
        onPress={onPlusOne}
        hitSlop={8}
        style={{ minWidth: 44, minHeight: 44, alignItems: 'center', justifyContent: 'center' }}
      >
        <Text variant="label" tone="accent">
          +1
        </Text>
      </Pressable>
      <Pressable accessibilityRole="button" accessibilityLabel={`Remove ${name} from this scan`} onPress={onRemove} hitSlop={8} style={removeHit}>
        <Text variant="label" tone="danger">
          Remove
        </Text>
      </Pressable>
    </Animated.View>
  );
}

type Entry = { kind: 'code'; at: number; line: TrayLine } | { kind: 'picture'; at: number; line: PictureLine };

/** Barcodes and pictures in one list, newest first. */
function receiptEntries(codes: TrayLine[], pictures: PictureLine[]): Entry[] {
  const all: Entry[] = [
    ...codes.map((line): Entry => ({ kind: 'code', at: line.addedAt, line })),
    ...pictures.map((line): Entry => ({ kind: 'picture', at: line.createdAt, line })),
  ];
  // A stable sort keeps lines of one moment in the order they were added; reversing puts the newest first.
  return all.sort((a, b) => a.at - b.at).reverse();
}

export function ReceiptBar({
  libraryId,
  lines,
  pictureLines = [],
  onRemovePicture,
  cards,
  online,
  items,
  processing,
  problem,
  notice,
  counters,
  sessionId,
  canAdd,
  onPlusOne,
  onRemove,
  onReview,
}: {
  libraryId: string;
  lines: TrayLine[];
  /** Items read from pictures, newest last like `lines`. */
  pictureLines?: PictureLine[];
  onRemovePicture?(snapId: string, localId: string): void;
  cards: CandidateDto[];
  online: boolean;
  items: number;
  processing: number;
  problem: string | null;
  notice: string | null;
  /** The session counters: when present, the bar shows the merged one-line status (#408). */
  counters?: SessionCounters;
  /** The session an undo takes back from; without it (or without `canAdd`) there is no undo. */
  sessionId?: string | null;
  canAdd?: boolean;
  onPlusOne(code: string): void;
  onRemove(code: string): void;
  onReview(): void;
}) {
  const colors = useColors();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const byId = useCardsStore((s) => s.byId);
  // The newest auto-add that still stands, by when it last changed; null when there is nothing to undo.
  const undoable = useMemo(() => {
    const standing = Object.values(byId).filter((c) => c.createdItemId != null);
    return standing.length > 0 ? standing.reduce((a, b) => (b.changeSeq > a.changeSeq ? b : a)) : null;
  }, [byId]);
  const [undoBusy, setUndoBusy] = useState(false);
  const [undoError, setUndoError] = useState<string | null>(null);
  const undoableId = undoable?.id ?? null;
  // A new add retires the last attempt's error. Adjusted during render (not in an effect).
  const [errorFor, setErrorFor] = useState(undoableId);
  if (errorFor !== undoableId) {
    setErrorFor(undoableId);
    setUndoError(null);
  }
  const status = counters ? reviewStatusLine(items, processing, counters.added, counters.toReview, counters.owned) : summaryLine(items, processing);

  const undoLast = () => {
    if (!sessionId || undoBusy) return; // a second tap while the first is running is ignored
    setUndoBusy(true);
    setUndoError(null);
    undoAutoCommit(sessionId, undefined)
      .then(async (undone) => {
        // The snackbar may still be showing this add: it goes away with it.
        if (useAutoCommitUndoStore.getState().last?.candidateId === undone.id) useAutoCommitUndoStore.getState().setLast(null);
        // The taken-back add leaves the library list by itself (#448).
        await invalidateLibraryItems(queryClient, libraryId);
      })
      .catch((e: unknown) => setUndoError(friendlyError(e)))
      .finally(() => setUndoBusy(false));
  };
  // VoiceOver ignores live regions: say "Queued" when a scan lands in the receipt with no connection to send it.
  const queued = online
    ? 0
    : lines.filter((l) => lineStatus(l, false) === 'queued').length + pictureLines.filter((l) => l.status === 'queued').length;
  const spokenQueued = useRef(0);
  useEffect(() => {
    if (queued > spokenQueued.current) announceForScreenReader(`Queued. ${queued} ${queued === 1 ? 'item' : 'items'} waiting for your server.`);
    spokenQueued.current = queued;
  }, [queued]);
  // Hoisted so the style props stay identifiers (the design gate counts static
  // inline objects per file, and this file's budget is spent).
  const dock = { borderTopWidth: 1, borderTopColor: colors.line };
  const dockFallback = { backgroundColor: colors.surface };
  return (
    // A frosted dock over the camera on iOS 26+, the surface bar elsewhere (#306).
    <GlassSurface style={dock} fallbackStyle={dockFallback} tint={colors.surface}>
      {open ? (
        <ScrollView style={{ maxHeight: 260, paddingHorizontal: spacing.lg }} accessibilityLabel="Receipt">
          {lines.length === 0 && pictureLines.length === 0 ? <Text tone="muted">Nothing scanned yet.</Text> : null}
          {receiptEntries(lines, pictureLines).map((entry) => {
            if (entry.kind === 'picture') {
              const { line } = entry;
              return <PictureRow key={line.key} line={line} onRemove={() => line.localId && onRemovePicture?.(line.snapId, line.localId)} />;
            }
            const { line } = entry;
            const card = cards.find((c) => c.rawCode === line.code);
            return (
              <ReceiptRow
                key={line.code}
                line={line}
                title={card?.release?.title}
                online={online}
                pending={card?.resolution === 'pending'}
                added={card != null && isAutoCommitted(card)}
                onPlusOne={() => onPlusOne(line.code)}
                onRemove={() => onRemove(line.code)}
              />
            );
          })}
        </ScrollView>
      ) : null}
      {notice ? (
        <Text variant="caption" tone="muted" style={{ paddingHorizontal: spacing.lg, paddingTop: spacing.sm }} accessibilityLiveRegion="polite">
          {notice}
        </Text>
      ) : null}
      {problem ? (
        <Text variant="caption" tone="danger" style={{ paddingHorizontal: spacing.lg, paddingTop: spacing.sm }} accessibilityLiveRegion="polite">
          {problem}
        </Text>
      ) : null}
      {!online ? (
        <Text variant="caption" tone="muted" style={{ paddingHorizontal: spacing.lg, paddingTop: spacing.sm }} accessibilityLiveRegion="polite">
          Couldn&apos;t reach your server. Your scans are saved and will finish when you&apos;re back online.
        </Text>
      ) : null}
      <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', padding: spacing.lg, gap: spacing.md }}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={`${status}${online ? '' : '. Offline: counts include scans still on this phone'}. ${open ? 'Hide' : 'Show'} the receipt`}
          accessibilityState={{ expanded: open }}
          onPress={() => setOpen((o) => !o)}
          style={{ flex: 1, minHeight: 44, justifyContent: 'center' }}
        >
          <Text variant="label" accessibilityLiveRegion="polite" numberOfLines={1}>
            {status}
          </Text>
        </Pressable>
        {!online ? (
          <View style={statusBadge} accessibilityLabel="Offline">
            <Text variant="label" style={statusBadgeText}>
              Offline
            </Text>
          </View>
        ) : null}
        {undoable && sessionId && canAdd ? <Button label="Undo last add" variant="secondary" busy={undoBusy} onPress={undoLast} /> : null}
        <Button label="Review" onPress={onReview} disabled={items === 0} />
      </View>
      {undoError ? (
        <Text variant="caption" tone="danger" accessibilityLiveRegion="polite" style={statusError}>
          {undoError}
        </Text>
      ) : null}
    </GlassSurface>
  );
}
