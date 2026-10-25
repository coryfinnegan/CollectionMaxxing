// The web root layout (#303): the sidebar beside every page on wide screens,
// and the skip link ahead of it. The tab pages bring their own tab bar on
// narrow screens; the pushed pages name themselves in their content.
import { Stack } from 'expo-router';
import { useState } from 'react';
import { Pressable, View, useWindowDimensions } from 'react-native';
import { RootFrame } from '@/components/RootFrame';
import { Sidebar } from '@/components/Sidebar';
import { WIDE_BREAKPOINT, radius, spacing, useColors } from '@/theme/tokens';
import { Text } from '@/ui';

const MAIN_ID = 'main-content';
const jumpToMain = () => document.getElementById(MAIN_ID)?.focus();

/** The first Tab stop on the web: hidden until it has focus, then it jumps past the sidebar to the page. */
function SkipLink() {
  const colors = useColors();
  const [shown, setShown] = useState(false);
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
        backgroundColor: colors.accent,
        transform: [{ translateY: shown ? 0 : -200 }],
      }}
    >
      <Text variant="label" style={{ color: colors.onAccent }}>
        Skip to main content
      </Text>
    </Pressable>
  );
}

export default function RootLayout() {
  const colors = useColors();
  const { width } = useWindowDimensions();
  const wide = width >= WIDE_BREAKPOINT;
  return (
    <RootFrame>
      <View style={{ flex: 1, flexDirection: wide ? 'row' : 'column', backgroundColor: colors.ground }}>
        <SkipLink />
        {wide ? <Sidebar /> : null}
        <View nativeID={MAIN_ID} role="main" tabIndex={-1} style={{ flex: 1 }}>
          <Stack
            screenOptions={{
              headerStyle: { backgroundColor: colors.surface },
              headerTintColor: colors.ink,
              contentStyle: { backgroundColor: colors.ground },
            }}
          >
            <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
            <Stack.Screen name="(pages)" options={{ headerShown: false }} />
            <Stack.Screen name="login" options={{ title: 'Sign in', headerShown: false }} />
            <Stack.Screen name="join/[token]" options={{ title: 'Join', headerShown: false }} />
            <Stack.Screen name="welcome/index" options={{ title: 'Welcome', headerShown: false }} />
            <Stack.Screen name="welcome/create" options={{ title: 'Create your library', headerShown: false }} />
            <Stack.Screen name="welcome/join" options={{ title: 'Join a library', headerShown: false }} />
            <Stack.Screen name="item/[id]/index" options={{ title: 'Item' }} />
            <Stack.Screen name="item/[id]/edit" options={{ title: 'Edit item' }} />
            <Stack.Screen name="scanner" options={{ title: 'Scanner', presentation: 'fullScreenModal' }} />
            <Stack.Screen name="review" options={{ title: 'Review' }} />
            <Stack.Screen name="evals" options={{ title: 'Run evals' }} />
            <Stack.Screen name="result/[id]" options={{ title: 'Result' }} />
          </Stack>
        </View>
      </View>
    </RootFrame>
  );
}
