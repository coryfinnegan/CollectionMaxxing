import { Text as RNText, type TextProps as RNTextProps } from 'react-native';
import { type TextVariant, typeScale, useColors } from '@/theme/tokens';

export type TextProps = RNTextProps & {
  variant?: TextVariant;
  tone?: 'ink' | 'muted' | 'danger' | 'accent';
};

export function Text({ variant = 'body', tone = 'ink', style, ...rest }: TextProps) {
  const colors = useColors();
  return <RNText {...rest} style={[typeScale[variant], { color: colors[tone] }, style]} />;
}
