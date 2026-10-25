// Everything above the root navigator that every platform shares: data, safe
// areas, fonts, auth wiring, and the document chrome on the web. The layouts
// only differ in what they navigate (the Stack and its chrome).
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider, useRouter } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { Platform } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { useEffect, useState, type ReactNode } from 'react';
import { installForegroundRegistration } from '@/features/auth/appSignIn';
import { installAuth } from '@/features/auth/bootstrap';
import { installFocusRing } from '@/lib/a11y/webFocusRing';
import { useAppFonts } from '@/theme/fonts';
import { useNavigationTheme } from '@/theme/navigationTheme';
import { darkColors, useColors } from '@/theme/tokens';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 30_000 } },
});

const mockMode = process.env.EXPO_PUBLIC_API_MOCK === '1';

export function RootFrame({ children }: { children: ReactNode }) {
  const colors = useColors();
  const navigationTheme = useNavigationTheme();
  const router = useRouter();
  // 401s send the iPhone and the browser to sign-in; reauth_required asks for a fresh sign-in on web.
  useEffect(() => installAuth(router), [router]);
  // iPhone: foreground returns re-register this install when the last registration is more than a day old.
  useEffect(() => installForegroundRegistration(), []);
  useEffect(installFocusRing, []);
  // The browser paints the page behind the app (overscroll, address-bar gaps) with the document background: keep it on our ground color.
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
        {children}
      </ThemeProvider>
    </SafeAreaProvider>
    </QueryClientProvider>
  );
}
