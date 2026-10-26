// A small hanging marker: iron with a brass edge. Textured in Full, solid in
// Flat, a heavier edge in Accessible.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Image, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';
import { type CastleMode, useCastleMode } from './mode';
import { castleTextures } from './textures';

type BannerMarkerProps = {
  label: string;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function BannerMarker({ label, mode, testID }: BannerMarkerProps) {
  const hooked = useCastleMode();
  const resolved = mode ?? hooked;
  return (
    <View
      style={[resolved === 'accessible' ? styles.frameAccessible : styles.frame, resolved === 'full' ? null : styles.solid]}
      testID={testID}
    >
      {resolved === 'full' ? (
        <Image source={castleTextures.ironFill} resizeMode="repeat" style={styles.fill} testID="castle-texture" />
      ) : null}
      <Text variant="label" style={styles.label}>
        {label}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  frame: {
    alignSelf: 'flex-start',
    borderColor: theme.castle.brass,
    borderWidth: 1,
    paddingVertical: theme.spacing.sm,
    paddingHorizontal: theme.spacing.md,
  },
  frameAccessible: {
    alignSelf: 'flex-start',
    borderColor: theme.castle.brass,
    borderWidth: 2,
    paddingVertical: theme.spacing.sm,
    paddingHorizontal: theme.spacing.md,
  },
  solid: { backgroundColor: theme.castle.iron },
  fill: { position: 'absolute', top: 0, left: 0, width: '100%', height: '100%' },
  label: { color: theme.castle.ironInk },
}));
