// A framed castle panel: in Full the 9-slice texture frame (rails and corners with
// baked shadow around a grain fill), in Flat and Accessible the same surface as a
// solid with an iron edge. Content padding clears the rails, so text always sits
// on the measured fill.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import type { ReactNode } from 'react';
import { Image, View, type StyleProp, type ViewStyle } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { type CastleMode } from './mode';
import { CASTLE_CORNER, CASTLE_EDGE, castleTextures, useCastleArt } from './textures';

export type CastleSurface = 'parchment' | 'stone';

type NineSliceProps = {
  surface: CastleSurface;
  mode: CastleMode;
  children?: ReactNode;
  style?: StyleProp<ViewStyle>;
  testID?: string;
};

const hidden = {
  accessibilityElementsHidden: true,
  importantForAccessibility: 'no-hide-descendants',
  testID: 'castle-texture',
} as const;

export function NineSlice({ surface, mode, children, style, testID }: NineSliceProps) {
  const artFor = useCastleArt();
  if (mode !== 'full') {
    return (
      <View style={[mode === 'accessible' ? styles.solidAccessible(surface) : styles.solid(surface), style]} testID={testID}>
        {children}
      </View>
    );
  }
  const art = surface === 'stone' ? artFor.stone : castleTextures.parchment;
  return (
    <View style={[styles.frame, style]} testID={testID}>
      <View style={styles.railRow}>
        <Image source={art.corner} style={styles.corner} {...hidden} />
        <Image source={art.edgeH} resizeMode="repeat" style={styles.edgeH} {...hidden} />
        <Image source={art.corner} style={[styles.corner, styles.flipH]} {...hidden} />
      </View>
      <View style={styles.middle}>
        <Image source={art.edgeV} resizeMode="repeat" style={styles.edgeV} {...hidden} />
        <View style={styles.content}>
          <Image source={art.fill} resizeMode="repeat" style={styles.fill} {...hidden} />
          {children}
        </View>
        <Image source={art.edgeV} resizeMode="repeat" style={[styles.edgeV, styles.flipH]} {...hidden} />
      </View>
      <View style={styles.railRow}>
        <Image source={art.corner} style={[styles.corner, styles.flipV]} {...hidden} />
        <Image source={art.edgeH} resizeMode="repeat" style={[styles.edgeH, styles.flipV]} {...hidden} />
        <Image source={art.corner} style={[styles.corner, styles.flipHV]} {...hidden} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  frame: {},
  railRow: { flexDirection: 'row' },
  middle: { flexDirection: 'row' },
  corner: { width: CASTLE_CORNER, height: CASTLE_CORNER },
  edgeH: { flex: 1, height: CASTLE_EDGE },
  // Sized in full (never inset alone): the web backend seeds image size from the
  // asset, and an unset dimension keeps the tile's own pixels instead of tiling.
  edgeV: { width: CASTLE_EDGE, height: '100%' },
  content: { flex: 1, padding: theme.spacing.lg, gap: theme.spacing.sm },
  fill: { position: 'absolute', top: 0, left: 0, width: '100%', height: '100%' },
  flipH: { transform: [{ scaleX: -1 }] },
  flipV: { transform: [{ scaleY: -1 }] },
  flipHV: { transform: [{ scaleX: -1 }, { scaleY: -1 }] },
  solid: (surface: CastleSurface) => ({
    backgroundColor: theme.castle[surface],
    borderColor: theme.castle.iron,
    borderWidth: 1,
    padding: theme.spacing.lg,
    gap: theme.spacing.sm,
  }),
  solidAccessible: (surface: CastleSurface) => ({
    backgroundColor: theme.castle[surface],
    borderColor: theme.castle.iron,
    borderWidth: 2,
    padding: theme.spacing.lg,
    gap: theme.spacing.sm,
  }),
}));
