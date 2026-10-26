// The Scan action above the native tab bar (#303): a glass pill that opens the
// Scan tab. It collapses to an icon button when the bar minimizes. Stateless:
// two instances render at once (one per placement) with unshared state, so the
// hide-on-Scan-tab gate lives in NativeTabBar, outside the accessory.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { NativeTabs } from 'expo-router/unstable-native-tabs';
import { useRouter } from 'expo-router';
import { Pressable, View } from 'react-native';
import Animated from 'react-native-reanimated';
import { StyleSheet, useUnistyles } from 'react-native-unistyles';
import { GlassSurface, Text, usePressFeedback } from '@/ui';

export const SCAN_LABEL = 'Scan';

export function ScanPill() {
  const router = useRouter();
  const { theme } = useUnistyles();
  const press = usePressFeedback('select');
  const placement = NativeTabs.BottomAccessory.usePlacement();
  const openScan = () => {
    press.buzz();
    router.navigate('/scan');
  };
  // Frosted accent glass on iOS 26+, the solid accent pill elsewhere (#306).
  if (placement === 'inline') {
    return (
      <Animated.View style={press.style}>
        <Pressable accessibilityRole="button" accessibilityLabel={SCAN_LABEL} onPress={openScan} onPressIn={press.onPressIn} onPressOut={press.onPressOut}>
          <GlassSurface style={styles.collapsed} fallbackStyle={styles.accentFallback} tint={theme.colors.accent}>
            <BarcodeGlyph color={theme.colors.onAccent} />
          </GlassSurface>
        </Pressable>
      </Animated.View>
    );
  }
  return (
    <Animated.View style={press.style}>
      <Pressable accessibilityRole="button" accessibilityLabel={SCAN_LABEL} onPress={openScan} onPressIn={press.onPressIn} onPressOut={press.onPressOut}>
        <GlassSurface style={styles.pill} fallbackStyle={styles.accentFallback} tint={theme.colors.accent}>
          <BarcodeGlyph color={theme.colors.onAccent} />
          <Text variant="label" style={styles.label}>
            {SCAN_LABEL}
          </Text>
        </GlassSurface>
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
        <View key={index} style={styles.bar({ width: barWidth, color })} />
      ))}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  collapsed: {
    width: 44,
    height: 44,
    borderRadius: theme.radius.pill,
    alignItems: 'center',
    justifyContent: 'center',
  },
  pill: {
    minHeight: 44,
    paddingHorizontal: theme.spacing.lg,
    borderRadius: theme.radius.pill,
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.sm,
    shadowColor: theme.fixed.black,
    shadowOpacity: 0.2,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 2 },
    elevation: 4,
  },
  accentFallback: { backgroundColor: theme.colors.accent },
  label: { color: theme.colors.onAccent },
  bars: { flexDirection: 'row', alignItems: 'center', gap: 2 },
  bar: ({ width, color }: { width: number; color: string }) => ({ height: 16, borderRadius: 1, width, backgroundColor: color }),
}));
