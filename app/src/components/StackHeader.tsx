// The shared pushed-screen header on the phone (#445): the chevron back at the
// top left, the screen's title centered, nothing else. It is a plain view, not
// a native bar button item, so iOS 26 draws no glass pill around the back
// control. Both native stack layouts render it as their header, so a new pushed
// screen inherits it; the scanner keeps its title with an empty back slot,
// since its own Close owns that flow.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { StyleSheet } from 'react-native-unistyles';
import { StackBackButton } from './StackBackButton';
import { Text } from '@/ui';

export function StackHeader({ title }: { title?: string }) {
  const insets = useSafeAreaInsets();
  return (
    <View style={styles.bar(insets.top)} accessibilityRole="header">
      <View style={styles.row}>
        <StackBackButton />
        <Text variant="label" numberOfLines={1} style={styles.title}>
          {title ?? ''}
        </Text>
        <View style={styles.slot} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  bar: (top: number) => ({
    backgroundColor: theme.colors.surface,
    paddingTop: top,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: theme.colors.line,
  }),
  row: { flexDirection: 'row', alignItems: 'center', height: 44 },
  // The 17pt semibold platform nav title, in the app face; the label variant owns the family and weight.
  title: { flex: 1, textAlign: 'center', fontSize: 17, lineHeight: 22 },
  slot: { width: 44 },
}));
