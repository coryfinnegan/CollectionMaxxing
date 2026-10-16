// The "Nice scan!" flash: a pill with a check that pops in. With Reduce Motion it just appears.
import { useEffect, useState } from 'react';
import { Animated, View } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from '@/ui';

export function NiceScanFlash({ visible, reduceMotion }: { visible: boolean; reduceMotion: boolean }) {
  const colors = useColors();
  const [pop] = useState(() => new Animated.Value(0));
  useEffect(() => {
    if (!visible || reduceMotion) {
      pop.setValue(1);
      return;
    }
    pop.setValue(0);
    Animated.timing(pop, { toValue: 1, duration: 120, useNativeDriver: true }).start();
  }, [visible, reduceMotion, pop]);

  if (!visible) return null;
  return (
    <Animated.View
      accessibilityLiveRegion="polite"
      style={{
        opacity: pop,
        transform: [{ scale: pop.interpolate({ inputRange: [0, 1], outputRange: [0.85, 1] }) }],
        flexDirection: 'row',
        alignItems: 'center',
        gap: spacing.sm,
        backgroundColor: colors.scan,
        paddingHorizontal: spacing.xl,
        paddingVertical: spacing.md,
        borderRadius: radius.pill,
      }}
    >
      <View importantForAccessibility="no-hide-descendants" accessibilityElementsHidden>
        <Text variant="heading" style={{ color: colors.onScan }}>
          ✓
        </Text>
      </View>
      <Text variant="heading" style={{ color: colors.onScan }}>
        Nice scan!
      </Text>
    </Animated.View>
  );
}
