// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useState } from 'react';
import Animated from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from './Text';

type CoverProps = {
  title: string;
  uri?: string | null;
  /** Width in points; the height follows a 2:3 cover ratio. */
  width?: number;
  /** Set on both ends of the library-to-detail flight for the shared transition; unset where it is unsupported. */
  sharedTag?: string;
};

/** A cover image, or a plain tile with the title when there is no art or it fails to load. */
export function Cover({ title, uri, width = 96, sharedTag }: CoverProps) {
  const height = Math.round(width * 1.5);
  // Which address failed, so a new address gets a fresh attempt even after a failure.
  const [failedUri, setFailedUri] = useState<string | null>(null);
  if (uri && failedUri !== uri) {
    return (
      <Animated.Image
        source={{ uri }}
        accessibilityIgnoresInvertColors
        accessibilityLabel={`Cover of ${title}`}
        onError={() => setFailedUri(uri)}
        sharedTransitionTag={sharedTag}
        style={styles.image({ width, height })}
      />
    );
  }
  return (
    <Animated.View
      // The title is shown next to the cover, so the stand-in tile stays out of the accessibility tree.
      accessibilityElementsHidden
      importantForAccessibility="no-hide-descendants"
      sharedTransitionTag={sharedTag}
      style={styles.tile({ width, height })}
    >
      <Text variant="caption" numberOfLines={3}>
        {title}
      </Text>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  image: ({ width, height }: { width: number; height: number }) => ({
    width,
    height,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
  }),
  tile: ({ width, height }: { width: number; height: number }) => ({
    width,
    height,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
    padding: theme.spacing.sm,
    justifyContent: 'flex-end',
  }),
}));
