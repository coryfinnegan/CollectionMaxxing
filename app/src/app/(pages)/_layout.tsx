// The pages that are not tabs (#303): native tabs only mount declared triggers,
// so pushed pages (locations, members, settings, ...) live in this group —
// URL-identical to before — inside a stack with headers on the phone. The web
// names every page in its own content, so these headers hide there.
import { Stack } from 'expo-router';
import { Platform } from 'react-native';
import { TabsFrame } from '@/components/TabsFrame';
import { useColors } from '@/theme/tokens';

export default function PagesLayout() {
  const colors = useColors();
  return (
    <TabsFrame>
      <Stack
        screenOptions={{
          headerStyle: { backgroundColor: colors.surface },
          headerTintColor: colors.ink,
          contentStyle: { backgroundColor: colors.ground },
          headerShown: Platform.OS !== 'web',
        }}
      >
        <Stack.Screen name="wishlist" options={{ title: 'Wishlist' }} />
        <Stack.Screen name="lent-out" options={{ title: 'Lent out' }} />
        <Stack.Screen name="members" options={{ title: 'Members' }} />
        <Stack.Screen name="settings" options={{ title: 'Settings' }} />
        <Stack.Screen name="about" options={{ title: 'About' }} />
        <Stack.Screen name="me/locations" options={{ title: 'Locations' }} />
        <Stack.Screen name="me/export" options={{ title: 'Export my data' }} />
        <Stack.Screen name="me/devices" options={{ title: 'Devices and browsers' }} />
        <Stack.Screen name="me/sign-in-methods" options={{ title: 'Sign-in methods' }} />
        <Stack.Screen name="me/deleted" options={{ title: 'Recently deleted' }} />
        <Stack.Screen name="me/delete" options={{ title: 'Delete account' }} />
      </Stack>
    </TabsFrame>
  );
}
