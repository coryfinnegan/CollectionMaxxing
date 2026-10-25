import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Stack, ThemeProvider, useRouter } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { Platform } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { useEffect, useState } from 'react';
import { installAuth } from '@/features/auth/bootstrap';
import { installFocusRing } from '@/lib/a11y/webFocusRing';
import { useAppFonts } from '@/theme/fonts';
import { useNavigationTheme } from '@/theme/navigationTheme';
import { darkColors, useColors } from '@/theme/tokens';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 30_000 } },
});

const mockMode = process.env.EXPO_PUBLIC_API_MOCK === '1';

export default function RootLayout() {
  const colors = useColors();
  const navigationTheme = useNavigationTheme();
  const router = useRouter();
  // 401s send the iPhone and the browser to sign-in; reauth_required asks for a fresh sign-in on web.
  useEffect(() => installAuth(router), [router]);
  useEffect(installFocusRing, []);
  // The browser paints the page behind the app (overscroll, address-bar gaps) with the document background: keep it on our ground colour.
  useEffect(() => {
    if (Platform.OS !== 'web' || typeof document === 'undefined') return;
    document.documentElement.style.backgroundColor = colors.ground;
    document.body.style.backgroundColor = colors.ground;
    document.documentElement.style.colorScheme = colors === darkColors ? 'dark' : 'light';
  }, [colors]);
  // In mock mode, wait for MSW to start before any request goes out.
  const [ready, setReady] = useState(!mockMode);
  useEffect(() => {
    if (!mockMode) return;
    import('@/mocks/start').then(({ startMocking }) => startMocking()).then(() => setReady(true));
  }, []);
  const fontsLoaded = useAppFonts();

  // The page title, set directly: expo-router's <Head> nags for a handoff origin that this self-hosted app has no use for.
  useEffect(() => {
    if (Platform.OS === 'web') document.title = 'CollectionMaxxing';
  }, []);

  if (!ready || !fontsLoaded) return null;
  return (
    <QueryClientProvider client={queryClient}>
    <SafeAreaProvider>
      <StatusBar style="auto" />
      <ThemeProvider value={navigationTheme}>
      <Stack
        screenOptions={{
          headerStyle: { backgroundColor: colors.surface },
          headerTintColor: colors.ink,
          contentStyle: { backgroundColor: colors.ground },
        }}
      >
        <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
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
      </ThemeProvider>
    </SafeAreaProvider>
    </QueryClientProvider>
  );
}
