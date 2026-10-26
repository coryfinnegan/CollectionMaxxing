// A hanging section marker: a heraldic banner beside its label, never under it.
// The cloth color-codes the media (books, games, movies) or the season (#362);
// without either it is plain iron. Full hangs the cloth from a brass rod, Flat leaves the rod off,
// Accessible doubles the edge. The label beside the cloth carries the meaning, so
// screen readers meet one header, never cloth and text separately.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';
import { type CastleMode, useCastleMode } from './mode';
import { seasonCloth, type Season } from './season';

export type BannerMedium = 'book' | 'game' | 'film';

type BannerMarkerProps = {
  label: string;
  /** The media the cloth color-codes; without one the cloth is plain iron. */
  medium?: BannerMedium;
  /** A seasonal cloth (#362), winning over the medium when both are given. */
  season?: Season;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function BannerMarker({ label, medium, season, mode, testID }: BannerMarkerProps) {
  const hooked = useCastleMode();
  const resolved = mode ?? hooked;
  return (
    <View accessibilityRole="header" style={styles.row} testID={testID}>
      <View style={styles.cloth(resolved, medium, season)} testID="banner-cloth">
        {resolved === 'full' ? <View style={styles.rod} testID="banner-rod" /> : null}
      </View>
      <Text variant="label">{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.sm, alignSelf: 'flex-start' },
  cloth: (resolved: CastleMode, medium: BannerMedium | undefined, season: Season | undefined) => ({
    width: 26,
    height: 34,
    alignItems: 'center',
    backgroundColor: season
      ? theme.castle[seasonCloth[season]]
      : medium === 'book'
        ? theme.castle.bannerBook
        : medium === 'game'
          ? theme.castle.bannerGame
          : medium === 'film'
            ? theme.castle.bannerFilm
            : theme.castle.iron,
    borderColor: theme.castle.iron,
    borderWidth: resolved === 'accessible' ? 2 : 1,
    borderTopWidth: 0,
    borderBottomLeftRadius: theme.radius.sm,
    borderBottomRightRadius: theme.radius.sm,
  }),
  rod: { width: 32, height: 4, marginTop: -2, borderRadius: 2, backgroundColor: theme.castle.brass },
}));
