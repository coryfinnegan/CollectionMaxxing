// The phone tab group (#303): native tabs with Library, Scan, and Me. The web
// uses _layout.web.tsx; pushed pages live in (pages).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useUnistyles } from 'react-native-unistyles';
import { NativeTabBar } from '@/components/NativeTabBar';
import { TabsFrame } from '@/components/TabsFrame';

export default function TabsLayout() {
  const { theme } = useUnistyles();
  // Plain object: SafeAreaView lives outside the babel-processed tree, so a
  // themed sheet value would arrive unstyled on web (#305).
  const fill = { flex: 1, backgroundColor: theme.colors.ground };
  return (
    <TabsFrame>
      {/* Native tabs have no header: the top inset keeps tab content clear of the status bar. */}
      <SafeAreaView edges={['top']} style={fill}>
        <NativeTabBar />
      </SafeAreaView>
    </TabsFrame>
  );
}
