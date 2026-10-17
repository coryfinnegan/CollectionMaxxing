import { Tabs } from 'expo-router';
import { Platform, View, useWindowDimensions } from 'react-native';
import { Sidebar } from '@/components/Sidebar';
import { OwnedDataBridge } from '@/features/offline/OwnedDataBridge';
import { AppLockGate } from '@/features/me/appLock';
import { SnapStoreBridge } from '@/features/scan/pipeline/SnapStoreBridge';
import { WIDE_BREAKPOINT, useColors } from '@/theme/tokens';

// Pages that only appear in the web sidebar, never in the phone tab bar.
const sidebarOnly = ['wishlist', 'lent-out', 'members', 'settings', 'me/locations', 'me/export', 'me/devices', 'me/deleted', 'me/delete'];

export default function TabsLayout() {
  const colors = useColors();
  const { width } = useWindowDimensions();
  const wide = Platform.OS === 'web' && width >= WIDE_BREAKPOINT;

  return (
    <AppLockGate>
    <OwnedDataBridge />
    <SnapStoreBridge />
    <View style={{ flex: 1, flexDirection: wide ? 'row' : 'column', backgroundColor: colors.ground }}>
      {wide ? <Sidebar /> : null}
      <View style={{ flex: 1 }}>
        <Tabs
          tabBar={wide ? () => null : undefined}
          screenOptions={{
            headerStyle: { backgroundColor: colors.surface },
            headerTintColor: colors.ink,
            headerShown: !wide,
            tabBarActiveTintColor: colors.scan,
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
          {sidebarOnly.map((name) => (
            <Tabs.Screen key={name} name={name} options={{ href: null, title: titleFor(name) }} />
          ))}
        </Tabs>
      </View>
    </View>
    </AppLockGate>
  );
}

function titleFor(name: string): string {
  const titles: Record<string, string> = {
    wishlist: 'Wishlist',
    'lent-out': 'Lent out',
    members: 'Members',
    settings: 'Settings',
    'me/locations': 'Locations',
    'me/export': 'Import and export',
    'me/devices': 'Devices and browsers',
    'me/deleted': 'Recently deleted',
    'me/delete': 'Delete account',
  };
  return titles[name] ?? name;
}
