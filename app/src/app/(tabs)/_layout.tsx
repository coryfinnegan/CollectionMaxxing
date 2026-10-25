// The phone tab group (#303): native tabs with Library, Scan, and Me. The web
// uses _layout.web.tsx; pushed pages live in (pages).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { SafeAreaView } from 'react-native-safe-area-context';
import { StyleSheet } from 'react-native-unistyles';
import { NativeTabBar } from '@/components/NativeTabBar';
import { TabsFrame } from '@/components/TabsFrame';

export default function TabsLayout() {
  return (
    <TabsFrame>
      {/* Native tabs have no header: the top inset keeps tab content clear of the status bar. */}
      <SafeAreaView edges={['top']} style={styles.fill}>
        <NativeTabBar />
      </SafeAreaView>
    </TabsFrame>
  );
}

const styles = StyleSheet.create((theme) => ({
  fill: { flex: 1, backgroundColor: theme.colors.ground },
}));
