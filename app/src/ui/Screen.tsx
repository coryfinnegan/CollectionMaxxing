import type { ReactNode } from 'react';
import { Platform, ScrollView, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
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
  // One title per screen: on the phone the navigator header names it, so the section title
  // only renders on the web. Pushed screens keep the nav-bar title on both platforms and pass
  // no title at all. The subtitle is content, not a title, and always shows.
  const showTitle = !!title && (showTitleOnPhone || Platform.OS === 'web');
  const content = (
    <>
      {showTitle || subtitle ? (
        <View style={styles.header}>
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
  return scroll ? (
    <ScrollView style={styles.fill} contentContainerStyle={styles.padding}>
      {content}
    </ScrollView>
  ) : (
    <View style={[styles.fill, styles.padding]}>{content}</View>
  );
}

const styles = StyleSheet.create((theme) => ({
  header: { gap: theme.spacing.xs },
  fill: { flex: 1, backgroundColor: theme.colors.ground },
  padding: { padding: theme.spacing.lg, gap: theme.spacing.lg },
}));
