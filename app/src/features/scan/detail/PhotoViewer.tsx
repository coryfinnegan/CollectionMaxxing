// Your own photo with the item boxed on it (docs/06 M4). The picture is the on-phone still; the box is drawn here, locally, from
// the normalised box Vision found. When the snap has more than one item the counter and the pager appear and the box moves.
// Nothing here is animated, so Reduce Motion has nothing to turn off. Nothing here uploads anything.
import { useState } from 'react';
import { Image, PanResponder, View, useWindowDimensions, type LayoutChangeEvent } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Button, Text } from '@/ui';
import { boxOverlay } from './overlay';
import { boxOf, type SnapContext } from './snapContext';

const MAX_HEIGHT = 380;
const SWIPE = 40;

export const PHOTO_GONE = "The photo isn't on this phone any more. The details below are still right.";

export function PhotoViewer({ context, currentId, onSelect }: { context: SnapContext; currentId: string; onSelect(cardId: string): void }) {
  const colors = useColors();
  const window = useWindowDimensions();
  const [measured, setMeasured] = useState<number | null>(null);
  const [broken, setBroken] = useState(false);

  const { peers, snap } = context;
  const index = Math.max(0, peers.findIndex((p) => p.card.id === currentId));
  const peer = peers[index];
  const many = peers.length > 1;

  const go = (step: number) => {
    const next = peers[(index + step + peers.length) % peers.length];
    if (next) onSelect(next.card.id);
  };
  // Swiping is the same as the buttons.
  const swipe = PanResponder.create({
    onMoveShouldSetPanResponder: (_, g) => Math.abs(g.dx) > 12 && Math.abs(g.dx) > Math.abs(g.dy) * 1.5,
    onPanResponderRelease: (_, g) => {
      if (Math.abs(g.dx) >= SWIPE) go(g.dx < 0 ? 1 : -1);
    },
  });

  if (!snap?.stillUri || broken) {
    return (
      <View style={{ padding: spacing.md, borderRadius: radius.md, backgroundColor: colors.surface, borderWidth: 1, borderColor: colors.line }}>
        <Text tone="muted">{PHOTO_GONE}</Text>
      </View>
    );
  }

  const frameWidth = measured ?? Math.max(0, window.width - spacing.lg * 2);
  const frameHeight = Math.min(MAX_HEIGHT, Math.round((frameWidth * snap.height) / Math.max(1, snap.width)));
  const frame = { width: frameWidth, height: frameHeight };
  const placed = peer ? boxOverlay(boxOf(peer), frame, { width: snap.width, height: snap.height }) : undefined;
  const label = many ? `Item ${index + 1} of ${peers.length} highlighted` : 'Item highlighted';

  return (
    <View style={{ gap: spacing.sm }}>
      <View
        testID="photo-frame"
        onLayout={(e: LayoutChangeEvent) => setMeasured(e.nativeEvent.layout.width)}
        style={{ width: '100%', height: frameHeight, borderRadius: radius.md, overflow: 'hidden', backgroundColor: colors.surface }}
        {...(many ? swipe.panHandlers : {})}
      >
        <Image
          source={{ uri: snap.stillUri }}
          accessibilityLabel="Your photo"
          resizeMode="contain"
          style={{ width: '100%', height: '100%' }}
          onError={() => setBroken(true)}
        />
        {placed ? (
          <View
            testID="item-box"
            accessible
            accessibilityRole="image"
            accessibilityLabel={label}
            pointerEvents="none"
            style={{
              position: 'absolute',
              left: placed.left,
              top: placed.top,
              width: placed.width,
              height: placed.height,
              borderWidth: 3,
              borderColor: colors.scan,
              borderRadius: radius.sm,
            }}
          />
        ) : null}
      </View>
      {many ? (
        <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: spacing.sm }}>
          <Button label="Previous item" variant="secondary" onPress={() => go(-1)} />
          <Text accessibilityLiveRegion="polite">{`${index + 1} of ${peers.length} in this photo`}</Text>
          <Button label="Next item" variant="secondary" onPress={() => go(1)} />
        </View>
      ) : null}
    </View>
  );
}
