// The walk's view of your own photo (#833). The band: a window onto the still, zoomed onto the item, everything else dimmed and the
// item lit, gliding to the next item so you keep your place on the shelf. The strip: the item itself, clipped out of the same still
// and turned upright when it is a spine. Both are one image moved by transforms inside a clipping view: no new files are made.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useRef } from 'react';
import { Image, Pressable, View } from 'react-native';
import Animated, { useAnimatedStyle, useSharedValue, withTiming, type SharedValue } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import type { Rect, TextOrientation } from '@/native/cm-vision';
import type { Placed, Size } from '@/features/scan/detail/overlay';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { Cover, Text } from '@/ui';
import { motionMs } from '@/ui/motion';
import { BAND_HEIGHT, bandView, stripView } from './placement';

export { BAND_HEIGHT };

type Glide = { left: SharedValue<number>; top: SharedValue<number>; width: SharedValue<number>; height: SharedValue<number> };

/** Four shared values for one rectangle, starting at `start`. */
function useRect(start: Placed): Glide {
  const left = useSharedValue(start.left);
  const top = useSharedValue(start.top);
  const width = useSharedValue(start.width);
  const height = useSharedValue(start.height);
  return { left, top, width, height };
}

/** Moves a rectangle to `target`: gliding (`settle`), or at once (Reduce Motion, a new photo). */
function moveTo(rect: Glide, target: Placed, now: boolean) {
  const to = (v: number) => (now ? v : withTiming(v, { duration: motionMs.settle }));
  rect.left.value = to(target.left);
  rect.top.value = to(target.top);
  rect.width.value = to(target.width);
  rect.height.value = to(target.height);
}

const NO_BOX: Placed = { left: 0, top: 0, width: 0, height: 0 };

export function ShelfBand({
  snapId,
  still,
  box,
  width,
  label,
  onOpen,
}: {
  snapId: string;
  still: { uri: string; width: number; height: number };
  box?: Rect;
  width: number;
  /** "Your photo, item highlighted, 4th from left". */
  label: string;
  /** Opens the full photo. */
  onOpen?: () => void;
}) {
  const reduceMotion = useReduceMotion();
  const view = bandView(still, box, { width, height: BAND_HEIGHT });
  const image = useRect(view.image);
  const lit = useRect(view.box ?? NO_BOX);
  const fade = useSharedValue(1);
  // A new photo does not glide from the last one: it cross-fades in where it belongs. The same photo glides to the next item.
  const shownSnap = useRef(snapId);
  const target = view.box ?? NO_BOX;
  useEffect(() => {
    const newPhoto = shownSnap.current !== snapId;
    shownSnap.current = snapId;
    moveTo(image, view.image, reduceMotion || newPhoto);
    moveTo(lit, target, reduceMotion || newPhoto);
    if (newPhoto && !reduceMotion) {
      fade.value = 0;
      fade.value = withTiming(1, { duration: motionMs.settle });
    }
    // The shared values are stable; the numbers below are what moves them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [snapId, reduceMotion, view.image.left, view.image.top, view.image.width, view.image.height, target.left, target.top, target.width, target.height]);

  // Explicit deps (only stable shared values are read): web bundlers without the Reanimated plugin need them stated.
  const imageStyle = useAnimatedStyle(() => ({ left: image.left.value, top: image.top.value, width: image.width.value, height: image.height.value, opacity: fade.value }), []);
  const above = useAnimatedStyle(() => ({ height: Math.max(0, lit.top.value) }), []);
  const below = useAnimatedStyle(() => ({ top: Math.max(0, lit.top.value + lit.height.value) }), []);
  const leftOf = useAnimatedStyle(() => ({ top: lit.top.value, height: lit.height.value, width: Math.max(0, lit.left.value) }), []);
  const rightOf = useAnimatedStyle(() => ({ top: lit.top.value, height: lit.height.value, left: lit.left.value + lit.width.value }), []);
  const frame = useAnimatedStyle(() => ({ left: lit.left.value - 1, top: lit.top.value - 1, width: lit.width.value + 2, height: lit.height.value + 2 }), []);

  return (
    <Pressable testID="walk-band" accessibilityRole="image" accessibilityLabel={label} accessibilityHint={onOpen ? 'Opens the whole photo' : undefined} onPress={onOpen} style={styles.band}>
      <Animated.View style={[styles.layer, imageStyle]}>
        <Image source={{ uri: still.uri }} resizeMode="stretch" style={styles.fill} accessible={false} accessibilityIgnoresInvertColors />
      </Animated.View>
      {view.box ? (
        <>
          <Animated.View pointerEvents="none" style={[styles.scrim, styles.wide, above]}>
            <View style={styles.deeper} />
          </Animated.View>
          <Animated.View pointerEvents="none" style={[styles.scrim, styles.wide, styles.toBottom, below]}>
            <View style={styles.deeper} />
          </Animated.View>
          <Animated.View pointerEvents="none" style={[styles.scrim, styles.fromLeft, leftOf]}>
            <View style={styles.deeper} />
          </Animated.View>
          <Animated.View pointerEvents="none" style={[styles.scrim, styles.toRight, rightOf]}>
            <View style={styles.deeper} />
          </Animated.View>
          <Animated.View testID="walk-box" pointerEvents="none" style={[styles.outline, frame]}>
            <View style={styles.accentBox} />
          </Animated.View>
        </>
      ) : null}
    </Pressable>
  );
}

/** The item on its own: clipped out of the still, turned upright when it is a spine, as large as `max` allows. */
export function SpineStrip({
  still,
  box,
  max,
  pass,
  label = 'Your photo of this spine',
}: {
  still: { uri: string; width: number; height: number };
  box: Rect;
  max: Size;
  /** The Vision pass that read the title: the strip turns by it so the title reads left to right. */
  pass?: TextOrientation;
  label?: string;
}) {
  const view = stripView({ width: still.width, height: still.height }, box, max, pass);
  if (!view) return null;
  return (
    <View testID="spine-strip" accessible accessibilityRole="image" accessibilityLabel={label} style={styles.strip(view.outer)}>
      <View testID={view.turned ? 'spine-strip-turned' : 'spine-strip-flat'} style={styles.clip(view.clip, view.turn)}>
        <Image source={{ uri: still.uri }} resizeMode="stretch" style={styles.placed(view.image)} accessible={false} accessibilityIgnoresInvertColors />
      </View>
    </View>
  );
}

/** With the still cleaned up: the stored crop, then the server's crop. */
export function CropStrip({ title, uri }: { title: string; uri: string }) {
  return (
    <View testID="spine-strip-crop" style={styles.cropRow}>
      <Cover title={title} uri={uri} width={64} />
      <Text variant="caption" tone="muted">
        Your photo of this item
      </Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  band: { width: '100%', height: BAND_HEIGHT, overflow: 'hidden', backgroundColor: theme.fixed.black },
  layer: { position: 'absolute' },
  fill: { width: '100%', height: '100%' },
  scrim: { position: 'absolute', backgroundColor: theme.fixed.scrim },
  // A second scrim inside the first: one alone barely dims dark spines, and the lit one must be the only bright thing in the band.
  deeper: { flex: 1, backgroundColor: theme.fixed.scrim },
  wide: { left: 0, right: 0, top: 0 },
  toBottom: { bottom: 0 },
  fromLeft: { left: 0 },
  toRight: { right: 0 },
  outline: { position: 'absolute', borderWidth: 1, borderColor: theme.fixed.black, borderRadius: theme.radius.sm + 1 },
  // The photo is a dark surface in either scheme (like the camera), so the box is the fixed highlight, a bright amber. The light
  // scheme's accent is a deep brown that disappears on a shelf.
  accentBox: { flex: 1, borderWidth: 3, borderColor: theme.fixed.highlight, borderRadius: theme.radius.sm },
  strip: (outer: Size) => ({
    width: outer.width,
    height: outer.height,
    overflow: 'hidden' as const,
    alignSelf: 'center' as const,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
  }),
  clip: (clip: Placed, turn: number) => ({
    position: 'absolute' as const,
    left: clip.left,
    top: clip.top,
    width: clip.width,
    height: clip.height,
    overflow: 'hidden' as const,
    transform: turn === 0 ? [] : [{ rotate: `${turn === 270 ? -90 : turn}deg` }],
  }),
  placed: (p: Placed) => ({ position: 'absolute' as const, left: p.left, top: p.top, width: p.width, height: p.height }),
  cropRow: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
}));
