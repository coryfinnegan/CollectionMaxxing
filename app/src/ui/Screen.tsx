import type { ReactNode } from 'react';
import { ScrollView, View } from 'react-native';
import { spacing, useColors } from '@/theme/tokens';
import { Text } from './Text';

type ScreenProps = {
  title?: string;
  subtitle?: string;
  children?: ReactNode;
  scroll?: boolean;
};

export function Screen({ title, subtitle, children, scroll = true }: ScreenProps) {
  const colors = useColors();
  const content = (
    <>
      {title ? (
        <View style={{ gap: spacing.xs }}>
          <Text variant="title" accessibilityRole="header">
            {title}
          </Text>
          {subtitle ? <Text tone="muted">{subtitle}</Text> : null}
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
