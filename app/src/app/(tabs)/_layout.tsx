import { Tabs } from 'expo-router';
import { useState } from 'react';
import { Platform, Pressable, View, useWindowDimensions } from 'react-native';
import { Sidebar } from '@/components/Sidebar';
import { OwnedDataBridge } from '@/features/offline/OwnedDataBridge';
import { AppLockGate } from '@/features/me/appLock';
import { FingerprintBridge } from '@/features/scan/fingerprints/FingerprintBridge';
import { SnapStoreBridge } from '@/features/scan/pipeline/SnapStoreBridge';
import { WIDE_BREAKPOINT, radius, spacing, useColors } from '@/theme/tokens';
import { Text } from '@/ui';

// Pages that only appear in the web sidebar, never in the phone tab bar.
const sidebarOnly = ['wishlist', 'lent-out', 'members', 'settings', 'about', 'me/locations', 'me/export', 'me/devices', 'me/deleted', 'me/delete'];

const MAIN_ID = 'main-content';
const jumpToMain = () => document.getElementById(MAIN_ID)?.focus();

/** The first Tab stop on the web: hidden until it has focus, then it jumps past the sidebar to the page. */
function SkipLink() {
  const colors = useColors();
  const [shown, setShown] = useState(false);
  if (Platform.OS !== 'web') return null;
  return (
    <Pressable
      accessibilityRole="link"
      accessibilityLabel="Skip to main content"
      onFocus={() => setShown(true)}
      onBlur={() => setShown(false)}
      onPress={jumpToMain}
      // A div with role link does not get Enter from the browser.
      {...({ onKeyDown: (e: { key: string }) => e.key === 'Enter' && jumpToMain() } as object)}
      style={{
        position: 'absolute',
        top: spacing.sm,
        left: spacing.sm,
        zIndex: 100,
        paddingVertical: spacing.sm,
        paddingHorizontal: spacing.lg,
        borderRadius: radius.md,
        backgroundColor: colors.scan,
        transform: [{ translateY: shown ? 0 : -200 }],
      }}
    >
      <Text variant="label" style={{ color: colors.onScan }}>
        Skip to main content
      </Text>
    </Pressable>
  );
}

export default function TabsLayout() {
  const colors = useColors();
  const { width } = useWindowDimensions();
  const wide = Platform.OS === 'web' && width >= WIDE_BREAKPOINT;

  return (
    <AppLockGate>
    <OwnedDataBridge />
    <SnapStoreBridge />
    <FingerprintBridge />
    <View style={{ flex: 1, flexDirection: wide ? 'row' : 'column', backgroundColor: colors.ground }}>
      <SkipLink />
      {wide ? <Sidebar /> : null}
      <View nativeID={MAIN_ID} role="main" tabIndex={-1} style={{ flex: 1 }}>
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
    about: 'About',
    'me/locations': 'Locations',
    'me/export': 'Import and export',
    'me/devices': 'Devices and browsers',
    'me/deleted': 'Recently deleted',
    'me/delete': 'Delete account',
  };
  return titles[name] ?? name;
}
