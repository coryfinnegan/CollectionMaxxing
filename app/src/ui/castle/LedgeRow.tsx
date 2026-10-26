// A list row seated on a stone ledge: the row itself keeps the page palette, the
// divider carries the castle. Flat uses a plain rule, Accessible a heavier one
// and no press motion.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Image, Pressable, View } from 'react-native';
import Animated from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';
import { usePressFeedback } from '../usePressFeedback';
import { type CastleMode, useCastleMode } from './mode';
import { castleTextures } from './textures';

type LedgeRowProps = {
  title: string;
  subtitle?: string;
  onPress?: () => void;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function LedgeRow({ title, subtitle, onPress, mode, testID }: LedgeRowProps) {
  const hooked = useCastleMode();
  const resolved = mode ?? hooked;
  const press = usePressFeedback('tap');
  const content = (
    <View>
      <View style={styles.row}>
        <View style={styles.text}>
          <Text>{title}</Text>
          {subtitle ? (
            <Text variant="caption" tone="muted">
              {subtitle}
            </Text>
          ) : null}
        </View>
      </View>
      {resolved === 'full' ? (
        <Image source={castleTextures.ledge} resizeMode="repeat" style={styles.ledge} testID="castle-texture" />
      ) : (
        <View style={resolved === 'accessible' ? styles.dividerAccessible : styles.divider} />
      )}
    </View>
  );
  if (!onPress) return <View testID={testID}>{content}</View>;
  if (resolved === 'accessible') {
    return (
      <Pressable accessibilityRole="button" accessibilityLabel={subtitle ? `${title}, ${subtitle}` : title} onPress={onPress} testID={testID}>
        {content}
      </Pressable>
    );
  }
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={subtitle ? `${title}, ${subtitle}` : title}
        onPress={() => {
          press.buzz();
          onPress();
        }}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        testID={testID}
      >
        {content}
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { minHeight: 48, paddingVertical: theme.spacing.sm, justifyContent: 'center' },
  text: { flex: 1 },
  ledge: { height: 16, width: '100%' },
  divider: { height: 1, backgroundColor: theme.colors.line },
  dividerAccessible: { height: 2, backgroundColor: theme.castle.iron },
}));
