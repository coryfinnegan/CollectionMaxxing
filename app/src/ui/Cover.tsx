import { useEffect, useState } from 'react';
import { Image, View } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

type CoverProps = {
  title: string;
  uri?: string | null;
  /** Width in points; the height follows a 2:3 cover ratio. */
  width?: number;
};

/** A cover image, or a plain tile with the title when there is no art or it fails to load. */
export function Cover({ title, uri, width = 96 }: CoverProps) {
  const colors = useColors();
  const height = Math.round(width * 1.5);
  const [failed, setFailed] = useState(false);
  // A new address deserves a fresh attempt, even if the last one failed.
  useEffect(() => setFailed(false), [uri]);
  if (uri && !failed) {
    return (
      <Image
        source={{ uri }}
        accessibilityIgnoresInvertColors
        accessibilityLabel={`Cover of ${title}`}
        onError={() => setFailed(true)}
        style={{ width, height, borderRadius: radius.sm, backgroundColor: colors.line }}
      />
    );
  }
  return (
    <View
      // The title is shown next to the cover, so the stand-in tile stays out of the accessibility tree.
      accessibilityElementsHidden
      importantForAccessibility="no-hide-descendants"
      style={{
        width,
        height,
        borderRadius: radius.sm,
        backgroundColor: colors.line,
        padding: spacing.sm,
        justifyContent: 'flex-end',
      }}
    >
      <Text variant="caption" numberOfLines={3}>
        {title}
      </Text>
    </View>
  );
}
