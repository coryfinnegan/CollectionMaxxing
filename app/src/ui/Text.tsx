import { Text as RNText, type TextProps as RNTextProps } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { typeScale, type TextVariant } from '@/theme/tokens';

export type TextProps = RNTextProps & {
  variant?: TextVariant;
  tone?: 'ink' | 'muted' | 'danger' | 'accent';
};

type Tone = NonNullable<TextProps['tone']>;

export function Text({ variant = 'body', tone = 'ink', style, ...rest }: TextProps) {
  // The type entry stays a static token lookup (Text.test.tsx asserts the array
  // shape); only the themed tone moves into the sheet.
  return <RNText {...rest} style={[typeScale[variant], styles.tone(tone), style]} />;
}

const styles = StyleSheet.create((theme) => ({
  tone: (tone: Tone) => ({ color: theme.colors[tone] }),
}));
