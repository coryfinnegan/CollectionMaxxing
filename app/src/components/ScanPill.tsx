// The Scan action above the native tab bar (#303): a glass pill that opens the
// Scan tab. It collapses to a barcode button when the bar minimizes. Stateless:
// two instances render at once (one per placement) with unshared state, so the
// hide-on-Scan-tab gate lives in NativeTabBar, outside the accessory.
import { NativeTabs } from 'expo-router/unstable-native-tabs';
import { useRouter } from 'expo-router';
import { Pressable, StyleSheet, View } from 'react-native';
import Animated from 'react-native-reanimated';
import { fixed, radius, spacing, useColors } from '@/theme/tokens';
import { Text, usePressFeedback } from '@/ui';

export const SCAN_LABEL = 'Scan a barcode';

export function ScanPill() {
  const router = useRouter();
  const colors = useColors();
  const press = usePressFeedback('select');
  const placement = NativeTabs.BottomAccessory.usePlacement();
  const openScan = () => {
    press.buzz();
    router.navigate('/scan');
  };
  if (placement === 'inline') {
    return (
      <Animated.View style={press.style}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={SCAN_LABEL}
          onPress={openScan}
          onPressIn={press.onPressIn}
          onPressOut={press.onPressOut}
          style={[styles.collapsed, { backgroundColor: colors.accent }]}
        >
          <BarcodeGlyph color={colors.onAccent} />
        </Pressable>
      </Animated.View>
    );
  }
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={SCAN_LABEL}
        onPress={openScan}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        style={[styles.pill, { backgroundColor: colors.accent }]}
      >
        <BarcodeGlyph color={colors.onAccent} />
        <Text variant="label" style={{ color: colors.onAccent }}>
          {SCAN_LABEL}
        </Text>
      </Pressable>
    </Animated.View>
  );
}

const barWidths = [3, 1.5, 1, 2.5, 1.5, 3, 1];

function BarcodeGlyph({ color }: { color: string }) {
  return (
    <View aria-hidden style={styles.bars}>
      {barWidths.map((barWidth, index) => (
        // Static bars that never reorder: the index key is stable.
        <View key={index} style={[styles.bar, { width: barWidth, backgroundColor: color }]} />
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  collapsed: { width: 44, height: 44, borderRadius: radius.pill, alignItems: 'center', justifyContent: 'center' },
  pill: {
    minHeight: 44,
    paddingHorizontal: spacing.lg,
    borderRadius: radius.pill,
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
    shadowColor: fixed.black,
    shadowOpacity: 0.2,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 2 },
    elevation: 4,
  },
  bars: { flexDirection: 'row', alignItems: 'center', gap: 2 },
  bar: { height: 16, borderRadius: 1 },
});
