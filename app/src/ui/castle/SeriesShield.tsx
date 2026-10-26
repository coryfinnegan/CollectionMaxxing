// A heraldic shield marker: the milestone shield art beside its label, never under
// it (labels never sit on heraldry). The shield reuses the mascot's ShieldLayer,
// so milestone arms and series shields share one drawing. Vector art like the
// dragon itself: nothing textural to drop, so no mode prop; the label beside the
// shield carries the meaning for screen readers.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import Svg from 'react-native-svg';
import { ShieldLayer } from '../mascot/layers';
import { Text } from '../Text';

type SeriesShieldProps = {
  label: string;
  sublabel?: string;
  size?: number;
  testID?: string;
};

export function SeriesShield({ label, sublabel, size = 44, testID }: SeriesShieldProps) {
  return (
    <View style={styles.row} testID={testID}>
      <Svg width={size} height={size} viewBox="150 100 60 75" aria-hidden>
        <ShieldLayer />
      </Svg>
      <View style={styles.words}>
        <Text variant="label">{label}</Text>
        {sublabel ? (
          <Text variant="caption" tone="muted">
            {sublabel}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.sm, alignSelf: 'flex-start' },
  words: { gap: theme.spacing.xs },
}));
