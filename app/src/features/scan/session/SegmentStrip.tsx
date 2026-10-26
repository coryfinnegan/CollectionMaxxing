// The segment strip (docs/16 §5): every snap of this scan in shoot order, each a thumbnail from the snap store with a
// state dot. The pictures never leave the phone; tapping a cell opens the snap viewer at that snap.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useMemo } from 'react';
import { Image, Pressable, ScrollView, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { useStore } from 'zustand';
import { fixed, useColors, type Colors } from '@/theme/tokens';
import { Text } from '@/ui';
import { useCardsStore, withLocalCards } from '../cards/cardsStore';
import { getPipelineStore, type PicSnap } from '../pipeline/pipelineStore';
import { getSnapStore } from '../pipeline/snapStore';
import { getTrayStore, useTrayValue } from '../tray/storage';
import { inShootOrder, segmentDot, segmentLabel, segmentStateOf, type SegmentDot } from './segmentStates';

const dotStyle = (dot: SegmentDot, colors: Colors) => {
  const color = dot.fill === 'muted' ? colors.muted : dot.fill === 'ink' ? colors.ink : dot.fill === 'accent' ? colors.accent : colors.sticker;
  return dot.hollow
    ? { borderColor: color, backgroundColor: 'transparent' as const }
    : { borderColor: 'transparent' as const, backgroundColor: color };
};

function Cell({ snap, state, thumbUri, onSelect }: { snap: PicSnap; state: ReturnType<typeof segmentStateOf>; thumbUri: string | undefined; onSelect(snapId: string): void }) {
  const colors = useColors();
  const dot = segmentDot(state);
  const label = segmentLabel(state);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Snap, ${label.toLowerCase()}`}
      onPress={() => onSelect(snap.snapId)}
      style={styles.cell}
    >
      {thumbUri ? (
        <Image source={{ uri: thumbUri }} accessibilityIgnoresInvertColors style={styles.thumb} />
      ) : (
        <View style={[styles.thumb, styles.thumbMissing]} />
      )}
      <View testID={`segment-dot-${snap.snapId}`} style={[styles.dot, dotStyle(dot, colors)]} />
      <Text style={styles.caption} numberOfLines={1}>
        {label}
      </Text>
    </Pressable>
  );
}

export function SegmentStrip({ libraryId, onSelect }: { libraryId: string; onSelect(snapId: string): void }) {
  const snaps = useStore(getPipelineStore(), (s) => s.snaps);
  const byId = useCardsStore((s) => s.byId);
  const local = useCardsStore((s) => s.local);
  const tray = getTrayStore(libraryId);
  const online = useTrayValue(tray, (s) => s.online, true);
  const cards = useMemo(() => withLocalCards({ byId, local }), [byId, local]);
  const mine = useMemo(() => inShootOrder(snaps.filter((s) => s.libraryId === libraryId)), [snaps, libraryId]);
  if (mine.length === 0) return null;
  return (
    <ScrollView horizontal showsHorizontalScrollIndicator={false} accessibilityLabel="Snaps in this scan" contentContainerStyle={styles.strip}>
      {mine.map((snap) => (
        <Cell
          key={snap.snapId}
          snap={snap}
          state={segmentStateOf(snap, cards, online)}
          thumbUri={getSnapStore().get(snap.snapId)?.stillUri ?? snap.thumbUri}
          onSelect={onSelect}
        />
      ))}
    </ScrollView>
  );
}

const styles = StyleSheet.create((theme) => ({
  strip: { gap: theme.spacing.sm, paddingHorizontal: theme.spacing.lg, paddingVertical: theme.spacing.sm },
  cell: { width: 64, alignItems: 'center', gap: 4, borderRadius: theme.radius.md, backgroundColor: theme.fixed.scrim, padding: theme.spacing.xs },
  thumb: { width: 52, height: 52, borderRadius: theme.radius.sm, backgroundColor: fixed.scrim },
  thumbMissing: { borderWidth: 1, borderColor: fixed.white },
  dot: { width: 10, height: 10, borderRadius: 5, borderWidth: 2 },
  caption: { color: fixed.white },
}));
