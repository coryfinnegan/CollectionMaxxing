import { Stack } from 'expo-router';
import { RootFrame } from '@/components/RootFrame';
import { StackHeader } from '@/components/StackHeader';
import { useColors } from '@/theme/tokens';

export default function RootLayout() {
  const colors = useColors();
  return (
    <RootFrame>
      <Stack
        screenOptions={{
          contentStyle: { backgroundColor: colors.ground },
          animationDuration: 250, // screen pushes match the motion vocabulary (#304)
          // The shared custom header (#445): a chevron-only back with no native bar-button glass. Every pushed
          // screen declares an explicit title, so the header never falls back to a route-group name (#372).
          header: ({ options }) => <StackHeader title={options.title} />,
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
    </RootFrame>
  );
}
