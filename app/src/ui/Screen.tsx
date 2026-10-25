import type { ReactNode } from 'react';
import { Platform, ScrollView, View } from 'react-native';
import { spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

type ScreenProps = {
  title?: string;
  /** Hero moments (sign-in, welcome) get the display cut; page titles use title. */
  titleVariant?: 'title' | 'display';
  /** Content titles (an item's name) and screens with no navigator header show on the phone too. */
  showTitleOnPhone?: boolean;
  subtitle?: string;
  /** Catalog facts (format, platform, year) set in mono. */
  subtitleVariant?: 'body' | 'mono';
  children?: ReactNode;
  scroll?: boolean;
};

export function Screen({
  title,
  titleVariant = 'title',
  showTitleOnPhone = false,
  subtitle,
  subtitleVariant = 'body',
  children,
  scroll = true,
}: ScreenProps) {
  const colors = useColors();
  // One title per screen: on the phone the navigator header names it, so the section title
  // only renders on the web. Pushed screens keep the nav-bar title on both platforms and pass
  // no title at all. The subtitle is content, not a title, and always shows.
  const showTitle = !!title && (showTitleOnPhone || Platform.OS === 'web');
  const content = (
    <>
      {showTitle || subtitle ? (
        <View style={{ gap: spacing.xs }}>
          {showTitle ? (
            <Text variant={titleVariant} accessibilityRole="header">
              {title}
            </Text>
          ) : null}
          {subtitle ? (
            <Text variant={subtitleVariant} tone="muted">
              {subtitle}
            </Text>
          ) : null}
        </View>
      ) : null}
      {children}
    </>
  );
  const padding = { padding: spacing.lg, gap: spacing.lg };
  return scroll ? (
    <ScrollView style={{ flex: 1, backgroundColor: colors.ground }} contentContainerStyle={padding}>
      {content}
    </ScrollView>
  ) : (
    <View style={[{ flex: 1, backgroundColor: colors.ground }, padding]}>{content}</View>
  );
}
