// The phone tab group (#303): native tabs with Library, Scan, and Me. The web
// uses _layout.web.tsx; pushed pages live in (pages).
import { SafeAreaView } from 'react-native-safe-area-context';
import { NativeTabBar } from '@/components/NativeTabBar';
import { TabsFrame } from '@/components/TabsFrame';
import { useColors } from '@/theme/tokens';

export default function TabsLayout() {
  const colors = useColors();
  return (
    <TabsFrame>
      {/* Native tabs have no header: the top inset keeps tab content clear of the status bar. */}
      <SafeAreaView edges={['top']} style={{ flex: 1, backgroundColor: colors.ground }}>
        <NativeTabBar />
      </SafeAreaView>
    </TabsFrame>
  );
}
