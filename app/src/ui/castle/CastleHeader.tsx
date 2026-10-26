// The castle frame header: crenellations over a stone band carrying a Cinzel
// display title, seated on a ledge. Flat paints the band solid between iron
// edges; Accessible doubles the edges.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Image, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';
import { type CastleMode, useCastleMode } from './mode';
import { castleTextures } from './textures';

type CastleHeaderProps = {
  title: string;
  subtitle?: string;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function CastleHeader({ title, subtitle, mode, testID }: CastleHeaderProps) {
  const hooked = useCastleMode();
  const resolved = mode ?? hooked;
  const heading = (
    <>
      <Text variant="castleDisplay" accessibilityRole="header" style={styles.title}>
        {title}
      </Text>
      {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
    </>
  );
  if (resolved !== 'full') {
    return (
      <View style={[resolved === 'accessible' ? styles.bandAccessible : styles.band, styles.padding]} testID={testID}>
        {heading}
      </View>
    );
  }
  return (
    <View testID={testID}>
      <Image source={castleTextures.crenel} resizeMode="repeat" style={styles.crenel} testID="castle-texture" />
      <View style={styles.body}>
        <Image source={castleTextures.stone.fill} resizeMode="repeat" style={styles.fill} testID="castle-texture" />
        {heading}
      </View>
      <Image source={castleTextures.ledge} resizeMode="repeat" style={styles.ledge} testID="castle-texture" />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  crenel: { height: 24, width: '100%' },
  ledge: { height: 16, width: '100%' },
  body: { padding: theme.spacing.lg, gap: theme.spacing.sm },
  fill: { position: 'absolute', top: 0, left: 0, width: '100%', height: '100%' },
  padding: { padding: theme.spacing.lg, gap: theme.spacing.sm },
  band: {
    backgroundColor: theme.castle.stone,
    borderTopColor: theme.castle.iron,
    borderBottomColor: theme.castle.iron,
    borderTopWidth: 1,
    borderBottomWidth: 1,
  },
  bandAccessible: {
    backgroundColor: theme.castle.stone,
    borderTopColor: theme.castle.iron,
    borderBottomColor: theme.castle.iron,
    borderTopWidth: 2,
    borderBottomWidth: 2,
  },
  title: { color: theme.castle.stoneInk },
  subtitle: { color: theme.castle.stoneInk },
}));
