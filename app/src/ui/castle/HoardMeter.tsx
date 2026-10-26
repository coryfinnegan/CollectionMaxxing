// The Great Hall's hoard (#363): a pile of brass coins that grows with the
// collection's high-water mark and never shrinks. The count beside the pile
// carries the meaning (plain words first); the coins are decorative.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';

/** Coins shown for a peak: logarithmic, so the pile keeps growing toward a full 24 at a thousand. */
export function coinsForPeak(peak: number): number {
  if (peak <= 0) return 0;
  return Math.min(24, Math.max(1, Math.round((24 * Math.log10(1 + peak)) / Math.log10(1001))));
}

type HoardMeterProps = {
  /** The high-water mark the pile is drawn from: pass useHoardPeak so it never shrinks. */
  peak: number;
  /** The live count the words report. */
  total: number;
  testID?: string;
};

export function HoardMeter({ peak, total, testID }: HoardMeterProps) {
  const coins = coinsForPeak(peak);
  return (
    <View style={styles.root} testID={testID}>
      <View style={styles.pile} testID="hoard-coins">
        {Array.from({ length: coins }, (_, i) => (
          <View key={i} style={styles.coin} />
        ))}
      </View>
      <Text variant="label">{`${total.toLocaleString()} ${total === 1 ? 'item' : 'items'} on your shelves`}</Text>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  root: { gap: theme.spacing.sm },
  pile: { flexDirection: 'row', flexWrap: 'wrap', gap: 4 },
  coin: { width: 12, height: 12, borderRadius: 6, backgroundColor: theme.castle.brass },
}));
