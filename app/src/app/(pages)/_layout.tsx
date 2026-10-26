// The pages that are not tabs (#303): native tabs only mount declared triggers,
// so pushed pages (locations, members, settings, ...) live in this group —
// URL-identical to before — inside a stack with headers on the phone. The web
// names every page in its own content, so these headers hide there.
// The layout owns the way back (#356): the shared header back on the phone,
// an in-flow row above the content on the web. Screens inherit it; none adds
// its own.
import { Stack } from 'expo-router';
import { Platform, StyleSheet, View } from 'react-native';
import { StackBackButton } from '@/components/StackBackButton';
import { TabsFrame } from '@/components/TabsFrame';
import { spacing, useColors } from '@/theme/tokens';

export default function PagesLayout() {
  const colors = useColors();
  return (
    <TabsFrame>
      {Platform.OS === 'web' ? (
        <View style={styles.backRow}>
          <StackBackButton />
        </View>
      ) : null}
      <Stack
        screenOptions={{
          headerStyle: { backgroundColor: colors.surface },
          headerTintColor: colors.ink,
          contentStyle: { backgroundColor: colors.ground },
          headerShown: Platform.OS !== 'web',
          headerLeft: () => <StackBackButton />,
          animationDuration: 250, // screen pushes match the motion vocabulary (#304)
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

const styles = StyleSheet.create({
  backRow: { paddingHorizontal: spacing.lg },
});
