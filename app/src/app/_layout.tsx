import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Stack, useRouter } from 'expo-router';
import Head from 'expo-router/head';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useState } from 'react';
import { installAuth } from '@/features/auth/bootstrap';
import { installFocusRing } from '@/lib/a11y/webFocusRing';
import { useColors } from '@/theme/tokens';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 30_000 } },
});

const mockMode = process.env.EXPO_PUBLIC_API_MOCK === '1';

export default function RootLayout() {
  const colors = useColors();
  const router = useRouter();
  // 401s send the iPhone to pairing and the browser to sign-in; reauth_required opens a passkey prompt.
  useEffect(() => installAuth(router), [router]);
  useEffect(installFocusRing, []);
  // In mock mode, wait for MSW to start before any request goes out.
  const [ready, setReady] = useState(!mockMode);
  useEffect(() => {
    if (!mockMode) return;
    import('@/mocks/start').then(({ startMocking }) => startMocking()).then(() => setReady(true));
  }, []);

  if (!ready) return null;
  return (
    <QueryClientProvider client={queryClient}>
      <Head>
        <title>CollectionMaxxing</title>
      </Head>
      <StatusBar style="auto" />
      <Stack
        screenOptions={{
          headerStyle: { backgroundColor: colors.surface },
          headerTintColor: colors.ink,
          contentStyle: { backgroundColor: colors.ground },
        }}
      >
        <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
        <Stack.Screen name="pair" options={{ title: 'Pair with your server', headerShown: false }} />
        <Stack.Screen name="setup" options={{ title: 'Set up', headerShown: false }} />
        <Stack.Screen name="login" options={{ title: 'Sign in', headerShown: false }} />
        <Stack.Screen name="join/[token]" options={{ title: 'Join', headerShown: false }} />
        <Stack.Screen name="item/[id]/index" options={{ title: 'Item' }} />
        <Stack.Screen name="item/[id]/edit" options={{ title: 'Edit item' }} />
        <Stack.Screen name="scanner" options={{ title: 'Scanner', presentation: 'fullScreenModal' }} />
        <Stack.Screen name="review" options={{ title: 'Review' }} />
        <Stack.Screen name="evals" options={{ title: 'Run evals' }} />
        <Stack.Screen name="result/[id]" options={{ title: 'Result' }} />
      </Stack>
    </QueryClientProvider>
  );
}
