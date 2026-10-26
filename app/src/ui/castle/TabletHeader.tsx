// A chiseled stone tablet carrying a Cinzel display title under a brass rule.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';
import { type CastleMode, useCastleMode } from './mode';
import { NineSlice } from './NineSlice';

type TabletHeaderProps = {
  title: string;
  subtitle?: string;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function TabletHeader({ title, subtitle, mode, testID }: TabletHeaderProps) {
  const hooked = useCastleMode();
  return (
    <NineSlice surface="stone" mode={mode ?? hooked} testID={testID}>
      <Text variant="castleDisplay" accessibilityRole="header" style={styles.title}>
        {title}
      </Text>
      <View style={styles.rule} />
      {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
    </NineSlice>
  );
}

const styles = StyleSheet.create((theme) => ({
  title: { color: theme.castle.stoneInk },
  rule: { height: 2, backgroundColor: theme.castle.brass },
  subtitle: { color: theme.castle.stoneInk },
}));
