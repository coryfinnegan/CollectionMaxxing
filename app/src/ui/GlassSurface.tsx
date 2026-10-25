// A floating surface: native liquid glass on iOS 26+, a plain view everywhere else (#306).
// Accents only (the scan pill, the scanner count bar), never a theme.
import { GlassView, isLiquidGlassAvailable } from 'expo-glass-effect';
import type { ReactNode } from 'react';
import { View, type StyleProp, type ViewStyle } from 'react-native';

type GlassSurfaceProps = {
  /** Layout (shape, padding, shadow): applies on both paths. */
  style?: StyleProp<ViewStyle>;
  /** The opaque background for the plain-view fallback; glass tints instead. */
  fallbackStyle?: StyleProp<ViewStyle>;
  /** Tint of the glass on iOS 26+. */
  tint?: string;
  children?: ReactNode;
  testID?: string;
};

export function GlassSurface({ style, fallbackStyle, tint, children, testID }: GlassSurfaceProps) {
  if (isLiquidGlassAvailable()) {
    return (
      <GlassView style={style} tintColor={tint} testID={testID}>
        {children}
      </GlassView>
    );
  }
  return (
    <View style={[style, fallbackStyle]} testID={testID}>
      {children}
    </View>
  );
}
