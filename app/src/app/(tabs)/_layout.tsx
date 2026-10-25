// The tab group (#303): Library, Scan, and Me. Everything else that used to
// hide here as a tab now lives in (pages) as pushed pages. The root layouts
// own the surrounding chrome (sidebar, skip link); this file only picks tabs.
import { Tabs } from 'expo-router';
import { Platform, useWindowDimensions } from 'react-native';
import { TabsFrame } from '@/components/TabsFrame';
import { WIDE_BREAKPOINT, useColors } from '@/theme/tokens';

export default function TabsLayout() {
  const colors = useColors();
  const { width } = useWindowDimensions();
  const wide = Platform.OS === 'web' && width >= WIDE_BREAKPOINT;

  return (
    <TabsFrame>
      <Tabs
        tabBar={wide ? () => null : undefined}
        screenOptions={{
          headerStyle: { backgroundColor: colors.surface },
          headerTintColor: colors.ink,
          // Every web page names itself in its own content, so the navigator's title bar would only repeat it.
          headerShown: Platform.OS !== 'web',
          tabBarActiveTintColor: colors.accent,
          tabBarInactiveTintColor: colors.muted,
          tabBarStyle: { backgroundColor: colors.surface, borderTopColor: colors.line },
          tabBarIconStyle: { display: 'none' },
          tabBarLabelPosition: 'below-icon',
        }}
      >
        <Tabs.Screen name="library" options={{ title: 'Library' }} />
        <Tabs.Screen
          name="scan"
          // No scanning on the web (docs/06 W2): the tab only exists on the phone.
          options={{ title: 'Scan', tabBarLabelStyle: { fontSize: 18, fontWeight: '800' }, ...(Platform.OS === 'web' ? { href: null } : {}) }}
        />
        <Tabs.Screen name="me/index" options={{ title: 'Me' }} />
      </Tabs>
    </TabsFrame>
  );
}
