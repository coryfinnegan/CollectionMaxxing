// The status bar ("17 items · 2 processing", Review) and the receipt behind it: one line per code, swipe to
// remove, +1 for a second copy, QUEUED while offline. Capitals are allowed here: it is the receipt's own voice.
import { useEffect, useMemo, useRef, useState } from 'react';
import { Animated, Image, PanResponder, Pressable, ScrollView, View } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { summaryLine } from '../cards/copy';
import { spacing, useColors } from '@/theme/tokens';
import { Button, Text } from '@/ui';
import type { PictureLine } from '../pipeline/lines';
import { lineStatus, type TrayLine } from './tray';

const REMOVE_DISTANCE = 80;

/** Swipe left to remove, with the same distance and feel for every kind of line. */
function useSwipeToRemove(onRemove: () => void) {
  const x = useMemo(() => new Animated.Value(0), []);
  const responder = useMemo(
    () =>
      PanResponder.create({
        onMoveShouldSetPanResponder: (_e, g) => Math.abs(g.dx) > 12 && Math.abs(g.dx) > Math.abs(g.dy),
        onPanResponderMove: (_e, g) => x.setValue(Math.min(0, g.dx)),
        onPanResponderRelease: (_e, g) => {
          if (g.dx < -REMOVE_DISTANCE) onRemove();
          else Animated.spring(x, { toValue: 0, useNativeDriver: true }).start();
        },
      }),
    [x, onRemove],
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
        <Text variant="label" numberOfLines={1} style={{ color: '#1A1C22' }}>
          {line.title}
        </Text>
        {label ? (
          <Text variant="caption" style={{ color: '#5E6472' }}>
            {label}
          </Text>
        ) : null}
      </View>
    </Animated.View>
  );
}

function ReceiptRow({
  line,
  title,
  online,
  pending,
  onPlusOne,
  onRemove,
}: {
  line: TrayLine;
  title: string | undefined;
  online: boolean;
  pending: boolean;
  onPlusOne(): void;
  onRemove(): void;
}) {
  const colors = useColors();
  const { x, responder } = useSwipeToRemove(onRemove);
  const status = lineStatus(line, online);
  const label = status === 'queued' ? 'QUEUED' : status === 'sending' || pending ? 'LOOKING UP' : '';
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
        <Text variant="label" numberOfLines={1} style={{ color: '#1A1C22' }}>
          {name}
        </Text>
        {label ? (
          <Text variant="caption" style={{ color: '#5E6472' }}>
            {label}
          </Text>
        ) : null}
      </View>
      <Text variant="label" style={{ color: '#1A1C22' }}>{`×${line.copies}`}</Text>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`Add another copy of ${name}`}
        onPress={onPlusOne}
        hitSlop={8}
        style={{ minWidth: 44, minHeight: 44, alignItems: 'center', justifyContent: 'center' }}
      >
        <Text variant="label" tone="scan">
          +1
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
  lines,
  pictureLines = [],
  onRemovePicture,
  cards,
  online,
  items,
  processing,
  problem,
  notice,
  onPlusOne,
  onRemove,
  onReview,
}: {
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
  onPlusOne(code: string): void;
  onRemove(code: string): void;
  onReview(): void;
}) {
  const colors = useColors();
  const [open, setOpen] = useState(false);
  // VoiceOver ignores live regions: say "Queued" when a scan lands in the receipt with no connection to send it.
  const queued = online
    ? 0
    : lines.filter((l) => lineStatus(l, false) === 'queued').length + pictureLines.filter((l) => l.status === 'queued').length;
  const spokenQueued = useRef(0);
  useEffect(() => {
    if (queued > spokenQueued.current) announceForScreenReader(`Queued. ${queued} ${queued === 1 ? 'item' : 'items'} waiting for your server.`);
    spokenQueued.current = queued;
  }, [queued]);
  return (
    <View style={{ backgroundColor: colors.surface, borderTopWidth: 1, borderTopColor: colors.line }}>
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
          accessibilityLabel={`${summaryLine(items, processing)}. ${open ? 'Hide' : 'Show'} the receipt`}
          accessibilityState={{ expanded: open }}
          onPress={() => setOpen((o) => !o)}
          style={{ flex: 1, minHeight: 44, justifyContent: 'center' }}
        >
          <Text variant="label">{summaryLine(items, processing)}</Text>
        </Pressable>
        <Button label="Review" onPress={onReview} disabled={items === 0} />
      </View>
    </View>
  );
}
