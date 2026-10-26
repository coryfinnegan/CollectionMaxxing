// The castle frame header: crenellations over a stone band carrying a Cinzel
// display title, seated on a ledge. Flat paints the band solid between iron
// edges; Accessible doubles the edges.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Image, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { PlacedMascot } from '../mascot/PlacedMascot';
import { Text } from '../Text';
import { type CastleMode, useCastleMode } from './mode';
import { useCastleArt } from './textures';

type CastleHeaderProps = {
  title: string;
  subtitle?: string;
  /** Perches the dragon (static idle, decorative) at the header's end. Honors the Dragon appearances setting. */
  perch?: boolean;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function CastleHeader({ title, subtitle, perch = false, mode, testID }: CastleHeaderProps) {
  const hooked = useCastleMode();
  const art = useCastleArt();
  const resolved = mode ?? hooked;
  const words = (
    <>
      <Text variant="castleDisplay" accessibilityRole="header" style={styles.title}>
        {title}
      </Text>
      {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
    </>
  );
  const heading = perch ? (
    <View style={styles.perchRow}>
      <View style={styles.perchWords}>{words}</View>
      <PlacedMascot pose="idle" placement="decorative" size={88} testID="castle-perch" />
    </View>
  ) : (
    words
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
      <Image source={art.crenel} resizeMode="repeat" style={styles.crenel} testID="castle-texture" />
      <View style={styles.body}>
        <Image source={art.stone.fill} resizeMode="repeat" style={styles.fill} testID="castle-texture" />
        {heading}
      </View>
      <Image source={art.ledge} resizeMode="repeat" style={styles.ledge} testID="castle-texture" />
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
  perchRow: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.md },
  perchWords: { flex: 1, gap: theme.spacing.sm },
}));
